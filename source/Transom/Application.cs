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

        panel.AddSeparator();

        panel.AddPushButton<BridgeToggleCommand>("Claude\nBridge")
            .SetImage("/Transom;component/Resources/Icons/Bridge16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/Bridge32.png")
            .SetToolTip("Start/stop the admin-free Claude-assist bridge (loopback HTTP on 127.0.0.1) so Claude can read schedules and write parameters back — including group members.");

        panel.AddPushButton<RegisterBridgeCommand>("Register\nwith Claude")
            .SetImage("/Transom;component/Resources/Icons/Register16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/Register32.png")
            .SetToolTip("Register Transom's bundled MCP bridge with Claude Desktop / Claude Code (per-user, no admin). Run once after install, or after changing the bridge port.");
    }
}
