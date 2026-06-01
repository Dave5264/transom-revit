using System.Collections.Generic;
using System.IO;
using System.Linq;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace Transom.Core;

/// <summary>
///     Writes a colour-coded import report: per schedule, the element rows with flagged cells filled
///     yellow ("changed since export") or red ("unable to write"), plus a Legend and an Issues list.
/// </summary>
public static class DiagnosticsWriter
{
    public static void Write(ImportWorkbook wb, List<CellDiagnostic> diags, string path)
    {
        var book = new XSSFWorkbook();
        var yellow = Fill(book, 255, 235, 156);
        var red = Fill(book, 255, 199, 199);
        var header = HeaderStyle(book);

        var legend = book.CreateSheet("Legend");
        legend.CreateRow(0).CreateCell(0).SetCellValue("Transom import report — flagged cells");
        var ly = legend.CreateRow(2);
        ly.CreateCell(0).SetCellValue("changed since export");
        ly.GetCell(0).CellStyle = yellow;
        var lr = legend.CreateRow(3);
        lr.CreateCell(0).SetCellValue("unable to write");
        lr.GetCell(0).CellStyle = red;
        legend.SetColumnWidth(0, 40 * 256);

        var usedNames = new HashSet<string> { "Legend", "Issues" };
        foreach (var sheet in wb.Sheets)
        {
            var ws = book.CreateSheet(SafeName(sheet.SheetTabName, usedNames));
            var cols = sheet.Columns.OrderBy(c => c.Col).ToList();
            var colPos = new Dictionary<int, int>();
            for (int i = 0; i < cols.Count; i++) colPos[cols[i].Col] = i;

            var h = ws.CreateRow(0);
            for (int i = 0; i < cols.Count; i++)
            {
                var c = h.CreateCell(i);
                c.SetCellValue(cols[i].FieldName);
                c.CellStyle = header;
            }

            var rowPos = new Dictionary<int, int>();
            int rr = 1;
            foreach (var row in sheet.Rows)
            {
                rowPos[row.ExcelRow] = rr;
                var wr = ws.CreateRow(rr);
                for (int i = 0; i < cols.Count; i++)
                {
                    var v = cols[i].Col < row.Cells.Length ? row.Cells[cols[i].Col] : "";
                    wr.CreateCell(i).SetCellValue(v);
                }
                rr++;
            }

            foreach (var d in diags.Where(x => x.SheetTabName == sheet.SheetTabName))
                if (rowPos.TryGetValue(d.ExcelRow, out var ri) && colPos.TryGetValue(d.Col, out var ci))
                {
                    var cell = ws.GetRow(ri)?.GetCell(ci);
                    if (cell != null) cell.CellStyle = d.Severity == "yellow" ? yellow : red;
                }

            for (int i = 0; i < cols.Count; i++) ws.AutoSizeColumn(i);
        }

        if (diags.Count > 0)
        {
            var iss = book.CreateSheet("Issues");
            var ih = iss.CreateRow(0);
            var heads = new[] { "Schedule", "Element", "Field", "Value", "Issue" };
            for (int i = 0; i < heads.Length; i++)
            {
                var c = ih.CreateCell(i);
                c.SetCellValue(heads[i]);
                c.CellStyle = header;
            }
            int r = 1;
            foreach (var d in diags)
            {
                var wr = iss.CreateRow(r++);
                wr.CreateCell(0).SetCellValue(d.SheetTabName);
                wr.CreateCell(1).SetCellValue(d.ElementLabel);
                wr.CreateCell(2).SetCellValue(d.FieldName);
                wr.CreateCell(3).SetCellValue(d.Value);
                var rc = wr.CreateCell(4);
                rc.SetCellValue(d.Reason);
                rc.CellStyle = d.Severity == "yellow" ? yellow : red;
            }
            for (int i = 0; i < heads.Length; i++) iss.AutoSizeColumn(i);
        }

        using var fs = File.Create(path);
        book.Write(fs);
    }

    private static ICellStyle Fill(XSSFWorkbook b, int r, int g, int bl)
    {
        var s = (XSSFCellStyle)b.CreateCellStyle();
        s.SetFillForegroundColor(new XSSFColor(new byte[] { (byte)r, (byte)g, (byte)bl }));
        s.FillPattern = NPOI.SS.UserModel.FillPattern.SolidForeground;
        s.BorderTop = BorderStyle.Thin;
        s.BorderBottom = BorderStyle.Thin;
        s.BorderLeft = BorderStyle.Thin;
        s.BorderRight = BorderStyle.Thin;
        return s;
    }

    private static ICellStyle HeaderStyle(XSSFWorkbook b)
    {
        var s = b.CreateCellStyle();
        var f = b.CreateFont();
        f.IsBold = true;
        s.SetFont(f);
        return s;
    }

    private static string SafeName(string name, HashSet<string> used)
    {
        foreach (var ch in "[]:*?/\\") name = name.Replace(ch, '_');
        if (name.Length > 28) name = name.Substring(0, 28);
        var n = name;
        int i = 2;
        while (!used.Add(n)) n = $"{name} {i++}";
        return n;
    }
}
