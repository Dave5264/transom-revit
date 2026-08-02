using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;
using Transom.Core;

namespace Transom.Office;

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

    /// <param name="outcomes">Optional run-results overlay keyed by (row UniqueId, column ParameterId): Applied
    /// cells render BOLD (keep their fill), Failed/Unverified cells render ITALIC on a red fill. Null = no overlay.</param>
    /// <param name="attempted">Optional attempted-value overlay keyed the same way. For a Failed/Unverified cell
    /// (rejected Option-2 column per §10c, or any failed/unverified write), the cell TEXT is replaced with this
    /// attempted value (what the user typed) rendered italic, instead of the re-read original — so the run-results
    /// report shows exactly what could not be applied. Null/absent = re-read text is kept.</param>
    public void WriteMany(List<ScheduleTable> tables, string path,
        Dictionary<(string uid, int paramId), ApplyOutcome>? outcomes = null,
        Dictionary<(string uid, int paramId), string>? attempted = null)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".csv") { WriteCsvMany(tables, path); return; }

        bool xls = ext == ".xls";
        using IWorkbook wb = xls ? new HSSFWorkbook() : new XSSFWorkbook();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairs = new List<(ScheduleTable t, string name)>();

        // ONE style cache for the whole workbook: styles/fonts allocate into workbook-global tables, so a
        // per-sheet cache re-creates identical styles per sheet (N× bloat) — and the .xls 4,000-style cap
        // is a WORKBOOK limit, so a per-sheet count could sail past it without the guard ever firing.
        var styleCache = new Dictionary<string, ICellStyle>();
        foreach (var table in tables)
        {
            var name = UniqueName(SafeSheetName(table.ScheduleName), used);
            WriteSheet(wb, wb.CreateSheet(name), table, xls, styleCache, outcomes, attempted);
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

    private static void WriteSheet(IWorkbook wb, ISheet sheet, ScheduleTable table, bool xls,
        Dictionary<string, ICellStyle> cache,
        Dictionary<(string uid, int paramId), ApplyOutcome>? outcomes = null,
        Dictionary<(string uid, int paramId), string>? attempted = null)
    {
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

        // Colour change (2026-06-14, ux1): a DISPLAY-ONLY schedule — non-round-trippable AND no row anchors to an
        // element/type (the importer's wholesale-skip case, e.g. AREA SCHEDULE - FUNCTION OF SPACE) — has NO import
        // path for ANY cell, so ALL its data cells export GREY (so the user won't try to edit what can't come back).
        // Mirrors the importer's gate exactly (Importer.cs ~:369 `!sheet.RoundTrippable && !anyAnchored`). This is the
        // ONLY new grey: importable cells (yellow grouped-built-in via option 2/Assist, blue grouped-project via vary,
        // green type/bulk) are by definition absent here (they all require round-trippability or an anchor), so nothing
        // importable is mis-greyed.
        bool anyAnchored = table.Rows.Any(rm => !string.IsNullOrEmpty(rm.UniqueId));
        bool displayOnly = !table.RoundTrippable && !anyAnchored;

        // Top-left cells of grouped super-header merges that sit ENTIRELY in the header band (every spanned row is a
        // columnHeader row). These round-trip via ViewSchedule.GroupHeaders, so they color editable (no fill) — like
        // per-column captions — not grey. (Matches ExcelWriter's HeaderGroupsOf / the importer's GroupHeader path.)
        bool RowIsHeader(int rr) => rr >= 0 && rr < table.Rows.Count && table.Rows[rr].Kind == "columnHeader";
        var superHeaderTopLeft = new HashSet<(int, int)>();
        if (table.RoundTrippable)
            foreach (var m in table.Merges)
            {
                bool allHeader = true;
                for (int rr = m.Top; rr <= m.Bottom; rr++) if (!RowIsHeader(rr)) { allHeader = false; break; }
                if (allHeader && m.Top < table.RowCount && m.Left < table.ColCount
                    && !string.IsNullOrEmpty(table.Cells[m.Top][m.Left].Text))
                    superHeaderTopLeft.Add((m.Top, m.Left));
            }

        for (int r = 0; r < table.RowCount; r++)
        {
            var row = sheet.CreateRow(r + rowOffset);
            var rowKind = r < table.Rows.Count ? table.Rows[r].Kind : null;
            // GREY (read-only / non-importable) and GREEN (type/bulk) hints reflect the PARAMETER's nature, which is
            // true regardless of round-trippability — so they apply even on a non-round-trippable TYPE schedule
            // (e.g. PARTITION TYPE R, whose 2 wall types collapse to one row → can't anchor → RoundTrippable=false,
            // yet its type cells are real type-bulk writes = green, and its read-only Width = grey). Scope the
            // un-gating to type/group rows so an itemized-but-non-round-trippable edge can't get recolored: when
            // RoundTrippable, behaviour is UNCHANGED; when not, only "type"/"group" rows read frozen/bulk.
            // The grouped-INSTANCE colours (blue/yellow/red) need the instance-anchor path, so they stay gated.
            bool colorByParamNature = table.RoundTrippable || rowKind is "type" or "group";
            // Export hint colours: grey = never importable, blue = group-member instance param (Claude-assist only).
            var frozen = colorByParamNature && r < table.Rows.Count ? table.Rows[r].FrozenCols : null;
            var groupProjectCols = table.RoundTrippable && r < table.Rows.Count ? table.Rows[r].GroupProjectCols : null;
            var groupBuiltinCols = table.RoundTrippable && r < table.Rows.Count ? table.Rows[r].GroupBuiltinCols : null;
            var groupBrokenCols = table.RoundTrippable && r < table.Rows.Count ? table.Rows[r].GroupBrokenCols : null;
            // Group-header / blank separator rows carry no element anchor, so editing them does nothing on
            // import. Grey the whole row so it reads as non-editable (matches the per-cell grey hint).
            var ghe = r < table.Rows.Count ? table.Rows[r].GroupHeaderEdit : null;
            var bulk = colorByParamNature && r < table.Rows.Count ? table.Rows[r].BulkCols : null;
            bool nonImportableRow = table.RoundTrippable && rowKind is "groupHeader" or "blank";
            // A real (ShowHeaders) header band: per-column captions round-trip via ScheduleField.ColumnHeading
            // (editable → no fill); the grouped super-header cells + blanks-under-merges don't (grey). Decided
            // per-cell below by matching the cell text to the column's exported heading.
            bool isHeaderRow = table.RoundTrippable && rowKind == "columnHeader";
            for (int c = 0; c < table.ColCount; c++)
            {
                var tc = table.Cells[r][c];
                var cell = row.CreateCell(c);
                cell.SetCellValue(tc.Text);
                // Colour precedence (#94): grey (can't import) > blue (grouped PROJECT/SHARED instance param → option 1
                //          vary) > yellow (grouped built-in DATA param e.g. Comments → resolved via a new parameter) >
                //          red (grouped GEOMETRY-driving built-in e.g. sill/head height → Claude-Assist only; replacing
                //          it would desync the 3D) > green (bulk: edits many elements) > normal. An editable group-header
                //          cell is a bulk write -> green. (#94 split the former blue catch-all into blue/yellow and
                //          repurposed groupBrokenCols as RED for the geometry-driving set — previously folded into yellow.)
                bool isGroupEditCell = ghe != null && c == ghe.Col;
                bool groupBroken = groupBrokenCols != null && groupBrokenCols.Contains(c);
                bool groupBuiltin = groupBuiltinCols != null && groupBuiltinCols.Contains(c);
                // A header-band cell is a round-trippable CAPTION when EITHER its text matches the column's exported
                // heading (per-column caption → ScheduleField.ColumnHeading) OR it's the top-left of a grouped
                // super-header merge (→ ViewSchedule.GroupHeaders). Both are editable on import → no fill. Remaining
                // header cells (merge blanks / non-caption) can't round-trip → grey.
                bool isEditableCaption = isHeaderRow
                    && ((c < table.Columns.Count && !string.IsNullOrEmpty(table.Columns[c].Header) && tc.Text == table.Columns[c].Header)
                        || superHeaderTopLeft.Contains((r, c)));
                // displayOnly (whole schedule can't import) → GREY every data cell, ahead of all other hints. In a
                // display-only table there are no importable (yellow/blue/green) cells to protect, so this can't
                // mis-grey something writable.
                var style = displayOnly ? GreyOf(tc.Style)
                    : isEditableCaption ? tc.Style                 // round-trippable column caption → editable (no fill)
                    : isHeaderRow ? GreyOf(tc.Style)               // non-caption header cell (super-header/merge blank) → grey
                    : isGroupEditCell ? GreenOf(tc.Style)
                    : nonImportableRow ? GreyOf(tc.Style)
                    : frozen != null && frozen.Contains(c) ? GreyOf(tc.Style)
                    : groupProjectCols != null && groupProjectCols.Contains(c) ? BlueOf(tc.Style)
                    : groupBuiltin ? YellowOf(tc.Style)
                    : groupBroken ? RedOf(tc.Style)               // #94: grouped geometry-driving built-in → Claude-Assist only
                    : bulk != null && bulk.Contains(c) ? GreenOf(tc.Style)
                    : tc.Style;

                // Run-results overlay (post-apply): if this cell carried an applied change, mark its outcome —
                // bold (kept fill) for Applied, italic on a red fill for Failed/Unverified.
                if (outcomes != null && r < table.Rows.Count && !string.IsNullOrEmpty(table.Rows[r].UniqueId)
                    && c < table.Columns.Count
                    && outcomes.TryGetValue((table.Rows[r].UniqueId!, table.Columns[c].ParameterId), out var oc))
                {
                    if (oc == ApplyOutcome.Applied) style = BoldOf(style);
                    else if (oc is ApplyOutcome.Failed or ApplyOutcome.Unverified)
                    {
                        style = FailedOf(style);
                        // §10c: for a rejected Option-2 column (source field still present), show the ATTEMPTED
                        // value the user typed — italic, via FailedOf above — instead of the re-read original.
                        if (attempted != null
                            && attempted.TryGetValue((table.Rows[r].UniqueId!, table.Columns[c].ParameterId), out var av))
                            cell.SetCellValue(av);
                    }
                }

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

        // PERF-04: this used to call NPOI's AutoSizeColumn for every column. AutoSizeColumn renders and measures
        // the text of EVERY cell in the column through the font metrics — O(rows × cols) text measurements per
        // sheet, NPOI's well-known slow path, and the reason large exports cost far more than their data volume
        // suggests. The measurement was then immediately clamped into [8, 60] characters, so all that precision
        // bought was a value somewhere inside that band.
        //
        // The replacement is the character-count approximation that used to sit in the `catch`: O(rows × cols)
        // string-LENGTH reads instead of font measurements, orders of magnitude cheaper, and it lands in the
        // same [8, 60] clamp, so the visible outcome is preserved.
        //
        // NOT ScheduleTable.ColWidthsPx. That field holds Revit's own per-column widths
        // (ScheduleReader.SafeWidthPx → TableSectionData.GetColumnWidthInPixels) and is tempting because it is
        // read and then never used — but it is the SCHEDULE VIEW's column widths, which in practice are left at
        // the default. Measured live on a real project (2026-08-01): every body column of an 18-column door
        // schedule reported exactly 96 px, so keying off it produced a workbook whose columns were all the
        // identical 13.7 characters wide. That is a visible regression against content-fitted widths, so the
        // cheap approximation wins on both axes here.
        for (int c = 0; c < table.ColCount; c++)
        {
            int maxLen = 4;
            for (int r = 0; r < table.RowCount; r++)
                maxLen = Math.Max(maxLen, table.Cells[r][c].Text.Length);
            int w = (maxLen + 2) * 256;
            sheet.SetColumnWidth(c, Math.Max(8 * 256, Math.Min(w, 60 * 256)));
        }

        sheet.SetColumnHidden(anchorCol, true);
        // §17: hide the appended combined-field COMPONENT columns (they exist for the import fallback; the user edits
        // the visible combined column). A user can unhide + edit a component column directly — it still imports.
        foreach (var col in table.Columns)
            if (col.Hidden && col.Col >= 0 && col.Col < anchorCol)
                sheet.SetColumnHidden(col.Col, true);
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

    /// <summary>An amber/yellow-tinted-FILL copy for grouped BUILT-IN-parameter cells in a SIMPLE group — they can't
    /// be written in place; resolve via option 2 (a new type parameter, if values are uniform per type) or
    /// Claude-Assist. Keeps the original (black) text colour. (PLACEHOLDER meaning — ux1 owns the final color
    /// contract + wording, Task #41: keep yellow distinct vs fold into red/red-grey.)</summary>
    private static CellStyleInfo YellowOf(CellStyleInfo s) => new()
    {
        FontName = s.FontName, TextSize = s.TextSize, Bold = s.Bold, Italic = s.Italic, Underline = s.Underline,
        HAlign = s.HAlign, VAlign = s.VAlign, TextColor = s.TextColor, BackColor = 0xFFF2CC,
        BorderTop = s.BorderTop, BorderBottom = s.BorderBottom, BorderLeft = s.BorderLeft, BorderRight = s.BorderRight,
    };

    /// <summary>A red-tinted-FILL copy for grouped BUILT-IN-parameter cells whose model group is BROKEN (a member
    /// anchored outside the group, a nested group, or mixed instance orientation), so the edit must use option 2
    /// (a new type parameter) or Claude-Assist. Keeps the original (black) text colour.</summary>
    private static CellStyleInfo RedOf(CellStyleInfo s) => new()
    {
        FontName = s.FontName, TextSize = s.TextSize, Bold = s.Bold, Italic = s.Italic, Underline = s.Underline,
        HAlign = s.HAlign, VAlign = s.VAlign, TextColor = s.TextColor, BackColor = 0xF8D2D5,
        BorderTop = s.BorderTop, BorderBottom = s.BorderBottom, BorderLeft = s.BorderLeft, BorderRight = s.BorderRight,
    };

    // RedGreyOf (red fill + grey text for a broken-group built-in with Claude-Assist OFF) was defined here
    // and never called — the style-precedence chain has no Assist-enabled branch that could reach it, so
    // its doc comment described a user-visible colour contract the export does not implement. Removed
    // rather than left as a promise; re-add it together with the branch that selects it.

    /// <summary>A green-tinted-FILL copy of a cell style for BULK-write cells (type param / group header → many
    /// elements). Keeps the original (black) text colour — only the background is tinted.</summary>
    private static CellStyleInfo GreenOf(CellStyleInfo s) => new()
    {
        FontName = s.FontName, TextSize = s.TextSize, Bold = s.Bold, Italic = s.Italic, Underline = s.Underline,
        HAlign = s.HAlign, VAlign = s.VAlign, TextColor = s.TextColor, BackColor = 0xDDF0DD,
        BorderTop = s.BorderTop, BorderBottom = s.BorderBottom, BorderLeft = s.BorderLeft, BorderRight = s.BorderRight,
    };

    /// <summary>A BOLD copy of a cell style for run-results cells that APPLIED — keeps the cell's existing fill
    /// (the hint colour from the export pass) and only turns the text bold.</summary>
    private static CellStyleInfo BoldOf(CellStyleInfo s) => new()
    {
        FontName = s.FontName, TextSize = s.TextSize, Bold = true, Italic = s.Italic, Underline = s.Underline,
        HAlign = s.HAlign, VAlign = s.VAlign, TextColor = s.TextColor, BackColor = s.BackColor,
        BorderTop = s.BorderTop, BorderBottom = s.BorderBottom, BorderLeft = s.BorderLeft, BorderRight = s.BorderRight,
    };

    /// <summary>An ITALIC, red-FILL copy of a cell style for run-results cells that FAILED or stayed UNVERIFIED —
    /// keeps the original text colour, turns the text italic, and tints the background red (0xF8D2D5).</summary>
    private static CellStyleInfo FailedOf(CellStyleInfo s) => new()
    {
        FontName = s.FontName, TextSize = s.TextSize, Bold = s.Bold, Italic = true, Underline = s.Underline,
        HAlign = s.HAlign, VAlign = s.VAlign, TextColor = s.TextColor, BackColor = 0xF8D2D5,
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
            sourceModel = new { guid = first.SourceModelGuid, title = first.SourceModelTitle, path = first.SourceModelPath },
            sheets = pairs.Select(p => new
            {
                sheetName = p.name,
                scheduleUniqueId = p.t.ScheduleUniqueId,
                scheduleName = p.t.ScheduleName,
                category = p.t.Category,
                roundTrippable = p.t.RoundTrippable,
                anchorColumnHeader = ScheduleReader.AnchorSentinel,
                // The workbook row holding the PER-COLUMN captions (= each column's ColumnHeading). For a multi-row
                // header (super-header band over the caption row) this is NOT row 0 — the importer must read column
                // captions from here, not the rendered top row (which carries super-headers). -1 = no real header row.
                captionRowExcel = CaptionRowExcel(p.t),
                columns = p.t.Columns.Select(c => new
                {
                    col = c.Col, fieldName = c.FieldName, header = c.Header, parameterId = c.ParameterId,
                    binding = c.Binding, writable = c.Writable, hidden = c.Hidden, specTypeId = c.SpecTypeId,
                    // §17: combined-parameter template (null for normal columns) — the ordered component parts the
                    // importer needs to parse an edited combined cell back into its component params.
                    combinedParts = c.CombinedParts?.Select(cp => new
                    {
                        paramId = cp.ParamId, prefix = cp.Prefix, suffix = cp.Suffix,
                        separator = cp.Separator, binding = cp.Binding, specTypeId = cp.SpecTypeId,
                    }).ToArray(),
                }).ToArray(),
                // excelRow is emitted as the ACTUAL SHEET ROW (table index + the synth-header offset), NOT the table
                // index — so the import side can key per-row metadata by it and match the worksheet row it reads from.
                // rowOffset mirrors WriteSheet's `synth ? 1 : 0` (headers-off / empty schedule shifts the body down 1).
                rows = p.t.Rows.Select(r => new
                {
                    excelRow = r.ExcelRow + ((!p.t.HasHeaderRow || p.t.RowCount == 0) ? 1 : 0),
                    uniqueId = r.UniqueId, kind = r.Kind, bindings = r.Bindings,
                    instanceIds = r.InstanceIds, aggregatedTypeUids = r.AggregatedTypeUids,
                    groupHeaderEdit = r.GroupHeaderEdit == null ? null : new
                    {
                        col = r.GroupHeaderEdit.Col, parameterId = r.GroupHeaderEdit.ParameterId,
                        fieldName = r.GroupHeaderEdit.FieldName, binding = r.GroupHeaderEdit.Binding,
                        specTypeId = r.GroupHeaderEdit.SpecTypeId, instanceIds = r.GroupHeaderEdit.InstanceIds,
                    },
                }).ToArray(),
                // Grouped super-headers (merged header-band cells, e.g. "DOOR"/"FRAME" spanning columns). Each is
                // round-trippable via ViewSchedule.GroupHeaders(top,left,bottom,right,caption). Captured by Body-section
                // rectangle + caption so the importer can detect a renamed super-header and re-apply it. Only merges
                // fully inside the header band (every spanned row is a columnHeader row) qualify.
                headerGroups = HeaderGroupsOf(p.t),
                baseline = BuildBaseline(p.t),
            }).ToArray(),
        };
        return System.Text.Json.JsonSerializer.Serialize(meta);
    }

    /// <summary>The workbook row index (0-based) of the PER-COLUMN caption row — the LAST 'columnHeader' row in the
    /// table (a multi-row header has super-header rows above it). The exporter writes rows 1:1 to the workbook only
    /// when there's a real header band (synth adds a row, but synth = headers-off = no caption row), so the table
    /// index equals the workbook index here. Returns -1 when there's no real header row (the round-trip skips it).</summary>
    private static int CaptionRowExcel(ScheduleTable t)
    {
        if (!t.HasHeaderRow) return -1;
        int last = -1;
        for (int r = 0; r < t.Rows.Count; r++)
            if (t.Rows[r].Kind == "columnHeader") last = r;
        return last;
    }

    /// <summary>The grouped super-headers (merged header-band cells) of a table, as rectangles + caption text, for
    /// the header round-trip. A merge qualifies when EVERY row it spans is a 'columnHeader' row (i.e. it's in the
    /// leading header band, not a merged data/group cell). Caption is read from the merge's top-left cell.</summary>
    private static object[] HeaderGroupsOf(ScheduleTable t)
    {
        bool IsHeaderRow(int r) => r >= 0 && r < t.Rows.Count && t.Rows[r].Kind == "columnHeader";
        var groups = new List<object>();
        foreach (var m in t.Merges)
        {
            bool allHeader = true;
            for (int r = m.Top; r <= m.Bottom; r++)
                if (!IsHeaderRow(r)) { allHeader = false; break; }
            if (!allHeader) continue;
            string caption = m.Top < t.RowCount && m.Left < t.ColCount ? t.Cells[m.Top][m.Left].Text : "";
            if (string.IsNullOrEmpty(caption)) continue;   // empty merge = nothing to round-trip
            groups.Add(new { top = m.Top, left = m.Left, bottom = m.Bottom, right = m.Right, caption });
        }
        return groups.ToArray();
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
            // Key per RENDERED ROW (not per type uid): a type/group schedule renders many instances under one type
            // anchor, so the bare type uid collides across rows and loses each row's per-instance snapshot. The
            // import side looks this up with the SAME helper. (ScheduleReader.BaselineRowKey.)
            baseline[ScheduleReader.BaselineRowKey(rm.UniqueId, rm.Kind, rm.InstanceIds)] = map;
        }
        return baseline;
    }
}
