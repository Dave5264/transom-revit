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
        meta.CreateRow(0).CreateCell(0).SetCellValue(BuildMetaJson(pairs));
        wb.SetSheetHidden(wb.GetSheetIndex(meta), SheetState.VeryHidden);

        using var fs = File.Create(path);
        wb.Write(fs);
    }

    private static void WriteSheet(IWorkbook wb, ISheet sheet, ScheduleTable table, bool xls)
    {
        var cache = new Dictionary<string, ICellStyle>();
        int anchorCol = table.ColCount;

        for (int r = 0; r < table.RowCount; r++)
        {
            var row = sheet.CreateRow(r);
            for (int c = 0; c < table.ColCount; c++)
            {
                var tc = table.Cells[r][c];
                var cell = row.CreateCell(c);
                cell.SetCellValue(tc.Text);
                cell.CellStyle = GetStyle(wb, cache, tc.Style, xls);
            }

            var anchorCell = row.CreateCell(anchorCol);
            if (r == 0)
                anchorCell.SetCellValue(ScheduleReader.AnchorSentinel);
            else if (r < table.Rows.Count && !string.IsNullOrEmpty(table.Rows[r].UniqueId))
                anchorCell.SetCellValue(table.Rows[r].UniqueId);
        }

        foreach (var m in table.Merges)
            sheet.AddMergedRegion(new CellRangeAddress(m.Top, m.Bottom, m.Left, m.Right));

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
                    excelRow = r.ExcelRow, uniqueId = r.UniqueId, kind = r.Kind,
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
            if (rm.Kind != "element" || string.IsNullOrEmpty(rm.UniqueId)) continue;
            var map = new Dictionary<string, string>();
            foreach (var col in writableCols)
                if (col < t.ColCount)
                    map[col.ToString()] = t.Cells[i][col].Text;
            baseline[rm.UniqueId!] = map;
        }
        return baseline;
    }
}
