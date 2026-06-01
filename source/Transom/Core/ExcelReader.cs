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
}

public sealed class ImportRow
{
    public int ExcelRow;       // 0-based sheet row (for report coloring)
    public string UniqueId = "";
    public string[] Cells = System.Array.Empty<string>();
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
        var metaJson = metaSheet.GetRow(0)?.GetCell(0)?.ToString() ?? "";

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

        imp.FormattingChanged = imp.Columns.Any(c => !c.Matched || c.ExcelCol != c.Col);

        for (int r = 1; r <= ws.LastRowNum; r++)
        {
            var row = ws.GetRow(r);
            if (row == null) continue;
            var uid = row.GetCell(anchorCol)?.ToString() ?? "";
            if (string.IsNullOrEmpty(uid)) continue; // header/group/blank rows carry no anchor

            var cells = new string[anchorCol];
            for (int c = 0; c < anchorCol; c++)
                cells[c] = row.GetCell(c)?.ToString() ?? "";

            imp.Rows.Add(new ImportRow { ExcelRow = r, UniqueId = uid, Cells = cells });
        }
    }
}
