using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Nice3point.Revit.Toolkit.External;
using Transom.Core;
using Transom.ViewModels;
using Transom.Views;

namespace Transom.Commands;

/// <summary>
///     Opens the Transom Export/Import dialog as a modeless window owned by the Revit main window.
///     Modeless keeps Revit's main thread free so the dialog can stay open while the MCP bridge and
///     Revit UI keep responding; model work is dispatched through an <see cref="ExternalEvent"/>.
/// </summary>
[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public class StartupCommand : ExternalCommand
{
    public override void Execute()
    {
        if (TransomView.Instance != null)
        {
            TransomView.Instance.Activate();
            return;
        }

        var uiDoc = Application.ActiveUIDocument;
        var active = uiDoc.ActiveView as ViewSchedule;

        var exportHandler = new ExportEventHandler();
        var exportEvent = Autodesk.Revit.UI.ExternalEvent.Create(exportHandler);
        var importHandler = new ImportEventHandler();
        var importEvent = Autodesk.Revit.UI.ExternalEvent.Create(importHandler);

        var viewModel = new TransomViewModel(uiDoc.Document, active,
            exportEvent, exportHandler, importEvent, importHandler);
        var view = new TransomView(viewModel);
        new WindowInteropHelper(view) { Owner = Application.MainWindowHandle };
        view.Show();
    }
}
