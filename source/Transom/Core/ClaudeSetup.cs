using System.Collections.Generic;

namespace Transom.Core;

/// <summary>
///     One-shot Claude setup, run by the Settings toggle the first time Claude Assist turns on (and re-run
///     harmlessly on later ONs — every path is idempotent, non-clobbering, admin-free, and self-heals the
///     bundled exes). Registers BOTH MCP servers with the user's Claude clients: the data bridge
///     (<c>transom</c>, via <see cref="McpRegistration"/>) and UI-Assist (<c>transom-ui-assist</c>, via
///     <see cref="ClickHelperRegistration"/>). Replaces the retired "Set up Claude" ribbon button
///     (SetupClaudeCommand). User-triggered only — nothing here runs unless the toggle is flipped, so Transom
///     stays a standalone product for users who don't have Claude.
/// </summary>
public static class ClaudeSetup
{
    public sealed record Result(int Updated, int Errors, bool ComponentsMissing, string Details)
    {
        /// <summary>Something was newly registered → the user must restart their Claude client once.</summary>
        public bool NeedsClaudeRestart => Updated > 0;
        public bool Failed => ComponentsMissing || Errors > 0;
    }

    /// <summary>Ensure both servers are installed + registered. Never throws; failures land in the Result.</summary>
    public static Result EnsureAll(TransomSettings settings)
    {
        var port = settings.BridgeSelfHostPort;

        // 1) Data bridge (transom): install the shim if needed, then register.
        bool bridgeReady = McpRegistration.EnsureShimInstalled();
        var bridgeRes = McpRegistration.Register(port);
        if (bridgeRes.Updated > 0) { settings.McpRegisteredPort = port; settings.Save(); }

        // 2) UI-Assist (transom-ui-assist): install its exes if needed, then register.
        bool uiReady = ClickHelperRegistration.EnsureInstalled();
        var uiRes = ClickHelperRegistration.Register();

        var details =
            "Data bridge (transom):\n  " + string.Join("\n  ", bridgeRes.Messages) +
            "\n\nUI-Assist (transom-ui-assist):\n  " + string.Join("\n  ", uiRes.Messages) +
            $"\n\nShim: {McpRegistration.ShimPath}" +
            $"\nUI-Assist server: {ClickHelperRegistration.McpServerPath}" +
            $"\nBridge port: {port}";

        return new Result(
            bridgeRes.Updated + uiRes.Updated,
            bridgeRes.Errors + uiRes.Errors,
            !bridgeReady || !uiReady,
            details);
    }
}
