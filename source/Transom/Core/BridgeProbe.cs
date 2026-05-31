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
            var resp = await Http.GetAsync($"http://localhost:{port}/revit_mcp/status/", cts.Token)
                .ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
