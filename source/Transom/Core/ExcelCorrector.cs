using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using NPOI.SS.UserModel;

namespace Transom.Core;

/// <summary>A user-supplied replacement for a cell that failed to parse on a previous preview.</summary>
public sealed class CellCorrection
{
    public string SheetTabName = "";
    public int ExcelRow;
    public int ExcelCol;
    public string NewValue = "";
}

/// <summary>
///     Applies user corrections to an imported workbook: validates each replacement against its column's
///     spec (Revit's unit parser), and for the ones that parse, updates the in-memory rows AND writes the
///     value back into the .xlsx so the source file is corrected. Unparseable replacements are left alone
///     (they resurface in the next preview).
/// </summary>
public static class ExcelCorrector
{
    /// <summary>Returns the corrections that validated and were applied.</summary>
    public static List<CellCorrection> Apply(string path, ImportWorkbook wb, List<CellCorrection> corrections, Units units)
    {
        var valid = new List<CellCorrection>();
        foreach (var c in corrections)
        {
            var sheet = wb.Sheets.FirstOrDefault(s => s.SheetTabName == c.SheetTabName);
            var col = sheet?.Columns.FirstOrDefault(k => k.ExcelCol == c.ExcelCol);
            if (sheet == null || col == null) continue;

            // String columns can't be unparseable; only spec'd (numeric) columns reach the fix pane.
            bool parses = col.SpecTypeId == null
                || UnitFormatUtils.TryParse(units, new ForgeTypeId(col.SpecTypeId), c.NewValue, out _);
            if (!parses) continue;

            valid.Add(c);
            var row = sheet.Rows.FirstOrDefault(r => r.ExcelRow == c.ExcelRow);
            if (row != null && c.ExcelCol >= 0 && c.ExcelCol < row.Cells.Length)
                row.Cells[c.ExcelCol] = c.NewValue;
        }

        if (valid.Count > 0) WriteBack(path, valid);
        return valid;
    }

    /// <summary>Persists the validated corrections into the workbook file (best-effort; no-op if the file is locked).</summary>
    private static void WriteBack(string path, List<CellCorrection> valid)
    {
        try
        {
            IWorkbook book;
            using (var fs = File.OpenRead(path)) book = WorkbookFactory.Create(fs);

            foreach (var c in valid)
            {
                var ws = book.GetSheet(c.SheetTabName);
                if (ws == null) continue;
                var row = ws.GetRow(c.ExcelRow) ?? ws.CreateRow(c.ExcelRow);
                var cell = row.GetCell(c.ExcelCol) ?? row.CreateCell(c.ExcelCol);
                cell.SetCellValue(c.NewValue);
            }

            using (var fs = File.Create(path)) book.Write(fs);
        }
        catch { /* file may be open in Excel; in-memory correction still drives this preview */ }
    }
}
