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

    /// <summary>For a CONFLICT-ONLY schedule (no selectable changes, but ≥1 unresolved TYPE conflict) there are no
    /// per-change Selected bits to drive the tri-state, so the checkbox is backed by this flag. Default TRUE: a
    /// conflict-only schedule is selected by default so its conflicts reach the resolution dialog on Apply (else the
    /// schedule is unselectable → its conflicts are skipped "not selected" → the picker never fires).</summary>
    private bool _conflictsOnlySelected = true;

    /// <summary>True when this schedule has unresolved conflicts but NO selectable changes — the checkbox state then
    /// comes from <see cref="_conflictsOnlySelected"/>, not the (empty) change set.</summary>
    private bool ConflictsOnly => _summary.Conflicts > 0 && !_changesFor(_summary).Any(c => c.Selectable);

    /// <summary>Tri-state: true = all selectable changes selected, false = none, null = mixed. GET is computed from
    /// the changes; SET (from the checkbox) selects/deselects all this schedule's SELECTABLE changes (a null set from
    /// the UI is ignored — indeterminate is never user-chosen). A conflict-only schedule (no changes, ≥1 conflict)
    /// is tracked by <see cref="_conflictsOnlySelected"/> so it can still be ticked to resolve its conflicts.</summary>
    public bool? SelectionState
    {
        get
        {
            // Only SELECTABLE (non-frozen) changes participate — a frozen change is never applied, so it must not
            // drag the tri-state to "mixed".
            var selectable = _changesFor(_summary).Where(c => c.Selectable).ToList();
            // No selectable changes: a schedule with unresolved CONFLICTS is still selectable (backed by
            // _conflictsOnlySelected) so the user can include it and reach the conflict picker on Apply; otherwise
            // (truly nothing to do) it reads as unchecked.
            if (selectable.Count == 0) return _summary.Conflicts > 0 ? _conflictsOnlySelected : false;
            bool anySelected = selectable.Any(c => c.Selected);
            bool anyUnselected = selectable.Any(c => !c.Selected);
            if (anySelected && anyUnselected) return null;   // mixed → indeterminate
            return anySelected;                               // all selected (true) or none (false)
        }
        set
        {
            if (value == null) return;            // indeterminate is display-only; never set from the UI
            if (ConflictsOnly) _conflictsOnlySelected = value.Value;   // no changes to flip; track the conflict-only choice
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
    /// <summary>"Apply selected" is enabled (not greyed) only after a Preview has produced a change set — i.e. when
    /// <see cref="_lastChangeSet"/> is non-null. Before any preview (just Browsed), and after a completed apply
    /// (which nulls _lastChangeSet), it's disabled, so the user must Preview before they can Apply. Raise
    /// OnPropertyChanged(nameof(CanApply)) wherever _lastChangeSet is set or nulled.</summary>
    public bool CanApply => _lastChangeSet != null;
    /// <summary>Conflict choices the user already made, keyed by (typeId, parameterId), remembered ACROSS a re-Preview
    /// so confirming a format fix (which re-runs the whole analysis) does NOT re-prompt conflicts already resolved.
    /// Stores the chosen value (parsed double + string) so it can be matched to the re-built conflict's options even
    /// after the value's format changed (e.g. "2.5" → "2'-6""). Cleared when the workbook path changes.</summary>
    private readonly System.Collections.Generic.Dictionary<(long, int), (bool isString, string str, double dbl)> _resolvedConflicts = new();
    private string _stagedPath = "";
    private string _finalDestination = "";
    private string _pendingGroupNote = "";
    /// <summary>Set in Apply when format-mismatched rows are still unconfirmed → appended to the post-apply status so
    /// the user is told those edits weren't applied (not silently dropped). Captured before OnApplied clears Changes.</summary>
    private string _pendingConfirmNote = "";
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
    // G1: the bridge port the UI binds/probes is Transom's OWN self-host bridge (BridgeSelfHostPort, 48810) —
    // NOT the retired external-pyRevit probe (48884), which Transom never listened on.
    [ObservableProperty] private int _bridgePort = 48810;
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
            ImportStatus = s + _pendingGroupNote + _pendingConfirmNote;
            _pendingGroupNote = "";
            _pendingConfirmNote = "";
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
            OnPropertyChanged(nameof(CanApply));   // applied → no change set → re-grey Apply until next Preview
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
        BridgePort = _settings.BridgeSelfHostPort;   // G1: bind the UI to the self-host bridge port (48810)
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
        _resolvedConflicts.Clear();   // a new workbook = fresh conflicts; don't carry remembered resolutions
        _lastChangeSet = null;
        OnPropertyChanged(nameof(CanApply));   // new/changed workbook → no change set yet → Apply greyed until Preview
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
        // A DELIBERATE fresh Preview starts with no remembered conflict choices — so if the workbook changed on disk
        // since the last Preview, every conflict is re-asked. (The reformat re-Preview goes through ConfirmFix→
        // RunAnalysis, which does NOT call Preview(), so it correctly KEEPS the remembered choices — the whole point.)
        _resolvedConflicts.Clear();
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

    /// <summary>Confirms a PENDING (format-mismatched) preview row from the inline confirm strip — the same editable
    /// box the old bottom fix-pane had, now in the row. NO shortcut: a parsable value must be SHOWN as an
    /// interpretation and CONFIRMED before it touches the New cell; the user's input is never silently applied.
    /// On confirm we parse the box value:
    ///   • junk (won't parse) → show "enter a usable value" IN the row, stay pending;
    ///   • parsable, but its interpretation DIFFERS from what the row currently shows (e.g. they typed "6", the row
    ///     still shows "7'-0"") → UPDATE the shown interpretation to "6'-0"" and stay pending (re-prompt) — they
    ///     confirm again to accept it;
    ///   • parsable, and its interpretation MATCHES what's shown (they're confirming the value on screen) → commit it
    ///     to the New cell, clear pending. POINT 4: if that committed value equals the model, the row drops (no-op).</summary>
    [RelayCommand]
    private void ConfirmRow(ProposedChange? change)
    {
        if (change is not { NeedsConfirm: true }) return;

        var units = _lastChangeSet?.Units;
        string entered = (change.EditValue ?? "").Trim();

        // Numeric/length rows: parse the box value against the schedule's units. (String rows have no spec — nothing
        // to interpret — so they fall straight through to the commit below.)
        double parsed = 0;
        bool numeric = units != null && !string.IsNullOrEmpty(change.SpecTypeId);
        if (numeric)
        {
            var spec = new Autodesk.Revit.DB.ForgeTypeId(change.SpecTypeId);
            if (string.IsNullOrEmpty(entered) || !Autodesk.Revit.DB.UnitFormatUtils.TryParse(units, spec, entered, out parsed))
            {
                change.ConfirmError = $"enter a usable value for {change.Field} (e.g. 7' or 7\")";
                return;   // junk → ask again IN the row, stay pending
            }
            var canonical = ExcelCorrector.Canonical(units, spec, parsed, entered);
            if (!ExcelCorrector.SameFormat(canonical, change.Suggestion))
            {
                // The box now means something different from what's shown → re-prompt with the new interpretation
                // (do NOT commit — the user must confirm what they'll get).
                change.Suggestion = canonical;
                change.ConfirmError = "";
                return;   // stay pending; the row now shows the new interpretation to confirm
            }
            // Confirming the value already on screen → commit it.
            change.NewDouble = parsed;
            change.NewValue = canonical;
        }

        change.ConfirmError = "";
        change.NeedsConfirm = false;   // notifies Selectable → the checkbox enables and the details strip hides
        // POINT 4: confirmed value == the model's current value (OldValue) ⇒ a no-op ⇒ drop the row entirely.
        if (ExcelCorrector.SameFormat(change.NewValue, change.OldValue))
            Changes.Remove(change);
        else
            change.Selected = true;    // a real confirmed change is ticked for apply by default

        RefreshAffectedRows();         // its schedule's tri-state now reflects the confirmed/removed change
        RecomputeAffectedSelectionInfo();
    }

    [RelayCommand]
    private void Apply()
    {
        // SAFETY BELT (code2): apply only rows that are BOTH ticked AND selectable. The confirm-gate already keeps a
        // pending (NeedsConfirm) row un-ticked, but Selected defaults true, so requiring Selectable here means a
        // future select-path that forgets the guard can never silently apply a half-interpreted value.
        var selected = Changes.Where(c => c.Selected && c.Selectable).ToList();

        // Rows the user hasn't confirmed yet (format-mismatched, awaiting the inline confirm) are EXCLUDED above.
        // Count them so we can tell the user instead of silently dropping their edits (the original complaint).
        int pending = Changes.Count(c => c.NeedsConfirm);

        // Header (column-caption) renames to apply, scoped to the SELECTED schedules (header edits have no per-row
        // checkbox — they ride their schedule's selection). A header edit is included when its schedule is ticked
        // (or there's no per-schedule selection at all, i.e. everything applies).
        var selectedHeaderChanges = SelectedHeaderChanges();

        if (selected.Count == 0 && selectedHeaderChanges.Count == 0)
        {
            // If the only reason nothing's selected is unconfirmed rows, say THAT (not a generic dead-end) — so the
            // user knows their edits are waiting on confirmation, not lost.
            ImportStatus = pending > 0
                ? $"{pending} row(s) need confirmation before they can be applied — confirm the interpreted value(s) below, then Apply."
                : AffectedSchedules.Count > 0
                    ? "No schedules selected — tick at least one schedule to import."
                    : "Nothing selected to apply.";
            return;
        }

        // Stash a note about rows left unconfirmed so the post-apply status TELLS the user they weren't applied
        // (rather than silently omitting them from the "Applied N" count — the original silent-drop complaint).
        // Captured now because OnApplied clears Changes before it builds the status.
        _pendingConfirmNote = pending > 0
            ? $"  ⚠ {pending} row(s) were not applied — they still need confirmation (confirm the interpreted value, then Apply again)."
            : "";

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

        // DEFECT D2 FIX (#96) + F1/F2 — SHARED adoption helper for the option-2 (2a TYPE / 2b INSTANCE) paths.
        // ApplyNewParam REPLACES the whole column and REPOINTS the schedule field (AddOrReplaceField) for BOTH 2a and
        // 2b — so every instance the column touches must be written on the NEW param, including the UNGROUPED ones
        // (which arrived as GroupMode.None directChanges; only grouped members route through the picker). Otherwise the
        // ungrouped edits land on the OLD param while the displayed (new) column shows their stale value.
        //   (a) stamp Resolution + the user-confirmed #97 name on the grouped changes and bucket them as option-2;
        //   (b) MOVE the matching ungrouped directChanges into newParamChanges so ApplyNewParam owns the whole column
        //       (same ColumnKey → grouped into ONE param; their BulkInstanceIds make editByUid isEdit=true → EditVal;
        //       Resolution set BEFORE the direct pass so it skips them and never writes the old param).
        // F2 SCOPE: directChanges + the picker are GLOBAL across all selected schedules, and a built-in param shares the
        // same negative ParameterId across schedules — so the adoption MUST be scoped to the SAME SourceScheduleUid(s)
        // as the grouped changes being resolved, or resolving schedule A's column would silently convert+repoint
        // schedule B's same-named column where the user had no conflict/dialog. Used by BOTH the 2a and 2b cases.
        void AdoptColumn(List<ProposedChange> groupedList, GroupResolution res, GroupResolutionPrompt p)
        {
            foreach (var c in groupedList) { c.Resolution = res; c.NewParamName = p.ChosenParamName; }
            newParamChanges.AddRange(groupedList);

            foreach (var c in MatchingUngrouped(groupedList))
            {
                c.Resolution = res;
                c.NewParamName = p.ChosenParamName;
                directChanges.Remove(c);
                newParamChanges.Add(c);
            }
        }

        // DEFECT D3 FIX (#100) — MIRROR of AdoptColumn for the SKIP path. The per-parameter picker runs ONLY over
        // grouped changes; a column's UNGROUPED instances live in directChanges (GroupMode.None) and the direct-write
        // pass applies them UNCONDITIONALLY. So picking Skip left the grouped changes unbucketed (correctly not applied)
        // but the ungrouped portion still wrote — Skip wasn't honored for them (user-found: 17 Hardware rows applied on
        // Skip). SkipColumn REMOVES the matching ungrouped directChanges (same ParameterId+Field, scoped to the grouped
        // changes' SourceScheduleUid set — same F2 scope as AdoptColumn) so NOTHING from a skipped column writes, and
        // records them as skipped so the report is honest.
        int SkipColumn(List<ProposedChange> groupedList)
        {
            int removed = 0;
            foreach (var c in MatchingUngrouped(groupedList))
            {
                directChanges.Remove(c);
                // F3: record one SkippedItem PER INSTANCE so the post-apply "N skipped" count (which counts
                // SkippedItems) matches the inline note's instance count — an honest, consistent skip report.
                int n = c.BulkInstanceIds?.Count ?? 1;
                for (int i = 0; i < n; i++)
                    _lastChangeSet?.Skipped.Add(new Core.SkippedItem
                    {
                        Reason = "skipped by user",
                        Detail = $"{c.Field} ({c.ElementName}) — column skipped at the group-conflict prompt",
                    });
                removed += n;
            }
            return removed;
        }

        // The ungrouped directChanges belonging to the SAME column (ParameterId + Field) as a set of grouped changes,
        // scoped to those grouped changes' source schedule(s) — shared by AdoptColumn (pull IN) and SkipColumn (pull OUT)
        // so the column-matching + F2 cross-schedule scope can't diverge between the two.
        List<ProposedChange> MatchingUngrouped(List<ProposedChange> groupedList)
        {
            var pid = groupedList[0].ParameterId;
            var field = groupedList[0].Field;
            var scheds = groupedList.Select(c => c.SourceScheduleUid).ToHashSet();
            return directChanges
                .Where(c => c.ParameterId == pid && c.Field == field && scheds.Contains(c.SourceScheduleUid))
                .ToList();
        }

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
                // #93B: the column's custom display heading (when it differs from the real parameter name) so the
                // dialog can name the actual parameter being changed even for a renamed column.
                Header = list.Select(c => c.SourceHeading).FirstOrDefault(h => !string.IsNullOrEmpty(h)) ?? "",
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
                IsGeometryDriven = list.Any(c => c.GeometryDriven),
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
                    // NOTE: Vary does NOT adopt/remove the column's ungrouped instances — they stay in directChanges and
                    // direct-write the ORIGINAL param, which is exactly right (Vary keeps the original param; ungrouped
                    // members just don't need 'vary by group instance'). Do NOT "symmetry-fix" this into MatchingUngrouped
                    // like Skip/Adopt/Assist — that would wrongly pull Vary's ungrouped edits off the write path.
                    foreach (var c in list) c.Resolution = GroupResolution.Vary;
                    varyChanges.AddRange(list);
                    break;
                case GroupResolution.NewTypeParam:
                    // F1: 2a repoints the column too (AddOrReplaceField), so it adopts the ungrouped instances exactly
                    // like 2b — via the shared helper, no copy-paste. (#97 name + #96 D2 fix + F2 scope folded in.)
                    AdoptColumn(list, GroupResolution.NewTypeParam, prompt);
                    break;
                case GroupResolution.NewInstanceParam:
                    AdoptColumn(list, GroupResolution.NewInstanceParam, prompt);
                    break;
                case GroupResolution.ClaudeAssist:
                    foreach (var c in list) c.Resolution = GroupResolution.ClaudeAssist;
                    stagedChanges.AddRange(list);
                    // CHANGE B (#100, user-directed): the WHOLE column goes to Claude, not a split — so the column's
                    // UNGROUPED instances must NOT direct-write the original param. Pull them out of directChanges (so
                    // the direct pass skips them) and stage them WITH the grouped ones. Same column-match + F2 scope as
                    // Skip/Adopt via the shared MatchingUngrouped helper. Result: an Assist column commits NOTHING now.
                    foreach (var c in MatchingUngrouped(list))
                    {
                        c.Resolution = GroupResolution.ClaudeAssist;
                        directChanges.Remove(c);
                        stagedChanges.Add(c);
                    }
                    wantClickHelper = true;
                    break;
                default: // Skip
                    // D3 FIX (#100): Skip must skip the WHOLE column — also pull the ungrouped instances OUT of
                    // directChanges so the direct pass doesn't write them. Count them in the skipped total.
                    int skippedUngrouped = SkipColumn(list);
                    notes.Add($"{InstanceCountOf(list) + skippedUngrouped} edit(s) to “{grp.Key.Field}” skipped");
                    break;
            }
        }

        // Stage the dance / Claude-assist edits to JSON for Claude; bring up ClickHelper when requested.
        if (stagedChanges.Count > 0)
        {
            var path = ChooseArtifactPath();
            if (path == null)
            {
                notes.Add($"{InstanceCountOf(stagedChanges)} group edit(s) not staged (no file chosen)");
                RescueUngroupedStaged();   // F1: don't silently drop the ungrouped edits on a Save-cancel
            }
            else
            {
                var staged = StageGroupEdits(stagedChanges, path);
                if (staged != null)
                {
                    notes.Add($"{InstanceCountOf(stagedChanges)} group edit(s) staged for Claude");
                    if (wantClickHelper) notes.Add(EnsureClickHelper());
                    ClaudeStagedNotice?.Invoke(staged);
                }
                else
                {
                    notes.Add($"{InstanceCountOf(stagedChanges)} group edit(s) could not be staged");
                    RescueUngroupedStaged();   // F1: staging failed → still apply the ungrouped edits directly
                }
            }
        }

        // F1 (data-loss fix): CHANGE B moved an Assist column's UNGROUPED instances out of directChanges into
        // stagedChanges. If staging is cancelled (no file) or fails, those rows would be in NEITHER bucket and silently
        // vanish (pre-v1.4.5 they direct-wrote regardless of the dialog). So on the cancel/fail paths, put the ungrouped
        // (GroupMode.None) staged changes BACK on the direct-write path — they can apply without Claude. The GROUPED
        // (BuiltinDance) staged changes are NOT rescued: Revit refuses a direct grouped built-in write, so they can only
        // go through Claude — dropping them on a stage-cancel is correct (they were never directly applicable).
        void RescueUngroupedStaged()
        {
            foreach (var c in stagedChanges.Where(c => c.GroupMode == GroupMode.None).ToList())
            {
                c.Resolution = null;          // clear the Assist routing so the direct pass writes it normally
                stagedChanges.Remove(c);
                if (!directChanges.Contains(c)) directChanges.Add(c);
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
            OnPropertyChanged(nameof(CanApply));   // nothing applicable → drop the change set → re-grey Apply
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
        // No "Recommended" wording anywhere in the dialog (user-directed 2026-06-22) — these notes just explain the
        // inferred binding so the user can choose; they no longer flag one option as recommended.
        Option2Mode.AmbiguousPreferType =>
            "This schedule is organized by type, so a type parameter keeps one value per type and unifies the variations.",
        Option2Mode.AmbiguousPreferInstance =>
            "This schedule itemizes every instance (or isn't grouped by type), so an instance parameter preserves " +
            "each element's own value.",
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
                oldValue = g.OldValue,                 // Cowork verifies "was oldValue -> now value"
                // The new shared-param name when this column is being converted via option 2a/2b (#97); empty for a
                // plain in-place edit. Cowork/the verifier reads THIS field rather than the original param (ties to #99).
                newParamName = string.IsNullOrWhiteSpace(g.NewParamName) ? null : g.NewParamName.Trim(),
                memberUniqueIds = g.BulkInstanceIds ?? new List<string> { g.UniqueId },
            }).ToArray();

            var payload = new
            {
                tool = "Transom",
                kind = "group-edits",
                schedule = _lastChangeSet?.ScheduleName ?? "",
                // Doc identity for Cowork to pick the RIGHT open model: title + path + CreationGUID. Title+GUID alone is
                // NOT enough — two of the user's models SHARE a CreationGUID, so the PATH is the disambiguator.
                docTitle = SelectedProject,
                docPath = _lastChangeSet?.DocPath ?? "",
                docCreationGuid = _lastChangeSet?.DocCreationGuid ?? "",
                project = SelectedProject,
                note = "These are parameter edits the user chose to apply via Claude — on elements inside Revit MODEL " +
                       "GROUPS, plus any UNGROUPED instances of the same column staged with them. FIRST check 'group': " +
                       "an EMPTY 'group' means the element is NOT in a model group, so it is ALWAYS a plain per-instance " +
                       "write via set_parameter (no group-edit-mode, no definition-swap) — this holds REGARDLESS of the " +
                       "parameterId sign. The parameterId rules below apply ONLY to entries WITH a non-empty 'group':\n" +
                       "  • parameterId >= 0 = PROJECT/SHARED params: writable per group instance after enabling 'vary " +
                       "by group instance', or via the bridge set_parameter (which handles group members directly).\n" +
                       "  • parameterId < 0 = BUILT-IN params on a GROUP member: a direct write is rejected ('Changes to " +
                       "groups are allowed only in group edit mode') and the API can't edit a group member — so apply " +
                       "these by driving Revit's 'Edit Group' MODE in the UI with the Transom UI-Assist (ClickHelper) " +
                       "tools (select+zoom+red-locator via API, then enter Edit Group, pick the member, set the param in " +
                       "Properties, Finish, then verify the value + that the group member COUNT is unchanged). This sets " +
                       "the value UNIFORMLY for every instance of the group type (the group definition) — that is correct " +
                       "and durable; per-instance DIVERGENT built-in values are not possible while grouped and need an " +
                       "instance shared parameter (import option 2b) instead. Full step-by-step is in 'Transom - Apply " +
                       "staged edits with Claude.md' in this same folder. Verify every write.",
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
        OnPropertyChanged(nameof(CanApply));   // a preview ran → there's a change set → enable Apply
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
            // Reuse a choice the user already made for this (type, parameter) — so confirming a format fix (which
            // re-runs the whole analysis) does NOT re-ask a conflict they already resolved. The remembered value is
            // matched to one of THIS rebuilt conflict's options (string exact, or double within 1e-9 so a reformatted
            // "2.5"→"2'-6"" still matches), and we apply that option silently. Only prompt for a genuinely new conflict.
            var key = (conflict.TypeId, conflict.ParameterId);
            ConflictOption? opt = null;
            bool fromMemory = false;
            if (_resolvedConflicts.TryGetValue(key, out var prev))
            {
                opt = conflict.Options.FirstOrDefault(o => o.Parseable &&
                    (prev.isString ? o.NewString == prev.str : System.Math.Abs(o.NewDouble - prev.dbl) < 1e-9));
                fromMemory = opt != null;
            }
            if (opt == null)
                opt = ConflictResolver?.Invoke(conflict);

            if (opt != null)
            {
                if (!fromMemory)   // remember a freshly-made choice so a later re-Preview won't re-prompt it
                    _resolvedConflicts[key] = (opt.IsString, opt.NewString, opt.NewDouble);
                // Drop a TRUE no-op only: the explicit "keep current" option (Display == CurrentDisplay — the type's
                // current value, offered when unedited siblings would be clobbered). A pick whose value merely EQUALS
                // the model in a DIFFERENT FORMAT (entered "7" vs current "7'-0"") is NOT dropped here — ResolveToChange
                // makes it a PENDING (NeedsConfirm) row so the interpretation is confirmed every time, and ConfirmRow
                // then removes it because the confirmed value equals the model. (ANY workbook↔model difference, even
                // trivial, is confirmed — never silently canonicalized.)
                if (opt.Display == conflict.CurrentDisplay)
                    Skipped.Add(new SkippedItem { Reason = "kept current", Detail = $"{conflict.Field} on '{conflict.TypeName}' — left at '{conflict.CurrentDisplay}'",
                        SheetTabName = ConflictTab(conflict.ScheduleUid, conflict.ScheduleName) });
                else
                    Changes.Add(Importer.ResolveToChange(conflict, opt));
            }
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
        _settings.BridgeSelfHostPort = value;   // G1: persist to the self-host bridge port
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
Transom stages **built-in parameter edits on elements inside Revit MODEL GROUPS** (plus any ungrouped instances
of the same column) into a group-edits JSON in this **same folder**, and drops these instructions alongside it.
A built-in param (Comments, Finish, Mark, …) on a GROUP member **can't be written by the API** — a direct write
is rejected with *""Changes to groups are allowed only in group edit mode.""* So you apply it the way a person
would: drive Revit's **""Edit Group"" mode** in the UI with the ClickHelper tools. These steps are for Claude.

> Project/shared parameters on grouped elements are NOT here — Transom applies those itself (it enables
> ""vary by group instance"" and writes them). This file is only the built-in edits, which need the UI path below.

## THE KEY FACT: the API is useless for editing a group member — this is a SCREENSHOT-DRIVEN UI task
Inside Edit Group mode the Revit API / pyRevit routes are UNAVAILABLE: you cannot select, read, write, or
verify a member via the API while editing the group. The API's ONLY role is 3 things, all BEFORE you enter
Edit Group or AFTER you Finish: (a) SELECT/zoom to set up the view, (b) apply/remove a RED color override as a
visual locator, (c) the final post-Finish VERIFY (param value + group member count). EVERYTHING between —
entering edit mode, picking the element, opening Properties, typing the value, applying, finishing — is
**screenshot + ClickHelper clicks/keys**. **Take a screenshot after almost every click/type/scroll, READ it,
and confirm the expected state before the next action** — a wrong-element pick, a mis-focused field, and a
missed button are all SILENT; the screenshot is the only way you catch them. Use the **Transom UI-Assist
(ClickHelper)** tools.

## 1. Find the staging file
In **this same folder**, find the file that parses as JSON with top-level `""tool"":""Transom""` and
`""kind"":""group-edits""` (default name `transom_group_edits.json`; match by **content, not name**). This
`.md` is not it. If several exist, use the most recent or ask the user.

## 2. Confirm the model + SAFETY
The open Revit document must match the JSON's `project`/`schedule`. **If not, STOP and tell the user.**
This procedure COMMITS edits — only do it on a **throwaway / non-production** model unless the user explicitly
says otherwise. If the model is **workshared, NEVER Synchronize with Central or Save** (the user controls sync).

## 3. Read the entries
Each entry = one `parameterId` set to one `value` across `memberUniqueIds`. **Check `group` FIRST:** an empty
`group` is an UNGROUPED instance → a plain per-instance write via the bridge `set_parameter` (no Edit Group
mode needed), regardless of parameterId sign. A non-empty `group` with `parameterId < 0` is the built-in
GROUP-member case this UI path handles. (A non-empty `group` with `parameterId >= 0` is project/shared and
writable via `set_parameter` / ""vary by group instance"" — Transom usually applies those itself.)

**One value per group, uniformly.** Editing a built-in in Edit Group mode changes the group **definition**, so
**every instance of that group type gets the same value** — that is correct and durable. If two entries want
**different** values on the same member role of one group type, they can't both hold while grouped: a built-in
can't differ between instances of a type. Do NOT guess — tell the user that per-instance divergent values need
an **instance shared parameter (import option 2b)** instead (2b never ungroups and is exclusion-safe), or to
pick one uniform value.

> NOTE: excluded members, attached detail groups, and nested groups do **not** block this UI path — a person
> (and you) can edit a member through all of those by hand. Proven live: a built-in was written on a member of
> a group that HAD an excluded member, with the member count unchanged and nothing lost. (This differs from the
> retired API definition-swap, which had to skip those cases. Do not skip them here.)

## 4. Apply each built-in group edit via Edit Group mode (per member element)
PRECONDITION: turn **Thin Lines ON** (lineweights off) so overlapping geometry is easy to pick — `key esc`
(canvas focus), then `keys tl`; screenshot to confirm crisp single lines.

For each member to edit:
1. **SELECT + ZOOM via API:** `uidoc.Selection.SetElementIds([id])`, `uidoc.ShowElements([id])`. Screenshot;
   confirm it's visible (Revit highlights it cyan).
2. **RED LOCATOR via API:** in a transaction, `view.SetElementOverrides(id, ogs)` with a RED projection-line +
   solid surface fill (a `FillPatternElement` whose `GetFillPattern().IsSolidFill`). Commit, then deselect
   (`SetElementIds([])`). Screenshot → confirm you can SEE the red element. (The override survives into Edit
   Group mode and scales with zoom.)
3. **ENTER EDIT GROUP:** select the GROUP instance via API (`SetElementIds([groupInstanceId])`) → screenshot
   (ribbon = ""Modify | Model Groups"") → `find ""Edit Group""` and `click-id` it (the center coord is often
   off-screen/negative — use click-id, not click-xy). The Edit Group toolbar now shows in `dialogs`
   [add, remove, attach, finish, cancel]. **From here the API is unavailable.**
4. **PICK THE MEMBER by its red color:** read the screenshot, click the red element's screen coord
   (`click-xy`). OVERLAP HAZARD: a click can grab an element on top — if the screenshot shows the wrong/
   overlapping element selected, `scroll <x> <y> <+notches>` to ZOOM IN until they separate, then click the
   red body (or `key tab` to cycle alternates under the cursor). **Screenshot + read the Properties header /
   status bar to CONFIRM the right element before editing** — a wrong pick is silent.
5. **REVEAL + SET THE PARAM** (built-in instance params like Comments live in the Properties palette's
   **Identity Data** section — `scroll` the palette down to it): click the value cell to focus → screenshot,
   confirm the caret is in the field → `type --at=X,Y ""VALUE""` (field empty first; put text LAST — `--enter`
   before the text breaks parsing) → then `key enter --at=X,Y` to commit. Confirm `fgIsRevit=True`. The
   Properties **""Apply""** button enables → click it; screenshot → value shows + Apply greys out = committed.
6. **FINISH:** `key esc` to deselect → `click-dialog ""finish""` → `dialogs` shows **0 open = committed**.

## 5. Verify (API available again)
Per member: re-read the parameter (must equal `value`), confirm its `GroupId` (still grouped), and the group's
**member COUNT is UNCHANGED** (= nothing lost or un-excluded). Then REMOVE the red override:
`view.SetElementOverrides(id, OverrideGraphicSettings())` in a transaction; screenshot → red gone.
If a write didn't take or the count changed, report it — never leave a half-applied state.

## 6. Report
Per group type / member: applied / skipped (with reason) / conflicts (per-instance divergence → point to
option 2b). Note this changed the group **definition**, so all instances of the type share the new value.

## Workshared close (if you ever close the model)
NEVER click ""synchronize"" or ""save"" on a close dialog. LOOP on `dialogs`: issue ONE `click-dialog` per call,
first match wins — ""do not save"" (Changes Not Saved) → ""keep ownership"" (Editable Elements / Close Without
Saving). Never bare `click-dialog` (defaults to cancel/close → aborts the close). The user controls sync.
";

    partial void OnExchangeFolderChanged(string value)
    {
        _settings.ExchangeFolder = value;
        _settings.Save();
    }
}
