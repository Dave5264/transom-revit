using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Transom.Core;

/// <summary>A Higgsfield API credential: a key id and a secret, sent together as one Authorization header.</summary>
public sealed record HiggsfieldCredentials(string KeyId, string Secret)
{
    public bool IsComplete => !string.IsNullOrWhiteSpace(KeyId) && !string.IsNullOrWhiteSpace(Secret);
    internal string AuthorizationValue => $"Key {KeyId.Trim()}:{Secret.Trim()}";
}

public enum HiggsfieldErrorKind
{
    BadCredentials, InsufficientCredits, Busy, ModelUnavailable, Validation, NotFound, CannotCancel,
    ServerError, Network, Timeout, Other,
}

/// <summary>A Higgsfield failure with the vendor's status mapped to a sentence a user can act on.</summary>
public sealed class HiggsfieldApiException : Exception
{
    public int StatusCode { get; }
    public HiggsfieldErrorKind Kind { get; }
    /// <summary>The vendor's own "detail" text (plus the correlation id when one was sent), for logs.</summary>
    public string Detail { get; }

    public HiggsfieldApiException(HiggsfieldErrorKind kind, int statusCode, string detail, string message)
        : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
        Detail = detail;
    }
}

/// <summary>A presigned upload slot from POST /files/generate-upload-url.</summary>
public sealed class HiggsfieldUpload
{
    public string UploadUrl = "";
    public string PublicUrl = "";
    public string ContentType = "";
    public Dictionary<string, string> UploadHeaders = new();
}

/// <summary>What a generation will cost, from the estimate endpoint. The vendor quotes both numbers as
/// STRINGS; they are parsed invariant-culture so a comma-decimal locale cannot mangle a price.</summary>
public sealed class HiggsfieldEstimate
{
    public decimal Credits;
    public decimal Usd;
    /// <summary>The figures exactly as the vendor sent them, for the confirmation dialog and the log.</summary>
    public string CreditsText = "";
    public string UsdText = "";
}

/// <summary>The request envelope: returned by a submit, and by every status poll.</summary>
public sealed class HiggsfieldRequestStatus
{
    public string Status = "";      // queued | in_progress | nsfw | failed | completed | canceled
    public string RequestId = "";
    public string StatusUrl = "";
    public string CancelUrl = "";
    public string? Error;
    public string? VideoUrl;
    public List<string> ImageUrls = new();

    public bool IsTerminal => Status is "completed" or "failed" or "nsfw" or "canceled";
}

/// <summary>One camera-motion preset for the DoP models: the vendor's UUID plus a display name.</summary>
public sealed class HiggsfieldMotionPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>
///     The Higgsfield HTTP client: auth, presigned upload, cost estimate, submit, poll, cancel, download.
///     Deliberately Revit-free (files + HTTPS) like the rest of Transom.Aire. Everything here was read from
///     docs.higgsfield.ai (concepts/* pages, OpenAPI spec) and the official Python SDK on 2026-09-05.
///     <para>
///     Retry policy mirrors AIRE's OpenAI path: retry pre-response connection faults and rate-limit /
///     gateway statuses that mean the request never ran; NEVER retry a timeout, because a generation
///     request that timed out may already be queued and billed. Submits are narrower still — a bare 500
///     from the application could come after the request was enqueued, so it is not retried either.
///     </para>
/// </summary>
public static class HiggsfieldClient
{
    public const string DefaultBaseUrl = "https://api.higgsfield.ai";

    /// <summary>The official SDK's default host — an older API surface that (per third-party clients) lists
    /// the DoP motion presets at /v1/motions, which the current spec does not expose anywhere.</summary>
    public const string DefaultMotionsBaseUrl = "https://platform.higgsfield.ai";

    public const string CloudUrl = "https://cloud.higgsfield.ai";
    public const string AuthDocsUrl = "https://docs.higgsfield.ai/docs/authentication";
    public const string BillingDocsUrl = "https://docs.higgsfield.ai/docs/concepts/billing-and-retention";

    /// <summary>Settable so an out-of-process harness can point the whole client at a local mock and
    /// exercise upload → estimate → submit → poll → cancel → download without spending a cent.</summary>
    public static string BaseUrl { get; set; } = DefaultBaseUrl;
    public static string MotionsBaseUrl { get; set; } = DefaultMotionsBaseUrl;

    /// <summary>Output URLs live for at least this long; the job downloads immediately regardless.</summary>
    public const int OutputRetentionDays = 7;

    // A 4K PNG is 10–30 MB; on a slow uplink the PUT alone can pass HttpClient's 100 s default.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };
    private static readonly HttpClient Downloads = new() { Timeout = TimeSpan.FromMinutes(10) };

    private const int MaxRetries = 2;

    // ---- upload ----------------------------------------------------------------

    public static async Task<HiggsfieldUpload> GenerateUploadUrlAsync(HiggsfieldCredentials creds,
        string contentType, CancellationToken ct)
    {
        var body = new JsonObject { ["content_type"] = contentType };
        var (_, text) = await SendAsync(() => JsonRequest(HttpMethod.Post, BaseUrl + "/files/generate-upload-url", creds, body),
            "upload-url", RetryTransient, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var upload = new HiggsfieldUpload
        {
            UploadUrl = Str(root, "upload_url"),
            PublicUrl = Str(root, "public_url"),
            ContentType = Str(root, "content_type"),
        };
        if (root.TryGetProperty("upload_headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            foreach (var h in headers.EnumerateObject())
                if (h.Value.ValueKind == JsonValueKind.String) upload.UploadHeaders[h.Name] = h.Value.GetString() ?? "";
        if (upload.UploadUrl.Length == 0 || upload.PublicUrl.Length == 0)
            throw new HiggsfieldApiException(HiggsfieldErrorKind.Other, 200, text,
                "Higgsfield returned an upload slot without upload_url / public_url (unexpected response shape).");
        if (upload.ContentType.Length == 0) upload.ContentType = contentType;
        return upload;
    }

    /// <summary>PUTs the bytes to the presigned URL with exactly the headers the slot asked for. The API
    /// credentials are deliberately NOT sent here — the storage host is not Higgsfield's API.</summary>
    public static async Task UploadAsync(HiggsfieldUpload slot, byte[] bytes, CancellationToken ct)
    {
        HttpRequestMessage Make()
        {
            var req = new HttpRequestMessage(HttpMethod.Put, slot.UploadUrl) { Content = new ByteArrayContent(bytes) };
            bool contentTypeSet = false;
            foreach (var (name, value) in slot.UploadHeaders)
            {
                if (name.StartsWith("content-", StringComparison.OrdinalIgnoreCase))
                {
                    req.Content.Headers.TryAddWithoutValidation(name, value);
                    if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase)) contentTypeSet = true;
                }
                else req.Headers.TryAddWithoutValidation(name, value);
            }
            if (!contentTypeSet) req.Content.Headers.ContentType = new MediaTypeHeaderValue(slot.ContentType);
            return req;
        }
        // A PUT to storage is idempotent, so gateway and server errors are safe to retry.
        await SendAsync(Make, "upload", status => status == 429 || status >= 500, ct).ConfigureAwait(false);
    }

    /// <summary>Upload one local file: slot, PUT, and the public URL to reference in a request.</summary>
    public static async Task<string> UploadFileAsync(HiggsfieldCredentials creds, string path, CancellationToken ct)
    {
        var contentType = MimeFor(path);
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var slot = await GenerateUploadUrlAsync(creds, contentType, ct).ConfigureAwait(false);
        await UploadAsync(slot, bytes, ct).ConfigureAwait(false);
        return slot.PublicUrl;
    }

    // ---- estimate / submit / poll / cancel ------------------------------------------

    /// <summary>
    ///     POST /estimate{model path} with the SAME body as a real generation. Charges nothing and returns
    ///     the vendor's own credits and USD — the number the confirmation dialog shows. Documented only on
    ///     the billing page, not in the OpenAPI spec.
    /// </summary>
    public static async Task<HiggsfieldEstimate> EstimateAsync(HiggsfieldCredentials creds, string modelPath,
        JsonObject body, CancellationToken ct)
    {
        var (_, text) = await SendAsync(() => JsonRequest(HttpMethod.Post, BaseUrl + "/estimate" + modelPath, creds, body),
            "estimate", RetryTransient, ct).ConfigureAwait(false);
        return ParseEstimate(text);
    }

    public static HiggsfieldEstimate ParseEstimate(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!TryMoney(root, "credits", out var credits, out var creditsText)
            || !TryMoney(root, "usd", out var usd, out var usdText))
            throw new HiggsfieldApiException(HiggsfieldErrorKind.Other, 200, text,
                "Higgsfield's estimate did not contain credits and usd (unexpected response shape).");
        return new HiggsfieldEstimate { Credits = credits, Usd = usd, CreditsText = creditsText, UsdText = usdText };
    }

    /// <summary>Accepts the documented quoted string ("0.094") and, defensively, a bare number.</summary>
    private static bool TryMoney(JsonElement root, string name, out decimal value, out string text)
    {
        value = 0; text = "";
        if (!root.TryGetProperty(name, out var el)) return false;
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                text = (el.GetString() ?? "").Trim();
                return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            case JsonValueKind.Number:
                value = el.GetDecimal();
                text = value.ToString(CultureInfo.InvariantCulture);
                return true;
            default:
                return false;
        }
    }

    public static async Task<HiggsfieldRequestStatus> SubmitAsync(HiggsfieldCredentials creds, string modelPath,
        JsonObject body, CancellationToken ct)
    {
        // Only statuses that prove the request never reached the application are retried (rate limit and
        // gateway errors). A 500 could follow an enqueue, and resubmitting would be a second paid clip.
        var (_, text) = await SendAsync(() => JsonRequest(HttpMethod.Post, BaseUrl + modelPath, creds, body),
            "submit", status => status is 429 or 502 or 503 or 504, ct).ConfigureAwait(false);
        var status = ParseStatus(text);
        if (status.RequestId.Length == 0)
            throw new HiggsfieldApiException(HiggsfieldErrorKind.Other, 200, text,
                "Higgsfield accepted the request but returned no request_id (unexpected response shape).");
        // Use the URLs the vendor handed back; fall back to the documented pattern only if it did not.
        if (status.StatusUrl.Length == 0) status.StatusUrl = $"{BaseUrl}/requests/{status.RequestId}/status";
        if (status.CancelUrl.Length == 0) status.CancelUrl = $"{BaseUrl}/requests/{status.RequestId}/cancel";
        return status;
    }

    public static async Task<HiggsfieldRequestStatus> GetStatusAsync(HiggsfieldCredentials creds, string statusUrl,
        CancellationToken ct)
    {
        var (_, text) = await SendAsync(() => JsonRequest(HttpMethod.Get, statusUrl, creds, null),
            "status", RetryTransient, ct).ConfigureAwait(false);
        return ParseStatus(text);
    }

    /// <summary>POST the cancel URL. 202 = cancelled (and refunded, as it was still queued). A 400 means
    /// generation had already started and surfaces as <see cref="HiggsfieldErrorKind.CannotCancel"/>.</summary>
    public static async Task CancelAsync(HiggsfieldCredentials creds, string cancelUrl, CancellationToken ct)
    {
        await SendAsync(() => JsonRequest(HttpMethod.Post, cancelUrl, creds, null), "cancel", RetryTransient, ct)
            .ConfigureAwait(false);
    }

    public static HiggsfieldRequestStatus ParseStatus(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var s = new HiggsfieldRequestStatus
        {
            Status = Str(root, "status"),
            RequestId = Str(root, "request_id"),
            StatusUrl = Str(root, "status_url"),
            CancelUrl = Str(root, "cancel_url"),
        };
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String) s.Error = err.GetString();
        if (root.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object)
            s.VideoUrl = Str(video, "url");
        if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
            foreach (var img in images.EnumerateArray())
                if (img.ValueKind == JsonValueKind.Object) s.ImageUrls.Add(Str(img, "url"));
        return s;
    }

    // ---- download --------------------------------------------------------------

    /// <summary>Streams an output URL to disk via a temp file, so a half-written clip never looks finished.
    /// Output URLs are public/presigned storage links; credentials are only sent if the URL is on the API host.</summary>
    public static async Task DownloadAsync(HiggsfieldCredentials creds, string url, string destination, CancellationToken ct)
    {
        var temp = destination + ".part";
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (SameHost(url, BaseUrl)) request.Headers.TryAddWithoutValidation("Authorization", creds.AuthorizationValue);
                using var response = await Downloads.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    int status = (int)response.StatusCode;
                    if ((status == 429 || status >= 500) && attempt < MaxRetries)
                    {
                        await Backoff(attempt, ct).ConfigureAwait(false);
                        continue;
                    }
                    throw new HiggsfieldApiException(HiggsfieldErrorKind.Other, status, "",
                        $"The finished clip could not be downloaded (HTTP {status}). It stays on Higgsfield for at least "
                        + $"{OutputRetentionDays} days at:\n{url}");
                }
                await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                File.Move(temp, destination, overwrite: true);
                return;
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                await Backoff(attempt, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new HiggsfieldApiException(HiggsfieldErrorKind.Network, 0, ex.Message,
                    $"Could not download the finished clip: {ex.Message}. It stays on Higgsfield for at least "
                    + $"{OutputRetentionDays} days at:\n{url}");
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    private static bool SameHost(string a, string b)
    {
        try { return string.Equals(new Uri(a).Host, new Uri(b).Host, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // ---- motion presets --------------------------------------------------------

    /// <summary>
    ///     Best-effort: GET /v1/motions on the older platform host, which third-party clients report returns
    ///     {id, name, description} for every DoP preset. Not in the published spec, so it is sent with both the
    ///     current Authorization header and the legacy hf-api-key / hf-secret pair the auth page says are still
    ///     accepted. Parsed leniently — an array, or an object whose first array-valued member is the list.
    /// </summary>
    public static async Task<List<HiggsfieldMotionPreset>> GetMotionPresetsAsync(HiggsfieldCredentials creds, CancellationToken ct)
    {
        HttpRequestMessage Make()
        {
            var req = JsonRequest(HttpMethod.Get, MotionsBaseUrl.TrimEnd('/') + "/v1/motions", creds, null);
            req.Headers.TryAddWithoutValidation("hf-api-key", creds.KeyId.Trim());
            req.Headers.TryAddWithoutValidation("hf-secret", creds.Secret.Trim());
            return req;
        }
        var (_, text) = await SendAsync(Make, "motions", RetryTransient, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(text);
        var list = doc.RootElement;
        if (list.ValueKind == JsonValueKind.Object)
        {
            list = default;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Array) { list = p.Value; break; }
        }
        var presets = new List<HiggsfieldMotionPreset>();
        if (list.ValueKind != JsonValueKind.Array)
            throw new HiggsfieldApiException(HiggsfieldErrorKind.Other, 200, text,
                "Higgsfield answered, but not with a list of motion presets (unexpected response shape).");
        foreach (var m in list.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            var id = Str(m, "id");
            if (id.Length == 0) continue;
            var name = Str(m, "name");
            presets.Add(new HiggsfieldMotionPreset
            {
                Id = id,
                Name = name.Length > 0 ? name : id,
                Description = Str(m, "description"),
            });
        }
        return presets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- plumbing ---------------------------------------------------------------

    private static bool RetryTransient(int status) => status == 429 || status >= 500;

    private static HttpRequestMessage JsonRequest(HttpMethod method, string url, HiggsfieldCredentials creds, JsonObject? body)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Authorization", creds.AuthorizationValue);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body != null)
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return req;
    }

    /// <summary>Sends with the retry policy described on the class; returns the successful response + body
    /// or throws a classified <see cref="HiggsfieldApiException"/>.</summary>
    private static async Task<(HttpResponseMessage Response, string Body)> SendAsync(Func<HttpRequestMessage> make,
        string what, Func<int, bool> retryStatus, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = make();
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                // Failed before any response — connection reset / DNS / TLS. Nothing ran, so nothing was billed.
                await Backoff(attempt, ct).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new HiggsfieldApiException(HiggsfieldErrorKind.Network, 0, ex.Message,
                    $"Could not reach Higgsfield ({what}): {ex.Message}");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HiggsfieldApiException(HiggsfieldErrorKind.Timeout, 0, "",
                    $"Higgsfield did not answer the {what} call within {Http.Timeout.TotalSeconds:0} s. "
                    + "Not retried automatically — a request that timed out may still have gone through.");
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return (response, body);

            int status = (int)response.StatusCode;
            var correlation = response.Headers.TryGetValues("X-Correlation-ID", out var ids) ? ids.FirstOrDefault() ?? "" : "";
            response.Dispose();
            if (retryStatus(status) && attempt < MaxRetries)
            {
                await Backoff(attempt, ct).ConfigureAwait(false);
                continue;
            }
            throw Classify(status, body, what, correlation);
        }
    }

    /// <summary>0.5 s, then 1 s. Cancellation propagates out of Delay, so a cancelled job stops here.</summary>
    private static Task Backoff(int attempt, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(0.5 * Math.Pow(2, attempt)), ct);

    /// <summary>The vendor's status codes (docs/concepts/errors) turned into sentences that say what to do.</summary>
    private static HiggsfieldApiException Classify(int status, string body, string what, string correlation)
    {
        var detail = ExtractDetail(body);
        var logged = correlation.Length > 0 ? $"{detail} [correlation {correlation}]" : detail;

        HiggsfieldApiException Make(HiggsfieldErrorKind kind, string message) => new(kind, status, logged, message);

        switch (status)
        {
            case 401:
                return Make(HiggsfieldErrorKind.BadCredentials,
                    "Higgsfield rejected these credentials. Check the Key ID and Secret — both come from cloud.higgsfield.ai, "
                    + "and a key deleted there stops working here.");
            case 403:
                return Make(HiggsfieldErrorKind.InsufficientCredits,
                    "Higgsfield says this account is out of credits, so nothing was generated or charged. "
                    + "Add credits at cloud.higgsfield.ai and try again." + Detail(detail));
            case 400 when detail.Contains("concurrent", StringComparison.OrdinalIgnoreCase):
                return Make(HiggsfieldErrorKind.Busy,
                    "Higgsfield is busy: this account has reached its limit of concurrent requests. "
                    + "Wait for the other request(s) to finish, then try again." + Detail(detail));
            case 400 when what == "cancel":
                return Make(HiggsfieldErrorKind.CannotCancel,
                    "Higgsfield had already started generating this clip, so it can no longer be cancelled. "
                    + "It will finish and be charged.");
            case 404 when what is "estimate" or "submit":
                return Make(HiggsfieldErrorKind.ModelUnavailable,
                    "Higgsfield says this model is not available to your account (HTTP 404). Pick another model, "
                    + "or check what your plan includes at cloud.higgsfield.ai." + Detail(detail));
            case 404:
                return Make(HiggsfieldErrorKind.NotFound,
                    "Higgsfield does not recognise this request (HTTP 404) — it may belong to a different account or key."
                    + Detail(detail));
            case 422:
                return Make(HiggsfieldErrorKind.Validation,
                    "Higgsfield rejected the request parameters: " + (detail.Length > 0 ? detail : "no detail given")
                    + ". Nothing was charged.");
            case 423:
                return Make(HiggsfieldErrorKind.ModelUnavailable,
                    "This model is temporarily blocked on Higgsfield (HTTP 423). Try again later, or pick another model.");
            case 503:
                return Make(HiggsfieldErrorKind.ModelUnavailable,
                    "This model is disabled or not ready on Higgsfield (HTTP 503). Try again later, or pick another model.");
            case >= 500:
                return Make(HiggsfieldErrorKind.ServerError, $"Higgsfield server error (HTTP {status}) during {what}." + Detail(detail));
            case 400:
                return Make(HiggsfieldErrorKind.Validation, "Higgsfield rejected the request (HTTP 400)." + Detail(detail));
            default:
                return Make(HiggsfieldErrorKind.Other, $"Higgsfield error (HTTP {status}) during {what}." + Detail(detail));
        }
    }

    private static string Detail(string detail) => detail.Length > 0 ? $" ({detail})" : "";

    /// <summary>Pulls "detail" out of the FastAPI-style error envelope; a validation error carries a list.</summary>
    private static string ExtractDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Trim(body);
            foreach (var name in new[] { "detail", "details", "message", "error" })
            {
                if (!root.TryGetProperty(name, out var el)) continue;
                if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? "";
                if (el.ValueKind == JsonValueKind.Array)
                {
                    var parts = new List<string>();
                    foreach (var item in el.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("msg", out var msg))
                        {
                            var loc = item.TryGetProperty("loc", out var l) && l.ValueKind == JsonValueKind.Array
                                ? string.Join(".", l.EnumerateArray().Select(x => x.ToString()))
                                : "";
                            parts.Add(loc.Length > 0 ? $"{loc}: {msg}" : msg.ToString());
                        }
                        else parts.Add(item.ToString());
                    }
                    return string.Join("; ", parts);
                }
                return el.ToString();
            }
        }
        catch { /* not JSON */ }
        return Trim(body);
    }

    private static string Trim(string body) => body.Length > 300 ? body[..300] + "…" : body;

    private static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    public static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".mp4" => "video/mp4",
        _ => "application/octet-stream",
    };
}
