using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Transom.Core;

/// <summary>
///     What one OpenAI image edit returned: the PNG plus the <c>usage</c> block and the echoed
///     size/quality. The token counts are what OpenAI actually billed for this image, so
///     <see cref="ActualCostUsd"/> is the real cost at list prices — the pre-flight estimate is a
///     lookup, this is the truth. Every usage field is nullable: a response without a usage object
///     still yields the image, and the log records "unknown" rather than zero.
/// </summary>
public sealed class AireEditResult
{
    public byte[] ImageBytes { get; init; } = Array.Empty<byte>();

    public int? InputTokens { get; init; }
    public int? InputImageTokens { get; init; }
    public int? InputTextTokens { get; init; }
    public int? OutputTokens { get; init; }

    /// <summary>The size OpenAI says it produced — with <c>size=auto</c> this is the only way to know.</summary>
    public string ActualSize { get; init; } = "";
    public string ActualQuality { get; init; } = "";

    public bool HasUsage => OutputTokens.HasValue;

    /// <summary>Cost from the billed token counts, or null when the response carried no usage block.
    /// Input tokens without an image/text split are priced at the (higher) image rate.</summary>
    public double? ActualCostUsd
    {
        get
        {
            if (!OutputTokens.HasValue) return null;
            int text = InputTextTokens ?? 0;
            int image = InputImageTokens ?? Math.Max(0, (InputTokens ?? 0) - text);
            return AireEngine.EstimateCost(image, OutputTokens.Value, text);
        }
    }
}

/// <summary>
///     AIRE — "AI Render Enhancer" — engine: model/size/quality catalog, token-based cost estimation, and
///     the OpenAI <c>images/edits</c> call. Ported 1:1 from the stand-alone AIRE.exe (PySide6/Python 3.11;
///     constants recovered from its bytecode 2026-08-01), so the WPF window and the bridge tools share one
///     behavior. Deliberately Revit-free: pure files + HTTPS, callable from any thread.
///     <para>
///     Catalog revised 2026-09-08 for GPT Image 2.5: the three models OpenAI is retiring are gone, the two
///     2.5 models are in, quality is per model, and the output-token estimate is a measured table keyed by
///     model, quality and size instead of a flat pixels-per-token guess.
///     </para>
/// </summary>
public static class AireEngine
{
    public const string AppName = "AI Render Enhancer";

    public static readonly string[] SupportedExtensions = { ".png", ".jpg", ".jpeg", ".webp" };

    /// <summary>Output files are always "&lt;stem&gt;_enhanced.png" — written into the output folder.</summary>
    public const string OutputFormat = "png";

    public const string OpenAiBillingUrl = "https://platform.openai.com/settings/organization/billing/overview";

    /// <summary>Where a user creates the key AIRE needs — linked from the API-key instructions overlay.</summary>
    public const string OpenAiApiKeysUrl = "https://platform.openai.com/api-keys";

    /// <summary>The one-time organization identity check GPT image models require (a 403 without it).</summary>
    public const string OpenAiVerifyOrganizationUrl = "https://platform.openai.com/settings/organization/general";

    public const string DefaultBaseUrl = "https://api.openai.com";

    /// <summary>Settable so an out-of-process harness can point the whole engine at a local mock and
    /// exercise the request shape, retry rules and usage accounting without spending a cent.</summary>
    public static string BaseUrl { get; set; } = DefaultBaseUrl;

    // ---- catalog -----------------------------------------------------------------------------------

    // Every entry satisfies OpenAI's size rules (both edges multiples of 16, aspect between 1:3 and 3:1,
    // 655,360–8,294,400 pixels, no edge over 3840). 3840x2160 is the ceiling; OpenAI documents anything
    // above 2560x1440 as experimental. 2160x3840 is the portrait rotation, permitted by the guide's
    // "neither edge may exceed 3840" wording but never confirmed live (assumption A5 in the 2.5 brief).
    private static readonly string[] FullSizeList =
        { "3840x2160", "2160x3840", "2048x2048", "2048x1152", "1536x1024", "1024x1536", "1024x1024", "auto" };

    /// <summary>Resolution choices per model. All three current models take arbitrary WIDTHxHEIGHT sizes up
    /// to 4K, so they share one list; the order here is the dropdown order.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ModelSizeOptions = new Dictionary<string, string[]>
    {
        ["gpt-image-2.5-flare"] = FullSizeList,
        ["gpt-image-2.5-sunburst"] = FullSizeList,
        ["gpt-image-2"] = FullSizeList,
    };

    private static readonly string[] Quality25 = { "max", "xhigh", "high", "medium", "low", "auto" };
    private static readonly string[] Quality2 = { "high", "medium", "low", "auto" };

    /// <summary>
    ///     Quality rungs per model, most expensive first; index 0 is the model's default. The 2.5 models add
    ///     <c>xhigh</c> and <c>max</c>; sending either to gpt-image-2 is a 400 at spend time, which is why this
    ///     is per model rather than one flat array.
    ///     <para>
    ///     The labels do NOT mean the same thing across models. Measured on OpenAI's token calculator
    ///     (2026-09-08): gpt-image-2's low / medium / high spend exactly what 2.5's low / high / max spend.
    ///     2.5 did not raise the ceiling — it inserted two rungs into the middle of the same ladder and
    ///     re-labelled it. <see cref="OutputTokenRung"/> encodes that mapping; keep the two in step.
    ///     </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> ModelQualityOptions = new Dictionary<string, string[]>
    {
        ["gpt-image-2.5-flare"] = Quality25,
        ["gpt-image-2.5-sunburst"] = Quality25,
        ["gpt-image-2"] = Quality2,
    };

    /// <summary>The model ids AIRE offers, in dropdown order.</summary>
    public static IReadOnlyList<string> Models => ModelSizeOptions.Keys.ToList();

    public static bool IsKnownModel(string model) => ModelSizeOptions.ContainsKey(model ?? "");

    public static string[] SizeOptionsFor(string model) =>
        ModelSizeOptions.TryGetValue(model ?? "", out var sizes) ? sizes : new[] { "auto" };

    public static string[] QualityOptionsFor(string model) =>
        ModelQualityOptions.TryGetValue(model ?? "", out var q) ? q : new[] { "auto" };

    /// <summary>The rung a model defaults to: <c>max</c> on 2.5 (what gpt-image-2 <c>high</c> spends), <c>high</c> on gpt-image-2.</summary>
    public static string DefaultQualityFor(string model) => QualityOptionsFor(model)[0];

    public static bool IsValidQuality(string model, string quality) =>
        QualityOptionsFor(model).Contains(quality ?? "");

    /// <summary>
    ///     Models OpenAI has announced a shutdown for, with the date, so a saved setting or a bridge argument
    ///     naming one gets a real explanation rather than "unknown model". From the deprecations page,
    ///     2026-09-08. dall-e is already gone; chatgpt-image-latest was never in AIRE but Claude may pass it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RetiredModels = new Dictionary<string, string>
    {
        ["gpt-image-1"] = "23 October 2026",
        ["gpt-image-1.5"] = "1 December 2026",
        ["gpt-image-1-mini"] = "1 December 2026",
        ["chatgpt-image-latest"] = "1 December 2026",
        ["dall-e-2"] = "12 May 2026",
        ["dall-e-3"] = "12 May 2026",
    };

    public static bool IsRetiredModel(string model) => RetiredModels.ContainsKey(model ?? "");

    /// <summary>
    ///     Models that accept the <c>input_fidelity</c> parameter. Every model in AIRE's current catalog is
    ///     ABSENT: gpt-image-2 answers a request carrying it with HTTP 400 <c>invalid_input_fidelity_model</c>
    ///     ("The model 'gpt-image-2' does not support the 'input_fidelity' parameter"), and OpenAI's migration
    ///     guide says of GPT Image 2 and later "Omit it. Image inputs are always processed at high fidelity."
    ///     The reference page's "gpt-image-1 and gpt-image-1.5 and later models" sentence is stale — believing
    ///     it is what shipped a ticked checkbox that fails every request (v1.9.17).
    ///     A table rather than an if: putting a model back is one line when OpenAI's behaviour changes.
    /// </summary>
    private static readonly HashSet<string> InputFidelityModels = new(StringComparer.OrdinalIgnoreCase)
    {
        // gpt-image-1 and gpt-image-1.5 took it. Both are retired and out of the catalog, so this is empty
        // by fact, not by oversight. Do not delete it.
    };

    public static bool SupportsInputFidelity(string model) => InputFidelityModels.Contains(model ?? "");

    /// <summary>One sentence for the disabled checkbox and the bridge, or null when the model takes it.</summary>
    public static string? InputFidelityNote(string model) =>
        SupportsInputFidelity(model)
            ? null
            : $"{model} always reads the source image at full fidelity, and refuses the input_fidelity "
              + "parameter, so AIRE does not send it.";

    /// <summary>One sentence for a retired id, or null when the id is not on the retirement list.</summary>
    public static string? RetirementMessage(string model)
    {
        if (!RetiredModels.TryGetValue(model ?? "", out var date)) return null;
        bool gone = date.EndsWith("2026") && DateTime.Today >= ParseRetirementDate(date);
        return gone
            ? $"{model} was switched off by OpenAI on {date}. Use {DefaultModel} instead (valid: {string.Join(", ", Models)})."
            : $"{model} is being retired by OpenAI on {date} and is no longer offered here. Use {DefaultModel} instead (valid: {string.Join(", ", Models)}).";
    }

    private static DateTime ParseRetirementDate(string text) =>
        DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : DateTime.MaxValue;

    /// <summary>
    ///     The rung to land on when a saved retired model is moved onto <see cref="DefaultModel"/>. The retired
    ///     models used the three-rung ladder, and the 2.5 ladder spends roughly the same at one rung up:
    ///     high → max, medium → high, low → medium. Leaving the label unchanged would silently cut the spend
    ///     (and the detail) about 4× at 4K — a change nobody asked for, in a tool whose point is that nothing
    ///     about the spend is silent.
    /// </summary>
    public static string MigrateQuality(string quality) => (quality ?? "").Trim().ToLowerInvariant() switch
    {
        "high" => "max",
        "medium" => "high",
        "low" => "medium",
        "auto" => "auto",
        _ => DefaultQuality,
    };

    public const string DefaultModel = "gpt-image-2.5-flare";
    public const string DefaultSize = "3840x2160";
    /// <summary>The rung on <see cref="DefaultModel"/> that spends what the previous default (gpt-image-2 / high) spent.</summary>
    public const string DefaultQuality = "max";

    // ---- pricing -----------------------------------------------------------------------------------

    // USD per 1M tokens. One flat set is correct for every model that remains (gpt-image-2 and both 2.5
    // models are all $5 / $8 / $30); it was wrong for the three retired ones, which is one reason they went.
    public const double ImageInputPricePer1M = 8.0;
    public const double ImageOutputPricePer1M = 30.0;
    public const double TextInputPricePer1M = 5.0;

    /// <summary>Approximate image tokens per pixel (1/750) — the original app's heuristic, now used only for
    /// the INPUT side of the estimate (the calculator does not cover input tokens; the usage block does).</summary>
    public const double ApproxImageTokenFactor = 0.0013333333333333333;

    public const string DefaultPrompt =
        "Architectural render enhancement. Improve the realism of grass, landscape planting, lighting, and "
        + "concrete texture. Preserve the exact camera angle, perspective, geometry, building shape, object "
        + "positions, window mullions, siding lines, trim edges, and overall composition. Do not repaint or "
        + "reinterpret the architecture. Do not blur, soften, stylize, or simplify details.";

    /// <summary>~4 chars per token, floor 1 — the original's prompt-token heuristic.</summary>
    public static int EstimateTextTokens(string prompt) =>
        Math.Max(1, (prompt ?? "").Length / 4);

    /// <summary>
    ///     Tokens for an image file from its real pixel count, plus the dimensions for display.
    ///     (0, null, null) when the file can't be decoded — mirrors the original's PIL failure path.
    /// </summary>
    public static (int Tokens, int? Width, int? Height) EstimateImageTokensFromFile(string imagePath)
    {
        try
        {
            // Header-only decode (no full bitmap) — BitmapDecoder with CacheOption.None reads just enough.
            using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(
                fs,
                System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                System.Windows.Media.Imaging.BitmapCacheOption.None).Frames[0];
            int w = frame.PixelWidth, h = frame.PixelHeight;
            long pixels = (long)w * h;
            return ((int)(pixels * ApproxImageTokenFactor), w, h);
        }
        catch
        {
            return (0, null, null);
        }
    }

    // ---- output-token table ------------------------------------------------------------------------

    /// <summary>
    ///     Output tokens per image on the GPT Image 2.5 ladder, read off OpenAI's interactive token
    ///     calculator on 2026-09-08 for every size AIRE offers that the calculator lists. Keyed by pixel
    ///     count because the calculator gives 2160x3840 the same figure as 3840x2160 (rotation does not
    ///     change the count). Columns: low, medium, high, xhigh, max.
    ///     <para>
    ///     Odd but measured: 1536x1024 costs FEWER tokens than 1024x1024 at every rung. The first paid
    ///     call's usage block is the check on that (a 1024x1024 / low image is 196 tokens, half a cent).
    ///     Re-measure after any model refresh.
    ///     </para>
    /// </summary>
    private static readonly (long Pixels, int[] Tokens)[] OutputTokenTable =
    {
        (1024L * 1024, new[] { 196, 439, 1756, 3122, 7024 }),
        (1536L * 1024, new[] { 158, 343, 1372, 2459, 5488 }),
        (3840L * 2160, new[] { 371, 865, 3336, 5930, 13342 }),
    };

    private static readonly string[] Ladder25 = { "low", "medium", "high", "xhigh", "max" };

    /// <summary>
    ///     Which column of the 2.5 ladder a (model, quality) pair spends at. gpt-image-2's three rungs are
    ///     the 2.5 ladder's low / high / max, token for token. <c>auto</c> is estimated at the model's top
    ///     rung: what OpenAI's auto resolves to is not documented, and an estimate that errs high is the
    ///     safer kind in a confirm dialog. Null for an unknown rung.
    /// </summary>
    private static int? OutputTokenRung(string model, string quality)
    {
        var q = (quality ?? "").Trim().ToLowerInvariant();
        bool is2 = model == "gpt-image-2";
        if (q == "auto") q = is2 ? "high" : "max";
        if (is2)
            q = q switch { "low" => "low", "medium" => "high", "high" => "max", _ => "" };
        int idx = Array.IndexOf(Ladder25, q);
        return idx < 0 ? null : idx;
    }

    /// <summary>"WxH" → pixel count; "auto" is 1536x1024 (the original's assumption). Null when unparseable.</summary>
    public static long? PixelsOf(string sizeString)
    {
        try
        {
            var s = (sizeString ?? "").Trim().ToLowerInvariant();
            if (s == "auto") s = "1536x1024";
            var parts = s.Split('x');
            return (long)int.Parse(parts[0]) * int.Parse(parts[1]);
        }
        catch { return null; }
    }

    /// <summary>
    ///     Pre-flight output-token estimate for one image. Exact for sizes in the measured table; for others,
    ///     linear interpolation on pixel count between the two nearest measured sizes, and beyond the table's
    ///     range the nearest end's tokens-per-pixel rate. A model or rung the table does not know falls back to
    ///     the original 1/750 pixels-per-token heuristic, so an estimate is never zero for a valid request.
    /// </summary>
    public static int EstimateOutputTokens(string model, string size, string quality)
    {
        var pixels = PixelsOf(size);
        if (pixels == null) return 0;
        var rung = IsKnownModel(model) ? OutputTokenRung(model, quality) : null;
        if (rung == null) return (int)(pixels.Value * ApproxImageTokenFactor);
        return TokensAt(pixels.Value, rung.Value);
    }

    private static int TokensAt(long pixels, int rung)
    {
        var table = OutputTokenTable;
        for (int i = 0; i < table.Length; i++)
            if (table[i].Pixels == pixels) return table[i].Tokens[rung];

        if (pixels < table[0].Pixels)
            return (int)Math.Round(pixels * (double)table[0].Tokens[rung] / table[0].Pixels);
        var last = table[^1];
        if (pixels > last.Pixels)
            return (int)Math.Round(pixels * (double)last.Tokens[rung] / last.Pixels);

        for (int i = 1; i < table.Length; i++)
        {
            if (pixels > table[i].Pixels) continue;
            var (p0, t0) = (table[i - 1].Pixels, table[i - 1].Tokens[rung]);
            var (p1, t1) = (table[i].Pixels, table[i].Tokens[rung]);
            double f = (pixels - p0) / (double)(p1 - p0);
            return (int)Math.Round(t0 + f * (t1 - t0));
        }
        return (int)(pixels * ApproxImageTokenFactor);
    }

    public static double EstimateCost(int inputImageTokens, int outputImageTokens, int textTokens) =>
        inputImageTokens / 1_000_000.0 * ImageInputPricePer1M
        + outputImageTokens / 1_000_000.0 * ImageOutputPricePer1M
        + textTokens / 1_000_000.0 * TextInputPricePer1M;

    public static string SecondsToText(double seconds) =>
        seconds < 60
            ? $"{seconds:0.0} sec"
            : $"{(int)(seconds / 60)} min {seconds % 60:0.0} sec";

    /// <summary>True when the file is a supported input image and not itself an AIRE output ("*_enhanced").</summary>
    public static bool IsEnhanceableImage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!SupportedExtensions.Contains(ext)) return false;
        return !Path.GetFileNameWithoutExtension(path).EndsWith("_enhanced", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>All enhanceable images directly in <paramref name="folder"/>, name-sorted (no recursion).</summary>
    public static List<string> ScanFolder(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(IsEnhanceableImage)
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ---- the API call --------------------------------------------------------------------------------

    // A single shared client: image generation at 4K routinely takes minutes, so the timeout matches the
    // OpenAI Python SDK's 600 s default rather than HttpClient's 100 s.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(600) };

    /// <summary>
    ///     Retries for transient failures. The stand-alone AIRE.exe called the OpenAI Python SDK with only
    ///     an api_key, so it inherited that SDK's automatic retry; this hand-rolled HTTP client has to
    ///     reproduce it or a single 429 in a long batch permanently fails an image the .exe would have
    ///     recovered. DELIBERATELY NARROWER than the SDK: only statuses that mean the request never ran
    ///     (rate limit / 5xx) and pre-response connection faults are retried, because those cannot have been
    ///     billed. A 429 carrying <c>insufficient_quota</c> is an empty account, not congestion, and is NOT
    ///     retried — OpenAI's guidance is explicit, and retrying it twice only makes the failure slower.
    ///     A request that TIMES OUT is not retried — at a 600 s ceiling the image may well have been
    ///     generated and charged, and silently paying twice is worse than reporting one failure.
    /// </summary>
    private const int MaxTransientRetries = 2;

    /// <summary>Upper bound on honouring a Retry-After header; beyond this the batch is better off failing the image.</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     One OpenAI <c>POST /v1/images/edits</c> call: sends the source image + prompt, returns the decoded
    ///     PNG bytes with the response's usage block. Throws with a plain-language message on any failure
    ///     (caller logs per image). Transient failures are retried per <see cref="MaxTransientRetries"/>,
    ///     waiting out a Retry-After header when the API sends one.
    /// </summary>
    public static async Task<AireEditResult> EditImageAsync(string apiKey, string model, string imagePath,
        string prompt, string size, string quality, bool highInputFidelity, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await SendEditAsync(apiKey, model, imagePath, prompt, size, quality, highInputFidelity, ct)
                    .ConfigureAwait(false);
            }
            catch (TransientApiException ex) when (attempt < MaxTransientRetries)
            {
                // 0.5 s, then 1 s — or what the API asked for. Cancellation propagates out of Delay, so a
                // cancelled job stops here.
                var wait = ex.RetryAfter is { } ra && ra > TimeSpan.Zero
                    ? (ra < MaxRetryAfter ? ra : MaxRetryAfter)
                    : TimeSpan.FromSeconds(0.5 * Math.Pow(2, attempt));
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (TransientApiException ex)
            {
                // Retries exhausted — surface the underlying message, not the wrapper, and keep the class so
                // the batch can still decide whether to carry on.
                throw new AireApiException(ex.Message, ex.Kind);
            }
        }
    }

    /// <summary>Marks a failure that is safe to retry because the request cannot have been billed.</summary>
    private sealed class TransientApiException : Exception
    {
        public TimeSpan? RetryAfter { get; }
        public string Kind { get; }
        public TransientApiException(string message, string kind, TimeSpan? retryAfter = null) : base(message)
        {
            Kind = kind;
            RetryAfter = retryAfter;
        }
    }

    private static async Task<AireEditResult> SendEditAsync(string apiKey, string model, string imagePath,
        string prompt, string size, string quality, bool highInputFidelity, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent(size), "size");
        form.Add(new StringContent(quality), "quality");
        form.Add(new StringContent(OutputFormat), "output_format");
        // A render must never come back with an alpha channel.
        form.Add(new StringContent("opaque"), "background");
        // The API parameter that does what the default prompt spends four sentences asking for. Omitted
        // (the API default is low) when the user turns it off, so the request is then exactly the old one.
        // Never sent to a model that refuses it: gpt-image-2 and the 2.5 pair 400 on the parameter's mere
        // presence, so a ticked box would fail every image in the batch (the v1.9.17 default did exactly that).
        if (highInputFidelity && SupportsInputFidelity(model))
            form.Add(new StringContent("high"), "input_fidelity");

        var bytes = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(MimeFor(imagePath));
        // Singular "image": the form the endpoint has always accepted for one file. The reference now also
        // documents an "images" array; one file under "image" is the shape the first paid 2.5 call verifies.
        form.Add(file, "image", Path.GetFileName(imagePath));

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl.TrimEnd('/') + "/v1/images/edits");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = form;

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // Failed before any response — connection reset / DNS / TLS. Nothing ran, so nothing was billed.
            throw new TransientApiException($"Could not reach the OpenAI API: {ex.Message}", "network");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = OpenAiError.Parse(body, (int)response.StatusCode);
                var (kind, message) = Describe(error);
                throw error.IsRetryable
                    ? new TransientApiException(message, kind, RetryAfterOf(response))
                    : new AireApiException(message, kind);
            }

            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (!(root.TryGetProperty("data", out var data)
                  && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0
                  && data[0].TryGetProperty("b64_json", out var b64)))
                throw new AireApiException("OpenAI returned no image data (unexpected response shape).", "other");

            var image = Convert.FromBase64String(b64.GetString() ?? "");
            return ReadUsage(root, image);
        }
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra == null) return null;
        if (ra.Delta is { } delta) return delta;
        if (ra.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    /// <summary>
    ///     Lifts the usage block and the echoed size/quality off a successful response. Never throws: a
    ///     logging feature must not fail a generated, billed image, so anything odd here simply leaves the
    ///     usage fields null.
    /// </summary>
    public static AireEditResult ReadUsage(JsonElement root, byte[] image)
    {
        int? inputTokens = null, inputImage = null, inputText = null, outputTokens = null;
        string actualSize = "", actualQuality = "";
        try
        {
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                inputTokens = IntOf(usage, "input_tokens");
                outputTokens = IntOf(usage, "output_tokens");
                if (usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
                {
                    inputImage = IntOf(details, "image_tokens");
                    inputText = IntOf(details, "text_tokens");
                }
            }
            if (root.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.String) actualSize = s.GetString() ?? "";
            if (root.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.String) actualQuality = q.GetString() ?? "";
        }
        catch { /* usage is a bonus, never a failure */ }

        return new AireEditResult
        {
            ImageBytes = image,
            InputTokens = inputTokens,
            InputImageTokens = inputImage,
            InputTextTokens = inputText,
            OutputTokens = outputTokens,
            ActualSize = actualSize,
            ActualQuality = actualQuality,
        };
    }

    private static int? IntOf(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    // ---- errors ----------------------------------------------------------------------------------------

    /// <summary>An OpenAI error body, with <c>error.code</c> as the discriminator (the part OpenAI documents as stable).</summary>
    public sealed class OpenAiError
    {
        public int Status { get; init; }
        public string Code { get; init; } = "";
        public string Type { get; init; } = "";
        public string Param { get; init; } = "";
        public string Message { get; init; } = "";

        /// <summary>Rate limits and server failures are retried; quota and user-correctable errors are not.</summary>
        public bool IsRetryable =>
            (Status == 429 && Code != "insufficient_quota") || Status >= 500;

        /// <summary>Which CLASS of failure this is — see <see cref="AireEngine.Describe"/> for the set.</summary>
        public string Kind => Describe(this).Kind;

        public static OpenAiError Parse(string body, int status)
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                    return new OpenAiError
                    {
                        Status = status,
                        Code = Str(err, "code"),
                        Type = Str(err, "type"),
                        Param = Str(err, "param"),
                        Message = Str(err, "message"),
                    };
            }
            catch { /* not JSON */ }
            var trimmed = body.Length > 400 ? body[..400] + "…" : body;
            return new OpenAiError { Status = status, Message = trimmed };
        }

        private static string Str(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    /// <summary>
    ///     The failures a real user actually hits, as plain sentences, plus the raw API text for anything else.
    ///     In a tool that spends money the wording is the feature: "insufficient_quota" and a rate limit are both
    ///     429s, and only one of them is fixed by waiting.
    /// </summary>
    public static string DescribeApiError(OpenAiError e) => Describe(e).Message;

    /// <summary>
    ///     The class of a failure and its sentence, from ONE pass over the error body so the two can never drift
    ///     apart. The kind is the discriminator three separate features run on: whether the batch stops
    ///     (<see cref="StopsBatch"/>), whether a verification acknowledgement was a lie
    ///     (<see cref="IsAccountLevel"/> + the window's banner), and which "What to do" line the log reader
    ///     shows. The set is:
    ///     <c>auth · verification · quota · rate_limit · moderation · fidelity · size · server · network · other</c>.
    /// </summary>
    private static (string Kind, string Message) Describe(OpenAiError e)
    {
        var msg = e.Message ?? "";
        bool mentions(string word) => msg.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;

        if (e.Status == 401)
            return ("auth",
                "OpenAI rejected the API key. Check the key in the API Key box (it starts with sk-), or create a new one "
                + "on the API keys page. Nothing was generated and nothing was charged.");

        // ANY 403, not just one whose wording matches: on an image-edit call there is no other plausible 403,
        // and a reworded message from OpenAI must not drop the user back onto "OpenAI API error (HTTP 403)".
        if (e.Status == 403)
            return ("verification",
                "Your OpenAI organization is not verified. Image models require a one-time identity check at "
                + "platform.openai.com → Settings → Organization → General → Verify Organization. Access can take "
                + "about 15 minutes to take effect afterwards. Nothing was generated and nothing was charged.");

        if (e.Status == 429 && e.Code == "insufficient_quota")
            return ("quota",
                "Your OpenAI account is out of credit. Add credit under Billing, then run the batch again. "
                + "Nothing was generated and nothing was charged.");

        if (e.Status == 429)
            return ("rate_limit",
                "OpenAI is rate-limiting this account. AIRE waited and retried, but the limit held. Your account tier "
                + "limits how many images per minute you can generate — wait a minute, then process the remaining "
                + "images again. Nothing was charged for this image.");

        if (e.Code == "moderation_blocked")
            return ("moderation",
                "OpenAI's safety filter blocked this image. Try adjusting the prompt, or remove this render from the "
                + "queue. The remaining images will still be processed.");

        // Above the generic 400 handler, and keyed off the documented error CODE first: this is the failure the
        // shipped v1.9.17 default produced on every request. An older build's logs and any future regression
        // both still read in English here.
        if (e.Code == "invalid_input_fidelity_model" || (e.Status == 400 && mentions("input_fidelity")))
            return ("fidelity",
                "This model does not accept the Input fidelity setting — it always reads the source image at "
                + "full fidelity. Untick Fidelity and run the batch again. Nothing was generated and nothing "
                + "was charged.");

        if (e.Status == 400 && (e.Param == "size" || mentions("size") || mentions("aspect") || mentions("pixel")
                                || mentions("resolution") || mentions("dimension")))
            return ("size",
                "This resolution is not valid for the selected model. Width and height must be multiples of 16, "
                + "the shape must be between 1:3 and 3:1, and the total must be between 0.66 and 8.3 megapixels. "
                + $"(OpenAI: {msg})");

        var detail = e.Code.Length > 0 ? $"{e.Code}: {msg}" : msg;
        return (e.Status >= 500 ? "server" : "other", $"OpenAI API error (HTTP {e.Status}): {detail}");
    }

    /// <summary>
    ///     Failures where every REMAINING render would fail identically, so the batch stops rather than grinding
    ///     through twenty refusals that say the same thing. Two groups: the ACCOUNT (auth / verification /
    ///     quota) and the REQUEST SHAPE (fidelity / size) — both of those are batch-level settings, identical
    ///     for every image, so image two cannot do better than image one.
    ///     <para>
    ///     Deliberately absent: moderation (about that one image), rate_limit and server/network (already
    ///     retried, and the next image may well succeed), and anything unclassified.
    ///     </para>
    /// </summary>
    public static bool StopsBatch(string kind) =>
        kind is "auth" or "verification" or "quota" or "fidelity" or "size";

    /// <summary>The subset of <see cref="StopsBatch"/> that is about the ACCOUNT rather than the request — what
    /// the user fixes on platform.openai.com, not in AIRE. A "verification" here is also what clears a
    /// verification acknowledgement that turned out to be wrong.</summary>
    public static bool IsAccountLevel(string kind) => kind is "auth" or "verification" or "quota";
}

/// <summary>
///     An OpenAI failure with its class attached (<see cref="AireEngine.DescribeApiError"/>'s companion), so
///     <see cref="AireJob"/> can tell a refusal that will repeat on every remaining image from one that is
///     about the image in hand. The message is already the plain-language sentence — callers log
///     <c>ex.Message</c> exactly as before.
/// </summary>
public sealed class AireApiException : Exception
{
    public string Kind { get; }
    public AireApiException(string message, string kind) : base(message) => Kind = kind;
}
