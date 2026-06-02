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
        var port = TransomSettings.Load().BridgeSelfHostPort;
        var res = McpRegistration.Register(port);

        var body = string.Join("\n", res.Messages);
        var head = res.Updated > 0
            ? "Registered Transom's MCP bridge with Claude.\n\nRestart Claude Desktop / Claude Code to pick it " +
              "up, then turn the Claude Bridge ON in Revit (the ribbon toggle).\n\n"
            : res.Errors > 0
                ? "Registration finished with issues:\n\n"
                : "Nothing to change.\n\n";

        var dlg = new TaskDialog("Transom — Register Claude Bridge")
        {
            MainInstruction = head.TrimEnd(),
            MainContent = body + $"\n\nShim: {McpRegistration.ShimPath}\nPort: {port}",
        };
        dlg.Show();
    }
}
