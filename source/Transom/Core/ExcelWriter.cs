using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;

namespace Transom.Core;

/// <summary>
///     Writes a <see cref="ScheduleTable"/> to a styled .xlsx via NPOI: visible grid (fonts, colors,
///     alignment, borders, merges, widths) + a hidden sentinel-headed UniqueId anchor column +
///     a hidden cowork_meta sheet carrying the round-trip metadata.
/// </summary>
public sealed class ExcelWriter
{
    public void Write(ScheduleTable table, string path)
    {
        var wb = new XSSFWorkbook();
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
                cell.CellStyle = GetStyle(wb, cache, tc.Style);
            }

            // hidden anchor column: sentinel header in row 0, UniqueId on element rows
            var anchorCell = row.CreateCell(anchorCol);
            if (r == 0)
                anchorCell.SetCellValue(ScheduleReader.AnchorSentinel);
            else if (r < table.Rows.Count && !string.IsNullOrEmpty(table.Rows[r].UniqueId))
                anchorCell.SetCellValue(table.Rows[r].UniqueId);
        }

        foreach (var m in table.Merges)
            sheet.AddMergedRegion(new CellRangeAddress(m.Top, m.Bottom, m.Left, m.Right));

        for (int c = 0; c < table.ColCount; c++)
            sheet.SetColumnWidth(c, PxToWidthUnits(table.ColWidthsPx[c]));

        sheet.SetColumnHidden(anchorCol, true);

        var meta = wb.CreateSheet("cowork_meta");
        meta.CreateRow(0).CreateCell(0).SetCellValue(BuildMetaJson(table));
        wb.SetSheetHidden(wb.GetSheetIndex(meta), SheetState.VeryHidden);

        using var fs = File.Create(path);
        wb.Write(fs);
    }

    // --- styling -----------------------------------------------------------

    private static ICellStyle GetStyle(XSSFWorkbook wb, Dictionary<string, ICellStyle> cache, CellStyleInfo s)
    {
        var key = Sig(s);
        if (cache.TryGetValue(key, out var existing)) return existing;

        var font = (XSSFFont)wb.CreateFont();
        if (!string.IsNullOrEmpty(s.FontName)) font.FontName = s.FontName;
        if (s.TextSize > 0) font.FontHeightInPoints = s.TextSize;
        font.IsBold = s.Bold;
        font.IsItalic = s.Italic;
        if (s.Underline) font.Underline = FontUnderlineType.Single;
        if (s.TextColor >= 0) font.SetColor(Rgb(s.TextColor));

        var style = (XSSFCellStyle)wb.CreateCellStyle();
        style.SetFont(font);
        style.Alignment = ToHAlign(s.HAlign);
        style.VerticalAlignment = ToVAlign(s.VAlign);
        style.BorderTop = BorderStyle.Thin;
        style.BorderBottom = BorderStyle.Thin;
        style.BorderLeft = BorderStyle.Thin;
        style.BorderRight = BorderStyle.Thin;
        if (s.BackColor >= 0 && s.BackColor != 0xFFFFFF)
        {
            style.SetFillForegroundColor(Rgb(s.BackColor));
            style.FillPattern = NPOI.SS.UserModel.FillPattern.SolidForeground;
        }

        cache[key] = style;
        return style;
    }

    private static string Sig(CellStyleInfo s) =>
        string.Join("|", s.FontName, s.TextSize, s.Bold, s.Italic, s.Underline,
            s.HAlign, s.VAlign, s.TextColor, s.BackColor);

    private static XSSFColor Rgb(int packed)
    {
        byte r = (byte)((packed >> 16) & 0xFF);
        byte g = (byte)((packed >> 8) & 0xFF);
        byte b = (byte)(packed & 0xFF);
        return new XSSFColor(new byte[] { r, g, b });
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

    private static int PxToWidthUnits(int px)
    {
        int units = (int)(px / 7.0 * 256); // ~7px per character
        return Math.Max(256, Math.Min(units, 255 * 256));
    }

    private static string SafeSheetName(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name)
            if ("[]:*?/\\".IndexOf(ch) < 0) sb.Append(ch);
        var s = sb.ToString().Trim();
        if (s.Length > 31) s = s.Substring(0, 31);
        return string.IsNullOrEmpty(s) ? "Schedule" : s;
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
