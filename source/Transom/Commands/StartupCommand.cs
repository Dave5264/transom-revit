using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Nice3point.Revit.Toolkit.External;
using Transom.ViewModels;
using Transom.Views;

namespace Transom.Commands;

/// <summary>
///     Opens the Transom Export/Import dialog, modal and owned to the Revit main window.
/// </summary>
[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public class StartupCommand : ExternalCommand
{
    public override void Execute()
    {
        var uiDoc = Application.ActiveUIDocument;
        var active = uiDoc.ActiveView as ViewSchedule;
        var viewModel = new TransomViewModel(uiDoc.Document, active);
        var view = new TransomView(viewModel);
        new WindowInteropHelper(view) { Owner = Application.MainWindowHandle };
        view.ShowDialog();
    }
}
