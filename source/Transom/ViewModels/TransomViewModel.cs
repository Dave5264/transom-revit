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

/// <summary>CHANGE 2 (§9.5): a read-only dependent row shown indented under its driving parent in the selection
/// step. Purely informational (no checkbox, no apply action) — it just names a schedule that inherits the parent's
/// type/instance edits.</summary>
public sealed class DependentRow
{
    public DependentRow(string display) => Display = display;
    public string Display { get; }
}

/// <summary>§16 pre-analysis tab picker: one selectable schedule/tab the user can include in (or exclude from) the
/// analysis. IsChecked defaults true (analyze all); the user unticks the tabs they don't care about so only the rest
/// are diffed. Carries the SheetTab (the scoping key passed to the importer) and the schedule name shown in the list.</summary>
public sealed partial class PickTabRow : ObservableObject
{
    public PickTabRow(string scheduleName, string sheetTab, string uid)
    {
        ScheduleName = scheduleName;
        SheetTab = sheetTab;
        Uid = uid;
    }

    public string ScheduleName { get; }
    public string SheetTab { get; }
    public string Uid { get; }
    [ObservableProperty] private bool _isChecked = true;
}

/// <summary>
///     One row in the import preview's "Schedules this import will change" list. Wraps a <see cref="SheetSummary"/>
///     and exposes a tri-state <see cref="SelectionState"/> that DRIVES the underlying <see cref="ProposedChange.Selected"/>
///     flags (the single source of truth Apply already filters on). checked = all this schedule's changes selected,
///     unchecked = none, indeterminate (null) = some (the user hand-picked cells). A user click cycles checked↔unchecked;
///     the indeterminate state is display-only, reflecting per-cell edits in the changes grid (UX_SPEC §4a/§5 C-3).
/// </summary>
public sealed partial class AffectedScheduleRow : ObservableObject
{
    private readonly SheetSummary _summary;
    private readonly Func<SheetSummary, IReadOnlyList<ProposedChange>> _changesFor;
    private readonly Action _afterToggle;

    public AffectedScheduleRow(SheetSummary summary,
        Func<SheetSummary, IReadOnlyList<ProposedChange>> changesFor, Action afterToggle,
        IEnumerable<DependentScheduleRef>? dependents = null)
    {
        _summary = summary;
        _changesFor = changesFor;
        _afterToggle = afterToggle;
        if (dependents != null)
            foreach (var d in dependents)
                Dependents.Add(new DependentRow($"↳ also affects: {d.ScheduleName}"));
        // Default: expand short dependent lists, collapse long ones (§9.5 — N=5).
        _isExpanded = Dependents.Count is > 0 and <= 5;
    }

    public string Display => _summary.Display;
    public string ScheduleName => _summary.ScheduleName;
    public string ScheduleUid => _summary.ScheduleUid;

    /// <summary>CHANGE 2 (§9.5): this parent's read-only dependent ("inherited-change") schedules, shown indented.
    /// Never selectable, never an apply target — purely informational.</summary>
    public ObservableCollection<DependentRow> Dependents { get; } = new();
    public bool HasDependents => Dependents.Count > 0;

    /// <summary>§9.5 collapse/expand for long dependent trees (default expanded ≤5). Display-only.</summary>
    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowDependents));

    /// <summary>§9.5: the dependent subtree shows only when this parent has dependents, is EXPANDED, AND is still
    /// selected (SelectionState != false). Deselecting the parent drops its subtree; a dependent survives elsewhere
    /// because it appears under EVERY driving parent (each owns its own copy), so another selected parent keeps it.</summary>
    public bool ShowDependents => HasDependents && IsExpanded && SelectionState != false;

    /// <summary>Tri-state: true = all selectable changes selected, false = none, null = mixed. GET is computed from
    /// the changes; SET (from the checkbox) selects/deselects all this schedule's SELECTABLE changes (a null set from
    /// the UI is ignored — indeterminate is never user-chosen).</summary>
    public bool? SelectionState
    {
        get
        {
            // Only SELECTABLE (non-frozen) changes participate — a frozen change is never applied, so it must not
            // drag the tri-state to "mixed". A schedule with no selectable changes reads as unchecked.
            var selectable = _changesFor(_summary).Where(c => c.Selectable).ToList();
            if (selectable.Count == 0) return false;
            bool anySelected = selectable.Any(c => c.Selected);
            bool anyUnselected = selectable.Any(c => !c.Selected);
            if (anySelected && anyUnselected) return null;   // mixed → indeterminate
            return anySelected;                               // all selected (true) or none (false)
        }
        set
        {
            if (value == null) return;            // indeterminate is display-only; never set from the UI
            foreach (var c in _changesFor(_summary))
                if (c.Selectable) c.Selected = value.Value;   // raises ProposedChange.SelectionChanged per change
            _afterToggle();                       // refresh grid + recompute counts/other rows
        }
    }

    /// <summary>Re-raise PropertyChanged on the computed tri-state (called when a per-cell edit may have changed it).
    /// Also re-evaluates ShowDependents — deselecting the parent (SelectionState→false) must drop its subtree (§9.5).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(SelectionState));
        OnPropertyChanged(nameof(ShowDependents));
    }
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
    /// <summary>Set while a per-schedule bulk toggle is driving many <see cref="ProposedChange.Selected"/> sets, so the
    /// per-change SelectionChanged handler doesn't re-run the row/helper refresh once per change (the bulk op refreshes
    /// once at the end via OnAffectedSelectionChanged). A single per-CELL edit leaves this false → handler runs.</summary>
    private bool _bulkSelecting;

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
            HasMultipleAffected = false;
            SomeAffectedDeselected = false;
            SkipLogScopedToSelection = false;
            InSelectStep = false;
            _lastChangeSet = null;
        });
        _importHandler.OnAppliedLog = log => _ui.Invoke(() => _diagnosticLog = log);
        _importHandler.OnError = s => _ui.Invoke(() => ImportStatus = "Error: " + s);
        _exportHandler.OnStaged = p => _ui.Invoke(() => { _stagedPath = p; CanFinalize = true; });
        _scheduleLoadHandler.OnLoaded = (activeId, scheds) => _ui.Invoke(() => SetSchedules(activeId, scheds));

        // Cross-update (UX_SPEC §5 C-3): when a per-CELL checkbox in the changes grid toggles a change's Selected,
        // re-evaluate the per-schedule tri-state + helper line so they stay consistent. Skipped during a bulk
        // per-schedule toggle (which refreshes once at the end) to avoid a refresh-per-change storm.
        ProposedChange.SelectionChanged += OnAnyChangeSelectionChanged;

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

    /// <summary>
    ///     Rebinds the Hub to the (possibly new) active document — used when the Schedule Hub button is
    ///     pressed again after a document close/reopen. Rebuilds the project list, reloads the schedule
    ///     list for the active document, and clears any stale filter so the fresh list isn't hidden.
    ///     (code3 fix for the Hub doc-rebind / stale-filter defect.)
    /// </summary>
    public void RefreshFromDocument(
        List<string> projects, string activeProjectTitle,
        long activeScheduleId, List<(long id, string name)> schedules)
    {
        Projects.Clear();
        foreach (var p in projects) Projects.Add(p);

        _selectedProject = activeProjectTitle; // backing field: skip OnSelectedProjectChanged's async reload
        OnPropertyChanged(nameof(SelectedProject));

        ScheduleFilter = "";                   // clear stale filter so the fresh list isn't hidden
        SetSchedules(activeScheduleId, schedules);
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
    public ObservableCollection<AffectedScheduleRow> AffectedSchedules { get; } = new();
    public ObservableCollection<UnparseableFix> Fixes { get; } = new();

    /// <summary>True when >1 schedule is affected — gates the "Select all · Select none" link pair (a single-schedule
    /// import doesn't need bulk controls). UX_SPEC §4b.</summary>
    [ObservableProperty] private bool _hasMultipleAffected;

    /// <summary>True when at least one affected schedule is fully deselected — shows the "N of M schedules selected…"
    /// helper line so the exclusion is unmistakable. UX_SPEC §4c.</summary>
    [ObservableProperty] private bool _someAffectedDeselected;

    /// <summary>The helper-line text ("2 of 3 schedules selected — unticked schedules won't be imported."). UX_SPEC §4c.</summary>
    [ObservableProperty] private string _affectedSelectionInfo = "";

    /// <summary>CHANGE 2 §3b surface-4: true in SUBSET mode → the skip-log panel shows a subtle "(selected schedules
    /// only)" hint on its section header so the scoped list reads as intentional, not data loss. False (all-selected)
    /// → no hint (global skip-log, unchanged).</summary>
    [ObservableProperty] private bool _skipLogScopedToSelection;

    /// <summary>CHANGE 1 (§8.3): the two-step import sub-state. True = STEP 1 (SELECT tabs) — show the affected-schedule
    /// list + "Resolve &amp; preview selected", hide the changes grid/fix pane/apply, conflicts NOT yet resolved.
    /// False = the normal preview (resolved + changes grid). InPreviewStep is the inverse for binding the grid's
    /// visibility. Only entered when >1 affected tab; a single affected tab skips step 1 (= today).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InPreviewStep))]
    private bool _inSelectStep;

    /// <summary>The normal preview content (changes grid, Apply, fix pane) shows only when NOT in the select step
    /// (§8) AND NOT in the §16 tab-pick step — so every InPreviewStep-gated element auto-hides during the picker.</summary>
    public bool InPreviewStep => !InSelectStep && !InTabPickStep;

    /// <summary>§16 pre-analysis tab picker: true = the PICK phase — show the tab checklist + "Analyze selected", and
    /// HIDE the affected-schedule list / changes grid / fix pane / apply (no analysis has run yet). The cheap name read
    /// (cowork_meta only) populates <see cref="PickableTabs"/>; "Analyze selected" sets <see cref="_selectedSheetTabs"/>
    /// and raises the scoped Preview, leaving this state. A single-tab workbook skips the picker entirely.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InPreviewStep))]
    private bool _inTabPickStep;

    /// <summary>§16: the workbook's schedules to pick from (name + tab + uid + IsChecked, default ALL checked). Only the
    /// ticked tabs are analyzed. Populated cheaply (ExcelReader.ReadSheetNames) before any model diff.</summary>
    public ObservableCollection<PickTabRow> PickableTabs { get; } = new();

    /// <summary>§16: the tabs the user picked to analyze — carried so a corrections re-preview (ConfirmFix → analysis)
    /// re-analyzes the SAME tabs without re-prompting the picker (§16.3). Null = full workbook (single-tab / legacy).</summary>
    private System.Collections.Generic.ISet<string>? _selectedSheetTabs;

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
        HasMultipleAffected = false;
        SomeAffectedDeselected = false;
        SkipLogScopedToSelection = false;
        InSelectStep = false;
        InTabPickStep = false;        // §16: clear the tab picker
        PickableTabs.Clear();
        _selectedSheetTabs = null;
        _lastChangeSet = null;
    }

    /// <summary>§16 PHASE 1 (the Preview button): read ONLY the tab names (cheap, cowork_meta only — no model diff) and
    /// show the tab picker so the user analyzes just the tabs they care about (a ~60-tab full analysis is ~2 min).
    /// A single-tab workbook skips the picker and analyzes directly (consistent with the v2 single-tab auto-select).</summary>
    [RelayCommand]
    private void Preview()
    {
        if (string.IsNullOrEmpty(WorkbookPath))
        {
            ImportStatus = "Choose a workbook first.";
            return;
        }
        System.Collections.Generic.IReadOnlyList<(string scheduleName, string sheetTab, string uid)> names;
        try
        {
            // cowork_meta-only read — tiny, no ReadRows, no Revit API; safe synchronously on the UI thread.
            names = new ExcelReader().ReadSheetNames(WorkbookPath);
        }
        catch (Exception ex)
        {
            ImportStatus = "Couldn't read the workbook: " + ex.Message;
            return;
        }

        // Single tab → no picker; analyze it directly (carry its tab as the scope for consistency + re-preview).
        if (names.Count <= 1)
        {
            _selectedSheetTabs = names.Count == 1
                ? new System.Collections.Generic.HashSet<string> { names[0].sheetTab }
                : null;
            RunAnalysis();
            return;
        }

        // Multi-tab → show the picker (all tabs checked by default; the user unticks what they don't want analyzed).
        PickableTabs.Clear();
        foreach (var n in names) PickableTabs.Add(new PickTabRow(n.scheduleName, n.sheetTab, n.uid));
        InTabPickStep = true;
        ImportStatus = $"Choose which schedule(s) to analyze ({names.Count} in the workbook) — only the ticked tabs are analyzed.";
    }

    /// <summary>§16: tick/untick all tabs in the picker.</summary>
    [RelayCommand]
    private void SelectAllTabs() { foreach (var t in PickableTabs) t.IsChecked = true; }

    [RelayCommand]
    private void SelectNoneTabs() { foreach (var t in PickableTabs) t.IsChecked = false; }

    /// <summary>§16 PHASE 2: the user confirmed the tab picker → set the analysis scope from the ticked tabs and run
    /// the (scoped) analysis. Deselecting every tab is guarded (nothing to analyze).</summary>
    [RelayCommand]
    private void AnalyzeSelected()
    {
        var picked = PickableTabs.Where(t => t.IsChecked).Select(t => t.SheetTab).ToHashSet();
        if (picked.Count == 0)
        {
            ImportStatus = "Tick at least one schedule to analyze.";
            return;
        }
        _selectedSheetTabs = picked;
        InTabPickStep = false;
        RunAnalysis();
    }

    /// <summary>§16 PHASE 2 / re-preview: raise the actual (scoped) analysis on <see cref="_selectedSheetTabs"/>. Used
    /// by AnalyzeSelected, the single-tab fast path, and a corrections re-preview — all reuse the same picked set so the
    /// picker is never shown twice (§16.3).</summary>
    private void RunAnalysis()
    {
        _importHandler.RequestedMode = ImportEventHandler.Mode.Preview;
        _importHandler.WorkbookPath = WorkbookPath;
        _importHandler.DocTitle = SelectedProject;
        _importHandler.WriteRunLog = ClaudeMode != "Off";
        _importHandler.ExchangeFolder = ExchangeFolder;
        _importHandler.ProduceReport = ProduceReport;
        _importHandler.SelectedSheetTabs = _selectedSheetTabs;   // §16: scope the analysis to the picked tabs
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
        RunAnalysis();                  // §16.3: re-analyze the SAME picked tabs (no picker re-prompt); now matches format
    }

    [RelayCommand]
    private void Apply()
    {
        var selected = Changes.Where(c => c.Selected).ToList();

        // Header (column-caption) renames to apply, scoped to the SELECTED schedules (header edits have no per-row
        // checkbox — they ride their schedule's selection). A header edit is included when its schedule is ticked
        // (or there's no per-schedule selection at all, i.e. everything applies).
        var selectedHeaderChanges = SelectedHeaderChanges();

        if (selected.Count == 0 && selectedHeaderChanges.Count == 0)
        {
            // UX_SPEC §4d: name the per-schedule case so an all-deselected import isn't a generic dead-end.
            ImportStatus = AffectedSchedules.Count > 0
                ? "No schedules selected — tick at least one schedule to import."
                : "Nothing selected to apply.";
            return;
        }

        // Clear any stale resolutions from a prior Apply — these ProposedChange objects are shared with the
        // cached change-set, so a cancelled or retried Apply must never route a column by a previous choice.
        foreach (var c in selected) c.Resolution = null;

        var notes = new List<string>();
        bool assist = ClaudeMode.StartsWith("Assist");

        var directChanges = selected.Where(c => c.GroupMode == GroupMode.None).ToList();
        var groupChanges = selected.Where(c => c.GroupMode is GroupMode.ProjectVary or GroupMode.BuiltinDance).ToList();

        var varyChanges = new List<ProposedChange>();      // option 1 — Transom enables vary + writes per instance
        var newParamChanges = new List<ProposedChange>();  // option 2 — Importer creates a new type param
        var stagedChanges = new List<ProposedChange>();    // option 3 — staged for Claude (UI-assist)
        bool wantClickHelper = false;

        // ONE picker per distinct blue/yellow column (parameter); the user chooses a resolution path for each.
        foreach (var grp in groupChanges.GroupBy(c => (c.ParameterId, c.Field)))
        {
            var list = grp.ToList();
            bool isBuiltin = list[0].GroupMode == GroupMode.BuiltinDance;
            // broken = a change whose group trips the HARD dance gate (level-anchored families / rotation /
            // multi-level — set in ComputeGroupBroken from IsDanceGateBroken). Mirror-only / nested-only groups do
            // NOT set GroupBroken, so they still OFFER option 3.
            var broken = list.FirstOrDefault(c => c.GroupBroken);
            // Option-2 binding is inferred at build time (ComputeOption2Mode) and carried per column in
            // Option2Modes; the mode drives which type/instance options the dialog offers and its note.
            var key = Core.ChangeSet.ColumnKey(grp.Key.ParameterId, grp.Key.Field);
            var mode = _lastChangeSet?.Option2Modes.GetValueOrDefault(key, Option2Mode.None) ?? Option2Mode.None;
            var prompt = new GroupResolutionPrompt
            {
                Field = grp.Key.Field,
                ParameterId = grp.Key.ParameterId,
                IsBuiltin = isBuiltin,
                IsBroken = broken != null,
                BrokenReason = broken?.GroupBrokenReason ?? "",
                // Named level-anchored families to re-host to unlock the dance — split back into a list for the
                // dialog's actionable block (ComputeGroupBroken joined them with "; ").
                BrokenFamilies = string.IsNullOrEmpty(broken?.GroupBrokenFamilies)
                    ? new List<string>()
                    : broken!.GroupBrokenFamilies.Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries).ToList(),
                Option2Available = mode != Option2Mode.None,
                Option2Mode = mode,
                BindingNote = BindingNoteFor(mode),
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
                case GroupResolution.NewInstanceParam:
                    foreach (var c in list) c.Resolution = GroupResolution.NewInstanceParam;
                    newParamChanges.AddRange(list);   // same bucket as NewTypeParam — ApplyNewParam branches on the resolution
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

        // Direct + vary + new-type-param edits all go to the Importer event; it applies them in the import transaction.
        var toApplyList = directChanges.Concat(varyChanges).Concat(newParamChanges).ToList();
        string groupNote = notes.Count > 0 ? string.Join("  ·  ", notes.Where(n => n.Length > 0)) : "";
        // Header-only imports are valid: nothing to apply ONLY when there are no data writes AND no header renames.
        if (toApplyList.Count == 0 && selectedHeaderChanges.Count == 0)
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
        toApply.HeaderChanges.AddRange(selectedHeaderChanges);

        _importHandler.RequestedMode = ImportEventHandler.Mode.Apply;
        _importHandler.PendingChangeSet = toApply;
        _importHandler.DocTitle = SelectedProject;
        string hdrNote = selectedHeaderChanges.Count > 0 ? $" + {selectedHeaderChanges.Count} heading rename(s)" : "";
        ImportStatus = $"Applying {toApplyList.Count} selected change(s){hdrNote}…";
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

    /// <summary>The dialog's explanatory note for an ambiguous option-2 binding (empty when there's nothing to disambiguate).</summary>
    private static string BindingNoteFor(Option2Mode mode) => mode switch
    {
        Option2Mode.AmbiguousPreferType =>
            "Recommended: type — this schedule is organized by type, so a type parameter keeps one value per " +
            "type and unifies the variations.",
        Option2Mode.AmbiguousPreferInstance =>
            "Recommended: instance — this schedule itemizes every instance (or isn't grouped by type), so an " +
            "instance parameter preserves each element's own value.",
        _ => "",
    };

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
                       "folder (rebuild type -> repoint all instances -> delete old -> rename, with the " +
                       "attached-detail/nested/excluded-group guards, conflict handling, and verification). Use the " +
                       "Transom UI-Assist (ClickHelper) tools to open groups when needed, and verify every write.",
                edits,
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            // Bundle the how-to instructions next to the JSON (same folder) so Claude always has them
            // alongside the staged edits — the JSON note refers to this file "in this same folder".
            if (!string.IsNullOrEmpty(dir))
            {
                try { File.WriteAllText(Path.Combine(dir, "Transom - Apply staged edits with Claude.md"), ClaudeGuideMarkdown()); }
                catch { /* the JSON is the essential artifact; don't fail staging if the guide can't be written */ }
            }

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

    // --- per-schedule selection in the import preview (UX_SPEC per-schedule-import-selection) ---

    /// <summary>The changes a schedule "owns": uid-first match (rename-safe), name fallback when uid is empty
    /// (cross-model / legacy) — same precedence the importer uses (ResolveSchedule). UX_SPEC §5 C-2.</summary>
    private IReadOnlyList<ProposedChange> ChangesForSchedule(SheetSummary s) =>
        Changes.Where(c => !string.IsNullOrEmpty(s.ScheduleUid)
            ? c.SourceScheduleUid == s.ScheduleUid
            : c.SourceScheduleName == s.ScheduleName).ToList();

    /// <summary>Header (caption) renames to apply, scoped to the SELECTED schedules. Header edits have no per-row
    /// checkbox — each rides its schedule's selection in the affected-schedules list. A schedule counts as selected
    /// when its row's SelectionState != false (or there's no affected-schedule selection UI at all).</summary>
    private List<HeaderChange> SelectedHeaderChanges()
    {
        var all = _lastChangeSet?.HeaderChanges;
        if (all == null || all.Count == 0) return new List<HeaderChange>();

        // The schedules the user kept ticked (by uid, falling back to name). When there are no affected-schedule rows
        // (single-schedule import with no list), nothing is deselected → apply all header changes.
        var selectedRows = AffectedSchedules.Where(r => r.SelectionState != false).ToList();
        if (AffectedSchedules.Count == 0)
            return all.Where(h => h.OutcomeNote != "skipped").ToList();

        var selUids = new HashSet<string>(selectedRows.Select(r => r.ScheduleUid).Where(u => !string.IsNullOrEmpty(u)));
        var selNames = new HashSet<string>(selectedRows.Select(r => r.ScheduleName).Where(n => !string.IsNullOrEmpty(n)));
        return all.Where(h => h.OutcomeNote != "skipped"
            && (selUids.Contains(h.ScheduleUid) || selNames.Contains(h.ScheduleName))).ToList();
    }

    [RelayCommand]
    private void SelectAllAffected() => BulkSetAffected(true);

    [RelayCommand]
    private void SelectNoneAffected() => BulkSetAffected(false);

    private void BulkSetAffected(bool selected)
    {
        _bulkSelecting = true;
        try { foreach (var row in AffectedSchedules) row.SelectionState = selected; }
        finally { _bulkSelecting = false; }
        OnAffectedSelectionChanged();   // single refresh after the bulk set
    }

    /// <summary>Handler for <see cref="ProposedChange.SelectionChanged"/> — a single per-cell edit re-evaluates the
    /// schedule rows + helper line. No-op during a bulk per-schedule toggle (it refreshes once at the end).</summary>
    private void OnAnyChangeSelectionChanged()
    {
        if (_bulkSelecting) return;
        // The event can fire off the UI thread in theory; marshal to be safe (cheap, idempotent).
        if (_ui.CheckAccess()) { RefreshAffectedRows(); RecomputeAffectedSelectionInfo(); }
        else _ui.BeginInvoke(() => { RefreshAffectedRows(); RecomputeAffectedSelectionInfo(); });
    }

    /// <summary>Called after any per-schedule toggle: refresh the changes grid (so the per-cell checkboxes redraw),
    /// re-evaluate every schedule row's tri-state, and recompute the helper line. UX_SPEC §5 C-3/C-5.</summary>
    private void OnAffectedSelectionChanged()
    {
        if (_bulkSelecting) return;   // a bulk select-all/none refreshes once at the end, not per row
        RefreshChanges();
        RefreshAffectedRows();
        RecomputeAffectedSelectionInfo();
    }

    /// <summary>Re-raise the computed tri-state on every affected-schedule row (e.g. after a per-CELL edit changed
    /// which cells are ticked). Subscribed to <see cref="ProposedChange.SelectionChanged"/>. UX_SPEC §5 C-3 cross-update.</summary>
    private void RefreshAffectedRows()
    {
        foreach (var row in AffectedSchedules) row.Refresh();
    }

    /// <summary>Recompute the "N of M schedules selected…" helper line + its visibility. UX_SPEC §4c.</summary>
    private void RecomputeAffectedSelectionInfo()
    {
        int total = AffectedSchedules.Count;
        // A schedule counts as "selected" when it is NOT fully deselected (checked or indeterminate).
        int selected = AffectedSchedules.Count(r => r.SelectionState != false);
        SomeAffectedDeselected = total > 0 && selected < total;
        AffectedSelectionInfo = SomeAffectedDeselected
            ? $"{selected} of {total} schedules selected — unticked schedules won't be imported."
            : "";
    }

    private void ShowPreview(ChangeSet cs)
    {
        _lastChangeSet = cs;
        InTabPickStep = false;   // §16: analysis returned → the pre-analysis tab picker is done
        _diagnosticLog = cs.DiagnosticLog;
        Changes.Clear();
        Skipped.Clear();
        foreach (var c in cs.Changes) Changes.Add(c);
        // §12 (user rule 2026-06-14): the skip display shows ONLY the user's data edits that won't apply — drop
        // structural/back-end skips (UserRelevant==false: inherent headers incl. user-edited, display-only schedules,
        // duplicate rows, metadata) ENTIRELY. This is the ONE place the VM Skipped collection is filled, so every skip
        // surface that reads it (panel + status count below + the diagnostic, which is rebuilt from the same scoped
        // sets) inherits the filter. It runs BEFORE the §11.1 subset-scope (line ~941), so the two filters compose:
        // structural-drop first (always), then selected-scope the remainder (subset mode only). ATTIC → 0 skipped.
        foreach (var s in cs.Skipped.Where(s => s.UserRelevant)) Skipped.Add(s);

        // CHANGE 1 (§8): conflicts are NO LONGER resolved here. They are resolved in ResolveSelectedAndFinalize,
        // AFTER the user picks which tabs to import (step 1), and ONLY for selected tabs. See ResolveAndPreview.

        // Rebuild the fix pane, preserving any value the user already typed. Reformat suggestions (parses but
        // wrong format → confirm) take precedence over the matching unparseable diagnostic for the same cell.
        var prior = Fixes.ToDictionary(f => (f.SheetTabName, f.ExcelRow, f.ExcelCol), f => f.NewValue);
        Fixes.Clear();
        var seen = new HashSet<(string, int, int)>();
        foreach (var rf in cs.Reformats)
        {
            var key = (rf.SheetTabName, rf.ExcelRow, rf.ExcelCol);
            if (!seen.Add(key)) continue;   // one fix row per cell — first wins (corrector suggestions are merged in first, keeping the user-typed value)
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

        // CHANGE 1 (§8.2): build the affected-schedules list from the union of (changes by source schedule) ∪
        // (conflicts by their schedule) — so a CONFLICT-ONLY tab still gets a selection row (it's "affected"),
        // and the change count stays honest (conflicts shown separately, not counted as changes). CHANGE 2 (§9.6):
        // attach each parent's dependent ("inherited-change") schedules (read-only).
        AffectedSchedules.Clear();
        foreach (var s in cs.SheetSummaries.Where(s => s.Changes > 0 || s.Conflicts > 0))
        {
            cs.Dependents.TryGetValue(s.ScheduleUid, out var deps);
            AffectedSchedules.Add(new AffectedScheduleRow(s, ChangesForSchedule, OnAffectedSelectionChanged, deps));
        }
        HasAffected = AffectedSchedules.Count > 0;
        HasMultipleAffected = AffectedSchedules.Count > 1;
        RecomputeAffectedSelectionInfo();

        // CHANGE 1 (§8.3): if >1 affected tab, enter STEP 1 (SELECT) — show the affected list, hide the changes
        // grid/apply, DON'T resolve conflicts yet. A single affected tab (or none) skips straight to resolve+preview
        // exactly as before (no extra click).
        if (AffectedSchedules.Count > 1)
        {
            InSelectStep = true;
            int affConf = cs.Conflicts.Count;
            ImportStatus = $"Choose which schedules to import ({AffectedSchedules.Count} affected"
                           + (affConf > 0 ? $", {affConf} conflict(s) to resolve next" : "") + ").";
            return;   // wait for "Resolve & preview selected" → ResolveAndPreview
        }

        InSelectStep = false;
        ResolveSelectedAndFinalize();
    }

    /// <summary>CHANGE 1 (§8.3): the "Resolve &amp; preview selected" action — leaves STEP 1, resolves conflicts for
    /// SELECTED tabs only, then shows the normal preview. Single-tab imports reach the same finalize directly from
    /// ShowPreview.</summary>
    [RelayCommand]
    private void ResolveAndPreview()
    {
        InSelectStep = false;
        ResolveSelectedAndFinalize();
    }

    /// <summary>
    ///     CHANGE 1 (§8.4): resolve type conflicts ONLY for schedules the user selected in step 1 (a conflict on a
    ///     deselected tab dissolves — recorded as skipped, NO dialog), then compute the final preview status.
    ///     Reused by the single-tab ShowPreview path and the ResolveAndPreview command.
    /// </summary>
    private void ResolveSelectedAndFinalize()
    {
        var cs = _lastChangeSet;
        if (cs == null) return;

        // Which schedules are selected (a schedule with no per-schedule row — shouldn't happen now since the list is
        // affected-keyed — is treated as selected, fail-open, since its changes still honour their own Selected).
        bool ScheduleSelected(string uid, string name)
        {
            var row = AffectedSchedules.FirstOrDefault(r => r.ScheduleUid == uid || r.ScheduleName == name);
            return row == null || row.SelectionState != false;   // null (mixed) or true ⇒ selected
        }

        // CHANGE 2 §3b: the tab a conflict belongs to (so its skip is attributable for the skip-log scoping).
        string ConflictTab(string uid, string name) =>
            cs.SheetSummaries.FirstOrDefault(s => s.ScheduleUid == uid || s.ScheduleName == name)?.SheetTabName ?? "";

        // §8.4: resolve each conflict only when its schedule is selected; else skip it without a dialog.
        foreach (var conflict in cs.Conflicts)
        {
            if (!ScheduleSelected(conflict.ScheduleUid, conflict.ScheduleName))
            {
                Skipped.Add(new SkippedItem
                {
                    Reason = "schedule not selected",
                    Detail = $"{conflict.Field} on '{conflict.TypeName}' — its schedule was not selected for import",
                    SheetTabName = ConflictTab(conflict.ScheduleUid, conflict.ScheduleName),
                });
                continue;
            }
            var opt = ConflictResolver?.Invoke(conflict);
            if (opt != null)
                Changes.Add(Importer.ResolveToChange(conflict, opt));
            else
                Skipped.Add(new SkippedItem { Reason = "conflict — unresolved", Detail = $"{conflict.Field} on '{conflict.TypeName}'",
                    SheetTabName = ConflictTab(conflict.ScheduleUid, conflict.ScheduleName) });
        }
        // A newly-resolved conflict change is added to Changes here — re-evaluate the schedule rows' tri-state so
        // its parent reflects it (the change is now selectable + selected by default).
        RefreshAffectedRows();
        RecomputeAffectedSelectionInfo();

        // CHANGE 2 §3b (FULL — integ1-2 final ruling): on a STRICT subset selection, scope BOTH the fix-pane (Fixes)
        // AND the skip-log (Skipped) display collections to the SELECTED schedules — so the post-resolve preview shows
        // ONLY selected content (no foreign fixables/skips). ALL-SELECTED GUARD: no subset → leave both global
        // (unchanged). FAIL-OPEN: an item whose tab can't be attributed to a known schedule is NEVER hidden.
        // (The status-line skip count reads Skipped.Count below, which is now the filtered count in subset mode, so the
        // headline "N skipped" matches the scoped skip-log — closes integ1-2's :920 status-count leak.)
        bool subset = AffectedSchedules.Any(r => r.SelectionState == false);
        SkipLogScopedToSelection = subset;   // §3b surface-4: drives the skip-panel "(selected schedules only)" hint
        if (subset)
        {
            // §11.1 ROOT-CAUSE FIX: a skip-ONLY foreign schedule (no changes, no conflicts) HAS a tab + SheetSummary
            // but is NOT in AffectedSchedules (that list = Changes>0 || Conflicts>0). The old code returned TRUE on
            // row==null → such a real-but-non-selected schedule's skip/fix LEAKED through (the "26" = ATTIC's N +
            // foreign skips). Correct discriminator: empty/unresolvable tab → SHOW (genuine fail-open); resolves to a
            // schedule that IS in the affected list → by its checkbox; resolves to a REAL but non-affected (skip-only)
            // schedule → HIDE (it isn't a selected import target).
            bool TabSelected(string tab)
            {
                if (string.IsNullOrEmpty(tab)) return true;   // genuinely unresolvable → show (fail-open)
                var ss = cs.SheetSummaries.FirstOrDefault(s => s.SheetTabName == tab);
                if (ss == null) return true;                  // unresolvable → show (fail-open)
                var row = AffectedSchedules.FirstOrDefault(r => r.ScheduleUid == ss.ScheduleUid || r.ScheduleName == ss.ScheduleName);
                if (row != null) return row.SelectionState != false;   // affected → by its checkbox
                return false;                                  // real but non-selected (skip-only) schedule → HIDE
            }
            for (int i = Fixes.Count - 1; i >= 0; i--)
                if (!TabSelected(Fixes[i].SheetTabName)) Fixes.RemoveAt(i);
            for (int i = Skipped.Count - 1; i >= 0; i--)
                if (!TabSelected(Skipped[i].SheetTabName)) Skipped.RemoveAt(i);

            // §3b surface-3: REBUILD the copy-log diagnostic scoped to the selected schedules (NOT a string-filter of
            // the frozen log) — enumerate only selected sheets, skip listing/count = selected-only, subset header.
            // Driven off the same selected set, so the diagnostic's "skipped: N" agrees with the panel + status count.
            if (cs.SourceWorkbook != null)
            {
                var selectedUids = AffectedSchedules.Where(r => r.SelectionState != false)
                    .Select(r => r.ScheduleUid).Where(u => !string.IsNullOrEmpty(u)).ToHashSet();
                _diagnosticLog = Importer.BuildDiagnosticLog(cs.SourceWorkbook, cs.DocCreationGuid, cs, selectedUids);
            }
        }

        // §15-E: read-only/out-of-range edits now drop silently at the diff sites, so no FrozenChange is ever
        // created — there is no "frozen" tally and no frozen panel. HasFrozen stays false (the XAML panel hides).
        HasFrozen = false;
        // Apply filters on Selected; the SELECTED message reports what Apply will actually do.
        int applyableSelected = Changes.Count(c => c.Selected);
        int applyableTotal = Changes.Count;
        int selSched = AffectedSchedules.Count(r => r.SelectionState != false);
        // Header (caption) renames the user made, scoped to the selected schedules (same scoping the apply uses).
        int selectedHeaderCount = SelectedHeaderChanges().Count;
        int totalHeaderCount = cs.HeaderChanges.Count;

        ReportPath = cs.ReportPath ?? "";

        // §15-B: scope the on-screen diagnostic counts (drift/advisory) to the SELECTED schedule(s) in subset mode —
        // the same selected-scope the skip-log + diagnostic Result block use — so the status never leaks workbook-
        // global counts (the "415 drift · 21 advisories" leak). All-selected → global (the two messages converge).
        // A diagnostic is selected when its SheetTabName maps to a selected schedule (fail-open on an unattributable
        // tab, mirroring the skip TabSelected rule). §15-C already excludes user-edited cells from drift at emission;
        // §15-G folds "can't-write" (red) into the skip/"didn't apply" surface, so it is NOT a separate status count.
        bool DiagSelected(string tab)
        {
            if (!subset || string.IsNullOrEmpty(tab)) return true;
            var ss = cs.SheetSummaries.FirstOrDefault(s => s.SheetTabName == tab);
            if (ss == null) return true;
            var row = AffectedSchedules.FirstOrDefault(r => r.ScheduleUid == ss.ScheduleUid || r.ScheduleName == ss.ScheduleName);
            return row != null ? row.SelectionState != false : false;
        }
        var selDiags = cs.Diagnostics.Where(d => DiagSelected(d.SheetTabName)).ToList();
        int drift = selDiags.Count(d => d.Severity == "yellow" && d.Category == "drift");
        int advisories = selDiags.Count(d => d.Severity == "yellow" && d.Category != "drift");
        var diagParts = new List<string>();
        if (drift > 0) diagParts.Add($"{drift} drift");
        if (advisories > 0) diagParts.Add($"{advisories} " + (advisories == 1 ? "advisory" : "advisories"));
        string diagTail = diagParts.Count > 0 ? "  —  " + string.Join(" · ", diagParts) + " (see log)" : "";

        // §15-A: SELECTED-schedule message (its scoped counts) — leads, visually primary (it's what Apply does).
        // §15-D: "skipped" excludes deselected schedules (already filtered out of Skipped in subset mode) — it counts
        // ONLY editable cells the user edited whose edit won't apply (§15-F).
        string selName = subset
            ? (selSched == 1
                ? AffectedSchedules.First(r => r.SelectionState != false).ScheduleName
                : $"{selSched} selected schedules")
            : "";
        string selectedMsg = (subset ? $"Selected ({selName}): " : "")
                       + $"{applyableSelected} change(s)"
                       + (selectedHeaderCount > 0 ? $", {selectedHeaderCount} heading rename(s)" : "")
                       + (Skipped.Count > 0 ? $", {Skipped.Count} skipped" : "")
                       + (cs.Conflicts.Count > 0 ? $", {cs.Conflicts.Count} conflict(s) reviewed" : "")
                       + diagTail
                       + (Fixes.Count > 0 ? $"  ·  {Fixes.Count} fixable below" : "")
                       + (cs.CrossModel ? "  — ⚠ different source model" : "");

        // §15-A: WORKBOOK message (totals) — only shown in subset mode (all-selected → the two converge to one line).
        if (subset)
        {
            int wbSched = AffectedSchedules.Count;
            ImportStatus = selectedMsg
                + $"\nWorkbook: {applyableTotal} change(s)"
                + (totalHeaderCount > 0 ? $" + {totalHeaderCount} heading rename(s)" : "")
                + $" across {wbSched} schedule{(wbSched == 1 ? "" : "s")} "
                + "(only the selected schedules will be imported).";
        }
        else
        {
            ImportStatus = selectedMsg;
        }
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

    /// <summary>The how-to markdown explaining how to apply the staged group-edits artifact with Claude.
    /// Written automatically next to the group-edits JSON whenever Claude-Assist stages built-in group edits
    /// (see <see cref="StageGroupEdits"/>).</summary>
    private string ClaudeGuideMarkdown() => @"# Transom — Applying staged BUILT-IN group edits with Claude

This file sits **next to the staged group-edits JSON** (same folder). When you import with **Claude Assist**,
Transom stages **built-in parameter edits on elements inside Revit MODEL GROUPS** into a group-edits JSON in
this **same folder**, and drops these instructions alongside it. Built-in params (Comments, Mark, Level, …)
**cannot vary per group instance**, so they must be
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
    }
}
