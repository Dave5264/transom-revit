using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Transom.Core;

namespace Transom.ViewModels;

public sealed partial class ScheduleEntry : ObservableObject
{
    public ScheduleEntry(ViewSchedule vs, bool isActive)
    {
        Id = vs.Id.Value;
        Name = vs.Name;
        IsActive = isActive;
    }

    public long Id { get; }
    public string Name { get; }
    public bool IsActive { get; }
    public Action? CheckedChanged;

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => CheckedChanged?.Invoke();
}

public sealed partial class TransomViewModel : ObservableObject
{
    private readonly ExternalEvent _exportEvent;
    private readonly ExportEventHandler _exportHandler;
    private readonly ExternalEvent _importEvent;
    private readonly ImportEventHandler _importHandler;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _copyResetTimer;
    private readonly List<ScheduleEntry> _allOther; // non-active schedules
    private readonly TransomSettings _settings;
    private ChangeSet? _lastChangeSet;
    private string _stagedPath = "";
    private string _finalDestination = "";

    [ObservableProperty] private string _status = "Pick schedules and export.";
    [ObservableProperty] private bool _copied;
    [ObservableProperty] private string _scheduleFilter = "";
    [ObservableProperty] private string _selectionInfo = "";

    [ObservableProperty] private string _workbookPath = "";
    [ObservableProperty] private string _importStatus = "Choose a Transom workbook to import.";

    [ObservableProperty] private bool _claudeAvailable;
    [ObservableProperty] private bool _claudeAssistExport;
    [ObservableProperty] private bool _claudeAssistImport;
    [ObservableProperty] private bool _canFinalize;
    [ObservableProperty] private int _bridgePort = 48884;
    [ObservableProperty] private string _exchangeFolder = "";
    [ObservableProperty] private string _bridgeStatus = "Checking bridge…";

    public TransomViewModel(Document doc, ViewSchedule? active,
        ExternalEvent exportEvent, ExportEventHandler exportHandler,
        ExternalEvent importEvent, ImportEventHandler importHandler)
    {
        _exportEvent = exportEvent;
        _exportHandler = exportHandler;
        _importEvent = importEvent;
        _importHandler = importHandler;

        _exportHandler.ReportStatus = s => _ui.Invoke(() => Status = s);
        _importHandler.OnPreview = cs => _ui.BeginInvoke(() => ShowPreview(cs));
        _importHandler.OnApplied = s => _ui.Invoke(() =>
        {
            ImportStatus = s;
            Changes.Clear();
            Skipped.Clear();
            _lastChangeSet = null;
        });
        _importHandler.OnError = s => _ui.Invoke(() => ImportStatus = "Error: " + s);

        _settings = TransomSettings.Load();
        BridgePort = _settings.BridgePort;
        ExchangeFolder = _settings.ExchangeFolder;
        _exportHandler.OnStaged = p => _ui.Invoke(() => { _stagedPath = p; CanFinalize = true; });
        _ = RefreshBridgeAsync();

        _copyResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        _copyResetTimer.Tick += (_, _) => { Copied = false; _copyResetTimer.Stop(); };

        var schedules = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .Where(v => !v.IsTemplate && !v.IsTitleblockRevisionSchedule)
            .OrderBy(v => v.Name)
            .ToList();

        ActiveSchedule = active != null ? new ScheduleEntry(active, true) : null;
        _allOther = schedules
            .Where(v => active == null || v.Id.Value != active.Id.Value)
            .Select(v => new ScheduleEntry(v, false))
            .ToList();
        foreach (var e in _allOther) e.CheckedChanged = UpdateSelectionInfo;

        ApplyFilter();
        UpdateSelectionInfo();
    }

    public ScheduleEntry? ActiveSchedule { get; }
    public bool HasActive => ActiveSchedule != null;
    public ObservableCollection<ScheduleEntry> FilteredSchedules { get; } = new();
    public ObservableCollection<ProposedChange> Changes { get; } = new();
    public ObservableCollection<SkippedItem> Skipped { get; } = new();

    /// <summary>Set by the view: shows a modal resolver for one type-param conflict, returns the chosen value (or null = skip).</summary>
    public Func<TypeConflict, ConflictOption?>? ConflictResolver;

    // --- Export ---

    partial void OnScheduleFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredSchedules.Clear();
        foreach (var e in _allOther)
            if (string.IsNullOrEmpty(ScheduleFilter) ||
                e.Name.Contains(ScheduleFilter, StringComparison.OrdinalIgnoreCase))
                FilteredSchedules.Add(e);
    }

    private void UpdateSelectionInfo()
    {
        int total = _allOther.Count + (HasActive ? 1 : 0);
        int sel = _allOther.Count(e => e.IsChecked) + (HasActive ? 1 : 0);
        SelectionInfo = $"{sel} of {total} selected";
    }

    [RelayCommand]
    private void SelectAllSchedules()
    {
        foreach (var e in FilteredSchedules) e.IsChecked = true;
    }

    [RelayCommand]
    private void Export()
    {
        var ids = new List<long>();
        if (ActiveSchedule != null) ids.Add(ActiveSchedule.Id);
        ids.AddRange(_allOther.Where(e => e.IsChecked).Select(e => e.Id));
        if (ids.Count == 0)
        {
            Status = "Select at least one schedule.";
            return;
        }

        var defaultName = ActiveSchedule?.Name
                          ?? _allOther.First(e => e.IsChecked).Name;
        var dlg = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx|Excel 97-2003 (*.xls)|*.xls|CSV — display only (*.csv)|*.csv",
            FileName = defaultName + ".xlsx",
        };
        if (dlg.ShowDialog() != true) return;

        bool stage = ClaudeAssistExport && ClaudeAvailable && !string.IsNullOrWhiteSpace(ExchangeFolder);
        _exportHandler.ScheduleIds = ids;
        _exportHandler.OutputPath = dlg.FileName;
        _exportHandler.Stage = stage;
        _exportHandler.ExchangeFolder = ExchangeFolder;
        _finalDestination = dlg.FileName;
        CanFinalize = false;
        Status = stage ? $"Staging {ids.Count} schedule(s)…" : $"Exporting {ids.Count} schedule(s)…";
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
        _importHandler.WriteRunLog = ClaudeAssistImport && ClaudeAvailable;
        _importHandler.ExchangeFolder = ExchangeFolder;
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
            Skipped = _lastChangeSet?.Skipped ?? new List<SkippedItem>(),
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

        // Resolve type-parameter conflicts one at a time (Revit-style), letting the user pick a value.
        foreach (var conflict in cs.Conflicts)
        {
            var opt = ConflictResolver?.Invoke(conflict);
            if (opt != null)
                Changes.Add(Importer.ResolveToChange(conflict, opt));
            else
                Skipped.Add(new SkippedItem { Reason = "conflict — unresolved", Detail = $"{conflict.Field} on '{conflict.TypeName}'" });
        }

        ImportStatus = $"{Changes.Count} change(s), {Skipped.Count} skipped"
                       + (cs.Conflicts.Count > 0 ? $", {cs.Conflicts.Count} conflict(s) reviewed" : "")
                       + (cs.CrossModel ? "  — ⚠ different source model" : "");
    }

    // --- Claude-assist ---

    [RelayCommand]
    private async Task RefreshBridge() => await RefreshBridgeAsync();

    private async Task RefreshBridgeAsync()
    {
        BridgeStatus = "Checking bridge…";
        var ok = await BridgeProbe.IsAvailableAsync(BridgePort);
        _ui.Invoke(() =>
        {
            ClaudeAvailable = ok;
            BridgeStatus = ok
                ? $"Claude bridge: available (port {BridgePort})"
                : $"Claude bridge: offline (port {BridgePort})";
            if (!ok) { ClaudeAssistExport = false; ClaudeAssistImport = false; }
        });
    }

    [RelayCommand]
    private void FinalizeExport()
    {
        if (string.IsNullOrEmpty(_stagedPath) || string.IsNullOrEmpty(_finalDestination)) return;
        try
        {
            File.Copy(_stagedPath, _finalDestination, true);
            Status = $"Finalized to {_finalDestination}";
            CanFinalize = false;
            _stagedPath = "";
        }
        catch (Exception ex)
        {
            Status = "Finalize failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ChooseExchangeFolder()
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true) ExchangeFolder = dlg.FolderName;
    }

    partial void OnBridgePortChanged(int value)
    {
        _settings.BridgePort = value;
        _settings.Save();
        _ = RefreshBridgeAsync();
    }

    partial void OnExchangeFolderChanged(string value)
    {
        _settings.ExchangeFolder = value;
        _settings.Save();
    }
}
