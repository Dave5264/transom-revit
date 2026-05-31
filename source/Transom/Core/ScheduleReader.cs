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
            SourceModelGuid = _doc.CreationGUID.ToString(),
            SourceModelTitle = _doc.Title,
            // Round-trippable only where each visible row maps to one writable element.
            RoundTrippable = def.IsItemized && !def.IsMaterialTakeoff,
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
        var anchors = new string?[table.RowCount];

        if (table.RoundTrippable)
        {
            int uidCol = table.ColCount; // injected field appends as the new last visible column
            var els = new FilteredElementCollector(_doc, vs.Id)
                .WhereElementIsNotElementType().ToElements();
            var validUids = new HashSet<string>(els.Select(e => e.UniqueId));

            var tx = new Transaction(_doc, "Transom: read row anchors (rolled back)");
            tx.Start();
            try
            {
                // Stamp each element's UniqueId into a transient text param (rolled back below).
                foreach (var e in els)
                {
                    var p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                        p.Set(e.UniqueId);
                }

                var def = vs.Definition;
                SchedulableField? sf = def.GetSchedulableFields()
                    .FirstOrDefault(s => SafeFieldName(s) == "Comments");
                if (sf != null)
                {
                    try { def.AddField(sf); } catch { /* already a field */ }
                }

                _doc.Regenerate();

                var sec = vs.GetTableData().GetSectionData(SectionType.Body);
                if (uidCol < sec.NumberOfColumns)
                {
                    int nr = sec.NumberOfRows;
                    for (int r = 0; r < table.RowCount && r < nr; r++)
                    {
                        var uid = vs.GetCellText(SectionType.Body, r, uidCol) ?? "";
                        anchors[r] = validUids.Contains(uid) ? uid : null;
                    }
                }
            }
            finally
            {
                tx.RollBack(); // model never persistently mutated
            }
        }

        for (int r = 0; r < table.RowCount; r++)
        {
            var meta = new RowMeta { ExcelRow = r, UniqueId = anchors[r] };
            if (anchors[r] != null) meta.Kind = "element";
            else if (r == 0) meta.Kind = "columnHeader";
            else meta.Kind = RowAllEmpty(table, r) ? "blank" : "groupHeader";
            table.Rows.Add(meta);
        }
    }

    private static bool RowAllEmpty(ScheduleTable table, int r)
    {
        for (int c = 0; c < table.ColCount; c++)
            if (!string.IsNullOrEmpty(table.Cells[r][c].Text))
                return false;
        return true;
    }

    private string SafeFieldName(SchedulableField sf)
    {
        try { return sf.GetName(_doc); }
        catch { return ""; }
    }
}
