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
///     Writes a <see cref="ScheduleTable"/> to .xlsx (XSSF), legacy .xls (HSSF), or .csv (display-only).
///     Reproduces fonts, colors, alignment, per-side borders, merges, and fitted column widths, plus a
///     hidden sentinel-headed UniqueId anchor column and a hidden cowork_meta sheet (xlsx/xls only).
/// </summary>
public sealed class ExcelWriter
{
    private const int HssfStyleCap = 4000; // .xls hard limit on distinct cell styles

    public void Write(ScheduleTable table, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".csv") { WriteCsv(table, path); return; }
        WriteWorkbook(table, path, xls: ext == ".xls");
    }

    private static void WriteWorkbook(ScheduleTable table, string path, bool xls)
    {
        IWorkbook wb = xls ? new HSSFWorkbook() : new XSSFWorkbook();
        var sheet = wb.CreateSheet(SafeSheetName(table.ScheduleName));
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

        // Size columns to fit their text at Excel's normal scale (Revit's paper widths are tiny).
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

        var meta = wb.CreateSheet("cowork_meta");
        meta.CreateRow(0).CreateCell(0).SetCellValue(BuildMetaJson(table));
        wb.SetSheetHidden(wb.GetSheetIndex(meta), SheetState.VeryHidden);

        using var fs = File.Create(path);
        wb.Write(fs);
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
        if (packed < 0 || packed == 0xFFFFFF) return; // skip white / none
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

    // --- CSV (display-only, not round-trippable) ---------------------------

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

    // --- cowork_meta -------------------------------------------------------

    private static string BuildMetaJson(ScheduleTable t)
    {
        var meta = new
        {
            tool = "Transom",
            version = 1,
            anchorSentinel = ScheduleReader.AnchorSentinel,
            sourceModel = new { guid = t.SourceModelGuid, title = t.SourceModelTitle },
            sheets = new[]
            {
                new
                {
                    sheetName = t.ScheduleName,
                    scheduleUniqueId = t.ScheduleUniqueId,
                    scheduleName = t.ScheduleName,
                    roundTrippable = t.RoundTrippable,
                    anchorColumnHeader = ScheduleReader.AnchorSentinel,
                    columns = t.Columns.Select(c => new
                    {
                        col = c.Col, fieldName = c.FieldName, parameterId = c.ParameterId,
                        binding = c.Binding, writable = c.Writable, specTypeId = c.SpecTypeId,
                    }).ToArray(),
                    rows = t.Rows.Select(r => new
                    {
                        excelRow = r.ExcelRow, uniqueId = r.UniqueId, kind = r.Kind,
                    }).ToArray(),
                },
            },
        };
        return System.Text.Json.JsonSerializer.Serialize(meta);
    }
}
