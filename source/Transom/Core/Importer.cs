using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace Transom.Core;

/// <summary>Outcome of applying one change, stamped after the apply + post-apply verification passes. Drives the
/// run-results report: bold = Applied, italic + red = Failed/Unverified.</summary>
public enum ApplyOutcome { Pending, Applied, Failed, Unverified, Skipped }

public sealed class ProposedChange : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    public string UniqueId = "";
    public long TypeId;
    public int ParameterId;
    public string Binding = "instance";

    /// <summary>When set, this is a bulk instance write: NewValue is applied to each of these instance UniqueIds.</summary>
    public List<string>? BulkInstanceIds;

    /// <summary>W2 (H3 report-overlay): the UniqueId of the SCHEDULE ROW this change is displayed on (the type/group
    /// row for a bulk-instance cell). A bulk cell's own <see cref="UniqueId"/> is empty (its targets live in
    /// <see cref="BulkInstanceIds"/>, which are instance uids that are NOT rows in a grouped schedule), so the
    /// run-results overlay — keyed by visible row uid — couldn't place it and rendered partial-bulk cells as flat
    /// failures. RunResultsWriter falls back to this row uid for bulk cells so the cell's true Outcome + the C4
    /// "N of M instances persisted" note land on the correct rendered cell. Empty for non-bulk changes (they use
    /// <see cref="UniqueId"/>).</summary>
    public string ReportRowUid = "";

    /// <summary>True when this change targets element(s) inside a Revit group (can't be written directly).</summary>
    public bool InGroup;
    public string GroupName = "";

    /// <summary>True when this change's model group trips the HARD dance gate — the definition-swap dance can't
    /// reproduce it (a level-anchored "broken family", true rotation, or multi-level placement). Such columns drop
    /// the dance (option 3) and route to option 2 / Claude-Assist. Mirror-only and bystander-nested groups do NOT
    /// set this (they dance). Set by <c>ComputeGroupBroken</c> from <c>GroupSafetyResult.IsDanceGateBroken</c>.</summary>
    public bool GroupBroken;
    public string GroupBrokenReason = "";

    /// <summary>The named level-anchored families that gate the dance (e.g. "CW_Closet Shelf and Pole (Casework) ×2"),
    /// joined for display. Tells the user exactly what to re-host to unlock option 3. Set by <c>ComputeGroupBroken</c>
    /// when the group is gated by anchored families; empty otherwise.</summary>
    public string GroupBrokenFamilies = "";

    /// <summary>How a grouped edit gets written durably (set by <see cref="Importer.Mark"/>, only when <see
    /// cref="InGroup"/> is true). ProjectVary = set the "vary by group instance" flag + write directly (project/
    /// shared, positive id). BuiltinDance = stage for Claude-Assist (Edit Group) — only GEOMETRY-driving built-ins
    /// (Sill/Head Height) need this. None = write directly like an ungrouped instance — built-in DATA params (Mark,
    /// Comments, Number, Finish) take this path: Revit sets them directly on a grouped instance.</summary>
    public GroupMode GroupMode = GroupMode.None;

    /// <summary>How the user chose to resolve this change's group conflict (set per-column by the
    /// GroupResolutionDialog on Apply). Null until resolved. Routes the change to the matching backend.</summary>
    public GroupResolution? Resolution;

    /// <summary>Outcome after the apply + post-apply verification passes (Pending until applied). Drives the
    /// run-results report — bold for Applied, italic + red for Failed/Unverified.</summary>
    public ApplyOutcome Outcome = ApplyOutcome.Pending;
    public string OutcomeNote = "";

    /// <summary>True when the field can't be changed by import (read-only / driven by the family or type selection). Shown greyed, not applied.</summary>
    public bool Frozen;
    public string FrozenReason = "";

    /// <summary>True when the user's entry parsed but wasn't in the schedule's unit format (e.g. typed "2.5", the
    /// schedule shows "2'-6""). The change is built with the CANONICAL value in <see cref="NewValue"/> /
    /// <see cref="NewDouble"/>, but it's shown in the preview as PENDING — the user confirms the interpretation
    /// inline (the row-details strip under the row) before it can be applied. <see cref="EnteredValue"/> holds the
    /// raw text they typed so the strip can show "you typed 2.5 → interpreted as 2'-6"". Confirming flips this false
    /// and selects the row (TransomViewModel.ConfirmRow). Until then it is not selectable (can't be applied
    /// half-interpreted). Replaces the old separate "reformat suggestion" fix-pane for parse-OK-wrong-format cells.</summary>
    private bool _needsConfirm;
    public bool NeedsConfirm
    {
        get => _needsConfirm;
        set { if (_needsConfirm == value) return; _needsConfirm = value; Notify(nameof(NeedsConfirm)); Notify(nameof(Selectable)); }
    }
    /// <summary>The raw text the user typed for a <see cref="NeedsConfirm"/> change (e.g. "2.5"), shown in the
    /// inline confirm strip next to the interpreted value. Empty for normal changes. MUST be a property (not a field)
    /// — WPF data binding ignores public fields, so as a field the strip rendered "you typed """ (empty).</summary>
    public string EnteredValue { get; set; } = "";

    /// <summary>The column's unit spec id (ForgeTypeId string) for a numeric/length <see cref="NeedsConfirm"/>
    /// change — lets <c>ConfirmRow</c> parse the value the user types in the inline box (e.g. "7"" instead of "7'-0"")
    /// against the schedule's units ON CONFIRM. Empty for non-spec changes.</summary>
    public string SpecTypeId = "";

    /// <summary>The value shown in the inline confirm strip's EDITABLE box (bound TwoWay). Defaults to the user's
    /// raw entry so they see exactly what they typed and can CORRECT it (e.g. change "7" to "7"") before confirming.
    /// It is NOT interpreted as you type (that would interrupt typing) — only <c>ConfirmRow</c> parses it, on confirm.
    /// Editing it clears any prior <see cref="ConfirmError"/> so a stale "try again" note doesn't linger.</summary>
    private string _editValue = "";
    public string EditValue
    {
        get => _editValue;
        set { if (_editValue == value) return; _editValue = value; Notify(nameof(EditValue)); ConfirmError = ""; }
    }

    /// <summary>Set by <c>ConfirmRow</c> when the box value can't be parsed on confirm (e.g. junk) — shown IN the
    /// confirm row so it asks again, and the row stays pending. Cleared when the user edits the box or confirms a
    /// valid value. Empty = no error.</summary>
    private string _confirmError = "";
    public string ConfirmError
    {
        get => _confirmError;
        set { if (_confirmError == value) return; _confirmError = value; Notify(nameof(ConfirmError)); Notify(nameof(HasConfirmError)); }
    }
    public bool HasConfirmError => !string.IsNullOrEmpty(_confirmError);

    /// <summary>The interpretation CURRENTLY shown in the confirm row's "interpreted as …" line (e.g. "7'-0""). Set
    /// at build to the entry's canonical form, and updated by <c>ConfirmRow</c> — but ONLY on confirm, never as you
    /// type. The commit rule: confirming when the box re-parses to a value DIFFERENT from this re-prompts (updates
    /// this to the new interpretation, stays pending); confirming when the box already matches this commits it to
    /// <see cref="NewValue"/>. So a parsable value is always SHOWN and CONFIRMED before it changes the New cell.</summary>
    private string _suggestion = "";
    public string Suggestion
    {
        get => _suggestion;
        set { if (_suggestion == value) return; _suggestion = value; Notify(nameof(Suggestion)); }
    }

    // An unconfirmed format-mismatched change can't be applied until the user confirms the interpretation, so it
    // isn't selectable yet (the checkbox is disabled). Confirming clears NeedsConfirm → it becomes selectable.
    public bool Selectable => !Frozen && !NeedsConfirm;

    /// <summary>True = a GEOMETRY-driving built-in instance param (Sill/Head Height, offsets) inside a group. Option 2
    /// is suppressed (replacing it would desync the schedule from the 3D model); the only in-group edit is Claude-
    /// Assist. Drives the resolution dialog's accurate "why option 2 is unavailable" message. (Export colours it yellow.)</summary>
    public bool GeometryDriven;

    /// <summary>Raised whenever any change's <see cref="Selected"/> toggles (incl. the per-cell DataGrid checkbox).
    /// The viewmodel subscribes once to re-evaluate each affected-schedule row's tri-state so cell edits and the
    /// per-schedule checkbox stay visually consistent (UX_SPEC §5 C-3). Static = one subscription, no per-instance churn.</summary>
    public static event System.Action? SelectionChanged;

    private bool _selected = true;
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Notify(nameof(Selected)); SelectionChanged?.Invoke(); }
    }
    public string ElementName { get; set; } = "";
    public string Field { get; set; } = "";
    /// <summary>The source schedule column's DISPLAY heading (ScheduleField.ColumnHeading), when known.
    /// Used by option 2 to give the new column a clean visible title instead of the verbose param name.
    /// Empty falls back to Field.</summary>
    public string SourceHeading { get; set; } = "";
    public string OldValue { get; set; } = "";

    /// <summary>The value shown in the preview's New cell. MUST notify — <c>ConfirmRow</c> updates it IN PLACE when
    /// the user confirms/corrects an inline pending row (e.g. "2'-6"" → "4'-0""), and the grid's New cell binds to it;
    /// without notification the cell kept showing the original suggestion (looked like "it just applies the suggested
    /// value"). The old fix-pane flow rebuilt the whole grid via re-analysis, so it never needed this.</summary>
    private string _newValue = "";
    public string NewValue
    {
        get => _newValue;
        set { if (_newValue == value) return; _newValue = value; Notify(nameof(NewValue)); }
    }
    public int InstancesAffected { get; set; } = 1;

    /// <summary>The source schedule this change came from — its display name. Set once in <c>Summarize()</c>
    /// (one choke point), used by option 2 to resolve the schedule whose column it replaces. Named to avoid
    /// the transient <c>ChangeSet.ScheduleName</c> cursor (which only holds the LAST sheet after the loop).</summary>
    public string SourceScheduleName = "";
    /// <summary>The source schedule's UniqueId (preferred over the name when resolving — survives renames).</summary>
    public string SourceScheduleUid = "";
    /// <summary>Preview "Scope" cell. Rows that actually route through the per-parameter resolution dialog
    /// (blue project/shared → vary, yellow geometry built-in → Claude-Assist) say "choose on Apply" so the
    /// preview matches the dialog they'll trigger — they are NOT applied silently. A grouped built-in DATA param
    /// (GroupMode.None despite InGroup) writes DIRECTLY, so it shows a normal instance/bulk scope, not the warning.</summary>
    public string Scope
    {
        get
        {
            if (GroupMode is GroupMode.ProjectVary or GroupMode.BuiltinDance)
            {
                var via = GroupMode == GroupMode.BuiltinDance ? "built-in" : "project/shared";
                return $"⚠ group ({via}) — choose on Apply · {InstancesAffected} inst";
            }
            return BulkInstanceIds != null ? $"all {InstancesAffected} inst"
                : Binding == "type" ? $"type · {InstancesAffected} inst" : "instance";
        }
    }

    public bool IsString;
    public bool IsInt;
    public string NewString = "";
    public double NewDouble;
    public int NewInt;
}

public sealed class SkippedItem
{
    public string Reason { get; set; } = "";
    public string Detail { get; set; } = "";
    /// <summary>CHANGE 2 §3b (FULL skip-log scoping, integ1-2 final ruling): the source schedule's Excel tab — lets
    /// the preview scope the skip-log to the SELECTED schedules in subset mode. Stamped in BULK in
    /// BuildChangeSet.Summarize() via the per-sheet skp0 boundary (so the dozens of cs.Skipped.Add sites need no
    /// change); conflict-skips stamp it inline in ResolveSelectedAndFinalize. Empty = not attributable → never hidden
    /// (fail-open).</summary>
    public string SheetTabName = "";
    /// <summary>§12 (user rule 2026-06-14): is this skip a USER DATA edit that won't apply (true → SHOW), or
    /// schedule STRUCTURE/DEFINITION the user didn't edit as data (false → HIDE)? All skip surfaces (panel, status
    /// count, diagnostic) show ONLY UserRelevant==true. Default TRUE = honesty floor: never silently hide a dropped
    /// user edit — only the three structural sites (display-only schedule, column-header-not-importable INCL.
    /// user-edited headers, duplicate-row) are explicitly set false. This filter is INDEPENDENT of and PRIOR to the
    /// §11.1 selected-scope filter: drop structural skips first, then subset-scope the remainder to selected.</summary>
    public bool UserRelevant = true;
}

public sealed class ConflictOption
{
    public string Display = "";
    public bool IsString;
    public string NewString = "";
    public double NewDouble;
    public bool Parseable = true;
    /// <summary>True for an option that is a VALUE THE USER TYPED into the schedule (vs the type's existing/current
    /// value, which is offered as "keep current"). The picker labels these "(entered value)" so the user can tell
    /// their own input apart from the current value.</summary>
    public bool IsEntered;

    /// <summary>The option's value in the schedule's unit format (e.g. "2'-6"" for an entered "2.5"). The picker
    /// shows <see cref="Display"/> (the raw entered text, so the user recognizes their input), but the RESOLVED
    /// change shows this canonical form in its New cell — so a picked entry reads the same as the interpreted
    /// value it will write. Equals Display for string options or when the entry was already canonical.</summary>
    public string CanonicalDisplay = "";
}

public sealed class TypeConflict
{
    public long TypeId;
    public int ParameterId;
    public string Field = "";
    public string TypeName = "";
    public string CurrentDisplay = "";
    /// <summary>The type's CURRENT value as a double (length etc.) — lets the resolver drop a picked option whose
    /// parsed value already equals the model (a no-op), even when the user picked an entered value like "7" that the
    /// "keep current" string check (Display vs CurrentDisplay) wouldn't catch. NaN for string-valued conflicts.</summary>
    public double CurrentDouble = double.NaN;
    /// <summary>The column's unit spec id (ForgeTypeId string), carried so a resolved PENDING change can re-parse a
    /// corrected value the user types in the inline confirm strip (ConfirmRow). Empty for string conflicts.</summary>
    public string SpecTypeId = "";
    public int InstancesAffected;
    public List<ConflictOption> Options = new();
    /// <summary>C-7: the source schedule this conflict came from, carried through so the resolved change
    /// (ResolveToChange) is stamped with its schedule and rolls up under the per-schedule selection.</summary>
    public string ScheduleName = "";
    public string ScheduleUid = "";
}

/// <summary>A flagged cell for the colour-coded import report. Severity: "red" (can't write) or "yellow".
/// Yellow splits by <see cref="Category"/>: "drift" (model actually changed since export) vs "advisory"
/// (a heads-up — sort/filter-key edit, missing instances, …) so the status line never calls an advisory "drift".</summary>
public sealed class CellDiagnostic
{
    public string SheetTabName = "";
    public int ExcelRow;
    public int Col;
    public string FieldName = "";
    public string ElementLabel = "";
    public string Severity = "";   // red | yellow
    public string Category = "advisory";   // drift | advisory (meaningful for yellow only)
    public string Reason = "";
    public string Value = "";
}

/// <summary>Per-schedule (sheet) tally shown on Preview: how many changes/skips this import would make to each schedule.</summary>
public sealed class SheetSummary
{
    public string ScheduleName = "";
    /// <summary>The source schedule's UniqueId — lets the preview's per-schedule selection match this summary to
    /// its <see cref="ProposedChange"/>s by uid (rename-safe), falling back to name when empty (cross-model/legacy).</summary>
    public string ScheduleUid = "";
    /// <summary>CHANGE 2 §3b: the schedule's Excel tab name — the join key for scoping Fixes/Skipped (which carry a
    /// tab, not a schedule uid) to the SELECTED schedules.</summary>
    public string SheetTabName = "";
    public int Changes;
    public int Skipped;
    /// <summary>CHANGE 1 (§8.2): unresolved TYPE conflicts this schedule contributes (counted in <c>Summarize()</c>
    /// via a cfl0 boundary, BEFORE the user resolves them at the dialog). Kept SEPARATE from <see cref="Changes"/>
    /// — a conflict isn't a change until resolved — so a conflict-only tab still appears in the selection step
    /// (filter is Changes>0 || Conflicts>0) without mis-advertising "N change(s)" (the §5c count-trap).</summary>
    public int Conflicts;
    public bool RoundTrippable = true;

    // Bound in XAML (WPF binding ignores public fields, so expose a property). A conflict IS an edit the user made
    // (it just needs a choice before it can apply), so it counts toward the change total — reporting "0 change(s),
    // N conflict(s)" read as "nothing happened" even though the user made N edits. Total = changes + conflicts; the
    // suffix names how many of those still need resolution.
    public string Display => $"{ScheduleName} — {Changes + Conflicts} change(s)"
                             + (Conflicts > 0 ? $" ({Conflicts} need resolution)" : "")
                             + (Skipped > 0 ? $", {Skipped} skipped" : "");
}

/// <summary>CHANGE 2 (§9): one dependent ("inherited-change") schedule — a schedule that displays elements a
/// driving (edited) schedule's TYPE/instance edits change, so it inherits the change without being edited. Shown
/// read-only/indented under its driving parent in the selection step; never an apply target.</summary>
public sealed class DependentScheduleRef
{
    public string ScheduleName = "";
    public string ScheduleUid = "";
}

public sealed class ChangeSet
{
    public string ScheduleName = "";
    public bool CrossModel;
    public List<SheetSummary> SheetSummaries = new();
    public List<ProposedChange> Changes = new();
    public List<SkippedItem> Skipped = new();
    public List<TypeConflict> Conflicts = new();
    public List<CellDiagnostic> Diagnostics = new();
    public List<ReformatSuggestion> Reformats = new();
    public string ReportPath = "";

    /// <summary>Keys ("paramId|field") of group-conflict columns for which "new type parameter" (option 2)
    /// is valid — i.e. the column's edited values are consistent within every affected type (a type param
    /// holds one value per type). Computed in <c>BuildChangeSet</c>; gates option 2 in the resolution dialog.
    /// Keyed by (ParameterId, Field) to match the per-column picker, not by parameter id alone.</summary>
    public HashSet<string> Option2EligibleParams = new();

    /// <summary>How each Option-2-eligible column's TYPE-vs-INSTANCE binding was inferred from the source
    /// schedule's organization (see <c>Importer.ComputeOption2Mode</c>). Keyed by <see cref="ColumnKey"/>
    /// (same key as <see cref="Option2EligibleParams"/>). Absent/None ⇒ option 2 not offered for that column.</summary>
    public Dictionary<string, Option2Mode> Option2Modes = new();

    /// <summary>The (ParameterId, Field) key used to identify one resolvable group-conflict column.</summary>
    public static string ColumnKey(int parameterId, string field) => parameterId + "|" + field;

    /// <summary>Names of the schedules in this import (workbook sheets). Used by option 2 to add the new
    /// type-parameter field to the affected schedules.</summary>
    public List<string> ImportedScheduleNames = new();

    /// <summary>Full plain-text diagnostic of this import (column matching, anchors, skips, results) for the Copy log button.</summary>
    public string DiagnosticLog = "";

    /// <summary>CHANGE 2 §3b surface-3: the source workbook + doc GUID captured at build time, so the copy-log
    /// diagnostic can be REBUILT post-selection (subset mode) — scoped to the selected sheets + skips — on the UI
    /// thread without a live Document (uses the captured GUID). See <c>Importer.BuildDiagnosticLog(cs, selectedUids)</c>.</summary>
    public ImportWorkbook? SourceWorkbook;
    public string DocCreationGuid = "";

    /// <summary>The document's unit settings, captured at build time (API thread) so the inline confirm strip can
    /// RE-PARSE a user-corrected value (e.g. "7"" instead of "7'-0"") on the UI thread without a re-Preview — drives
    /// <c>TransomViewModel.ConfirmRow</c>. UnitFormatUtils.Parse/Format operate on this snapshot and need no
    /// transaction. Null only for a degenerate change-set built without a doc.</summary>
    public Units? Units;

    /// <summary>CHANGE 2 (§9): dependent ("inherited-change") schedules per driving (edited) schedule — the OTHER
    /// project schedules that display elements a driving schedule's TYPE/instance edits change. Keyed by the
    /// driving schedule's UniqueId → its dependents. Computed once in <c>BuildChangeSet</c> (API thread, one
    /// collector pass per schedule, see <c>ComputeDependents</c>); read-only display in the selection step. Empty
    /// when no type/instance edge is justified (grouped/shared/computed never infer an edge — §9.2).</summary>
    public Dictionary<string, List<DependentScheduleRef>> Dependents = new();

    /// <summary>Revit warnings/errors raised during the apply commit (the ApplyFailureCollector's messages), kept so
    /// the FINAL by-uid log (rebuilt post-VerifyApplied) can still include them — see <c>FinalizeApplyReport</c>.</summary>
    public List<string> RevitApplyMessages = new();

    /// <summary>Benign geometry-fixups Transom auto-applied during the commit so an edit could land (e.g. unjoined
    /// elements, removed dimension references). Surfaced in the apply headline + a dedicated report section so the
    /// user is WARNED the model geometry was adjusted — never silent.</summary>
    public List<string> AutoFixWarnings = new();

    /// <summary>W1 (Task #17): elements Revit DELETED during apply that we did NOT ask to delete — captured from the
    /// <c>DocumentChanged</c> committed delta and scope-filtered to Transom's apply transactions. These are Revit's
    /// own family-regeneration auto-fixes ("Delete Splitting Element" etc.), which are pre-collector and unpreventable,
    /// so a "successful" apply can silently remove geometry. Surfaced in the apply log + run report so it is never
    /// silent. Each entry is "Name (id)" (name best-effort — a deleted element is usually un-queryable). Empty = none.</summary>
    public List<string> RevitDeletions = new();

    /// <summary>§10 mandatory per-(schedule,type) warnings recorded while applying option 2 — each a type left
    /// blank because the column was converted to a TYPE param and that type's instances had varying values.
    /// Surfaced in the apply status note AND the copyable apply log (it survives the post-apply log rebuild).</summary>
    public List<string> Option2Warnings = new();

    /// <summary>D1/D2 (option-2 apply-log honesty): the conversion counts from <c>ApplyNewParam</c> — params
    /// created, values written (edits + carried-forward originals), source columns replaced, columns appended.
    /// Set only when an option-2 (NewTypeParam/NewInstanceParam) conversion ran. Surfaced as a dedicated section
    /// in the apply log so a conversion that wrote N edits is never reported as "applied: 0". Null = no option-2
    /// conversion this apply. The per-change list of edited cells is derived from <see cref="Changes"/> (those
    /// with an option-2 Resolution) in BuildApplyLog; this carries only the aggregate counts.</summary>
    public Option2Summary? Option2Result;

    /// <summary>Header edits to round-trip: the user renamed a column caption (or a grouped super-header) in the
    /// workbook. Both ARE settable through the API (verified): per-column captions via <c>ScheduleField.ColumnHeading</c>,
    /// merged super-headers via <c>ViewSchedule.GroupHeaders(top,left,bottom,right,caption)</c>. (The schedule body's
    /// <c>SetCellText</c> is forbidden, so neither uses it.) Applied in the same apply transaction. Built in
    /// <c>BuildChangeSet</c>; headers-off (synthesized) columns never produce an entry.</summary>
    public List<HeaderChange> HeaderChanges = new();
}

/// <summary>A round-trippable header rename. Two kinds:
/// • <see cref="HeaderChangeKind.ColumnCaption"/> — a per-column heading; the live <c>ScheduleField</c> is resolved by
///   (ScheduleUid, ParameterId), rename-safe; applied via <c>field.ColumnHeading = NewHeading</c>.
/// • <see cref="HeaderChangeKind.GroupHeader"/> — a merged super-header spanning a cell rectangle; applied via
///   <c>ViewSchedule.GroupHeaders(Top,Left,Bottom,Right,NewHeading)</c> (keyed by the rectangle, since a super-header
///   isn't tied to one field). The rectangle is in Body-section grid coordinates (matches the exported merge region).</summary>
public enum HeaderChangeKind { ColumnCaption, GroupHeader }

public sealed class HeaderChange
{
    public HeaderChangeKind Kind = HeaderChangeKind.ColumnCaption;
    public string ScheduleUid = "";
    public string ScheduleName = "";
    public string SheetTabName = "";
    public int ParameterId;        // ColumnCaption: the column's stable identity
    public int ExcelCol;           // workbook column (run-results overlay / report coloring)
    public int ExcelRow;           // workbook header row the caption lives on
    // GroupHeader: the merged super-header's Body-section rectangle (matches the exported MergeRegion).
    public int Top, Left, Bottom, Right;
    public string OldHeading = "";
    public string NewHeading = "";
    public ApplyOutcome Outcome = ApplyOutcome.Pending;
    public string OutcomeNote = "";
    public bool Selected = true;
}

/// <summary>D1/D2: aggregate counts of what an option-2 (NewTypeParam/NewInstanceParam) conversion did, for honest
/// apply-log reporting. Counts are across all converted columns in one apply.</summary>
public sealed class Option2Summary
{
    public int ParamsCreated;
    public int ValuesWritten;     // edits + carried-forward originals actually written to the new param(s)
    public int ColumnsReplaced;   // source field replaced by the new '(Transom)' column
    public int ColumnsAppended;   // new column added alongside (key-guard / no-source-to-replace)
}

/// <summary>
///     Diffs an imported workbook against the live model (read-only) into a change set + diagnostics, and
///     applies a confirmed change set in one transaction. Uses a three-way compare (exported baseline vs
///     current model vs spreadsheet): only cells you actually edited (spreadsheet ≠ baseline) become writes;
///     model drift (current ≠ baseline) is flagged "changed since export"; unwritable cells are flagged red.
/// </summary>
public sealed class Importer
{
    /// <summary>Revit's rendered placeholder for an aggregated cell whose instances hold differing values. The
    /// export baseline records it verbatim (no named constant exists in the exporter — the string comes from
    /// Revit's own renderer), so it can never equal a single element's live display: drift checks must skip it
    /// or they flag phantom "changed since export" on every preview.</summary>
    private const string VariesSentinel = "<varies>";

    private sealed class TypeCandidate
    {
        public ImportColumn Col = null!;
        public string SheetTab = "";
        public string ScheduleName = "";   // C-7: carried so a resolved type-conflict change can be matched to its schedule.
        public string ScheduleUid = "";    // C-7: (rename-safe uid; name fallback) — see ChangesForSchedule / per-schedule selection.
        public string TypeName = "";
        public bool IsString;
        public string? SpecTypeId;
        public string CurString = "";
        public double CurDouble;
        public string CurDisplay = "";
        public readonly List<(int excelRow, string value, string label)> Cells = new();   // EDITED cells only
        /// <summary>Count of ALL schedule rows that resolve to this type for this column (edited OR not). When this
        /// exceeds Cells.Count, some visible rows of the type were left UNEDITED — and since a type holds one value,
        /// writing the edited value to the type would silently clobber those siblings (which all show CurDisplay). So
        /// an edit that differs from CurDisplay with unedited siblings present is a type conflict, not a clean write.</summary>
        public int TotalRows;
    }

    public ChangeSet BuildChangeSet(Document doc, ImportWorkbook wb)
    {
        var cs = new ChangeSet { CrossModel = wb.SourceModelGuid != doc.CreationGUID.ToString() };
        var units = doc.GetUnits();
        cs.Units = units;   // captured for the inline confirm strip's UI-thread re-parse (ConfirmRow)

        foreach (var sheet in wb.Sheets)
        {
            cs.ScheduleName = sheet.ScheduleName;
            // CHANGE 1 (§8.2): cfl0 mirrors chg0/skp0 — ResolveTypeGroups (which appends cs.Conflicts) runs before
            // Summarize() per sheet, so (Conflicts - cfl0) is this sheet's conflict count. Kept separate from Changes
            // so a conflict-only tab surfaces in the selection step without inflating "N change(s)".
            int chg0 = cs.Changes.Count, skp0 = cs.Skipped.Count, cfl0 = cs.Conflicts.Count;
            void Summarize()
            {
                // §4a: stamp every change this sheet produced with its source schedule (one choke point — the
                // change factories don't know the sheet). Option 2 resolves the schedule to replace from here.
                for (int i = chg0; i < cs.Changes.Count; i++)
                {
                    cs.Changes[i].SourceScheduleName = sheet.ScheduleName;
                    cs.Changes[i].SourceScheduleUid = sheet.ScheduleUniqueId;
                }
                // CHANGE 1 (C-7 reuse): stamp this sheet's CONFLICTS with their source schedule too, so the affected
                // predicate + conflict-scoping (§8.4) can attribute a conflict to its tab.
                for (int i = cfl0; i < cs.Conflicts.Count; i++)
                {
                    cs.Conflicts[i].ScheduleName = sheet.ScheduleName;
                    cs.Conflicts[i].ScheduleUid = sheet.ScheduleUniqueId;
                }
                // CHANGE 2 §3b (FULL): stamp this sheet's SKIPS with their tab via the existing skp0 boundary — ONE
                // place, so the dozens of cs.Skipped.Add sites need no change — so the preview can scope the skip-log
                // to the SELECTED schedules. Conflict-skips added later (ResolveSelectedAndFinalize) stamp their tab inline.
                for (int i = skp0; i < cs.Skipped.Count; i++)
                    cs.Skipped[i].SheetTabName = sheet.SheetTabName;
                cs.SheetSummaries.Add(new SheetSummary
                {
                    ScheduleName = sheet.ScheduleName, ScheduleUid = sheet.ScheduleUniqueId,
                    SheetTabName = sheet.SheetTabName,   // bridge for the fix-pane + skip-log filters (tab→schedule)
                    RoundTrippable = sheet.RoundTrippable,
                    // §12 (step-1 surface): the per-schedule selection-list "M skipped" (SheetSummary.Display →
                    // AffectedScheduleRow.Display) must show ONLY user-relevant skips, like every other skip surface —
                    // count user-relevant skips in THIS sheet's appended [skp0..end) range, excluding back-end/structural
                    // (UserRelevant==false). The Changes/Conflicts counts are unchanged. ATTIC → 0 (its 3 header skips
                    // are structural), and Display drops the ", N skipped" segment when 0.
                    Changes = cs.Changes.Count - chg0, Skipped = cs.Skipped.Skip(skp0).Count(s => s.UserRelevant),
                    Conflicts = cs.Conflicts.Count - cfl0,
                });
            }

            // Item B (user-committed default = B2): a non-round-trippable schedule whose ROWS nonetheless anchored
            // (a TYPE schedule whose multi-type collapse blocked a single-anchor verdict but whose rows still carry
            // a type uid + AggregatedTypeUids — e.g. PARTITION TYPE R) CAN still write its writable TYPE cells: the
            // edit fans to every aggregated type (HandleTypeRow → RecordTypeAll, the same machinery itemized
            // multi-type rows use). So only WHOLESALE-skip a non-round-trippable sheet when NOTHING anchored at all
            // (genuinely display-only: material takeoffs, un-anchorable areas/rooms). A sheet with ≥1 anchored row
            // flows into the per-row loop, where each row's binding/edited/read-only/<varies> guards already decide
            // per-cell (read-only stays skipped, an unchanged <varies> writes nothing, per-type rejects are reported).
            bool anyAnchored = sheet.Rows.Any(r => !string.IsNullOrEmpty(r.UniqueId));
            if (!sheet.RoundTrippable && !anyAnchored)
            {
                cs.Skipped.Add(new SkippedItem
                {
                    Reason = "display-only schedule",
                    Detail = $"{sheet.ScheduleName} — not itemized and no row maps to an element/type, so values can't " +
                             "be written back. Turn on 'Itemize every instance' (the schedule's Sorting/Grouping tab) " +
                             "and re-export to round-trip.",
                    UserRelevant = false,   // §12 HIDE: structural (the schedule can't round-trip); not a user data edit
                });
                Summarize();
                continue;
            }

            foreach (var unmatched in sheet.Columns.Where(c => c.Writable && !c.Matched))
            {
                var colLabel = string.IsNullOrEmpty(unmatched.Header) ? unmatched.FieldName : unmatched.Header;
                // ID-PRIMARY matching (2026-06-14) RETIRES the FIX-1 ambiguous-duplicate stopgap: a duplicate-NAME
                // column now resolves to its distinct paramId in ExcelReader's same-header group, so it always lands —
                // it never reaches here as an "ambiguous" skip. A writable column that's STILL unmatched is genuinely
                // renamed/removed (or a count mismatch under a shared header), so its edits dropped → §12 SHOW
                // (UserRelevant=true, the default), user-framed wording (§12.4): it's the user's own model change.
                cs.Skipped.Add(new SkippedItem
                {
                    Reason = "column not in spreadsheet",
                    Detail = $"Your edits in column '{colLabel}' couldn't be applied — that column was renamed or " +
                             "removed in the model since you exported.",
                });
            }

            // Column-HEADER (caption) edits NOW round-trip (user directive 2026-06-18: any header editable through the
            // API should be editable-and-round-tripped). A per-column caption is settable via ScheduleField.ColumnHeading
            // (verified live; the body section itself rejects SetCellText, so DATA cells stay non-importable). Detect a
            // changed header (current sheet heading != the exported ColumnHeading for a matched column) and record a
            // HeaderChange — applied in the apply transaction by setting field.ColumnHeading (field resolved by
            // ParameterId, NOT by the old heading, so the rename can't break the column match).
            //
            // Excluded from the COLUMN-CAPTION path:
            //  • headers-off / synthesized-header schedules: col.Header is empty (no real ColumnHeading existed), and
            //    the workbook's row-0 is a Transom-synthesized field-name row, not a user caption. Editing it must not
            //    write a heading onto a schedule whose headers are turned off.
            //  • merged super-headers (e.g. DOOR / FRAME): handled by the GROUP-HEADER path just below (they round-trip
            //    via ViewSchedule.GroupHeaders, not ColumnHeading).
            // Compare against the CAPTION row (per-column headings), NOT row 0 — row 0 carries super-headers on a
            // multi-band header, so comparing it to the exported per-column caption fired SPURIOUS renames that
            // overwrote real headings with super-header text (bug, 2026-06-19). CurrentCaptions reads the caption row.
            // A column whose CAPTION-row cell falls inside a MULTI-column super-header merge isn't a per-column
            // caption — it's the super-header spanning that cell (e.g. WINDOW's single-band header puts the merged
            // "1ST FLOOR" ON the caption row, so col 0 reads "1ST FLOOR" not "TYPE"). Skip those here; the super-
            // header round-trips via the GROUP-HEADER path below. (Guards the WINDOW spurious-rename bug, 2026-06-19.)
            bool InMultiColSuperHeader(int excelCol) => sheet.HeaderGroups.Any(hg =>
                hg.Left < hg.Right && excelCol >= hg.Left && excelCol <= hg.Right
                && sheet.CaptionRowExcel >= hg.Top && sheet.CaptionRowExcel <= hg.Bottom);
            foreach (var col in sheet.Columns.Where(c => c.Matched && c.ExcelCol >= 0 && c.ExcelCol < sheet.CurrentCaptions.Length))
            {
                var currentHeader = sheet.CurrentCaptions[col.ExcelCol] ?? "";
                if (string.IsNullOrEmpty(col.Header) || string.IsNullOrEmpty(currentHeader) || currentHeader == col.Header)
                    continue;
                if (InMultiColSuperHeader(col.ExcelCol)) continue;   // cell is a super-header, not this column's caption
                cs.HeaderChanges.Add(new HeaderChange
                {
                    Kind = HeaderChangeKind.ColumnCaption,
                    ScheduleUid = sheet.ScheduleUniqueId,
                    ScheduleName = sheet.ScheduleName,
                    SheetTabName = sheet.SheetTabName,
                    ParameterId = col.ParameterId,
                    ExcelCol = col.ExcelCol,
                    ExcelRow = sheet.CaptionRowExcel >= 0 ? sheet.CaptionRowExcel : 0,   // the workbook caption row
                    OldHeading = col.Header,
                    NewHeading = currentHeader,
                });
            }

            // Grouped SUPER-HEADER edits also round-trip (verified): a merged header-band caption (e.g. "DOOR"/"FRAME")
            // is settable via ViewSchedule.GroupHeaders(top,left,bottom,right,caption). Detect a changed super-header by
            // comparing the workbook's current top-left text to the exported caption; apply by rectangle (super-headers
            // aren't tied to a field, so the rectangle is the key — robust when the sheet wasn't reordered).
            foreach (var hg in sheet.HeaderGroups)
            {
                if (string.IsNullOrEmpty(hg.Caption) || string.IsNullOrEmpty(hg.CurrentCaption) || hg.CurrentCaption == hg.Caption)
                    continue;
                cs.HeaderChanges.Add(new HeaderChange
                {
                    Kind = HeaderChangeKind.GroupHeader,
                    ScheduleUid = sheet.ScheduleUniqueId,
                    ScheduleName = sheet.ScheduleName,
                    SheetTabName = sheet.SheetTabName,
                    Top = hg.Top, Left = hg.Left, Bottom = hg.Bottom, Right = hg.Right,
                    ExcelCol = hg.Left, ExcelRow = hg.Top,
                    OldHeading = hg.Caption,
                    NewHeading = hg.CurrentCaption,
                });
            }

            // A duplicated anchor (a row copied in Excel) would write one element twice — all copies were dropped.
            foreach (var dupUid in sheet.DuplicateUids)
                cs.Skipped.Add(new SkippedItem
                {
                    Reason = "duplicate row",
                    Detail = $"anchor …{(dupUid.Length > 6 ? dupUid.Substring(dupUid.Length - 6) : dupUid)} appears on " +
                             "more than one row — all copies skipped so the element isn't written twice",
                    UserRelevant = false,   // §12 HIDE: a copied row is a structural input artifact, not a distinct user data edit
                });

            var typeGroups = new Dictionary<(long, int), TypeCandidate>();

            foreach (var row in sheet.Rows)
            {
                if (row.GroupHeaderEdit != null)
                {
                    HandleGroupHeaderRow(doc, sheet, row, cs, units);
                    continue;
                }

                if (row.Kind == "type" || row.Kind == "group")
                {
                    // Look up the baseline by the SAME per-rendered-row key the export wrote (a type uid alone
                    // collides across that type's rows). RowBindings stays keyed by the row's anchor uid.
                    sheet.Baseline.TryGetValue(ScheduleReader.BaselineRowKey(row.UniqueId, row.Kind, row.InstanceIds), out var baseRowT);
                    sheet.RowBindings.TryGetValue(row.UniqueId, out var rbT);
                    HandleTypeRow(doc, sheet, row, cs, typeGroups, units, rbT, baseRowT);
                    continue;
                }

                var label = row.Cells.FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? row.UniqueId;
                var el = doc.GetElement(row.UniqueId);
                if (el == null)
                {
                    cs.Skipped.Add(new SkippedItem { Reason = "element deleted", Detail = label });
                    foreach (var col in sheet.Columns.Where(c => c.Writable && c.Matched && c.ExcelCol < row.Cells.Length))
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "element no longer exists", row.Cells[col.ExcelCol]));
                    continue;
                }

                // Element (itemized) rows have no InstanceIds, so BaselineRowKey returns row.UniqueId — identical to
                // the historical lookup; routed through the helper only to keep all sites deriving the key one way.
                sheet.Baseline.TryGetValue(ScheduleReader.BaselineRowKey(row.UniqueId, row.Kind, row.InstanceIds), out var baseRow);
                var (elInGroup, elGroupName) = GroupInfo(doc, el);

                foreach (var col in sheet.Columns)
                {
                    if (!col.Writable || !col.Matched || col.ExcelCol >= row.Cells.Length) continue;
                    var cellText = row.Cells[col.ExcelCol] ?? "";
                    if (MergedSkip(sheet, row, col, cs, label, cellText)) continue;
                    var baseline = baseRow != null && baseRow.TryGetValue(col.Col, out var bv) ? bv : null;

                    // §17: a COMBINED column has no single parameter — route an edited combined cell through the
                    // fail-closed parse→distribute path (NEVER GetParam on the combined column). FORK 3 (per-field
                    // component-wins): if any of this field's component columns was edited directly in this row, the
                    // combined parse is suppressed for the whole field (the explicit component edit lands instead).
                    if (col.CombinedParts != null)
                    {
                        HandleCombinedColumn(doc, sheet, row, cs, col, el, baseRow, baseline, label, units, elInGroup, elGroupName, cellText);
                        continue;
                    }

                    // Resolve against the live model so a stale/wrong exported binding can't misroute the write.
                    var binding = ResolveBindingLive(doc, el, col.ParameterId, col.Binding);

                    var host = binding == "type"
                        ? (el.GetTypeId() != ElementId.InvalidElementId ? doc.GetElement(el.GetTypeId()) : null)
                        : binding == "instance" ? el : null;
                    var param = host == null ? null : GetParam(host, col.ParameterId);

                    if (param == null)
                    {
                        if (baseline == null || cellText != baseline)
                        {
                            cs.Skipped.Add(new SkippedItem { Reason = "parameter not found", Detail = $"{col.FieldName} ({label})" });
                            cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "parameter not found", cellText));
                        }
                        continue;
                    }

                    var current = CurrentDisplay(param);
                    bool edited = baseline != null ? cellText != baseline : cellText != current;

                    // §15-E: an edit to a READ-ONLY (family/type-driven) cell DROPS SILENTLY — not a change, not a
                    // skip, not shown, no "frozen" tally. A read-only cell is never the user's editable edit, so per
                    // the guiding principle (alert only to deviations on cells that CAN be edited) we surface nothing.
                    if (param.IsReadOnly) continue;

                    // Drift: current model value differs from what was exported. §15-C: only flag drift on a cell the
                    // user did NOT edit — a cell the user is CHANGING is intent, not drift. (Never knowable without a
                    // baseline, and never with the "<varies>" sentinel — it compares unequal to any single live display.)
                    // §15-C gap fix: ALSO suppress drift when the workbook cell ALREADY MATCHES the current model value
                    // (`!Drifted(param, cellText, …)` = the same no-op-write equality). This is the user's own earlier-
                    // applied edit (workbook==model now), which the current-preview `edited` check misses (cellText==current
                    // → edited=false) — so drift only fires when the model holds something the user did NOT put there.
                    if (!edited && baseline != null && baseline != VariesSentinel
                        && Drifted(param, baseline, units, col.SpecTypeId)
                        && Drifted(param, cellText, units, col.SpecTypeId))
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "yellow",
                            $"changed since export (was '{baseline}', now '{current}')", cellText, category: "drift"));

                    // Count an UNEDITED type-bound row so the type-conflict check knows it would be clobbered by a type
                    // write (a type holds one value; this row currently shows the type's value). Edited rows are counted
                    // by RecordType below. (Itemized schedules: each row is an instance, but Width etc. is type-bound,
                    // so one edited row's value, written to the type, silently changes every other instance of the type.)
                    if (!edited)
                    {
                        if (binding == "type" && !param.IsReadOnly)
                            EnsureTypeCandidate(typeGroups, sheet, col, host!, param);
                        continue; // not edited -> no write (never revert model drift)
                    }

                    if (param.StorageType == StorageType.String)
                    {
                        if (binding == "type")
                            RecordType(typeGroups, sheet, col, host!, param, label, row.ExcelRow, cellText);
                        else if ((param.AsString() ?? "") != cellText)
                            cs.Changes.Add(Mark(doc, InstanceChange(el, col, param.AsString() ?? "", cellText, true, cellText, 0), elInGroup, elGroupName));
                    }
                    else if (param.StorageType == StorageType.Integer && binding == "instance")
                    {
                        if (!TryParseInteger(IsYesNo(param), cellText, out int iv))
                        {
                            cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{col.FieldName} = '{cellText}'" });
                            cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "value can't be parsed", cellText));
                        }
                        else if (param.AsInteger() != iv)
                            cs.Changes.Add(Mark(doc, IntChange(el, col, current, cellText, iv), elInGroup, elGroupName));
                    }
                    else if (param.StorageType == StorageType.Double && col.SpecTypeId != null)
                    {
                        bool parsedOk = UnitFormatUtils.TryParse(units, new ForgeTypeId(col.SpecTypeId), cellText, out double parsed);
                        if (binding == "type")
                            // Record the cell EVEN IF it doesn't parse — an unreadable value ("ABC") must still reach the
                            // type-conflict machinery so it shows up as a pickable option and then an inline confirm row,
                            // NOT get silently dropped. Parsing is resolved later on the confirm line, never here.
                            RecordType(typeGroups, sheet, col, host!, param, label, row.ExcelRow, cellText);
                        else if (!parsedOk)
                        {
                            // INSTANCE cell that can't be read → an inline PENDING row (box + Confirm), same as the
                            // format-mismatch case, just with no interpretation yet — the confirm line asks for a usable
                            // value. Never a silent drop. (Read-only/merged cells are filtered earlier, so this is an
                            // editable cell the user typed junk into.)
                            var modelDisp = param.AsValueString() ?? "";
                            var ch = InstanceChange(el, col, modelDisp, "", false, "", 0);
                            ch.NeedsConfirm = true;
                            ch.EnteredValue = cellText;
                            ch.SpecTypeId = col.SpecTypeId;
                            ch.EditValue = cellText;
                            ch.ConfirmError = $"enter a usable value for {col.FieldName} (e.g. 7' or 7\")";   // shown in the row immediately
                            ch.Selected = false;
                            cs.Changes.Add(Mark(doc, ch, elInGroup, elGroupName));
                        }
                        else
                        {
                            // Drop a TRUE no-op only: the workbook cell, AS WRITTEN, already equals the model's display
                            // (e.g. cell "6'-8"" and the model shows "6'-8""). ANY other difference is shown — including
                            // a trivial format-only one (cell "7" vs model "7'-0"", same length, different text): the user
                            // must CONFIRM Transom's interpretation every time, never have it silently canonicalized.
                            var modelDisp = param.AsValueString() ?? "";
                            if (ExcelCorrector.SameFormat(cellText, modelDisp)) { /* no-op directly from the workbook */ }
                            else
                            {
                                // The change carries the canonical New + the parsed double actually written. A cell not
                                // already in the schedule's format becomes a PENDING row the user confirms inline
                                // (NeedsConfirm) — incl. the trivial "7"→"7'-0"" case, which confirms then drops (its
                                // confirmed value equals the model, removed by ConfirmRow).
                                var canonical = ExcelCorrector.Canonical(units, new ForgeTypeId(col.SpecTypeId), parsed, cellText);
                                var ch = InstanceChange(el, col, modelDisp, canonical, false, "", parsed);
                                if (!ExcelCorrector.SameFormat(cellText, canonical))
                                {
                                    ch.NeedsConfirm = true;
                                    ch.EnteredValue = cellText;
                                    ch.SpecTypeId = col.SpecTypeId;
                                    ch.EditValue = cellText;     // the box starts at what the user typed; interpreted only on Confirm
                                    ch.Suggestion = canonical;   // the "interpreted as …" line shown until they confirm/re-prompt
                                    ch.Selected = false;
                                }
                                cs.Changes.Add(Mark(doc, ch, elInGroup, elGroupName));
                            }
                        }
                    }
                    // else: non-text/non-numeric (an ElementId / family-or-type selection) can't be set from a cell.
                    // §15-E: such an edit to a non-editable cell DROPS SILENTLY (no frozen change, no diagnostic, no
                    // tally) — it isn't an editable-cell edit, so the principle says surface nothing.
                }
            }

            var typeCounts = TypeInstanceCounts(doc, sheet.Category, typeGroups.Keys);
            ResolveTypeGroups(cs, typeGroups, units, typeCounts);

            // #5: warn when an edit targets a column the schedule filters or sorts/groups on — changing it can
            // drop the row from this schedule or reorder it (e.g. renaming a Type Mark the schedule filters on).
            var sensitive = SensitiveFieldParamIds(doc, sheet.ScheduleUniqueId);
            if (sensitive.Count > 0)
            {
                var flagged = new HashSet<string>();
                for (int i = chg0; i < cs.Changes.Count; i++)
                {
                    var ch = cs.Changes[i];
                    if (ch.Frozen || !sensitive.Contains(ch.ParameterId) || !flagged.Add(ch.Field)) continue;
                    cs.Diagnostics.Add(new CellDiagnostic
                    {
                        SheetTabName = sheet.SheetTabName, ExcelRow = 0, Col = 0, FieldName = ch.Field,
                        ElementLabel = ch.ElementName, Severity = "yellow",
                        Reason = $"'{ch.Field}' drives the schedule's filter or sort/group — editing it may remove " +
                                 "this row from the schedule or reorder it",
                        Value = ch.NewValue,
                    });
                }
            }
            Summarize();
        }

        cs.ImportedScheduleNames = wb.Sheets.Select(s => s.ScheduleName).Distinct().ToList();
        ComputeOption2Eligibility(doc, cs);
        ComputeOption2Mode(doc, cs);
        // Stamp geometry-driving built-in instance changes so the resolution dialog shows the right message + path
        // (Claude-Assist only; option 2 already suppressed above). Matches the export's yellow colouring.
        foreach (var ch in cs.Changes)
            if (!ch.Frozen && ch.GroupMode == GroupMode.BuiltinDance && IsGeometryDrivingBuiltinChange(doc, ch))
                ch.GeometryDriven = true;
        ComputeGroupBroken(doc, cs);
        ComputeDependents(doc, cs);   // CHANGE 2 (§9.4): dependent schedules per driving schedule (API thread, one pass)

        // CHANGE 2 §3b surface-3: keep wb + doc GUID so the copy-log diagnostic can be rebuilt scoped post-selection.
        cs.SourceWorkbook = wb;
        cs.DocCreationGuid = doc.CreationGUID.ToString();
        cs.DiagnosticLog = BuildDiagnosticLog(doc, wb, cs);
        return cs;
    }

    /// <summary>
    ///     §17 FAIL-CLOSED combined-parameter distribution. An edited COMBINED cell is split back into its component
    ///     parts and each part written — but ONLY when the parse is PROVABLY safe (§17.2). The one outcome we must
    ///     never produce is a SILENT WRONG distribution (separator-in-value collision), so this auto-distributes only
    ///     when ALL of (a)-(d) hold; ANY doubt → write NOTHING, surface a user-relevant non-importable diagnostic, and
    ///     let the (hidden) component columns be the editable fallback.
    ///       (a) the separator is PROVABLY ABSENT from every component's CURRENT value AND every edited token,
    ///       (b) every part resolves to a settable param on the element/type,
    ///       (c) every parsed token re-parses cleanly to its component's spec/units,
    ///       (d) the edited string matches the prefix/suffix/separator template EXACTLY (splits into exactly N tokens).
    ///     FORK 3 (per-FIELD component-wins): if ANY of this field's component columns was edited DIRECTLY in this row,
    ///     the combined parse is suppressed for the WHOLE field (the explicit component edit lands instead).
    /// </summary>
    private void HandleCombinedColumn(Document doc, ImportSheet sheet, ImportRow row, ChangeSet cs, ImportColumn col,
        Element el, Dictionary<int, string>? baseRow, string? baseline, string label, Units units,
        bool elInGroup, string elGroupName, string cellText)
    {
        var parts = col.CombinedParts!;

        // Only act on an EDITED combined cell. Unedited → no-op (drift on a combined cell isn't surfaced — its
        // components carry their own drift if any). baseline null → compare against the rendered current value.
        bool edited = baseline != null ? cellText != baseline : !string.IsNullOrEmpty(cellText);
        if (!edited) return;

        // FORK 3: did the user edit any of this field's component columns directly in THIS row? If so, suppress the
        // whole field's combined parse (component-wins per field) — the component columns import on their own.
        foreach (var part in parts)
        {
            var compCol = sheet.Columns.FirstOrDefault(c => c.CombinedParts == null && c.Matched
                && c.ParameterId == part.ParamId && c.ExcelCol >= 0 && c.ExcelCol < row.Cells.Length);
            if (compCol == null) continue;
            var compText = row.Cells[compCol.ExcelCol] ?? "";
            var compBase = baseRow != null && baseRow.TryGetValue(compCol.Col, out var cbv) ? cbv : null;
            bool compEdited = compBase != null ? compText != compBase : !string.IsNullOrEmpty(compText);
            if (compEdited)
            {
                // Component-wins: ignore the combined cell for this field; note it so the user knows why.
                cs.Skipped.Add(new SkippedItem
                {
                    Reason = "combined value ignored",
                    Detail = $"You edited a component column of '{col.FieldName}' directly, so the combined value was " +
                             "ignored for that field — edit the combined cell OR the component columns, not both.",
                });
                return;
            }
        }

        void Refuse(string why)
        {
            // Fail closed: write NOTHING, surface as a user-relevant non-importable (the user edited it, it won't land).
            cs.Skipped.Add(new SkippedItem
            {
                Reason = "can't split combined value",
                Detail = $"Your edit to '{col.FieldName}' ({label}) couldn't be split into its parts safely — {why}. " +
                         "Edit the component columns directly instead (unhide them).",
            });
            cs.Diagnostics.Add(Diag(sheet, row, col, label, "yellow",
                $"combined value can't be split safely — {why}", cellText, category: "advisory"));
        }

        // (d) template split: walk the parts in order, peeling prefix + value + suffix, using the separator BETWEEN
        // consecutive parts. The split must consume the whole string into exactly N tokens.
        var tokens = new string[parts.Count];
        string rest = cellText;
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            if (!string.IsNullOrEmpty(p.Prefix))
            {
                if (!rest.StartsWith(p.Prefix)) { Refuse($"it doesn't match the expected format (prefix '{p.Prefix}')"); return; }
                rest = rest.Substring(p.Prefix.Length);
            }
            string token;
            bool last = i == parts.Count - 1;
            string sep = p.Separator ?? "";
            if (last || string.IsNullOrEmpty(sep))
            {
                token = rest;
                rest = "";
            }
            else
            {
                // (a) separator-in-value collision: if the separator occurs more than once in what remains, the split
                // point is ambiguous → refuse. We require EXACTLY one occurrence at the boundary for this part.
                int first = rest.IndexOf(sep, StringComparison.Ordinal);
                if (first < 0) { Refuse($"it doesn't match the expected format (separator '{sep}' missing)"); return; }
                token = rest.Substring(0, first);
                rest = rest.Substring(first + sep.Length);
            }
            // strip suffix
            if (!string.IsNullOrEmpty(p.Suffix))
            {
                if (!token.EndsWith(p.Suffix)) { Refuse($"it doesn't match the expected format (suffix '{p.Suffix}')"); return; }
                token = token.Substring(0, token.Length - p.Suffix.Length);
            }
            tokens[i] = token.Trim();
        }
        if (rest.Length > 0) { Refuse("it has extra text after the last part"); return; }

        // Resolve each part live + (a) prove the separator is absent from every CURRENT component value + (c) re-parse
        // each token. Build the would-be writes; commit none until ALL parts pass.
        var pending = new List<ProposedChange>();
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            string token = tokens[i];

            var binding = ResolveBindingLive(doc, el, p.ParamId, p.Binding);
            var host = binding == "type"
                ? (el.GetTypeId() != ElementId.InvalidElementId ? doc.GetElement(el.GetTypeId()) : null)
                : binding == "instance" ? el : null;
            var param = host == null ? null : GetParam(host, p.ParamId);
            if (param == null || param.IsReadOnly) { Refuse("one of its parts can't be written on this element"); return; }   // (b)

            // (a) the separator must not appear inside this part's CURRENT value (it would make a future split ambiguous)
            string curVal = param.StorageType == StorageType.String ? (param.AsString() ?? "") : (param.AsValueString() ?? "");
            var psep = p.Separator ?? "";
            if (!string.IsNullOrEmpty(psep) && (curVal.Contains(psep) || token.Contains(psep)))
            { Refuse($"a value contains the separator '{psep}'"); return; }

            // (c) re-parse the token to the part's storage type/spec — same parse the importer uses for a normal write.
            if (param.StorageType == StorageType.String)
            {
                if ((param.AsString() ?? "") == token) continue;   // no-op for this part
                pending.Add(new ProposedChange
                {
                    UniqueId = host!.UniqueId, ParameterId = p.ParamId, Binding = binding, ElementName = SafeName(el),
                    Field = col.FieldName + " · " + (param.Definition?.Name ?? ("part " + p.ParamId)),
                    OldValue = param.AsString() ?? "", NewValue = token, IsString = true, NewString = token,
                });
            }
            else if (param.StorageType == StorageType.Integer)
            {
                if (!TryParseInteger(IsYesNo(param), token, out int iv)) { Refuse($"the part '{token}' isn't a valid number"); return; }
                if (param.AsInteger() == iv) continue;
                pending.Add(new ProposedChange
                {
                    UniqueId = host!.UniqueId, ParameterId = p.ParamId, Binding = binding, ElementName = SafeName(el),
                    Field = col.FieldName + " · " + (param.Definition?.Name ?? ("part " + p.ParamId)),
                    OldValue = CurrentDisplay(param), NewValue = token, IsInt = true, NewInt = iv,
                });
            }
            else if (param.StorageType == StorageType.Double && p.SpecTypeId != null)
            {
                if (!UnitFormatUtils.TryParse(units, new ForgeTypeId(p.SpecTypeId), token, out double parsed))
                { Refuse($"the part '{token}' isn't a valid value"); return; }
                if (Math.Abs(param.AsDouble() - parsed) < 1e-9) continue;
                pending.Add(new ProposedChange
                {
                    UniqueId = host!.UniqueId, ParameterId = p.ParamId, Binding = binding, ElementName = SafeName(el),
                    Field = col.FieldName + " · " + (param.Definition?.Name ?? ("part " + p.ParamId)),
                    OldValue = param.AsValueString() ?? "", NewValue = token, NewDouble = parsed,
                });
            }
            else { Refuse("one of its parts has an unsupported value type"); return; }   // (b)/(c)
        }

        // All parts passed the gate → commit the (changed) component writes via the normal by-uid apply path.
        foreach (var ch in pending) cs.Changes.Add(Mark(doc, ch, elInGroup, elGroupName));
    }

    /// <summary>
    ///     CHANGE 2 (§9.4): for each EDITED (driving) schedule, find the OTHER project schedules that would inherit
    ///     its edits and store them on <see cref="ChangeSet.Dependents"/> (read-only display in the selection step).
    ///     Propagation (§9.2, evidence-based): a TYPE-bound write changes every instance of the type project-wide, so
    ///     any schedule displaying any such instance depends; an INSTANCE/bulk write only reaches the written
    ///     instances, so only a schedule showing one of those exact uids depends. Grouped/shared/computed writes are
    ///     NOT type/instance-keyed → no edge inferred (never a false dependent). A driving schedule is never its own
    ///     dependent. ONE collector pass per project schedule (templates + driving schedules excluded); cost is
    ///     O(sum of displayed elements + driving instances) — no per-type rescan. Runs on the API thread.
    /// </summary>
    private static void ComputeDependents(Document doc, ChangeSet cs)
    {
        try
        {
            // 1. Driving edits keyed per source schedule: type ids (type writes) + instance uids (instance writes).
            //    Only non-frozen, directly-applicable changes drive a dependency (a frozen/blue change writes nothing).
            var drivingTypeIdsBySched = new Dictionary<string, HashSet<long>>();
            var drivingInstUidsBySched = new Dictionary<string, HashSet<string>>();
            foreach (var ch in cs.Changes)
            {
                // §9.2: drivers are ONLY ungrouped, directly-applicable changes. Skip frozen (writes nothing) AND
                // grouped (InGroup covers ProjectVary + BuiltinDance + shared-in-group — set by Mark()): a grouped
                // change is not a clean type/instance-keyed direct write (a BuiltinDance isn't even applied in the
                // direct pass), so inferring a dependent from it would be a FALSE edge. (At this preview-time point
                // no option-2 Resolution is set yet, so a grouped change can't be reclassified to a direct type
                // write here anyway — staying conservative matches §9.2's "don't infer what we can't justify".)
                if (ch.Frozen || ch.InGroup) continue;
                var schedUid = !string.IsNullOrEmpty(ch.SourceScheduleUid) ? ch.SourceScheduleUid : ch.SourceScheduleName;
                if (string.IsNullOrEmpty(schedUid)) continue;
                // TYPE-bound write (incl. option-2 NewTypeParam) → key on the written type id.
                bool typeKeyed = ch.Binding == "type" || ch.Resolution == GroupResolution.NewTypeParam;
                if (typeKeyed && ch.TypeId != 0)
                {
                    if (!drivingTypeIdsBySched.TryGetValue(schedUid, out var set)) drivingTypeIdsBySched[schedUid] = set = new HashSet<long>();
                    set.Add(ch.TypeId);
                }
                else
                {
                    // INSTANCE/bulk write → the specific instance uids it touches.
                    var uids = ch.BulkInstanceIds ?? (string.IsNullOrEmpty(ch.UniqueId) ? null : new List<string> { ch.UniqueId });
                    if (uids == null) continue;
                    if (!drivingInstUidsBySched.TryGetValue(schedUid, out var set)) drivingInstUidsBySched[schedUid] = set = new HashSet<string>();
                    foreach (var u in uids) if (!string.IsNullOrEmpty(u)) set.Add(u);
                }
            }

            var drivingSchedUids = new HashSet<string>(drivingTypeIdsBySched.Keys.Concat(drivingInstUidsBySched.Keys));
            if (drivingSchedUids.Count == 0) return;   // nothing drives anything → no dependents
            bool anyInstanceDrivers = drivingInstUidsBySched.Count > 0;

            // 2. One pass over project schedules (excluding templates + the driving schedules themselves). For each,
            //    derive its displayed TYPE ids and (only if needed) displayed instance uids.
            foreach (var vs in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
            {
                if (vs.IsTemplate) continue;
                var candUid = vs.UniqueId;
                if (drivingSchedUids.Contains(candUid)) continue;   // a driving schedule is never its own dependent

                HashSet<long> shownTypeIds = new();
                HashSet<string>? shownInstUids = anyInstanceDrivers ? new HashSet<string>() : null;
                try
                {
                    foreach (var el in new FilteredElementCollector(doc, vs.Id).WhereElementIsNotElementType())
                    {
                        var tid = el.GetTypeId();
                        if (tid != null && tid != ElementId.InvalidElementId) shownTypeIds.Add(tid.Value);
                        shownInstUids?.Add(el.UniqueId);
                    }
                }
                catch { continue; }   // a schedule that won't enumerate (linked / odd) → skip it as a candidate

                // 3. Attribute this candidate to each driving (parent) schedule it shares a type/instance with.
                foreach (var parentUid in drivingSchedUids)
                {
                    bool depends =
                        (drivingTypeIdsBySched.TryGetValue(parentUid, out var ptypes) && shownTypeIds.Overlaps(ptypes))
                        || (shownInstUids != null && drivingInstUidsBySched.TryGetValue(parentUid, out var pinsts) && shownInstUids.Overlaps(pinsts));
                    if (!depends) continue;
                    if (!cs.Dependents.TryGetValue(parentUid, out var list)) cs.Dependents[parentUid] = list = new List<DependentScheduleRef>();
                    list.Add(new DependentScheduleRef { ScheduleName = vs.Name, ScheduleUid = candUid });
                }
            }
        }
        catch { /* dependents are an informational overlay — never block the change set on them */ }
    }

    /// <summary>
    ///     Marks which group-conflict columns can use "new type parameter" (resolution option 2): only when,
    ///     for every affected type, the column's edited instances share a single value (a type parameter holds
    ///     one value per type). A column with two different values inside one type can't move to a type param.
    /// </summary>
    private static void ComputeOption2Eligibility(Document doc, ChangeSet cs)
    {
        foreach (var pg in cs.Changes
                     .Where(c => !c.Frozen && c.GroupMode is GroupMode.ProjectVary or GroupMode.BuiltinDance)
                     .GroupBy(c => ChangeSet.ColumnKey(c.ParameterId, c.Field)))
        {
            // A geometry-driving built-in instance param can't be moved to a new param without desyncing the model
            // from the schedule (Sill/Head Height drive the 3D) — never offer option 2 for it (Claude-Assist only).
            if (pg.Any(c => IsGeometryDrivingBuiltinChange(doc, c))) continue;
            // If any element's type can't be resolved (deleted/odd), the column isn't safely movable to a type param.
            if (pg.Any(c => ElementTypeIdOf(doc, c) < 0)) continue;

            // Compare the PARSED value (NewString/NewInt/NewDouble within tolerance), not the display text — two
            // cells can render differently yet be the same internal value (and vice-versa).
            bool aligned = pg
                .GroupBy(c => ElementTypeIdOf(doc, c))
                .All(typeGrp => typeGrp.Select(CanonicalValue).Distinct().Count() <= 1);
            if (aligned) cs.Option2EligibleParams.Add(pg.Key);
        }
    }

    /// <summary>
    ///     §4b: per Option-2 column, infers the TYPE-vs-INSTANCE binding from the SOURCE schedule's organization
    ///     and writes it to <see cref="ChangeSet.Option2Modes"/>. A schedule that is grouped-by-type (not itemized
    ///     and has sort/group fields) AND whose source values are uniform per type AND whose edits align per type
    ///     ⇒ <see cref="Option2Mode.AutoType"/> (one type-param option). Otherwise ambiguous: prefer TYPE when the
    ///     schedule is organized by type, else prefer INSTANCE (itemized / not grouped). Columns whose source
    ///     schedule can't be resolved get no entry (treated as None ⇒ option 2 not offered).
    /// </summary>
    private static void ComputeOption2Mode(Document doc, ChangeSet cs)
    {
        foreach (var pg in cs.Changes
                     .Where(c => !c.Frozen && c.GroupMode is GroupMode.ProjectVary or GroupMode.BuiltinDance)
                     .GroupBy(c => ChangeSet.ColumnKey(c.ParameterId, c.Field)))
        {
            var sample = pg.First();
            // Geometry-driving built-in instance params get NO option 2 (replacing them desyncs the 3D) → leave None
            // so only Claude-Assist/Skip are offered, matching the yellow cell colour.
            if (pg.Any(c => IsGeometryDrivingBuiltinChange(doc, c))) continue;
            var vs = ResolveSchedule(doc, sample.SourceScheduleUid, sample.SourceScheduleName);
            if (vs == null) continue; // can't resolve the schedule → leave None (option 2 not offered)

            bool organizedByType;
            try
            {
                var def = vs.Definition;
                organizedByType = !def.IsItemized && def.GetSortGroupFields().Count > 0;
            }
            catch { organizedByType = false; }

            bool editsAligned = cs.Option2EligibleParams.Contains(pg.Key);
            bool sourceAligned = SourceUniformPerType(doc, vs, sample.ParameterId);

            var mode = organizedByType && sourceAligned && editsAligned ? Option2Mode.AutoType
                     : organizedByType ? Option2Mode.AmbiguousPreferType
                     : Option2Mode.AmbiguousPreferInstance;
            cs.Option2Modes[pg.Key] = mode;
        }
    }

    /// <summary>
    ///     True when, for every type appearing in <paramref name="vs"/>'s elements, the source parameter's STORED
    ///     value is uniform (≤1 distinct canonical value per type). Uses the SAME canonicalization as the merge
    ///     write (trimmed string / int / double rounded to 1e-9 — <see cref="CanonicalParsed"/>), not rendered
    ///     cell text, so the uniformity test and the carry-forward write agree.
    /// </summary>
    private static bool SourceUniformPerType(Document doc, ViewSchedule vs, int paramId)
    {
        try
        {
            var byType = new Dictionary<long, HashSet<string>>();
            foreach (var el in new FilteredElementCollector(doc, vs.Id).WhereElementIsNotElementType())
            {
                var tid = el.GetTypeId();
                long k = tid != null && tid != ElementId.InvalidElementId ? tid.Value : -1;
                if (!byType.TryGetValue(k, out var set)) byType[k] = set = new HashSet<string>();
                set.Add(CanonicalParsed(ReadLive(el, paramId)));
                if (set.Count > 1) return false; // a type with two distinct stored values → not uniform
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    ///     Applies the round-trippable column-caption edits (<see cref="ChangeSet.HeaderChanges"/>) by setting
    ///     <c>ScheduleField.ColumnHeading</c> on the live schedule. Must run inside an open transaction. The field
    ///     is located by ParameterId (rename-safe — the edited heading is NOT used to match); for a combined column
    ///     (no single parameter, ParameterId &lt; 0) it falls back to the visible field at the change's column index.
    ///     Each write is verified by re-read. Returns the count newly Applied; appends Failed/Unverified labels to
    ///     the shared lists (so they fold into the apply headline + log exactly like data changes).
    /// </summary>
    private int ApplyHeaderChanges(Document doc, ChangeSet cs, List<string> failed, List<string> unverified)
    {
        int applied = 0;
        foreach (var hc in cs.HeaderChanges)
        {
            if (!hc.Selected || hc.OutcomeNote == "skipped") continue;
            string label = $"header '{hc.OldHeading}' → '{hc.NewHeading}'" +
                           (string.IsNullOrEmpty(hc.ScheduleName) ? "" : $" ({hc.ScheduleName})");
            var vs = ResolveSchedule(doc, hc.ScheduleUid, hc.ScheduleName);
            if (vs == null)
            {
                failed.Add(label + " — schedule not found");
                hc.Outcome = ApplyOutcome.Failed; hc.OutcomeNote = "schedule not found";
                continue;
            }

            if (hc.Kind == HeaderChangeKind.GroupHeader)
            {
                // Merged super-header: change its caption via GroupHeaders. An ALREADY-grouped span must be
                // UNgrouped first — Revit's CanGroupHeaders is false until then and GroupHeaders throws "Headers
                // could not be grouped" (live-verified). So ungroup (if grouped) then re-group with the new caption.
                try
                {
                    if (vs.CanUngroupHeaders(hc.Top, hc.Left, hc.Bottom, hc.Right))
                        vs.UngroupHeaders(hc.Top, hc.Left, hc.Bottom, hc.Right);
                    vs.GroupHeaders(hc.Top, hc.Left, hc.Bottom, hc.Right, hc.NewHeading);
                }
                catch (Exception ex)
                {
                    failed.Add(label + " — " + ex.Message);
                    hc.Outcome = ApplyOutcome.Failed; hc.OutcomeNote = ex.Message;
                    continue;
                }
                string afterG;
                try { afterG = vs.GetCellText(SectionType.Body, hc.Top, hc.Left) ?? ""; } catch { afterG = ""; }
                hc.Outcome = ApplyOutcome.Applied; applied++;
                if (!string.IsNullOrEmpty(afterG) && afterG != hc.NewHeading) hc.OutcomeNote = $"saved as '{afterG}'";
                continue;
            }

            var field = ResolveHeaderField(vs.Definition, hc);
            if (field == null)
            {
                failed.Add(label + " — column not found");
                hc.Outcome = ApplyOutcome.Failed; hc.OutcomeNote = "column not found";
                continue;
            }

            try
            {
                field.ColumnHeading = hc.NewHeading;
            }
            catch (Exception ex)
            {
                // A caption Revit refuses (e.g. a duplicate-heading constraint on some schedule kinds): report, don't crash.
                failed.Add(label + " — " + ex.Message);
                hc.Outcome = ApplyOutcome.Failed; hc.OutcomeNote = ex.Message;
                continue;
            }

            // Verify by re-read — ColumnHeading can be trimmed/normalized by Revit.
            string after;
            try { after = field.ColumnHeading ?? ""; } catch { after = ""; }
            if (after == hc.NewHeading)
            {
                hc.Outcome = ApplyOutcome.Applied; applied++;
            }
            else
            {
                // Revit accepted but coerced the text (e.g. whitespace-normalized) — treat as applied-with-note,
                // not failed, since the heading DID change; record what actually stuck.
                hc.Outcome = ApplyOutcome.Applied; applied++;
                if (!string.IsNullOrEmpty(after) && after != hc.NewHeading)
                    hc.OutcomeNote = $"saved as '{after}'";
            }
        }
        return applied;
    }

    /// <summary>Finds the <see cref="ScheduleField"/> a <see cref="HeaderChange"/> targets: by ParameterId (the
    /// column's stable identity), or — for a combined column (ParameterId &lt; 0, no single param) — the visible
    /// field at the change's exported column index. Null when no field matches (column removed since export).</summary>
    private static ScheduleField? ResolveHeaderField(ScheduleDefinition def, HeaderChange hc)
    {
        if (hc.ParameterId >= 0)
        {
            foreach (var fid in def.GetFieldOrder())
            {
                var f = def.GetField(fid);
                if (f != null && !f.IsHidden && (int)f.ParameterId.Value == hc.ParameterId) return f;
            }
            return null;
        }
        // Combined / no-paramId column: match by visible position (same ordering ExcelReader exported).
        int vc = 0;
        foreach (var fid in def.GetFieldOrder())
        {
            var f = def.GetField(fid);
            if (f == null || f.IsHidden) continue;
            if (vc == hc.ExcelCol) return f;
            vc++;
        }
        return null;
    }

    /// <summary>Resolves a source schedule by UniqueId first (survives renames), else by Name; never a template.</summary>
    private static ViewSchedule? ResolveSchedule(Document doc, string uid, string name)
    {
        if (!string.IsNullOrEmpty(uid) && doc.GetElement(uid) is ViewSchedule byUid && !byUid.IsTemplate)
            return byUid;
        if (!string.IsNullOrEmpty(name))
            return new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);
        return null;
    }

    /// <summary>
    ///     Flags every grouped change whose model group trips the HARD dance gate — one the definition-swap dance
    ///     cannot faithfully reproduce (a level-anchored "broken family", true rotation, or multi-level placement;
    ///     see <see cref="GroupSafety"/>). Gated columns drop option 3 (dance) and route to option 2 / Claude-Assist.
    ///     Mirror-only and bystander-nested groups are NOT gated here (they dance — STEP-14b is the arbiter). When a
    ///     group is gated by anchored families, the named families are carried so the dialog/report can tell the user
    ///     exactly what to re-host. Cached per group TYPE across the change set. (Export RED colour is computed
    ///     separately from the broader <see cref="GroupSafetyResult.IsBroken"/> in ScheduleReader.)
    /// </summary>
    private static void ComputeGroupBroken(Document doc, ChangeSet cs)
    {
        var cache = new Dictionary<long, GroupSafetyResult>();
        GroupSafetyResult Safety(ElementId typeId)
        {
            long k = typeId.Value;
            if (!cache.TryGetValue(k, out var res)) cache[k] = res = GroupSafety.AnalyzeType(doc, typeId);
            return res;
        }
        foreach (var ch in cs.Changes)
        {
            if (!ch.InGroup) continue;
            var memberUids = ch.BulkInstanceIds ?? new List<string> { ch.UniqueId };
            foreach (var uid in memberUids)
            {
                if (string.IsNullOrEmpty(uid)) continue;
                var gid = doc.GetElement(uid)?.GroupId;
                if (gid == null || gid == ElementId.InvalidElementId) continue;
                if (doc.GetElement(gid) is not Group g) continue;
                var s = Safety(g.GetTypeId());
                if (s.IsDanceGateBroken)
                {
                    ch.GroupBroken = true;
                    ch.GroupBrokenReason = s.Explain();
                    if (s.HasAnchored)
                        ch.GroupBrokenFamilies = string.Join("; ", s.BrokenFamilies.Select(f => f.ToString()));
                    break;
                }
            }
        }
    }

    /// <summary>Canonical string of a change's parsed value, so eligibility compares stored values (doubles
    /// rounded to internal-unit tolerance), not formatted display text.</summary>
    private static string CanonicalValue(ProposedChange ch) =>
        ch.IsString ? ch.NewString
        : ch.IsInt ? ch.NewInt.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : Math.Round(ch.NewDouble, 9).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The type id of the element(s) a group-conflict change targets (bulk = the shared type; single
    /// = the instance's type). -1 if it can't be resolved.</summary>
    private static long ElementTypeIdOf(Document doc, ProposedChange ch)
    {
        try
        {
            var uid = ch.BulkInstanceIds is { Count: > 0 } ? ch.BulkInstanceIds[0] : ch.UniqueId;
            var el = string.IsNullOrEmpty(uid) ? null : doc.GetElement(uid);
            var tid = el?.GetTypeId();
            return tid != null && tid != ElementId.InvalidElementId ? tid.Value : -1;
        }
        catch { return -1; }
    }

    /// <summary>True when a change targets a GEOMETRY-driving built-in INSTANCE parameter (a measurable Double on a
    /// negative-id built-in — Sill/Head Height, offsets). These can't be resolved by option 2 (a new instance param
    /// wouldn't drive the model → the schedule desyncs from the 3D geometry), so option 2 must NOT be offered for
    /// them; the only honest in-group edit is Claude-Assist (Edit Group mode). Mirrors ScheduleReader's export rule
    /// (yellow), so the import's option-gating matches the cell colour. Resolves the param off a representative
    /// element of the change.</summary>
    private static bool IsGeometryDrivingBuiltinChange(Document doc, ProposedChange ch)
    {
        if (ch.ParameterId >= 0) return false;   // project/shared params vary → not this case
        try
        {
            var uid = ch.BulkInstanceIds is { Count: > 0 } ? ch.BulkInstanceIds[0] : ch.UniqueId;
            var el = string.IsNullOrEmpty(uid) ? null : doc.GetElement(uid);
            var p = el == null ? null : GetParam(el, ch.ParameterId);
            if (p == null || p.StorageType != StorageType.Double) return false;
            var s = p.Definition.GetDataType();
            return s != null && !s.Empty() && UnitUtils.IsMeasurableSpec(s);
        }
        catch { return false; }
    }

    /// <summary>
    ///     Builds a copyable plain-text diagnostic: per-sheet column matching + anchor resolution, then the
    ///     change/skip/conflict/diagnostic tallies. Surfaces the common failure modes (no header row, renamed
    ///     columns, missing anchor, cross-model) in words, so a failed import can be diagnosed from the log alone.
    /// </summary>
    private static string BuildDiagnosticLog(Document doc, ImportWorkbook wb, ChangeSet cs)
        => BuildDiagnosticLog(wb, doc.CreationGUID.ToString(), cs, null);

    /// <summary>
    ///     CHANGE 2 §3b surface-3: the copy-log diagnostic. <paramref name="selectedScheduleUids"/> null = the full
    ///     pre-selection log (unchanged). Non-null (SUBSET mode) = REBUILT scoped to the selected schedules: only
    ///     their sheet sections are enumerated, the "skipped:" count + listing is filtered to skips whose tab belongs
    ///     to a selected schedule (FAIL-OPEN: an unattributed/unknown-tab skip is kept), and the header says
    ///     "skipped (selected schedules only): N". Doc-free (uses the captured GUID) so the VM can call it on the UI
    ///     thread post-selection. The three skip surfaces (panel, status count, this) all read the same selected set,
    ///     so their counts agree.
    /// </summary>
    public static string BuildDiagnosticLog(ImportWorkbook wb, string docGuid, ChangeSet cs,
        IReadOnlyCollection<string>? selectedScheduleUids)
    {
        bool subset = selectedScheduleUids != null;
        // Map a tab → its schedule uid (via the sheet metadata) so skips (tab-keyed) can be tested against the
        // selected-schedule-uid set. A selected schedule's sheet always passes; an unattributed/unknown tab is kept.
        bool SheetSelected(string scheduleUid) => !subset || selectedScheduleUids!.Contains(scheduleUid);
        bool SkipSelected(string tab)
        {
            if (!subset || string.IsNullOrEmpty(tab)) return true;   // all-selected, or unattributed → keep (fail-open)
            var sh = wb.Sheets.FirstOrDefault(s => s.SheetTabName == tab);
            if (sh == null) return true;                              // unknown tab → keep (fail-open)
            return selectedScheduleUids!.Contains(sh.ScheduleUniqueId);
        }
        // §12 (user rule 2026-06-14): the user-facing diagnostic shows ONLY the user's data-edit skips — drop
        // structural/back-end skips (UserRelevant==false) FIRST, in BOTH modes (this filter is independent of and
        // prior to the subset selected-scope, exactly as the on-screen panel composes them in ShowPreview). So the
        // diagnostic "skipped: N" + listing matches the panel + status count (all three off the same UserRelevant set).
        var userSkips = cs.Skipped.Where(s => s.UserRelevant);
        var scopedSkips = subset ? userSkips.Where(s => SkipSelected(s.SheetTabName)).ToList() : userSkips.ToList();
        var shownSheets = subset ? wb.Sheets.Where(s => SheetSelected(s.ScheduleUniqueId)).ToList() : wb.Sheets.ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Transom import diagnostic — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                      + (subset ? "  (selected schedules only)" : ""));
        sb.AppendLine("Workbook: " + wb.Path);
        sb.AppendLine($"Source model GUID: {wb.SourceModelGuid}  |  current doc: {docGuid}"
                      + (cs.CrossModel ? "  ⚠ CROSS-MODEL (matched by name)" : ""));
        sb.AppendLine($"Sheets: {(subset ? $"{shownSheets.Count} selected of {wb.Sheets.Count}" : wb.Sheets.Count.ToString())}");

        foreach (var sheet in shownSheets)
        {
            sb.AppendLine();
            sb.AppendLine($"== Sheet: {sheet.ScheduleName}  (tab '{sheet.SheetTabName}') ==");
            sb.AppendLine($"  roundTrippable: {sheet.RoundTrippable}");
            sb.AppendLine($"  anchor column index: {sheet.AnchorCol}  (sentinel '{ScheduleReader.AnchorSentinel}')");
            sb.AppendLine($"  data rows anchored: {sheet.Rows.Count}");
            if (sheet.MergedCells.Count > 0)
                sb.AppendLine($"  merged data cells (skipped, not imported): {sheet.MergedCells.Count}");
            if (sheet.DuplicateUids.Count > 0)
                sb.AppendLine($"  duplicate anchor rows (all copies dropped): {sheet.DuplicateUids.Count}");
            sb.AppendLine("  current sheet header row: ["
                          + string.Join(" | ", sheet.CurrentHeaders.Select(h => string.IsNullOrEmpty(h) ? "∅" : h)) + "]");

            int byPos = sheet.Columns.Count(c => c.MatchedByPosition);
            int unmatched = sheet.Columns.Count(c => c.Writable && !c.Matched);
            sb.AppendLine($"  columns ({sheet.Columns.Count}):");
            foreach (var col in sheet.Columns)
                sb.AppendLine($"    [col {col.Col}] field '{col.FieldName}' header '{col.Header}' "
                              + $"writable={col.Writable} binding={col.Binding} -> "
                              + (!col.Matched ? "NOT MATCHED"
                                 : col.MatchedByPosition ? $"matched at sheet col {col.ExcelCol} (BY POSITION — header text didn't match)"
                                 : $"matched at sheet col {col.ExcelCol} (by header)"));

            if (byPos > 0)
                sb.AppendLine($"  note: {byPos} column(s) matched by position, not header text. This is normal for a "
                              + "schedule with column headers turned off, or a banded/multi-row header (the leaf field "
                              + "names sit in a second header row, so the rendered top row carries super-headers/blanks).");
            if (unmatched > 0)
                sb.AppendLine($"  ! {unmatched} writable column(s) could NOT be matched — their edits are skipped. "
                              + "This happens when the sheet's columns were reordered AND a header was renamed; "
                              + "Transom won't guess a position in that case.");
        }

        // §3b EXHAUSTIVE scope (integ1-2 mandate): every Result count + list reads the SELECTED-scoped collection in
        // subset mode (else global). A change is selected by SourceScheduleUid; a conflict by ScheduleUid; a
        // diagnostic/reformat by its SheetTabName (same SkipSelected fail-open rule). All-selected → these reduce to
        // the global collections (no behavior change).
        var scopedChanges = subset ? cs.Changes.Where(c => selectedScheduleUids!.Contains(c.SourceScheduleUid)).ToList() : cs.Changes.ToList();
        var scopedConflicts = subset ? cs.Conflicts.Where(c => selectedScheduleUids!.Contains(c.ScheduleUid)).ToList() : cs.Conflicts.ToList();
        var scopedDiags = subset ? cs.Diagnostics.Where(d => SkipSelected(d.SheetTabName)).ToList() : cs.Diagnostics.ToList();
        var scopedReformats = subset ? cs.Reformats.Where(r => SkipSelected(r.SheetTabName)).ToList() : cs.Reformats.ToList();

        sb.AppendLine();
        sb.AppendLine("== Result ==" + (subset ? "  (selected schedules only)" : ""));
        sb.AppendLine($"  changes proposed: {scopedChanges.Count}");   // §15-E: no "frozen" — read-only edits drop silently
        sb.AppendLine($"  conflicts: {scopedConflicts.Count}");
        int red = scopedDiags.Count(d => d.Severity == "red");
        int drift = scopedDiags.Count(d => d.Severity == "yellow" && d.Category == "drift");
        int advisory = scopedDiags.Count(d => d.Severity == "yellow" && d.Category != "drift");
        // C3 (H4): the leading total previously dropped the BLUE severity, so the breakdown didn't sum to it
        // (retest read "7 (0 red / 0 drift / 6 advisory)" — the 7th was the hidden frozen cell). Count blue and
        // label it "frozen/skipped": most blue sites pair with a FrozenChange, but the "instance scope ambiguous"
        // site emits a blue diagnostic with no FrozenChange, so blue ≥ the (frozen: N) count above — the broader
        // label stays honest. Now red+drift+advisory+frozen == diagnostics count exactly. (ux1 wording.)
        int frozen = scopedDiags.Count(d => d.Severity == "blue");
        sb.AppendLine($"  cell diagnostics: {scopedDiags.Count} ({red} red / {drift} drift / {advisory} advisory / {frozen} frozen/skipped)");
        // §3b surface-3: scoped skip count + listing in subset mode; the header names the scope so a copied log
        // is self-explanatory (and the count agrees with the on-screen panel + status count, all off the same set).
        sb.AppendLine(subset ? $"  skipped (selected schedules only): {scopedSkips.Count}" : $"  skipped: {scopedSkips.Count}");
        foreach (var s in scopedSkips)
            sb.AppendLine($"    - [{s.Reason}] {s.Detail}");
        sb.AppendLine($"  reformat suggestions (parse OK, wrong unit format — confirm in the fix pane): {scopedReformats.Count}");
        foreach (var r in scopedReformats)
            sb.AppendLine($"    - tab '{r.SheetTabName}' R{r.ExcelRow}C{r.ExcelCol} '{r.FieldName}' on {r.ElementLabel}: entered '{r.Entered}' → suggested '{r.Canonical}'");

        return sb.ToString();
    }

    /// <summary>
    ///     Grouped schedules: the row maps to a type. Type-parameter edits write once to the type (via the
    ///     shared conflict-grouping path); instance-parameter edits bulk-apply the value to every instance the
    ///     row represents (the list captured at export), with deleted/unwritable instances flagged.
    /// </summary>
    private void HandleTypeRow(Document doc, ImportSheet sheet, ImportRow row, ChangeSet cs,
        Dictionary<(long, int), TypeCandidate> typeGroups, Units units,
        Dictionary<int, string>? rowBindings, Dictionary<int, string>? baseRow)
    {
        var label = row.Cells.FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? row.UniqueId;
        bool isGroup = row.Kind == "group";   // field-grouped row: no type, every column is an instance-bulk write
        var typeEl = isGroup ? null : doc.GetElement(row.UniqueId);

        // Resolve bindings against a live instance (also the write host for field-group rows, which have no type).
        Element? reprInst = row.InstanceIds is { Count: > 0 } ? doc.GetElement(row.InstanceIds[0]) : null;

        if (!isGroup && typeEl == null)
        {
            cs.Skipped.Add(new SkippedItem { Reason = "type deleted", Detail = label });
            foreach (var col in sheet.Columns.Where(c => c.Writable && c.Matched && c.ExcelCol < row.Cells.Length))
                cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "type no longer exists", row.Cells[col.ExcelCol]));
            return;
        }
        if (isGroup && reprInst == null)
        {
            cs.Skipped.Add(new SkippedItem { Reason = "group elements deleted", Detail = label });
            return;
        }

        var nameEl = (typeEl ?? reprInst)!;   // non-null: type rows have a type, group rows have a live instance

        // #3: a multi-type row (a Type Mark shared by 2+ types) fans each TYPE edit out to EVERY aggregated type;
        // single-type rows fall back to the one anchor. ResolveTypeGroups then skips any type the value already matches.
        void RecordTypeAll(ImportColumn c, string text)
        {
            var targets = row.AggregatedTypeUids ?? new List<string> { row.UniqueId };
            foreach (var tuid in targets)
            {
                var te = doc.GetElement(tuid);
                var tp = te == null ? null : GetParam(te, c.ParameterId);
                if (tp != null && !tp.IsReadOnly) RecordType(typeGroups, sheet, c, te, tp, label, row.ExcelRow, text);
            }
        }

        // Count this row against its type(s) WITHOUT recording an edit — for an UNEDITED type cell, so the conflict
        // check knows the row exists (and would be clobbered by a type write). Mirrors RecordTypeAll's type fan-out.
        void RegisterTypeAll(ImportColumn c)
        {
            var targets = row.AggregatedTypeUids ?? new List<string> { row.UniqueId };
            foreach (var tuid in targets)
            {
                var te = doc.GetElement(tuid);
                var tp = te == null ? null : GetParam(te, c.ParameterId);
                if (tp != null && !tp.IsReadOnly) EnsureTypeCandidate(typeGroups, sheet, c, te, tp);
            }
        }

        foreach (var col in sheet.Columns)
        {
            if (!col.Writable || !col.Matched || col.ExcelCol >= row.Cells.Length) continue;
            var cellText = row.Cells[col.ExcelCol] ?? "";
            if (MergedSkip(sheet, row, col, cs, label, cellText)) continue;
            var baseline = baseRow != null && baseRow.TryGetValue(col.Col, out var bv) ? bv : null;
            var binding = reprInst != null
                ? ResolveBindingLive(doc, reprInst, col.ParameterId, col.Binding)
                : (rowBindings != null && rowBindings.TryGetValue(col.Col, out var rbv) ? rbv : col.Binding);
            if (isGroup) binding = "instance"; // field-group rows have no type; everything is instance-bulk

            if (binding == "type")
            {
                // binding is never "type" for group rows (forced to instance), so typeEl is non-null here.
                var param = GetParam(typeEl!, col.ParameterId);
                if (param == null)
                {
                    if (baseline == null || cellText != baseline)
                    {
                        cs.Skipped.Add(new SkippedItem { Reason = "parameter not found", Detail = $"{col.FieldName} ({label})" });
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "parameter not found", cellText));
                    }
                    continue;
                }
                var current = CurrentDisplay(param);
                bool edited = baseline != null ? cellText != baseline : cellText != current;
                // §15-E: an edit to a READ-ONLY type cell DROPS SILENTLY (no change, no skip, no diagnostic, no tally).
                if (param.IsReadOnly) continue;
                // §15-C: drift only on a cell the user did NOT edit. (Skip the "<varies>" sentinel — see element-row site.)
                // §15-C gap fix: also suppress drift when the workbook cell ALREADY MATCHES the current model value
                // (`Drifted(param, cellText, …)` false) — the user's own earlier-applied edit, not a model deviation.
                if (!edited && baseline != null && baseline != VariesSentinel
                    && Drifted(param, baseline, units, col.SpecTypeId)
                    && Drifted(param, cellText, units, col.SpecTypeId))
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "yellow",
                        $"changed since export (was '{baseline}', now '{current}')", cellText, category: "drift"));
                if (!edited) { RegisterTypeAll(col); continue; }   // count the unedited row for the type-conflict check

                if (param.StorageType == StorageType.String)
                    RecordTypeAll(col, cellText);
                else if (param.StorageType == StorageType.Double && col.SpecTypeId != null)
                    // Record the cell EVEN IF it doesn't parse — an unreadable value ("ABC") must still reach the
                    // type-conflict machinery so it shows up as a pickable option and then an inline confirm row, not
                    // get dropped to the bottom fix-pane. Parsing is resolved on the confirm line, never here.
                    RecordTypeAll(col, cellText);
                // else: non-text/non-numeric type cell — §15-E drops the edit silently (no frozen change/diagnostic).
            }
            else if (binding == "instance")
            {
                bool edited = baseline != null ? cellText != baseline : !string.IsNullOrEmpty(cellText);
                if (!edited) continue;

                // Multi-row grouped types now carry their own hidden-group instance subset (resolved at export), so a
                // null/empty list is a genuine resolution failure (not the old "ambiguous scope" — that case is gone).
                if (row.InstanceIds == null || row.InstanceIds.Count == 0)
                {
                    cs.Skipped.Add(new SkippedItem { Reason = "instances unresolved", Detail = $"{col.FieldName} ({label}) — couldn't resolve this row's instances" });
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "skipped — couldn't resolve this row's instances", cellText));
                    continue;
                }

                // Resolve the live instances; a representative drives storage-type / read-only checks.
                var ids = row.InstanceIds.Where(uid => doc.GetElement(uid) != null).ToList();
                int missing = row.InstanceIds.Count - ids.Count;
                if (missing > 0)
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "yellow",
                        $"{missing} of {row.InstanceIds.Count} instance(s) no longer exist", cellText));
                if (ids.Count == 0)
                {
                    cs.Skipped.Add(new SkippedItem { Reason = "all instances deleted", Detail = $"{col.FieldName} ({label})" });
                    continue;
                }

                var repr = doc.GetElement(ids[0]);
                var rparam = repr == null ? null : GetParam(repr, col.ParameterId);
                if (rparam == null)
                {
                    cs.Skipped.Add(new SkippedItem { Reason = "parameter not found", Detail = $"{col.FieldName} ({label})" });
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "parameter not found", cellText));
                    continue;
                }
                // §15-E: an edit to a READ-ONLY instance-bulk cell DROPS SILENTLY (no change, skip, diagnostic, tally).
                if (rparam.IsReadOnly) continue;

                var oldDisp = string.IsNullOrEmpty(baseline) ? "(varies)" : baseline;
                bool isString = false, isInt = false;
                string str = "";
                double dbl = 0;
                int iv = 0;
                // Pending-confirm state for a non-canonical numeric entry on this bulk cell (set in the Double branch).
                bool bulkNeedsConfirm = false;
                string bulkEntered = "";
                string cellDisp = cellText;   // what the change shows as New (canonical when the entry was reformatted)
                if (rparam.StorageType == StorageType.String) { isString = true; str = cellText; }
                else if (rparam.StorageType == StorageType.Integer)
                {
                    isInt = true;
                    if (!TryParseInteger(IsYesNo(rparam), cellText, out iv))
                    {
                        cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{col.FieldName} = '{cellText}'" });
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "value can't be parsed", cellText));
                        continue;
                    }
                }
                else if (rparam.StorageType == StorageType.Double && col.SpecTypeId != null)
                {
                    if (!UnitFormatUtils.TryParse(units, new ForgeTypeId(col.SpecTypeId), cellText, out double parsed))
                    {
                        cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{col.FieldName} = '{cellText}'" });
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "value can't be parsed", cellText));
                        continue;
                    }
                    var canonical = ExcelCorrector.Canonical(units, new ForgeTypeId(col.SpecTypeId), parsed, cellText);
                    // Non-canonical entry → the resulting bulk change is shown PENDING (confirm inline), not parked in
                    // a separate fix pane. The written value is the canonical 'parsed' double either way; display the
                    // canonical form and remember the raw entry for the confirm strip.
                    if (!ExcelCorrector.SameFormat(cellText, canonical))
                    {
                        bulkNeedsConfirm = true;
                        bulkEntered = cellText;
                        cellDisp = canonical;
                    }
                    dbl = parsed;
                }
                else
                    // §15-E: non-text/non-numeric instance-bulk cell — the edit DROPS SILENTLY (no frozen change/diagnostic).
                    continue;

                // Idempotency: drop instances that ALREADY hold the new value (e.g. a prior option-1
                // vary apply already wrote them). Without this, a re-preview re-proposes the change —
                // the baseline is the frozen export value, so cellText != baseline stays true forever;
                // only a live-value compare can tell the write already happened. Mirrors the live-value
                // no-op guards on the normal-element path (String/Int/Double) and ResolveTypeGroups.
                var pending = new List<string>();
                int already = 0;
                foreach (var uid in ids)
                {
                    var inst = doc.GetElement(uid);
                    var ip = inst == null ? null : GetParam(inst, col.ParameterId);
                    if (ip != null && AlreadyHas(ip, isString, str, isInt, iv, dbl)) { already++; continue; }
                    pending.Add(uid);
                }
                // Every targeted instance already matches -> nothing to write. §15-C: this is an EDITED cell (the user's
                // intended value already present), so it is NOT drift — surface nothing (drift is only for cells the
                // user did NOT edit).
                if (pending.Count == 0)
                    continue;

                // Group members can't be written directly — split them out so the rest still applies.
                var ungrouped = new List<string>();
                var grouped = new List<string>();
                string gName = "";
                foreach (var uid in pending)
                {
                    var inst = doc.GetElement(uid);
                    var (gi, gn) = inst == null ? (false, "") : GroupInfo(doc, inst);
                    if (gi) { grouped.Add(uid); if (gName == "") gName = gn; }
                    else ungrouped.Add(uid);
                }
                if (ungrouped.Count > 0)
                {
                    var bc = BulkChange(nameEl, col, ungrouped, oldDisp, cellDisp, isString, str, dbl, isInt, iv, row.UniqueId);
                    if (bulkNeedsConfirm) { bc.NeedsConfirm = true; bc.EnteredValue = bulkEntered; bc.SpecTypeId = col.SpecTypeId ?? ""; bc.EditValue = bulkEntered; bc.Suggestion = cellDisp; bc.Selected = false; }
                    cs.Changes.Add(bc);
                }
                if (grouped.Count > 0)
                {
                    var bc = BulkChange(nameEl, col, grouped, oldDisp, cellDisp, isString, str, dbl, isInt, iv, row.UniqueId);
                    if (bulkNeedsConfirm) { bc.NeedsConfirm = true; bc.EnteredValue = bulkEntered; bc.SpecTypeId = col.SpecTypeId ?? ""; bc.EditValue = bulkEntered; bc.Suggestion = cellDisp; bc.Selected = false; }
                    cs.Changes.Add(Mark(doc, bc, true, gName));
                }
            }
            // binding == "none" -> parameter lives on neither host for this type; nothing to write.
        }
    }

    /// <summary>
    ///     An edited group HEADER: bulk-write the grouping parameter to every element under that header
    ///     (e.g. recategorize all sheets in "8-ELECTRICAL"). The value cell maps to the group field, not the
    ///     header cell's own column. Reuses the verified bulk-instance write path via <see cref="ProposedChange"/>.
    /// </summary>
    private void HandleGroupHeaderRow(Document doc, ImportSheet sheet, ImportRow row, ChangeSet cs, Units units)
    {
        var ghe = row.GroupHeaderEdit!;
        if (ghe.Col < 0 || ghe.Col >= row.Cells.Length) return;
        var cellText = row.Cells[ghe.Col] ?? "";

        // Group-header rows carry a synthetic unique anchor (no InstanceIds), so BaselineRowKey returns row.UniqueId
        // — identical to the historical lookup; via the helper so every site derives the key the same way.
        sheet.Baseline.TryGetValue(ScheduleReader.BaselineRowKey(row.UniqueId, row.Kind, row.InstanceIds), out var baseRow);
        var baseline = baseRow != null && baseRow.TryGetValue(ghe.Col, out var bv) ? bv : null;
        bool edited = baseline != null ? cellText != baseline : !string.IsNullOrEmpty(cellText);
        if (!edited) return;

        var label = "group " + (string.IsNullOrEmpty(baseline) ? cellText : baseline);

        var ids = ghe.InstanceIds.Where(uid => doc.GetElement(uid) != null).ToList();
        int missing = ghe.InstanceIds.Count - ids.Count;
        if (ids.Count == 0)
        {
            cs.Skipped.Add(new SkippedItem { Reason = "all members deleted", Detail = $"{ghe.FieldName} ({label})" });
            return;
        }

        var repr = doc.GetElement(ids[0]);
        var rparam = repr == null ? null : GetParam(repr, ghe.ParameterId);
        if (rparam == null)
        {
            cs.Skipped.Add(new SkippedItem { Reason = "parameter not found", Detail = $"{ghe.FieldName} ({label})" });
            return;
        }
        // §15-E: a read-only group-header param → the edit DROPS SILENTLY (not a skip, not shown). Read-only cells
        // are never the user's editable edit, so per the guiding principle we surface nothing.
        if (rparam.IsReadOnly) return;

        var oldDisp = string.IsNullOrEmpty(baseline) ? CurrentDisplay(rparam) : baseline;
        bool isString = false, isInt = false; string str = ""; double dbl = 0; int iv = 0;
        // Pending-confirm state — an unreadable or non-canonical group-header value becomes an inline PENDING row
        // (box + Confirm) like every other path, instead of dropping to a skip. Parsing is resolved on the confirm
        // line, never here. (Only matters for field-grouped schedules — the door schedule has no group-header rows.)
        bool ghNeedsConfirm = false; string ghEntered = ""; string ghSuggestion = ""; string ghError = ""; string ghDisp = cellText;
        if (rparam.StorageType == StorageType.String) { isString = true; str = cellText; }
        else if (rparam.StorageType == StorageType.Integer)
        {
            isInt = true;
            if (!TryParseInteger(IsYesNo(rparam), cellText, out iv))
            {
                ghNeedsConfirm = true; ghEntered = cellText; ghDisp = "";
                ghError = $"enter a usable value for {ghe.FieldName}";
            }
        }
        else if (rparam.StorageType == StorageType.Double && ghe.SpecTypeId != null)
        {
            if (!UnitFormatUtils.TryParse(units, new ForgeTypeId(ghe.SpecTypeId), cellText, out double parsed))
            {
                ghNeedsConfirm = true; ghEntered = cellText; ghDisp = "";
                ghError = $"enter a usable value for {ghe.FieldName} (e.g. 7' or 7\")";
            }
            else
            {
                dbl = parsed;
                var canonical = ExcelCorrector.Canonical(units, new ForgeTypeId(ghe.SpecTypeId), parsed, cellText);
                if (!ExcelCorrector.SameFormat(cellText, canonical))   // readable but not in schedule's format → confirm inline
                {
                    ghNeedsConfirm = true; ghEntered = cellText; ghSuggestion = canonical; ghDisp = canonical;
                }
            }
        }
        else
        {
            cs.Skipped.Add(new SkippedItem { Reason = "unsupported", Detail = $"{ghe.FieldName} ({label})" });
            return;
        }

        if (missing > 0)
            cs.Diagnostics.Add(new CellDiagnostic
            {
                SheetTabName = sheet.SheetTabName, ExcelRow = row.ExcelRow, Col = ghe.Col, FieldName = ghe.FieldName,
                ElementLabel = label, Severity = "yellow", Reason = $"{missing} member(s) no longer exist", Value = cellText,
            });

        // Split grouped vs ungrouped members. Grouped members can't be written in the direct apply transaction
        // (the "Changes to groups are allowed only in group edit mode" error) — Mark() routes them to the durable
        // group path (project-param vary, or Claude-assist dance for built-ins). Ungrouped members apply directly.
        var ghUngrouped = new List<string>();
        var ghGrouped = new List<string>();
        string ghGroupName = "";
        foreach (var uid in ids)
        {
            var inst = doc.GetElement(uid);
            var (gi, gn) = inst == null ? (false, "") : GroupInfo(doc, inst);
            if (gi) { ghGrouped.Add(uid); if (ghGroupName == "") ghGroupName = gn; }
            else ghUngrouped.Add(uid);
        }
        ProposedChange MakeGh(List<string> bulk)
        {
            var ch = new ProposedChange
            {
                ParameterId = ghe.ParameterId, Binding = "instance", BulkInstanceIds = bulk,
                ElementName = label, Field = ghe.FieldName, OldValue = oldDisp, NewValue = ghDisp,
                InstancesAffected = bulk.Count, IsString = isString, NewString = str, NewDouble = dbl, IsInt = isInt, NewInt = iv,
                ReportRowUid = row.UniqueId,   // W2: overlay the partial-bulk outcome on this visible (type/group) row
            };
            if (ghNeedsConfirm)
            {
                ch.NeedsConfirm = true; ch.EnteredValue = ghEntered; ch.EditValue = ghEntered;
                ch.SpecTypeId = ghe.SpecTypeId ?? ""; ch.Suggestion = ghSuggestion; ch.ConfirmError = ghError;
                ch.Selected = false;
            }
            return ch;
        }
        if (ghUngrouped.Count > 0) cs.Changes.Add(MakeGh(ghUngrouped));
        if (ghGrouped.Count > 0) cs.Changes.Add(Mark(doc, MakeGh(ghGrouped), true, ghGroupName));
    }

    private static ProposedChange BulkChange(Element typeEl, ImportColumn col, List<string> instanceIds,
        string oldDisp, string newDisp, bool isString, string str, double dbl, bool isInt, int iv, string reportRowUid = "") => new()
    {
        ParameterId = col.ParameterId, Binding = "instance", BulkInstanceIds = instanceIds,
        ElementName = SafeName(typeEl), Field = col.FieldName, SourceHeading = col.Header, OldValue = oldDisp, NewValue = newDisp,
        InstancesAffected = instanceIds.Count, IsString = isString, NewString = str, NewDouble = dbl, IsInt = isInt, NewInt = iv,
        ReportRowUid = reportRowUid,   // W2: visible row this bulk cell renders on, for the run-results overlay
    };

    private static ReformatSuggestion Reformat(ImportSheet sheet, ImportRow row, ImportColumn col, string label, string entered, string canonical) => new()
    {
        SheetTabName = sheet.SheetTabName, ExcelRow = row.ExcelRow, ExcelCol = col.ExcelCol,
        FieldName = col.FieldName, ElementLabel = label, Entered = entered, Canonical = canonical,
    };

    /// <summary>Ensures the per-(type,param) <see cref="TypeCandidate"/> exists (capturing the type's one current
    /// value) and counts this schedule row against it — called for EVERY type-bound row, edited or not, so the
    /// candidate knows how many of the type's schedule rows are unedited (= would be clobbered by a type write).</summary>
    private static TypeCandidate EnsureTypeCandidate(Dictionary<(long, int), TypeCandidate> typeGroups,
        ImportSheet sheet, ImportColumn col, Element host, Parameter param)
    {
        var key = (host.Id.Value, col.ParameterId);
        if (!typeGroups.TryGetValue(key, out var tc))
        {
            tc = new TypeCandidate { Col = col, SheetTab = sheet.SheetTabName, TypeName = SafeName(host), SpecTypeId = col.SpecTypeId,
                                     ScheduleName = sheet.ScheduleName, ScheduleUid = sheet.ScheduleUniqueId };
            if (param.StorageType == StorageType.String)
            {
                tc.IsString = true;
                tc.CurString = param.AsString() ?? "";
                tc.CurDisplay = tc.CurString;
            }
            else
            {
                tc.IsString = false;
                tc.CurDouble = param.AsDouble();
                tc.CurDisplay = param.AsValueString() ?? "";
            }
            typeGroups[key] = tc;
        }
        tc.TotalRows++;
        return tc;
    }

    /// <summary>Registers an EDITED type-bound cell (also counts the row via <see cref="EnsureTypeCandidate"/>).</summary>
    private static void RecordType(Dictionary<(long, int), TypeCandidate> typeGroups, ImportSheet sheet,
        ImportColumn col, Element host, Parameter param, string label, int excelRow, string cellText)
        => EnsureTypeCandidate(typeGroups, sheet, col, host, param).Cells.Add((excelRow, cellText, label));

    /// <summary>True when an EDITED value of this type already equals the type's CURRENT value — so the unedited
    /// siblings (which hold the current value) are NOT in conflict with the edit and CurDisplay must NOT be folded
    /// into the conflict set. String params compare exact display text; Double params compare the PARSED value
    /// within 1e-9 (so a non-canonical-but-equal entry like "3' 0"" vs "3'-0"" doesn't spuriously raise a conflict).</summary>
    private static bool EditMatchesCurrent(TypeCandidate tc, List<string> editedDistinct, Units units)
    {
        if (tc.IsString) return editedDistinct.Contains(tc.CurDisplay);
        foreach (var v in editedDistinct)
            if (UnitFormatUtils.TryParse(units, new ForgeTypeId(tc.SpecTypeId!), v, out double d)
                && Math.Abs(d - tc.CurDouble) < 1e-9)
                return true;
        return false;
    }

    /// <summary>Distinct edited values for a type. STRING columns dedup exact text. DOUBLE/length columns dedup by
    /// PARSED value (within 1e-9) so two rows edited to the same measurement in different formats ("3" and "3'-0"")
    /// collapse to ONE value (no false conflict, no duplicate picker option). The representative is the USER'S RAW
    /// ENTERED TEXT (the first-seen of a group) — so the conflict picker shows exactly what the user typed ("3"),
    /// not a reinterpreted form ("3'-0""), and they recognise their own input. Format interpretation is surfaced
    /// SEPARATELY by the per-row reformat-confirm in the preview (cs.Reformats), not by rewriting the picker label.
    /// The value still WRITES correctly: every consumer re-parses the text (raw "3" parses the same as "3'-0"").</summary>
    private static List<string> EditedDistinct(TypeCandidate tc, Units units)
    {
        if (tc.IsString || tc.SpecTypeId == null)
            return tc.Cells.Select(c => c.value).Distinct().ToList();
        var byVal = new List<(double key, string raw)>();
        var unparseable = new List<string>();
        foreach (var v in tc.Cells.Select(c => c.value).Distinct())
        {
            if (!UnitFormatUtils.TryParse(units, new ForgeTypeId(tc.SpecTypeId), v, out double d))
            { if (!unparseable.Contains(v)) unparseable.Add(v); continue; }
            if (byVal.Any(b => Math.Abs(b.key - d) < 1e-9)) continue;   // same measurement, different format → one
            byVal.Add((d, v));                                           // keep the RAW entered text as shown to the user
        }
        return byVal.Select(b => b.raw).Concat(unparseable).ToList();
    }

    private static void ResolveTypeGroups(ChangeSet cs, Dictionary<(long, int), TypeCandidate> typeGroups, Units units,
        Dictionary<long, int> typeCounts)
    {
        foreach (var kv in typeGroups)
        {
            var tc = kv.Value;
            if (tc.Cells.Count == 0) continue;   // type with only UNEDITED rows (no edit on it) → nothing to write
            // #1: a type edit changes the TYPE, so it affects EVERY instance of that type — report the real
            // instance count, not the number of schedule rows touched (~1). Falls back to cell count if unknown.
            int affects = typeCounts.TryGetValue(kv.Key.Item1, out var instCount) ? instCount : tc.Cells.Count;

            // PER-ROW FORMAT RESOLUTION is now carried on the resulting type CHANGE itself (NeedsConfirm, set at the
            // clean-write site below) so a non-canonical entry ("3", "1'4"") shows as a PENDING preview row the user
            // confirms inline — not as a separate fix-pane entry. A value that becomes a type CONFLICT is interpreted
            // through the conflict picker instead (which already shows the entered value), so it needs no inline strip.

            // Distinct EDITED values. For a DOUBLE column dedup by PARSED value (1e-9), not raw text — so two rows
            // edited to the same width in different formats ("3" and "3'-0"") are ONE value, not a false conflict;
            // keep the canonical form as the representative. String columns dedup exact.
            var editedDistinct = EditedDistinct(tc, units);

            // The conflict set = the distinct EFFECTIVE values the type's schedule rows want. Edited rows want their
            // new value; UNEDITED rows (Cells.Count < TotalRows) want the type's CURRENT value (CurDisplay) — and a
            // type holds ONE value, so writing an edited value clobbers those siblings. Fold CurDisplay in when any
            // visible row of the type was left unedited; if the resulting set has >1 value, it's a type conflict the
            // user must resolve (the whole point: an itemized partial edit must surface the clobber). When every
            // visible row was edited to one value, the set is just that value → a clean type write (no prompt).
            bool hasUneditedSiblings = tc.Cells.Count < tc.TotalRows;
            var effective = new List<string>(editedDistinct);
            if (hasUneditedSiblings && !EditMatchesCurrent(tc, editedDistinct, units))
                effective.Add(tc.CurDisplay);

            if (effective.Count > 1)
            {
                var conflict = new TypeConflict
                {
                    TypeId = kv.Key.Item1,
                    ParameterId = kv.Key.Item2,
                    Field = tc.Col.FieldName,
                    TypeName = tc.TypeName,
                    CurrentDisplay = tc.CurDisplay,
                    CurrentDouble = tc.IsString ? double.NaN : tc.CurDouble,   // for the resolver's no-op drop
                    SpecTypeId = tc.SpecTypeId ?? "",   // for the inline confirm strip's re-parse
                    InstancesAffected = affects,
                    ScheduleName = tc.ScheduleName, ScheduleUid = tc.ScheduleUid,   // C-7
                };
                foreach (var v in effective)
                {
                    // Every value EXCEPT the type's current value is something the user TYPED — flag it so the picker
                    // labels it "(entered value)". (CurDisplay, folded in when unedited siblings exist, is the
                    // keep-current option, not a user entry.)
                    var opt = new ConflictOption { Display = v, CanonicalDisplay = v, IsString = tc.IsString, NewString = v, IsEntered = editedDistinct.Contains(v) };
                    if (!tc.IsString)
                    {
                        opt.Parseable = UnitFormatUtils.TryParse(units, new ForgeTypeId(tc.SpecTypeId!), v, out double d);
                        opt.NewDouble = opt.Parseable ? d : 0;
                        // The resolved change shows the value in the schedule's format, even though the picker shows
                        // the raw entry "v" (so the user recognizes their input). Equals v when already canonical.
                        if (opt.Parseable) opt.CanonicalDisplay = ExcelCorrector.Canonical(units, new ForgeTypeId(tc.SpecTypeId!), d, v);
                    }
                    conflict.Options.Add(opt);
                }
                cs.Conflicts.Add(conflict);
                foreach (var cell in tc.Cells)
                    cs.Diagnostics.Add(new CellDiagnostic
                    {
                        SheetTabName = tc.SheetTab, ExcelRow = cell.excelRow, Col = tc.Col.ExcelCol, FieldName = tc.Col.FieldName,
                        ElementLabel = cell.label, Severity = "red", Reason = "type conflict — pick a value", Value = cell.value,
                    });
                continue;
            }

            // Clean type write: all visible rows of the type agree on one value (no unedited-sibling disagreement).
            var value = editedDistinct[0];
            if (tc.IsString)
            {
                if (value == tc.CurString) continue;
                var chS = TypeChange(kv.Key, tc, value, true, value, 0);
                chS.InstancesAffected = affects;
                cs.Changes.Add(chS);
            }
            else
            {
                if (!UnitFormatUtils.TryParse(units, new ForgeTypeId(tc.SpecTypeId!), value, out double parsed))
                {
                    // Unreadable single type value ("ABC") → an inline PENDING row (box + Confirm), not a silent drop;
                    // the confirm line asks for a usable value. Same treatment the conflict-picker path gives an
                    // unreadable pick — kept consistent here for a type with no sibling disagreement.
                    var chBad = TypeChange(kv.Key, tc, "", false, "", 0);
                    chBad.InstancesAffected = affects;
                    chBad.NeedsConfirm = true;
                    chBad.EnteredValue = value;
                    chBad.SpecTypeId = tc.SpecTypeId ?? "";
                    chBad.EditValue = value;
                    chBad.ConfirmError = $"enter a usable value for {tc.Col.FieldName} (e.g. 7' or 7\")";
                    chBad.Selected = false;
                    cs.Changes.Add(chBad);
                    continue;
                }
                // Drop a TRUE no-op only: the entry AS WRITTEN already equals the type's current display. A trivial
                // format-only difference ("7" vs current "7'-0"") is NOT dropped — it's shown PENDING so the user
                // confirms the interpretation (and then ConfirmRow removes it because the confirmed value == model).
                if (ExcelCorrector.SameFormat(value, tc.CurDisplay)) continue;
                // #2: show the canonical formatted value (e.g. 1'-0") as New, not the raw cell text ("1"), so the
                // preview reveals how a unit-less entry was interpreted instead of silently writing it.
                var canonical = ExcelCorrector.Canonical(units, new ForgeTypeId(tc.SpecTypeId!), parsed, value);
                var chD = TypeChange(kv.Key, tc, canonical, false, "", parsed);
                chD.InstancesAffected = affects;
                // A non-canonical entry ("3") becomes a PENDING change the user confirms inline (the value written
                // is the canonical 'parsed' double either way; this only gates it behind a confirm + shows what
                // they typed). A value already in the schedule's format applies as a normal change.
                if (!ExcelCorrector.SameFormat(value, canonical))
                {
                    chD.NeedsConfirm = true;
                    chD.EnteredValue = value;
                    chD.SpecTypeId = tc.SpecTypeId ?? "";
                    chD.EditValue = value;       // the box starts at what the user typed; interpreted only on Confirm
                    chD.Suggestion = canonical;  // the "interpreted as …" line shown until they confirm/re-prompt
                    chD.Selected = false;
                }
                cs.Changes.Add(chD);
            }
        }
    }

    /// <summary>#1: instances per type id (for the requested type ids only) — lets a type-param edit's Scope
    /// report the real blast radius (every instance of the type) instead of the number of schedule rows touched.</summary>
    private static Dictionary<long, int> TypeInstanceCounts(Document doc, int category, IEnumerable<(long, int)> keys)
    {
        var counts = new Dictionary<long, int>();
        foreach (var k in keys) counts[k.Item1] = 0;
        if (counts.Count == 0) return counts;
        var col = new FilteredElementCollector(doc).WhereElementIsNotElementType();
        if (category != -1) col = col.OfCategoryId(new ElementId((long)category));
        foreach (var e in col)
        {
            var tid = e.GetTypeId().Value;
            if (counts.ContainsKey(tid)) counts[tid]++;
        }
        return counts;
    }

    /// <summary>#5: parameter ids the schedule uses in a filter or sort/group field. Editing such a value can
    /// remove a row from the schedule (filter) or reorder it (sort/group), so the preview flags it.</summary>
    private static HashSet<long> SensitiveFieldParamIds(Document doc, string scheduleUniqueId)
    {
        var ids = new HashSet<long>();
        if (string.IsNullOrEmpty(scheduleUniqueId) || doc.GetElement(scheduleUniqueId) is not ViewSchedule vs) return ids;
        var def = vs.Definition;
        void Add(ScheduleFieldId fid)
        {
            try
            {
                var f = def.GetField(fid);
                if (f != null && f.ParameterId != ElementId.InvalidElementId) ids.Add(f.ParameterId.Value);
            }
            catch { /* field may not resolve — ignore */ }
        }
        try { foreach (var sg in def.GetSortGroupFields()) Add(sg.FieldId); } catch { /* ignore */ }
        try { for (int i = 0; i < def.GetFilterCount(); i++) Add(def.GetFilter(i).FieldId); } catch { /* ignore */ }
        return ids;
    }

    public static ProposedChange ResolveToChange(TypeConflict c, ConflictOption opt)
    {
        // The picker only chose WHICH value wins the conflict — it does NOT get to silently decide the FORMAT, and it
        // accepts UNREADABLE picks too (e.g. "ABC"). Any non-string pick that isn't already in the schedule's format
        // comes back PENDING (NeedsConfirm) so it's confirmed inline, every time:
        //   • readable but non-canonical ("7" → "7'-0"") → shows the interpretation to confirm;
        //   • unreadable ("ABC") → shows "enter a usable value" and waits for a fix.
        // Already-canonical picks (and string options) apply directly.
        string canonical = string.IsNullOrEmpty(opt.CanonicalDisplay) ? opt.Display : opt.CanonicalDisplay;
        bool unreadable = !opt.IsString && !opt.Parseable;
        bool reformat = !opt.IsString && opt.Parseable && opt.Display != canonical;
        bool needsConfirm = unreadable || reformat;
        return new()
        {
            TypeId = c.TypeId, ParameterId = c.ParameterId, Binding = "type", ElementName = c.TypeName,
            // New shows the value in the schedule's format (the canonical/interpreted value) — but blank for an
            // unreadable pick (nothing valid yet) and PENDING until confirmed.
            Field = c.Field, OldValue = c.CurrentDisplay,
            NewValue = unreadable ? "" : canonical,
            InstancesAffected = c.InstancesAffected,
            IsString = opt.IsString, NewString = opt.NewString, NewDouble = opt.NewDouble,
            NeedsConfirm = needsConfirm,
            EnteredValue = needsConfirm ? opt.Display : "",
            EditValue = needsConfirm ? opt.Display : "",
            Suggestion = reformat ? canonical : "",   // an unreadable pick has no interpretation to show
            ConfirmError = unreadable ? $"enter a usable value for {c.Field} (e.g. 7' or 7\")" : "",
            SpecTypeId = needsConfirm ? c.SpecTypeId : "",
            Selected = !needsConfirm,
            // C-7: stamp the source schedule so this resolved conflict rolls up under its schedule's tri-state and a
            // per-schedule deselect skips it (ChangesForSchedule matches on these — uid-first, name fallback).
            SourceScheduleName = c.ScheduleName, SourceScheduleUid = c.ScheduleUid,
        };
    }

    /// <summary>Collects (and quiets) Revit's own failures during the apply commit, so they land in the log
    /// instead of blocking on a dialog. Warnings are deleted (commit proceeds, unchanged behavior). ERRORS that
    /// carry a resolution are RESOLVED (logged with the resolution type) and the commit proceeds — an unresolved
    /// Error otherwise rolls back the ENTIRE transaction silently (the GreenChanges 243-for-1 loss). Errors with
    /// no resolution (and DocumentCorruption etc.) are logged as-is. Revit re-invokes the preprocessor after
    /// ProceedWithCommit, so a per-transaction pass cap (3) breaks resolution loops: call <see cref="Reset"/>
    /// immediately after every tx.Start() that uses this collector. Failure lines name the failing elements so
    /// the log answers WHICH edit broke (Name (id), up to 8, then "+K more").</summary>
    private sealed class ApplyFailureCollector : IFailuresPreprocessor
    {
        public readonly List<string> Messages = new();
        /// <summary>Plain-language warnings for auto-resolved failures (joins removed / dimension references removed)
        /// so the apply report can tell the user the model geometry was adjusted to let the change apply.</summary>
        public readonly List<string> AutoFixWarnings = new();
        private int _passes;

        /// <summary>Resets the per-transaction pass counter. Call right after every tx.Start().</summary>
        public void Reset() => _passes = 0;

        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            _passes++;
            int resolved = 0;
            foreach (var f in a.GetFailureMessages())
            {
                var sev = f.GetSeverity();
                string desc;
                try { desc = f.GetDescriptionText(); } catch { desc = "(failure)"; }
                int n = 0; try { n = f.GetFailingElementIds().Count; } catch { /* ignore */ }
                string who = FailingElementNames(a, f);

                if (sev == FailureSeverity.Warning)
                {
                    Messages.Add($"[{sev}] {desc}" + (n > 0 ? $" — {n} element(s)" : "") + who);
                    try { a.DeleteWarning(f); } catch { /* ignore */ }
                }
                else if (sev == FailureSeverity.Error && f.HasResolutions())
                {
                    var rtEnum = FailureResolutionType.Default;
                    try { rtEnum = f.GetCurrentResolutionType(); } catch { /* keep Default */ }
                    // C1: name the resolution Revit will actually apply (caption when it's the Default query-key)
                    // instead of the useless "resolved: Default".
                    var rtName = ResolutionName(f, rtEnum);
                    // C2 (H1 whitelist): FAIL-CLOSED — auto-resolve ONLY failures whose FailureDefinitionId is in a
                    // small static allow-set, keyed on the FAILURE identity (not the resolution type, which can be
                    // the blind `Default` query-key). Seeded with the one Error we've observed and proven benign:
                    // JoinElementsFailures.CannotKeepJoined (default resolution = Unjoin/Detach). Every other
                    // Error+resolution is logged-and-refused → rollback → per-change retry names it. This is the
                    // work-order C2 SHAPE DECISION (id allow-list, not resolution-type class gate): an unseen/unknown
                    // failure is never auto-resolved sight-unseen. (research-kb\resolution-whitelist.md §6a.)
                    if (IsAllowedFailure(f))
                    {
                        // Resolve FIRST, then record — so the log/warning only ever claims a fix that ACTUALLY happened
                        // (if ResolveFailure throws, we record nothing and the commit fails honestly).
                        bool ok = false;
                        try { a.ResolveFailure(f); ok = true; resolved++; } catch { /* resolution failed → record nothing */ }
                        if (ok)
                        {
                            Messages.Add($"[Error] {desc} — resolved: {rtName}" + (n > 0 ? $" — {n} element(s)" : "") + who);
                            // User-facing warning: a benign geometry-fixup was auto-applied so the edit could land. Name
                            // the fix in plain terms (Revit's resolution caption) + the elements, so the user can review
                            // what was adjusted (joins removed / dimension references removed).
                            AutoFixWarnings.Add($"{desc.TrimEnd('.')} — auto-fixed by '{rtName}'" + (n > 0 ? $" on {n} element(s)" : "") + who);
                        }
                    }
                    else
                    {
                        // TEMP DIAGNOSTIC (2026-06-20): log the failure's actual FailureDefinitionId GUID so we can
                        // identify WHICH join/geometry-failure variant is firing (the "Can't keep elements joined"
                        // text maps to several distinct definition ids) and whitelist the correct one. Remove once
                        // the right id is added to IsAllowedFailure.
                        string fidGuid; try { fidGuid = f.GetFailureDefinitionId().Guid.ToString(); } catch { fidGuid = "(unavailable)"; }
                        Messages.Add($"[Error] {desc} — NOT auto-resolved (failure id not in allow-list); left for retry/report"
                                     + $"  [DIAG failureDefinitionId.Guid={fidGuid}]"
                                     + (n > 0 ? $" — {n} element(s)" : "") + who);
                    }
                }
                else
                {
                    // Resolution-less Errors / DocumentCorruption: log only — never auto-resolve these.
                    Messages.Add($"[{sev}] {desc}" + (n > 0 ? $" — {n} element(s)" : "") + who);
                }
            }
            return resolved > 0 && _passes < 3
                ? FailureProcessingResult.ProceedWithCommit
                : FailureProcessingResult.Continue;
        }

        /// <summary>": Name (id), Name (id) … +K more" for the failure's elements (best-effort, max 8).</summary>
        private static string FailingElementNames(FailuresAccessor a, FailureMessageAccessor f)
        {
            try
            {
                var doc = a.GetDocument();
                var ids = f.GetFailingElementIds();
                if (ids == null || ids.Count == 0) return "";
                var parts = new List<string>();
                foreach (var id in ids.Take(8))
                {
                    var el = doc.GetElement(id);
                    string name = "";
                    try { name = el?.Name ?? ""; } catch { /* some elements throw on Name */ }
                    if (string.IsNullOrEmpty(name)) { try { name = el?.Category?.Name ?? ""; } catch { /* ignore */ } }
                    parts.Add($"{(string.IsNullOrEmpty(name) ? "?" : name)} ({id.Value})");
                }
                return ": " + string.Join(", ", parts) + (ids.Count > 8 ? $" … +{ids.Count - 8} more" : "");
            }
            catch { return ""; }
        }

        /// <summary>
        ///     C1: an honest name for the resolution Revit will actually apply. <c>GetCurrentResolutionType()</c>
        ///     returns the <c>Default</c> query-key when no resolution was set explicitly (our case), which logged a
        ///     useless "resolved: Default". The Revit 2025 API has no per-message "applicable types" getter, but it
        ///     DOES expose <c>GetDefaultResolutionCaption()</c> — the human-readable caption of the default
        ///     resolution (e.g. "Unjoin Elements") — so when the type is Default we append that caption. Best-effort;
        ///     falls back to the bare enum name if the caption is empty/throws, so the log never regresses. We only
        ///     NAME it — we do not call SetCurrentResolutionType, so ResolveFailure still applies Revit's own default
        ///     (the retest-proven behavior).
        /// </summary>
        internal static string ResolutionName(FailureMessageAccessor f, FailureResolutionType rt)
        {
            if (rt != FailureResolutionType.Default) return rt.ToString();
            string caption = "";
            try { caption = f.GetDefaultResolutionCaption() ?? ""; } catch { /* ignore */ }
            return string.IsNullOrWhiteSpace(caption) ? "Default" : $"Default ({caption})";
        }

        /// <summary>
        ///     C2 (H1 whitelist, fail-closed): true iff this failure's <see cref="FailureDefinitionId"/> is in the
        ///     static allow-set of failures we've observed and proven benign to auto-resolve. Keying on the FAILURE
        ///     identity (not the resolution type) is fail-closed: an unknown/unseen failure returns false → it is
        ///     logged-and-not-resolved → honest rollback → per-change retry. It also sidesteps the <c>Default</c>
        ///     resolution-type sentinel entirely (we never inspect the resolution to decide whether to resolve).
        ///     Allow-set = { JoinElementsFailures.CannotKeepJoined } — the join error (default resolution = Unjoin/
        ///     Detach, benign; research-kb\resolution-whitelist.md §1.1, §6a). Do NOT pre-add unseen ids (e.g.
        ///     CannotKeepWallJoinToRoof); add only after observing and judging them benign.
        /// </summary>
        internal static bool IsAllowedFailure(FailureMessageAccessor f)
        {
            try
            {
                var id = f.GetFailureDefinitionId();
                // Benign geometry-fixup errors whose DEFAULT resolution IS the intended "just apply it" path — each
                // observed live (2026-06-20) and user-confirmed to auto-resolve:
                //   • CannotJoinElementsError (Guid 6650b20b…): the ACTUAL id behind the "Can't keep elements joined"
                //     error that a door-type RESIZE raises between the host wall's finishes (Stone/Panel Siding) —
                //     CONFIRMED live via the [DIAG …Guid=] instrumentation. Default resolution = Unjoin/Detach (the
                //     apply path). NOTE: the "Can't keep elements joined" TEXT maps to several distinct definition ids;
                //     this resize case is CannotJoinElementsError, NOT CannotKeepJoined — keep both whitelisted.
                //   • CannotKeepJoined (Guid fe859f1c…): the other join variant (also Unjoin/Detach).
                //   • DimensionReferencesInvalid (Guid b44c8ba0…, default = Remove Reference(s)): a resize invalidates
                //     dimensions attached to the element — removing those references IS the apply path (user-directed).
                // Each auto-resolution is SURFACED as a user warning (apply log + run report name it), so a silent
                // geometric adjustment never happens unannounced. Other failures stay fail-closed (logged + refused).
                //
                // DO NOT whitelist "Instance(s) of <type> not cutting anything" (Guid c4c5d448-87fb-4622-acff-
                // fdb957172dda). It was added then REVERTED (2026-06-20) after observing it live: its dialog is
                // "Error - cannot be ignored" and its ONLY resolution is "Delete Instance(s)" — i.e. auto-resolving
                // it DELETES the door, the opposite of "apply it anyway". Refusing it (rollback + report) is correct:
                // the user is told the edit didn't take, and no geometry is destroyed.
                return id == BuiltInFailures.JoinElementsFailures.CannotJoinElementsError
                    || id == BuiltInFailures.JoinElementsFailures.CannotKeepJoined
                    || id == BuiltInFailures.DimensionFailures.DimensionReferencesInvalid;
            }
            catch { return false; }   // can't identify the failure → fail closed (do not resolve)
        }
    }

    /// <summary>
    ///     W1 (Task #17): watches <see cref="Autodesk.Revit.ApplicationServices.Application.DocumentChanged"/> around
    ///     the apply to capture elements Revit DELETED that we never asked to delete — its own family-regeneration
    ///     auto-fixes ("Delete Splitting Element" etc.). Those deletions commit inside the transaction's regen,
    ///     BEFORE any IFailuresPreprocessor runs, so they're unpreventable; the honest move is to detect + surface
    ///     them (research-kb\resolution-whitelist.md §9). DocumentChanged reports the committed DB delta per
    ///     transaction, which includes regen/cascade deletions. The event is document-wide and per-transaction, so we
    ///     scope-filter to our own apply transactions by name. Subscribe before the apply, <see cref="Dispose"/> in a
    ///     finally. Read-only handler (the event forbids doc edits — we only record). NOTE: a deleted element is
    ///     usually un-queryable even inside the handler (the delete already committed), so names are best-effort and
    ///     most entries are id-only — the ids are authoritative regardless.
    /// </summary>
    private sealed class DeletionWatcher : IDisposable
    {
        private readonly Autodesk.Revit.ApplicationServices.Application _app;
        private readonly Document _doc;
        private readonly EventHandler<Autodesk.Revit.DB.Events.DocumentChangedEventArgs> _handler;
        private readonly Dictionary<long, string> _deleted = new();   // id -> "Name (id)" (or "(id)")

        // Transaction names this watcher attributes to Transom (must match the apply tx names below).
        private static readonly string[] OurTxNames =
        {
            "Transom: import edits",
            "Transom: import edit (retry)",
            "Transom: import edits (new parameter, retried)",
        };

        public DeletionWatcher(Document doc)
        {
            _doc = doc;
            _app = doc.Application;
            _handler = OnDocumentChanged;
            _app.DocumentChanged += _handler;
        }

        private void OnDocumentChanged(object? sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
        {
            try
            {
                if (!ReferenceEquals(e.GetDocument(), _doc)) return;   // document-wide event — only our doc
                // Scope-filter to our apply transactions so other add-ins'/concurrent transactions aren't misattributed.
                var txNames = e.GetTransactionNames();
                if (txNames == null || !txNames.Any(IsOurTx)) return;
                foreach (var id in e.GetDeletedElementIds())
                {
                    long key = id.Value;
                    if (_deleted.ContainsKey(key)) continue;
                    string name = "";
                    try { name = _doc.GetElement(id)?.Name ?? ""; } catch { /* deleted → usually un-queryable */ }
                    _deleted[key] = string.IsNullOrEmpty(name) ? $"(id {key})" : $"{name} (id {key})";
                }
            }
            catch { /* observation only — never let it affect the apply */ }
        }

        private static bool IsOurTx(string n)
        {
            foreach (var t in OurTxNames) if (string.Equals(n, t, StringComparison.Ordinal)) return true;
            // The retry TransactionGroup ("Transom: import edits (retried)") assimilates into a tx whose name may be
            // either the group or an inner name; match any "Transom: import edit" prefix as the belt.
            return n != null && n.StartsWith("Transom: import edit", StringComparison.Ordinal);
        }

        /// <summary>The captured Revit-internal deletions as "Name (id)" strings (deduped, insertion order).</summary>
        public List<string> Deletions => _deleted.Values.ToList();

        public void Dispose()
        {
            try { _app.DocumentChanged -= _handler; } catch { /* ignore */ }
        }
    }

    /// <summary>
    ///     Applies the change set. Thin wrapper around <see cref="ApplyInner"/> that runs a <see cref="DeletionWatcher"/>
    ///     across the WHOLE apply (fast path + per-change retry) — W1 (Task #17): captures Revit-internal geometry
    ///     deletions from the DocumentChanged delta and surfaces them in the apply log + run report, so a "successful"
    ///     apply that silently deleted geometry is never reported as clean. Observation only — does not touch the
    ///     failure/resolve pipeline.
    /// </summary>
    public string Apply(Document doc, ChangeSet cs)
    {
        using var watcher = new DeletionWatcher(doc);
        try
        {
            return ApplyInner(doc, cs);
        }
        finally
        {
            // Surface whatever Revit deleted during the apply (best-effort; never affects the returned status).
            try
            {
                cs.RevitDeletions = watcher.Deletions;
                if (cs.RevitDeletions.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine();
                    sb.AppendLine($"== Revit auto-deleted geometry during apply — review ({cs.RevitDeletions.Count}) ==");
                    sb.AppendLine("  Revit removed these element(s) it could not regenerate (family/void auto-fix). This is");
                    sb.AppendLine("  internal to Revit and NOT preventable by the importer — surfaced so it is never silent:");
                    foreach (var d in cs.RevitDeletions.Take(50)) sb.AppendLine("    - " + d);
                    if (cs.RevitDeletions.Count > 50) sb.AppendLine($"    … +{cs.RevitDeletions.Count - 50} more");
                    cs.DiagnosticLog += sb.ToString();
                }
            }
            catch { /* surfacing is best-effort */ }
        }
    }

    private string ApplyInner(Document doc, ChangeSet cs)
    {
        int applied = 0;
        var failed = new List<string>();
        var unverified = new List<string>();
        var collector = new ApplyFailureCollector();
        string newParamNote = "";

        using var tx = new Transaction(doc, "Transom: import edits");
        tx.Start();
        AttachCollector(tx, collector);   // capture Revit warnings/errors into the log; resets the pass counter

        try
        {
            // Resolution option 2 (REPLACE the column with a new type/instance parameter) is handled in one
            // bulk pass — create the param, merge originals with edits, write, then replace the source field —
            // not by the per-change write loop below.
            var newParamChanges = cs.Changes
                .Where(c => c.Resolution is GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam && !c.Frozen)
                .ToList();
            if (newParamChanges.Count > 0)
                newParamNote = ApplyNewParam(doc, newParamChanges, cs, failed);

            foreach (var ch in cs.Changes)
            {
                if (ch.Frozen) continue; // can't be written — shown greyed in the preview only
                if (ch.Resolution is GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam) continue; // handled in the bulk pass above
                if (ch.GroupMode == GroupMode.BuiltinDance) continue; // grouped built-in — staged for resolution (option 2 / Claude-Assist), not written in the direct pass

                // Bulk instance write (grouped schedule): apply to every instance the row represented.
                if (ch.BulkInstanceIds != null)
                {
                    bool anyFail = false, anyUnver = false;
                    int persisted = 0, totalInst = ch.BulkInstanceIds.Count;
                    foreach (var uid in ch.BulkInstanceIds)
                    {
                        var inst = doc.GetElement(uid);
                        var ip = inst == null ? null : GetParam(inst, ch.ParameterId);
                        if (ip == null || ip.IsReadOnly) { failed.Add(Label(ch)); anyFail = true; continue; }
                        // Grouped project param: allow it to vary per instance, then write each instance directly.
                        if (ch.GroupMode == GroupMode.ProjectVary && !EnsureVary(ip, doc)) { failed.Add(Label(ch) + " — can't vary by group instance"); anyFail = true; continue; }
                        if (!TrySetValue(ip, ch, out var bulkSetErr)) { failed.Add(Label(ch) + (bulkSetErr == null ? "" : " — " + bulkSetErr)); anyFail = true; continue; }
                        if (!VerifyWrite(ip, ch)) { unverified.Add(Label(ch)); anyUnver = true; continue; }
                        applied++; persisted++;
                    }
                    // Any failed instance fails the whole cell; else any unverified; else applied.
                    ch.Outcome = anyFail ? ApplyOutcome.Failed : anyUnver ? ApplyOutcome.Unverified : ApplyOutcome.Applied;
                    // C4 (H3): a "Failed" bulk cell whose transaction still committed the good instances must not
                    // read as if nothing landed — record how many of M instances actually persisted (this tx
                    // commits, so persisted writes survive). Re-import is idempotent; the three-way compare
                    // re-offers only the instances still differing.
                    if (ch.Outcome != ApplyOutcome.Applied && persisted > 0)
                        ch.OutcomeNote = $"{persisted} of {totalInst} instances persisted";
                    continue;
                }

                var host = ch.Binding == "type" ? doc.GetElement(new ElementId(ch.TypeId)) : doc.GetElement(ch.UniqueId);
                var param = host == null ? null : GetParam(host, ch.ParameterId);
                if (param == null || param.IsReadOnly) { failed.Add(Label(ch)); ch.Outcome = ApplyOutcome.Failed; continue; }
                if (ch.GroupMode == GroupMode.ProjectVary && !EnsureVary(param, doc)) { failed.Add(Label(ch) + " — can't vary by group instance"); ch.Outcome = ApplyOutcome.Failed; continue; }
                if (!TrySetValue(param, ch, out var setErr)) { failed.Add(Label(ch) + (setErr == null ? "" : " — " + setErr)); ch.Outcome = ApplyOutcome.Failed; continue; }

                // H3: re-read the parameter and confirm the value actually landed (a Set can return
                // true yet be coerced/clamped, or silently no-op on some derived parameters).
                if (!VerifyWrite(param, ch)) { unverified.Add(Label(ch)); ch.Outcome = ApplyOutcome.Unverified; continue; }
                applied++; ch.Outcome = ApplyOutcome.Applied;
            }

            // Column-caption (header) round-trip: set ScheduleField.ColumnHeading for each selected HeaderChange,
            // in this same transaction. Field resolved by ParameterId (rename-safe). Each is verified by re-read.
            applied += ApplyHeaderChanges(doc, cs, failed, unverified);

            var commitStatus = tx.Commit();
            if (commitStatus != TransactionStatus.Committed)
            {
                // Revit discarded the ENTIRE transaction — Commit() returns RolledBack WITHOUT throwing (an
                // unresolved ERROR-severity failure the collector couldn't auto-resolve). The single bulk
                // transaction is only a fast path: atomicity must never cost a good write (user decision,
                // 2026-06-11), so fall back automatically to one transaction per change — everything that can
                // succeed persists, only the poisoned change(s) stay Failed, and the run-results workbook +
                // apply log list exactly those for a targeted fix/re-import.
                return RetryPerChange(doc, cs, collector, attempted: applied);
            }
        }
        catch (Exception ex)
        {
            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            cs.DiagnosticLog = "Transom apply — FAILED (rolled back): " + ex.Message;
            return "Apply failed (rolled back): " + ex.Message;
        }

        // §18 (corrected per BUG 2, 2026-06-18): fold option-2 conversions into the headline total — but ONLY those
        // actually APPLIED. ApplyNewParam stamps each option-2 change's Outcome (Applied/Failed), so counting by
        // Outcome==Applied keeps the headline honest when a conversion FAILS/rolls back (e.g. option-2b on multi-
        // instance grouped members) instead of falsely reporting it as applied.
        int o2ConvertedStatus = cs.Changes.Count(c => c.Resolution is GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam && !c.Frozen && c.Outcome == ApplyOutcome.Applied);
        var msg = $"Applied {applied + o2ConvertedStatus} change(s)";
        if (failed.Count > 0) msg += $", {failed.Count} failed";
        if (unverified.Count > 0) msg += $", {unverified.Count} unverified (value didn't take)";
        if (collector.Messages.Count > 0) msg += $"  —  {collector.Messages.Count} Revit warning(s) (see log)";
        if (collector.AutoFixWarnings.Count > 0) msg += $"  —  ⚠ {collector.AutoFixWarnings.Count} geometry auto-fix(es) applied (joins/dim refs removed — review)";
        if (newParamNote.Length > 0) msg += $"  —  {newParamNote}";
        // Header renames are folded into `applied` above; call them out so the user sees the caption edits landed.
        int hdrApplied = cs.HeaderChanges.Count(h => h.Selected && h.Outcome == ApplyOutcome.Applied);
        if (hdrApplied > 0) msg += $"  —  {hdrApplied} column heading(s) renamed";
        msg += $". {cs.Skipped.Count(s => s.UserRelevant)} skipped.";   // #64: post-apply count = preview UserRelevant count

        cs.AutoFixWarnings = new List<string>(collector.AutoFixWarnings);
        cs.RevitApplyMessages = new List<string>(collector.Messages);
        cs.DiagnosticLog = BuildApplyLog(applied, failed, unverified, collector.Messages, cs);
        return msg;
    }

    /// <summary>Wires the failure collector into a transaction and resets its per-transaction pass counter.
    /// Call after tx.Start() and before any write.</summary>
    private static void AttachCollector(Transaction tx, ApplyFailureCollector collector)
    {
        var fho = tx.GetFailureHandlingOptions();
        fho.SetFailuresPreprocessor(collector);
        fho.SetClearAfterRollback(true);
        tx.SetFailureHandlingOptions(fho);
        collector.Reset();
    }

    /// <summary>Wraps SetValue: a parameter Set can THROW outright (e.g. renaming a type to a duplicate name
    /// raises ArgumentException immediately — unlike Type Mark's gentle warning), and an uncaught throw aborts
    /// the entire batch via the apply's catch. Contain it here and surface it as that change's failure reason.</summary>
    private static bool TrySetValue(Parameter param, ProposedChange ch, out string? error)
    {
        try { error = null; return SetValue(param, ch); }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>
    ///     Fallback when the single "Transom: import edits" transaction was rolled back by Revit: re-run the
    ///     write loop with ONE transaction per change inside a TransactionGroup (assimilated to a single undo
    ///     entry), so every change that can succeed persists and only the change(s) Revit refuses stay Failed.
    ///     The option-2 bulk pass (new shared parameter) is retried as one separate transaction first and is
    ///     never split per change — a half-created parameter/field replacement is worse than none.
    /// </summary>
    private string RetryPerChange(Document doc, ChangeSet cs, ApplyFailureCollector collector, int attempted)
    {
        // Nothing from the rolled-back pass persisted — clear its optimistic stamps before re-running.
        foreach (var c in cs.Changes)
            if (c.Outcome is ApplyOutcome.Applied or ApplyOutcome.Unverified) c.Outcome = ApplyOutcome.Pending;

        int applied = 0, total = 0;
        var failed = new List<string>();
        var unverified = new List<string>();
        string newParamNote = "";

        using var tg = new TransactionGroup(doc, "Transom: import edits (retried)");
        try
        {
            tg.Start();

            // C5 (H5): mark the pass boundary. Messages above this line came from the rolled-back first pass
            // (those writes were DISCARDED); messages below belong to the per-change retry transactions (which
            // persist). The "--- " prefix keeps separators out of the "[Warning]/[Error]" line counts.
            collector.Messages.Add("--- retry pass (per-change, after rollback) ---");

            var newParamChanges = cs.Changes
                .Where(c => c.Resolution is GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam && !c.Frozen)
                .ToList();
            if (newParamChanges.Count > 0)
            {
                total += newParamChanges.Count;
                cs.Option2Warnings.Clear();   // §10 warnings from the discarded pass describe rolled-back state
                using var ptx = new Transaction(doc, "Transom: import edits (new parameter, retried)");
                ptx.Start();
                AttachCollector(ptx, collector);
                bool ok;
                try
                {
                    newParamNote = ApplyNewParam(doc, newParamChanges, cs, failed);
                    ok = ptx.Commit() == TransactionStatus.Committed;
                }
                catch (Exception ex)
                {
                    failed.Add($"option-2 bulk pass — {ex.Message}");
                    ok = false;
                }
                if (!ok)
                {
                    try { if (ptx.GetStatus() == TransactionStatus.Started) ptx.RollBack(); } catch { /* ignore */ }
                    foreach (var c in newParamChanges)
                    { c.Outcome = ApplyOutcome.Failed; c.OutcomeNote = "option-2 bulk pass rolled back on retry"; }
                    failed.Add($"option-2 bulk pass rolled back — {newParamChanges.Count} change(s) not applied");
                }
                else applied += newParamChanges.Count(c => c.Outcome == ApplyOutcome.Applied);
            }

            foreach (var ch in cs.Changes)
            {
                if (ch.Frozen) continue;
                if (ch.Resolution is GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam) continue; // bulk pass above
                if (ch.GroupMode == GroupMode.BuiltinDance) continue;        // grouped built-in — staged for resolution, not written in the direct pass
                total++;

                using var ctx = new Transaction(doc, "Transom: import edit (retry)");
                ctx.Start();
                AttachCollector(ctx, collector);

                bool anyFail = false, anyUnver = false;
                int persisted = 0, totalInst = ch.BulkInstanceIds?.Count ?? 0;
                try
                {
                    if (ch.BulkInstanceIds != null)
                    {
                        // Bulk cell: all member instances in this ONE transaction (the cell is the retry unit).
                        foreach (var uid in ch.BulkInstanceIds)
                        {
                            var inst = doc.GetElement(uid);
                            var ip = inst == null ? null : GetParam(inst, ch.ParameterId);
                            if (ip == null || ip.IsReadOnly) { failed.Add(Label(ch)); anyFail = true; continue; }
                            if (ch.GroupMode == GroupMode.ProjectVary && !EnsureVary(ip, doc)) { failed.Add(Label(ch) + " — can't vary by group instance"); anyFail = true; continue; }
                            if (!TrySetValue(ip, ch, out var es)) { failed.Add(Label(ch) + (es == null ? "" : " — " + es)); anyFail = true; continue; }
                            if (!VerifyWrite(ip, ch)) { unverified.Add(Label(ch)); anyUnver = true; continue; }
                            persisted++;
                        }
                    }
                    else
                    {
                        // Re-resolve from the doc — the rolled-back pass left nothing of the first resolution.
                        var host = ch.Binding == "type" ? doc.GetElement(new ElementId(ch.TypeId)) : doc.GetElement(ch.UniqueId);
                        var param = host == null ? null : GetParam(host, ch.ParameterId);
                        if (param == null || param.IsReadOnly) { failed.Add(Label(ch)); anyFail = true; }
                        else if (ch.GroupMode == GroupMode.ProjectVary && !EnsureVary(param, doc)) { failed.Add(Label(ch) + " — can't vary by group instance"); anyFail = true; }
                        else if (!TrySetValue(param, ch, out var es)) { failed.Add(Label(ch) + (es == null ? "" : " — " + es)); anyFail = true; }
                        else if (!VerifyWrite(param, ch)) { unverified.Add(Label(ch)); anyUnver = true; }
                    }
                }
                catch (Exception ex)
                {
                    failed.Add(Label(ch) + " — " + ex.Message);
                    anyFail = true;
                }

                if (anyFail && ch.BulkInstanceIds == null)
                {
                    // Nothing usable was written — discard the empty transaction.
                    try { if (ctx.GetStatus() == TransactionStatus.Started) ctx.RollBack(); } catch { /* ignore */ }
                    ch.Outcome = ApplyOutcome.Failed;
                    continue;
                }

                var st = TransactionStatus.RolledBack;
                try { st = ctx.Commit(); } catch { /* treat as rolled back */ }
                if (st != TransactionStatus.Committed)
                {
                    ch.Outcome = ApplyOutcome.Failed;
                    ch.OutcomeNote = "rolled back individually (see Revit failures in log)";
                    if (!anyFail) failed.Add(Label(ch) + " — rolled back individually (see Revit failures in log)");
                }
                else
                {
                    // Partial bulk failures keep main-loop semantics: any failed instance fails the whole cell.
                    ch.Outcome = anyFail ? ApplyOutcome.Failed : anyUnver ? ApplyOutcome.Unverified : ApplyOutcome.Applied;
                    if (ch.Outcome == ApplyOutcome.Applied) applied++;
                    // C4 (H3): this tx committed, so a "Failed"/"Unverified" bulk cell's good instances really
                    // persisted — record N of M (mirrors the main-loop note). Only here, never on the rollback
                    // branch above where nothing survived.
                    else if (ch.BulkInstanceIds != null && persisted > 0)
                        ch.OutcomeNote = $"{persisted} of {totalInst} instances persisted";
                }
            }

            // Header (caption) round-trip in its own transaction — independent of the data writes, so it persists
            // even when the data bulk pass fell back to per-change. (Counts toward 'applied' / 'total' like a change.)
            if (cs.HeaderChanges.Any(h => h.Selected && h.OutcomeNote != "skipped"))
            {
                total += cs.HeaderChanges.Count(h => h.Selected);
                using var htx = new Transaction(doc, "Transom: import header edits (retry)");
                htx.Start();
                AttachCollector(htx, collector);
                int hApplied = ApplyHeaderChanges(doc, cs, failed, unverified);
                if (htx.Commit() == TransactionStatus.Committed) applied += hApplied;
                else
                {
                    try { if (htx.GetStatus() == TransactionStatus.Started) htx.RollBack(); } catch { /* ignore */ }
                    foreach (var h in cs.HeaderChanges.Where(h => h.Selected))
                    { h.Outcome = ApplyOutcome.Failed; h.OutcomeNote = "rolled back"; }
                }
            }

            tg.Assimilate();   // ONE undo entry for the whole retried apply
        }
        catch (Exception ex)
        {
            try { if (tg.GetStatus() == TransactionStatus.Started) tg.RollBack(); } catch { /* ignore */ }
            foreach (var c in cs.Changes)
                if (c.Outcome is ApplyOutcome.Applied or ApplyOutcome.Unverified) c.Outcome = ApplyOutcome.Failed;
            cs.DiagnosticLog = BuildApplyLog(attempted, failed, unverified, collector.Messages, cs, rolledBack: true,
                retryNote: $"*** Per-change retry ABORTED ({ex.Message}) — nothing persisted. ***");
            return $"Apply ROLLED BACK by Revit — 0 of {attempted} attempted change(s) were saved; per-change retry aborted: {ex.Message} (see log).";
        }

        int failedChanges = cs.Changes.Count(c => !c.Frozen && c.Outcome == ApplyOutcome.Failed);

        if (applied == 0)
        {
            // The retry ALSO saved nothing — keep the honest all-rolled-back reporting.
            cs.DiagnosticLog = BuildApplyLog(attempted, failed, unverified, collector.Messages, cs, rolledBack: true,
                retryNote: $"*** Per-change retry ran after the rollback and ALSO saved nothing: 0 applied, {failedChanges} failed individually (listed below). ***");
            int errCount = collector.Messages.Count(m => m.StartsWith("[Error]"));
            return $"Apply ROLLED BACK by Revit — 0 of {attempted} attempted change(s) were saved" +
                   (errCount > 0 ? $"; {errCount} error(s) blocked the commit (see log)." : " (see log).");
        }

        // Carry the auto-fix warnings recorded during the per-change RETRY transactions (the same collector accumulates
        // them) — this is the path our join/dimension-ref auto-resolve actually lands on (bulk rollback → retry), so
        // without this the "geometry auto-fix applied" warning would be silent exactly where it matters most.
        cs.AutoFixWarnings = new List<string>(collector.AutoFixWarnings);
        cs.RevitApplyMessages = new List<string>(collector.Messages);
        cs.DiagnosticLog = BuildApplyLog(applied, failed, unverified, collector.Messages, cs,
            retryNote: $"*** Initial bulk commit was ROLLED BACK by Revit ({attempted} write(s) discarded); every change was retried in its own transaction. ***\n" +
                       $"retried individually: {applied} applied, {failedChanges} failed");
        var msg = $"Apply recovered after rollback — {applied} of {total} change(s) saved individually, {failedChanges} failed";
        if (unverified.Count > 0) msg += $", {unverified.Count} unverified (value didn't take)";
        msg += " (see log).";
        if (newParamNote.Length > 0) msg += $"  —  {newParamNote}";
        return msg;
    }

    /// <summary>
    ///     Post-apply (post-commit) verification: re-reads every applied change's target parameter from the model
    ///     and compares it to the change's parsed value. Catches silent failures that only surface after the
    ///     transaction commits (e.g. a value Revit clamps, or a group-phase write the dance couldn't reproduce).
    ///     A mismatch stamps <see cref="ApplyOutcome.Failed"/>; a match on a still-<c>Pending</c> change stamps
    ///     <see cref="ApplyOutcome.Applied"/>. Option-2 (NewTypeParam/NewInstanceParam) changes are skipped — they
    ///     store the value in a DIFFERENT parameter, so the original target won't reflect it; stamp trusted as-is.
    /// </summary>
    public void VerifyApplied(Document doc, ChangeSet cs)
    {
        foreach (var ch in cs.Changes)
        {
            if (ch.Frozen) continue;
            if (ch.Resolution is GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam) continue; // value lives in a different param — trust its stamp

            // FIX 3 + FIX 4: re-derive the outcome from the FINAL model state (this runs post-commit), so a
            // type-bound param written once per itemized row sharing it is judged by its settled value — not by a
            // mid-loop VerifyWrite that read it before a later same-type row re-wrote it (the "unverified" inflation).
            // FIX 4: distinguish a param that is ABSENT on the element (an impossible edit — benign skip) from one
            // that is PRESENT but whose value didn't take (a real failure); record the distinction on OutcomeNote.
            bool matches;
            bool anyAbsent = false, anyPresentMismatch = false;
            if (ch.BulkInstanceIds != null)
            {
                // Bulk write: every instance must currently hold the value, else the cell failed.
                matches = true;
                foreach (var uid in ch.BulkInstanceIds)
                {
                    var inst = doc.GetElement(uid);
                    var ip = inst == null ? null : GetParam(inst, ch.ParameterId);
                    if (ip == null) { matches = false; anyAbsent = true; continue; }
                    if (!VerifyWrite(ip, ch)) { matches = false; anyPresentMismatch = true; }
                }
            }
            else
            {
                var host = ch.Binding == "type" ? doc.GetElement(new ElementId(ch.TypeId)) : doc.GetElement(ch.UniqueId);
                var param = host == null ? null : GetParam(host, ch.ParameterId);
                if (param == null) { matches = false; anyAbsent = true; }
                else { matches = VerifyWrite(param, ch); if (!matches) anyPresentMismatch = true; }
            }

            if (!matches)
            {
                ch.Outcome = ApplyOutcome.Failed;
                // FIX 4: precise, honest cause. Present-but-mismatch wins the label when both occur (a real failure
                // is the more serious signal than a benign absent-param). Preserve any existing note (e.g. the C4
                // "N of M instances persisted" partial-bulk note) by appending rather than clobbering.
                string cause = anyPresentMismatch
                    ? "write did not take (value did not persist)"
                    : anyAbsent
                        ? "no such parameter on this element (skipped — nothing to write)"
                        : "";
                if (cause.Length > 0)
                    ch.OutcomeNote = string.IsNullOrEmpty(ch.OutcomeNote) ? cause : ch.OutcomeNote + "; " + cause;
            }
            else if (ch.Outcome == ApplyOutcome.Pending || ch.Outcome == ApplyOutcome.Unverified)
                // FIX 3: a mid-loop "Unverified" whose FINAL value is correct is Applied — clear the inflated stamp.
                ch.Outcome = ApplyOutcome.Applied;
        }
    }

    /// <summary>
    ///     FIX 3 + FIX 4: after <see cref="VerifyApplied"/> has re-stamped every change from the FINAL model state,
    ///     rebuild the apply log and the one-line status so their counts match the by-uid truth — not the mid-loop
    ///     tally that over-counts type-bound params written once per itemized row (FIX 3). The non-applied changes
    ///     are split into honest buckets (FIX 4): a real "write did not take (failed)" vs a benign "no such
    ///     parameter on this element (skipped)". Replaces the stale <see cref="ChangeSet.DiagnosticLog"/> in place
    ///     and returns the corrected status line. No-op semantics preserved when nothing failed. Idempotent.
    /// </summary>
    public string FinalizeApplyReport(ChangeSet cs)
    {
        // Only direct/bulk writes carry a by-final-state outcome here; option-2 conversions are reported in their
        // own section (their value lives in a different param). Frozen changes were never written.
        var writes = cs.Changes
            .Where(c => !c.Frozen && c.Resolution is not (GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam)
                        && c.GroupMode != GroupMode.BuiltinDance)
            .ToList();

        int applied = writes.Count(c => c.Outcome == ApplyOutcome.Applied);

        // FIX 4: split the failures by precise cause (the OutcomeNote VerifyApplied stamped).
        bool IsAbsent(ProposedChange c) => c.OutcomeNote.Contains("no such parameter on this element");
        var failedTookNot = writes.Where(c => c.Outcome == ApplyOutcome.Failed && !IsAbsent(c)).ToList();
        var skippedAbsent = writes.Where(c => c.Outcome == ApplyOutcome.Failed && IsAbsent(c)).ToList();
        // Anything left Unverified after VerifyApplied genuinely couldn't be confirmed (e.g. an element/param that
        // vanished between commit and verify) — keep it as its own honest, small bucket.
        var stillUnverified = writes.Where(c => c.Outcome == ApplyOutcome.Unverified).ToList();

        // Rebuild the failed/unverified label lists from final state so the log enumerates the right cells.
        var failedLabels = failedTookNot.Select(LabelWithNote).ToList();
        var absentLabels = skippedAbsent.Select(LabelWithNote).ToList();
        var unverLabels = stillUnverified.Select(LabelWithNote).ToList();

        cs.DiagnosticLog = BuildFinalApplyLogText(applied, failedLabels, absentLabels, unverLabels, cs);

        // Corrected status line (FIX 3: honest counts; FIX 4: distinct buckets). Keep any non-count suffixes the
        // caller appended (prompts auto-dismissed, run-results path, deletions) by leaving those to the handler.
        // §18: `applied` counts DIRECT/bulk writes only — option-2 (column→type/instance param) conversions land but
        // were excluded, so a conversion-only apply read "Applied 0". Fold the converted count into the headline total.
        // BUG 2 FIX (2026-06-18): count ONLY conversions actually APPLIED (Outcome==Applied) — a FAILED/rolled-back
        // conversion (e.g. option-2b on multi-instance grouped members) must NOT be reported as applied. ApplyNewParam
        // stamps each option-2 change's Outcome, so this is the honest "by-uid verified" count.
        int o2Converted = cs.Changes.Count(c => c.Resolution is GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam && !c.Frozen && c.Outcome == ApplyOutcome.Applied);
        // BUG 3 FIX (2026-06-18): option-2 changes are EXCLUDED from `writes` above, so a FAILED/rolled-back
        // conversion (e.g. option-2b blocked on multi-instance grouped members) was in NO bucket — the run read
        // "applied:0, failed:0" while 3 real edits silently dropped. Count option-2 changes NOT applied as failed so
        // a blocked conversion is honestly surfaced, never omitted. (Apply path unchanged — reporting only.)
        var o2FailedChanges = cs.Changes.Where(c => c.Resolution is GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam && !c.Frozen && c.Outcome != ApplyOutcome.Applied).ToList();
        int o2Failed = o2FailedChanges.Count;
        // Header (caption) renames land in the same apply but aren't in `writes` — fold their applied count into the
        // headline so a header-only (or header+data) apply isn't reported as "Applied 0".
        int hdrApplied = cs.HeaderChanges.Count(h => h.Selected && h.Outcome == ApplyOutcome.Applied);
        int hdrFailed = cs.HeaderChanges.Count(h => h.Selected && h.Outcome == ApplyOutcome.Failed);
        var msg = $"Applied {applied + o2Converted + hdrApplied} change(s)";
        if (hdrApplied > 0) msg += $" (incl. {hdrApplied} heading rename(s))";
        if (failedTookNot.Count > 0) msg += $", {failedTookNot.Count} failed (value didn't take)";
        if (hdrFailed > 0) msg += $", {hdrFailed} heading rename(s) failed";
        if (o2Failed > 0)
        {
            // Surface the SPECIFIC reason(s) (StampFailed stamped them on OutcomeNote) so a blocked conversion says
            // WHY, not just a count — the prior log dropped the cause entirely.
            var reasons = o2FailedChanges.Select(c => c.OutcomeNote).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            msg += $", {o2Failed} failed (column→parameter conversion couldn't be applied"
                 + (reasons.Count > 0 ? ": " + string.Join("; ", reasons) : "") + ")";
        }
        if (skippedAbsent.Count > 0) msg += $", {skippedAbsent.Count} skipped (no such parameter on the element)";
        if (stillUnverified.Count > 0) msg += $", {stillUnverified.Count} unverified";
        if (cs.AutoFixWarnings.Count > 0) msg += $". ⚠ {cs.AutoFixWarnings.Count} geometry auto-fix(es) applied to let edits land (joins / dimension references removed) — review the apply log";
        msg += $". {cs.Skipped.Count(s => s.UserRelevant)} skipped.";   // #64: post-apply count = preview UserRelevant count
        return msg;
    }

    private static string LabelWithNote(ProposedChange ch) =>
        Label(ch) + (string.IsNullOrEmpty(ch.OutcomeNote) ? "" : "  [" + ch.OutcomeNote + "]");

    /// <summary>FIX 3/FIX 4 final apply log: counts + enumerated cells from the settled per-change outcomes, with
    /// the non-applied set split into "write did not take (failed)" vs "no such parameter (skipped)". Mirrors
    /// <see cref="BuildApplyLog"/>'s sections (option-2, Revit warnings via the cs log) without the mid-loop tally.</summary>
    private static string BuildFinalApplyLogText(int applied, List<string> failed, List<string> absent,
        List<string> unverified, ChangeSet cs)
    {
        const int cap = 200;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Transom apply (final, by-uid verified) — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        // §18: include option-2 conversions in the headline total (they land but are tallied separately below), so the
        // "applied:" line matches the FinalizeApplyReport headline + the conversion section.
        int o2ConvertedLog = cs.Changes.Count(c => c.Resolution is GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam && !c.Frozen && c.Outcome == ApplyOutcome.Applied);   // BUG 2 FIX: only actually-applied conversions
        int hdrAppliedLog = cs.HeaderChanges.Count(h => h.Selected && h.Outcome == ApplyOutcome.Applied);
        sb.AppendLine($"applied: {applied + o2ConvertedLog + hdrAppliedLog}");
        sb.AppendLine($"skipped (from preview): {cs.Skipped.Count(s => s.UserRelevant)}");   // #64: user-relevant only

        // Enumerate EXACTLY what landed in the model, so the log alone answers "which edits applied" without probing.
        // Each line: schedule · element/type · field: old -> new  (× N instances for a type/bulk write). Direct/bulk
        // writes only (option-2 conversions + header renames are listed in their own sections below).
        var appliedWrites = cs.Changes
            .Where(c => !c.Frozen && c.Outcome == ApplyOutcome.Applied
                        && c.Resolution is not (GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam)
                        && c.GroupMode != GroupMode.BuiltinDance)
            .ToList();
        if (appliedWrites.Count > 0)
        {
            sb.AppendLine($"\n== changes applied to the model ({appliedWrites.Count}) ==");
            foreach (var c in appliedWrites.Take(cap))
            {
                string scope = c.Binding == "type" ? $"  [type → {c.InstancesAffected} instance(s)]"
                             : c.BulkInstanceIds is { Count: > 0 } ? $"  [{c.InstancesAffected} instance(s)]"
                             : "";
                string sched = string.IsNullOrEmpty(c.SourceScheduleName) ? "" : $"[{c.SourceScheduleName}] ";
                sb.AppendLine($"  - {sched}{Label(c)}{scope}");
            }
            if (appliedWrites.Count > cap) sb.AppendLine($"  … +{appliedWrites.Count - cap} more");
        }

        if (cs.Option2Result != null)
        {
            var o2 = cs.Changes.Where(c => c.Resolution is GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam).ToList();
            int o2Applied = o2.Count(c => c.Outcome == ApplyOutcome.Applied);
            int o2Failed = o2.Count(c => c.Outcome == ApplyOutcome.Failed);
            sb.AppendLine($"option 2 (column→parameter conversion): {o2.Count} change(s) converted "
                          + $"({o2Applied} applied / {o2Failed} failed / {cs.Option2Warnings.Count} type(s) left blank) — see section below");
        }

        // WARN the user about benign geometry-fixups Transom auto-applied so an edit could land (joins removed /
        // dimension references removed). These are NOT failures — the edit succeeded — but the model geometry was
        // adjusted, so the user must be told to review.
        if (cs.AutoFixWarnings.Count > 0)
        {
            sb.AppendLine($"\n== ⚠ geometry auto-fixes applied (review — model geometry was adjusted to let edits land): {cs.AutoFixWarnings.Count} ==");
            foreach (var w in cs.AutoFixWarnings.Take(cap)) sb.AppendLine("  - " + w);
            if (cs.AutoFixWarnings.Count > cap) sb.AppendLine($"  … +{cs.AutoFixWarnings.Count - cap} more");
        }

        // Preserve the Revit warnings/errors captured during the commit (carried on the changeset).
        sb.AppendLine($"\n== Revit warnings / errors during apply: {cs.RevitApplyMessages.Count} ==");
        foreach (var m in cs.RevitApplyMessages.Take(cap)) sb.AppendLine("  - " + m);
        if (cs.RevitApplyMessages.Count > cap) sb.AppendLine($"  … +{cs.RevitApplyMessages.Count - cap} more");

        // FIX 4: two distinct, honest buckets for non-applied direct writes.
        sb.AppendLine($"\n== failed writes — value did not take ({failed.Count}) ==");
        foreach (var f in failed.Take(cap)) sb.AppendLine("  - " + f);
        if (failed.Count > cap) sb.AppendLine($"  … +{failed.Count - cap} more");

        sb.AppendLine($"\n== skipped — no such parameter on the element ({absent.Count}) ==");
        sb.AppendLine("  (the cell's parameter does not exist on that specific element/family — an impossible edit, not a write failure)");
        foreach (var a in absent.Take(cap)) sb.AppendLine("  - " + a);
        if (absent.Count > cap) sb.AppendLine($"  … +{absent.Count - cap} more");

        if (unverified.Count > 0)
        {
            sb.AppendLine($"\n== unverified (could not confirm after commit) ({unverified.Count}) ==");
            foreach (var u in unverified.Take(cap)) sb.AppendLine("  - " + u);
            if (unverified.Count > cap) sb.AppendLine($"  … +{unverified.Count - cap} more");
        }

        // Preserve the option-2 detail + §10 sections + any Revit-deletion note already appended to the prior log.
        if (cs.Option2Result is { } o2r)
        {
            sb.AppendLine($"\n== option 2 — column(s) converted to a type/instance parameter ==");
            sb.AppendLine($"  params created: {o2r.ParamsCreated}; values written (edits + carried-forward originals): {o2r.ValuesWritten}; "
                          + $"source columns replaced: {o2r.ColumnsReplaced}" + (o2r.ColumnsAppended > 0 ? $"; columns appended: {o2r.ColumnsAppended}" : ""));
        }
        if (cs.Option2Warnings.Count > 0)
        {
            sb.AppendLine($"\n== option 2 — types left blank (varying values under a type parameter): {cs.Option2Warnings.Count} ==");
            foreach (var w in cs.Option2Warnings.Take(cap)) sb.AppendLine("  - " + w);
            if (cs.Option2Warnings.Count > cap) sb.AppendLine($"  … +{cs.Option2Warnings.Count - cap} more");
        }

        AppendHeaderChangeSection(sb, cs, cap);
        return sb.ToString();
    }

    /// <summary>Appends the column-caption (header) rename section to an apply log, listing each rename + outcome.
    /// Shared by both <see cref="BuildApplyLog"/> and <see cref="BuildFinalApplyLogText"/>. No-op when none.</summary>
    private static void AppendHeaderChangeSection(System.Text.StringBuilder sb, ChangeSet cs, int cap)
    {
        var hdrs = cs.HeaderChanges.Where(h => h.Selected).ToList();
        if (hdrs.Count == 0) return;
        int hApplied = hdrs.Count(h => h.Outcome == ApplyOutcome.Applied);
        int hFailed = hdrs.Count(h => h.Outcome == ApplyOutcome.Failed);
        sb.AppendLine($"\n== column heading renames: {hdrs.Count} ({hApplied} applied / {hFailed} failed) ==");
        foreach (var h in hdrs.Take(cap))
        {
            string where = string.IsNullOrEmpty(h.ScheduleName) ? "" : $"[{h.ScheduleName}] ";
            string outcome = h.Outcome == ApplyOutcome.Applied ? "applied"
                : h.Outcome == ApplyOutcome.Failed ? "FAILED" : h.Outcome.ToString().ToLower();
            string note = string.IsNullOrEmpty(h.OutcomeNote) ? "" : $" — {h.OutcomeNote}";
            sb.AppendLine($"    - {where}'{h.OldHeading}' → '{h.NewHeading}'  [{outcome}]{note}");
        }
        if (hdrs.Count > cap) sb.AppendLine($"    … +{hdrs.Count - cap} more");
    }

    private static string Label(ProposedChange ch) =>
        $"{(string.IsNullOrEmpty(ch.ElementName) ? "?" : ch.ElementName)} · {ch.Field}: '{ch.OldValue}' -> '{ch.NewValue}'";

    /// <summary>Full plain-text record of an apply for the Copy-log button: counts, each failed/unverified write,
    /// and every Revit warning/error raised during the commit.</summary>
    private static string BuildApplyLog(int applied, List<string> failed, List<string> unverified,
        List<string> revitMessages, ChangeSet cs, bool rolledBack = false, string? retryNote = null)
    {
        const int cap = 200;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Transom apply — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        if (rolledBack)
            sb.AppendLine("*** ROLLED BACK BY REVIT — NOTHING PERSISTED. The 'attempted' count below is what was " +
                          "written in-transaction before Revit discarded the commit (usually unresolved errors). ***");
        if (retryNote != null) sb.AppendLine(retryNote);
        sb.AppendLine($"{(rolledBack ? "attempted (NOT saved)" : "applied")}: {applied}");
        sb.AppendLine($"skipped (from preview): {cs.Skipped.Count(s => s.UserRelevant)}");   // #64: user-relevant only

        // D2: the headline "applied" counts only DIRECT writes (the main loop) — option-2 conversions are stamped
        // Applied on their changes but never increment that int, which made the log read "applied: 0" while a
        // conversion wrote real edits. Reconcile with an explicit, self-consistent option-2 tally so the headline
        // is never read as "nothing happened". (Counts derived from the change outcomes + §10 blanks.)
        if (cs.Option2Result != null)
        {
            var o2 = cs.Changes.Where(c => c.Resolution is GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam).ToList();
            int o2Applied = o2.Count(c => c.Outcome == ApplyOutcome.Applied);
            int o2Failed = o2.Count(c => c.Outcome == ApplyOutcome.Failed);
            sb.AppendLine($"option 2 (column→parameter conversion): {o2.Count} change(s) converted "
                          + $"({o2Applied} applied / {o2Failed} failed / {cs.Option2Warnings.Count} type(s) left blank) — see section below");
        }

        sb.AppendLine($"\n== Revit warnings / errors during apply: {revitMessages.Count} ==");
        foreach (var m in revitMessages.Take(cap)) sb.AppendLine("  - " + m);
        if (revitMessages.Count > cap) sb.AppendLine($"  … +{revitMessages.Count - cap} more");

        if (rolledBack)
        {
            // No zero-tallies under a rollback banner — "failed writes: 0" would read as success.
            sb.AppendLine($"\nfailed/unverified: n/a — all {applied} in-transaction writes were discarded by the rollback");
            if (failed.Count > 0)
            {
                sb.AppendLine($"\n== per-change retry failures: {failed.Count} ==");
                foreach (var f in failed.Take(cap)) sb.AppendLine("  - " + f);
                if (failed.Count > cap) sb.AppendLine($"  … +{failed.Count - cap} more");
            }
        }
        else
        {
            sb.AppendLine($"\n== failed writes: {failed.Count} ==");
            foreach (var f in failed.Take(cap)) sb.AppendLine("  - " + f);
            if (failed.Count > cap) sb.AppendLine($"  … +{failed.Count - cap} more");

            sb.AppendLine($"\n== unverified (Set returned but value didn't take): {unverified.Count} ==");
            foreach (var u in unverified.Take(cap)) sb.AppendLine("  - " + u);
            if (unverified.Count > cap) sb.AppendLine($"  … +{unverified.Count - cap} more");
        }

        // D1/D3: explicit record of what the option-2 conversion created/wrote, with the EDITED cells named
        // (incl. Hardware Group). Without this the conversion was invisible in the apply log — only the transient
        // UI status line carried it — so a user reading the pasted log saw "applied: 0" and concluded edits were
        // lost. The §10 "types left blank" section below is the complementary caveat.
        if (cs.Option2Result is { } o2r)
        {
            sb.AppendLine($"\n== option 2 — column(s) converted to a type/instance parameter ==");
            sb.AppendLine($"  params created: {o2r.ParamsCreated}; values written (edits + carried-forward originals): {o2r.ValuesWritten}; "
                          + $"source columns replaced: {o2r.ColumnsReplaced}" + (o2r.ColumnsAppended > 0 ? $"; columns appended: {o2r.ColumnsAppended}" : ""));
            var o2changes = cs.Changes
                .Where(c => c.Resolution is GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam)
                .ToList();
            sb.AppendLine($"  edited cells in this conversion: {o2changes.Count}");
            foreach (var c in o2changes.Take(cap))
            {
                string where = string.IsNullOrEmpty(c.SourceScheduleName) ? "" : $"[{c.SourceScheduleName}] ";
                string outcome = c.Outcome == ApplyOutcome.Applied ? "applied"
                    : c.Outcome == ApplyOutcome.Failed ? "FAILED" : c.Outcome.ToString().ToLower();
                string note = string.IsNullOrEmpty(c.OutcomeNote) ? "" : $" — {c.OutcomeNote}";
                sb.AppendLine($"    - {where}{c.ElementName} · {c.Field} → '{c.Field} (Transom)' = '{c.NewValue}'  [{outcome}]{note}");
            }
            if (o2changes.Count > cap) sb.AppendLine($"    … +{o2changes.Count - cap} more");
        }

        // §10: mandatory per-type warnings for divergent types left blank by a TYPE-param conversion.
        if (cs.Option2Warnings.Count > 0)
        {
            sb.AppendLine($"\n== option 2 — types left blank (varying values under a type parameter): {cs.Option2Warnings.Count} ==");
            foreach (var w in cs.Option2Warnings.Take(cap)) sb.AppendLine("  - " + w);
            if (cs.Option2Warnings.Count > cap) sb.AppendLine($"  … +{cs.Option2Warnings.Count - cap} more");
        }

        AppendHeaderChangeSection(sb, cs, cap);
        return sb.ToString();
    }

    private static bool SetValue(Parameter param, ProposedChange ch) =>
        ch.IsString ? param.Set(ch.NewString) : ch.IsInt ? param.Set(ch.NewInt) : param.Set(ch.NewDouble);

    /// <summary>Enables "vary by group instance" on a project/shared parameter so its value can be written per
    /// group instance without ungrouping (the sanctioned, durable mechanism). Idempotent — only flips when off.
    /// Returns false if the parameter doesn't support it (built-in or family-embedded/calculated param).</summary>
    private static bool EnsureVary(Parameter param, Document doc)
    {
        try
        {
            if (param.Definition is InternalDefinition def)
            {
                if (!def.VariesAcrossGroups) def.SetAllowVaryBetweenGroups(doc, true);
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    ///     Resolution option 2 (§6): REPLACE each group-conflicted column with ONE new shared parameter holding
    ///     the ORIGINAL stored values MERGED with the user's edits (edit wins on collision). TYPE binding writes
    ///     one value per type (a type param shares it across all instances of the type); INSTANCE binding writes
    ///     every element its own value. After writing, the SOURCE field is removed so the schedule shows a single
    ///     column at the original index (the built-in param is RETAINED on elements — only the FIELD is removed).
    ///     Runs inside the caller's transaction. Returns a status note (incl. any mandatory §10 divergent-type
    ///     warnings appended), or "" when nothing was created.
    /// </summary>
    private string ApplyNewParam(Document doc, List<ProposedChange> changes, ChangeSet cs, List<string> failed)
    {
        var app = doc.Application;
        int created = 0, written = 0, replaced = 0, appended = 0;
        var warnings = cs.Option2Warnings;        // §10 per-(schedule,type) blank-cell warnings (survives log rebuild)
        var guards = new List<string>();          // key-guard / no-source append notes

        // One new param per resolvable column — keyed (ParameterId, Field), matching the picker/eligibility.
        foreach (var pg in changes.GroupBy(c => ChangeSet.ColumnKey(c.ParameterId, c.Field)))
        {
            var list = pg.ToList();
            var sample = list[0];
            bool instanceBinding = sample.Resolution == GroupResolution.NewInstanceParam;
            int sourceParamId = sample.ParameterId;

            // Source parameter (for storage/spec) + the affected categories. For INSTANCE binding bind the
            // INSTANCE category; for TYPE binding bind the TYPE category (usually equal, but resolve properly).
            Parameter? src = null;
            var cats = app.Create.NewCategorySet();
            foreach (var ch in list)
            {
                var uid = ch.BulkInstanceIds is { Count: > 0 } ? ch.BulkInstanceIds[0] : ch.UniqueId;
                var el = string.IsNullOrEmpty(uid) ? null : doc.GetElement(uid);
                if (el == null) continue;
                src ??= GetParam(el, sourceParamId);
                Category? cat;
                if (instanceBinding) cat = el.Category;
                else
                {
                    var tid0 = el.GetTypeId();
                    var typeEl0 = tid0 != null && tid0 != ElementId.InvalidElementId ? doc.GetElement(tid0) : null;
                    cat = (typeEl0 ?? el).Category;
                }
                if (cat is { AllowsBoundParameters: true }) cats.Insert(cat);
            }
            string kind = instanceBinding ? "instance" : "type";
            if (src == null || cats.IsEmpty)
            {
                var why = $"new {kind} param not created — " + (src == null ? "source parameter not readable on any element" : "no category allows bound parameters");
                failed.Add($"{sample.Field} — {why}"); StampFailed(list, why); continue;
            }

            // Derive the new param's spec from the SOURCE storage type — don't blindly trust GetDataType().
            var spec = DeriveSpec(src);

            // §10b: TYPE binding on an ITEMIZED schedule whose instances disagree per type is a likely mistake
            // (the user entered differing values for one type but forced a single type param). PRE-SCAN every
            // schedule in this column for viability BEFORE any model mutation. If any itemized schedule has a
            // non-uniform type → REJECT THE WHOLE COLUMN: create no param, change no field (the original column
            // stays byte-identical), stamp every change Failed + a rejection note, and let the run-results report
            // surface the attempted values. The GROUPED divergent path (blank + warn, §10a) is unaffected:
            // grouped schedules never pass the `itemized` gate, so they fall through to the normal write below.
            if (!instanceBinding && ColumnRejectedItemized(doc, list))
            {
                const string reject = "rejected: differing values for one or more types — choose an instance " +
                    "parameter or fix the data, then re-import this column";
                foreach (var ch in list) { ch.Outcome = ApplyOutcome.Failed; ch.OutcomeNote = reject; }
                failed.Add($"{sample.Field} (new type param) — {reject}");
                continue;
            }

            // §8: the param NAME encodes the binding kind ("_instance" suffix for instance) so type/instance
            // params can never collide on a name — EnsureSharedParam applies the suffix. ColumnHeading stays clean.
            var name = $"{sample.Field} (Transom)";
            ElementId paramId; Guid guid;
            try { (paramId, guid) = EnsureSharedParam(doc, app, name, spec, cats, instanceBinding); }
            catch (Exception ex) { var why = $"new {kind} param threw on create/bind: {ex.Message}"; failed.Add($"{sample.Field} — {why}"); StampFailed(list, why); continue; }
            if (paramId == ElementId.InvalidElementId || guid == Guid.Empty)
            { var why = $"new {kind} param couldn't be created/bound ('{name}')"; failed.Add($"{sample.Field} — {why}"); StampFailed(list, why); continue; }
            created++;
            string newParamName = ResolveParamName(doc, paramId, name); // actual name (suffix/disambiguation applied)

            // BUG 1 FIX (option-2b, research1 2026-06-18): a freshly-created INSTANCE shared param defaults to
            // "values aligned per group type" (vary OFF). Writing DIVERGENT per-instance values to multi-instance
            // GROUPED members with vary OFF raises Revit's unsuppressable "group changed outside edit mode" → the
            // whole transaction rolls back (no param, no write, originals intact — exactly the live-confirmed symptom).
            // So flip the new param to "vary by group instance" + Regenerate BEFORE the per-instance write loop, the
            // same staging the option-1/ProjectVary path does via EnsureVary. Type binding writes once per type and
            // never varies, so it's instance-only. Vary is a DEFINITION-level setting — flipping it on any one bound
            // instance's Parameter flips it for the whole param. Best-effort: if the param/category can't vary (UI
            // would grey the checkbox), the write loop's own failure handling still reports it honestly.
            if (instanceBinding)
            {
                // ORDER MATTERS (research1 retest, 2026-06-18): the binding must be COMMITTED before its
                // InternalDefinition is reachable, and vary must be set on the INTERNAL definition (a freshly-created
                // SHARED param exposes only ExternalDefinition via get_Parameter — SetAllowVaryBetweenGroups doesn't
                // exist there). So: (1) Regenerate to commit the binding; (2) find the bound InternalDefinition by GUID
                // from the doc's ParameterBindings; (3) SetAllowVaryBetweenGroups(true); (4) Regenerate; (5) RE-READ
                // VariesAcrossGroups to confirm it STUCK. If it didn't stick (category/spec can't vary), record a
                // diagnostic — the per-instance write loop will then fail honestly rather than silently rolling back.
                doc.Regenerate();   // commit the binding so the param resolves to its bound InternalDefinition below
                // Get the InternalDefinition off a bound instance's parameter — AFTER the Regenerate the committed
                // binding exposes the InternalDefinition (a pre-commit fresh shared param would expose only the
                // ExternalDefinition, which has no SetAllowVaryBetweenGroups — that ordering was the prior fix's gap).
                InternalDefinition? varyDef = null;
                foreach (var ch in list)
                {
                    var uid = ch.BulkInstanceIds is { Count: > 0 } ? ch.BulkInstanceIds[0] : ch.UniqueId;
                    var rep = string.IsNullOrEmpty(uid) ? null : doc.GetElement(uid);
                    if (rep?.get_Parameter(guid)?.Definition is InternalDefinition d) { varyDef = d; break; }
                }
                bool varied = false;
                if (varyDef != null)
                {
                    try { if (!varyDef.VariesAcrossGroups) varyDef.SetAllowVaryBetweenGroups(doc, true); } catch { /* report below */ }
                    doc.Regenerate();
                    try { varied = varyDef.VariesAcrossGroups; } catch { varied = false; }
                }
                if (!varied)
                    cs.Option2Warnings.Add($"'{sample.Field}': couldn't enable 'vary by group instance' on the new " +
                        "instance parameter — per-instance values in multi-instance groups may be rejected by Revit.");
            }

            // MERGE + WRITE per distinct source schedule in this column. Track per-change outcome so each change
            // is stamped Applied/Failed by the element/type it targets. A change is Applied only when it was
            // actually written (touched) AND none of its writes failed — one failed instance fails the whole cell
            // (mirrors the bulk-write semantics on the direct path).
            var touched = new HashSet<ProposedChange>();
            var changeFailed = new HashSet<ProposedChange>();

            foreach (var schedUid in list.Select(c => c.SourceScheduleUid).Distinct())
            {
                var vs = ResolveSchedule(doc, schedUid, list.First(c => c.SourceScheduleUid == schedUid).SourceScheduleName);
                if (vs == null)
                {
                    // Can't resolve the schedule → can't merge originals; fall back to writing edits only +
                    // append-only (never throw, never replace a field we can't read). Edits still land.
                    foreach (var ch in list.Where(c => c.SourceScheduleUid == schedUid))
                    {
                        touched.Add(ch);
                        bool ok = instanceBinding
                            ? WriteEditInstances(doc, ch, guid, sourceParamId, failed)
                            : WriteEditType(doc, ch, guid, failed);
                        if (ok) written++; else changeFailed.Add(ch);
                    }
                    continue;
                }

                var elements = new FilteredElementCollector(doc, vs.Id).WhereElementIsNotElementType().ToList();
                int wroteThisSched = 0;

                if (instanceBinding)
                {
                    // Edit lookup: instance UniqueId → the change carrying its edit (over bulk ids + single uid).
                    var editByUid = new Dictionary<string, ProposedChange>();
                    foreach (var ch in list.Where(c => c.SourceScheduleUid == schedUid))
                        foreach (var uid in (ch.BulkInstanceIds ?? new List<string> { ch.UniqueId }))
                            if (!string.IsNullOrEmpty(uid)) editByUid[uid] = ch;

                    foreach (var el in elements)
                    {
                        bool isEdit = editByUid.TryGetValue(el.UniqueId, out var ech);
                        if (isEdit) touched.Add(ech!);
                        var np = el.get_Parameter(guid);
                        if (np == null || np.IsReadOnly)
                        {
                            // The new param should be bound here; a null/readonly slot is a real failure, not a
                            // silent skip (the source value would otherwise be lost when the field is removed).
                            failed.Add($"{newParamName} on {SafeName(el)}");
                            if (isEdit) changeFailed.Add(ech!);
                            continue;
                        }
                        // finalVal = edit ?? live source value. EDIT WINS. Never null (ReadLive → empty-string =
                        // exactly what the old column showed = not data loss).
                        var finalVal = isEdit ? EditVal(ech!) : ReadLive(el, sourceParamId);
                        bool ok = SetParsed(np, finalVal) && VerifyParsed(np, finalVal);
                        if (ok) { written++; wroteThisSched++; }
                        else { failed.Add($"{newParamName} on {SafeName(el)}"); if (isEdit) changeFailed.Add(ech!); }
                    }
                }
                else // TYPE binding
                {
                    // Edit lookup: type id → the change carrying that type's edit.
                    var editByType = new Dictionary<long, ProposedChange>();
                    foreach (var ch in list.Where(c => c.SourceScheduleUid == schedUid))
                    {
                        long t = ElementTypeIdOf(doc, ch);
                        if (t >= 0) editByType[t] = ch;
                    }

                    // Group the schedule's elements by type; each type writes ONE value to its type param.
                    foreach (var typeGrp in elements.Where(e => e.GetTypeId() != ElementId.InvalidElementId)
                                 .GroupBy(e => e.GetTypeId().Value))
                    {
                        long typeId = typeGrp.Key;
                        var typeEl = doc.GetElement(new ElementId(typeId));
                        var np = typeEl?.get_Parameter(guid);
                        bool edited = editByType.TryGetValue(typeId, out var tch);

                        if (edited)
                        {
                            // Edited type → write the edit once to the type (shared by all its instances).
                            touched.Add(tch!);
                            var v = EditVal(tch!);
                            bool ok = np != null && !np.IsReadOnly && SetParsed(np, v) && VerifyParsed(np, v);
                            if (ok) { written++; wroteThisSched++; }
                            else { failed.Add($"{newParamName} on type {SafeName(typeEl ?? (Element)typeGrp.First())}"); changeFailed.Add(tch!); }
                            continue;
                        }

                        // Unedited type: carry forward the ORIGINAL value if uniform across its instances.
                        var originals = typeGrp.Select(e => ReadLive(e, sourceParamId)).ToList();
                        bool uniform = originals.Select(CanonicalParsed).Distinct().Count() <= 1;
                        if (uniform)
                        {
                            // Uniform (or single instance) → carry forward the one original value.
                            var v = originals[0];
                            bool ok = np != null && !np.IsReadOnly && SetParsed(np, v) && VerifyParsed(np, v);
                            if (ok) { written++; wroteThisSched++; }
                            else failed.Add($"{newParamName} on type {SafeName(typeEl ?? (Element)typeGrp.First())}");
                        }
                        else
                        {
                            // §10: DIVERGENT + UNEDITED under a TYPE param → SKIP (write nothing). The built-in
                            // source param is RETAINED on every instance (only the FIELD is removed), so no data
                            // is deleted; the new cell shows blank for this type. Record the MANDATORY warning.
                            string typeName = SafeName(typeEl ?? (Element)typeGrp.First());
                            warnings.Add(DivergentTypeWarning(newParamName, typeName, sample.Field));
                        }
                    }
                }

                // §6/§7: REPLACE the source field with the new column on THIS schedule once anything was written.
                if (wroteThisSched > 0)
                {
                    var res = AddOrReplaceField(doc, vs, paramId, sourceParamId, CleanHeading(sample));
                    switch (res)
                    {
                        case AddFieldResult.Replaced: replaced++; break;
                        case AddFieldResult.AppendedKeyGuard:
                            appended++;
                            guards.Add($"'{sample.Field}' on '{vs.Name}' kept its original column — it drives the " +
                                       "schedule's sort/group/filter, so the new column was ADDED alongside (removing it would orphan the key).");
                            break;
                        case AddFieldResult.AppendedNoSource:
                            appended++;
                            guards.Add($"'{sample.Field}' was ADDED to '{vs.Name}' — the original column wasn't present there to replace.");
                            break;
                        // AlreadyPresent (idempotent re-run) / NotSchedulable / Failed → no field-count change to report.
                    }
                }
            }

            // Stamp every change: Applied only when it was actually written AND none of its writes failed.
            // A change never touched (its instances/type absent from the schedule) is Failed (nothing landed).
            foreach (var ch in list)
                ch.Outcome = touched.Contains(ch) && !changeFailed.Contains(ch)
                    ? ApplyOutcome.Applied : ApplyOutcome.Failed;
        }

        // §10 divergent-type warnings live in cs.Option2Warnings — BuildApplyLog emits them into the copyable
        // apply log (which is rebuilt AFTER this method, so appending to cs.DiagnosticLog here would be lost).

        if (created == 0) return "";
        // D1/D2: record the conversion counts on the ChangeSet so BuildApplyLog can emit an explicit option-2
        // section (the status-note `note` returned below only reaches the transient UI line, not the apply log).
        cs.Option2Result = new Option2Summary
        {
            ParamsCreated = created, ValuesWritten = written, ColumnsReplaced = replaced, ColumnsAppended = appended,
        };
        var note = $"option 2: {created} new param(s), {written} value(s) written, " +
                   $"{replaced} column(s) replaced" + (appended > 0 ? $", {appended} appended" : "");
        if (guards.Count > 0) note += "  —  " + string.Join("  ", guards);
        if (warnings.Count > 0)
            note += $"  —  {warnings.Count} type(s) left blank (instances had varying values under the type param; " +
                    "their original values are retained on each instance — see log).";
        return note;
    }

    /// <summary>Fallback write for INSTANCE binding when the source schedule can't be resolved (no merge of
    /// originals possible): writes only the change's edited value to each of its instances via the new GUID.</summary>
    private static bool WriteEditInstances(Document doc, ProposedChange ch, Guid guid, int sourceParamId, List<string> failed)
    {
        bool any = false;
        foreach (var uid in (ch.BulkInstanceIds ?? new List<string> { ch.UniqueId }))
        {
            var el = string.IsNullOrEmpty(uid) ? null : doc.GetElement(uid);
            var np = el?.get_Parameter(guid);
            if (np == null || np.IsReadOnly) { failed.Add($"{ch.Field} (Transom) on {Label(ch)}"); continue; }
            var v = EditVal(ch);
            if (SetParsed(np, v) && VerifyParsed(np, v)) any = true;
            else failed.Add($"{ch.Field} (Transom) on {Label(ch)}");
        }
        return any;
    }

    /// <summary>Fallback write for TYPE binding when the source schedule can't be resolved: writes the change's
    /// edited value once to its type via the new GUID.</summary>
    private static bool WriteEditType(Document doc, ProposedChange ch, Guid guid, List<string> failed)
    {
        long tid = ElementTypeIdOf(doc, ch);
        var typeEl = tid >= 0 ? doc.GetElement(new ElementId(tid)) : null;
        var np = typeEl?.get_Parameter(guid);
        if (np == null || np.IsReadOnly) { failed.Add($"{ch.Field} (Transom) on type {Label(ch)}"); return false; }
        var v = EditVal(ch);
        if (SetParsed(np, v) && VerifyParsed(np, v)) return true;
        failed.Add($"{ch.Field} (Transom) on type {Label(ch)}");
        return false;
    }

    /// <summary>
    ///     §10b viability pre-scan for a TYPE-param column: returns true when ANY source schedule in the column
    ///     is ITEMIZED ("Itemize every instance" checked) AND has a type whose instances would need MORE THAN ONE
    ///     value under the chosen type param. The per-instance final value is the instance's own edit (edit wins)
    ///     else its live source value; grouped by type, a type is non-uniform when its instances carry >1 distinct
    ///     canonical value — exactly the differing per-instance input the user can only enter on an itemized
    ///     schedule. A true result means REJECT THE WHOLE COLUMN (§10b): the caller makes no model change at all.
    ///     Runs READ-ONLY, BEFORE any mutation, so a rejected column leaves its source field byte-identical.
    ///     GROUPED schedules are never itemized → they return false here and fall through to the §10a blank+warn
    ///     write path. Best-effort: a schedule that can't be read is treated as not-rejecting (false).
    /// </summary>
    private static bool ColumnRejectedItemized(Document doc, List<ProposedChange> list)
    {
        foreach (var schedUid in list.Select(c => c.SourceScheduleUid).Distinct())
        {
            var vs = ResolveSchedule(doc, schedUid, list.First(c => c.SourceScheduleUid == schedUid).SourceScheduleName);
            if (vs == null) continue;
            bool itemized;
            try { itemized = vs.Definition.IsItemized; }
            catch { itemized = false; }
            if (!itemized) continue; // grouped → §10a (blank + warn), never a wholesale reject

            // Per-instance edit lookup for THIS schedule (over bulk ids + single uid), mirroring the write path.
            var editByUid = new Dictionary<string, ProposedChange>();
            foreach (var ch in list.Where(c => c.SourceScheduleUid == schedUid))
                foreach (var uid in (ch.BulkInstanceIds ?? new List<string> { ch.UniqueId }))
                    if (!string.IsNullOrEmpty(uid)) editByUid[uid] = ch;

            // Group the schedule's elements by type; a type whose merged per-instance values disagree → reject.
            var byType = new Dictionary<long, HashSet<string>>();
            try
            {
                foreach (var el in new FilteredElementCollector(doc, vs.Id).WhereElementIsNotElementType())
                {
                    var tid = el.GetTypeId();
                    if (tid == null || tid == ElementId.InvalidElementId) continue;
                    var finalVal = editByUid.TryGetValue(el.UniqueId, out var ech)
                        ? EditVal(ech) : ReadLive(el, list[0].ParameterId);
                    if (!byType.TryGetValue(tid.Value, out var set)) byType[tid.Value] = set = new HashSet<string>();
                    set.Add(CanonicalParsed(finalVal));
                    if (set.Count > 1) return true; // differing values for one type on an itemized schedule → reject
                }
            }
            catch { /* unreadable schedule → don't reject on its account */ }
        }
        return false;
    }

    /// <summary>The §10 mandatory per-type warning shown when a divergent, unedited type is left blank by a
    /// TYPE-param conversion. Bracketed fields filled per the spec template.</summary>
    private static string DivergentTypeWarning(string newParamName, string typeName, string oldParamName) =>
        $"'{newParamName}' is blank for type '{typeName}' because the column was converted to a type parameter " +
        $"and that type's instances had varying values. The original per-instance values remain in the '{oldParamName}' " +
        "parameter on each instance — type a single value into this cell to set one unified value for all of them.";

    /// <summary>The actual stored name of a just-ensured shared parameter (the requested name plus any binding
    /// suffix / disambiguation EnsureSharedParam applied), for user-facing messages. Falls back to the request.</summary>
    private static string ResolveParamName(Document doc, ElementId paramId, string requested)
    {
        try { return doc.GetElement(paramId)?.Name ?? requested; }
        catch { return requested; }
    }

    /// <summary>Stamps every change in a failed option-2 column as <see cref="ApplyOutcome.Failed"/> and records the
    /// SPECIFIC reason on each change's OutcomeNote — so the per-change failure cause is durable (run-results + the
    /// status/log surface it). Without the reason, an option-2 early-bail showed only a generic "N failed" with no
    /// cause (the local `failed` list isn't enumerated for option-2 in the final log).</summary>
    private static void StampFailed(List<ProposedChange> changes, string reason = "")
    {
        foreach (var ch in changes)
        {
            ch.Outcome = ApplyOutcome.Failed;
            if (!string.IsNullOrEmpty(reason)) ch.OutcomeNote = reason;
        }
    }

    /// <summary>The spec (ForgeTypeId) to create a shared param matching a source parameter's storage type.</summary>
    private static ForgeTypeId DeriveSpec(Parameter src)
    {
        try
        {
            switch (src.StorageType)
            {
                case StorageType.String: return SpecTypeId.String.Text;
                case StorageType.Integer:
                    try { if (src.Definition.GetDataType() == SpecTypeId.Boolean.YesNo) return SpecTypeId.Boolean.YesNo; } catch { }
                    return SpecTypeId.Int.Integer;
                case StorageType.Double:
                    var dt = src.Definition.GetDataType();
                    if (dt != null && !dt.Empty() && UnitUtils.IsMeasurableSpec(dt)) return dt;
                    return SpecTypeId.Number;
            }
        }
        catch { /* fall through */ }
        return SpecTypeId.String.Text;
    }

    /// <summary>
    ///     Ensures a shared parameter exists and is bound (TYPE or INSTANCE per <paramref name="instanceBinding"/>)
    ///     to <paramref name="cats"/>; regenerates, then returns its element id + GUID. §8 NAMING: the param NAME
    ///     encodes the binding kind — INSTANCE ⇒ "<paramref name="name"/>_instance", TYPE ⇒ "<paramref name="name"/>"
    ///     unchanged — so a type param and an instance param can NEVER collide on a name (the binding-flip hazard
    ///     is structurally eliminated: the instance path can't reuse/re-bind the type-named param). Reuses an
    ///     existing definition only when name+spec match (else disambiguates the name); extends the binding to new
    ///     categories WITHOUT changing its kind. Saves/restores the app shared-parameter file so an import doesn't
    ///     change session state, and writes a valid header if it has to create one.
    /// </summary>
    private static (ElementId id, Guid guid) EnsureSharedParam(Document doc,
        Autodesk.Revit.ApplicationServices.Application app, string name, ForgeTypeId spec, CategorySet cats,
        bool instanceBinding)
    {
        // §8: distinct backing-param name per binding kind (the visible ColumnHeading stays the clean source name).
        string paramName = instanceBinding ? name + "_instance" : name;

        var savedFile = app.SharedParametersFilename;
        try
        {
            var defFile = app.OpenSharedParameterFile();
            if (defFile == null)
            {
                var tmp = Path.Combine(Path.GetTempPath(), "Transom_SharedParameters.txt");
                if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0)
                    File.WriteAllText(tmp,
                        "# This is a Revit shared parameter file.\n# Do not edit manually.\n" +
                        "*META\tVERSION\tMINVERSION\nMETA\t2\t1\n*GROUP\tID\tNAME\n" +
                        "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\n");
                app.SharedParametersFilename = tmp;
                defFile = app.OpenSharedParameterFile();
            }
            if (defFile == null) return (ElementId.InvalidElementId, Guid.Empty);

            var group = defFile.Groups.Cast<DefinitionGroup>().FirstOrDefault(g => g.Name == "Transom")
                        ?? defFile.Groups.Create("Transom");

            // Reuse a same-named definition only if its spec matches; otherwise pick a fresh, disambiguated name.
            ExternalDefinition? ext = null;
            var useName = paramName;
            for (int i = 1; ext == null && i <= 50; i++)
            {
                var existing = group.Definitions.Cast<Definition>().FirstOrDefault(d => d.Name == useName) as ExternalDefinition;
                if (existing == null)
                {
                    ext = group.Definitions.Create(new ExternalDefinitionCreationOptions(useName, spec)) as ExternalDefinition;
                    break;
                }
                if (SameSpec(existing, spec)) { ext = existing; break; }
                useName = $"{paramName} ({i + 1})";
            }
            if (ext == null) return (ElementId.InvalidElementId, Guid.Empty);

            var bindings = doc.ParameterBindings;
            ElementBinding bind = instanceBinding ? app.Create.NewInstanceBinding(cats) : app.Create.NewTypeBinding(cats);
            if (bindings.Contains(ext))
            {
                // Extend the existing binding to cover any new categories (don't leave new categories unbound),
                // preserving its kind. Read existing categories via the matching binding cast — InstanceBinding
                // for the "_instance" param, TypeBinding otherwise (both expose .Categories). The "_instance"
                // suffix means the kinds always match here; the defensive branch never disambiguates in normal flow.
                var union = app.Create.NewCategorySet();
                var existingDef = bindings.get_Item(ext);
                CategorySet? existingCats = instanceBinding
                    ? (existingDef as InstanceBinding)?.Categories
                    : (existingDef as TypeBinding)?.Categories;
                if (existingCats != null) foreach (Category c in existingCats) union.Insert(c);
                foreach (Category c in cats) union.Insert(c);
                ElementBinding rebind = instanceBinding ? app.Create.NewInstanceBinding(union) : app.Create.NewTypeBinding(union);
                bindings.ReInsert(ext, rebind, GroupTypeId.Data);
            }
            else if (!bindings.Insert(ext, bind, GroupTypeId.Data))
            {
                return (ElementId.InvalidElementId, Guid.Empty);
            }

            // The binding + per-element parameter aren't resolvable until the document regenerates.
            doc.Regenerate();

            var spe = SharedParameterElement.Lookup(doc, ext.GUID);
            return (spe?.Id ?? ElementId.InvalidElementId, ext.GUID);
        }
        finally
        {
            try { app.SharedParametersFilename = savedFile; } catch { /* best effort */ }
        }
    }

    private static bool SameSpec(ExternalDefinition def, ForgeTypeId spec)
    {
        try { return def.GetDataType() == spec; }
        catch { return false; }
    }

    /// <summary>Visible column title for the new option-2 field: the source column's display heading when we
    /// captured it, else the parameter/field name — WITHOUT the "(Transom)" suffix the underlying param carries.</summary>
    private static string CleanHeading(ProposedChange sample) =>
        !string.IsNullOrWhiteSpace(sample.SourceHeading) ? sample.SourceHeading : sample.Field;

    /// <summary>Outcome of placing the new option-2 field on one schedule (§7).</summary>
    private enum AddFieldResult { Replaced, AppendedKeyGuard, AppendedNoSource, AlreadyPresent, NotSchedulable, Failed }

    /// <summary>
    ///     §7: places the new parameter <paramref name="newParamId"/> on ONE schedule, REPLACING the source
    ///     column (<paramref name="sourceParamId"/>) so there's a single column at the source's original index.
    ///     The REPLACE sequence (design2 §0) inserts the new field AT the source's index, sets its heading, then
    ///     removes the ORIGINAL by its STABLE <see cref="ScheduleFieldId"/> (NEVER by integer index — that would
    ///     delete the field just inserted) — rolling back the insert if the remove throws. Removes the FIELD only,
    ///     NEVER the built-in PARAM. Guards: a source that drives the schedule's sort/group/filter is kept (append
    ///     alongside, never orphan a key); a source not present in this schedule is appended.
    /// </summary>
    private static AddFieldResult AddOrReplaceField(Document doc, ViewSchedule sched, ElementId newParamId,
        int sourceParamId, string heading)
    {
        try
        {
            var def = sched.Definition;

            var sf = def.GetSchedulableFields().FirstOrDefault(f => f.ParameterId == newParamId);
            if (sf == null) return AddFieldResult.NotSchedulable; // category mismatch — can't show it here

            // Idempotent re-run: the new param is already a field on this schedule.
            if (def.GetFieldOrder().Any(fid => def.GetField(fid).ParameterId == newParamId))
                return AddFieldResult.AlreadyPresent;

            // Locate the SOURCE field by its parameter id → its STABLE id + current index.
            ScheduleFieldId? srcFid = null;
            int srcIdx = -1;
            foreach (var fid in def.GetFieldOrder())
            {
                if (def.GetField(fid).ParameterId.Value == sourceParamId) { srcFid = fid; srcIdx = def.GetFieldIndex(fid); break; }
            }

            void SetHeading(ScheduleField f)
            {
                if (string.IsNullOrWhiteSpace(heading)) return;
                try { f.ColumnHeading = heading; } catch { /* heading is cosmetic; never fail over it */ }
            }

            // Source column not present here → just append the new column (nothing to replace).
            if (srcFid == null)
            {
                SetHeading(def.AddField(sf));
                return AddFieldResult.AppendedNoSource;
            }

            // Never orphan a sort/group/filter key: keep the source field, append the new one alongside.
            bool keyGuarded = SensitiveFieldParamIds(doc, sched.UniqueId).Contains((long)sourceParamId);
            if (keyGuarded)
            {
                SetHeading(def.AddField(sf));
                return AddFieldResult.AppendedKeyGuard;
            }

            // REPLACE: insert AT the source index, set the new heading, then remove the ORIGINAL by stable id.
            var newField = def.InsertField(sf, srcIdx);
            SetHeading(newField);
            try
            {
                def.RemoveField(srcFid!); // by STABLE ScheduleFieldId — new field settles at srcIdx, count unchanged
            }
            catch
            {
                // Partial failure → revert the insert so we don't leave a duplicate column.
                try { def.RemoveField(newField.FieldId); } catch { /* best effort */ }
                return AddFieldResult.Failed;
            }
            return AddFieldResult.Replaced;
        }
        catch { return AddFieldResult.Failed; }
    }

    /// <summary>Re-reads a just-written parameter to confirm the new value persisted (within unit tolerance).</summary>
    private static bool VerifyWrite(Parameter param, ProposedChange ch)
    {
        try
        {
            if (param.StorageType == StorageType.String)
                // Revit trims leading/trailing whitespace when storing a string param, so compare trimmed —
                // otherwise a legit write of e.g. " s" reads back as "s" and looks like a failed (unverified) write.
                return (param.AsString() ?? "").Trim() == (ch.NewString ?? "").Trim();
            if (param.StorageType == StorageType.Integer)
                return param.AsInteger() == ch.NewInt;
            if (param.StorageType == StorageType.Double)
                return Math.Abs(param.AsDouble() - ch.NewDouble) <= 1e-6;
            return true; // other storage types aren't written by Transom
        }
        catch { return false; }
    }

    /// <summary>True when a parameter already holds the value a change would write (within unit tolerance).
    /// Lets the bulk-instance path skip already-applied writes so a re-preview after an option-1 vary apply
    /// doesn't re-propose the change (string trimmed to match Revit's storage trim, doubles to 1e-9).</summary>
    private static bool AlreadyHas(Parameter p, bool isString, string str, bool isInt, int iv, double dbl)
    {
        try
        {
            if (p.StorageType == StorageType.String && isString)
                return (p.AsString() ?? "").Trim() == (str ?? "").Trim();
            if (p.StorageType == StorageType.Integer && isInt)
                return p.AsInteger() == iv;
            if (p.StorageType == StorageType.Double && !isString && !isInt)
                return Math.Abs(p.AsDouble() - dbl) <= 1e-9;
            return false;
        }
        catch { return false; }
    }

    // --- option-2 merge value helpers (§6) ---

    /// <summary>One INTERNAL parameter value carried through an option-2 merge — the stored value (As* getter),
    /// NOT display text. Mirrors the (IsString/IsInt/double) shape <see cref="SetValue"/>/<see cref="VerifyWrite"/>
    /// already use, so the same canonicalization governs the uniformity test and the write.</summary>
    private readonly struct ParsedVal
    {
        public readonly bool IsString;
        public readonly bool IsInt;
        public readonly string Str;
        public readonly int Int;
        public readonly double Dbl;
        private ParsedVal(bool isString, bool isInt, string str, int i, double d)
        { IsString = isString; IsInt = isInt; Str = str; Int = i; Dbl = d; }
        public static ParsedVal OfString(string s) => new(true, false, s ?? "", 0, 0);
        public static ParsedVal OfInt(int i) => new(false, true, "", i, 0);
        public static ParsedVal OfDouble(double d) => new(false, false, "", 0, d);
    }

    /// <summary>Reads an element's parameter as its INTERNAL stored value (never display text); negative ids are
    /// built-ins (via <see cref="GetParam"/>). A missing/odd parameter yields an empty-string value — the same
    /// "nothing there" the old column rendered, so carrying it forward is not data loss.</summary>
    private static ParsedVal ReadLive(Element el, int paramId)
    {
        try
        {
            var p = GetParam(el, paramId);
            if (p == null) return ParsedVal.OfString("");
            return p.StorageType switch
            {
                StorageType.String => ParsedVal.OfString(p.AsString() ?? ""),
                StorageType.Integer => ParsedVal.OfInt(p.AsInteger()),
                StorageType.Double => ParsedVal.OfDouble(p.AsDouble()),
                _ => ParsedVal.OfString(""),
            };
        }
        catch { return ParsedVal.OfString(""); }
    }

    /// <summary>A <see cref="ParsedVal"/> built from a change's already-parsed edit (string/int/double).</summary>
    private static ParsedVal EditVal(ProposedChange ch) =>
        ch.IsString ? ParsedVal.OfString(ch.NewString)
        : ch.IsInt ? ParsedVal.OfInt(ch.NewInt)
        : ParsedVal.OfDouble(ch.NewDouble);

    /// <summary>Canonical string of a <see cref="ParsedVal"/> — mirrors <see cref="CanonicalValue"/> (trimmed
    /// string / invariant int / double rounded to 1e-9) so the per-type uniformity test and the merge write
    /// agree on what "the same value" means.</summary>
    private static string CanonicalParsed(ParsedVal v) =>
        v.IsString ? (v.Str ?? "").Trim()
        : v.IsInt ? v.Int.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : Math.Round(v.Dbl, 9).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Writes a <see cref="ParsedVal"/> to a parameter (ParsedVal variant of <see cref="SetValue"/>).</summary>
    private static bool SetParsed(Parameter param, ParsedVal v) =>
        v.IsString ? param.Set(v.Str) : v.IsInt ? param.Set(v.Int) : param.Set(v.Dbl);

    /// <summary>Re-reads a parameter and confirms a <see cref="ParsedVal"/> persisted (ParsedVal variant of
    /// <see cref="VerifyWrite"/> — string trimmed to match Revit's storage trim, doubles to 1e-6).</summary>
    private static bool VerifyParsed(Parameter param, ParsedVal v)
    {
        try
        {
            if (param.StorageType == StorageType.String)
                return (param.AsString() ?? "").Trim() == (v.Str ?? "").Trim();
            if (param.StorageType == StorageType.Integer)
                return param.AsInteger() == v.Int;
            if (param.StorageType == StorageType.Double)
                return Math.Abs(param.AsDouble() - v.Dbl) <= 1e-6;
            return true;
        }
        catch { return false; }
    }

    // --- helpers ---

    private static ProposedChange InstanceChange(Element el, ImportColumn col, string oldDisp, string newDisp,
        bool isString, string str, double dbl) => new()
    {
        UniqueId = el.UniqueId, ParameterId = col.ParameterId, Binding = "instance", ElementName = SafeName(el),
        Field = col.FieldName, SourceHeading = col.Header, OldValue = oldDisp, NewValue = newDisp, IsString = isString, NewString = str, NewDouble = dbl,
    };

    /// <summary>An edited cell whose field can't be written by import (read-only / family- or type-driven). Shown greyed, never applied.</summary>
    private static ProposedChange FrozenChange(string elementName, ImportColumn col, string oldDisp, string attempted, string reason) => new()
    {
        ParameterId = col.ParameterId, Binding = "instance", ElementName = elementName,
        Field = col.FieldName, OldValue = oldDisp, NewValue = attempted, Frozen = true, FrozenReason = reason, Selected = false,
    };

    private static ProposedChange IntChange(Element el, ImportColumn col, string oldDisp, string newDisp, int iv) => new()
    {
        UniqueId = el.UniqueId, ParameterId = col.ParameterId, Binding = "instance", ElementName = SafeName(el),
        Field = col.FieldName, OldValue = oldDisp, NewValue = newDisp, IsInt = true, NewInt = iv,
    };

    /// <summary>Whether a parameter is a Yes/No (boolean) integer.</summary>
    private static bool IsYesNo(Parameter param)
    {
        try { return param.Definition.GetDataType() == SpecTypeId.Boolean.YesNo; }
        catch { return false; }
    }

    /// <summary>Parses an integer or Yes/No cell. Yes/No accepts Yes/No/Y/N/True/False/1/0 (blank = No).</summary>
    private static bool TryParseInteger(bool yesNo, string text, out int value)
    {
        value = 0;
        text = (text ?? "").Trim();
        if (yesNo)
        {
            switch (text.ToLowerInvariant())
            {
                case "yes": case "y": case "true": case "1": value = 1; return true;
                case "no": case "n": case "false": case "0": case "": value = 0; return true;
                default: return false;
            }
        }
        return int.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static ProposedChange TypeChange((long, int) key, TypeCandidate tc, string newDisp,
        bool isString, string str, double dbl) => new()
    {
        TypeId = key.Item1, ParameterId = key.Item2, Binding = "type", ElementName = tc.TypeName,
        Field = tc.Col.FieldName, OldValue = tc.CurDisplay, NewValue = newDisp, InstancesAffected = tc.Cells.Count,
        IsString = isString, NewString = str, NewDouble = dbl,
    };

    private static CellDiagnostic Diag(ImportSheet sheet, ImportRow row, ImportColumn col, string label,
        string severity, string reason, string value, string category = "advisory") => new()
    {
        SheetTabName = sheet.SheetTabName, ExcelRow = row.ExcelRow, Col = col.ExcelCol, FieldName = col.FieldName,
        ElementLabel = label, Severity = severity, Category = category, Reason = reason, Value = value,
    };

    /// <summary>
    ///     True (and records a skip + red diagnostic) when this cell is a non-top-left member of a merged region.
    ///     Such cells read blank, so importing them would wipe the parameter — we never write a merged cell.
    /// </summary>
    private static bool MergedSkip(ImportSheet sheet, ImportRow row, ImportColumn col, ChangeSet cs, string label, string cellText)
    {
        if (!sheet.MergedCells.Contains((row.ExcelRow, col.ExcelCol))) return false;
        cs.Skipped.Add(new SkippedItem
        {
            Reason = "merged cell",
            Detail = $"{col.FieldName} ({label}) — inside a merged region; not imported (un-merge to edit it)",
        });
        cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "merged cell — not imported", cellText));
        return true;
    }

    private static string CurrentDisplay(Parameter param) =>
        param.StorageType == StorageType.String ? (param.AsString() ?? "") : (param.AsValueString() ?? "");

    private static bool Drifted(Parameter param, string baseline, Units units, string? spec)
    {
        if (param.StorageType == StorageType.String)
            return (param.AsString() ?? "") != baseline;
        if (param.StorageType == StorageType.Double && spec != null)
        {
            if (UnitFormatUtils.TryParse(units, new ForgeTypeId(spec), baseline, out double b))
                return Math.Abs(param.AsDouble() - b) > 1e-9;
            return (param.AsValueString() ?? "") != baseline;
        }
        return false;
    }

    private static Parameter? GetParam(Element host, int parameterId)
    {
        if (parameterId < 0)
            return host.get_Parameter((BuiltInParameter)parameterId);
        foreach (Parameter p in host.Parameters)
            if (p.Id.Value == parameterId) return p;
        return null;
    }

    /// <summary>
    ///     Resolves where to write a parameter for this element against the LIVE model (not the binding frozen
    ///     at export, which can be stale or wrong). Honours the schedule field's classification when that host
    ///     carries the parameter — a window's Height/Width are type params even though the instance exposes a
    ///     read-only mirror — falling back to wherever the parameter actually lives (the multi-category case).
    /// </summary>
    private static string ResolveBindingLive(Document doc, Element instance, int parameterId, string scheduleBinding)
    {
        bool onInstance = GetParam(instance, parameterId) != null;
        bool onType = false;
        var tid = instance.GetTypeId();
        if (tid != ElementId.InvalidElementId)
        {
            var t = doc.GetElement(tid);
            onType = t != null && GetParam(t, parameterId) != null;
        }

        if (scheduleBinding == "type" && onType) return "type";
        if (scheduleBinding == "instance" && onInstance) return "instance";
        if (onInstance) return "instance";
        if (onType) return "type";
        return "none";
    }

    private static string SafeName(Element e)
    {
        try { return e.Name; }
        catch { return e.Id.ToString(); }
    }

    /// <summary>Whether an element is a member of a Revit group, and the group's name. Group membership only
    /// constrains <em>some</em> instance writes (project/shared need "vary"; geometry built-ins need Edit Group) —
    /// built-in DATA params still write directly. <see cref="Mark"/> picks the right path per parameter.</summary>
    private static (bool inGroup, string name) GroupInfo(Document doc, Element e)
    {
        var gid = e.GroupId;
        if (gid == null || gid == ElementId.InvalidElementId) return (false, "");
        var g = doc.GetElement(gid);
        return (true, g != null ? SafeName(g) : "Group");
    }

    private static ProposedChange Mark(Document doc, ProposedChange ch, bool inGroup, string groupName)
    {
        ch.InGroup = inGroup;
        ch.GroupName = groupName;
        // How a grouped edit gets written durably:
        //   • Project/shared params (positive id) → ProjectVary: set the "vary by group instance" flag, write directly.
        //   • IDENTITY built-ins Revit lets differ between group instances (Mark, Number — see
        //     ScheduleReader.IsInstanceIdentityBuiltin) → None: a direct write COMMITS on a grouped instance
        //     (live-verified), exactly like an ungrouped one.
        //   • Every OTHER grouped built-in (negative id) → BuiltinDance (option 2b / Claude-Assist): Revit refuses a
        //     direct write inside a group ("group edit mode") and these can't vary — true for both geometry built-ins
        //     (Sill/Head Height, which 2b can't fix either → Claude-Assist) AND ordinary data built-ins like Comments.
        //     (Routing Comments to None — the earlier over-generalization — made its grouped writes roll back.)
        if (!inGroup) ch.GroupMode = GroupMode.None;
        else if (ch.ParameterId >= 0) ch.GroupMode = GroupMode.ProjectVary;
        else if (ScheduleReader.IsInstanceIdentityBuiltin(ch.ParameterId)) ch.GroupMode = GroupMode.None;
        else ch.GroupMode = GroupMode.BuiltinDance;
        return ch;
    }
}
