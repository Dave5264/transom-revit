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
///     AIRE — "AI Render Enhancer" — engine: model/size/quality catalog, token-based cost estimation, and
///     the OpenAI <c>images/edits</c> call. Ported 1:1 from the stand-alone AIRE.exe (PySide6/Python 3.11;
///     constants recovered from its bytecode 2026-08-01), so the WPF window and the bridge tools share one
///     behavior. Deliberately Revit-free: pure files + HTTPS, callable from any thread.
/// </summary>
public static class AireEngine
{
    public const string AppName = "AI Render Enhancer";

    public static readonly string[] SupportedExtensions = { ".png", ".jpg", ".jpeg", ".webp" };

    /// <summary>Output files are always "&lt;stem&gt;_enhanced.png" next to nothing — written into the output folder.</summary>
    public const string OutputFormat = "png";

    public const string OpenAiBillingUrl = "https://platform.openai.com/settings/organization/billing/overview";

    /// <summary>Where a user creates the key AIRE needs — linked from the API-key instructions overlay.</summary>
    public const string OpenAiApiKeysUrl = "https://platform.openai.com/api-keys";

    /// <summary>Resolution choices per model — gpt-image-2 adds the 4K/2K tiers; the rest take the standard sizes.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ModelSizeOptions = new Dictionary<string, string[]>
    {
        ["gpt-image-2"] = new[] { "3840x2160", "2160x3840", "2048x2048", "2048x1152", "1536x1024", "1024x1536", "1024x1024", "auto" },
        ["gpt-image-1.5"] = new[] { "1536x1024", "1024x1536", "1024x1024", "auto" },
        ["gpt-image-1"] = new[] { "1536x1024", "1024x1536", "1024x1024", "auto" },
        ["gpt-image-1-mini"] = new[] { "1536x1024", "1024x1536", "1024x1024", "auto" },
    };

    public static readonly string[] QualityOptions = { "high", "medium", "low", "auto" };

    public const string DefaultModel = "gpt-image-2";
    public const string DefaultSize = "3840x2160";
    public const string DefaultQuality = "high";

    // Estimation-only pricing (USD per 1M tokens) — the confirm dialog and CSV log are estimates, not billing.
    public const double ImageInputPricePer1M = 8.0;
    public const double ImageOutputPricePer1M = 30.0;
    public const double TextInputPricePer1M = 5.0;

    /// <summary>Approximate image tokens per pixel (1/750) — same heuristic the original app shipped.</summary>
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

    /// <summary>Tokens for a "WxH" size string; "auto" is estimated as 1536x1024 (the original's assumption).</summary>
    public static int EstimateImageTokensFromSize(string sizeString)
    {
        try
        {
            var s = (sizeString ?? "").Trim().ToLowerInvariant();
            if (s == "auto") s = "1536x1024";
            var parts = s.Split('x');
            long pixels = (long)int.Parse(parts[0]) * int.Parse(parts[1]);
            return (int)(pixels * ApproxImageTokenFactor);
        }
        catch
        {
            return 0;
        }
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

    // A single shared client: image generation at 4K routinely takes minutes, so the timeout matches the
    // OpenAI Python SDK's 600 s default rather than HttpClient's 100 s.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(600) };

    /// <summary>
    ///     One OpenAI <c>POST /v1/images/edits</c> call: sends the source image + prompt, returns the
    ///     decoded PNG bytes. Throws with the API's error message on any failure (caller logs per image).
    /// </summary>
    public static async Task<byte[]> EditImageAsync(string apiKey, string model, string imagePath,
        string prompt, string size, string quality, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent(size), "size");
        form.Add(new StringContent(quality), "quality");
        form.Add(new StringContent(OutputFormat), "output_format");

        var bytes = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(MimeFor(imagePath));
        form.Add(file, "image", Path.GetFileName(imagePath));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/edits");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = form;

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ApiErrorMessage(body, (int)response.StatusCode));

        using var json = JsonDocument.Parse(body);
        if (json.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0
            && data[0].TryGetProperty("b64_json", out var b64))
            return Convert.FromBase64String(b64.GetString() ?? "");

        throw new InvalidOperationException("OpenAI returned no image data (unexpected response shape).");
    }

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    /// <summary>Pulls error.message out of an OpenAI error body; falls back to the raw body (truncated).</summary>
    private static string ApiErrorMessage(string body, int status)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg))
                return $"OpenAI API error (HTTP {status}): {msg.GetString()}";
        }
        catch { /* not JSON */ }
        var trimmed = body.Length > 400 ? body[..400] + "…" : body;
        return $"OpenAI API error (HTTP {status}): {trimmed}";
    }
}
