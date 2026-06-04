using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NPOI.SS.UserModel;

namespace Transom.Core;

public sealed class ImportColumn
{
    public int Col;                 // column index at export time
    public string FieldName = "";
    public string Header = "";       // exported column heading
    public int ParameterId;
    public string Binding = "instance";
    public bool Writable;
    public bool Hidden;
    public string? SpecTypeId;

    // Resolved against the current sheet (matched by header so reorder is safe):
    public int ExcelCol = -1;
    public bool Matched;
    public bool MatchedByPosition;   // true = header text didn't match; placed by exported position (banded/renamed/headers-off)
}

public sealed class ImportRow
{
    public int ExcelRow;       // 0-based sheet row (for report coloring)
    public string UniqueId = "";   // instance UniqueId (element rows) or type UniqueId (type rows)
    public string Kind = "element";  // element | type
    public List<string>? InstanceIds;  // type rows only: instances to bulk-write; null when ambiguous
    public List<string>? AggregatedTypeUids;  // multi-type rows: every type uid a type edit fans out to
    public string[] Cells = System.Array.Empty<string>();

    /// <summary>Set on an editable group-header row: its value cell bulk-writes the group field to its members.</summary>
    public GroupHeaderEdit? GroupHeaderEdit;
}

public sealed class ImportSheet
{
    public string ScheduleUniqueId = "";
    public string ScheduleName = "";
    public string SheetTabName = "";
    public int Category = -1;
    public bool RoundTrippable;
    public int AnchorCol = -1;
    public string[] CurrentHeaders = System.Array.Empty<string>();
    public bool FormattingChanged;   // headers renamed or reordered vs the exported metadata
    public List<ImportColumn> Columns = new();
    public List<ImportRow> Rows = new();

    /// <summary>Exported value per element: uniqueId -> (column -> value at export time).</summary>
    public Dictionary<string, Dictionary<int, string>> Baseline = new();

    /// <summary>Resolved binding per element: uniqueId -> (column -> instance|type|none).</summary>
    public Dictionary<string, Dictionary<int, string>> RowBindings = new();

    /// <summary>Row kind per anchor: uniqueId -> "element" | "type".</summary>
    public Dictionary<string, string> RowKinds = new();

    /// <summary>Type rows only: type uniqueId -> instances it represents (bulk write-back).</summary>
    public Dictionary<string, List<string>> RowInstanceIds = new();

    /// <summary>Multi-type rows: anchor uniqueId -> the full list of type uids a type edit must fan out to.</summary>
    public Dictionary<string, List<string>> RowAggregatedTypeUids = new();

    /// <summary>Editable group-header rows: synthetic uid -> its bulk-write spec.</summary>
    public Dictionary<string, GroupHeaderEdit> RowGroupHeaderEdits = new();

    /// <summary>Data cells (excelRow, excelCol) that are non-top-left members of a merged region — a user merge
    /// blanks them, which would otherwise look like "clear this value"; the importer skips + reports them.</summary>
    public HashSet<(int row, int col)> MergedCells = new();

    /// <summary>Anchor UniqueIds that appear on more than one row (e.g. a row was copied in Excel); all their
    /// copies are dropped so one element is never written twice. Reported by the importer.</summary>
    public List<string> DuplicateUids = new();
}

public sealed class ImportWorkbook
{
    public string Path = "";
    public string SourceModelGuid = "";
    public List<ImportSheet> Sheets = new();
}

/// <summary>
///     Reads a Transom-exported workbook back: parses cowork_meta (columns + per-element baseline values)
///     and, for each data sheet, locates the anchor column by its sentinel header (not index) and reads
///     per-row UniqueId + cells.
/// </summary>
public sealed class ExcelReader
{
    public ImportWorkbook Read(string path)
    {
        using var fs = File.OpenRead(path);
        IWorkbook wb = WorkbookFactory.Create(fs);

        var metaSheet = wb.GetSheet("cowork_meta")
                        ?? throw new System.InvalidOperationException(
                            "Not a Transom workbook (no cowork_meta sheet).");
        // The meta JSON is chunked across consecutive rows (cell A) to dodge Excel's 32,767-char cell cap;
        // reassemble it. Single-cell (older) workbooks read back as one chunk.
        var metaSb = new System.Text.StringBuilder();
        for (int r = 0; ; r++)
        {
            var s = metaSheet.GetRow(r)?.GetCell(0)?.ToString();
            if (string.IsNullOrEmpty(s)) break;
            metaSb.Append(s);
        }
        var metaJson = metaSb.ToString();

        using var doc = JsonDocument.Parse(metaJson);
        var root = doc.RootElement;

        var result = new ImportWorkbook
        {
            Path = path,
            SourceModelGuid = root.GetProperty("sourceModel").GetProperty("guid").GetString() ?? "",
        };

        foreach (var sheetMeta in root.GetProperty("sheets").EnumerateArray())
        {
            var imp = new ImportSheet
            {
                ScheduleUniqueId = sheetMeta.GetProperty("scheduleUniqueId").GetString() ?? "",
                ScheduleName = sheetMeta.GetProperty("scheduleName").GetString() ?? "",
                SheetTabName = sheetMeta.GetProperty("sheetName").GetString() ?? "",
                Category = sheetMeta.TryGetProperty("category", out var cat) ? cat.GetInt32() : -1,
                RoundTrippable = sheetMeta.TryGetProperty("roundTrippable", out var rt) && rt.GetBoolean(),
            };

            foreach (var col in sheetMeta.GetProperty("columns").EnumerateArray())
            {
                imp.Columns.Add(new ImportColumn
                {
                    Col = col.GetProperty("col").GetInt32(),
                    FieldName = col.GetProperty("fieldName").GetString() ?? "",
                    Header = col.TryGetProperty("header", out var hh) ? hh.GetString() ?? "" : "",
                    ParameterId = col.GetProperty("parameterId").GetInt32(),
                    Binding = col.GetProperty("binding").GetString() ?? "instance",
                    Writable = col.GetProperty("writable").GetBoolean(),
                    Hidden = col.TryGetProperty("hidden", out var hd) && hd.GetBoolean(),
                    SpecTypeId = col.TryGetProperty("specTypeId", out var s) && s.ValueKind == JsonValueKind.String
                        ? s.GetString()
                        : null,
                });
            }

            if (sheetMeta.TryGetProperty("baseline", out var baseEl) && baseEl.ValueKind == JsonValueKind.Object)
                foreach (var uidProp in baseEl.EnumerateObject())
                {
                    var map = new Dictionary<int, string>();
                    foreach (var colProp in uidProp.Value.EnumerateObject())
                        if (int.TryParse(colProp.Name, out var ci))
                            map[ci] = colProp.Value.GetString() ?? "";
                    imp.Baseline[uidProp.Name] = map;
                }

            // Per-row metadata keyed by uniqueId: resolved bindings, kind, and (type rows) instance lists.
            if (sheetMeta.TryGetProperty("rows", out var rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
                foreach (var rm in rowsEl.EnumerateArray())
                {
                    if (!rm.TryGetProperty("uniqueId", out var uidp) || uidp.ValueKind != JsonValueKind.String)
                        continue;
                    var uid = uidp.GetString()!;

                    if (rm.TryGetProperty("bindings", out var bel) && bel.ValueKind == JsonValueKind.Object)
                    {
                        var map = new Dictionary<int, string>();
                        foreach (var bp in bel.EnumerateObject())
                            if (int.TryParse(bp.Name, out var ci))
                                map[ci] = bp.Value.GetString() ?? "";
                        imp.RowBindings[uid] = map;
                    }

                    if (rm.TryGetProperty("kind", out var kp) && kp.ValueKind == JsonValueKind.String)
                        imp.RowKinds[uid] = kp.GetString() ?? "element";

                    if (rm.TryGetProperty("instanceIds", out var iel) && iel.ValueKind == JsonValueKind.Array)
                    {
                        var ids = new List<string>();
                        foreach (var ip in iel.EnumerateArray())
                            if (ip.ValueKind == JsonValueKind.String) ids.Add(ip.GetString()!);
                        imp.RowInstanceIds[uid] = ids;
                    }

                    if (rm.TryGetProperty("aggregatedTypeUids", out var ael) && ael.ValueKind == JsonValueKind.Array)
                    {
                        var agg = new List<string>();
                        foreach (var ap in ael.EnumerateArray())
                            if (ap.ValueKind == JsonValueKind.String) agg.Add(ap.GetString()!);
                        if (agg.Count > 0) imp.RowAggregatedTypeUids[uid] = agg;
                    }

                    if (rm.TryGetProperty("groupHeaderEdit", out var gh) && gh.ValueKind == JsonValueKind.Object)
                    {
                        var g = new GroupHeaderEdit
                        {
                            Col = gh.GetProperty("col").GetInt32(),
                            ParameterId = gh.GetProperty("parameterId").GetInt32(),
                            FieldName = gh.TryGetProperty("fieldName", out var gfn) ? gfn.GetString() ?? "" : "",
                            Binding = gh.TryGetProperty("binding", out var gb) ? gb.GetString() ?? "instance" : "instance",
                            SpecTypeId = gh.TryGetProperty("specTypeId", out var gs) && gs.ValueKind == JsonValueKind.String
                                ? gs.GetString() : null,
                        };
                        if (gh.TryGetProperty("instanceIds", out var gie) && gie.ValueKind == JsonValueKind.Array)
                            foreach (var ip in gie.EnumerateArray())
                                if (ip.ValueKind == JsonValueKind.String) g.InstanceIds.Add(ip.GetString()!);
                        imp.RowGroupHeaderEdits[uid] = g;
                    }
                }

            var ws = wb.GetSheet(imp.SheetTabName);
            if (ws != null)
                ReadRows(ws, imp);

            result.Sheets.Add(imp);
        }

        return result;
    }

    private static void ReadRows(ISheet ws, ImportSheet imp)
    {
        int anchorCol = -1;
        var header = ws.GetRow(0);
        if (header != null)
            for (int c = 0; c <= header.LastCellNum; c++)
                if (header.GetCell(c)?.ToString() == ScheduleReader.AnchorSentinel)
                {
                    anchorCol = c;
                    break;
                }

        if (anchorCol < 0)
            throw new System.InvalidOperationException(
                $"Sheet '{imp.ScheduleName}': anchor column '{ScheduleReader.AnchorSentinel}' not found — cannot import safely.");
        imp.AnchorCol = anchorCol;

        // Current header row (the user may have renamed/reordered these columns).
        var headers = new string[anchorCol];
        for (int c = 0; c < anchorCol; c++)
            headers[c] = header?.GetCell(c)?.ToString() ?? "";
        imp.CurrentHeaders = headers;

        // Match each metadata column to its current position by header text (so reorder is safe).
        var used = new bool[anchorCol];
        foreach (var col in imp.Columns)
        {
            if (!string.IsNullOrEmpty(col.Header))
            {
                for (int c = 0; c < anchorCol; c++)
                    if (!used[c] && headers[c] == col.Header) { col.ExcelCol = c; col.Matched = true; used[c] = true; break; }
            }
            else if (col.Col < anchorCol && !used[col.Col]) // legacy workbook (no stored header) -> position
            {
                col.ExcelCol = col.Col;
                col.Matched = true;
                used[col.Col] = true;
            }
        }

        // Positional fallback for columns header-matching couldn't place. A header text can fail to match when:
        //   • the schedule's column headers are turned off (no field-name row at all), or
        //   • the header is banded/multi-row (the rendered row 0 carries super-headers / merge blanks while the
        //     leaf field names live in a second header row), or
        //   • the user renamed a header in Excel.
        // It's only safe to fall back to the exported position when the sheet wasn't reordered — which we infer
        // from every header-matched column still sitting at its exported index. If anything moved, we don't
        // guess: unmatched columns stay unmatched and are reported (never mis-written).
        bool noReorder = imp.Columns.Where(c => c.Matched).All(c => c.ExcelCol == c.Col);
        // Also require the sheet's data-column count to match the export: if a column was inserted or deleted
        // before the anchor, positions no longer line up and falling back by index could write the wrong field.
        bool sameWidth = anchorCol == imp.Columns.Count;
        if (noReorder && sameWidth)
            foreach (var col in imp.Columns)
                if (!col.Matched && col.Col >= 0 && col.Col < anchorCol && !used[col.Col])
                {
                    col.ExcelCol = col.Col;
                    col.Matched = true;
                    col.MatchedByPosition = true;
                    used[col.Col] = true;
                }

        imp.FormattingChanged = imp.Columns.Any(c => !c.Matched || c.ExcelCol != c.Col);

        // Merged-cell map: a user merging cells in Excel leaves every non-top-left member blank, which would
        // otherwise read as "clear this value". Record those (data columns only) so the importer skips + reports
        // them instead of silently wiping the parameter.
        for (int i = 0; i < ws.NumMergedRegions; i++)
        {
            var m = ws.GetMergedRegion(i);
            for (int rr = m.FirstRow; rr <= m.LastRow; rr++)
                for (int cc = m.FirstColumn; cc <= m.LastColumn && cc < anchorCol; cc++)
                    if (rr != m.FirstRow || cc != m.FirstColumn)
                        imp.MergedCells.Add((rr, cc));
        }

        // First pass: gather anchored rows. A UniqueId on more than one row is only unsafe for ITEMIZED
        // (element) rows — that means a row was copied in Excel, so writing the element twice would double-apply,
        // and we drop the copies. For GROUPED schedules a single type can legitimately appear on several rows
        // (multi-field grouping); those are handled downstream (type-param writes dedupe + instance scope is
        // marked ambiguous), so they must NOT be dropped here.
        var pending = new List<(int r, string uid, string kind, string[] cells)>();
        var elemCount = new Dictionary<string, int>();
        for (int r = 1; r <= ws.LastRowNum; r++)
        {
            var row = ws.GetRow(r);
            if (row == null) continue;
            var uid = row.GetCell(anchorCol)?.ToString() ?? "";
            if (string.IsNullOrEmpty(uid)) continue; // header/group/blank rows carry no anchor

            var kind = imp.RowKinds.TryGetValue(uid, out var k) ? k : "element";
            var cells = new string[anchorCol];
            for (int c = 0; c < anchorCol; c++)
                cells[c] = row.GetCell(c)?.ToString() ?? "";
            pending.Add((r, uid, kind, cells));
            if (kind == "element")
                elemCount[uid] = elemCount.TryGetValue(uid, out var n) ? n + 1 : 1;
        }
        var dup = new HashSet<string>();
        foreach (var kv in elemCount)
            if (kv.Value > 1) { dup.Add(kv.Key); imp.DuplicateUids.Add(kv.Key); }

        foreach (var (r, uid, kind, cells) in pending)
        {
            if (kind == "element" && dup.Contains(uid)) continue; // a copied element row -> skip every copy
            imp.Rows.Add(new ImportRow
            {
                ExcelRow = r,
                UniqueId = uid,
                Kind = kind,
                InstanceIds = imp.RowInstanceIds.TryGetValue(uid, out var ids) ? ids : null,
                AggregatedTypeUids = imp.RowAggregatedTypeUids.TryGetValue(uid, out var agg) ? agg : null,
                GroupHeaderEdit = imp.RowGroupHeaderEdits.TryGetValue(uid, out var ghe) ? ghe : null,
                Cells = cells,
            });
        }
    }
}
