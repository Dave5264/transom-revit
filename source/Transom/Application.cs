using Nice3point.Revit.Toolkit.External;
using Transom.Commands;

namespace Transom;

/// <summary>
///     Transom add-in entry point. Adds the ribbon button that opens the Export/Import dialog.
/// </summary>
[UsedImplicitly]
public class Application : ExternalApplication
{
    public override void OnStartup()
    {
        CreateRibbon();

        // Seamless MCP setup: copy the bundled self-contained shim to the per-user location and register it
        // with the user's Claude clients once (Option A — see install/SEAMLESS_INSTALL.md). Run off the UI
        // thread so Revit startup is never delayed by the file copy; the helper never throws.
        System.Threading.Tasks.Task.Run(Core.McpRegistration.EnsureBundledShimAndAutoRegister);
    }

    private void CreateRibbon()
    {
        // NOTE: final placement is the built-in Add-Ins tab per SPEC; confirm the tab target
        // when first loaded in Revit (milestone 1). Using a named panel for now.
        var panel = Application.CreatePanel("Schedule Tools", "Transom");

        panel.AddPushButton<StartupCommand>("Schedule\nHub")
            .SetImage("/Transom;component/Resources/Icons/RibbonIcon16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/RibbonIcon32.png")
            .SetToolTip("Export schedules to spreadsheets with full fidelity, and import edits back into the model.");

        panel.AddSeparator();

        panel.AddPushButton<UpdateCommand>("Check for\nUpdates")
            .SetImage("/Transom;component/Resources/Icons/Update16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/Update32.png")
            .SetToolTip("Check GitHub for a newer Transom release and install it (no admin required).");

        panel.AddPushButton<HelpCommand>("Help")
            .SetImage("/Transom;component/Resources/Icons/Help16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/Help32.png")
            .SetToolTip("Transom help & support — how to use it, report an issue, and documentation.");

        // Claude Assist — its own ribbon group. #107: two buttons — "Set up Claude" (one-click registration of
        // BOTH MCP servers) and the runtime "Claude Bridge" toggle — plus Settings. The action buttons grey out
        // when the Claude app isn't running (Revit re-checks availability on UI context changes); Settings stays
        // enabled so you can configure Claude mode / bridge port before Claude is up.
        var claudePanel = Application.CreatePanel("Claude Assist", "Transom");

        var claudeAvail = typeof(ClaudeAvailability).FullName;

        // #107: ONE "Set up Claude" button replaces the separate "Register with Claude" + "Claude UI Assist"
        // buttons — it registers BOTH the data bridge (transom) AND UI-Assist (transom-ui-assist) in one click
        // and shows the restart notice once. (Old RegisterBridgeCommand / UiAssistSetupCommand are no longer
        // on the ribbon; SetupClaudeCommand calls the same registration paths.)
        var setupBtn = claudePanel.AddPushButton<SetupClaudeCommand>("Set up\nClaude");
        setupBtn.SetImage("/Transom;component/Resources/Icons/Register16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/Register32.png")
            .SetToolTip("Set up Claude (one-time, per-user, no admin): registers Transom's bundled MCP servers with " +
                        "Claude Desktop / Claude Code — the data bridge (read schedules, write parameters incl. group " +
                        "members) AND UI-Assist (let Claude drive Revit UI commands with no API, e.g. Edit Group). " +
                        "Run once after install, or after changing the bridge port. Restart Claude afterward.  Claude must be running to use this.");
        setupBtn.AvailabilityClassName = claudeAvail;

        var bridgeBtn = claudePanel.AddPushButton<BridgeToggleCommand>("Claude\nBridge");
        bridgeBtn.SetImage("/Transom;component/Resources/Icons/Bridge16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/Bridge32.png")
            .SetToolTip("Start/stop the admin-free Claude-assist bridge (loopback HTTP on 127.0.0.1) so Claude can read schedules and write parameters back — including group members.  Claude must be running to use this.");
        bridgeBtn.AvailabilityClassName = claudeAvail;

        claudePanel.AddPushButton<SettingsCommand>("Settings")
            .SetImage("/Transom;component/Resources/Icons/Settings16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/Settings32.png")
            .SetToolTip("Open Transom Settings — the Settings tab of Schedule Hub (Claude mode, bridge port, exchange folder, and more). Always available.");

        // Revision Tools — its own ribbon group.
        var revisionPanel = Application.CreatePanel("Revision Tools", "Transom");
        revisionPanel.AddPushButton<RevisionNarrativeCommand>("Revision\nNarrative")
            .SetImage("/Transom;component/Resources/Icons/RevisionNarrative16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/RevisionNarrative32.png")
            .SetToolTip("Generate a Revision Narrative (.docx) from a selected revision's clouds — reads each cloud's Comments, groups by discipline and sheet, numbers the items per sheet, and writes the firm letterhead narrative (start from a previous narrative to keep header/footer/fonts). Stand-alone; no Claude required.");
    }
}
