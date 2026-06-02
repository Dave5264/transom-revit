using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;

namespace Transom.Core;

/// <summary>
///     Writes one or more <see cref="ScheduleTable"/>s to .xlsx (XSSF), legacy .xls (HSSF), or .csv.
///     Each schedule becomes its own worksheet (xlsx/xls); CSV gets one file per schedule. Reproduces
///     fonts, colors, alignment, per-side borders, merges, and fitted widths, plus a hidden
///     sentinel-headed UniqueId anchor column and a hidden cowork_meta sheet (xlsx/xls only).
/// </summary>
public sealed class ExcelWriter
{
    private const int HssfStyleCap = 4000;

    public void Write(ScheduleTable table, string path) => WriteMany(new List<ScheduleTable> { table }, path);

    public void WriteMany(List<ScheduleTable> tables, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".csv") { WriteCsvMany(tables, path); return; }

        bool xls = ext == ".xls";
        IWorkbook wb = xls ? new HSSFWorkbook() : new XSSFWorkbook();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairs = new List<(ScheduleTable t, string name)>();

        foreach (var table in tables)
        {
            var name = UniqueName(SafeSheetName(table.ScheduleName), used);
            WriteSheet(wb, wb.CreateSheet(name), table, xls);
            pairs.Add((table, name));
        }

        var meta = wb.CreateSheet("cowork_meta");
        // Excel hard-caps a single cell at 32,767 chars; a large grouped schedule's metadata (per-row instanceIds
        // + baseline) easily exceeds that. Split the JSON across consecutive rows (cell A) and reassemble on read.
        var metaJson = BuildMetaJson(pairs);
        const int metaChunk = 32000;
        if (metaJson.Length == 0)
            meta.CreateRow(0).CreateCell(0).SetCellValue("");
        else
            for (int i = 0, r = 0; i < metaJson.Length; i += metaChunk, r++)
                meta.CreateRow(r).CreateCell(0).SetCellValue(metaJson.Substring(i, Math.Min(metaChunk, metaJson.Length - i)));
        wb.SetSheetHidden(wb.GetSheetIndex(meta), SheetState.VeryHidden);

        using var fs = File.Create(path);
        wb.Write(fs);
    }

    private static void WriteSheet(IWorkbook wb, ISheet sheet, ScheduleTable table, bool xls)
    {
        var cache = new Dictionary<string, ICellStyle>();
        int anchorCol = table.ColCount;

        // When the schedule's column headers are turned off, Revit's Body has no field-name row, so row 0 is real
        // data. Synthesize a header row (field names + sentinel) and shift the body down one row — otherwise import
        // can't match columns by header and the first data row's anchor would be clobbered by the sentinel.
        // Also synthesize when the schedule is empty (no body rows at all): without it the sheet would carry no
        // header and no sentinel, so re-importing the (empty) workbook would fail with "anchor column not found".
        bool synth = !table.HasHeaderRow || table.RowCount == 0;
        int rowOffset = synth ? 1 : 0;

        if (synth)
        {
            var hr = sheet.CreateRow(0);
            for (int c = 0; c < table.ColCount; c++)
            {
                var name = c < table.Columns.Count
                    ? (string.IsNullOrEmpty(table.Columns[c].Header) ? table.Columns[c].FieldName : table.Columns[c].Header)
                    : "";
                var cell = hr.CreateCell(c);
                cell.SetCellValue(name);
                cell.CellStyle = GetStyle(wb, cache, new CellStyleInfo { Bold = true }, xls);
            }
            hr.CreateCell(anchorCol).SetCellValue(ScheduleReader.AnchorSentinel);
        }

        for (int r = 0; r < table.RowCount; r++)
        {
            var row = sheet.CreateRow(r + rowOffset);
            // Export hint colours (round-trippable schedules only): grey = never importable,
            // blue = group-member instance param (importable via Claude-assist only).
            var frozen = table.RoundTrippable && r < table.Rows.Count ? table.Rows[r].FrozenCols : null;
            var groupProjectCols = table.RoundTrippable && r < table.Rows.Count ? table.Rows[r].GroupProjectCols : null;
            var groupBuiltinCols = table.RoundTrippable && r < table.Rows.Count ? table.Rows[r].GroupBuiltinCols : null;
            // Group-header / blank separator rows carry no element anchor, so editing them does nothing on
            // import. Grey the whole row so it reads as non-editable (matches the per-cell grey hint).
            var rowKind = r < table.Rows.Count ? table.Rows[r].Kind : null;
            var ghe = r < table.Rows.Count ? table.Rows[r].GroupHeaderEdit : null;
            var bulk = table.RoundTrippable && r < table.Rows.Count ? table.Rows[r].BulkCols : null;
            bool nonImportableRow = table.RoundTrippable && rowKind is "groupHeader" or "blank";
            for (int c = 0; c < table.ColCount; c++)
            {
                var tc = table.Cells[r][c];
                var cell = row.CreateCell(c);
                cell.SetCellValue(tc.Text);
                // Colour precedence: grey (can't import) > blue (grouped project param, Transom applies via vary)
                // > yellow/grey (grouped built-in param: yellow when Claude-assist on, distinct grey when off)
                // > green (bulk: edits many elements) > normal. An editable group-header cell is a bulk write -> green.
                bool isGroupEditCell = ghe != null && c == ghe.Col;
                var style = isGroupEditCell ? GreenOf(tc.Style)
                    : nonImportableRow ? GreyOf(tc.Style)
                    : frozen != null && frozen.Contains(c) ? GreyOf(tc.Style)
                    : groupProjectCols != null && groupProjectCols.Contains(c) ? BlueOf(tc.Style)
                    : groupBuiltinCols != null && groupBuiltinCols.Contains(c)
                        ? (table.ClaudeAssistEnabled ? YellowOf(tc.Style) : GreyBuiltinOf(tc.Style))
                    : bulk != null && bulk.Contains(c) ? GreenOf(tc.Style)
                    : tc.Style;
                cell.CellStyle = GetStyle(wb, cache, style, xls);
            }

            var anchorCell = row.CreateCell(anchorCol);
            // The sentinel-headed anchor column: header carries the sentinel; data rows carry their UniqueId.
            // With a synthesized header row the sentinel is already written above, so body row 0 keeps its anchor.
            if (!synth && r == 0)
                anchorCell.SetCellValue(ScheduleReader.AnchorSentinel);
            else if (r < table.Rows.Count && !string.IsNullOrEmpty(table.Rows[r].UniqueId))
                anchorCell.SetCellValue(table.Rows[r].UniqueId);
        }

        foreach (var m in table.Merges)
            sheet.AddMergedRegion(new CellRangeAddress(m.Top + rowOffset, m.Bottom + rowOffset, m.Left, m.Right));

        for (int c = 0; c < table.ColCount; c++)
        {
            int w;
            try
            {
                sheet.AutoSizeColumn(c);
                w = (int)sheet.GetColumnWidth(c) + 2 * 256;
            }
            catch
            {
                int maxLen = 4;
                for (int r = 0; r < table.RowCount; r++)
                    maxLen = Math.Max(maxLen, table.Cells[r][c].Text.Length);
                w = (maxLen + 2) * 256;
            }
            sheet.SetColumnWidth(c, Math.Max(8 * 256, Math.Min(w, 60 * 256)));
        }

        sheet.SetColumnHidden(anchorCol, true);
    }

    /// <summary>A greyed copy of a cell style (grey text on light-grey fill) for cells that can't be imported.</summary>
    private static CellStyleInfo GreyOf(CellStyleInfo s) => new()
    {
        FontName = s.FontName, TextSize = s.TextSize, Bold = s.Bold, Italic = s.Italic, Underline = s.Underline,
        HAlign = s.HAlign, VAlign = s.VAlign, TextColor = 0x9A9A9A, BackColor = 0xE6E6E6,
        BorderTop = s.BorderTop, BorderBottom = s.BorderBottom, BorderLeft = s.BorderLeft, BorderRight = s.BorderRight,
    };

    /// <summary>A blue-tinted-FILL copy of a cell style for grouped PROJECT-parameter cells. Transom applies these
    /// itself (sets "vary by group instance" then writes). Keeps the original (black) text colour.</summary>
    private static CellStyleInfo BlueOf(CellStyleInfo s) => new()
    {
        FontName = s.FontName, TextSize = s.TextSize, Bold = s.Bold, Italic = s.Italic, Underline = s.Underline,
        HAlign = s.HAlign, VAlign = s.VAlign, TextColor = s.TextColor, BackColor = 0xDDEBF7,
        BorderTop = s.BorderTop, BorderBottom = s.BorderBottom, BorderLeft = s.BorderLeft, BorderRight = s.BorderRight,
    };

    /// <summary>An amber/yellow-tinted-FILL copy for grouped BUILT-IN-parameter cells when Claude-assist is enabled —
    /// these need the Claude definition-swap to apply. Keeps the original (black) text colour.</summary>
    private static CellStyleInfo YellowOf(CellStyleInfo s) => new()
    {
        FontName = s.FontName, TextSize = s.TextSize, Bold = s.Bold, Italic = s.Italic, Underline = s.Underline,
        HAlign = s.HAlign, VAlign = s.VAlign, TextColor = s.TextColor, BackColor = 0xFFF2CC,
        BorderTop = s.BorderTop, BorderBottom = s.BorderBottom, BorderLeft = s.BorderLeft, BorderRight = s.BorderRight,
    };

    /// <summary>A muted tan-grey copy for grouped BUILT-IN-parameter cells when Claude-assist is OFF — the yellow
    /// (Claude) path is unavailable, so they read as non-editable but stay visually distinct from the plain
    /// can't-ever-edit grey (<see cref="GreyOf"/>).</summary>
    private static CellStyleInfo GreyBuiltinOf(CellStyleInfo s) => new()
    {
        FontName = s.FontName, TextSize = s.TextSize, Bold = s.Bold, Italic = s.Italic, Underline = s.Underline,
        HAlign = s.HAlign, VAlign = s.VAlign, TextColor = 0x9A9A9A, BackColor = 0xEDE8D8,
        BorderTop = s.BorderTop, BorderBottom = s.BorderBottom, BorderLeft = s.BorderLeft, BorderRight = s.BorderRight,
    };

    /// <summary>A green-tinted-FILL copy of a cell style for BULK-write cells (type param / group header → many
    /// elements). Keeps the original (black) text colour — only the background is tinted.</summary>
    private static CellStyleInfo GreenOf(CellStyleInfo s) => new()
    {
        FontName = s.FontName, TextSize = s.TextSize, Bold = s.Bold, Italic = s.Italic, Underline = s.Underline,
        HAlign = s.HAlign, VAlign = s.VAlign, TextColor = s.TextColor, BackColor = 0xDDF0DD,
        BorderTop = s.BorderTop, BorderBottom = s.BorderBottom, BorderLeft = s.BorderLeft, BorderRight = s.BorderRight,
    };

    // --- styling -----------------------------------------------------------

    private static ICellStyle GetStyle(IWorkbook wb, Dictionary<string, ICellStyle> cache, CellStyleInfo s, bool xls)
    {
        var key = Sig(s);
        if (cache.TryGetValue(key, out var existing)) return existing;

        if (xls && cache.Count >= HssfStyleCap)
            throw new InvalidOperationException(
                "This schedule has too many distinct cell styles for the legacy .xls format (4,000 limit). " +
                "Please export as .xlsx instead.");

        var font = wb.CreateFont();
        if (!string.IsNullOrEmpty(s.FontName)) font.FontName = s.FontName;
        if (s.TextSize > 0) font.FontHeightInPoints = s.TextSize;
        font.IsBold = s.Bold;
        font.IsItalic = s.Italic;
        if (s.Underline) font.Underline = FontUnderlineType.Single;
        ApplyFontColor(wb, font, s.TextColor);

        var style = wb.CreateCellStyle();
        style.SetFont(font);
        style.Alignment = ToHAlign(s.HAlign);
        style.VerticalAlignment = ToVAlign(s.VAlign);
        style.BorderTop = ToBorder(s.BorderTop);
        style.BorderBottom = ToBorder(s.BorderBottom);
        style.BorderLeft = ToBorder(s.BorderLeft);
        style.BorderRight = ToBorder(s.BorderRight);
        ApplyFill(wb, style, s.BackColor);

        cache[key] = style;
        return style;
    }

    private static string Sig(CellStyleInfo s) =>
        string.Join("|", s.FontName, s.TextSize, s.Bold, s.Italic, s.Underline, s.HAlign, s.VAlign,
            s.TextColor, s.BackColor, s.BorderTop, s.BorderBottom, s.BorderLeft, s.BorderRight);

    private static BorderStyle ToBorder(int w) => w switch
    {
        1 => BorderStyle.Thin,
        2 => BorderStyle.Medium,
        3 => BorderStyle.Thick,
        _ => BorderStyle.None,
    };

    private static void ApplyFontColor(IWorkbook wb, IFont font, int packed)
    {
        if (packed < 0) return;
        byte r = (byte)((packed >> 16) & 0xFF), g = (byte)((packed >> 8) & 0xFF), b = (byte)(packed & 0xFF);
        if (wb is XSSFWorkbook)
            ((XSSFFont)font).SetColor(new XSSFColor(new byte[] { r, g, b }));
        else if (wb is HSSFWorkbook hwb)
        {
            var hc = hwb.GetCustomPalette().FindSimilarColor(r, g, b);
            if (hc != null) font.Color = hc.Indexed;
        }
    }

    private static void ApplyFill(IWorkbook wb, ICellStyle style, int packed)
    {
        if (packed < 0 || packed == 0xFFFFFF) return;
        byte r = (byte)((packed >> 16) & 0xFF), g = (byte)((packed >> 8) & 0xFF), b = (byte)(packed & 0xFF);
        if (wb is XSSFWorkbook)
        {
            ((XSSFCellStyle)style).SetFillForegroundColor(new XSSFColor(new byte[] { r, g, b }));
            style.FillPattern = NPOI.SS.UserModel.FillPattern.SolidForeground;
        }
        else if (wb is HSSFWorkbook hwb)
        {
            var hc = hwb.GetCustomPalette().FindSimilarColor(r, g, b);
            if (hc != null)
            {
                style.FillForegroundColor = hc.Indexed;
                style.FillPattern = NPOI.SS.UserModel.FillPattern.SolidForeground;
            }
        }
    }

    private static HorizontalAlignment ToHAlign(string s) => s switch
    {
        "Center" => HorizontalAlignment.Center,
        "Right" => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Left,
    };

    private static VerticalAlignment ToVAlign(string s) => s switch
    {
        "Middle" => VerticalAlignment.Center,
        "Bottom" => VerticalAlignment.Bottom,
        _ => VerticalAlignment.Top,
    };

    private static string SafeSheetName(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name)
            if ("[]:*?/\\".IndexOf(ch) < 0) sb.Append(ch);
        var s = sb.ToString().Trim();
        if (s.Length > 31) s = s.Substring(0, 31);
        return string.IsNullOrEmpty(s) ? "Schedule" : s;
    }

    private static string UniqueName(string baseName, HashSet<string> used)
    {
        var name = baseName;
        int i = 2;
        while (!used.Add(name))
        {
            var suffix = " (" + i++ + ")";
            name = baseName.Length + suffix.Length > 31
                ? baseName.Substring(0, 31 - suffix.Length) + suffix
                : baseName + suffix;
        }
        return name;
    }

    // --- CSV (display-only, not round-trippable) ---------------------------

    private static void WriteCsvMany(List<ScheduleTable> tables, string path)
    {
        if (tables.Count == 1) { WriteCsv(tables[0], path); return; }

        // CSV can't hold multiple sheets -> one file per schedule.
        var dir = Path.GetDirectoryName(path) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(path);
        foreach (var t in tables)
            WriteCsv(t, Path.Combine(dir, $"{baseName}_{SafeFileName(t.ScheduleName)}.csv"));
    }

    private static void WriteCsv(ScheduleTable table, string path)
    {
        using var sw = new StreamWriter(path, false, new UTF8Encoding(true));
        for (int r = 0; r < table.RowCount; r++)
        {
            var fields = new string[table.ColCount];
            for (int c = 0; c < table.ColCount; c++)
                fields[c] = CsvEscape(table.Cells[r][c].Text);
            sw.WriteLine(string.Join(",", fields));
        }
    }

    private static string CsvEscape(string v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    private static string SafeFileName(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return name;
    }

    // --- cowork_meta -------------------------------------------------------

    private static string BuildMetaJson(List<(ScheduleTable t, string name)> pairs)
    {
        var first = pairs.Count > 0 ? pairs[0].t : new ScheduleTable();
        var meta = new
        {
            tool = "Transom",
            version = 1,
            anchorSentinel = ScheduleReader.AnchorSentinel,
            sourceModel = new { guid = first.SourceModelGuid, title = first.SourceModelTitle },
            sheets = pairs.Select(p => new
            {
                sheetName = p.name,
                scheduleUniqueId = p.t.ScheduleUniqueId,
                scheduleName = p.t.ScheduleName,
                category = p.t.Category,
                roundTrippable = p.t.RoundTrippable,
                anchorColumnHeader = ScheduleReader.AnchorSentinel,
                columns = p.t.Columns.Select(c => new
                {
                    col = c.Col, fieldName = c.FieldName, header = c.Header, parameterId = c.ParameterId,
                    binding = c.Binding, writable = c.Writable, hidden = c.Hidden, specTypeId = c.SpecTypeId,
                }).ToArray(),
                rows = p.t.Rows.Select(r => new
                {
                    excelRow = r.ExcelRow, uniqueId = r.UniqueId, kind = r.Kind, bindings = r.Bindings,
                    instanceIds = r.InstanceIds,
                    groupHeaderEdit = r.GroupHeaderEdit == null ? null : new
                    {
                        col = r.GroupHeaderEdit.Col, parameterId = r.GroupHeaderEdit.ParameterId,
                        fieldName = r.GroupHeaderEdit.FieldName, binding = r.GroupHeaderEdit.Binding,
                        specTypeId = r.GroupHeaderEdit.SpecTypeId, instanceIds = r.GroupHeaderEdit.InstanceIds,
                    },
                }).ToArray(),
                baseline = BuildBaseline(p.t),
            }).ToArray(),
        };
        return System.Text.Json.JsonSerializer.Serialize(meta);
    }

    /// <summary>Exported value per element for writable columns: uniqueId -> (col -> value), for three-way import diff.</summary>
    private static Dictionary<string, Dictionary<string, string>> BuildBaseline(ScheduleTable t)
    {
        var writableCols = t.Columns.Where(c => c.Writable).Select(c => c.Col).ToHashSet();
        var baseline = new Dictionary<string, Dictionary<string, string>>();
        for (int i = 0; i < t.Rows.Count && i < t.RowCount; i++)
        {
            var rm = t.Rows[i];
            // Editable group header: baseline is just its value cell, keyed by the synthetic anchor.
            if (rm.GroupHeaderEdit is { } g && !string.IsNullOrEmpty(rm.UniqueId))
            {
                baseline[rm.UniqueId!] = new Dictionary<string, string>
                {
                    [g.Col.ToString()] = g.Col < t.ColCount ? t.Cells[i][g.Col].Text : "",
                };
                continue;
            }
            if ((rm.Kind != "element" && rm.Kind != "type" && rm.Kind != "group") || string.IsNullOrEmpty(rm.UniqueId)) continue;
            var map = new Dictionary<string, string>();
            foreach (var col in writableCols)
                if (col < t.ColCount)
                    map[col.ToString()] = t.Cells[i][col].Text;
            baseline[rm.UniqueId!] = map;
        }
        return baseline;
    }
}
