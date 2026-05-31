using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Transom.Core;

namespace Transom.ViewModels;

public sealed class ScheduleEntry
{
    public ScheduleEntry(ViewSchedule vs)
    {
        Id = vs.Id.Value;
        Name = vs.Name;
    }

    public long Id { get; }
    public string Name { get; }
}

public sealed partial class TransomViewModel : ObservableObject
{
    private readonly ExternalEvent _exportEvent;
    private readonly ExportEventHandler _exportHandler;
    private readonly ExternalEvent _importEvent;
    private readonly ImportEventHandler _importHandler;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _copyResetTimer;
    private ChangeSet? _lastChangeSet;

    [ObservableProperty] private ScheduleEntry? _selectedSchedule;
    [ObservableProperty] private string _status = "Pick a schedule and export.";
    [ObservableProperty] private bool _copied;

    [ObservableProperty] private string _workbookPath = "";
    [ObservableProperty] private string _importStatus = "Choose a Transom workbook to import.";

    public TransomViewModel(Document doc, ViewSchedule? active,
        ExternalEvent exportEvent, ExportEventHandler exportHandler,
        ExternalEvent importEvent, ImportEventHandler importHandler)
    {
        _exportEvent = exportEvent;
        _exportHandler = exportHandler;
        _importEvent = importEvent;
        _importHandler = importHandler;

        _exportHandler.ReportStatus = s => _ui.Invoke(() => Status = s);
        _importHandler.OnPreview = cs => _ui.Invoke(() => ShowPreview(cs));
        _importHandler.OnApplied = s => _ui.Invoke(() =>
        {
            ImportStatus = s;
            Changes.Clear();
            Skipped.Clear();
            _lastChangeSet = null;
        });
        _importHandler.OnError = s => _ui.Invoke(() => ImportStatus = "Error: " + s);

        _copyResetTimer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1.4) };
        _copyResetTimer.Tick += (_, _) =>
        {
            Copied = false;
            _copyResetTimer.Stop();
        };

        Schedules = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .Where(v => !v.IsTemplate && !v.IsTitleblockRevisionSchedule)
            .OrderBy(v => v.Name)
            .Select(v => new ScheduleEntry(v))
            .ToList();

        SelectedSchedule = active != null
            ? Schedules.FirstOrDefault(e => e.Id == active.Id.Value) ?? Schedules.FirstOrDefault()
            : Schedules.FirstOrDefault();
    }

    public List<ScheduleEntry> Schedules { get; }
    public ObservableCollection<ProposedChange> Changes { get; } = new();
    public ObservableCollection<SkippedItem> Skipped { get; } = new();

    // --- Export ---

    [RelayCommand]
    private void Export()
    {
        if (SelectedSchedule == null)
        {
            Status = "No schedule selected.";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx|Excel 97-2003 (*.xls)|*.xls|CSV — display only (*.csv)|*.csv",
            FileName = SelectedSchedule.Name + ".xlsx",
        };
        if (dlg.ShowDialog() != true) return;

        _exportHandler.ScheduleId = SelectedSchedule.Id;
        _exportHandler.OutputPath = dlg.FileName;
        Status = "Exporting…";
        _exportEvent.Raise();
    }

    [RelayCommand]
    private void CopyStatus()
    {
        try
        {
            System.Windows.Clipboard.SetText(Status ?? string.Empty);
            Copied = true;
            _copyResetTimer.Stop();
            _copyResetTimer.Start();
        }
        catch { /* clipboard busy */ }
    }

    // --- Import ---

    [RelayCommand]
    private void ChooseWorkbook()
    {
        var dlg = new OpenFileDialog { Filter = "Transom Workbook (*.xlsx;*.xls)|*.xlsx;*.xls" };
        if (dlg.ShowDialog() != true) return;
        WorkbookPath = dlg.FileName;
        ImportStatus = "Ready — click Preview.";
        Changes.Clear();
        Skipped.Clear();
        _lastChangeSet = null;
    }

    [RelayCommand]
    private void Preview()
    {
        if (string.IsNullOrEmpty(WorkbookPath))
        {
            ImportStatus = "Choose a workbook first.";
            return;
        }
        _importHandler.RequestedMode = ImportEventHandler.Mode.Preview;
        _importHandler.WorkbookPath = WorkbookPath;
        ImportStatus = "Analyzing…";
        _importEvent.Raise();
    }

    [RelayCommand]
    private void Apply()
    {
        var selected = Changes.Where(c => c.Selected).ToList();
        if (selected.Count == 0)
        {
            ImportStatus = "Nothing selected to apply.";
            return;
        }
        var toApply = new ChangeSet
        {
            ScheduleName = _lastChangeSet?.ScheduleName ?? "",
            Skipped = _lastChangeSet?.Skipped ?? new System.Collections.Generic.List<SkippedItem>(),
        };
        toApply.Changes.AddRange(selected);

        _importHandler.RequestedMode = ImportEventHandler.Mode.Apply;
        _importHandler.PendingChangeSet = toApply;
        ImportStatus = $"Applying {selected.Count} selected change(s)…";
        _importEvent.Raise();
    }

    [RelayCommand]
    private void SelectAll() { foreach (var c in Changes) c.Selected = true; RefreshChanges(); }

    [RelayCommand]
    private void SelectNone() { foreach (var c in Changes) c.Selected = false; RefreshChanges(); }

    private void RefreshChanges()
    {
        var snapshot = Changes.ToList();
        Changes.Clear();
        foreach (var c in snapshot) Changes.Add(c);
    }

    private void ShowPreview(ChangeSet cs)
    {
        _lastChangeSet = cs;
        Changes.Clear();
        Skipped.Clear();
        foreach (var c in cs.Changes) Changes.Add(c);
        foreach (var s in cs.Skipped) Skipped.Add(s);

        ImportStatus = $"{cs.Changes.Count} change(s), {cs.Skipped.Count} skipped"
                       + (cs.CrossModel ? "  — ⚠ different source model" : "");
    }
}
