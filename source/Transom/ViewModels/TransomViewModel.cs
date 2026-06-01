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
    public ScheduleEntry(long id, string name, bool isActive)
    {
        Id = id;
        Name = name;
        IsActive = isActive;
    }

    public long Id { get; }
    public string Name { get; }
    public bool IsActive { get; }
    public Action? CheckedChanged;

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => CheckedChanged?.Invoke();
}

/// <summary>One unparseable import cell, with an input box for a corrected value.</summary>
public sealed partial class UnparseableFix : ObservableObject
{
    public string SheetTabName = "";
    public int ExcelRow;
    public int ExcelCol;

    // These are bound in XAML, so they must be properties (WPF {Binding} ignores public fields).
    public string FieldName { get; set; } = "";
    public string ElementLabel { get; set; } = "";
    public string BadValue { get; set; } = "";

    /// <summary>Set when the entry parsed but isn't in the schedule's format — the canonical value to confirm.</summary>
    public string Suggested { get; set; } = "";
    public bool HasSuggestion => !string.IsNullOrEmpty(Suggested);

    [ObservableProperty] private string _newValue = "";
}

public sealed partial class TransomViewModel : ObservableObject
{
    private readonly ExternalEvent _exportEvent;
    private readonly ExportEventHandler _exportHandler;
    private readonly ExternalEvent _importEvent;
    private readonly ImportEventHandler _importHandler;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _copyResetTimer;
    private readonly ExternalEvent _scheduleLoadEvent;
    private readonly ScheduleLoadEventHandler _scheduleLoadHandler;
    private List<ScheduleEntry> _allOther = new(); // non-active schedules
    private readonly TransomSettings _settings;
    private bool _initialized;
    private ChangeSet? _lastChangeSet;
    private string _stagedPath = "";
    private string _finalDestination = "";
    private string _pendingGroupNote = "";

    [ObservableProperty] private string _status = "Pick schedules and export.";
    [ObservableProperty] private bool _copied;
    [ObservableProperty] private string _scheduleFilter = "";
    [ObservableProperty] private string _selectionInfo = "";
    [ObservableProperty] private string _selectedProject = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActive))]
    private ScheduleEntry? _activeSchedule;

    [ObservableProperty] private string _workbookPath = "";
    [ObservableProperty] private string _importStatus = "Choose a Transom workbook to import.";
    [ObservableProperty] private string _reportPath = "";
    [ObservableProperty] private bool _copiedImport;

    [ObservableProperty] private bool _claudeAvailable;
    [ObservableProperty] private string _claudeMode = "Off"; // Off | Verify (read-only) | Assist (write)
    [ObservableProperty] private bool _canFinalize;
    [ObservableProperty] private int _bridgePort = 48884;
    [ObservableProperty] private string _exchangeFolder = "";
    [ObservableProperty] private string _bridgeStatus = "Checking bridge…";

    public TransomViewModel(
        List<string> projects, string activeProjectTitle,
        long activeScheduleId, List<(long id, string name)> schedules,
        ExternalEvent exportEvent, ExportEventHandler exportHandler,
        ExternalEvent importEvent, ImportEventHandler importHandler,
        ExternalEvent scheduleLoadEvent, ScheduleLoadEventHandler scheduleLoadHandler)
    {
        _exportEvent = exportEvent;
        _exportHandler = exportHandler;
        _importEvent = importEvent;
        _importHandler = importHandler;
        _scheduleLoadEvent = scheduleLoadEvent;
        _scheduleLoadHandler = scheduleLoadHandler;

        _exportHandler.ReportStatus = s => _ui.Invoke(() => Status = s);
        _importHandler.OnPreview = cs => _ui.BeginInvoke(() => ShowPreview(cs));
        _importHandler.OnApplied = s => _ui.Invoke(() =>
        {
            ImportStatus = s + _pendingGroupNote;
            _pendingGroupNote = "";
            Changes.Clear();
            Skipped.Clear();
            Fixes.Clear();
            _lastChangeSet = null;
        });
        _importHandler.OnError = s => _ui.Invoke(() => ImportStatus = "Error: " + s);
        _exportHandler.OnStaged = p => _ui.Invoke(() => { _stagedPath = p; CanFinalize = true; });
        _scheduleLoadHandler.OnLoaded = (activeId, scheds) => _ui.Invoke(() => SetSchedules(activeId, scheds));

        _settings = TransomSettings.Load();
        BridgePort = _settings.BridgePort;
        ExchangeFolder = _settings.ExchangeFolder;
        _ = RefreshBridgeAsync();

        _copyResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        _copyResetTimer.Tick += (_, _) => { Copied = false; CopiedImport = false; _copyResetTimer.Stop(); };

        foreach (var p in projects) Projects.Add(p);
        _selectedProject = activeProjectTitle; // backing field: don't trigger a reload during construction
        SetSchedules(activeScheduleId, schedules);
        _initialized = true;
    }

    private void SetSchedules(long activeId, List<(long id, string name)> schedules)
    {
        var activeName = schedules.FirstOrDefault(s => s.id == activeId).name;
        ActiveSchedule = activeId != 0 && activeName != null
            ? new ScheduleEntry(activeId, activeName, true) : null;
        _allOther = schedules.Where(s => s.id != activeId)
            .Select(s => new ScheduleEntry(s.id, s.name, false)).ToList();
        foreach (var e in _allOther) e.CheckedChanged = UpdateSelectionInfo;
        ApplyFilter();
        UpdateSelectionInfo();
    }

    partial void OnSelectedProjectChanged(string value)
    {
        if (!_initialized) return;
        Status = $"Loading schedules for {value}…";
        _scheduleLoadHandler.DocTitle = value;
        _scheduleLoadEvent.Raise();
    }

    public bool HasActive => ActiveSchedule != null;
    public ObservableCollection<string> Projects { get; } = new();
    public string[] ClaudeModes { get; } = { "Off", "Verify (read-only)", "Assist (write)" };
    public ObservableCollection<ScheduleEntry> FilteredSchedules { get; } = new();
    public ObservableCollection<ProposedChange> Changes { get; } = new();
    public ObservableCollection<SkippedItem> Skipped { get; } = new();
    public ObservableCollection<UnparseableFix> Fixes { get; } = new();

    /// <summary>Set by the view: shows a modal resolver for one type-param conflict, returns the chosen value (or null = skip).</summary>
    public Func<TypeConflict, ConflictOption?>? ConflictResolver;

    /// <summary>Set by the view: asks how to handle edits that target group members (skip / abort / hand to Claude).</summary>
    public Func<GroupPrompt, GroupDecision>? GroupResolver;

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

        bool stage = ClaudeMode != "Off" && !string.IsNullOrWhiteSpace(ExchangeFolder);
        _exportHandler.ScheduleIds = ids;
        _exportHandler.OutputPath = dlg.FileName;
        _exportHandler.DocTitle = SelectedProject;
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
        Fixes.Clear();
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
        _importHandler.DocTitle = SelectedProject;
        _importHandler.WriteRunLog = ClaudeMode != "Off";
        _importHandler.ExchangeFolder = ExchangeFolder;
        // Carry any typed-in corrections for previously-unparseable cells into this re-preview.
        _importHandler.Corrections = Fixes
            .Where(f => !string.IsNullOrWhiteSpace(f.NewValue))
            .Select(f => new CellCorrection
            {
                SheetTabName = f.SheetTabName, ExcelRow = f.ExcelRow, ExcelCol = f.ExcelCol, NewValue = f.NewValue.Trim(),
            })
            .ToList();
        ImportStatus = "Analyzing…";
        _importEvent.Raise();
    }

    [RelayCommand]
    private void ConfirmFix(UnparseableFix? fix)
    {
        if (fix == null || string.IsNullOrEmpty(fix.Suggested)) return;
        fix.NewValue = fix.Suggested;   // accept the reformatted value
        Preview();                      // re-validate; it now matches the expected format and applies
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

        // Edits that land on group members can't be written in the normal import — ask how to handle them.
        var grouped = selected.Where(c => c.InGroup).ToList();
        string groupNote = "";
        if (grouped.Count > 0)
        {
            var prompt = new GroupPrompt { Grouped = grouped, AssistEnabled = ClaudeMode.StartsWith("Assist") };
            var decision = GroupResolver?.Invoke(prompt) ?? GroupDecision.SkipGrouped;
            if (decision == GroupDecision.Abort)
            {
                ImportStatus = $"Cancelled — {prompt.InstanceCount} edit(s) target elements in group(s).";
                return;
            }
            if (decision == GroupDecision.ClaudeHandle)
            {
                var path = ChooseArtifactPath();
                if (path == null)
                {
                    groupNote = $"{prompt.InstanceCount} grouped edit(s) not staged (no file chosen)";
                }
                else
                {
                    var staged = StageGroupEdits(grouped, path);
                    groupNote = staged != null
                        ? $"{prompt.InstanceCount} grouped edit(s) staged for Claude → {staged}"
                        : $"{prompt.InstanceCount} grouped edit(s) could not be staged";
                }
            }
            else
            {
                groupNote = $"{prompt.InstanceCount} grouped edit(s) skipped";
            }
        }

        var toApplyList = selected.Where(c => !c.InGroup).ToList();
        if (toApplyList.Count == 0)
        {
            ImportStatus = groupNote.Length > 0 ? groupNote : "Nothing to apply.";
            Changes.Clear();
            Skipped.Clear();
            _lastChangeSet = null;
            return;
        }

        _pendingGroupNote = groupNote.Length > 0 ? "  ·  " + groupNote : "";
        var toApply = new ChangeSet
        {
            ScheduleName = _lastChangeSet?.ScheduleName ?? "",
            Skipped = _lastChangeSet?.Skipped ?? new List<SkippedItem>(),
        };
        toApply.Changes.AddRange(toApplyList);

        _importHandler.RequestedMode = ImportEventHandler.Mode.Apply;
        _importHandler.PendingChangeSet = toApply;
        _importHandler.DocTitle = SelectedProject;
        ImportStatus = $"Applying {toApplyList.Count} selected change(s)…";
        _importEvent.Raise();
    }

    /// <summary>Prompts the user for where to save the Claude group-edits artifact (defaults to the exchange folder).</summary>
    private string? ChooseArtifactPath()
    {
        var dir = !string.IsNullOrWhiteSpace(ExchangeFolder) ? ExchangeFolder
            : Path.GetDirectoryName(WorkbookPath) ?? "";
        var dlg = new SaveFileDialog
        {
            Title = "Save Claude group-edits artifact",
            Filter = "JSON (*.json)|*.json",
            FileName = "transom_group_edits.json",
            InitialDirectory = Directory.Exists(dir) ? dir : "",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>Writes the group-blocked edits to a JSON file Claude can act on (open groups + apply over the write bridge).</summary>
    private string? StageGroupEdits(List<ProposedChange> grouped, string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var edits = grouped.Select(g => new
            {
                field = g.Field,
                group = g.GroupName,
                elementName = g.ElementName,
                parameterId = g.ParameterId,
                isString = g.IsString,
                valueString = g.NewString,
                valueDouble = g.NewDouble,
                newDisplay = g.NewValue,
                instanceUniqueIds = g.BulkInstanceIds ?? new List<string> { g.UniqueId },
            }).ToArray();

            var payload = new
            {
                tool = "Transom",
                kind = "group-edits",
                schedule = _lastChangeSet?.ScheduleName ?? "",
                project = SelectedProject,
                note = "Elements are inside Revit groups. For each entry, set the parameter (by parameterId; " +
                       "negative ids are BuiltInParameter) on each listed instance UniqueId. Group members can't be " +
                       "edited in place — ungroup the affected instances (or use group-edit mode), apply, then restore grouping.",
                edits,
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch { return null; }
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

        // Rebuild the fix pane, preserving any value the user already typed. Reformat suggestions (parses but
        // wrong format → confirm) take precedence over the matching unparseable diagnostic for the same cell.
        var prior = Fixes.ToDictionary(f => (f.SheetTabName, f.ExcelRow, f.ExcelCol), f => f.NewValue);
        Fixes.Clear();
        var seen = new HashSet<(string, int, int)>();
        foreach (var rf in cs.Reformats)
        {
            var key = (rf.SheetTabName, rf.ExcelRow, rf.ExcelCol);
            seen.Add(key);
            Fixes.Add(new UnparseableFix
            {
                SheetTabName = rf.SheetTabName, ExcelRow = rf.ExcelRow, ExcelCol = rf.ExcelCol,
                FieldName = rf.FieldName, ElementLabel = rf.ElementLabel, BadValue = rf.Entered,
                Suggested = rf.Canonical,
                NewValue = prior.TryGetValue(key, out var pv) ? pv : rf.Entered,
            });
        }
        foreach (var d in cs.Diagnostics.Where(d => d.Reason == "value can't be parsed"))
        {
            var key = (d.SheetTabName, d.ExcelRow, d.Col);
            if (!seen.Add(key)) continue;
            Fixes.Add(new UnparseableFix
            {
                SheetTabName = d.SheetTabName, ExcelRow = d.ExcelRow, ExcelCol = d.Col,
                FieldName = d.FieldName, ElementLabel = d.ElementLabel, BadValue = d.Value,
                NewValue = prior.TryGetValue(key, out var v) ? v : "",
            });
        }

        ReportPath = cs.ReportPath ?? "";
        int red = cs.Diagnostics.Count(d => d.Severity == "red");
        int yellow = cs.Diagnostics.Count(d => d.Severity == "yellow");
        ImportStatus = $"{Changes.Count} change(s), {Skipped.Count} skipped"
                       + (cs.Conflicts.Count > 0 ? $", {cs.Conflicts.Count} conflict(s) reviewed" : "")
                       + (red + yellow > 0 ? $"  —  {red} can't-write · {yellow} drift (see report)" : "")
                       + (Fixes.Count > 0 ? $"  ·  {Fixes.Count} fixable below" : "")
                       + (cs.CrossModel ? "  — ⚠ different source model" : "");
    }

    [RelayCommand]
    private void CopyImportStatus()
    {
        try
        {
            System.Windows.Clipboard.SetText(ImportStatus ?? string.Empty);
            CopiedImport = true;
            _copyResetTimer.Stop();
            _copyResetTimer.Start();
        }
        catch { /* clipboard busy */ }
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (string.IsNullOrEmpty(ReportPath)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReportPath) { UseShellExecute = true }); }
        catch { /* nothing to open */ }
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
                ? $"Write bridge: available (port {BridgePort}) — Assist enabled"
                : $"Write bridge: offline (port {BridgePort}) — Verify (read-only) still works";
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
