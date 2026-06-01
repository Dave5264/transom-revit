using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Transom.Core;

/// <summary>
///     Reads a <see cref="ViewSchedule"/> into a <see cref="ScheduleTable"/>:
///     a display pass (GetCellText + styles + merges + widths) and the spike-proven
///     anchor pass (Approach A: rolled-back UniqueId injection) — see SPIKE_RESULTS.md.
/// </summary>
public sealed class ScheduleReader
{
    public const string AnchorSentinel = "__transom_uid__";

    private readonly Document _doc;

    public ScheduleReader(Document doc) => _doc = doc;

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
        return table;
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

        if (table.RoundTrippable)
        {
            var els = new FilteredElementCollector(_doc, vs.Id)
                .WhereElementIsNotElementType().ToElements();
            foreach (var e in els) uidToElement[e.UniqueId] = e;

            if (grouped)
                ReadTypeAnchors(vs, table, els, anchors, uidToElement, typeToInstances, representative);
            else
                ReadInstanceAnchors(vs, table, els, anchors);

            // Nothing could be anchored (empty / linked / un-anchorable / not type-groupable) -> display-only.
            if (anchors.All(a => a == null))
                table.RoundTrippable = false;
        }

        // Grouped rows are bulk-instance-safe only when their type maps to exactly one row; a type split
        // across rows (multi-field grouping) leaves instance scope ambiguous -> type params only.
        var typeRowCount = anchors.Where(a => a != null).GroupBy(a => a!).ToDictionary(g => g.Key, g => g.Count());

        var writable = table.Columns.Where(c => c.Writable).ToList();
        for (int r = 0; r < table.RowCount; r++)
        {
            var meta = new RowMeta { ExcelRow = r, UniqueId = anchors[r] };
            if (anchors[r] != null && uidToElement.TryGetValue(anchors[r]!, out var host))
            {
                meta.Kind = grouped ? "type" : "element";
                if (grouped)
                {
                    if (typeRowCount[anchors[r]!] == 1 && typeToInstances.TryGetValue(anchors[r]!, out var insts))
                        meta.InstanceIds = insts;          // unambiguous -> bulk-instance allowed
                    representative.TryGetValue(anchors[r]!, out host); // resolve bindings against an instance
                }
                // Resolve binding per (element, field): a shared param can be instance in one family/category
                // and type in another; for grouped rows we resolve against a representative instance.
                if (host != null)
                {
                    var b = new Dictionary<int, string>();
                    foreach (var col in writable) b[col.Col] = ResolveBinding(host, col.ParameterId, col.Binding);
                    meta.Bindings = b;
                }
            }
            else if (r == 0) meta.Kind = "columnHeader";
            else meta.Kind = RowAllEmpty(table, r) ? "blank" : "groupHeader";
            table.Rows.Add(meta);
        }
    }

    /// <summary>Itemized schedules: anchor each row to its element via a rolled-back UID stamp.</summary>
    private void ReadInstanceAnchors(ViewSchedule vs, ScheduleTable table, System.Collections.Generic.IList<Element> els, string?[] anchors)
    {
        var validUids = new HashSet<string>(els.Select(e => e.UniqueId));
        ReadAnchorColumn(vs, table, els, anchors, validUids, (int)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
    }

    /// <summary>
    ///     Stamps each host's UniqueId into a carrier text parameter (rolled back), renders it, and reads the
    ///     per-row UID. The carrier is Comments / Type Comments when available; otherwise any writable string
    ///     parameter that ISN'T a sort/group field (so stamping it can't reorder rows) — hijacking a visible
    ///     column or appending a schedulable spare. This lets annotation/device families (which lack a Comments
    ///     parameter) round-trip too.
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

        var tx = new Transaction(_doc, "Transom: read row anchors (rolled back)");
        tx.Start();
        try
        {
            foreach (var h in hosts)
            {
                var p = GetParamOn(h, cpid);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                    p.Set(h.UniqueId);
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
        finally { tx.RollBack(); }
    }

    /// <summary>
    ///     Picks the parameter to carry the rolled-back UID anchor: the preferred built-in (Comments / Type
    ///     Comments) when writable + present, else any writable string parameter that isn't a sort/group field,
    ///     preferring one already visible (hijack its column) over a schedulable spare to append.
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
            if (sortGroup.Contains(pid)) continue;
            if (VisibleColumnOf(def, pid) >= 0) visibleCand ??= pid;
            else if (SchedulableFieldFor(def, pid) != null) addableCand ??= pid;
        }
        return visibleCand ?? addableCand;
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
