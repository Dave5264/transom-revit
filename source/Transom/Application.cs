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
    }

    private void CreateRibbon()
    {
        // NOTE: final placement is the built-in Add-Ins tab per SPEC; confirm the tab target
        // when first loaded in Revit (milestone 1). Using a named panel for now.
        var panel = Application.CreatePanel("Schedule Tools", "Transom");

        panel.AddPushButton<StartupCommand>("Transom")
            .SetImage("/Transom;component/Resources/Icons/RibbonIcon16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/RibbonIcon32.png")
            .SetToolTip("Export schedules to spreadsheets with full fidelity, and import edits back into the model.");

        panel.AddSeparator();

        panel.AddPushButton<UpdateCommand>("Check for\nUpdates")
            .SetImage("/Transom;component/Resources/Icons/RibbonIcon16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/RibbonIcon32.png")
            .SetToolTip("Check GitHub for a newer Transom release and install it (no admin required).");

        panel.AddPushButton<HelpCommand>("Help")
            .SetImage("/Transom;component/Resources/Icons/RibbonIcon16.png")
            .SetLargeImage("/Transom;component/Resources/Icons/RibbonIcon32.png")
            .SetToolTip("Transom help & support — how to use it, report an issue, and documentation.");
    }
}
