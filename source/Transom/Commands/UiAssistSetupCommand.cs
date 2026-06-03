using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Nice3point.Revit.Toolkit.External;
using Transom.Core;

namespace Transom.Commands;

/// <summary>
///     One-time set up of Transom's optional Claude UI-Assist feature: installs the bundled UI engine +
///     MCP server to the per-user folder and registers the <c>transom-ui-assist</c> server with the
///     user's Claude clients (idempotent, non-clobbering, admin-free — the same merge pattern as the
///     data bridge). User-triggered only; nothing here runs unless this button is clicked, so Transom
///     stays a standalone product for users who don't have Claude.
/// </summary>
[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public class UiAssistSetupCommand : ExternalCommand
{
    public override void Execute()
    {
        // Self-heal: copy the bundled executables from the add-in payload if they aren't in place yet.
        bool ready = ClickHelperRegistration.EnsureInstalled();

        var res = ClickHelperRegistration.Register();

        var body = string.Join("\n", res.Messages);
        var head = res.Updated > 0
            ? "Set up Transom's Claude UI-Assist and registered it with Claude.\n\n" +
              "Restart Claude Desktop / Claude Code to pick it up. Claude can then drive the Revit UI " +
              "commands that have no API (Edit Group, Finish, dialogs, Properties edits) — used only when " +
              "you ask it to.\n\n"
            : !ready
                ? "Couldn't install the UI-Assist components — the bundled executables " +
                  "(Transom.ClickHelper.Mcp.exe / Transom.ClickHelper.exe) weren't found in the add-in " +
                  "folder. Reinstall Transom, or report this.\n\n"
                : res.Errors > 0
                    ? "Set up finished with issues:\n\n"
                    : "Already set up — nothing to change.\n\n";

        var dlg = new TaskDialog("Transom — Claude UI Assist")
        {
            MainInstruction = head.TrimEnd(),
            MainContent = body + $"\n\nInstalled to: {ClickHelperRegistration.McpServerPath}",
        };
        dlg.Show();
    }
}
