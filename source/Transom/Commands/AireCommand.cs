using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Nice3point.Revit.Toolkit.External;
using Transom.Views;

namespace Transom.Commands;

/// <summary>
///     Opens AIRE (AI Render Enhancer) as a modeless window owned by Revit's main window — same singleton
///     pattern as the Schedule Hub. AIRE is model-independent (it enhances image files through the OpenAI
///     API), so the button is always available, documents open or not, and the window never blocks Revit.
/// </summary>
[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public class AireCommand : ExternalCommand
{
    public override void Execute()
    {
        if (AireView.Instance != null)
        {
            AireView.Instance.Activate();
            return;
        }

        var view = new AireView();
        // Native owner only (z-order/minimize with Revit); startup location stays CenterScreen — the
        // same WindowInteropHelper-vs-Window.Owner trap StartupCommand documents.
        new WindowInteropHelper(view) { Owner = Application.MainWindowHandle };
        view.Show();
    }
}
