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
        // WindowInteropHelper.Owner sets the native owner HWND (so the Hub stays above Revit and minimises
        // with it) but NOT WPF's Window.Owner — and WindowStartupLocation="CenterOwner" is implemented
        // against Window.Owner. With it null WPF silently fell back to centring on the PRIMARY screen, so
        // on a multi-monitor setup the Hub opened on a different display from Revit. Centre it on Revit's
        // window explicitly.
        CenterOnRevit(view, app.MainWindowHandle);
        view.Show();
        // Zero documents (Settings is always-available): Export/Import are disabled, so land on the one
        // tab that works instead of a greyed Export page.
        if (projects.Count == 0) view.SelectSettingsTab();
        return view;
    }

    /// <summary>
    ///     Centres <paramref name="view"/> on Revit's main window and clamps it to that monitor's WORK
    ///     AREA, so it can't open partly off-screen (the Hub asks for 880 DIP, which exceeds the usable
    ///     height of a 1080p display at 150% scaling). Startup position only — the user can move/resize
    ///     afterwards. Best-effort: any failure leaves WPF's own placement in charge.
    /// </summary>
    private static void CenterOnRevit(System.Windows.Window view, IntPtr revitHwnd)
    {
        try
        {
            if (revitHwnd == IntPtr.Zero) return;
            var mon = MonitorFromWindow(revitHwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return;

            // Work area in physical pixels → DIP. 96 DPI per WPF's unit definition; the system scale is
            // what Revit's own window is laid out against, which is the monitor we're centring on.
            double scale = 1.0;
            try
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(view);
                if (dpi.DpiScaleX > 0) scale = dpi.DpiScaleX;
            }
            catch { /* keep 1.0 */ }

            double waLeft = mi.rcWork.Left / scale, waTop = mi.rcWork.Top / scale;
            double waW = (mi.rcWork.Right - mi.rcWork.Left) / scale;
            double waH = (mi.rcWork.Bottom - mi.rcWork.Top) / scale;

            // Clamp BEFORE centring: the Hub asks for 880 DIP, taller than a 1080p display's work area at
            // 150% scaling, and an oversized centred window puts its footer (and Close button) off-screen.
            if (view.Width > waW) view.Width = waW;
            if (view.Height > waH) view.Height = waH;

            view.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            view.Left = waLeft + (waW - view.Width) / 2;
            view.Top = waTop + (waH - view.Height) / 2;
        }
        catch { /* fall back to WPF's own placement */ }
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

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
        Document? firstProject = null;
        if (app.Application?.Documents != null)
            foreach (Document d in app.Application.Documents)
                if (!d.IsLinked && !d.IsFamilyDocument)
                {
                    projects.Add(d.Title);
                    firstProject ??= d;
                }

        // DocumentCreated/Opened fire BEFORE the new document becomes active, so ActiveUIDocument can be
        // null (or stale) while a project is already open — fall back to the first open project so the
        // Hub binds a real document (dropdown + schedules) instead of enabling tabs against nothing.
        doc ??= firstProject;

        var active = uiDoc?.ActiveView as ViewSchedule;
        var schedules = doc != null ? DocUtil.UserSchedules(doc) : new List<(long id, string name)>();
        return (projects, doc, active?.Id.Value ?? 0, schedules);
    }
}
