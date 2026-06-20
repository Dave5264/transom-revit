using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Transom.Core;

/// <summary>A user-supplied replacement for a cell that failed to parse on a previous preview.</summary>
public sealed class CellCorrection
{
    public string SheetTabName = "";
    public int ExcelRow;
    public int ExcelCol;
    public string NewValue = "";
}

/// <summary>A parseable entry that isn't in the schedule's expected unit format — show the canonical form for confirmation.</summary>
public sealed class ReformatSuggestion
{
    public string SheetTabName = "";
    public int ExcelRow;
    public int ExcelCol;
    public string FieldName = "";
    public string ElementLabel = "";
    public string Entered = "";
    public string Canonical = "";
}

public sealed class CorrectionResult
{
    public List<CellCorrection> Applied = new();
    public List<ReformatSuggestion> Reformats = new();
}

/// <summary>
///     Applies user corrections to an imported workbook IN MEMORY only. Each replacement is parsed against its
///     column's spec (Revit's unit parser); a value already in the schedule's expected format is applied to the
///     in-session import data, while one that parses but differs (e.g. "42" → "42' - 0\"") is returned as a
///     reformat suggestion for the user to confirm. Unparseable replacements are left to resurface.
///     The user's workbook FILE is never modified — corrections live only for this import (and are recorded in the
///     run report if one is produced); we cannot change the user's input without their confirmation.
/// </summary>
public static class ExcelCorrector
{
    public static CorrectionResult Apply(ImportWorkbook wb, List<CellCorrection> corrections, Units units)
    {
        var result = new CorrectionResult();

        foreach (var c in corrections)
        {
            var sheet = wb.Sheets.FirstOrDefault(s => s.SheetTabName == c.SheetTabName);
            var col = sheet?.Columns.FirstOrDefault(k => k.ExcelCol == c.ExcelCol);
            if (sheet == null || col == null) continue;
            var row = sheet.Rows.FirstOrDefault(r => r.ExcelRow == c.ExcelRow);

            // String columns never reach the fix pane (strings always parse); accept defensively.
            if (col.SpecTypeId == null)
            {
                result.Applied.Add(c);
                SetCell(row, c.ExcelCol, c.NewValue);
                continue;
            }

            var spec = new ForgeTypeId(col.SpecTypeId);
            if (!UnitFormatUtils.TryParse(units, spec, c.NewValue, out double val))
                continue; // unparseable -> leave; BuildChangeSet re-flags it

            var canonical = Canonical(units, spec, val, c.NewValue);

            if (SameFormat(c.NewValue, canonical))
            {
                // Already in the expected format -> accept for this import (canonical drives the in-memory cell).
                var applied = new CellCorrection
                {
                    SheetTabName = c.SheetTabName, ExcelRow = c.ExcelRow, ExcelCol = c.ExcelCol, NewValue = canonical,
                };
                result.Applied.Add(applied);
                SetCell(row, c.ExcelCol, canonical);
            }
            else
            {
                // Parses, but not in the schedule's format -> ask the user to confirm the reformatted value.
                result.Reformats.Add(new ReformatSuggestion
                {
                    SheetTabName = c.SheetTabName, ExcelRow = c.ExcelRow, ExcelCol = c.ExcelCol,
                    FieldName = col.FieldName,
                    ElementLabel = row?.Cells.FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "",
                    Entered = c.NewValue, Canonical = canonical,
                });
            }
        }

        return result;
    }

    /// <summary>The schedule's display format for a value; falls back to the editable form if that doesn't round-trip.</summary>
    public static string Canonical(Units units, ForgeTypeId spec, double value, string fallback)
    {
        try
        {
            var display = UnitFormatUtils.Format(units, spec, value, false);
            // Guard against a display string that won't re-parse (would cause a confirm loop).
            if (UnitFormatUtils.TryParse(units, spec, display, out _)) return display;
            return UnitFormatUtils.Format(units, spec, value, true);
        }
        catch { return fallback; }
    }

    public static bool SameFormat(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void SetCell(ImportRow? row, int col, string value)
    {
        if (row != null && col >= 0 && col < row.Cells.Length) row.Cells[col] = value;
    }
}
