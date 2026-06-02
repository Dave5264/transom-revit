using System.Collections.Generic;
using System.Linq;
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
///     Revit UI keep responding; model work is dispatched through <see cref="ExternalEvent"/>s.
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
        var doc = uiDoc.Document;
        var active = uiDoc.ActiveView as ViewSchedule;

        var projects = new List<string>();
        foreach (Document d in Application.Application.Documents)
            if (!d.IsLinked && !d.IsFamilyDocument)
                projects.Add(d.Title);

        var schedules = DocUtil.UserSchedules(doc);

        var exportHandler = new ExportEventHandler();
        var exportEvent = Autodesk.Revit.UI.ExternalEvent.Create(exportHandler);
        var importHandler = new ImportEventHandler();
        var importEvent = Autodesk.Revit.UI.ExternalEvent.Create(importHandler);
        var loadHandler = new ScheduleLoadEventHandler();
        var loadEvent = Autodesk.Revit.UI.ExternalEvent.Create(loadHandler);

        var viewModel = new TransomViewModel(
            projects, doc.Title, active?.Id.Value ?? 0, schedules,
            exportEvent, exportHandler, importEvent, importHandler, loadEvent, loadHandler);
        var view = new TransomView(viewModel);
        new WindowInteropHelper(view) { Owner = Application.MainWindowHandle };
        view.Show();
    }
}
