using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>
///     Reads a <see cref="ViewSchedule"/> into a <see cref="ScheduleTable"/>:
///     a display pass (GetCellText + styles + merges + widths) and the spike-proven
///     anchor pass (Approach A: rolled-back UniqueId injection) — see docs/SPIKE_RESULTS.md.
/// </summary>
public sealed class ScheduleReader
{
    public const string AnchorSentinel = "__transom_uid__";

    /// <summary>
    ///     The per-RENDERED-ROW key for the import baseline (three-way diff). MUST be derived identically on the
    ///     export side (<see cref="ExcelWriter"/>'s baseline build) and the import side (<see cref="Importer"/>'s
    ///     baseline lookup), so this single helper is the only place that computes it.
    ///     <para/>
    ///     A TYPE- or GROUP-organized schedule renders MANY instances under one anchor row whose UniqueId is the
    ///     TYPE uid — NOT unique across rows. Keying the baseline by the type uid alone makes every row of a type
    ///     overwrite the last, so each row's per-instance snapshot (e.g. a per-instance Mark) is lost and the
    ///     importer mis-detects edits. Including the row's primary resolved instance uid makes the key unique per
    ///     rendered row. Element rows (itemized) and header rows have no InstanceIds, so they keep their own
    ///     UniqueId — byte-identical to the historical behavior, so those paths are unaffected.
    /// </summary>
    public static string BaselineRowKey(string? uniqueId, string kind, IReadOnlyList<string>? instanceIds)
    {
        if ((kind == "type" || kind == "group") && instanceIds is { Count: > 0 })
            return uniqueId + "|" + instanceIds[0];   // type uid + first instance = unique per rendered row
        return uniqueId ?? "";                          // element rows: instance uid; headers: synthetic uid
    }

    /// <summary>
    ///     Built-in INSTANCE-IDENTITY parameters Revit lets DIFFER between instances of the same group type, so a
    ///     direct write commits on a grouped instance (no "vary", no Edit Group) — live-verified for Mark
    ///     (-1001203) 2026-06-19. Ordinary built-in data params (e.g. Comments -1010106) are NOT in this set: a
    ///     direct grouped write rolls back and they can't vary, so they must use option 2b / Claude-Assist. No
    ///     public API flag separates the two, so this is a curated allow-list shared by the export colouring
    ///     (NORMAL vs blue) and the import routing (<see cref="Importer"/> GroupMode.None vs BuiltinDance).
    /// </summary>
    public static bool IsInstanceIdentityBuiltin(int parameterId) =>
        parameterId == (int)BuiltInParameter.ALL_MODEL_MARK     // "Mark" (door/window/family Number maps here)
        || parameterId == (int)BuiltInParameter.DOOR_NUMBER     // doors "Mark"/Number (== ALL_MODEL_MARK on doors)
        || parameterId == (int)BuiltInParameter.ROOM_NUMBER;    // room "Number" (identity, varies per instance)

    private readonly Document _doc;

    /// <summary>Set by the export handler so the rolled-back anchor pass can auto-dismiss side-effect dialogs
    /// (e.g. "rename corresponding views?" when stamping a Level name) and swallow group-edit warnings.</summary>
    public UIApplication? UiApp;

    public ScheduleReader(Document doc) => _doc = doc;

    /// <summary>Swallows warnings raised during the rolled-back anchor stamp (e.g. modifying group members).</summary>
    private sealed class SwallowWarnings : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            foreach (var f in a.GetFailureMessages())
                if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
            return FailureProcessingResult.Continue;
        }
    }

    public ScheduleTable Read(ViewSchedule vs)
    {
        var def = vs.Definition;
        var table = new ScheduleTable
        {
            ScheduleName = vs.Name,
            ScheduleUniqueId = vs.UniqueId,
            Category = (int)def.CategoryId.Value,
            SourceModelGuid = _doc.CreationGUID.ToString(),
            SourceModelTitle = _doc.Title,
            // Itemized schedules anchor per instance; grouped (non-itemized) schedules anchor per type
            // (one row = one type). Material takeoffs are computed quantities — never round-trippable.
            // The anchor pass downgrades this to false if no row can actually be anchored.
            RoundTrippable = !def.IsMaterialTakeoff,
            // When the schedule's column headers are turned off, Body row 0 is data (no field-name row),
            // so the writer must synthesize a header row for import column-matching to work.
            HasHeaderRow = def.ShowHeaders,
        };

        var sec = vs.GetTableData().GetSectionData(SectionType.Body);
        int nr = sec.NumberOfRows;
        int nc = sec.NumberOfColumns;
        table.RowCount = nr;
        table.ColCount = nc;

        // Column metadata from the visible fields (1:1 with body columns).
        var fields = VisibleFields(def);
        for (int c = 0; c < nc && c < fields.Count; c++)
            table.Columns.Add(BuildColumnMeta(c, fields[c]));

        // Display pass: rendered text + per-cell style.
        table.Cells = new TableCell[nr][];
        for (int r = 0; r < nr; r++)
        {
            table.Cells[r] = new TableCell[nc];
            for (int c = 0; c < nc; c++)
                table.Cells[r][c] = new TableCell
                {
                    Text = vs.GetCellText(SectionType.Body, r, c) ?? "",
                    Style = ReadStyle(sec, r, c),
                };
        }

        table.Merges = ReadMerges(sec, nr, nc);

        table.ColWidthsPx = new int[nc];
        for (int c = 0; c < nc; c++)
            table.ColWidthsPx[c] = SafeWidthPx(sec, c);

        ReadAnchorsAndClassify(vs, table);
        if (table.RoundTrippable) DetectGroupHeaderEdits(vs, table);
        // §17: combined-parameter fields — make the combined column editable + append the component params as HIDDEN
        // columns ON THIS sheet (replaces the old "— parts" companion table). Mutates `table` in place.
        if (table.RoundTrippable) AppendCombinedComponents(vs, table);
        return table;
    }

    /// <summary>
    ///     Makes a group-HEADER row editable: changing the value in the header bulk-writes the grouping
    ///     parameter to every element under it (e.g. recategorize all sheets in "8-ELECTRICAL" at once).
    ///     Scoped to itemized schedules with a single instance-bound group field that has a header — the
    ///     common case, including hidden group fields shown only in the header. Multi-level / type-bound
    ///     group headers stay non-editable (greyed) for now.
    /// </summary>
    private void DetectGroupHeaderEdits(ViewSchedule vs, ScheduleTable table)
    {
        var def = vs.Definition;
        if (!def.IsItemized) return;

        ScheduleSortGroupField? hdr = null; int headerCount = 0;
        foreach (var sg in def.GetSortGroupFields())
            if (sg.ShowHeader) { hdr = sg; headerCount++; }
        if (hdr == null || headerCount != 1) return;   // single header level only

        var f = def.GetField(hdr.FieldId);
        if (f == null) return;
        int pid = (int)f.ParameterId.Value;
        if (f.FieldType != ScheduleFieldType.Instance) return;  // instance-bound group fields only

        var els = new FilteredElementCollector(_doc, vs.Id).WhereElementIsNotElementType().ToElements();
        var sample = els.FirstOrDefault();
        if (sample == null) return;
        if (ResolveBinding(sample, pid, "instance") != "instance") return;
        var sp = GetParamOn(sample, pid);
        if (sp == null || sp.IsReadOnly) return;

        string? spec = null;
        try { var s = f.GetSpecTypeId(); if (s != null && !s.Empty() && UnitUtils.IsMeasurableSpec(s)) spec = s.TypeId; }
        catch { /* string/int */ }
        if (!SupportedStorage(sp, spec)) return;

        var fieldName = f.GetName();
        GroupHeaderEdit? cur = null; int idx = 0;
        for (int r = 0; r < table.Rows.Count; r++)
        {
            var rm = table.Rows[r];
            if (rm.Kind == "groupHeader")
            {
                int valueCol = -1;
                for (int c = 0; c < table.ColCount; c++)
                    if (!string.IsNullOrEmpty(table.Cells[r][c].Text)) { valueCol = c; break; }
                if (valueCol < 0) { cur = null; continue; }
                cur = new GroupHeaderEdit { Col = valueCol, ParameterId = pid, FieldName = fieldName, Binding = "instance", SpecTypeId = spec };
                rm.GroupHeaderEdit = cur;
                rm.UniqueId = "grphdr::" + idx++;   // synthetic anchor so the header round-trips
            }
            else if (rm.Kind == "element" && rm.UniqueId != null && cur != null)
                cur.InstanceIds.Add(rm.UniqueId);
            // blank / columnHeader rows: leave the current group intact
        }

        // A header with no members can't write anything — revert it to a plain (greyed) header.
        foreach (var rm in table.Rows)
            if (rm.GroupHeaderEdit is { InstanceIds.Count: 0 }) { rm.GroupHeaderEdit = null; rm.UniqueId = null; }
    }

    // --- column metadata ---------------------------------------------------

    private static List<ScheduleField> VisibleFields(ScheduleDefinition def)
    {
        var list = new List<ScheduleField>();
        foreach (var fid in def.GetFieldOrder())
        {
            var f = def.GetField(fid);
            if (f != null && !f.IsHidden) list.Add(f);
        }
        return list;
    }

    private static ColumnMeta BuildColumnMeta(int col, ScheduleField f)
    {
        var meta = new ColumnMeta
        {
            Col = col,
            FieldName = f.GetName(),
            Header = f.ColumnHeading ?? "",
            Hidden = f.IsHidden,
            ParameterId = (int)f.ParameterId.Value,
        };

        meta.Binding = f.FieldType == ScheduleFieldType.ElementType ? "type"
            : f.FieldType == ScheduleFieldType.Instance ? "instance"
            : "none";

        bool calc = false, combined = false;
        try { calc = f.IsCalculatedField; } catch { /* not supported */ }
        try { combined = f.IsCombinedParameterField; } catch { /* not supported */ }
        meta.Writable = (meta.Binding is "instance" or "type") && !calc && !combined;

        try
        {
            var spec = f.GetSpecTypeId();
            if (spec != null && !spec.Empty() && UnitUtils.IsMeasurableSpec(spec))
                meta.SpecTypeId = spec.TypeId;
        }
        catch { /* string/int/no-spec field */ }

        return meta;
    }

    // --- styles / merges / widths -----------------------------------------

    private CellStyleInfo ReadStyle(TableSectionData sec, int r, int c)
    {
        var info = new CellStyleInfo();
        TableCellStyle s;
        try { s = sec.GetTableCellStyle(r, c); }
        catch { return info; }
        if (s == null) return info;

        info.FontName = s.FontName ?? "";
        info.TextSize = s.TextSize;
        info.Bold = s.IsFontBold;
        info.Italic = s.IsFontItalic;
        info.Underline = s.IsFontUnderline;
        info.HAlign = s.FontHorizontalAlignment.ToString();
        info.VAlign = s.FontVerticalAlignment.ToString();
        info.TextColor = PackColor(s.TextColor);
        info.BackColor = PackColor(s.BackgroundColor);
        info.BorderTop = BorderWeight(s.BorderTopLineStyle);
        info.BorderBottom = BorderWeight(s.BorderBottomLineStyle);
        info.BorderLeft = BorderWeight(s.BorderLeftLineStyle);
        info.BorderRight = BorderWeight(s.BorderRightLineStyle);
        return info;
    }

    /// <summary>Maps a border line-style id to a coarse weight: 0=none, 1=thin, 2=medium, 3=thick.</summary>
    private int BorderWeight(ElementId lineStyleId)
    {
        if (lineStyleId == null || lineStyleId.Value == -1) return 0; // -1 = no border
        if (lineStyleId.Value < 0) return 1;                          // built-in grid line -> thin
        try
        {
            var cat = (_doc.GetElement(lineStyleId) as GraphicsStyle)?.GraphicsStyleCategory;
            int? w = cat?.GetLineWeight(GraphicsStyleType.Projection);
            if (w == null) return 1;
            return w <= 3 ? 1 : (w <= 6 ? 2 : 3);
        }
        catch { return 1; }
    }

    private static int PackColor(Color c)
    {
        if (c == null || !c.IsValid) return -1;
        return (c.Red << 16) | (c.Green << 8) | c.Blue;
    }

    private static List<MergeRegion> ReadMerges(TableSectionData sec, int nr, int nc)
    {
        var seen = new HashSet<(int, int, int, int)>();
        var list = new List<MergeRegion>();
        for (int r = 0; r < nr; r++)
        for (int c = 0; c < nc; c++)
        {
            TableMergedCell m;
            try { m = sec.GetMergedCell(r, c); } catch { continue; }
            if (m == null) continue;
            if (m.Top == m.Bottom && m.Left == m.Right) continue; // unmerged 1x1
            if (seen.Add((m.Top, m.Bottom, m.Left, m.Right)))
                list.Add(new MergeRegion { Top = m.Top, Bottom = m.Bottom, Left = m.Left, Right = m.Right });
        }
        return list;
    }

    private static int SafeWidthPx(TableSectionData sec, int c)
    {
        try { return sec.GetColumnWidthInPixels(c); }
        catch { return 64; }
    }

    // --- anchor pass (Approach A) + row classification --------------------

    private void ReadAnchorsAndClassify(ViewSchedule vs, ScheduleTable table)
    {
        var def = vs.Definition;
        bool grouped = !def.IsItemized;

        var anchors = new string?[table.RowCount];           // instance uid (itemized) or type uid (grouped)
        var uidToElement = new Dictionary<string, Element>(); // both instances and (for grouped) types
        var typeToInstances = new Dictionary<string, List<string>>(); // grouped: type uid -> instance uids it owns
        var representative = new Dictionary<string, Element>();        // grouped: type uid -> one instance (binding resolution)

        bool fieldGrouped = false;
        if (table.RoundTrippable)
        {
            var els = new FilteredElementCollector(_doc, vs.Id)
                .WhereElementIsNotElementType().ToElements();
            foreach (var e in els) uidToElement[e.UniqueId] = e;

            if (grouped)
            {
                ReadTypeAnchors(vs, table, els, anchors, uidToElement, typeToInstances, representative);
                if (anchors.All(a => a == null))
                {
                    // Not type-based (areas, rooms): anchor each row to the set of instances sharing its
                    // group-field values (read-only value matching).
                    ReadFieldGroupAnchors(vs, table, els, anchors, uidToElement, typeToInstances, representative);
                    fieldGrouped = anchors.Any(a => a != null);
                }
            }
            else
                ReadInstanceAnchors(vs, table, els, anchors);

            // Nothing could be anchored (empty / linked / un-anchorable / not groupable) -> display-only.
            if (anchors.All(a => a == null))
                table.RoundTrippable = false;

            DetermineEditability(table, els); // mark columns that can't be written on import (greyed on export)
        }

        // CRITICAL: the rolled-back anchor pass above may have called def.AddField (when the UID carrier wasn't
        // already a visible column) inside a transaction that was then RolledBack — which INVALIDATES this
        // ScheduleDefinition handle. Re-fetch it before any further use; otherwise def.GetSortGroupFields() /
        // def.GetField() below throw InvalidObjectException ("the referenced object is not valid… its creation
        // was undone"). This aborted EVERY schedule whose anchor carrier had to be added as a field.
        def = vs.Definition;

        // A grouped row is bulk-safe only when its key maps to exactly one row; a key split across rows
        // (multi-field grouping) leaves instance scope ambiguous -> skip those rows' instance writes.
        var typeRowCount = anchors.Where(a => a != null).GroupBy(a => a!).ToDictionary(g => g.Key, g => g.Count());

        // #3: detect Type-Mark groups shared by 2+ types (e.g. "UJ" -> two near-identical types). They collapse to
        // ONE rendered row the per-type anchor can't resolve; we still make that row editable, anchored to ALL its
        // types (a type edit fans out to each). Only when grouped by visible field(s), so the row's group key is
        // readable from its cells.
        var sgParams = new List<int>();
        foreach (var sg in def.GetSortGroupFields())
        {
            var sgf = def.GetField(sg.FieldId);
            if (sgf != null) sgParams.Add((int)sgf.ParameterId.Value);
        }
        var sgCols = sgParams.Select(pid => table.Columns.FirstOrDefault(c => c.ParameterId == pid)?.Col ?? -1).ToList();
        bool canMulti = grouped && !fieldGrouped && sgParams.Count > 0 && sgCols.All(c => c >= 0);
        var typesByGroupKey = new Dictionary<string, List<string>>();
        if (canMulti)
            foreach (var kv in uidToElement)
            {
                var gkey = GroupKeyOf(kv.Value, sgParams);
                if (!typesByGroupKey.TryGetValue(gkey, out var lst)) { lst = new List<string>(); typesByGroupKey[gkey] = lst; }
                lst.Add(kv.Key);
            }
        string RowKey(int rr) => string.Join("", sgCols.Select(c => c < table.ColCount ? table.Cells[rr][c].Text : ""));
        bool RowKeyPresent(int rr) => sgCols.Any(c => c < table.ColCount && !string.IsNullOrEmpty(table.Cells[rr][c].Text));
        bool HasDataBeyondKey(int rr) => table.Columns.Any(c => c.Writable && !sgCols.Contains(c.Col)
            && c.Col < table.ColCount && !string.IsNullOrEmpty(table.Cells[rr][c.Col].Text));

        var writable = table.Columns.Where(c => c.Writable).ToList();

        // First rendered DATA row (first row carrying an element/type anchor). The LEADING block of anchorless,
        // non-blank rows above it is the schedule's HEADER BAND — Revit puts column captions in the Body section,
        // and a schedule with grouped headers (e.g. a "DOOR"/"FRAME" super-header over per-column captions) renders
        // as MULTIPLE leading header rows. Every such row is a header, not a 'groupHeader' data separator (which only
        // appears BETWEEN data rows). Classifying the whole leading band as 'columnHeader' lets the writer color the
        // round-trippable per-column captions correctly (and stops the 2nd+ header rows being mis-greyed as groupHeader).
        int firstDataRow = -1;
        for (int rr = 0; rr < table.RowCount; rr++)
            if (anchors[rr] != null) { firstDataRow = rr; break; }

        // HIDDEN-GROUP INSTANCE RESOLUTION (user-ratified 2026-06-19): a type that renders on MULTIPLE rows
        // (typeRowCount > 1) splits by its GROUP-FIELD values — including HIDDEN group fields with no column
        // (e.g. WINDOW grouped by Level + Type Mark, but Level has no column → "F3T" renders once per level).
        // Resolve EACH such row's true instance subset so it can be written/coloured by parameter nature (no
        // "ambiguous scope" skip/grey).
        //
        // For every multi-row type: partition its instances by the FULL group key (all sort/group fields). Then map
        // each of the type's rendered rows to a partition, PREFERRING a value match on the VISIBLE group columns the
        // row actually shows (reliable, order-independent); only when the visible columns don't distinguish the rows
        // (the splitting field is fully HIDDEN, the WINDOW case) fall back to the schedule's sort order. The sort
        // order respects each group field's ascending/descending flag. (Live-verify caveat: numeric/elevation
        // collation of a hidden level field — that's the remaining order-fallback risk.)
        var rowInstanceQueues = new Dictionary<string, Queue<List<string>>>();          // hidden-split: ordered partitions
        var rowInstanceByVisible = new Dictionary<string, Dictionary<string, List<string>>>(); // visible-split: type → (visibleKey → subset)
        // Visible group fields = the sort/group params that ARE a visible column (sgCols >= 0), in field order.
        var visGroupPids = new List<int>();
        for (int gi = 0; gi < sgParams.Count; gi++) if (gi < sgCols.Count && sgCols[gi] >= 0) visGroupPids.Add(sgParams[gi]);
        if (grouped && sgParams.Count > 0)
            foreach (var kv in typeRowCount)
            {
                if (kv.Value <= 1) continue;                          // single-row type → no split needed
                if (!typeToInstances.TryGetValue(kv.Key, out var members) || members.Count == 0) continue;
                var els2 = members.Select(uid => uidToElement.TryGetValue(uid, out var e) ? e : _doc.GetElement(uid))
                                  .Where(e => e != null).ToList();
                // Does the VISIBLE group key uniquely identify each partition? (distinct full-key count == distinct visible-key count)
                int fullKeys = els2.Select(e => GroupKeyOf(e!, sgParams)).Distinct().Count();
                int visKeys = visGroupPids.Count > 0 ? els2.Select(e => GroupKeyOf(e!, visGroupPids)).Distinct().Count() : 1;
                if (visGroupPids.Count > 0 && visKeys == fullKeys)
                {
                    // visible columns distinguish the rows → match by value (order-independent, reliable)
                    var map = new Dictionary<string, List<string>>();
                    foreach (var uid in members)
                    {
                        var el = uidToElement.TryGetValue(uid, out var e) ? e : _doc.GetElement(uid);
                        if (el == null) continue;
                        var vk = GroupKeyOf(el, visGroupPids);
                        if (!map.TryGetValue(vk, out var lst)) { lst = new List<string>(); map[vk] = lst; }
                        lst.Add(uid);
                    }
                    rowInstanceByVisible[kv.Key] = map;
                }
                else
                {
                    // splitting field is fully HIDDEN → order-based fallback: partition by full group key, order the
                    // partitions ordinal-ascending (the common grouped row order). LIVE-VERIFY caveat: descending sort
                    // or numeric/elevation collation of a hidden field could mis-order; confirm the WINDOW F3T case
                    // maps right (1st-floor 8 vs 2nd-floor 1) before trusting writes here.
                    var byKey = new SortedDictionary<string, List<string>>(System.StringComparer.Ordinal);
                    foreach (var uid in members)
                    {
                        var el = uidToElement.TryGetValue(uid, out var e) ? e : _doc.GetElement(uid);
                        if (el == null) continue;
                        var gkey = GroupKeyOf(el, sgParams);
                        if (!byKey.TryGetValue(gkey, out var lst)) { lst = new List<string>(); byKey[gkey] = lst; }
                        lst.Add(uid);
                    }
                    var q = new Queue<List<string>>();
                    foreach (var part in byKey.Values) q.Enqueue(part);
                    rowInstanceQueues[kv.Key] = q;
                }
            }

        // Cache GroupSafety verdicts per group-TYPE for this pass: built-in group-member cells in a BROKEN group
        // (anchored/sketch members, nested, or mixed orientation) color RED (option 2 / Claude-Assist) instead
        // of yellow (dance available). See GroupSafety.
        var groupSafetyCache = new Dictionary<long, GroupSafetyResult>();
        GroupSafetyResult SafetyOfGroup(ElementId? gid)
        {
            if (gid == null || gid == ElementId.InvalidElementId) return new GroupSafetyResult();
            if (_doc.GetElement(gid) is not Group g) return new GroupSafetyResult();
            long key = g.GetTypeId().Value;
            if (!groupSafetyCache.TryGetValue(key, out var res))
                groupSafetyCache[key] = res = GroupSafety.AnalyzeType(_doc, g.GetTypeId());
            return res;
        }

        for (int r = 0; r < table.RowCount; r++)
        {
            var meta = new RowMeta { ExcelRow = r, UniqueId = anchors[r] };
            if (anchors[r] != null && uidToElement.TryGetValue(anchors[r]!, out var host))
            {
                meta.Kind = fieldGrouped ? "group" : grouped ? "type" : "element";

                // Itemized rows map to one element: a group member can't have its instance params written
                // in a normal import, so those cells are greyed on export (per-element, not per-column).
                bool inGroup = !grouped && host.GroupId != null && host.GroupId != ElementId.InvalidElementId;

                // The anchor element BEFORE the representative-instance reassignment below: for grouped (type)
                // rows this is the row's actual TYPE element — needed for the per-cell type read-only check.
                var anchorEl = host;

                if (grouped)
                {
                    if (typeRowCount[anchors[r]!] == 1 && typeToInstances.TryGetValue(anchors[r]!, out var insts))
                        meta.InstanceIds = insts;          // single-row type -> all its instances
                    else if (rowInstanceByVisible.TryGetValue(anchors[r]!, out var vmap)
                             && vmap.TryGetValue(RowKey(r), out var vsub))
                        meta.InstanceIds = vsub;           // multi-row, VISIBLE split -> match this row's visible group key
                    else if (rowInstanceQueues.TryGetValue(anchors[r]!, out var q) && q.Count > 0)
                        meta.InstanceIds = q.Dequeue();    // multi-row, HIDDEN split -> this row's subset in sort order
                    representative.TryGetValue(anchors[r]!, out host); // resolve bindings against an instance
                }
                // Resolve binding per (element, field): a shared param can be instance in one family/category
                // and type in another; for grouped rows we resolve against a representative instance.
                if (host != null)
                {
                    var b = new Dictionary<int, string>();
                    var frozen = new HashSet<int>();    // grey: never importable
                    var groupProjectCols = new HashSet<int>(); // blue: grouped project param -> Transom sets vary + writes
                    var groupBuiltinCols = new HashSet<int>(); // yellow: grouped built-in param, SIMPLE group -> dance
                    var groupBrokenCols = new HashSet<int>();  // red (#94): grouped geometry-driving built-in -> Claude-Assist only
                    var bulkCols = new HashSet<int>();  // green: type param -> edits every instance of the type
                    // A grouped (non-itemized) row bulk-writes instance params to all its members; if any member
                    // lives in a Revit model group, those instance writes need Claude-assist too (blue), just like
                    // an itemized group member. (inGroup only covers the itemized case.)
                    bool anyMemberGrouped = false;
                    if (grouped && anchors[r] != null && typeToInstances.TryGetValue(anchors[r]!, out var memberIds))
                        foreach (var mid in memberIds)
                        {
                            var inst = _doc.GetElement(mid);
                            if (inst != null && inst.GroupId != null && inst.GroupId != ElementId.InvalidElementId)
                            { anyMemberGrouped = true; break; }
                        }

                    // Is this row's grouped element in a BROKEN model group? Drives red (broken) vs yellow (simple)
                    // for its built-in group-member cells. Prefer a broken verdict if ANY relevant group is broken.
                    GroupSafetyResult? rowGroupSafety = null;
                    if (inGroup) rowGroupSafety = SafetyOfGroup(host.GroupId);
                    else if (anyMemberGrouped && grouped && anchors[r] != null
                             && typeToInstances.TryGetValue(anchors[r]!, out var safMids))
                        foreach (var mid in safMids)
                        {
                            var inst = _doc.GetElement(mid);
                            if (inst?.GroupId == null || inst.GroupId == ElementId.InvalidElementId) continue;
                            var s = SafetyOfGroup(inst.GroupId);
                            rowGroupSafety ??= s;
                            if (s.IsBroken) { rowGroupSafety = s; break; }
                        }
                    bool rowGroupBroken = rowGroupSafety?.IsBroken == true;

                    foreach (var col in table.Columns)
                    {
                        if (!col.Writable) { frozen.Add(col.Col); continue; } // calculated/combined -> can't edit
                        var bind = ResolveBinding(host, col.ParameterId, col.Binding);
                        b[col.Col] = bind;
                        if (!col.ImportEditable) { frozen.Add(col.Col); continue; }      // read-only / family-type
                        if ((inGroup || anyMemberGrouped) && bind == "instance")
                        {
                            // Colour a grouped instance cell by the PARAMETER's nature via the shared classifier (#94 —
                            // single source of truth; user rule 2026-06-19: decide per parameter, no "ambiguous" outcome).
                            switch (ClassifyGroupedInstance(host, col.ParameterId))
                            {
                                case GroupedInstanceColor.Red: groupBrokenCols.Add(col.Col); break;     // geometry-driving → Assist
                                case GroupedInstanceColor.Blue: groupProjectCols.Add(col.Col); break;   // project/shared → vary
                                case GroupedInstanceColor.Yellow: groupBuiltinCols.Add(col.Col); break; // built-in data → new param
                                // Normal (identity built-in Mark/Number): no fill — direct write commits (GroupMode.None).
                            }
                        }
                        else if (bind == "type")
                        {
                            // Per-cell overlay: DetermineEditability samples ONE element per column, so a column
                            // can stay green while THIS row's type drives the parameter read-only (e.g. a family
                            // formula). Check the row's actual type; grey the cell instead of green when unwritable.
                            var typeEl = fieldGrouped ? null
                                : grouped ? anchorEl
                                : host.GetTypeId() != ElementId.InvalidElementId ? _doc.GetElement(host.GetTypeId()) : null;
                            var tp = typeEl == null ? null : GetParamOn(typeEl, col.ParameterId);
                            if (typeEl != null && (tp == null || tp.IsReadOnly)) frozen.Add(col.Col);
                            else bulkCols.Add(col.Col);                                  // bulk: writes to the type
                        }
                    }
                    meta.Bindings = b;
                    if (frozen.Count > 0) meta.FrozenCols = frozen;
                    if (groupProjectCols.Count > 0) meta.GroupProjectCols = groupProjectCols;
                    if (groupBuiltinCols.Count > 0) meta.GroupBuiltinCols = groupBuiltinCols;
                    if (groupBrokenCols.Count > 0)
                    {
                        meta.GroupBrokenCols = groupBrokenCols;
                        meta.GroupBrokenReason = rowGroupSafety?.Explain() ?? "";
                    }
                    if (bulkCols.Count > 0) meta.BulkCols = bulkCols;
                }
            }
            else if (canMulti && !(r == 0 && table.HasHeaderRow) && !RowAllEmpty(table, r)
                     && RowKeyPresent(r) && HasDataBeyondKey(r)
                     && typesByGroupKey.TryGetValue(RowKey(r), out var aggTypes) && aggTypes.Count >= 2)
                BuildMultiTypeRow(table, meta, aggTypes, typeToInstances);
            // Leading header band: row 0 with headers on, PLUS any anchorless non-blank row above the first data row
            // (the grouped-header super-rows + per-column caption row of a multi-line header). All are 'columnHeader'.
            else if (table.HasHeaderRow
                     && ((r == 0) || (firstDataRow >= 0 && r < firstDataRow && !RowAllEmpty(table, r))))
                meta.Kind = "columnHeader";
            else meta.Kind = RowAllEmpty(table, r) ? "blank" : "groupHeader";
            table.Rows.Add(meta);
        }
    }

    /// <summary>The sort/group key of an element: its sort/group field values joined (matches a rendered row's key).</summary>
    private string GroupKeyOf(Element e, List<int> paramIds)
    {
        var parts = new string[paramIds.Count];
        for (int i = 0; i < paramIds.Count; i++)
        {
            var p = GetParamOn(e, paramIds[i]);
            parts[i] = p == null ? "" : p.StorageType == StorageType.String ? p.AsString() ?? "" : p.AsValueString() ?? "";
        }
        return string.Join("", parts);
    }

    /// <summary>#3: turns a multi-type row (a Type Mark shared by 2+ types) into an editable "type" row anchored to
    /// ALL its types. InstanceIds = the union of every type's instances; the per-column hint colours are the WORST
    /// CASE across the aggregated types (sets are unioned, and ExcelWriter's grey&gt;blue&gt;yellow&gt;green precedence
    /// picks the most-cautionary). A type edit fans out to every type on import (see Importer.RecordTypeAll).</summary>
    private void BuildMultiTypeRow(ScheduleTable table, RowMeta meta, List<string> typeUids,
        Dictionary<string, List<string>> typeToInstances)
    {
        meta.Kind = "type";
        meta.UniqueId = typeUids[0];          // representative -> carried in the anchor column
        meta.AggregatedTypeUids = typeUids;   // a type edit fans out to all of these

        var allInstances = new List<string>();
        var bindings = new Dictionary<int, string>();
        var frozen = new HashSet<int>();
        var groupProject = new HashSet<int>();   // blue: grouped project/shared instance param → option 1 (vary)
        var groupBuiltin = new HashSet<int>();   // yellow: grouped built-in DATA instance param → new param (2a/2b)
        var groupBroken = new HashSet<int>();    // red (#94): grouped geometry-driving built-in → Claude-Assist only
        var bulk = new HashSet<int>();

        // Per-cell type read-only check uses the row's ANCHOR type (same element the import write anchors on).
        var anchorType = _doc.GetElement(typeUids[0]);

        foreach (var tuid in typeUids)
        {
            if (!typeToInstances.TryGetValue(tuid, out var members) || members.Count == 0) continue;
            allInstances.AddRange(members);
            var repr = _doc.GetElement(members[0]);
            if (repr == null) continue;
            bool anyMemberGrouped = members.Select(m => _doc.GetElement(m))
                .Any(inst => inst != null && inst.GroupId != null && inst.GroupId != ElementId.InvalidElementId);
            foreach (var col in table.Columns)
            {
                if (!col.Writable) { frozen.Add(col.Col); continue; }
                var bind = ResolveBinding(repr, col.ParameterId, col.Binding);
                if (!bindings.ContainsKey(col.Col)) bindings[col.Col] = bind;
                if (!col.ImportEditable) { frozen.Add(col.Col); continue; }
                if (anyMemberGrouped && bind == "instance")
                {
                    // Shared classifier (#94 — same mapping as every other site).
                    switch (ClassifyGroupedInstance(repr, col.ParameterId))
                    {
                        case GroupedInstanceColor.Red: groupBroken.Add(col.Col); break;     // geometry-driving → Assist
                        case GroupedInstanceColor.Blue: groupProject.Add(col.Col); break;   // project/shared → vary
                        case GroupedInstanceColor.Yellow: groupBuiltin.Add(col.Col); break; // built-in data → new param
                        // Normal (identity built-in Mark/Number): no fill — direct write commits (GroupMode.None).
                    }
                }
                else if (bind == "type")
                {
                    // Same per-cell overlay as the single-type row path: grey when the anchor type's
                    // parameter is missing or read-only (column-level meta stays untouched).
                    var tp = anchorType == null ? null : GetParamOn(anchorType, col.ParameterId);
                    if (anchorType != null && (tp == null || tp.IsReadOnly)) frozen.Add(col.Col);
                    else bulk.Add(col.Col);
                }
            }
        }

        meta.InstanceIds = allInstances;
        meta.Bindings = bindings;
        if (frozen.Count > 0) meta.FrozenCols = frozen;
        if (groupProject.Count > 0) meta.GroupProjectCols = groupProject;
        if (groupBuiltin.Count > 0) meta.GroupBuiltinCols = groupBuiltin;
        if (groupBroken.Count > 0) meta.GroupBrokenCols = groupBroken;   // red (#94): geometry-driving grouped built-ins
        if (bulk.Count > 0) meta.BulkCols = bulk;
    }

    /// <summary>
    ///     §17: COMBINED-parameter fields. For each visible combined field (one column built from N component
    ///     params, e.g. door WIDTH = Width_Active / Width_Inactive): (1) stamp the ordered component template
    ///     (ParamId + Prefix/Suffix/Separator) onto the combined column's ColumnMeta and make that column EDITABLE
    ///     (it was display-only); (2) append each distinct settable component param as a HIDDEN column ON THIS
    ///     sheet (sharing the same rows/anchor), so the component imports directly as the fallback. Mutates
    ///     <paramref name="main"/> in place (replaces the old separate "— parts" companion sheet). The import side
    ///     parses an edited combined cell back into its components (fail-closed); a hidden component column edited
    ///     directly imports like any normal column.
    /// </summary>
    private void AppendCombinedComponents(ViewSchedule vs, ScheduleTable main)
    {
        var def = vs.Definition;
        var visible = VisibleFields(def);

        // A representative element (+ its type) to resolve component params/bindings/values.
        Element? sample = null;
        foreach (var rm in main.Rows)
        {
            if (rm.Kind == "element" && rm.UniqueId != null) sample = _doc.GetElement(rm.UniqueId);
            else if (rm.InstanceIds is { Count: > 0 }) sample = _doc.GetElement(rm.InstanceIds[0]);
            if (sample != null) break;
        }
        if (sample == null) return;
        var sampleType = sample.GetTypeId() != ElementId.InvalidElementId ? _doc.GetElement(sample.GetTypeId()) : null;

        bool ResolvesSettable(int pid) =>
            (GetParamOn(sample, pid) != null || (sampleType != null && GetParamOn(sampleType, pid) != null))
            && ResolveBinding(sample, pid, "") is "instance" or "type";

        string? SpecOf(int pid)
        {
            var p = GetParamOn(sample, pid) ?? (sampleType != null ? GetParamOn(sampleType, pid) : null);
            if (p == null) return null;
            try { var s = p.Definition.GetDataType(); if (s != null && !s.Empty() && UnitUtils.IsMeasurableSpec(s)) return s.TypeId; }
            catch { /* string/int */ }
            return null;
        }

        // Stamp the template onto each combined column + collect the distinct component params to append.
        var compIds = new List<int>();
        var seen = new HashSet<int>();
        bool anyCombined = false;
        for (int c = 0; c < main.Columns.Count && c < visible.Count; c++)
        {
            var f = visible[c];
            bool combined = false;
            try { combined = f.IsCombinedParameterField; } catch { /* not supported */ }
            if (!combined) continue;
            IList<TableCellCombinedParameterData> parts;
            try { parts = f.GetCombinedParameters(); } catch { continue; }

            var template = new List<CombinedPart>();
            bool allPartsSettable = true;
            foreach (var p in parts)
            {
                if (p.ParamId == null || p.ParamId == ElementId.InvalidElementId) continue;   // literal/separator-only — not data
                int pid = (int)p.ParamId.Value;
                if (!ResolvesSettable(pid)) { allPartsSettable = false; break; }               // §17.4: a part we can't honor
                template.Add(new CombinedPart
                {
                    ParamId = pid,
                    Prefix = p.Prefix ?? "",
                    Suffix = p.Suffix ?? "",
                    Separator = p.Separator ?? "",
                    Binding = ResolveBinding(sample, pid, ""),
                    SpecTypeId = SpecOf(pid),
                });
                if (seen.Add(pid)) compIds.Add(pid);
            }
            // §17.4: only promise editability when every data part resolves to a settable param; else leave the
            // combined column display-only (no editable promise we can't keep).
            if (!allPartsSettable || template.Count == 0) continue;

            main.Columns[c].CombinedParts = template;
            main.Columns[c].Writable = true;          // the combined cell is now editable (parse→distribute on import)
            main.Columns[c].ImportEditable = true;
            anyCombined = true;
        }
        if (!anyCombined || compIds.Count == 0) return;

        // Don't re-add a component that's ALREADY a visible column (it round-trips on its own already).
        var existing = new HashSet<int>(main.Columns.Select(c => c.ParameterId));
        var toAppend = compIds.Where(pid => !existing.Contains(pid)).ToList();
        if (toAppend.Count == 0) return;

        int oldNc = main.ColCount;
        int addN = toAppend.Count;
        int newNc = oldNc + addN;

        // Build the hidden component ColumnMeta + per-row values, then widen the parent's cell/width arrays.
        var newCols = new List<ColumnMeta>();
        foreach (var pid in toAppend)
        {
            var p = GetParamOn(sample, pid) ?? (sampleType != null ? GetParamOn(sampleType, pid) : null);
            var col = new ColumnMeta
            {
                Col = main.Columns.Count + newCols.Count,
                ParameterId = pid,
                FieldName = p?.Definition?.Name ?? ("param " + pid),
                Header = p?.Definition?.Name ?? ("param " + pid),
                Binding = ResolveBinding(sample, pid, ""),
                Hidden = true,                        // §17: components are HIDDEN columns on the parent sheet
                SpecTypeId = SpecOf(pid),
            };
            col.Writable = p != null && col.Binding is "instance" or "type";
            col.ImportEditable = col.Writable && p != null && !p.IsReadOnly && SupportedStorage(p, col.SpecTypeId);
            newCols.Add(col);
        }

        // Widen every row's cell array + the width array; fill the appended cells with the component values.
        var widened = new TableCell[main.RowCount][];
        for (int r = 0; r < main.RowCount; r++)
        {
            widened[r] = new TableCell[newNc];
            for (int c = 0; c < oldNc; c++)
                widened[r][c] = (r < main.Cells.Length && c < main.Cells[r].Length) ? main.Cells[r][c] : new TableCell();

            var rm = r < main.Rows.Count ? main.Rows[r] : null;
            for (int j = 0; j < addN; j++)
            {
                int destCol = oldNc + j;
                var col = newCols[j];
                var cell = new TableCell();
                if (rm != null && rm.Kind == "columnHeader") cell.Text = col.Header;
                else if (rm != null && rm.UniqueId != null && rm.Kind is "element" or "type" or "group")
                {
                    Element? el = rm.Kind == "element" ? _doc.GetElement(rm.UniqueId) : null;
                    Element? typeEl = rm.Kind == "type" ? _doc.GetElement(rm.UniqueId) : null;
                    Element? repr = rm.InstanceIds is { Count: > 0 } ? _doc.GetElement(rm.InstanceIds[0]) : el;
                    Element? bindHost = el ?? repr;
                    if (bindHost != null)
                    {
                        var bind = ResolveBinding(bindHost, col.ParameterId, col.Binding);
                        var valueHost = bind == "type"
                            ? (typeEl ?? (bindHost.GetTypeId() != ElementId.InvalidElementId ? _doc.GetElement(bindHost.GetTypeId()) : null))
                            : el ?? repr;
                        var vp = valueHost == null ? null : GetParamOn(valueHost, col.ParameterId);
                        cell.Text = vp == null ? "" : vp.StorageType == StorageType.String ? vp.AsString() ?? "" : vp.AsValueString() ?? "";
                        // Per-row binding + frozen/bulk classification for the appended column (mirrors the main pass).
                        (rm.Bindings ??= new Dictionary<int, string>())[destCol] = bind;
                        bool inGroup = rm.Kind == "element" && el != null && el.GroupId != null && el.GroupId != ElementId.InvalidElementId;
                        if (!col.Writable || !col.ImportEditable) (rm.FrozenCols ??= new HashSet<int>()).Add(destCol);
                        else if (inGroup && bind == "instance")
                        {
                            // #94/F3: classify via the SHARED helper, same as the main passes (previously this appended
                            // column used a stale 2-way mapping — geometry→yellow, everything else→blue — and never
                            // produced red or split built-in data into yellow).
                            switch (ClassifyGroupedInstance(el, col.ParameterId))
                            {
                                case GroupedInstanceColor.Red: (rm.GroupBrokenCols ??= new HashSet<int>()).Add(destCol); break;
                                case GroupedInstanceColor.Blue: (rm.GroupProjectCols ??= new HashSet<int>()).Add(destCol); break;
                                case GroupedInstanceColor.Yellow: (rm.GroupBuiltinCols ??= new HashSet<int>()).Add(destCol); break;
                                // Normal (identity built-in): no fill — direct write commits.
                            }
                        }
                        else if (bind == "type") (rm.BulkCols ??= new HashSet<int>()).Add(destCol);
                    }
                }
                widened[r][destCol] = cell;
            }
        }

        foreach (var col in newCols) main.Columns.Add(col);
        main.Cells = widened;
        main.ColCount = newNc;
        var widths = new int[newNc];
        for (int c = 0; c < oldNc; c++) widths[c] = c < main.ColWidthsPx.Length ? main.ColWidthsPx[c] : 96;
        for (int c = oldNc; c < newNc; c++) widths[c] = 96;
        main.ColWidthsPx = widths;
    }

    /// <summary>
    ///     Marks each writable column import-editable or not, by checking a representative element's parameter:
    ///     read-only, or an unsupported storage type (ElementId / family-or-type-driven, or a Double with no
    ///     measurable spec) can't be written on import. Per-element nuances (group membership) are layered on
    ///     per row during classification.
    /// </summary>
    private void DetermineEditability(ScheduleTable table, System.Collections.Generic.IList<Element> els)
    {
        var sample = els.Count > 0 ? els[0] : null;
        foreach (var col in table.Columns)
        {
            if (!col.Writable) { col.ImportEditable = false; continue; }
            if (sample == null) continue; // can't tell -> leave editable

            var type = sample.GetTypeId() != ElementId.InvalidElementId ? _doc.GetElement(sample.GetTypeId()) : null;
            var p = GetParamOn(sample, col.ParameterId) ?? (type != null ? GetParamOn(type, col.ParameterId) : null);
            col.ImportEditable = p != null && !p.IsReadOnly && SupportedStorage(p, col.SpecTypeId);
        }
    }

    private static bool SupportedStorage(Parameter p, string? spec) =>
        p.StorageType == StorageType.String
        || p.StorageType == StorageType.Integer
        || (p.StorageType == StorageType.Double && spec != null);

    /// <summary>
    ///     The export edit-hint colour for a GROUPED INSTANCE cell (#94). SINGLE SOURCE OF TRUTH shared by every
    ///     classifier site (the itemized per-row pass, the multi-type meta pass, and the combined-column append) so the
    ///     colour→behaviour mapping can't diverge between them. Decide PER PARAMETER:
    ///       • RED    — geometry-driving built-in (Sill/Head Height): can't vary, can't safely replace (a new param
    ///                  wouldn't drive the 3D) → Claude-Assist only; option 2 suppressed.
    ///       • NORMAL — instance-identity built-in (Mark, Number): Revit lets it differ between group instances, so a
    ///                  direct write commits (GroupMode.None) — no fill; round-trips like an ungrouped instance cell.
    ///       • BLUE   — project/shared instance param (positive id) → option 1 (vary by group instance), preserves value.
    ///       • YELLOW — ordinary built-in DATA param (negative id, not geometry, not identity — e.g. Comments, Finish)
    ///                  → resolved via a new parameter (option 2a type / 2b instance); can't vary, can't write in place.
    /// </summary>
    private GroupedInstanceColor ClassifyGroupedInstance(Element? host, int parameterId)
    {
        // The geometry-driving test needs a live element to read the param's storage/spec; without one we can't
        // distinguish a geometry-driving built-in, so skip Red (all current callers pass a non-null host).
        if (host != null && IsGeometryDrivingBuiltin(GetParamOn(host, parameterId))) return GroupedInstanceColor.Red;
        if (IsInstanceIdentityBuiltin(parameterId)) return GroupedInstanceColor.Normal;
        return parameterId >= 0 ? GroupedInstanceColor.Blue : GroupedInstanceColor.Yellow;
    }

    /// <summary>#94: the export edit-hint colour for a grouped instance cell — see <see cref="ClassifyGroupedInstance"/>.</summary>
    private enum GroupedInstanceColor { Normal, Blue, Yellow, Red }

    /// <summary>
    ///     True when a parameter is a BUILT-IN INSTANCE param that DRIVES GEOMETRY / a model quantity — i.e. a
    ///     measurable Double (length/offset/height, via <see cref="UnitUtils.IsMeasurableSpec"/>) on a built-in
    ///     (negative-id) param. These can't round-trip inside a group by any safe automatic path: built-ins can't
    ///     "vary by group instance", and replacing the column with a NEW instance param (option 2b) would desync the
    ///     schedule from the actual geometry (the new param isn't wired to the model — e.g. Sill/Head Height drive the
    ///     window's position). The only honest edit is in Revit's Edit Group mode (Claude-Assist). So these colour RED
    ///     (#94) and option 2 is suppressed for them. (Project/shared instance params — positive id — vary instead,
    ///     which preserves geometry, so they stay BLUE even when geometric. Built-in DATA params — string/int, e.g.
    ///     Comments/Finish — colour YELLOW: resolved via a new parameter.)
    /// </summary>
    private static bool IsGeometryDrivingBuiltin(Parameter p)
    {
        if (p == null) return false;
        int pid;
        try { pid = (int)p.Id.Value; } catch { return false; }
        if (pid >= 0) return false;                       // project/shared params vary → not this case
        if (p.StorageType != StorageType.Double) return false;  // geometry/quantities are Doubles
        try
        {
            var s = p.Definition.GetDataType();
            return s != null && !s.Empty() && UnitUtils.IsMeasurableSpec(s);
        }
        catch { return false; }
    }

    /// <summary>Itemized schedules: anchor each row to its element — by a unique visible key column when one
    /// exists (pure reads, no transaction), else via the rolled-back UID stamp.</summary>
    private void ReadInstanceAnchors(ViewSchedule vs, ScheduleTable table, System.Collections.Generic.IList<Element> els, string?[] anchors)
    {
        if (TryAnchorByUniqueKeyColumn(table, els, anchors)) return;
        var validUids = new HashSet<string>(els.Select(e => e.UniqueId));
        ReadAnchorColumn(vs, table, els, anchors, validUids, (int)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
    }

    /// <summary>
    ///     No-stamp anchor path: a visible string column whose value is non-empty and DISTINCT across every
    ///     element in the schedule (Sheet Number on a sheet index — uniqueness Revit enforces — or a unique
    ///     Mark) identifies each rendered row by value match alone: pure reads, no transaction, no rollback to
    ///     trust. Both sides must form a perfect 1:1 — every element's value found on exactly one row — or the
    ///     column is rejected and the caller falls back to the stamp; a header/summary row that coincidentally
    ///     repeats a value breaks the 1:1 and rejects the column, so a false anchor can't slip through.
    /// </summary>
    private bool TryAnchorByUniqueKeyColumn(ScheduleTable table, System.Collections.Generic.IList<Element> els, string?[] anchors)
    {
        if (els.Count == 0) return false;
        foreach (var col in table.Columns)
        {
            // Real single-parameter visible columns only: hidden/synthetic (§17) columns aren't rendered rows'
            // cells at this point, and combined columns have no single parameter to read.
            if (col.Hidden || col.CombinedParts != null || col.ParameterId == -1 || col.Col < 0) continue;

            var byValue = new Dictionary<string, string>(StringComparer.Ordinal); // value -> element uid
            bool ok = true;
            foreach (var e in els)
            {
                var p = GetParamOn(e, col.ParameterId);
                var v = p != null && p.StorageType == StorageType.String ? p.AsString() : null;
                if (string.IsNullOrEmpty(v) || byValue.ContainsKey(v!)) { ok = false; break; }
                byValue[v!] = e.UniqueId;
            }
            if (!ok) continue;

            var rowFor = new Dictionary<string, int>(StringComparer.Ordinal); // value -> rendered row
            for (int r = 0; r < table.RowCount && ok; r++)
            {
                var text = col.Col < table.Cells[r].Length ? table.Cells[r][col.Col].Text : "";
                if (string.IsNullOrEmpty(text) || !byValue.ContainsKey(text)) continue; // header/blank/summary row
                if (rowFor.ContainsKey(text)) ok = false;   // value on two rows -> not a key, reject the column
                else rowFor[text] = r;
            }
            if (!ok || rowFor.Count != byValue.Count) continue; // not a perfect 1:1 -> try the next column

            foreach (var kv in rowFor) anchors[kv.Value] = byValue[kv.Key];
            return true;
        }
        return false;
    }

    /// <summary>
    ///     Stamps each host's UniqueId into a carrier text parameter (rolled back), renders it, and reads the
    ///     per-row UID. The carrier is Comments / Type Comments when available; otherwise any writable string
    ///     parameter that ISN'T a sort/group field (so stamping it can't reorder rows) and isn't an identity
    ///     param (<see cref="IdentityCarrierBlocklist"/>) — preferring an appended non-visible spare over a
    ///     visible column. This lets annotation/device families (which lack a Comments parameter) round-trip
    ///     too. The stamp is verified gone after the rollback (<see cref="VerifyAnchorRollback"/>).
    /// </summary>
    private void ReadAnchorColumn(ViewSchedule vs, ScheduleTable table,
        System.Collections.Generic.IList<Element> hosts, string?[] anchors, HashSet<string> validUids, int preferredBuiltIn)
    {
        var def = vs.Definition;
        var sample = hosts.FirstOrDefault();
        if (sample == null) return;

        int? carrier = PickAnchorParam(def, sample, preferredBuiltIn);
        if (carrier == null) return; // no usable carrier -> schedule stays display-only
        int cpid = carrier.Value;

        var stamped = new List<(string uid, string? original)>();   // what the stamp touched + each pre-stamp value
        var tx = new Transaction(_doc, "Transom: read row anchors (rolled back)");
        tx.Start();

        // The stamp can trigger Revit side-effect prompts (e.g. stamping a Level name -> "rename corresponding
        // views?") and group-edit warnings. Suppress both: auto-answer any dialog with No/Cancel, and swallow
        // warnings — nothing here is committed (we RollBack), so this is purely to keep export non-interactive.
        var fho = tx.GetFailureHandlingOptions();
        fho.SetFailuresPreprocessor(new SwallowWarnings());
        fho.SetClearAfterRollback(true);
        tx.SetFailureHandlingOptions(fho);
        System.EventHandler<Autodesk.Revit.UI.Events.DialogBoxShowingEventArgs>? dlg = null;
        if (UiApp != null)
        {
            dlg = (_, e) => { try { e.OverrideResult(7); } catch { /* 7 = No/Cancel */ } };
            UiApp.DialogBoxShowing += dlg;
        }
        try
        {
            // Stamp each host's UniqueId into the carrier parameter. CRITICAL: editing a member of a Revit
            // group makes Revit regenerate that group and INVALIDATE the other captured Element handles
            // mid-loop, so reusing the pre-loop `hosts` handles throws InvalidObjectException ("referenced
            // object is not valid…") on the next iteration and aborts the export. Capture the UniqueIds up
            // front (ids are stable across regen; the Element wrapper is not), re-fetch each pass, and isolate
            // each write — a stale handle or a member that rejects the edit just leaves that row unanchored
            // (display-only) instead of killing the whole schedule. Hit by itemized schedules with grouped
            // members (e.g. a unit-door schedule with hundreds of grouped instances).
            foreach (var uid in hosts.Select(h => h.UniqueId).ToList())
            {
                try
                {
                    var h = _doc.GetElement(uid);
                    if (h == null) continue;
                    var p = GetParamOn(h, cpid);
                    if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                    {
                        stamped.Add((uid, p.AsString()));   // pre-stamp value, for post-rollback verification
                        p.Set(uid);
                    }
                }
                catch { /* stale handle or rejected group-member edit — row stays unanchored */ }
            }

            // Append the carrier as a field if it isn't already a visible column.
            if (VisibleColumnOf(def, cpid) < 0)
            {
                var sf = SchedulableFieldFor(def, cpid);
                if (sf != null) { try { def.AddField(sf); } catch { /* already a field */ } }
            }

            _doc.Regenerate();

            int col = VisibleColumnOf(def, cpid);
            var sec = vs.GetTableData().GetSectionData(SectionType.Body);
            if (col >= 0 && col < sec.NumberOfColumns)
            {
                int nr = sec.NumberOfRows;
                for (int r = 0; r < table.RowCount && r < nr; r++)
                {
                    var uid = vs.GetCellText(SectionType.Body, r, col) ?? "";
                    anchors[r] = validUids.Contains(uid) ? uid : null;
                }
            }
        }
        finally
        {
            tx.RollBack();
            if (UiApp != null && dlg != null) UiApp.DialogBoxShowing -= dlg;
        }

        VerifyAnchorRollback(cpid, stamped);
    }

    /// <summary>
    ///     Post-rollback verification of the UID stamp — the spike's manual "all stamped params restored" check,
    ///     enforced in code on every export. If any carrier parameter still holds its element's UniqueId after
    ///     the rollback (a rollback that didn't take: never observed, but the failure mode is user data silently
    ///     keeping GUIDs), restore the captured pre-stamp values in a repair transaction and tell the user what
    ///     happened. Silent in normal operation.
    /// </summary>
    private void VerifyAnchorRollback(int cpid, List<(string uid, string? original)> stamped)
    {
        var stuck = new List<(string uid, string? original)>();
        foreach (var (uid, original) in stamped)
        {
            try
            {
                var h = _doc.GetElement(uid);
                var p = h == null ? null : GetParamOn(h, cpid);
                if (p != null && p.AsString() == uid && (original ?? "") != uid) stuck.Add((uid, original));
            }
            catch { /* element handle gone with the rollback — nothing kept the stamp */ }
        }
        if (stuck.Count == 0) return;

        int restored = 0;
        try
        {
            var fix = new Transaction(_doc, "Transom: restore anchor carrier values");
            fix.Start();
            foreach (var (uid, original) in stuck)
            {
                try
                {
                    var p = GetParamOn(_doc.GetElement(uid), cpid);
                    if (p != null && !p.IsReadOnly) { p.Set(original ?? ""); restored++; }
                }
                catch { /* counted by the dialog below */ }
            }
            fix.Commit();
        }
        catch { /* repair transaction itself failed — the dialog below still tells the user */ }

        Autodesk.Revit.UI.TaskDialog.Show("Transom",
            $"The export's row-anchor pass did not roll back cleanly: {stuck.Count} element(s) kept a temporary "
            + $"ID in a text parameter. Transom restored {restored} of them.\n\n"
            + (restored == stuck.Count
                ? "Your data is intact, but please report this so the cause can be found."
                : "Some values could NOT be restored — close the model WITHOUT saving or synchronizing, then reopen it."));
    }

    /// <summary>Identity parameters that must NEVER carry the rolled-back UID stamp: mid-pass (or on any
    /// rollback failure) the carrier visibly holds a GUID on EVERY scheduled element, and losing a name/number
    /// is model corruption, not junk. The 2026-06-01 sheet-index scare was exactly this — sheets have no
    /// Comments, so the picker hijacked Sheet Name and stamped every sheet in the project (rolled back, but a
    /// then-unsuppressed dialog froze the stamped state on screen).</summary>
    private static readonly HashSet<int> IdentityCarrierBlocklist = new()
    {
        (int)BuiltInParameter.SHEET_NAME, (int)BuiltInParameter.SHEET_NUMBER,
        (int)BuiltInParameter.VIEW_NAME,
        (int)BuiltInParameter.ROOM_NAME, (int)BuiltInParameter.ROOM_NUMBER,
        (int)BuiltInParameter.LEVEL_NAME,
        (int)BuiltInParameter.DATUM_TEXT,
    };

    /// <summary>
    ///     Picks the parameter to carry the rolled-back UID anchor: the preferred built-in (Comments / Type
    ///     Comments) when writable + present, else any writable string parameter that isn't a sort/group field
    ///     or an identity param (<see cref="IdentityCarrierBlocklist"/>). A non-visible schedulable spare is
    ///     preferred over hijacking a visible column: the appended field vanishes with the rollback, so the
    ///     stamp never flashes GUIDs in a column the user is looking at.
    /// </summary>
    private int? PickAnchorParam(ScheduleDefinition def, Element sample, int preferredBuiltIn)
    {
        var sortGroup = SortGroupParamIds(def);

        var pref = sample.get_Parameter((BuiltInParameter)preferredBuiltIn);
        if (pref != null && !pref.IsReadOnly && pref.StorageType == StorageType.String
            && !sortGroup.Contains(preferredBuiltIn))
            return preferredBuiltIn;

        int? visibleCand = null, addableCand = null;
        foreach (Parameter p in sample.Parameters)
        {
            if (p.StorageType != StorageType.String || p.IsReadOnly) continue;
            int pid = (int)p.Id.Value;
            if (sortGroup.Contains(pid) || IdentityCarrierBlocklist.Contains(pid)) continue;
            if (VisibleColumnOf(def, pid) >= 0) visibleCand ??= pid;
            else if (SchedulableFieldFor(def, pid) != null) addableCand ??= pid;
        }
        return addableCand ?? visibleCand;
    }

    private static HashSet<int> SortGroupParamIds(ScheduleDefinition def)
    {
        var s = new HashSet<int>();
        try
        {
            foreach (var sg in def.GetSortGroupFields())
            {
                var f = def.GetField(sg.FieldId);
                if (f != null) s.Add((int)f.ParameterId.Value);
            }
        }
        catch { /* not supported */ }
        return s;
    }

    private static SchedulableField? SchedulableFieldFor(ScheduleDefinition def, int pid)
    {
        foreach (var s in def.GetSchedulableFields())
        {
            try { if ((int)s.ParameterId.Value == pid) return s; }
            catch { /* skip */ }
        }
        return null;
    }

    /// <summary>
    ///     Grouped schedules: each row is one type. Stamp each type's UniqueId into Type Comments (a type
    ///     parameter, so every instance shares it and it renders uniformly in the grouped row), read it back
    ///     per row, and record the instances each type owns for bulk instance write-back.
    /// </summary>
    private void ReadTypeAnchors(ViewSchedule vs, ScheduleTable table, System.Collections.Generic.IList<Element> els,
        string?[] anchors, Dictionary<string, Element> uidToElement,
        Dictionary<string, List<string>> typeToInstances, Dictionary<string, Element> representative)
    {
        // Group the schedule's instances by their type (respects the schedule's category + filters).
        var typeElements = new Dictionary<string, Element>();
        foreach (var e in els)
        {
            var tid = e.GetTypeId();
            if (tid == ElementId.InvalidElementId) continue;
            var t = _doc.GetElement(tid);
            if (t == null) continue;
            var tUid = t.UniqueId;
            typeElements[tUid] = t;
            uidToElement[tUid] = t;
            if (!typeToInstances.TryGetValue(tUid, out var list)) { list = new List<string>(); typeToInstances[tUid] = list; }
            list.Add(e.UniqueId);
            if (!representative.ContainsKey(tUid)) representative[tUid] = e;
        }
        if (typeElements.Count == 0) return; // nothing type-groupable (e.g. rooms, sheet lists) -> display-only

        var validTypeUids = new HashSet<string>(typeElements.Keys);
        ReadAnchorColumn(vs, table, typeElements.Values.ToList(), anchors, validTypeUids,
            (int)BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
    }

    /// <summary>
    ///     Grouped schedules with no type (areas, rooms): anchor each row to the set of instances sharing its
    ///     group-field values. Reads the rendered row's group-column values and matches them to the elements'
    ///     group-field parameter values (read-only). Only works when every group field is a visible column;
    ///     hierarchical grouping with hidden group fields is left display-only.
    /// </summary>
    private void ReadFieldGroupAnchors(ViewSchedule vs, ScheduleTable table, System.Collections.Generic.IList<Element> els,
        string?[] anchors, Dictionary<string, Element> uidToElement,
        Dictionary<string, List<string>> groupToInstances, Dictionary<string, Element> representative)
    {
        const string sep = "";
        var def = vs.Definition;

        var gpids = new List<int>();
        foreach (var sg in def.GetSortGroupFields())
        {
            var f = def.GetField(sg.FieldId);
            if (f != null) gpids.Add((int)f.ParameterId.Value);
        }
        if (gpids.Count == 0) return;

        // Element group key = its group-field values, joined in field order.
        string ElemKey(Element e)
        {
            var parts = new string[gpids.Count];
            for (int i = 0; i < gpids.Count; i++)
            {
                var p = GetParamOn(e, gpids[i]);
                parts[i] = p == null ? "" : p.StorageType == StorageType.String ? p.AsString() ?? "" : p.AsValueString() ?? "";
            }
            return string.Join(sep, parts);
        }

        var groups = new Dictionary<string, List<string>>();
        var groupRepr = new Dictionary<string, Element>();
        var groupIndex = new Dictionary<string, int>();
        foreach (var e in els)
        {
            var k = ElemKey(e);
            if (!groups.TryGetValue(k, out var list))
            {
                list = new List<string>();
                groups[k] = list;
                groupRepr[k] = e;
                groupIndex[k] = groupIndex.Count;
            }
            list.Add(e.UniqueId);
        }

        // Visible column index of each group field (in field order). Bail if any group field is hidden.
        var pidToCol = new Dictionary<int, int>();
        int vc = 0;
        foreach (var fid in def.GetFieldOrder())
        {
            var f = def.GetField(fid);
            if (f == null || f.IsHidden) continue;
            int pid = (int)f.ParameterId.Value;
            if (!pidToCol.ContainsKey(pid)) pidToCol[pid] = vc;
            vc++;
        }
        var gcols = new List<int>();
        foreach (var pid in gpids)
        {
            if (!pidToCol.TryGetValue(pid, out var c)) return; // a group field isn't a visible column -> bail
            gcols.Add(c);
        }

        var sec = vs.GetTableData().GetSectionData(SectionType.Body);
        int nr = sec.NumberOfRows;
        for (int r = 0; r < table.RowCount && r < nr; r++)
        {
            var parts = new string[gcols.Count];
            bool any = false;
            for (int i = 0; i < gcols.Count; i++)
            {
                var t = vs.GetCellText(SectionType.Body, r, gcols[i]) ?? "";
                if (t.Length > 0) any = true;
                parts[i] = t;
            }
            if (!any) continue;
            var key = string.Join(sep, parts);
            if (!groups.TryGetValue(key, out var insts)) continue; // header / non-matching row

            var synth = "grp::" + groupIndex[key]; // XML-safe anchor id (the raw key may hold control chars)
            anchors[r] = synth;
            if (!groupToInstances.ContainsKey(synth))
            {
                groupToInstances[synth] = insts;
                representative[synth] = groupRepr[key];
                uidToElement[synth] = groupRepr[key];
            }
        }
    }

    /// <summary>
    ///     Where to write this parameter for this element — "instance", "type", or "none". The schedule field's
    ///     own classification wins when that host actually has the parameter: a window's Height/Width are type
    ///     parameters even though the instance also exposes a read-only mirror that lies about IsReadOnly (Set
    ///     silently fails). Only fall back to the other host when the scheduled one doesn't carry the parameter
    ///     (the multi-category case a shared param can be instance in one family, type in another).
    /// </summary>
    private string ResolveBinding(Element e, int parameterId, string scheduleBinding)
    {
        bool onInstance = GetParamOn(e, parameterId) != null;
        bool onType = false;
        var typeId = e.GetTypeId();
        if (typeId != ElementId.InvalidElementId)
        {
            var type = _doc.GetElement(typeId);
            onType = type != null && GetParamOn(type, parameterId) != null;
        }

        if (scheduleBinding == "type" && onType) return "type";
        if (scheduleBinding == "instance" && onInstance) return "instance";
        if (onInstance) return "instance";
        if (onType) return "type";
        return "none";
    }

    private static Parameter? GetParamOn(Element host, int parameterId)
    {
        if (parameterId < 0) return host.get_Parameter((BuiltInParameter)parameterId);
        foreach (Parameter p in host.Parameters)
            if (p.Id.Value == parameterId) return p;
        return null;
    }

    private static bool RowAllEmpty(ScheduleTable table, int r)
    {
        for (int c = 0; c < table.ColCount; c++)
            if (!string.IsNullOrEmpty(table.Cells[r][c].Text))
                return false;
        return true;
    }

    /// <summary>Visible (body) column index of the first non-hidden field with the given parameter id, or -1.</summary>
    private static int VisibleColumnOf(ScheduleDefinition def, int parameterId)
    {
        int idx = 0;
        foreach (var fid in def.GetFieldOrder())
        {
            var f = def.GetField(fid);
            if (f == null || f.IsHidden) continue;
            if ((int)f.ParameterId.Value == parameterId) return idx;
            idx++;
        }
        return -1;
    }

    private string SafeFieldName(SchedulableField sf)
    {
        try { return sf.GetName(_doc); }
        catch { return ""; }
    }
}
