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

        // The bridge's ExternalEvent can only be created in an API context — OnStartup is the one moment we
        // reliably have one, so BridgeRuntime builds its handler/event/server here even if Claude Assist is
        // off (cheap; nothing listens until Start). The Settings toggle (modeless WPF) then only Start/Stops.
        Core.BridgeRuntime.Initialize();

        // Seamless MCP setup: copy the bundled self-contained shim to the per-user location and register it
        // with the user's Claude clients once (Option A — see install/SEAMLESS_INSTALL.md). Run off the UI
        // thread so Revit startup is never delayed by the file copy; the helper never throws.
        System.Threading.Tasks.Task.Run(Core.McpRegistration.EnsureBundledShimAndAutoRegister);

        // Claude Assist is a persisted toggle: when it's ON, the bridge must come up with Revit or the
        // Settings panel would show a dead bridge after every restart. Silent, loopback-only, token-gated.
        var settings = Core.TransomSettings.Load();
        if (settings.ClaudeAssistEnabled)
            Core.BridgeRuntime.Start(settings.BridgeSelfHostPort);
    }

    private void CreateRibbon()
    {
        // One combined "Transom Tools" group (was "Schedule Tools" + a separate "Revision Tools",
        // merged 2026-07-12 per user request): the two tools first, utilities after the separator.
        var panel = Application.CreatePanel("Transom Tools", "Transom");

        panel.AddPushButton<StartupCommand>("Schedule\nHub")
            .SetImage("/Transom;component/Resources/Icons/RibbonIcon16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/RibbonIcon32.png")
            .SetToolTip("Export schedules to spreadsheets with full fidelity, and import edits back into the model.");

        panel.AddPushButton<RevisionNarrativeCommand>("Revision\nNarrative")
            .SetImage("/Transom;component/Resources/Icons/RevisionNarrative16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/RevisionNarrative32.png")
            .SetToolTip("Generate a Revision Narrative (.docx) from a selected revision's clouds — reads each cloud's Comments, groups by discipline and sheet, numbers the items per sheet, and writes the firm letterhead narrative (start from a previous narrative to keep header/footer/fonts). Stand-alone; no Claude required.");

        panel.AddSeparator();

        panel.AddPushButton<UpdateCommand>("Check for\nUpdates")
            .SetImage("/Transom;component/Resources/Icons/Update16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/Update32.png")
            .SetToolTip("Check GitHub for a newer Transom release and install it (no admin required).");

        panel.AddPushButton<HelpCommand>("Help")
            .SetImage("/Transom;component/Resources/Icons/Help16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/Help32.png")
            .SetToolTip("Transom help & support — how to use it, report an issue, and documentation.");

        // Claude consolidation: the old "Claude Assist" ribbon group (Set up Claude / Claude Bridge / Bridge
        // Status) is retired — setup, the on/off switch, and the status checklist all live on the Settings tab
        // now (see docs/design-notes/claude-ui-consolidation.md). Settings is the one remaining entry point.
        panel.AddPushButton<SettingsCommand>("Settings")
            .SetImage("/Transom;component/Resources/Icons/Settings16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/Settings32.png")
            .SetToolTip("Open Transom Settings — turn Claude Assist on/off and see its status, plus the exchange folder, bridge port, and preferences. Always available.");
    }
}
