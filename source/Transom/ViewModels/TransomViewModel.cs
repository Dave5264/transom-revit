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
    [ObservableProperty] private bool _copiedLog;
    private string _diagnosticLog = "";
    [ObservableProperty] private bool _produceReport;   // off by default — report only on request
    [ObservableProperty] private bool _hasFrozen;       // any greyed (un-writable) rows in the preview
    [ObservableProperty] private bool _hasAffected;     // any schedule with at least one proposed change

    [ObservableProperty] private bool _claudeAvailable;
    [ObservableProperty] private string _claudeMode = "Off"; // Off | Verify (read-only) | Assist (write)
    [ObservableProperty] private bool _canFinalize;
    [ObservableProperty] private int _bridgePort = 48884;
    [ObservableProperty] private string _exchangeFolder = "";
    [ObservableProperty] private string _bridgeStatus = "Checking bridge…";
    [ObservableProperty] private bool _encouragingMessages = true;

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
            AffectedSchedules.Clear();
            HasAffected = false;
            _lastChangeSet = null;
        });
        _importHandler.OnAppliedLog = log => _ui.Invoke(() => _diagnosticLog = log);
        _importHandler.OnError = s => _ui.Invoke(() => ImportStatus = "Error: " + s);
        _exportHandler.OnStaged = p => _ui.Invoke(() => { _stagedPath = p; CanFinalize = true; });
        _scheduleLoadHandler.OnLoaded = (activeId, scheds) => _ui.Invoke(() => SetSchedules(activeId, scheds));

        _settings = TransomSettings.Load();
        BridgePort = _settings.BridgePort;
        ExchangeFolder = _settings.ExchangeFolder;
        EncouragingMessages = _settings.EncouragingMessages;
        _ = RefreshBridgeAsync();

        _copyResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        _copyResetTimer.Tick += (_, _) => { Copied = false; CopiedImport = false; CopiedLog = false; _copyResetTimer.Stop(); };

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
    public ObservableCollection<SheetSummary> AffectedSchedules { get; } = new();
    public ObservableCollection<UnparseableFix> Fixes { get; } = new();

    /// <summary>Set by the view: shows a modal resolver for one type-param conflict, returns the chosen value (or null = skip).</summary>
    public Func<TypeConflict, ConflictOption?>? ConflictResolver;

    /// <summary>Set by the view: shows the per-parameter group-conflict picker for ONE blue/yellow column,
    /// returns the chosen <see cref="GroupResolution"/> (or null to cancel the whole import).</summary>
    public Func<GroupResolutionPrompt, GroupResolution?>? GroupConflictResolver;

    /// <summary>Set by the view: tells the user built-in group edits were staged for Claude-assist. Arg = staged path.</summary>
    public Action<string>? ClaudeStagedNotice;

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

    // Operate on the full list, not just the filtered view, so a selection hidden by the filter is also set/cleared.
    [RelayCommand]
    private void SelectAllSchedules()
    {
        foreach (var e in _allOther) e.IsChecked = true;
    }

    [RelayCommand]
    private void SelectNoneSchedules()
    {
        foreach (var e in _allOther) e.IsChecked = false;
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
        _exportHandler.ClaudeAssistEnabled = ClaudeMode.StartsWith("Assist");
        _finalDestination = dlg.FileName;
        CanFinalize = false;
        Status = stage ? $"Staging {ids.Count} schedule(s)…" : $"Exporting {ids.Count} schedule(s)…";
        _exportEvent.Raise();
        MaybeEncourage();
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
        AffectedSchedules.Clear();
        HasAffected = false;
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
        _importHandler.ProduceReport = ProduceReport;
        // Carry any typed-in corrections for previously-unparseable cells into this re-preview.
        _importHandler.Corrections = Fixes
            .Where(f => !string.IsNullOrWhiteSpace(f.NewValue))
            .Select(f => new CellCorrection
            {
                SheetTabName = f.SheetTabName, ExcelRow = f.ExcelRow, ExcelCol = f.ExcelCol, NewValue = f.NewValue.Trim(),
            })
            .ToList();
        ImportStatus = "Analysing…";
        _importEvent.Raise();
        MaybeEncourage();
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

        // Clear any stale resolutions from a prior Apply — these ProposedChange objects are shared with the
        // cached change-set, so a cancelled or retried Apply must never route a column by a previous choice.
        foreach (var c in selected) c.Resolution = null;

        var notes = new List<string>();
        bool assist = ClaudeMode.StartsWith("Assist");
        var eligible = _lastChangeSet?.Option2EligibleParams ?? new HashSet<string>();

        var directChanges = selected.Where(c => c.GroupMode == GroupMode.None).ToList();
        var groupChanges = selected.Where(c => c.GroupMode is GroupMode.ProjectVary or GroupMode.BuiltinDance).ToList();

        var varyChanges = new List<ProposedChange>();      // option 1 — Transom enables vary + writes per instance
        var newParamChanges = new List<ProposedChange>();  // option 2 — Importer creates a new type param
        var stagedChanges = new List<ProposedChange>();    // option 3/4 — staged for Claude (dance / UI-assist)
        bool wantClickHelper = false;

        // ONE picker per distinct blue/yellow column (parameter); the user chooses a resolution path for each.
        foreach (var grp in groupChanges.GroupBy(c => (c.ParameterId, c.Field)))
        {
            var list = grp.ToList();
            bool isBuiltin = list[0].GroupMode == GroupMode.BuiltinDance;
            var prompt = new GroupResolutionPrompt
            {
                Field = grp.Key.Field,
                ParameterId = grp.Key.ParameterId,
                IsBuiltin = isBuiltin,
                Option2Available = eligible.Contains(Core.ChangeSet.ColumnKey(grp.Key.ParameterId, grp.Key.Field)),
                AssistEnabled = assist,
                Changes = list,
            };

            var choice = GroupConflictResolver?.Invoke(prompt);
            if (GroupConflictResolver != null && choice == null)   // user cancelled the whole import
            {
                ImportStatus = "Import cancelled — no changes applied.";
                return;
            }

            switch (choice ?? GroupResolution.Skip)
            {
                case GroupResolution.Vary:
                    foreach (var c in list) c.Resolution = GroupResolution.Vary;
                    varyChanges.AddRange(list);
                    break;
                case GroupResolution.NewTypeParam:
                    foreach (var c in list) c.Resolution = GroupResolution.NewTypeParam;
                    newParamChanges.AddRange(list);
                    break;
                case GroupResolution.GroupDance:
                    foreach (var c in list) c.Resolution = GroupResolution.GroupDance;
                    stagedChanges.AddRange(list);
                    if (assist) wantClickHelper = true;
                    break;
                case GroupResolution.ClaudeAssist:
                    foreach (var c in list) c.Resolution = GroupResolution.ClaudeAssist;
                    stagedChanges.AddRange(list);
                    wantClickHelper = true;
                    break;
                default: // Skip
                    notes.Add($"{InstanceCountOf(list)} edit(s) to “{grp.Key.Field}” skipped");
                    break;
            }
        }

        // Stage the dance / Claude-assist edits to JSON for Claude; bring up ClickHelper when requested.
        if (stagedChanges.Count > 0)
        {
            var path = ChooseArtifactPath();
            if (path == null) notes.Add($"{InstanceCountOf(stagedChanges)} group edit(s) not staged (no file chosen)");
            else
            {
                var staged = StageGroupEdits(stagedChanges, path);
                if (staged != null)
                {
                    notes.Add($"{InstanceCountOf(stagedChanges)} group edit(s) staged for Claude");
                    if (wantClickHelper) notes.Add(EnsureClickHelper());
                    ClaudeStagedNotice?.Invoke(staged);
                }
                else notes.Add($"{InstanceCountOf(stagedChanges)} group edit(s) could not be staged");
            }
        }

        // Direct + vary + new-type-param edits all go to the Importer; it routes each by Resolution/GroupMode.
        var toApplyList = directChanges.Concat(varyChanges).Concat(newParamChanges).ToList();
        string groupNote = notes.Count > 0 ? string.Join("  ·  ", notes.Where(n => n.Length > 0)) : "";
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
            ImportedScheduleNames = _lastChangeSet?.ImportedScheduleNames ?? new List<string>(),
        };
        toApply.Changes.AddRange(toApplyList);

        _importHandler.RequestedMode = ImportEventHandler.Mode.Apply;
        _importHandler.PendingChangeSet = toApply;
        _importHandler.DocTitle = SelectedProject;
        ImportStatus = $"Applying {toApplyList.Count} selected change(s)…";
        _importEvent.Raise();
        MaybeEncourage();
    }

    /// <summary>Best-effort install + register of the Click Helper MCP so Claude has the UI tools for the
    /// Claude-assist / group-dance paths. (For the data side, the Claude Bridge must also be ON via the ribbon.)</summary>
    private static string EnsureClickHelper()
    {
        try
        {
            Core.ClickHelperRegistration.EnsureInstalled();
            var res = Core.ClickHelperRegistration.Register();
            return res.Updated > 0
                ? "ClickHelper registered with Claude (restart Claude, ensure the Claude Bridge is ON)"
                : "ClickHelper already set up (ensure the Claude Bridge is ON)";
        }
        catch { return "ClickHelper setup skipped (couldn't register — set it up via the ribbon)"; }
    }

    /// <summary>Total element writes represented by a set of changes (bulk changes count each instance).</summary>
    private static int InstanceCountOf(List<ProposedChange> list) => list.Sum(g => g.BulkInstanceIds?.Count ?? 1);

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

            // Each entry: one BUILT-IN parameter set to one value (uniform) across member elements that live in
            // instances of one group type. memberUniqueIds are the actual member elements (e.g. the bath door in
            // each unit instance) — all share the same target value.
            var edits = grouped.Select(g => new
            {
                field = g.Field,
                group = g.GroupName,
                elementName = g.ElementName,
                parameterId = g.ParameterId,
                value = g.NewValue,
                memberUniqueIds = g.BulkInstanceIds ?? new List<string> { g.UniqueId },
            }).ToArray();

            var payload = new
            {
                tool = "Transom",
                kind = "group-edits",
                schedule = _lastChangeSet?.ScheduleName ?? "",
                project = SelectedProject,
                note = "These are parameter edits on elements inside Revit MODEL GROUPS that the user chose to " +
                       "apply via Claude. parameterId >= 0 = PROJECT/SHARED params: writable per group instance " +
                       "after enabling 'vary by group instance', or via the bridge set_parameter (which handles " +
                       "group members directly). parameterId < 0 = BUILT-IN params: these CANNOT vary per instance, " +
                       "so a direct write is rejected ('Changes to groups are allowed only in group edit mode') — " +
                       "do NOT use set_parameter; change them UNIFORMLY in the group DEFINITION via the safe " +
                       "definition-swap procedure in 'Transom - Apply staged edits with Claude.md' in this same " +
                       "folder (the dance: rebuild type -> repoint all instances -> delete old -> rename, with the " +
                       "attached-detail/nested/excluded-group guards, conflict handling, and verification). Use the " +
                       "Transom UI-Assist (ClickHelper) tools to open groups when needed, and verify every write.",
                edits,
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch { return null; }
    }

    [RelayCommand]
    private void SelectAll() { foreach (var c in Changes) if (c.Selectable) c.Selected = true; RefreshChanges(); }

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
        _diagnosticLog = cs.DiagnosticLog;
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

        AffectedSchedules.Clear();
        foreach (var s in cs.SheetSummaries.Where(s => s.Changes > 0))
            AffectedSchedules.Add(s);
        HasAffected = AffectedSchedules.Count > 0;

        HasFrozen = Changes.Any(c => c.Frozen);
        int frozen = Changes.Count(c => c.Frozen);
        int applyable = Changes.Count - frozen;

        ReportPath = cs.ReportPath ?? "";
        int red = cs.Diagnostics.Count(d => d.Severity == "red");
        int yellow = cs.Diagnostics.Count(d => d.Severity == "yellow");
        ImportStatus = $"{applyable} change(s), {Skipped.Count} skipped"
                       + (frozen > 0 ? $", {frozen} frozen" : "")
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
    private void CopyDiagnosticLog()
    {
        try
        {
            System.Windows.Clipboard.SetText(string.IsNullOrEmpty(_diagnosticLog)
                ? "No import diagnostic yet — click Preview first."
                : _diagnosticLog);
            CopiedLog = true;
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

    partial void OnEncouragingMessagesChanged(bool value)
    {
        _settings.EncouragingMessages = value;
        _settings.Save();
    }

    /// <summary>Occasionally shows a cheerful message after an action (when the setting is on).</summary>
    private void MaybeEncourage()
    {
        if (!EncouragingMessages) return;
        var msg = Encouragement.Maybe();
        if (msg != null)
            try { TaskDialog.Show("Transom", msg); } catch { /* never let a pep talk break anything */ }
    }

    /// <summary>Writes a how-to markdown into the exchange folder explaining how to apply the staged group-edits
    /// artifact with Claude. Enabled only once an exchange folder is chosen.</summary>
    [RelayCommand(CanExecute = nameof(CanWriteClaudeGuide))]
    private void WriteClaudeGuide()
    {
        try
        {
            Directory.CreateDirectory(ExchangeFolder);
            var path = Path.Combine(ExchangeFolder, "Transom - Apply staged edits with Claude.md");
            File.WriteAllText(path, ClaudeGuideMarkdown());
            TaskDialog.Show("Transom", "Instructions written to:\n\n" + path);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Transom", "Couldn't write the instructions:\n\n" + ex.Message);
        }
    }

    private bool CanWriteClaudeGuide() => !string.IsNullOrWhiteSpace(ExchangeFolder);

    private string ClaudeGuideMarkdown() => @"# Transom — Applying staged BUILT-IN group edits with Claude

This file sits in your **Claude exchange folder**. When you import with **Claude Assist**, Transom stages
**built-in parameter edits on elements inside Revit MODEL GROUPS** into a group-edits JSON in this **same
folder**. Built-in params (Comments, Mark, Level, …) **cannot vary per group instance**, so they must be
changed **uniformly in the group DEFINITION**. These steps are for Claude to follow.

> Project parameters on grouped elements are NOT here — Transom applies those itself (it enables ""vary
> by group instance"" and writes them). This file is only the built-in edits, which need the swap below.

## What you need
A way to run Revit API code on the open model (e.g. the Revit MCP `execute_revit_code`). The Transom
write-bridge's `set_parameter` will **not** work here — a direct write to a group member is rejected with
*""Changes to groups are allowed only in group edit mode.""* You must use the definition-swap below.

## 1. Find the staging file
In **this same folder**, find the file that parses as JSON with top-level `""tool"":""Transom""` and
`""kind"":""group-edits""` (default name `transom_group_edits.json`; match by **content, not name**). This
`.md` is not it. If several exist, use the most recent or ask the user.

## 2. Confirm the model
The open Revit document must match the JSON's `project`/`schedule`. **If not, STOP and tell the user.**

## 3. Group the edits and check for conflicts
Each entry = one `parameterId` (negative = built-in) set to one `value` across `memberUniqueIds` (the
member elements, one per group instance). Resolve each member's group type:
`el = doc.GetElement(uid); groupTypeId = doc.GetElement(el.GroupId).GetTypeId()`. Group the entries by
group type.

**Conflict check (before changing anything):** within one group type, if two entries target the same
member role with **different** values, they can't both hold while grouped (a built-in can't differ across
instances of one type). Do **not** guess — report it and ask the user whether to ungroup those instances
or pick one value.

## 4. The safe definition-swap (per group type)
Run it all in ONE transaction with a FailuresPreprocessor (DeleteWarning + Continue) AND a
DialogBoxShowing handler (OverrideResult to dismiss) so nothing blocks:

a. **Safety guard.** Inspect one instance of the type. If it has attached detail groups
   (`GetAvailableAttachedDetailGroupTypeIds` / `GetShownAttachedDetailGroupTypeIds` non-empty), nested
   groups (a member that is itself a Model Group), or excluded members — **SKIP this type** and report
   ""manual edit needed"". The swap can silently lose these.
b. Pick one instance **A**. Record A's member ids, then `A.UngroupMembers()`.
c. For each staged entry for this type, find the member among A's freed ids (its uid is in the entry's
   `memberUniqueIds`) and set the parameter to `value` (pass unit text verbatim; parse doubles via
   `UnitFormatUtils`). `doc.Regenerate()`.
d. `doc.Create.NewGroup(freedIds)` → newGroup (a new group type).
e. For **every other** instance of the original type: `group.ChangeTypeId(newGroup.GetTypeId())`.
   Use **ChangeTypeId**, NOT the `GroupType` property setter (the setter pops a modal dialog).
f. Delete the original (now-unused) group type, then rename newGroup's type back to the original name.
g. **Verify:** read the parameter on members across several instances — every one must equal `value`.
   If verification fails for a type, roll back that type's work and report it; never leave a half state.

## 5. Report
Per group type: applied / skipped (with reason) / conflicts. Note this changed the group **definition**,
so all instances now share the new value — **durable** (unlike a per-instance override, which Revit
silently drops on the next type change, reload, or sync).

## After applying
Review in Revit; if **workshared**, **Synchronize with Central**; delete the JSON when done.
";

    partial void OnExchangeFolderChanged(string value)
    {
        _settings.ExchangeFolder = value;
        _settings.Save();
        WriteClaudeGuideCommand.NotifyCanExecuteChanged();
    }
}
