using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Nice3point.Revit.Toolkit.External;
using Transom.Core;

namespace Transom.Commands;

/// <summary>
///     Registers the bundled self-contained MCP shim with the user's Claude clients (Option A from
///     install/MCP_CONFIG_MERGE.md): an idempotent, non-clobbering, admin-free merge of a single
///     <c>mcpServers.transom</c> entry into the per-user config files. User-triggered so we never touch
///     external config without an explicit click.
/// </summary>
[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public class RegisterBridgeCommand : ExternalCommand
{
    public override void Execute()
    {
        var settings = TransomSettings.Load();
        var port = settings.BridgeSelfHostPort;

        // Self-heal: install the bundled shim from the add-in payload if it isn't in place yet, so a missing
        // shim becomes a one-click fix here rather than a dead-end "shim not found" warning.
        bool shimReady = McpRegistration.EnsureShimInstalled();

        var res = McpRegistration.Register(port);
        if (res.Updated > 0) { settings.McpRegisteredPort = port; settings.Save(); }

        var body = string.Join("\n", res.Messages);
        var head = res.Updated > 0
            ? "Registered Transom's MCP bridge with Claude.\n\nRestart Claude Desktop / Claude Code to pick it " +
              "up, then turn the Claude Bridge ON in Revit (the ribbon toggle).\n\n"
            : !shimReady
                ? "Couldn't install the bridge components — the bundled shim (Transom.McpShim.exe) wasn't found " +
                  "in the add-in folder. Reinstall Transom (v1.3.1+), or report this.\n\n"
                : res.Errors > 0
                    ? "Registration finished with issues:\n\n"
                    : "Nothing to change.\n\n";

        var details = body
                      + $"\n\nShim: {McpRegistration.ShimPath}"
                      + $"\nShim present: {(McpRegistration.ShimPresent() ? "yes" : "NO")}"
                      + $"\nPort: {port}";
        var isError = !shimReady || res.Errors > 0;
        Transom.Views.ReportDialog.Show("Transom — Register Claude Bridge", head.TrimEnd(), details, isError);
    }
}
