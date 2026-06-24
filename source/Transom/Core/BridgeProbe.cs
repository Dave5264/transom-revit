using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Transom.Core;

/// <summary>
///     Detects the Revit MCP bridge by pinging its status endpoint. Informational only — nothing in the
///     add-in depends on Claude being present. Async + short timeout so it never blocks the UI thread.
/// </summary>
public static class BridgeProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(700) };

    public static async Task<bool> IsAvailableAsync(int port)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            // 127.0.0.1 (not "localhost", which can resolve to IPv6 ::1). Hit the BUNDLED bridge's /status route
            // (BridgeServer.cs) and require it to actually be OUR bridge — a 200 from something else squatting on
            // the port (or the pyRevit MCP) must NOT count. The status body is {"ok":true,"tool":"Transom",...}.
            var resp = await Http.GetAsync($"http://127.0.0.1:{port}/status", cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return IsOurBridge(body);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True only when the /status body is Transom's own healthy response (<c>ok==true</c> AND
    /// <c>tool=="Transom"</c>) — so an unrelated listener on the port can't false-positive.</summary>
    private static bool IsOurBridge(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okEl)
                      && okEl.ValueKind == System.Text.Json.JsonValueKind.True;
            bool isTransom = root.TryGetProperty("tool", out var toolEl)
                             && toolEl.ValueKind == System.Text.Json.JsonValueKind.String
                             && toolEl.GetString() == "Transom";
            return ok && isTransom;
        }
        catch { return false; }
    }
}
