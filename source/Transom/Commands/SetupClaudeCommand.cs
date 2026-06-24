using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Nice3point.Revit.Toolkit.External;
using Transom.Core;

namespace Transom.Commands;

/// <summary>
///     #107: ONE-CLICK Claude setup. Registers BOTH MCP servers with the user's Claude clients in a single
///     action — the data bridge (<c>transom</c>, via <see cref="McpRegistration"/>) and the optional
///     UI-Assist server (<c>transom-ui-assist</c>, via <see cref="ClickHelperRegistration"/>) — and shows the
///     "restart your Claude client" notice ONCE. Replaces the two separate "Register with Claude" +
///     "Claude UI Assist" buttons. Each path is idempotent, non-clobbering, admin-free, and self-heals the
///     bundled exes. User-triggered only — nothing here runs unless the button is clicked, so Transom stays a
///     standalone product for users who don't have Claude.
/// </summary>
[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public class SetupClaudeCommand : ExternalCommand
{
    public override void Execute()
    {
        var settings = TransomSettings.Load();
        var port = settings.BridgeSelfHostPort;

        // 1) Data bridge (transom): install the shim if needed, then register.
        bool bridgeReady = McpRegistration.EnsureShimInstalled();
        var bridgeRes = McpRegistration.Register(port);
        if (bridgeRes.Updated > 0) { settings.McpRegisteredPort = port; settings.Save(); }

        // 2) UI-Assist (transom-ui-assist): install its exes if needed, then register.
        bool uiReady = ClickHelperRegistration.EnsureInstalled();
        var uiRes = ClickHelperRegistration.Register();

        int updated = bridgeRes.Updated + uiRes.Updated;
        int errors = bridgeRes.Errors + uiRes.Errors;
        bool componentsMissing = !bridgeReady || !uiReady;

        // ONE restart notice, regardless of how many servers changed.
        string head = updated > 0
            ? "Set up Claude — registered Transom's MCP servers with Claude Desktop / Claude Code.\n\n" +
              "Restart your Claude client to load them, then turn the Claude Bridge ON in Revit (the ribbon " +
              "toggle). Claude can then read schedules + write parameters (data bridge) and, when you ask, drive " +
              "Revit UI commands that have no API such as Edit Group (UI-Assist).\n\n"
            : componentsMissing
                ? "Couldn't install the bundled Claude components from the add-in folder " +
                  "(Transom.McpShim.exe / Transom.ClickHelper.exe / Transom.ClickHelper.Mcp.exe). " +
                  "Reinstall Transom, or report this.\n\n"
                : errors > 0
                    ? "Set up finished with issues:\n\n"
                    : "Already set up — nothing to change.\n\n";

        var details =
            "Data bridge (transom):\n  " + string.Join("\n  ", bridgeRes.Messages) +
            "\n\nUI-Assist (transom-ui-assist):\n  " + string.Join("\n  ", uiRes.Messages) +
            $"\n\nShim: {McpRegistration.ShimPath}" +
            $"\nUI-Assist server: {ClickHelperRegistration.McpServerPath}" +
            $"\nBridge port: {port}";

        bool isError = componentsMissing || errors > 0;
        Transom.Views.ReportDialog.Show("Transom — Set up Claude", head.TrimEnd(), details, isError);
    }
}
