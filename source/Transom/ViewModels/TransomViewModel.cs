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
    /// <see cref="_lastChangeSet"/> is non-null — AND every pending (NeedsConfirm) row in a SELECTED schedule has
    /// been confirmed or discarded (the confirm-or-discard gate: a pending row must never be silently skipped by an
    /// Apply, so Apply waits for all of them). Scoped to selected schedules per review 2026-07-13: a pending row in
    /// a schedule the user DESELECTED can never apply, so it must not block the rest of the import. Raise
    /// OnPropertyChanged(nameof(CanApply)) wherever _lastChangeSet is set/nulled, a pending row is confirmed,
    /// discarded, or added (conflict resolution), the grid is refilled, OR a schedule's selection flips.</summary>
    public bool CanApply => _lastChangeSet != null
        && !_importBusy                                                    // a raise is outstanding — see _importBusy
        // Count header changes by the SAME predicate ApplyHeaderChanges uses (Importer: skips !Selected or
        // OutcomeNote=="skipped"). The raw list length left Apply enabled for a preview whose only header
        // renames were deselected — pressing it ran a transaction that wrote nothing.
        && (Changes.Count > 0 || _lastChangeSet.HeaderChanges.Any(h => h.Selected && h.OutcomeNote != "skipped"))
        && !Changes.Any(PendingBlocksApply);

    /// <summary>
    ///     True from the moment an import ExternalEvent is raised until its callback returns. REQUIRED:
    ///     <see cref="ImportEventHandler"/> holds its request in single scalar fields with no queue, so two
    ///     raises COALESCE into one Execute against the second request. Without this gate the corrections
    ///     re-preview was the live case — Apply stayed enabled while a re-preview raise was outstanding, so
    ///     clicking it discarded the user's corrections and applied the pre-correction change set, reporting
    ///     an ordinary success. (docs/design-notes/revit-api-research-notes.md: handlers must not assume 1:1.)
    /// </summary>
    private bool _importBusy;

    private void SetImportBusy(bool busy)
    {
        if (_importBusy == busy) return;
        _importBusy = busy;
        OnPropertyChanged(nameof(CanApply));
    }

    /// <summary>True for a pending row whose schedule is still selected — the rows the confirm-or-discard gate
    /// counts. A pending row in a fully-deselected schedule is excluded everywhere the gate looks.</summary>
    private bool PendingBlocksApply(ProposedChange c) => c.NeedsConfirm && ScheduleSelectedFor(c);

    /// <summary>
    ///     THE schedule-identity rule, used by every site that has to decide "is this the same schedule".
    ///     Uid-first (rename-safe), name fallback — and the uid comparison only counts when the uid is
    ///     NON-EMPTY. That guard is the whole point: a workbook exported without schedule uids leaves every
    ///     uid "", so a bare `a.Uid == b.Uid` matches the FIRST row for everything, and per-schedule
    ///     selection silently applies one schedule's answer to all of them.
    /// </summary>
    private static bool SameSchedule(string uidA, string nameA, string uidB, string nameB) =>
        (!string.IsNullOrEmpty(uidA) && !string.IsNullOrEmpty(uidB) && uidA == uidB)
        || (!string.IsNullOrEmpty(nameA) && nameA == nameB);

    /// <summary>Whether the change's source schedule is selected (checked or mixed). No per-schedule row (single-
    /// schedule import / unattributable) ⇒ selected, fail-open — matches ResolveSelectedAndFinalize's rule.</summary>
    private bool ScheduleSelectedFor(ProposedChange c)
    {
        var row = AffectedSchedules.FirstOrDefault(r =>
            SameSchedule(c.SourceScheduleUid, c.SourceScheduleName, r.ScheduleUid, r.ScheduleName));
        return row == null || row.SelectionState != false;
    }
    /// <summary>Conflict choices the user already made, keyed by (typeId, parameterId), remembered ACROSS a re-Preview
    /// so confirming a format fix (which re-runs the whole analysis) does NOT re-prompt conflicts already resolved.
    /// Stores the chosen value (parsed double + string) so it can be matched to the re-built conflict's options even
    /// after the value's format changed (e.g. "2.5" → "2'-6""). Cleared when the workbook path changes.</summary>
    private readonly System.Collections.Generic.Dictionary<(long, int), (bool isString, string str, double dbl)> _resolvedConflicts = new();
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

    // False at zero documents (Settings opens without a project now) — gates the Export/Import tabs.
    [ObservableProperty] private bool _hasProject;

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

    // Claude Assist is ONE persisted on/off switch (replaces the old unpersisted Off/Verify/Assist mode).
    // ON = run-logs are written to the exchange folder, grouped built-ins may be staged for Claude on Apply,
    // and the loopback bridge runs (auto-started with Revit while the flag is set).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClaudeAssistHint))]
    private bool _isClaudeAssistEnabled;
    /// <summary>True while the toggle's setup/start/stop work is in flight — the switch is disabled so a
    /// double-click can't race the registration or the listener.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClaudeToggleEnabled))]
    private bool _claudeToggleBusy;
    public bool ClaudeToggleEnabled => !ClaudeToggleBusy;
    /// <summary>Shown once after a toggle-on that newly registered something: the user must restart Claude.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRestartNotice))]
    private string _restartNotice = "";
    public bool HasRestartNotice => !string.IsNullOrEmpty(RestartNotice);
    /// <summary>True before first setup (nothing registered, no shim) — the status panel shows one line.</summary>
    [ObservableProperty] private bool _claudeNotSetUp;
    [ObservableProperty] private string _claudeStatusFooter = "";
    public ObservableCollection<ClaudeStatusRow> ClaudeStatusRows { get; } = new();
    public string ClaudeAssistHint => IsClaudeAssistEnabled
        ? "Claude Code can read schedules and write parameters over the local bridge."
        : "First time on runs one-time setup (per-user, no admin).";
    // G1: the bridge port the UI binds/probes is Transom's OWN self-host bridge (BridgeSelfHostPort, 48810) —
    // NOT the retired external-pyRevit probe (48884), which Transom never listened on.
    [ObservableProperty] private int _bridgePort = 48810;
    [ObservableProperty] private string _exchangeFolder = "";
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
        // Every import callback clears the busy flag — the raise is done once one of these has run.
        _importHandler.OnPreview = cs => _ui.BeginInvoke(() => { SetImportBusy(false); ShowPreview(cs); });
        _importHandler.OnApplied = s => _ui.Invoke(() =>
        {
            SetImportBusy(false);
            // Option-2 old-values cleanup (user-directed 2026-07-18): grouped members the disposition couldn't
            // write via the API were collected during apply (only VERIFIED targets); stage them for Claude now —
            // after apply, because only now is it known which new-param copies actually landed. Runs before
            // _lastChangeSet is dropped below (it supplies the doc identity for the staging payload).
            var oldStaged = _importHandler.PendingChangeSet?.Option2OldValueStaged;
            if (oldStaged is { Count: > 0 })
                s += "  ·  " + StageOldValueEditsInteractive(oldStaged);
            ImportStatus = s + _pendingGroupNote;
            _pendingGroupNote = "";
            Changes.Clear();
            Skipped.Clear();
            Fixes.Clear();
            AffectedSchedules.Clear();
            OptionAffected.Clear();
            HasOptionAffected = false;
            HasAffected = false;
            HasMultipleAffected = false;
            SomeAffectedDeselected = false;
            SkipLogScopedToSelection = false;
            InSelectStep = false;
            _lastChangeSet = null;
            OnPropertyChanged(nameof(CanApply));   // applied → no change set → re-grey Apply until next Preview
        });
        _importHandler.OnAppliedLog = log => _ui.Invoke(() => _diagnosticLog = log);
        _importHandler.OnError = s => _ui.Invoke(() => { SetImportBusy(false); ImportStatus = "Error: " + s; });
        _scheduleLoadHandler.OnLoaded = (activeId, scheds) => _ui.Invoke(() => SetSchedules(activeId, scheds));

        // Cross-update (UX_SPEC §5 C-3): when a per-CELL checkbox in the changes grid toggles a change's Selected,
        // re-evaluate the per-schedule tri-state + helper line so they stay consistent. Skipped during a bulk
        // per-schedule toggle (which refreshes once at the end) to avoid a refresh-per-change storm.
        ProposedChange.SelectionChanged += OnAnyChangeSelectionChanged;

        _settings = TransomSettings.Load();
        _bridgePort = _settings.BridgeSelfHostPort;  // backing field: don't re-register/restart during construction
        ExchangeFolder = _settings.ExchangeFolder;
        EncouragingMessages = _settings.EncouragingMessages;
        // Backing field: reflect the persisted flag without running the toggle's setup/start logic (the
        // bridge is auto-started by Application.OnStartup when the flag is on).
        _isClaudeAssistEnabled = _settings.ClaudeAssistEnabled;
        _ = RefreshClaudeStatusAsync();
        RefreshSkills();   // populate the Claude Skills tab list (cheap folder read; re-read on tab entry)

        _copyResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        _copyResetTimer.Tick += (_, _) => { Copied = false; CopiedImport = false; CopiedLog = false; _copyResetTimer.Stop(); };

        foreach (var p in projects) Projects.Add(p);
        _hasProject = projects.Count > 0;
        _selectedProject = activeProjectTitle; // backing field: don't trigger a reload during construction
        SetSchedules(activeScheduleId, schedules);
        _initialized = true;
    }

    /// <summary>
    ///     Unhooks this viewmodel from the static <see cref="ProposedChange.SelectionChanged"/> event.
    ///     MUST be called when the Hub window closes: the static event otherwise roots the viewmodel for
    ///     the whole Revit session, and each reopened Hub adds another dead subscriber that re-runs the
    ///     row/selection refresh (against stale, possibly closed-document state) on every checkbox click.
    /// </summary>
    public void Detach() => ProposedChange.SelectionChanged -= OnAnyChangeSelectionChanged;

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
        HasProject = projects.Count > 0;

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
    public ObservableCollection<ScheduleEntry> FilteredSchedules { get; } = new();
    public ObservableCollection<ProposedChange> Changes { get; } = new();
    public ObservableCollection<SkippedItem> Skipped { get; } = new();
    public ObservableCollection<AffectedScheduleRow> AffectedSchedules { get; } = new();
    public ObservableCollection<UnparseableFix> Fixes { get; } = new();

    /// <summary>Preview split (user request 2026-07-19): flat, read-only "Schedules that may change based on options
    /// selected" list — schedules a pending group-conflict option (e.g. option 2's column replacement) MAY reach.
    /// Rebuilt by <see cref="RecomputeOptionAffected"/> from <see cref="ChangeSet.OptionDependents"/>, scoped to the
    /// currently selected driving schedules.</summary>
    public ObservableCollection<DependentRow> OptionAffected { get; } = new();

    /// <summary>Gates the "may change based on options selected" sub-section (visible only when it has rows).</summary>
    [ObservableProperty] private bool _hasOptionAffected;

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

    /// <summary>Set by the view (option-2 follow-up 1, user request 2026-07-12): the "also replace this column in
    /// these other schedules" checklist for one converted column. Returns the ticked schedule uids; null (dialog
    /// closed) = replace nowhere else. Only invoked when the build-time scan found other schedules.</summary>
    public Func<Option2SchedulesPrompt, List<string>?>? Option2SchedulesResolver;

    /// <summary>Set by the view (option-2 follow-up 2): the "what happens to the old values" chooser — leave,
    /// clear, or one uniform replacement value. Null (dialog closed) = Leave, the safe default.</summary>
    public Func<Option2OldValuesPrompt, Option2OldValuesPrompt?>? Option2OldValuesResolver;

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

        _exportHandler.ScheduleIds = ids;
        _exportHandler.OutputPath = dlg.FileName;
        _exportHandler.DocTitle = SelectedProject;
        _exportHandler.ExchangeFolder = ExchangeFolder;   // still passed: enables the Claude-review run-log (no finalize gate)
        _exportHandler.ClaudeAssistEnabled = IsClaudeAssistEnabled;
        Status = $"Exporting {ids.Count} schedule(s)…";
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
        OptionAffected.Clear();
        HasOptionAffected = false;
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
            names = OfficeIsolation.Engine.ReadSheetNames(WorkbookPath);
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
        // Never stack a second request on an outstanding raise — they coalesce and one is silently lost.
        if (_importBusy) { ImportStatus = "Still analysing — wait for the current pass to finish."; return; }
        _importHandler.RequestedMode = ImportEventHandler.Mode.Preview;
        _importHandler.WorkbookPath = WorkbookPath;
        _importHandler.DocTitle = SelectedProject;
        _importHandler.WriteRunLog = IsClaudeAssistEnabled;
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
        SetImportBusy(true);
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
            // A real confirmed change is ticked for apply by default — UNLESS its schedule is deselected
            // (review 2026-07-13: unconditional Selected=true wrote a confirmed row into a schedule the user
            // had unticked, because the tri-state setter skips non-selectable pending rows when deselecting).
            change.Selected = ScheduleSelectedFor(change);

        RefreshAffectedRows();         // its schedule's tri-state now reflects the confirmed/removed change
        RecomputeAffectedSelectionInfo();
        OnPropertyChanged(nameof(CanApply));   // one fewer pending row — the confirm-or-discard gate may open
    }

    /// <summary>Discards a PENDING (NeedsConfirm) row from the inline strip — the row disappears and nothing is
    /// written for it. The dropped edit is recorded as skipped ("discarded by user") so the skip panel / log stay
    /// honest — a discard is deliberate, never silent. Together with Confirm this is the confirm-or-discard gate:
    /// Apply stays greyed until every pending row is resolved one way or the other.</summary>
    [RelayCommand]
    private void DiscardRow(ProposedChange? change)
    {
        if (change is not { NeedsConfirm: true }) return;
        Changes.Remove(change);

        // Attribute the skip to its schedule's tab (uid-first, name fallback) so subset-mode scoping still works.
        var tab = _lastChangeSet?.SheetSummaries.FirstOrDefault(s =>
                (!string.IsNullOrEmpty(change.SourceScheduleUid) && s.ScheduleUid == change.SourceScheduleUid)
                || s.ScheduleName == change.SourceScheduleName)?.SheetTabName ?? "";
        var skip = new SkippedItem
        {
            Reason = "discarded by user",
            Detail = $"{change.Field} ({change.ElementName}) — entered “{change.EnteredValue}”, discarded at the confirm prompt",
            SheetTabName = tab,
        };
        // §15-D honesty: the DISPLAY collection is contractually scoped to selected schedules in subset mode —
        // a discard from a deselected schedule goes to the change-set log only, not the scoped panel/count.
        if (!SkipLogScopedToSelection || ScheduleSelectedFor(change))
            Skipped.Add(skip);                // skip panel (display)
        _lastChangeSet?.Skipped.Add(skip);    // copy-log diagnostic rebuilds read the change-set's list

        RefreshAffectedRows();                // its schedule's tri-state no longer counts the discarded row
        RecomputeAffectedSelectionInfo();
        OnPropertyChanged(nameof(CanApply));  // one fewer pending row — the confirm-or-discard gate may open
    }

    [RelayCommand]
    private void Apply()
    {
        // BUSY GATE (belt for a programmatic invoke; CanApply greys the button): applying while a preview
        // raise is outstanding coalesces the two requests, so the preview never runs and the apply proceeds
        // against the pre-correction change set.
        if (_importBusy)
        {
            ImportStatus = "Still analysing — wait for the current pass to finish, then Apply.";
            return;
        }

        // CONFIRM-OR-DISCARD GATE: Apply is blocked outright while any pending (NeedsConfirm) row remains in a
        // SELECTED schedule — every such row must be confirmed or discarded first (they sit at the TOP of the grid
        // so they can't hide below the fold, the original silent-skip complaint). Pending rows in deselected
        // schedules can never apply, so they don't block (review 2026-07-13). CanApply greys the button for the
        // same condition; this guard is the belt for a programmatic invoke.
        int pending = Changes.Count(PendingBlocksApply);
        if (pending > 0)
        {
            ImportStatus = $"{pending} change(s) still need confirmation — Confirm or Discard each pending row at the top of the list, then Apply.";
            return;
        }

        // SAFETY BELT (code2): apply only rows that are BOTH ticked AND selectable. The gate above already blocks
        // pending rows, but requiring Selectable here means a future select-path that forgets the guard can never
        // silently apply a half-interpreted value.
        var selected = Changes.Where(c => c.Selected && c.Selectable).ToList();

        // Header (column-caption) renames to apply, scoped to the SELECTED schedules (header edits have no per-row
        // checkbox — they ride their schedule's selection). A header edit is included when its schedule is ticked
        // (or there's no per-schedule selection at all, i.e. everything applies).
        var selectedHeaderChanges = SelectedHeaderChanges();

        if (selected.Count == 0 && selectedHeaderChanges.Count == 0)
        {
            ImportStatus = AffectedSchedules.Count > 0
                ? "No schedules selected — tick at least one schedule to import."
                : "Nothing selected to apply.";
            return;
        }

        // CROSS-MODEL GATE: a workbook exported from a different model matches rows by NAME, so values can
        // land on same-named elements that are not the ones the user edited. That must never happen on a
        // warning string alone (the pre-fix behaviour) — require an explicit opt-in per Apply.
        if (_lastChangeSet?.CrossModel == true)
        {
            var proceed = System.Windows.MessageBox.Show(
                "This workbook was exported from a DIFFERENT model than the one you're applying to" +
                (string.IsNullOrEmpty(SelectedProject) ? "" : $" (\"{SelectedProject}\")") + ".\n\n" +
                "Rows were matched by name, so values can land on same-named elements that are not the " +
                "ones you edited in Excel.\n\nApply to this model anyway?",
                "Transom — different source model",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);
            if (proceed != System.Windows.MessageBoxResult.Yes)
            {
                ImportStatus = "Apply cancelled — the workbook came from a different model.";
                return;
            }
        }

        // Clear any stale resolutions from a prior Apply — these ProposedChange objects are shared with the
        // cached change-set, so a cancelled or retried Apply must never route a column by a previous choice.
        // The option-2 follow-up stamps are cleared for the same reason (review 2026-07-13): a cancelled Apply
        // must not leave ticked extra schedules or a Clear/SetValue disposition to fire on the retry.
        foreach (var c in selected)
        {
            c.Resolution = null;
            c.Option2ExtraScheduleUids = new List<string>();
            c.Option2OldValues = OldValueDisposition.Leave;
            c.Option2OldValueText = "";
        }

        var notes = new List<string>();
        bool assist = IsClaudeAssistEnabled;

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

        // OPTION-2 FOLLOW-UPS (user request 2026-07-12; old-values flow reworked per user direction 2026-07-18):
        // after a committed 2a/2b choice, (1) when the build-time scan found OTHER schedules displaying this
        // parameter, offer them as replace-targets too (checklist); then (2) the OLD-values step — a warning that
        // the old data stays in place (Transom can't edit it on group members and never half-applies a column),
        // plus, when Claude Assist is on, an opt-in Claude cleanup (clear / one uniform value) that ApplyNewParam
        // executes all-or-nothing: API-writable elements written directly, grouped members staged for Claude.
        // Both choices are stamped on every change in the column; ApplyNewParam reads them off the column's sample.
        // Closing either dialog without choosing takes the safe default (no extra schedules / leave old values).
        void ResolveOption2Extras(string columnKey, GroupResolutionPrompt p)
        {
            var column = newParamChanges.Where(c => ChangeSet.ColumnKey(c.ParameterId, c.Field) == columnKey).ToList();
            if (column.Count == 0) return;

            var ticked = new List<string>();
            var others = _lastChangeSet?.Option2OtherSchedules.GetValueOrDefault(columnKey);
            if (others is { Count: > 0 } && Option2SchedulesResolver != null)
            {
                var sp = new Option2SchedulesPrompt { Field = p.Field, NewParamName = p.ChosenParamName, Schedules = others };
                ticked = Option2SchedulesResolver(sp) ?? new List<string>();
            }

            var disp = OldValueDisposition.Leave;
            string dispText = "";
            if (Option2OldValuesResolver != null)
            {
                var op = new Option2OldValuesPrompt
                {
                    Field = p.Field,
                    NewParamName = p.ChosenParamName,
                    // "Clear" can't blank a numeric parameter — only offer it for string-storage columns.
                    AllowClear = column[0].IsString,
                    // Assist off → the dialog is a warning only (old values stay in place); on → it offers the
                    // opt-in Claude cleanup that reveals Clear/Replace (user-directed 2026-07-18).
                    AssistEnabled = assist,
                };
                var r = Option2OldValuesResolver(op);
                if (r != null) { disp = r.Choice; dispText = r.NewValue; }
            }

            foreach (var c in column)
            {
                c.Option2ExtraScheduleUids = ticked;
                c.Option2OldValues = disp;
                c.Option2OldValueText = dispText;
            }
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
                    ResolveOption2Extras(key, prompt);
                    break;
                case GroupResolution.NewInstanceParam:
                    AdoptColumn(list, GroupResolution.NewInstanceParam, prompt);
                    ResolveOption2Extras(key, prompt);
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
        SetImportBusy(true);
        _importEvent.Raise();
        MaybeEncourage();
    }

    /// <summary>Best-effort install + register of the Click Helper MCP so Claude has the UI tools for the
    /// Claude-assist / group-dance paths. (The data bridge runs whenever Claude Assist is on in Settings.)</summary>
    private static string EnsureClickHelper()
    {
        try
        {
            Core.ClickHelperRegistration.EnsureInstalled();
            var res = Core.ClickHelperRegistration.Register();
            return res.Updated > 0
                ? "ClickHelper registered with Claude (restart Claude to load it)"
                : "ClickHelper already set up";
        }
        catch { return "ClickHelper setup skipped (couldn't register — toggle Claude Assist off and on in Settings to retry)"; }
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

    /// <summary>Prompts the user for where to save a Claude staging artifact (defaults to the exchange folder).</summary>
    private string? ChooseArtifactPath(string fileName = "transom_group_edits.json")
    {
        var dir = !string.IsNullOrWhiteSpace(ExchangeFolder) ? ExchangeFolder
            : Path.GetDirectoryName(WorkbookPath) ?? "";
        var dlg = new SaveFileDialog
        {
            Title = "Save Claude group-edits artifact",
            Filter = "JSON (*.json)|*.json",
            FileName = fileName,
            InitialDirectory = Directory.Exists(dir) ? dir : "",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>Option-2 old-values cleanup (user-directed 2026-07-18): prompt for a path and stage the grouped
    /// members' OLD-parameter edits for Claude. Returns the status note; on a cancelled save nothing is written
    /// and the old values simply stay in place (they were never touched — staging is the only pending action).</summary>
    private string StageOldValueEditsInteractive(List<OldValueStagedEdit> staged)
    {
        int n = staged.Sum(e => e.MemberUniqueIds.Count);
        var path = ChooseArtifactPath("transom_old_value_edits.json");
        if (path == null)
            return $"{n} old-value group edit(s) not staged (no file chosen — the old values stay in place)";
        var written = StageOldValueEdits(staged, path);
        if (written == null)
            return $"{n} old-value group edit(s) could not be staged (the old values stay in place)";
        ClaudeStagedNotice?.Invoke(written);
        return $"{n} old-value group edit(s) staged for Claude";
    }

    /// <summary>Writes the option-2 OLD-parameter cleanup edits to a JSON file Claude can act on. Same shape and
    /// guide as <see cref="StageGroupEdits"/> — every entry here is a grouped member (the API-writable elements
    /// were already written directly during apply), uniform value per entry, "" meaning blank the parameter.</summary>
    private string? StageOldValueEdits(List<OldValueStagedEdit> staged, string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var edits = staged.Select(e => new
            {
                field = e.Field,
                group = e.GroupName,
                elementName = e.ElementName,
                parameterId = e.ParameterId,
                value = e.Value,
                memberUniqueIds = e.MemberUniqueIds,
            }).ToArray();

            var payload = new
            {
                tool = "Transom",
                kind = "group-edits",
                purpose = "option2-old-values",
                schedule = _lastChangeSet?.ScheduleName ?? "",
                docTitle = SelectedProject,
                docPath = _lastChangeSet?.DocPath ?? "",
                docCreationGuid = _lastChangeSet?.DocCreationGuid ?? "",
                project = SelectedProject,
                note = "These are OLD-parameter CLEANUP edits after a Transom option-2a/2b column conversion: the " +
                       "schedule column now shows a NEW parameter, and the user chose to clear or overwrite the OLD " +
                       "parameter's leftover values. Every entry targets member elements inside Revit MODEL GROUPS " +
                       "(Transom already wrote the ungrouped elements directly). 'value' is the uniform target for " +
                       "every listed member; an EMPTY 'value' means BLANK the parameter. parameterId < 0 = a BUILT-IN " +
                       "param: a direct write is rejected ('Changes to groups are allowed only in group edit mode'), " +
                       "so apply it by driving Revit's 'Edit Group' MODE in the UI with the Transom UI-Assist " +
                       "(ClickHelper) tools (select+zoom+red-locator via API, then enter Edit Group, pick the member, " +
                       "set the param in Properties, Finish, then verify the value + that the group member COUNT is " +
                       "unchanged). parameterId >= 0 = a PROJECT/SHARED param on a group member: writable via the " +
                       "bridge set_parameter after enabling 'vary by group instance'. The write lands on the group " +
                       "DEFINITION (every instance of the group type) — that is intended: Transom verified every " +
                       "instance's value was already migrated to the new parameter before staging. Full step-by-step " +
                       "is in 'Transom - Apply staged edits with Claude.md' in this same folder. Verify every write.",
                edits,
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            // Bundle the how-to next to the JSON, same as StageGroupEdits (the note refers to it by name).
            if (!string.IsNullOrEmpty(dir))
            {
                try { File.WriteAllText(Path.Combine(dir, "Transom - Apply staged edits with Claude.md"), ClaudeGuideMarkdown()); }
                catch { /* the JSON is the essential artifact; don't fail staging if the guide can't be written */ }
            }

            return path;
        }
        catch { return null; }
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
                       "  • parameterId >= 0 = PROJECT/SHARED params: writable per group instance ONLY after 'vary by " +
                       "group instance' is enabled for the parameter — bridge set_parameter writes it once vary is on, " +
                       "and refuses with an actionable error when it is not (the vary flag is a one-way door the " +
                       "bridge never sets itself).\n" +
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

    // _bulkSelecting is REQUIRED here for the same reason BulkSetAffected sets it: each Selected assignment
    // raises the static ProposedChange.SelectionChanged, whose handler refreshes every affected-schedule row,
    // and each row's SelectionState getter re-enumerates the whole Changes collection. Without the guard one
    // click costs O(changes × rows × changes) — ~2.5M enumerations on a 500-change / 10-schedule import, on
    // the UI thread. The refresh runs once at the end instead.
    [RelayCommand]
    private void SelectAll() => BulkSetChanges(true);

    [RelayCommand]
    private void SelectNone() => BulkSetChanges(false);

    private void BulkSetChanges(bool selected)
    {
        _bulkSelecting = true;
        try
        {
            foreach (var c in Changes)
                if (!selected || c.Selectable) c.Selected = selected;
        }
        finally { _bulkSelecting = false; }

        OnAnyChangeSelectionChanged();   // single refresh of rows/helpers after the bulk set
        RefreshChanges();
    }

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
        Changes.Where(c => SameSchedule(c.SourceScheduleUid, c.SourceScheduleName, s.ScheduleUid, s.ScheduleName)).ToList();

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
        // The event can fire off the UI thread in theory; marshal to be safe (cheap, idempotent). Per-cell edits
        // can flip a schedule's tri-state to fully-deselected, which re-scopes the confirm-or-discard gate →
        // CanApply re-evaluates with the rest.
        if (_ui.CheckAccess()) { RefreshAffectedRows(); RecomputeAffectedSelectionInfo(); OnPropertyChanged(nameof(CanApply)); }
        else _ui.BeginInvoke(() => { RefreshAffectedRows(); RecomputeAffectedSelectionInfo(); OnPropertyChanged(nameof(CanApply)); });
    }

    /// <summary>Called after any per-schedule toggle: refresh the changes grid (so the per-cell checkboxes redraw),
    /// re-evaluate every schedule row's tri-state, and recompute the helper line. UX_SPEC §5 C-3/C-5.</summary>
    private void OnAffectedSelectionChanged()
    {
        if (_bulkSelecting) return;   // a bulk select-all/none refreshes once at the end, not per row
        RefreshChanges();
        RefreshAffectedRows();
        RecomputeAffectedSelectionInfo();
        // The confirm-or-discard gate is scoped to SELECTED schedules, so flipping a schedule's checkbox can
        // open or close it — re-evaluate (review 2026-07-13).
        OnPropertyChanged(nameof(CanApply));
    }

    /// <summary>Re-raise the computed tri-state on every affected-schedule row (e.g. after a per-CELL edit changed
    /// which cells are ticked). Subscribed to <see cref="ProposedChange.SelectionChanged"/>. UX_SPEC §5 C-3 cross-update.</summary>
    private void RefreshAffectedRows()
    {
        foreach (var row in AffectedSchedules) row.Refresh();
    }

    /// <summary>Reorder the changes grid so PENDING (NeedsConfirm) rows sit at the TOP — they need an action
    /// (confirm/discard) and must not hide below the fold. Stable: relative order within the pending and
    /// non-pending halves is preserved. No-op when already ordered (avoids a needless grid rebuild).</summary>
    private void MovePendingFirst()
    {
        var ordered = Changes.OrderByDescending(c => c.NeedsConfirm).ToList();
        if (ordered.SequenceEqual(Changes)) return;
        Changes.Clear();
        foreach (var c in ordered) Changes.Add(c);
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
        RecomputeOptionAffected();
    }

    /// <summary>Preview split (user request 2026-07-19): rebuild the read-only "Schedules that may change based on
    /// options selected" list from the change set's <see cref="ChangeSet.OptionDependents"/>, scoped to driving
    /// schedules that are still selected — deselecting a parent drops the option edges it drives, mirroring the
    /// §9.5 dependent-subtree cascade. Distinct by schedule name (a schedule can be reachable via several parents).
    /// Piggybacks on RecomputeAffectedSelectionInfo so every selection-change path refreshes it.</summary>
    private void RecomputeOptionAffected()
    {
        OptionAffected.Clear();
        var deps = _lastChangeSet?.OptionDependents;
        if (deps == null || deps.Count == 0) { HasOptionAffected = false; return; }
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in AffectedSchedules)
        {
            if (row.SelectionState == false) continue;
            if (deps.TryGetValue(row.ScheduleUid, out var list) || deps.TryGetValue(row.ScheduleName, out list))
                foreach (var d in list) names.Add(d.ScheduleName);
        }
        foreach (var n in names) OptionAffected.Add(new DependentRow($"↳ {n}"));
        HasOptionAffected = OptionAffected.Count > 0;
    }

    private void ShowPreview(ChangeSet cs)
    {
        _lastChangeSet = cs;
        InTabPickStep = false;   // §16: analysis returned → the pre-analysis tab picker is done
        _diagnosticLog = cs.DiagnosticLog;
        Changes.Clear();
        Skipped.Clear();
        foreach (var c in cs.Changes) Changes.Add(c);
        // PENDING (NeedsConfirm) rows go to the TOP of the grid — they need an action (confirm/discard) and used
        // to hide below the fold, where they were silently skipped on Apply. MovePendingFirst is the ONE home of
        // that ordering rule (stable; no-op when already ordered).
        MovePendingFirst();
        // After the fill, not before: CanApply now also reads the pending rows just added (confirm-or-discard gate).
        OnPropertyChanged(nameof(CanApply));
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
            // Dependents are keyed by the changes' SourceScheduleUid with a name FALLBACK when the workbook
            // carried no uid — look up both ways, same as RecomputeOptionAffected, or those edges never show.
            if (!cs.Dependents.TryGetValue(s.ScheduleUid, out var deps)) cs.Dependents.TryGetValue(s.ScheduleName, out deps);
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
            var row = AffectedSchedules.FirstOrDefault(r => SameSchedule(uid, name, r.ScheduleUid, r.ScheduleName));
            return row == null || row.SelectionState != false;   // null (mixed) or true ⇒ selected
        }

        // CHANGE 2 §3b: the tab a conflict belongs to (so its skip is attributable for the skip-log scoping).
        string ConflictTab(string uid, string name) =>
            cs.SheetSummaries.FirstOrDefault(s => SameSchedule(uid, name, s.ScheduleUid, s.ScheduleName))?.SheetTabName ?? "";

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
        // its parent reflects it (the change is now selectable + selected by default). A resolved pick that came
        // back PENDING (reformat/unreadable) was appended at the bottom — move it to the top with the rest, and
        // re-evaluate the confirm-or-discard gate.
        MovePendingFirst();
        RefreshAffectedRows();
        RecomputeAffectedSelectionInfo();
        OnPropertyChanged(nameof(CanApply));

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
                var row = AffectedSchedules.FirstOrDefault(r => SameSchedule(ss.ScheduleUid, ss.ScheduleName, r.ScheduleUid, r.ScheduleName));
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
        // Apply filters on Selected && Selectable; the SELECTED message reports what Apply will actually do —
        // a pre-ticked PENDING row is not applyable until confirmed, so it must not inflate this count
        // (review 2026-07-13: the old Selected-only count claimed 15 when Apply would write 10).
        int applyableSelected = Changes.Count(c => c.Selected && c.Selectable);
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
            var row = AffectedSchedules.FirstOrDefault(r => SameSchedule(ss.ScheduleUid, ss.ScheduleName, r.ScheduleUid, r.ScheduleName));
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
        // Confirm-or-discard gate: say up-front how many rows still block Apply (they're at the top of the grid).
        // Same selected-schedule scope as the gate itself — a deselected schedule's pending rows don't block.
        int pendingCount = Changes.Count(PendingBlocksApply);
        string selectedMsg = (subset ? $"Selected ({selName}): " : "")
                       + $"{applyableSelected} change(s)"
                       + (pendingCount > 0 ? $"  —  ⚠ {pendingCount} need confirmation (top of the list): Confirm or Discard each before Apply" : "")
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

    partial void OnIsClaudeAssistEnabledChanged(bool value)
    {
        _settings.ClaudeAssistEnabled = value;
        _settings.Save();
        if (value)
        {
            _ = EnableClaudeAssistAsync();
        }
        else
        {
            RestartNotice = "";
            BridgeRuntime.Stop();
            _ = RefreshClaudeStatusAsync();
        }
    }

    /// <summary>
    ///     Toggle-ON path: ensure setup (idempotent — shim + both Claude registrations), start the bridge,
    ///     and surface the one-time restart-Claude notice when something was newly registered. Registration is
    ///     file IO, so it runs off the UI thread; the switch is disabled meanwhile (ClaudeToggleBusy).
    /// </summary>
    private async Task EnableClaudeAssistAsync()
    {
        ClaudeToggleBusy = true;
        try
        {
            // "On = it works": staging silently requires an exchange folder, so default an empty one.
            if (string.IsNullOrWhiteSpace(ExchangeFolder))
                ExchangeFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Transom Exchange");

            var setup = await Task.Run(() => ClaudeSetup.EnsureAll(_settings));
            var startError = setup.ComponentsMissing ? null : await Task.Run(() => BridgeRuntime.Start(BridgePort));

            _ui.Invoke(() =>
            {
                if (setup.Failed || startError != null)
                {
                    Views.ReportDialog.Show("Transom — Claude Assist",
                        setup.ComponentsMissing
                            ? "Couldn't install the bundled Claude components from the add-in folder " +
                              "(Transom.McpShim.exe / Transom.ClickHelper.exe / Transom.ClickHelper.Mcp.exe). " +
                              "Reinstall Transom, or report this."
                            : startError ?? "Setup finished with issues — see details.",
                        setup.Details + (startError is null ? "" : "\n\n--- bridge ---\n" + startError),
                        isError: true);
                }
                RestartNotice = setup.NeedsClaudeRestart
                    ? "Setup complete — restart Claude Code to load Transom's tools; it connects automatically after that."
                    : "";
            });
        }
        finally
        {
            _ui.Invoke(() => ClaudeToggleBusy = false);
            await RefreshClaudeStatusAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshClaudeStatus() => await RefreshClaudeStatusAsync();

    private async Task RefreshClaudeStatusAsync()
    {
        var status = await Task.Run(() => ClaudeStatus.Capture(BridgePort));
        _ui.Invoke(() =>
        {
            ClaudeNotSetUp = !status.IsSetUp && !IsClaudeAssistEnabled;
            ClaudeStatusRows.Clear();
            if (!ClaudeNotSetUp)
                foreach (var row in status.Rows()) ClaudeStatusRows.Add(row);
            ClaudeStatusFooter = ClaudeNotSetUp
                ? ""
                : (string.IsNullOrEmpty(SelectedProject) ? "" : $"Active model: {SelectedProject}   ·   ") +
                  $"Transom {AppInfo.Version}";
        });
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
        // The port is baked into the Claude registration AND the live listener, so with Claude Assist on a
        // port change must re-register + restart the bridge (the old flow relied on the user re-clicking
        // "Set up Claude"). The XAML binds this with UpdateSourceTrigger=LostFocus so it fires once per edit,
        // not per keystroke.
        if (IsClaudeAssistEnabled)
        {
            // Report failures the way the Assist toggle does. This path used to discard the setup Result,
            // the Start() error string AND any exception (`_ =` plus a ContinueWith that never observed the
            // antecedent) — so typing a port already in use left the bridge stopped while the UI said
            // nothing, and the only symptom was Claude tools quietly not working.
            _ = Task.Run(async () =>
            {
                string? failure = null;
                try
                {
                    ClaudeSetup.EnsureAll(_settings);
                    BridgeRuntime.Stop();
                    var startError = BridgeRuntime.Start(value);
                    if (!string.IsNullOrEmpty(startError)) failure = startError;
                }
                catch (Exception ex) { failure = ex.Message; }

                if (failure != null)
                    _ui.Invoke(() => ClaudeStatusFooter =
                        $"Bridge could not restart on port {value}: {failure}  ·  pick a different port.");

                await RefreshClaudeStatusAsync();
            });
        }
        else
        {
            _ = RefreshClaudeStatusAsync();
        }
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
        // Don't interrupt a Claude-Assist session: this is a MODAL TaskDialog, so while a Claude session drives
        // Transom over the loopback bridge it blocks the call (Revit not idle) and forces an extra dismissal
        // mid-sequence. A human running with Claude Assist off still gets the pep talk.
        if (IsClaudeAssistEnabled) return;
        var msg = Encouragement.Maybe();
        if (msg != null)
            try { TaskDialog.Show("Transom", msg); } catch { /* never let a pep talk break anything */ }
    }

    /// <summary>The how-to markdown explaining how to apply the staged group-edits artifact with Claude.
    /// Written automatically next to the group-edits JSON whenever Claude-Assist stages built-in group edits
    /// (see <see cref="StageGroupEdits"/>).</summary>
    // --- Claude Skills tab (user request 2026-07-18): a library of reusable Claude workflow .md files ---

    /// <summary>The skill library rows (every .md in <see cref="SkillLibrary.Dir"/>).</summary>
    public ObservableCollection<SkillEntry> Skills { get; } = new();

    [ObservableProperty] private SkillEntry? _selectedSkill;

    /// <summary>Transient result line for the last Stage/Import/Remove ("Copied to clipboard…").</summary>
    [ObservableProperty] private string _skillStatus = "";

    /// <summary>The detail pane under the list — the highlighted skill's frontmatter description.</summary>
    [ObservableProperty] private string _selectedSkillDescription = "Select a skill to see its description.";

    /// <summary>Shown under the list so the user knows where skill files live (Claude saves new ones here too).</summary>
    public string SkillsFolder => SkillLibrary.Dir;

    partial void OnSelectedSkillChanged(SkillEntry? value) =>
        SelectedSkillDescription = value == null
            ? "Select a skill to see its description."
            : string.IsNullOrWhiteSpace(value.Description)
                ? "(no description — add a description: line to the skill's frontmatter so it shows here)"
                : value.Description;

    /// <summary>Re-reads the library folder. Runs on tab entry and after Import/Remove; keeps the selection
    /// when the selected file still exists.</summary>
    [RelayCommand]
    private void RefreshSkills()
    {
        var keep = SelectedSkill?.Path;
        Skills.Clear();
        foreach (var s in SkillLibrary.List()) Skills.Add(s);
        SelectedSkill = Skills.FirstOrDefault(s => s.Path == keep) ?? Skills.FirstOrDefault();
    }

    /// <summary>Stage = copy the skill's full path to the clipboard, ready to paste into a Claude Code session
    /// (Claude reads the file from disk — no copy is made).</summary>
    [RelayCommand]
    private void StageSkill()
    {
        if (SelectedSkill == null) { SkillStatus = "Select a skill first."; return; }
        try
        {
            System.Windows.Clipboard.SetText(SelectedSkill.Path);
            SkillStatus = $"Copied to clipboard: {SelectedSkill.FileName} — paste it into your Claude Code session.";
        }
        catch { SkillStatus = "Couldn't copy to the clipboard — try again."; }
    }

    /// <summary>Import = copy a skill .md from anywhere into the library (never overwrites; collisions get a
    /// numbered name).</summary>
    [RelayCommand]
    private void ImportSkill()
    {
        var dlg = new OpenFileDialog { Title = "Import a Claude skill", Filter = "Skill markdown (*.md)|*.md" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var dst = SkillLibrary.Import(dlg.FileName);
            RefreshSkills();
            SelectedSkill = Skills.FirstOrDefault(s => s.Path == dst) ?? SelectedSkill;
            SkillStatus = $"Imported {Path.GetFileName(dst)}.";
        }
        catch (Exception ex) { SkillStatus = "Import failed: " + ex.Message; }
    }

    /// <summary>Remove = delete the skill file, after a confirm (a skill may be the user's only copy).</summary>
    [RelayCommand]
    private void RemoveSkill()
    {
        if (SelectedSkill == null) { SkillStatus = "Select a skill first."; return; }
        var res = System.Windows.MessageBox.Show(
            $"Delete “{SelectedSkill.FileName}” from the skill library?\n\nThis deletes the file and can't be undone.",
            "Transom — Remove skill", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (res != System.Windows.MessageBoxResult.Yes) return;
        try
        {
            SkillLibrary.Remove(SelectedSkill.Path);
            SkillStatus = $"Removed {SelectedSkill.FileName}.";
            RefreshSkills();
        }
        catch (Exception ex) { SkillStatus = "Remove failed: " + ex.Message; }
    }

    private string ClaudeGuideMarkdown() => @"# Transom — Applying staged BUILT-IN group edits with Claude

This file sits **next to the staged group-edits JSON** (same folder). When you import with **Claude Assist**,
Transom stages **built-in parameter edits on elements inside Revit MODEL GROUPS** (plus any ungrouped instances
of the same column) into a group-edits JSON in this **same folder**, and drops these instructions alongside it.
A built-in param (Comments, Finish, Mark, …) on a GROUP member **can't be written by the API** — a direct write
is rejected with *""Changes to groups are allowed only in group edit mode.""* So you apply it the way a person
would: drive Revit's **""Edit Group"" mode** in the UI with the **Transom UI-Assist (transom-ui-assist)** tools.

> Project/shared parameters on grouped elements are NOT here — Transom applies those itself (it enables
> ""vary by group instance"" and writes them). This file is only the built-in edits, which need the UI path below.

" + GuideTwoPhasesMd + @"
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

## 4. Apply each built-in group edit (per member element)

" + GuidePhasesMd + @"
## 5. Report
Per group type / member: applied / skipped (with reason) / conflicts (per-instance divergence → point to
option 2b). Note this changed the group **definition**, so all instances of the type share the new value.

" + GuideWorksharedCloseMd + @"
" + GuideSkillsMd;

    // --- sections shared VERBATIM between the staged-edits guide (ClaudeGuideMarkdown) and the standalone
    // Claude Assist guide (ClaudeAssistStandaloneMarkdown) so the two can't drift apart ---

    private const string GuideTwoPhasesMd = @"## Two phases: API vs UI (the load-bearing rule)
You work in two tool sets, in distinct phases:
- the **Revit API** (the `transom` bridge / `execute_revit_code`) — for selection, view setup, the RED colour
  locator, and verification. Use it BEFORE you enter Edit Group and AFTER you Finish.
- the **transom-ui-assist** UI tools (`revit_*`) — for everything INSIDE Edit Group mode.

**Inside Edit Group mode the Revit API is DEAD** (it times out): you cannot select, read, write, or verify a
member via the API while editing the group. So all API work happens BEFORE entering and AFTER finishing — never
during. Everything between (enter edit mode, pick the element, open Properties, set the value, finish) is
**screenshot + `revit_*` clicks/keys/scrolls**. **Take a `revit_screenshot(screen:true)` after almost every
click/type/scroll, READ it, and confirm the expected state before the next action** — a wrong-element pick, a
mis-focused field, and a missed button are all SILENT.

**Run Claude Code with bypass permissions** for the whole UI phase (e.g. `claude --dangerously-skip-permissions`,
or the client's session-wide ""don't ask again"" approval). A tool-approval prompt popping in the Claude window
mid-sequence steals Windows focus from Revit, so the next `revit_*` click lands on the wrong window and misses
SILENTLY — leaving a half-finished Edit Group. Bypass only relaxes Claude's own tool prompts on this machine; the
loopback bridge is unchanged (loopback-only, per-user, session-token gated).
";

    private const string GuidePhasesMd = @"### Phase A — API setup (use the Revit API via `execute_revit_code`; do this BEFORE entering Edit Group)
1. **revit_tile** if Revit isn't already tiled side-by-side. The right split is **Revit at ~2/3 of the
   screen, Claude at 1/3** (user-confirmed: half/half cramps the Revit canvas; Claude only needs a chat
   column) — revit_tile does this by default; pass `revitFrac` only to deviate.
   **Confirm the geometry in revit_tile's reply** (e.g. `revit=0,0,1280,1032` on a 1920px screen). Half/half
   is not merely cramped: Revit's ribbon reflows to window width, and at ~960px the tab strip **overflows and
   silently hides the right-hand tabs, including `Transom` itself** — the add-in then looks like it failed to
   load when it is only off-screen behind the `>>` chevron. If a tab or ribbon button seems missing, re-check
   the width BEFORE concluding anything is broken. A 50/50 split coming back from a tool that should default
   to 2/3 means the `%LocalAppData%\Transom\mcp\` helper copy is **stale** — see the trap note below.
2. **Open a view where the member is visible** — if the active view doesn't contain it, activate/open one that
   does (a plan/elevation/section that shows it).
3. **Select + zoom via API:** `Selection.SetElementIds([id])`, `ShowElements([id])`. `revit_screenshot(screen:true)`;
   confirm the element is visible (Revit highlights it). **If it's not visible, adjust the view/zoom and try
   again** before proceeding.
4. **Thin Lines ON** (once per session — now that model geometry is on screen): `revit_keys(shortcut:""tl"", x, y)`
   at a canvas point over the visible geometry, then `revit_screenshot(screen:true)` to confirm crisp single
   lines (lineweights off make overlapping geometry easier to pick). Skip if already done this session.
5. **Red locator via API:** in a transaction, `view.SetElementOverrides(id, ogs)` with a RED projection line +
   solid surface fill. Commit, deselect (`SetElementIds([])`), `revit_screenshot(screen:true)` → confirm you
   can SEE the red element. (The override survives into Edit Group mode and scales with zoom.)

### Phase B — UI automation, INSIDE Edit Group (the Revit API is UNAVAILABLE — use ONLY the transom-ui-assist `revit_*` tools)
6. **Enter Edit Group:** select the GROUP instance via API (`SetElementIds([groupInstanceId])`), then
   **revit_edit_group**. (Fallback if it reports the button wasn't found: `revit_find(text:""Edit Group"")` →
   `revit_click_by_id`.) A screenshot / `revit_list_dialogs` shows the Edit Group toolbar
   [add, remove, attach, finish, cancel]. **From here the API is dead.**
7. **Pick the member by its red colour:** `revit_screenshot(screen:true)`, read it, `revit_click_xy` the red
   element. OVERLAP: if a wrong/overlapping element is selected, `revit_scroll(x, y, +notches)` to ZOOM IN
   until they separate, then click the red body. **Screenshot + read the Properties header / status bar to
   CONFIRM the right element before editing** — a wrong pick is silent.
8. **Reveal the parameter — it may be in either place:**
   - an **instance** param → the **Properties palette** (built-ins like Comments live under *Identity Data*);
     `revit_scroll` the palette to it.
   - a **type** param → click **Edit Type** to open the Type Properties dialog (`revit_find` / `revit_click_by_id`),
     `revit_scroll` to the parameter there. (Editing a type property changes the type for ALL its instances.)
9. **Set the value — by the control's kind:**
   - **text / number cell** → click the value cell, `revit_type(text:""VALUE"", x, y, enter:true)` (one atomic
     click+type+commit; field empty first).
   - **dropdown / combo** → click it to open, `revit_screenshot`, `revit_click_xy` the option.
   - **checkbox / Yes-No / toggle** → `revit_click_xy` it to the desired state.
   Screenshot → confirm the value shows. If a Properties **Apply** button is present, click it (value commits,
   Apply greys out).
10. **Close + return:** if you opened the Edit Type dialog, click **OK** to close it; dismiss any other dialog
    (`revit_list_dialogs` → `revit_click_dialog`); click back into the live view.
11. **Finish:** **revit_finish_group** (fallback: `revit_find(text:""Finish"")` → `revit_click_by_id`).
    `revit_list_dialogs` shows **0 open = committed**. To ABORT without committing: **revit_cancel_group**.

### Phase C — API verify & cleanup (the API is available again)
12. Per member: re-read the parameter (must equal `value`), confirm its `GroupId` (still grouped), and the
    group's **member COUNT is UNCHANGED** (nothing lost or un-excluded).
13. **Remove the red override:** `view.SetElementOverrides(id, new OverrideGraphicSettings())` in a transaction;
    `revit_screenshot(screen:true)` → red gone. If a write didn't take or the count changed, report it — never
    leave a half-applied state.
";

    private const string GuideWorksharedCloseMd = @"## Workshared close (if you ever close the model)
NEVER click ""synchronize"" or ""save"" on a close dialog. LOOP on `revit_list_dialogs`: issue ONE
`revit_click_dialog` per call, first match wins — ""do not save"" (Changes Not Saved) → ""keep ownership""
(Editable Elements / Close Without Saving). Never bare-dismiss (it defaults to cancel/close → aborts the
close). The user controls sync.
";

    // Shared by both guides so every staging/connect file teaches the same skill format (the Claude Skills
    // tab parses the frontmatter to show the description — user request 2026-07-18, item 6 of the brief).
    private const string GuideSkillsMd = @"## Saving a reusable skill (Transom's Claude Skills tab)
When the user asks you to save a workflow for reuse — or you finish a sequence worth repeating — write it as
a **skill file** into Transom's skill library folder: `%LocalAppData%\Transom\skills\<short-kebab-name>.md`.
The Transom window's **Claude Skills** tab lists every `.md` in that folder; the user can stage one later by
copying its path back into a Claude Code session.

**Required format** (Transom parses this to display the skill, so follow it exactly): the file starts with a
YAML frontmatter block, then the workflow body:

```
---
name: short-kebab-case-name
description: One sentence saying what the skill does and when to use it.
---

(the workflow body)
```

The `description:` line is **REQUIRED** — it is what Transom shows in the tab's detail pane, so make it say
what the skill does and when to use it. The body is the step-by-step instructions a future Claude session
needs to repeat the workflow: which tools to call (`transom` bridge vs `transom-ui-assist` UI), in what
order, what to verify after each step, and any safety rules. Write it self-contained — the future session
has Transom's tools but none of today's conversation.
";

    /// <summary>Settings-tab export of the standalone Claude Assist guide — the same operating instructions
    /// that accompany a staged edit, reframed so Claude Code can connect to and drive Revit with NO staged
    /// edit present (the "just let me use Claude in Revit" path).</summary>
    [RelayCommand]
    private void ExportClaudeAssistGuide()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export the Claude Assist guide",
            Filter = "Markdown (*.md)|*.md",
            FileName = "Transom - Claude Assist.md",
            InitialDirectory = Directory.Exists(ExchangeFolder) ? ExchangeFolder : "",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, ClaudeAssistStandaloneMarkdown());
        }
        catch (Exception ex)
        {
            Views.ReportDialog.Show("Transom — Claude Assist",
                "Couldn't write the guide file.", ex.Message, isError: true);
        }
    }

    private string ClaudeAssistStandaloneMarkdown() => $@"# Transom — Connect Claude Code with Revit

The user dropped this file into the session because they want to work with their **live Revit model** through
**Transom** (a Revit add-in). There is **no staged edit** — this is the general ""drive Revit with Claude""
path. Transom registered two local MCP servers with Claude Code when the user turned **Claude Assist** on
(Schedule Hub → Settings tab):

- **`transom`** — the data bridge (loopback only, 127.0.0.1:{BridgePort}). Read schedules and elements, set
  parameters, create views/sheets/levels, tag, color, export images, and run arbitrary Revit API C# via
  `execute_revit_code`. This is your primary tool set — almost everything happens here.
- **`transom-ui-assist`** — UI automation (`revit_*` tools) for the few Revit commands that have NO API:
  Edit Group mode, modal dialogs, and Properties-palette edits on group members. Screenshot-driven clicking —
  slower and riskier than the API, so use it only when the API path is closed.

## 1. Connect and verify (do this first)
1. Call `mcp__transom__status`. A reply like `{{""ok"":true, …, ""doc"":""<title>""}}` means you're connected —
   tell the user which document you're on, and offer `list_schedules` to show what's in it.
2. If the `transom` tools are missing from this session: Claude Assist is off in Transom's Settings tab, or
   Claude Code wasn't restarted after setup (MCP servers load only at startup). Tell the user which.
3. If `status` errors or times out: the bridge isn't listening — the user should check the Settings status
   panel (""Bridge listening 127.0.0.1:{BridgePort}"") and that a Revit document is open.

## 2. If it isn't connecting — diagnose layer by layer
The user may have dropped this file in precisely BECAUSE the connection isn't working. (Note one known false
negative: Transom's status panel has a ""Claude app running"" row that is process-name based — it can show
red while this very session is connected. Trust a working `status` call over that row, and tell the user so.)
How the plumbing fits together, so you can test each link:

- **The add-in**: Transom lives on Revit's **Transom** ribbon tab → **Schedule Hub** button. The **Claude
  Assist** toggle, its status panel, and the bridge port (under Advanced) are on the Schedule Hub window's
  **Settings** tab.
- **The bridge**: while Claude Assist is ON and a document is open, Transom self-hosts HTTP inside Revit at
  `127.0.0.1:{BridgePort}` (loopback only). `GET /status` needs no auth — probing
  `http://127.0.0.1:{BridgePort}/status` with curl from a shell is a fair bridge test. Every other call
  needs the per-session token, which rotates each time the bridge starts.
- **The shim**: Claude Code never talks to the bridge directly — it spawns
  `%LocalAppData%\Transom\mcp\Transom.McpShim.exe` (a stdio MCP server), which forwards to the bridge and
  adds the token it reads from `%LocalAppData%\Transom\bridge.token`.
- **Registration**: turning Claude Assist on merges two entries into the `mcpServers` section of the Claude
  Code user config at `%UserProfile%\.claude.json`: `transom` (the shim, args `--port {BridgePort}`) and
  `transom-ui-assist` (`Transom.ClickHelper.Mcp.exe`, the UI automation). Claude Code loads MCP servers only
  at STARTUP — after first-time setup or a port change, Claude Code must be restarted.
- **Claude Code settings**: nothing special is needed for bridge reads/writes. For UI-assist sequences
  (Edit Group, below), run with bypass permissions (e.g. `claude --dangerously-skip-permissions`) so
  approval prompts can't steal Windows focus from Revit mid-click.

Walk the chain in order:
1. **No `transom` tools in this session?** Check `%UserProfile%\.claude.json` has both entries above pointing
   at existing exes under `%LocalAppData%\Transom\mcp\` (`claude mcp list` also shows what loaded). Missing
   or wrong → toggle Claude Assist OFF then ON in Transom Settings, then restart Claude Code.
2. **Tools present but `status` fails?** Confirm Revit is open with a document and the toggle is ON — the
   status panel should show ""Bridge listening 127.0.0.1:{BridgePort}"". Probe `/status` with curl to split
   shim problems from bridge problems: curl OK + tool failing = shim/registration side; curl dead = bridge
   side (toggle OFF/ON restarts it). Also confirm the port in `.claude.json` matches the Settings port.
3. **Reads work but writes return 401?** Stale session token — toggle Claude Assist OFF then ON (rewrites
   `bridge.token`) and retry.
4. **Still stuck?** Research is fair game: Claude Code's MCP docs (https://code.claude.com/docs, MCP
   section), `claude mcp get transom`, and the Transom repository (github.com/Dave5264/transom-revit) for
   README and issues. Report to the user exactly which layer failed and what you changed.

## 3. Ground rules (safety)
- **Confirm the document before any write** — `status` reports the open doc; if it isn't the model the user
  means, STOP and ask.
- **Verify every write** by reading the value back; report failures, never assume.
- **If the model is workshared, NEVER Synchronize with Central and NEVER Save** — the user controls sync.
- Wrap `execute_revit_code` model changes in a Transaction; keep one logical change per transaction so a
  failure rolls back cleanly.

" + GuideTwoPhasesMd + @"
## Editing a built-in parameter on a GROUP member (the one write the API refuses)
A built-in param (Comments, Finish, Mark, …) on an element inside a Revit MODEL GROUP can't be written by the
API — the write is rejected with *""Changes to groups are allowed only in group edit mode.""* Apply it the way
a person would, by driving Revit's **Edit Group** mode with the transom-ui-assist tools:

" + GuidePhasesMd + @"
Editing a member inside Edit Group edits the group **definition** — every instance of that group type gets the
same value. That is correct and durable; say so in your report. Per-instance DIVERGENT built-in values are not
possible while grouped and need an instance shared parameter instead (Transom import option 2b).

" + GuideWorksharedCloseMd + @"
" + GuideSkillsMd;

    /// <summary>Settings-tab "Show me what you can do" export — a scripted demo build (a 12'x10' shed with a
    /// gable roof, door, window, and a section-boxed 3D reveal) plus the full connection guide in one file,
    /// so a brand-new user can watch Claude work without having to invent a first command.</summary>
    [RelayCommand]
    private void ExportShowcaseGuide()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export the Transom demo file",
            Filter = "Markdown (*.md)|*.md",
            FileName = "Transom - Show me what you can do.md",
            InitialDirectory = Directory.Exists(ExchangeFolder) ? ExchangeFolder : "",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, ShowcaseMarkdown());
        }
        catch (Exception ex)
        {
            Views.ReportDialog.Show("Transom — Show me what you can do",
                "Couldn't write the demo file.", ex.Message, isError: true);
        }
    }

    private string ShowcaseMarkdown() => ShowcaseIntroMd + ShowcaseExerciseMd + @"
---

*Everything below is the standard Transom connection guide — use it for the connection check above and as
the reference for all further work in this session.*

" + ClaudeAssistStandaloneMarkdown();

    private const string ShowcaseIntroMd = @"# Transom — Show me what you can do (guided demo)

The user exported this from Transom's Settings tab because they're new to driving Revit with Claude and want
to SEE a capability demo instead of having to invent a first command. Follow this contract, in order:

1. **Connect first.** Run the connection check from the guide at the bottom of this file (call
   `mcp__transom__status`). If the tools or the bridge aren't up, walk the user through the guide's
   layer-by-layer diagnosis until they are — getting them connected is part of the job.
2. **Check the document.** This demo CREATES elements and expects a fresh throwaway project (**File ▸ New**
   in Revit). If `status` reports what looks like a real model (a saved path, a project-like title), warn
   the user and get an explicit OK before touching it.
3. **Then wait.** Say, roughly: ""I'm ready to show you what I can do — just give the word."" Do NOT build
   anything until the user says go.
4. **On the word,** run the demo script below step by step, narrating briefly (a line per step, no
   lectures). If a step errors, diagnose and adapt — the script is a baseline, not a cage.
5. **Afterwards,** summarize what was built in plain terms and offer natural next steps (add a floor,
   dimension the plan, tag the openings, steepen the pitch, export an image) so the user sees where to go
   from here.

";

    private const string ShowcaseExerciseMd = @"## The demo script: a 12' × 10' shed with a gable roof
Run each step as its own `execute_revit_code` call — separate transactions, so a late failure can't undo
earlier steps. All lengths are in FEET (the Revit API's internal length unit). Carry forward the element
ids each step returns; the placeholders in later steps name them.

### Step 1 — four walls (two flat eave walls, two gable-profile ends)
There is no API to attach a wall top to a roof, so the gable-end walls are created from an explicit
pentagon profile that already rises to the ridge — no triangular gap under the roof later:

```csharp
var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
    .OrderBy(l => l.Elevation).First();
var wtId = doc.GetDefaultElementTypeId(ElementTypeGroup.WallType);
double W = 12, D = 10, H = 8, ridgeZ = 10.5; // 6/12 pitch: 8 + (10/2)*0.5
var w1 = Wall.Create(doc, Line.CreateBound(new XYZ(0,0,0), new XYZ(W,0,0)), wtId, level.Id, H, 0, false, false);
var w2 = Wall.Create(doc, Line.CreateBound(new XYZ(0,D,0), new XYZ(W,D,0)), wtId, level.Id, H, 0, false, false);
Wall MakeGable(double x)
{
    var p = new List<Curve> {
        Line.CreateBound(new XYZ(x,0,0), new XYZ(x,D,0)),
        Line.CreateBound(new XYZ(x,D,0), new XYZ(x,D,H)),
        Line.CreateBound(new XYZ(x,D,H), new XYZ(x,D/2,ridgeZ)),
        Line.CreateBound(new XYZ(x,D/2,ridgeZ), new XYZ(x,0,H)),
        Line.CreateBound(new XYZ(x,0,H), new XYZ(x,0,0)) };
    return Wall.Create(doc, p, wtId, level.Id, false);
}
var w3 = MakeGable(0); var w4 = MakeGable(W);
return new[] { w1.Id.Value, w2.Id.Value, w3.Id.Value, w4.Id.Value };
```

### Step 2 — gable roof with a 1' overhang on all sides

```csharp
var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
    .OrderBy(l => l.Elevation).First();
var roofType = (RoofType)doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.RoofType));
double o = 1, W = 12, D = 10;
var ca = new CurveArray();
ca.Append(Line.CreateBound(new XYZ(-o,-o,0),   new XYZ(W+o,-o,0)));
ca.Append(Line.CreateBound(new XYZ(W+o,-o,0),  new XYZ(W+o,D+o,0)));
ca.Append(Line.CreateBound(new XYZ(W+o,D+o,0), new XYZ(-o,D+o,0)));
ca.Append(Line.CreateBound(new XYZ(-o,D+o,0),  new XYZ(-o,-o,0)));
ModelCurveArray mapping = new ModelCurveArray();
var roof = doc.Create.NewFootPrintRoof(ca, level, roofType, out mapping);
// gable: slope ONLY the two X-parallel (eave) edges -> the ridge runs along X
foreach (ModelCurve mc in mapping)
{
    var ln = mc.GeometryCurve as Line;
    bool eave = ln != null && Math.Abs(ln.Direction.Y) < 0.01;
    roof.set_DefinesSlope(mc, eave);
    if (eave) roof.set_SlopeAngle(mc, 0.5); // 6/12
}
// the base offset applies at the eave edge, which is 1' OUTBOARD of the wall line:
// 7.5 + 0.5*1 = 8.0 at the wall tops, ridge at 7.5 + 0.5*6 = 10.5 = the gable apex
roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM).Set(7.5);
return roof.Id.Value;
```

### Step 3 — door + window, hosted in the front wall
Templates differ, so pick family symbols by category and preference — never by hardcoded id. If a category
has no symbols at all (some blank templates ship empty), tell the user and skip that opening rather than
failing the demo.

```csharp
var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
    .OrderBy(l => l.Elevation).First();
var front = (Wall)doc.GetElement(new ElementId(W1_ID)); // first id returned by step 1
FamilySymbol Pick(BuiltInCategory cat, string prefer)
{
    var syms = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(cat)
        .Cast<FamilySymbol>().ToList();
    return syms.FirstOrDefault(s => s.Family.Name.Contains(prefer)) ?? syms.FirstOrDefault();
}
var doorSym = Pick(BuiltInCategory.OST_Doors, ""Exterior"");
var winSym  = Pick(BuiltInCategory.OST_Windows, ""Double-Hung"");
var placed = new List<long>();
if (doorSym != null)
{
    if (!doorSym.IsActive) doorSym.Activate();
    placed.Add(doc.Create.NewFamilyInstance(new XYZ(3.5,0,0), doorSym, front, level,
        Autodesk.Revit.DB.Structure.StructuralType.NonStructural).Id.Value);
}
if (winSym != null)
{
    if (!winSym.IsActive) winSym.Activate();
    var win = doc.Create.NewFamilyInstance(new XYZ(8.5,0,0), winSym, front, level,
        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
    win.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM).Set(3.0); // 3' sill
    placed.Add(win.Id.Value);
}
return placed;
```

### Step 4 — a 3D view with a tight section box around the shed

```csharp
var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
    .First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
var v = View3D.CreateIsometric(doc, vft.Id);
try { v.Name = ""Transom Demo 3D""; } catch { /* name taken — keep the default */ }
v.DetailLevel = ViewDetailLevel.Fine;
v.DisplayStyle = DisplayStyle.Shading;
var ids = new[] { ALL_IDS_FROM_STEPS_1_TO_3 }.Select(i => new ElementId(i)).ToList();
XYZ min = null, max = null;
foreach (var id in ids)
{
    var bb = doc.GetElement(id).get_BoundingBox(null);
    if (bb == null) continue;
    min = min == null ? bb.Min : new XYZ(Math.Min(min.X, bb.Min.X), Math.Min(min.Y, bb.Min.Y), Math.Min(min.Z, bb.Min.Z));
    max = max == null ? bb.Max : new XYZ(Math.Max(max.X, bb.Max.X), Math.Max(max.Y, bb.Max.Y), Math.Max(max.Z, bb.Max.Z));
}
v.SetSectionBox(new BoundingBoxXYZ { Min = min - new XYZ(1,1,1), Max = max + new XYZ(1,1,1) });
return v.Id.Value;
```

### Step 5 — reveal it (run with `readOnly:true`)
Activating a view and changing the selection must happen OUTSIDE a transaction — that's what
`readOnly:true` gives you.

```csharp
var uidoc = uiapp.ActiveUIDocument;
var v = (View3D)doc.GetElement(new ElementId(VIEW_ID)); // from step 4
uidoc.ActiveView = v;
var ids = new[] { ALL_IDS_FROM_STEPS_1_TO_3 }.Select(i => new ElementId(i)).ToList();
uidoc.ShowElements(ids);                              // zoom to fit the shed
uidoc.Selection.SetElementIds(new List<ElementId>()); // end clean: nothing left selected
return ""demo revealed"";
```

### Step 6 — wrap up
Tell the user, in a couple of sentences, what now exists (four walls, a gable roof with a 1' overhang, a
door, a window, a section-boxed 3D view) and invite the next move — theirs or from the suggestions above.

";

    partial void OnExchangeFolderChanged(string value)
    {
        _settings.ExchangeFolder = value;
        _settings.Save();
    }
}
