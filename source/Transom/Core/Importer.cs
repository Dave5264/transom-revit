using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace Transom.Core;

/// <summary>Outcome of applying one change, stamped after the apply + post-apply verification passes. Drives the
/// run-results report: bold = Applied, italic + red = Failed/Unverified.</summary>
public enum ApplyOutcome { Pending, Applied, Failed, Unverified, Skipped }

public sealed class ProposedChange
{
    public string UniqueId = "";
    public long TypeId;
    public int ParameterId;
    public string Binding = "instance";

    /// <summary>When set, this is a bulk instance write: NewValue is applied to each of these instance UniqueIds.</summary>
    public List<string>? BulkInstanceIds;

    /// <summary>True when this change targets element(s) inside a Revit group (can't be written directly).</summary>
    public bool InGroup;
    public string GroupName = "";

    /// <summary>True when this change's model group is BROKEN — the definition-swap dance can't reproduce it
    /// (a member anchored outside the group, a nested group, or mixed instance orientation). Such columns drop
    /// the dance (option 3) and route to option 2 / Claude-Assist, and color RED. Set by <c>ComputeGroupBroken</c>.</summary>
    public bool GroupBroken;
    public string GroupBrokenReason = "";

    /// <summary>How a grouped edit gets written durably. ProjectVary = Transom applies in-process (vary flag);
    /// BuiltinDance = staged for Claude-assist (definition swap). Set only when <see cref="InGroup"/> is true.
    /// Project/shared params (positive id) vary; built-ins (negative id) can't and need the dance.</summary>
    public GroupMode GroupMode = GroupMode.None;

    /// <summary>Project/shared parameters carry positive ids; built-in parameters are negative.</summary>
    public static GroupMode ModeFor(int parameterId) => parameterId >= 0 ? GroupMode.ProjectVary : GroupMode.BuiltinDance;

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
    public bool Selectable => !Frozen;

    public bool Selected { get; set; } = true;
    public string ElementName { get; set; } = "";
    public string Field { get; set; } = "";
    /// <summary>The source schedule column's DISPLAY heading (ScheduleField.ColumnHeading), when known.
    /// Used by option 2 to give the new column a clean visible title instead of the verbose param name.
    /// Empty falls back to Field.</summary>
    public string SourceHeading { get; set; } = "";
    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";
    public int InstancesAffected { get; set; } = 1;
    /// <summary>Preview "Scope" cell. Group-conflicted (blue project / yellow built-in) rows say "choose on
    /// Apply" so the preview is consistent with the per-parameter resolution dialog they'll trigger — they
    /// are NOT applied silently.</summary>
    public string Scope
    {
        get
        {
            if (InGroup)
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
}

public sealed class ConflictOption
{
    public string Display = "";
    public bool IsString;
    public string NewString = "";
    public double NewDouble;
    public bool Parseable = true;
}

public sealed class TypeConflict
{
    public long TypeId;
    public int ParameterId;
    public string Field = "";
    public string TypeName = "";
    public string CurrentDisplay = "";
    public int InstancesAffected;
    public List<ConflictOption> Options = new();
}

/// <summary>A flagged cell for the colour-coded import report. Severity: "red" (can't write) or "yellow" (changed since export).</summary>
public sealed class CellDiagnostic
{
    public string SheetTabName = "";
    public int ExcelRow;
    public int Col;
    public string FieldName = "";
    public string ElementLabel = "";
    public string Severity = "";   // red | yellow
    public string Reason = "";
    public string Value = "";
}

/// <summary>Per-schedule (sheet) tally shown on Preview: how many changes/skips this import would make to each schedule.</summary>
public sealed class SheetSummary
{
    public string ScheduleName = "";
    public int Changes;
    public int Skipped;
    public bool RoundTrippable = true;

    // Bound in XAML (WPF binding ignores public fields, so expose a property).
    public string Display => $"{ScheduleName} — {Changes} change(s)" + (Skipped > 0 ? $", {Skipped} skipped" : "");
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

    /// <summary>The (ParameterId, Field) key used to identify one resolvable group-conflict column.</summary>
    public static string ColumnKey(int parameterId, string field) => parameterId + "|" + field;

    /// <summary>Names of the schedules in this import (workbook sheets). Used by option 2 to add the new
    /// type-parameter field to the affected schedules.</summary>
    public List<string> ImportedScheduleNames = new();

    /// <summary>Full plain-text diagnostic of this import (column matching, anchors, skips, results) for the Copy log button.</summary>
    public string DiagnosticLog = "";
}

/// <summary>
///     Diffs an imported workbook against the live model (read-only) into a change set + diagnostics, and
///     applies a confirmed change set in one transaction. Uses a three-way compare (exported baseline vs
///     current model vs spreadsheet): only cells you actually edited (spreadsheet ≠ baseline) become writes;
///     model drift (current ≠ baseline) is flagged "changed since export"; unwritable cells are flagged red.
/// </summary>
public sealed class Importer
{
    private sealed class TypeCandidate
    {
        public ImportColumn Col = null!;
        public string SheetTab = "";
        public string TypeName = "";
        public bool IsString;
        public string? SpecTypeId;
        public string CurString = "";
        public double CurDouble;
        public string CurDisplay = "";
        public readonly List<(int excelRow, string value, string label)> Cells = new();
    }

    public ChangeSet BuildChangeSet(Document doc, ImportWorkbook wb)
    {
        var cs = new ChangeSet { CrossModel = wb.SourceModelGuid != doc.CreationGUID.ToString() };
        var units = doc.GetUnits();

        foreach (var sheet in wb.Sheets)
        {
            cs.ScheduleName = sheet.ScheduleName;
            int chg0 = cs.Changes.Count, skp0 = cs.Skipped.Count;
            void Summarize() => cs.SheetSummaries.Add(new SheetSummary
            {
                ScheduleName = sheet.ScheduleName, RoundTrippable = sheet.RoundTrippable,
                Changes = cs.Changes.Count - chg0, Skipped = cs.Skipped.Count - skp0,
            });

            if (!sheet.RoundTrippable)
            {
                cs.Skipped.Add(new SkippedItem
                {
                    Reason = "display-only schedule",
                    Detail = $"{sheet.ScheduleName} — not itemized, so rows don't map to individual elements and " +
                             "values can't be written back. Turn on 'Itemize every instance' (the schedule's " +
                             "Sorting/Grouping tab) and re-export to round-trip.",
                });
                Summarize();
                continue;
            }

            foreach (var unmatched in sheet.Columns.Where(c => c.Writable && !c.Matched))
                cs.Skipped.Add(new SkippedItem
                {
                    Reason = "column not in spreadsheet",
                    Detail = $"'{(string.IsNullOrEmpty(unmatched.Header) ? unmatched.FieldName : unmatched.Header)}' (renamed or removed)",
                });

            // A duplicated anchor (a row copied in Excel) would write one element twice — all copies were dropped.
            foreach (var dupUid in sheet.DuplicateUids)
                cs.Skipped.Add(new SkippedItem
                {
                    Reason = "duplicate row",
                    Detail = $"anchor …{(dupUid.Length > 6 ? dupUid.Substring(dupUid.Length - 6) : dupUid)} appears on " +
                             "more than one row — all copies skipped so the element isn't written twice",
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
                    sheet.Baseline.TryGetValue(row.UniqueId, out var baseRowT);
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

                sheet.Baseline.TryGetValue(row.UniqueId, out var baseRow);
                var (elInGroup, elGroupName) = GroupInfo(doc, el);

                foreach (var col in sheet.Columns)
                {
                    if (!col.Writable || !col.Matched || col.ExcelCol >= row.Cells.Length) continue;
                    var cellText = row.Cells[col.ExcelCol] ?? "";
                    if (MergedSkip(sheet, row, col, cs, label, cellText)) continue;
                    var baseline = baseRow != null && baseRow.TryGetValue(col.Col, out var bv) ? bv : null;

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

                    if (param.IsReadOnly)
                    {
                        if (edited)
                        {
                            cs.Changes.Add(FrozenChange(SafeName(el), col, current, cellText, "read-only — driven by the family or type"));
                            cs.Diagnostics.Add(Diag(sheet, row, col, label, "blue", "frozen — read-only (family/type driven)", cellText));
                        }
                        continue;
                    }

                    // Drift: current model value differs from what was exported (only knowable with a baseline).
                    if (baseline != null && Drifted(param, baseline, units, col.SpecTypeId))
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "yellow",
                            $"changed since export (was '{baseline}', now '{current}')", cellText));

                    if (!edited) continue; // not edited -> no write (never revert model drift)

                    if (param.StorageType == StorageType.String)
                    {
                        if (binding == "type")
                            RecordType(typeGroups, sheet, col, host!, param, label, row.ExcelRow, cellText);
                        else if ((param.AsString() ?? "") != cellText)
                            cs.Changes.Add(Mark(InstanceChange(el, col, param.AsString() ?? "", cellText, true, cellText, 0), elInGroup, elGroupName));
                    }
                    else if (param.StorageType == StorageType.Integer && binding == "instance")
                    {
                        if (!TryParseInteger(IsYesNo(param), cellText, out int iv))
                        {
                            cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{col.FieldName} = '{cellText}'" });
                            cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "value can't be parsed", cellText));
                        }
                        else if (param.AsInteger() != iv)
                            cs.Changes.Add(Mark(IntChange(el, col, current, cellText, iv), elInGroup, elGroupName));
                    }
                    else if (param.StorageType == StorageType.Double && col.SpecTypeId != null)
                    {
                        if (!UnitFormatUtils.TryParse(units, new ForgeTypeId(col.SpecTypeId), cellText, out double parsed))
                        {
                            cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{col.FieldName} = '{cellText}'" });
                            cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "value can't be parsed", cellText));
                            continue;
                        }
                        if (binding == "type")
                            RecordType(typeGroups, sheet, col, host!, param, label, row.ExcelRow, cellText);
                        else if (Math.Abs(param.AsDouble() - parsed) >= 1e-9)
                        {
                            // Always check the entered value is in the schedule's unit format; if not, ask to confirm.
                            var canonical = ExcelCorrector.Canonical(units, new ForgeTypeId(col.SpecTypeId), parsed, cellText);
                            if (!ExcelCorrector.SameFormat(cellText, canonical))
                                cs.Reformats.Add(Reformat(sheet, row, col, label, cellText, canonical));
                            else
                                cs.Changes.Add(Mark(InstanceChange(el, col, param.AsValueString() ?? "", cellText, false, "", parsed), elInGroup, elGroupName));
                        }
                    }
                    else
                    {
                        // Non-text, non-numeric (e.g. an ElementId / family-or-type selection) — can't be set from a cell.
                        cs.Changes.Add(FrozenChange(SafeName(el), col, current, cellText, "set by a family/type selection, not by text"));
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "blue", "frozen — set by family/type (can't import)", cellText));
                    }
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
        ComputeGroupBroken(doc, cs);

        cs.DiagnosticLog = BuildDiagnosticLog(doc, wb, cs);
        return cs;
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
    ///     Flags every grouped change whose model group is BROKEN — one the definition-swap dance cannot
    ///     faithfully reproduce (a member anchored outside the group, a nested model group, or instances at
    ///     mixed orientations; see <see cref="GroupSafety"/>). Broken columns drop option 3 (dance) and route
    ///     to option 2 / Claude-Assist, and color RED on export. Cached per group TYPE across the change set.
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
                if (s.IsBroken) { ch.GroupBroken = true; ch.GroupBrokenReason = s.Explain(); break; }
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

    /// <summary>
    ///     Builds a copyable plain-text diagnostic: per-sheet column matching + anchor resolution, then the
    ///     change/skip/conflict/diagnostic tallies. Surfaces the common failure modes (no header row, renamed
    ///     columns, missing anchor, cross-model) in words, so a failed import can be diagnosed from the log alone.
    /// </summary>
    private static string BuildDiagnosticLog(Document doc, ImportWorkbook wb, ChangeSet cs)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Transom import diagnostic — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("Workbook: " + wb.Path);
        sb.AppendLine($"Source model GUID: {wb.SourceModelGuid}  |  current doc: {doc.CreationGUID}"
                      + (cs.CrossModel ? "  ⚠ CROSS-MODEL (matched by name)" : ""));
        sb.AppendLine($"Sheets: {wb.Sheets.Count}");

        foreach (var sheet in wb.Sheets)
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

        sb.AppendLine();
        sb.AppendLine("== Result ==");
        sb.AppendLine($"  changes proposed: {cs.Changes.Count} (frozen: {cs.Changes.Count(c => c.Frozen)})");
        sb.AppendLine($"  conflicts: {cs.Conflicts.Count}");
        int red = cs.Diagnostics.Count(d => d.Severity == "red");
        int yellow = cs.Diagnostics.Count(d => d.Severity == "yellow");
        sb.AppendLine($"  cell diagnostics: {cs.Diagnostics.Count} ({red} red / {yellow} yellow)");
        sb.AppendLine($"  skipped: {cs.Skipped.Count}");
        foreach (var s in cs.Skipped)
            sb.AppendLine($"    - [{s.Reason}] {s.Detail}");

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
                if (param.IsReadOnly)
                {
                    if (edited)
                    {
                        cs.Changes.Add(FrozenChange(SafeName(typeEl!), col, current, cellText, "read-only — driven by the family or type"));
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "blue", "frozen — read-only (family/type driven)", cellText));
                    }
                    continue;
                }
                if (baseline != null && Drifted(param, baseline, units, col.SpecTypeId))
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "yellow",
                        $"changed since export (was '{baseline}', now '{current}')", cellText));
                if (!edited) continue;

                if (param.StorageType == StorageType.String)
                    RecordTypeAll(col, cellText);
                else if (param.StorageType == StorageType.Double && col.SpecTypeId != null)
                {
                    if (!UnitFormatUtils.TryParse(units, new ForgeTypeId(col.SpecTypeId), cellText, out _))
                    {
                        cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{col.FieldName} = '{cellText}'" });
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "red", "value can't be parsed", cellText));
                        continue;
                    }
                    RecordTypeAll(col, cellText);
                }
                else
                {
                    cs.Changes.Add(FrozenChange(SafeName(typeEl!), col, current, cellText, "set by a family/type selection, not by text"));
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "blue", "frozen — set by family/type (can't import)", cellText));
                }
            }
            else if (binding == "instance")
            {
                bool edited = baseline != null ? cellText != baseline : !string.IsNullOrEmpty(cellText);
                if (!edited) continue;

                if (row.InstanceIds == null || row.InstanceIds.Count == 0)
                {
                    cs.Skipped.Add(new SkippedItem { Reason = "ambiguous instance scope", Detail = $"{col.FieldName} ({label}) — type spans multiple rows" });
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "blue", "skipped — instance scope ambiguous (type spans rows)", cellText));
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
                if (rparam.IsReadOnly)
                {
                    cs.Changes.Add(FrozenChange(SafeName(nameEl), col, "(varies)", cellText, "read-only — driven by the family or type"));
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "blue", "frozen — read-only (family/type driven)", cellText));
                    continue;
                }

                var oldDisp = string.IsNullOrEmpty(baseline) ? "(varies)" : baseline;
                bool isString = false, isInt = false;
                string str = "";
                double dbl = 0;
                int iv = 0;
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
                    if (!ExcelCorrector.SameFormat(cellText, canonical))
                    {
                        cs.Reformats.Add(Reformat(sheet, row, col, label, cellText, canonical));
                        continue;
                    }
                    dbl = parsed;
                }
                else
                {
                    cs.Changes.Add(FrozenChange(SafeName(nameEl), col, oldDisp, cellText, "set by a family/type selection, not by text"));
                    cs.Diagnostics.Add(Diag(sheet, row, col, label, "blue", "frozen — set by family/type (can't import)", cellText));
                    continue;
                }

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
                // Every targeted instance already matches -> nothing to write. Surface it as drift so the
                // user still sees the cell "changed since export", consistent with the normal/type paths.
                if (pending.Count == 0)
                {
                    if (already > 0)
                        cs.Diagnostics.Add(Diag(sheet, row, col, label, "yellow",
                            $"changed since export (model already set to '{cellText}')", cellText));
                    continue;
                }

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
                    cs.Changes.Add(BulkChange(nameEl, col, ungrouped, oldDisp, cellText, isString, str, dbl, isInt, iv));
                if (grouped.Count > 0)
                    cs.Changes.Add(Mark(BulkChange(nameEl, col, grouped, oldDisp, cellText, isString, str, dbl, isInt, iv), true, gName));
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

        sheet.Baseline.TryGetValue(row.UniqueId, out var baseRow);
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
        if (rparam.IsReadOnly)
        {
            cs.Skipped.Add(new SkippedItem { Reason = "read-only", Detail = $"{ghe.FieldName} ({label})" });
            return;
        }

        var oldDisp = string.IsNullOrEmpty(baseline) ? CurrentDisplay(rparam) : baseline;
        bool isString = false, isInt = false; string str = ""; double dbl = 0; int iv = 0;
        if (rparam.StorageType == StorageType.String) { isString = true; str = cellText; }
        else if (rparam.StorageType == StorageType.Integer)
        {
            isInt = true;
            if (!TryParseInteger(IsYesNo(rparam), cellText, out iv))
            {
                cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{ghe.FieldName} = '{cellText}'" });
                return;
            }
        }
        else if (rparam.StorageType == StorageType.Double && ghe.SpecTypeId != null)
        {
            if (!UnitFormatUtils.TryParse(units, new ForgeTypeId(ghe.SpecTypeId), cellText, out double parsed))
            {
                cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{ghe.FieldName} = '{cellText}'" });
                return;
            }
            dbl = parsed;
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
        ProposedChange MakeGh(List<string> bulk) => new()
        {
            ParameterId = ghe.ParameterId, Binding = "instance", BulkInstanceIds = bulk,
            ElementName = label, Field = ghe.FieldName, OldValue = oldDisp, NewValue = cellText,
            InstancesAffected = bulk.Count, IsString = isString, NewString = str, NewDouble = dbl, IsInt = isInt, NewInt = iv,
        };
        if (ghUngrouped.Count > 0) cs.Changes.Add(MakeGh(ghUngrouped));
        if (ghGrouped.Count > 0) cs.Changes.Add(Mark(MakeGh(ghGrouped), true, ghGroupName));
    }

    private static ProposedChange BulkChange(Element typeEl, ImportColumn col, List<string> instanceIds,
        string oldDisp, string newDisp, bool isString, string str, double dbl, bool isInt, int iv) => new()
    {
        ParameterId = col.ParameterId, Binding = "instance", BulkInstanceIds = instanceIds,
        ElementName = SafeName(typeEl), Field = col.FieldName, SourceHeading = col.Header, OldValue = oldDisp, NewValue = newDisp,
        InstancesAffected = instanceIds.Count, IsString = isString, NewString = str, NewDouble = dbl, IsInt = isInt, NewInt = iv,
    };

    private static ReformatSuggestion Reformat(ImportSheet sheet, ImportRow row, ImportColumn col, string label, string entered, string canonical) => new()
    {
        SheetTabName = sheet.SheetTabName, ExcelRow = row.ExcelRow, ExcelCol = col.ExcelCol,
        FieldName = col.FieldName, ElementLabel = label, Entered = entered, Canonical = canonical,
    };

    private static void RecordType(Dictionary<(long, int), TypeCandidate> typeGroups, ImportSheet sheet,
        ImportColumn col, Element host, Parameter param, string label, int excelRow, string cellText)
    {
        var key = (host.Id.Value, col.ParameterId);
        if (!typeGroups.TryGetValue(key, out var tc))
        {
            tc = new TypeCandidate { Col = col, SheetTab = sheet.SheetTabName, TypeName = SafeName(host), SpecTypeId = col.SpecTypeId };
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
        tc.Cells.Add((excelRow, cellText, label));
    }

    private static void ResolveTypeGroups(ChangeSet cs, Dictionary<(long, int), TypeCandidate> typeGroups, Units units,
        Dictionary<long, int> typeCounts)
    {
        foreach (var kv in typeGroups)
        {
            var tc = kv.Value;
            // #1: a type edit changes the TYPE, so it affects EVERY instance of that type — report the real
            // instance count, not the number of schedule rows touched (~1). Falls back to cell count if unknown.
            int affects = typeCounts.TryGetValue(kv.Key.Item1, out var instCount) ? instCount : tc.Cells.Count;
            var distinct = tc.Cells.Select(c => c.value).Distinct().ToList();
            if (distinct.Count > 1)
            {
                var conflict = new TypeConflict
                {
                    TypeId = kv.Key.Item1,
                    ParameterId = kv.Key.Item2,
                    Field = tc.Col.FieldName,
                    TypeName = tc.TypeName,
                    CurrentDisplay = tc.CurDisplay,
                    InstancesAffected = affects,
                };
                foreach (var v in distinct)
                {
                    var opt = new ConflictOption { Display = v, IsString = tc.IsString, NewString = v };
                    if (!tc.IsString)
                    {
                        opt.Parseable = UnitFormatUtils.TryParse(units, new ForgeTypeId(tc.SpecTypeId!), v, out double d);
                        opt.NewDouble = opt.Parseable ? d : 0;
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

            var value = distinct[0];
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
                    cs.Skipped.Add(new SkippedItem { Reason = "unparseable", Detail = $"{tc.Col.FieldName} = '{value}'" });
                    continue;
                }
                if (Math.Abs(parsed - tc.CurDouble) < 1e-9) continue;
                // #2: show the canonical formatted value (e.g. 1'-0") as New, not the raw cell text ("1"), so the
                // preview reveals how a unit-less entry was interpreted instead of silently writing it.
                var canonical = ExcelCorrector.Canonical(units, new ForgeTypeId(tc.SpecTypeId!), parsed, value);
                var chD = TypeChange(kv.Key, tc, canonical, false, "", parsed);
                chD.InstancesAffected = affects;
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

    public static ProposedChange ResolveToChange(TypeConflict c, ConflictOption opt) => new()
    {
        TypeId = c.TypeId, ParameterId = c.ParameterId, Binding = "type", ElementName = c.TypeName,
        Field = c.Field, OldValue = c.CurrentDisplay, NewValue = opt.Display, InstancesAffected = c.InstancesAffected,
        IsString = opt.IsString, NewString = opt.NewString, NewDouble = opt.NewDouble,
    };

    /// <summary>Collects (and quiets) Revit's own warnings during the apply commit, so they land in the log
    /// instead of blocking on a dialog. Warnings are deleted (commit proceeds); errors are logged as-is.</summary>
    private sealed class ApplyFailureCollector : IFailuresPreprocessor
    {
        public readonly List<string> Messages = new();
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            foreach (var f in a.GetFailureMessages())
            {
                var sev = f.GetSeverity();
                string desc;
                try { desc = f.GetDescriptionText(); } catch { desc = "(failure)"; }
                int n = 0; try { n = f.GetFailingElementIds().Count; } catch { /* ignore */ }
                Messages.Add($"[{sev}] {desc}" + (n > 0 ? $" — {n} element(s)" : ""));
                if (sev == FailureSeverity.Warning) { try { a.DeleteWarning(f); } catch { /* ignore */ } }
            }
            return FailureProcessingResult.Continue;
        }
    }

    public string Apply(Document doc, ChangeSet cs)
    {
        int applied = 0;
        var failed = new List<string>();
        var unverified = new List<string>();
        var collector = new ApplyFailureCollector();
        string newParamNote = "";

        using var tx = new Transaction(doc, "Transom: import edits");
        tx.Start();
        var fho = tx.GetFailureHandlingOptions();
        fho.SetFailuresPreprocessor(collector);   // capture Revit warnings/errors into the log
        fho.SetClearAfterRollback(true);
        tx.SetFailureHandlingOptions(fho);

        try
        {
            // Resolution option 2 (new type parameter) is handled in one bulk pass — create the param, write
            // per-type values, add it to the affected schedules — not by the per-change write loop below.
            var newParamChanges = cs.Changes.Where(c => c.Resolution == GroupResolution.NewTypeParam && !c.Frozen).ToList();
            if (newParamChanges.Count > 0)
                newParamNote = ApplyNewTypeParam(doc, newParamChanges, cs.ImportedScheduleNames, failed);

            foreach (var ch in cs.Changes)
            {
                if (ch.Frozen) continue; // can't be written — shown greyed in the preview only
                if (ch.Resolution == GroupResolution.NewTypeParam) continue; // handled in the bulk pass above
                if (ch.Resolution == GroupResolution.GroupDance) continue;   // handled by GroupDanceApplier AFTER this transaction
                if (ch.GroupMode == GroupMode.BuiltinDance) continue; // built-in group param — staged for Claude-assist, not applied here

                // Bulk instance write (grouped schedule): apply to every instance the row represented.
                if (ch.BulkInstanceIds != null)
                {
                    bool anyFail = false, anyUnver = false;
                    foreach (var uid in ch.BulkInstanceIds)
                    {
                        var inst = doc.GetElement(uid);
                        var ip = inst == null ? null : GetParam(inst, ch.ParameterId);
                        if (ip == null || ip.IsReadOnly) { failed.Add(Label(ch)); anyFail = true; continue; }
                        // Grouped project param: allow it to vary per instance, then write each instance directly.
                        if (ch.GroupMode == GroupMode.ProjectVary && !EnsureVary(ip, doc)) { failed.Add(Label(ch) + " — can't vary by group instance"); anyFail = true; continue; }
                        if (!SetValue(ip, ch)) { failed.Add(Label(ch)); anyFail = true; continue; }
                        if (!VerifyWrite(ip, ch)) { unverified.Add(Label(ch)); anyUnver = true; continue; }
                        applied++;
                    }
                    // Any failed instance fails the whole cell; else any unverified; else applied.
                    ch.Outcome = anyFail ? ApplyOutcome.Failed : anyUnver ? ApplyOutcome.Unverified : ApplyOutcome.Applied;
                    continue;
                }

                var host = ch.Binding == "type" ? doc.GetElement(new ElementId(ch.TypeId)) : doc.GetElement(ch.UniqueId);
                var param = host == null ? null : GetParam(host, ch.ParameterId);
                if (param == null || param.IsReadOnly) { failed.Add(Label(ch)); ch.Outcome = ApplyOutcome.Failed; continue; }
                if (ch.GroupMode == GroupMode.ProjectVary && !EnsureVary(param, doc)) { failed.Add(Label(ch) + " — can't vary by group instance"); ch.Outcome = ApplyOutcome.Failed; continue; }
                if (!SetValue(param, ch)) { failed.Add(Label(ch)); ch.Outcome = ApplyOutcome.Failed; continue; }

                // H3: re-read the parameter and confirm the value actually landed (a Set can return
                // true yet be coerced/clamped, or silently no-op on some derived parameters).
                if (!VerifyWrite(param, ch)) { unverified.Add(Label(ch)); ch.Outcome = ApplyOutcome.Unverified; continue; }
                applied++; ch.Outcome = ApplyOutcome.Applied;
            }
            var commitStatus = tx.Commit();
            if (commitStatus != TransactionStatus.Committed)
            {
                // Revit discarded the ENTIRE transaction — Commit() returns RolledBack WITHOUT throwing, so
                // this is otherwise invisible. It happens when unresolved ERROR-severity failures can't be
                // committed (ApplyFailureCollector only auto-deletes warnings). Nothing persisted, so report
                // the rollback honestly instead of the in-transaction "applied" tally — which would otherwise
                // read as a success the model never actually received.
                // Nothing persisted: every change that thought it applied actually failed.
                foreach (var c in cs.Changes)
                    if (c.Outcome is ApplyOutcome.Applied or ApplyOutcome.Unverified) c.Outcome = ApplyOutcome.Failed;
                int errCount = collector.Messages.Count(m => m.StartsWith("[Error]"));
                cs.DiagnosticLog = BuildApplyLog(applied, failed, unverified, collector.Messages, cs, rolledBack: true);
                return $"Apply ROLLED BACK by Revit — 0 of {applied} attempted change(s) were saved" +
                       (errCount > 0 ? $"; {errCount} error(s) blocked the commit (see log)." : " (see log).");
            }
        }
        catch (Exception ex)
        {
            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            cs.DiagnosticLog = "Transom apply — FAILED (rolled back): " + ex.Message;
            return "Apply failed (rolled back): " + ex.Message;
        }

        var msg = $"Applied {applied} change(s)";
        if (failed.Count > 0) msg += $", {failed.Count} failed";
        if (unverified.Count > 0) msg += $", {unverified.Count} unverified (value didn't take)";
        if (collector.Messages.Count > 0) msg += $"  —  {collector.Messages.Count} Revit warning(s) (see log)";
        if (newParamNote.Length > 0) msg += $"  —  {newParamNote}";
        msg += $". {cs.Skipped.Count} skipped.";

        cs.DiagnosticLog = BuildApplyLog(applied, failed, unverified, collector.Messages, cs);
        return msg;
    }

    /// <summary>
    ///     Post-apply (post-commit) verification: re-reads every applied change's target parameter from the model
    ///     and compares it to the change's parsed value. Catches silent failures that only surface after the
    ///     transaction commits (e.g. a value Revit clamps, or a group-phase write the dance couldn't reproduce).
    ///     A mismatch stamps <see cref="ApplyOutcome.Failed"/>; a match on a still-<c>Pending</c> change stamps
    ///     <see cref="ApplyOutcome.Applied"/>. Option-2 (NewTypeParam) changes are skipped — they store the value
    ///     in a DIFFERENT parameter, so the original target won't reflect it; their stamp is trusted as-is.
    /// </summary>
    public void VerifyApplied(Document doc, ChangeSet cs)
    {
        foreach (var ch in cs.Changes)
        {
            if (ch.Frozen) continue;
            if (ch.Resolution == GroupResolution.NewTypeParam) continue; // value lives in a different param — trust its stamp

            bool matches;
            if (ch.BulkInstanceIds != null)
            {
                // Bulk write: every instance must currently hold the value, else the cell failed.
                matches = true;
                foreach (var uid in ch.BulkInstanceIds)
                {
                    var inst = doc.GetElement(uid);
                    var ip = inst == null ? null : GetParam(inst, ch.ParameterId);
                    if (ip == null || !VerifyWrite(ip, ch)) { matches = false; break; }
                }
            }
            else
            {
                var host = ch.Binding == "type" ? doc.GetElement(new ElementId(ch.TypeId)) : doc.GetElement(ch.UniqueId);
                var param = host == null ? null : GetParam(host, ch.ParameterId);
                matches = param != null && VerifyWrite(param, ch);
            }

            if (!matches) ch.Outcome = ApplyOutcome.Failed;
            else if (ch.Outcome == ApplyOutcome.Pending) ch.Outcome = ApplyOutcome.Applied;
        }
    }

    private static string Label(ProposedChange ch) =>
        $"{(string.IsNullOrEmpty(ch.ElementName) ? "?" : ch.ElementName)} · {ch.Field}: '{ch.OldValue}' -> '{ch.NewValue}'";

    /// <summary>Full plain-text record of an apply for the Copy-log button: counts, each failed/unverified write,
    /// and every Revit warning/error raised during the commit.</summary>
    private static string BuildApplyLog(int applied, List<string> failed, List<string> unverified,
        List<string> revitMessages, ChangeSet cs, bool rolledBack = false)
    {
        const int cap = 200;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Transom apply — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        if (rolledBack)
            sb.AppendLine("*** ROLLED BACK BY REVIT — NOTHING PERSISTED. The 'attempted' count below is what was " +
                          "written in-transaction before Revit discarded the commit (usually unresolved errors). ***");
        sb.AppendLine($"{(rolledBack ? "attempted (NOT saved)" : "applied")}: {applied}");
        sb.AppendLine($"skipped (from preview): {cs.Skipped.Count}");

        sb.AppendLine($"\n== Revit warnings / errors during apply: {revitMessages.Count} ==");
        foreach (var m in revitMessages.Take(cap)) sb.AppendLine("  - " + m);
        if (revitMessages.Count > cap) sb.AppendLine($"  … +{revitMessages.Count - cap} more");

        sb.AppendLine($"\n== failed writes: {failed.Count} ==");
        foreach (var f in failed.Take(cap)) sb.AppendLine("  - " + f);
        if (failed.Count > cap) sb.AppendLine($"  … +{failed.Count - cap} more");

        sb.AppendLine($"\n== unverified (Set returned but value didn't take): {unverified.Count} ==");
        foreach (var u in unverified.Take(cap)) sb.AppendLine("  - " + u);
        if (unverified.Count > cap) sb.AppendLine($"  … +{unverified.Count - cap} more");

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
    ///     Resolution option 2: move each group-conflicted column's values out of the constrained parameter
    ///     into a NEW shared TYPE parameter, then add that parameter to the affected schedules. Type params
    ///     hold one value per type, so this is only offered when the column's values are consistent per type
    ///     (gated by <see cref="ComputeOption2Eligibility"/>). Runs inside the caller's transaction.
    ///     Returns (values written, status note).
    /// </summary>
    private string ApplyNewTypeParam(Document doc, List<ProposedChange> changes,
        List<string> scheduleNames, List<string> failed)
    {
        var app = doc.Application;
        int created = 0, written = 0, fields = 0;

        // One new param per resolvable column — keyed (ParameterId, Field), matching the picker/eligibility.
        foreach (var pg in changes.GroupBy(c => ChangeSet.ColumnKey(c.ParameterId, c.Field)))
        {
            var list = pg.ToList();
            var sample = list[0];

            // Source parameter (for storage/spec), the affected TYPE categories, and one change per type.
            Parameter? src = null;
            var cats = app.Create.NewCategorySet();
            var perType = new Dictionary<long, ProposedChange>();
            foreach (var ch in list)
            {
                var uid = ch.BulkInstanceIds is { Count: > 0 } ? ch.BulkInstanceIds[0] : ch.UniqueId;
                var el = string.IsNullOrEmpty(uid) ? null : doc.GetElement(uid);
                if (el == null) continue;
                src ??= GetParam(el, ch.ParameterId);
                // A TYPE binding must use the TYPE's category (usually == the instance's, but resolve it properly).
                var tid0 = el.GetTypeId();
                var typeEl0 = tid0 != null && tid0 != ElementId.InvalidElementId ? doc.GetElement(tid0) : null;
                var cat = (typeEl0 ?? el).Category;
                if (cat is { AllowsBoundParameters: true }) cats.Insert(cat);
                if (tid0 != null && tid0 != ElementId.InvalidElementId) perType[tid0.Value] = ch;
            }
            if (src == null || cats.IsEmpty || perType.Count == 0)
            { failed.Add($"{sample.Field} (new type param) — no writable source/category"); StampFailed(list); continue; }

            // Derive the new param's spec from the SOURCE storage type — don't blindly trust GetDataType().
            var spec = DeriveSpec(src);

            var name = $"{sample.Field} (Transom)";
            ElementId paramId; Guid guid;
            try { (paramId, guid) = EnsureSharedTypeParam(doc, app, name, spec, cats); }
            catch (Exception ex) { failed.Add($"{sample.Field} (new type param) — {ex.Message}"); StampFailed(list); continue; }
            if (paramId == ElementId.InvalidElementId || guid == Guid.Empty)
            { failed.Add($"{sample.Field} (new type param) — couldn't create/bind '{name}'"); StampFailed(list); continue; }
            created++;

            // Write one value per affected type and VERIFY it (option 2 was previously unverified).
            // Track each type's success so every change in this column can be stamped by the type it targets.
            int wroteHere = 0;
            var typeOk = new Dictionary<long, bool>();
            foreach (var kv in perType)
            {
                var typeEl = doc.GetElement(new ElementId(kv.Key));
                var np = typeEl?.get_Parameter(guid);
                bool ok = np != null && !np.IsReadOnly && SetValue(np, kv.Value) && VerifyWrite(np, kv.Value);
                typeOk[kv.Key] = ok;
                if (ok) { written++; wroteHere++; }
                else failed.Add($"{name} on type {kv.Key}");
            }

            // Stamp every change in this column from the outcome of the type it writes to (a type whose write
            // never ran — element/type unresolved — counts as Failed, mirroring the failed-list semantics).
            foreach (var ch in list)
            {
                long tid = ElementTypeIdOf(doc, ch);
                ch.Outcome = tid >= 0 && typeOk.TryGetValue(tid, out var ok) && ok
                    ? ApplyOutcome.Applied : ApplyOutcome.Failed;
            }

            // Only surface the new column on schedules if at least one value actually landed — never leave a
            // junk field on a column that wrote nothing.
            if (wroteHere > 0) fields += AddFieldToSchedules(doc, paramId, scheduleNames, CleanHeading(sample));
        }

        return created == 0 ? "" :
            $"option 2: {created} new type param(s), {written} value(s) written, {fields} schedule field(s) added";
    }

    /// <summary>Stamps every change in a failed option-2 column as <see cref="ApplyOutcome.Failed"/>.</summary>
    private static void StampFailed(List<ProposedChange> changes)
    {
        foreach (var ch in changes) ch.Outcome = ApplyOutcome.Failed;
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
    ///     Ensures a shared TYPE parameter named <paramref name="name"/> with the given spec exists and is
    ///     bound (type binding) to <paramref name="cats"/>; regenerates, then returns its element id + GUID.
    ///     Reuses an existing definition only when its spec matches (else disambiguates the name); extends the
    ///     binding to new categories. Saves/restores the app shared-parameter file so an import doesn't change
    ///     session state, and writes a valid header if it has to create one.
    /// </summary>
    private static (ElementId id, Guid guid) EnsureSharedTypeParam(Document doc,
        Autodesk.Revit.ApplicationServices.Application app, string name, ForgeTypeId spec, CategorySet cats)
    {
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
            var useName = name;
            for (int i = 1; ext == null && i <= 50; i++)
            {
                var existing = group.Definitions.Cast<Definition>().FirstOrDefault(d => d.Name == useName) as ExternalDefinition;
                if (existing == null)
                {
                    ext = group.Definitions.Create(new ExternalDefinitionCreationOptions(useName, spec)) as ExternalDefinition;
                    break;
                }
                if (SameSpec(existing, spec)) { ext = existing; break; }
                useName = $"{name} ({i + 1})";
            }
            if (ext == null) return (ElementId.InvalidElementId, Guid.Empty);

            var bindings = doc.ParameterBindings;
            if (bindings.Contains(ext))
            {
                // Extend the existing binding to cover any new categories (don't leave new categories unbound).
                if (bindings.get_Item(ext) is TypeBinding existingTb)
                {
                    var union = app.Create.NewCategorySet();
                    foreach (Category c in existingTb.Categories) union.Insert(c);
                    foreach (Category c in cats) union.Insert(c);
                    bindings.ReInsert(ext, app.Create.NewTypeBinding(union), GroupTypeId.Data);
                }
            }
            else if (!bindings.Insert(ext, app.Create.NewTypeBinding(cats), GroupTypeId.Data))
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

    /// <summary>Adds the new parameter as a field to each named schedule that can show it (matching category).</summary>
    private static int AddFieldToSchedules(Document doc, ElementId paramId, List<string> scheduleNames, string heading)
    {
        int added = 0;
        var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
            .Where(v => !v.IsTemplate && scheduleNames.Contains(v.Name)).ToList();
        foreach (var sched in schedules)
        {
            try
            {
                var def = sched.Definition;
                bool present = def.GetFieldOrder().Select(fid => def.GetField(fid).ParameterId).Any(pid => pid == paramId);
                if (present) continue;
                var sf = def.GetSchedulableFields().FirstOrDefault(f => f.ParameterId == paramId);
                if (sf == null) continue; // not schedulable here (category mismatch)
                var newField = def.AddField(sf);               // AddField returns the added ScheduleField
                if (!string.IsNullOrWhiteSpace(heading))
                {
                    try { newField.ColumnHeading = heading; }  // new field only — pre-existing untouched
                    catch { /* heading is cosmetic; never fail the add over it */ }
                }
                added++;
            }
            catch { /* a schedule that won't take the field — skip it */ }
        }
        return added;
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
        string severity, string reason, string value) => new()
    {
        SheetTabName = sheet.SheetTabName, ExcelRow = row.ExcelRow, Col = col.ExcelCol, FieldName = col.FieldName,
        ElementLabel = label, Severity = severity, Reason = reason, Value = value,
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

    /// <summary>Whether an element is a member of a Revit group (its instance params can't be written directly), and the group's name.</summary>
    private static (bool inGroup, string name) GroupInfo(Document doc, Element e)
    {
        var gid = e.GroupId;
        if (gid == null || gid == ElementId.InvalidElementId) return (false, "");
        var g = doc.GetElement(gid);
        return (true, g != null ? SafeName(g) : "Group");
    }

    private static ProposedChange Mark(ProposedChange ch, bool inGroup, string groupName)
    {
        ch.InGroup = inGroup;
        ch.GroupName = groupName;
        // Project/shared params (positive id) can be set to vary per group instance — Transom applies them.
        // Built-in params (negative id) can't vary; they need the Claude-assist definition-swap.
        ch.GroupMode = inGroup ? ProposedChange.ModeFor(ch.ParameterId) : GroupMode.None;
        return ch;
    }
}
