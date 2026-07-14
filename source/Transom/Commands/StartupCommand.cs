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
    public override void Execute() => OpenOrActivate(Application);

    /// <summary>Opens Schedule Hub (or activates the existing instance) and returns the window. Shared with
    /// <see cref="SettingsCommand"/> so the Settings button reuses the exact same window.</summary>
    internal static TransomView OpenOrActivate(Autodesk.Revit.UI.UIApplication app)
    {
        // Re-read the LIVE document state so a re-invoke after a doc close/reopen rebinds the Hub
        // instead of showing the previous document's stale schedule list / filter (code3 fix).
        var (projects, doc, activeScheduleId, schedules) = GatherDocumentState(app);

        if (TransomView.Instance != null)
        {
            // Existing window: rebind to the current document (rebuild projects, reload schedules,
            // clear stale filter), then focus it.
            if (TransomView.Instance.DataContext is TransomViewModel existingVm)
                existingVm.RefreshFromDocument(projects, doc?.Title ?? "", activeScheduleId, schedules);
            TransomView.Instance.Activate();
            return TransomView.Instance;
        }

        var exportHandler = new ExportEventHandler();
        var exportEvent = Autodesk.Revit.UI.ExternalEvent.Create(exportHandler);
        var importHandler = new ImportEventHandler();
        var importEvent = Autodesk.Revit.UI.ExternalEvent.Create(importHandler);
        var loadHandler = new ScheduleLoadEventHandler();
        var loadEvent = Autodesk.Revit.UI.ExternalEvent.Create(loadHandler);

        var viewModel = new TransomViewModel(
            projects, doc?.Title ?? "", activeScheduleId, schedules,
            exportEvent, exportHandler, importEvent, importHandler, loadEvent, loadHandler);
        var view = new TransomView(viewModel);
        new WindowInteropHelper(view) { Owner = app.MainWindowHandle };
        view.Show();
        // Zero documents (Settings is always-available): Export/Import are disabled, so land on the one
        // tab that works instead of a greyed Export page.
        if (projects.Count == 0) view.SelectSettingsTab();
        return view;
    }

    /// <summary>Rebinds an already-open Hub to the current document state — called from the document
    /// opened/created/closed events (Application.RefreshOpenHub) so the window tracks documents live while
    /// it sits open (the tabs enable/disable via TransomViewModel.HasProject). No-op when the Hub is closed.</summary>
    internal static void RefreshOpenHub(Autodesk.Revit.UI.UIApplication app)
    {
        if (TransomView.Instance?.DataContext is not TransomViewModel vm) return;
        var (projects, doc, activeScheduleId, schedules) = GatherDocumentState(app);
        vm.RefreshFromDocument(projects, doc?.Title ?? "", activeScheduleId, schedules);
        if (projects.Count == 0) TransomView.Instance.SelectSettingsTab(); // last doc closed → only live tab
    }

    /// <summary>The Hub's document snapshot: open project titles, the active document (null at zero docs),
    /// the active schedule view id (0 if none), and the active document's user schedules.</summary>
    private static (List<string> projects, Document? doc, long activeScheduleId, List<(long id, string name)> schedules)
        GatherDocumentState(Autodesk.Revit.UI.UIApplication app)
    {
        var uiDoc = app.ActiveUIDocument;
        var doc = uiDoc?.Document;

        var projects = new List<string>();
        if (app.Application?.Documents != null)
            foreach (Document d in app.Application.Documents)
                if (!d.IsLinked && !d.IsFamilyDocument)
                    projects.Add(d.Title);

        var active = uiDoc?.ActiveView as ViewSchedule;
        var schedules = doc != null ? DocUtil.UserSchedules(doc) : new List<(long id, string name)>();
        return (projects, doc, active?.Id.Value ?? 0, schedules);
    }
}
