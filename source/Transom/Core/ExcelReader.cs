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

    /// <summary>§17: for a COMBINED column, the ordered component parts (from cowork_meta combinedParts). Non-null =
    /// route this column through the fail-closed parse→distribute path on import, NOT GetParam (it has no single
    /// parameter). The component params also appear as their own (hidden) ImportColumns → import directly as fallback.</summary>
    public List<CombinedPart>? CombinedParts;

    // Resolved against the current sheet. ID-PRIMARY matching (2026-06-14): paramId is the column's identity; the
    // physical column is located by header text (reorder-safe), with same-header groups placed by exported position
    // so distinct-paramId duplicates each land on their own column. The WRITE keys on ParameterId. A COMBINED column
    // (no single paramId) is matched by header/position and is exempt from id-primary.
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
    /// <summary>Workbook row (0-based) holding the per-column captions (each column's ColumnHeading). For a multi-row
    /// header this is BELOW the super-header row, so it differs from row 0 (where CurrentHeaders is read). -1 = none.</summary>
    public int CaptionRowExcel = -1;
    /// <summary>Current per-column caption text read from <see cref="CaptionRowExcel"/> — used by the column-caption
    /// rename round-trip (NOT row 0, which carries super-headers on a multi-band header). Empty when no caption row.</summary>
    public string[] CurrentCaptions = System.Array.Empty<string>();
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

    /// <summary>Grouped super-headers (merged header-band cells) from cowork_meta: each carries its Body-section
    /// rectangle + the exported caption. The importer compares the workbook's current text at the rectangle's
    /// top-left to detect a renamed super-header and re-apply it via ViewSchedule.GroupHeaders.</summary>
    public List<ImportHeaderGroup> HeaderGroups = new();
}

/// <summary>An exported grouped super-header: its Body-section rectangle + the caption at export time, plus the
/// caption currently in the workbook (read on import) so the diff can detect a rename.</summary>
public sealed class ImportHeaderGroup
{
    public int Top, Left, Bottom, Right;
    public string Caption = "";        // at export time (from cowork_meta)
    public string CurrentCaption = ""; // in the workbook now (read in ReadRows)
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
    /// <summary>Full read of a Transom workbook (every sheet) — back-compat entry point.</summary>
    public ImportWorkbook Read(string path) => Read(path, null);

    /// <summary>§16 pre-analysis tab picker: when <paramref name="selectedSheetTabs"/> is non-null, only sheets whose
    /// tab name is in the set are parsed + ReadRows'd (the rest are skipped entirely, not added to result.Sheets), so
    /// the downstream model diff (BuildChangeSet) only ever sees the selected tabs. Null = full read (every sheet).
    /// The cowork_meta parse is the same; only the expensive per-sheet row-metadata + ReadRows is gated.</summary>
    public ImportWorkbook Read(string path, ISet<string>? selectedSheetTabs)
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
            // §16: in scoped mode skip an unselected tab BEFORE parsing its row metadata / ReadRows (the expensive part).
            var tabName = sheetMeta.GetProperty("sheetName").GetString() ?? "";
            if (selectedSheetTabs != null && !selectedSheetTabs.Contains(tabName)) continue;

            var imp = new ImportSheet
            {
                ScheduleUniqueId = sheetMeta.GetProperty("scheduleUniqueId").GetString() ?? "",
                ScheduleName = sheetMeta.GetProperty("scheduleName").GetString() ?? "",
                SheetTabName = tabName,
                Category = sheetMeta.TryGetProperty("category", out var cat) ? cat.GetInt32() : -1,
                RoundTrippable = sheetMeta.TryGetProperty("roundTrippable", out var rt) && rt.GetBoolean(),
            };

            foreach (var col in sheetMeta.GetProperty("columns").EnumerateArray())
            {
                var ic = new ImportColumn
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
                };
                // §17: combined-parameter template (absent/null for normal columns).
                if (col.TryGetProperty("combinedParts", out var cps) && cps.ValueKind == JsonValueKind.Array)
                {
                    var parts = new List<CombinedPart>();
                    foreach (var cp in cps.EnumerateArray())
                        parts.Add(new CombinedPart
                        {
                            ParamId = cp.GetProperty("paramId").GetInt32(),
                            Prefix = cp.TryGetProperty("prefix", out var pf) ? pf.GetString() ?? "" : "",
                            Suffix = cp.TryGetProperty("suffix", out var sf) ? sf.GetString() ?? "" : "",
                            Separator = cp.TryGetProperty("separator", out var sp) ? sp.GetString() ?? "" : "",
                            Binding = cp.TryGetProperty("binding", out var bd) ? bd.GetString() ?? "instance" : "instance",
                            SpecTypeId = cp.TryGetProperty("specTypeId", out var st) && st.ValueKind == JsonValueKind.String
                                ? st.GetString() : null,
                        });
                    if (parts.Count > 0) ic.CombinedParts = parts;
                }
                imp.Columns.Add(ic);
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

            if (sheetMeta.TryGetProperty("captionRowExcel", out var crEl) && crEl.ValueKind == JsonValueKind.Number)
                imp.CaptionRowExcel = crEl.GetInt32();

            // Grouped super-headers (merged header-band cells) — rectangle + exported caption, for header round-trip.
            if (sheetMeta.TryGetProperty("headerGroups", out var hgEl) && hgEl.ValueKind == JsonValueKind.Array)
                foreach (var hg in hgEl.EnumerateArray())
                    imp.HeaderGroups.Add(new ImportHeaderGroup
                    {
                        Top = hg.GetProperty("top").GetInt32(),
                        Left = hg.GetProperty("left").GetInt32(),
                        Bottom = hg.GetProperty("bottom").GetInt32(),
                        Right = hg.GetProperty("right").GetInt32(),
                        Caption = hg.TryGetProperty("caption", out var cap) ? cap.GetString() ?? "" : "",
                    });

            var ws = wb.GetSheet(imp.SheetTabName);
            if (ws != null)
                ReadRows(ws, imp);

            result.Sheets.Add(imp);
        }

        return result;
    }

    /// <summary>§16 pre-analysis tab picker, PHASE 1 (cheap): read ONLY the schedule/tab names from cowork_meta — no
    /// per-sheet ReadRows, no model diff — so the picker can list the workbook's tabs instantly (vs the ~2-min full
    /// analysis). Same cowork_meta-absent error path as Read. File IO only; no Revit API needed (safe off the API thread).</summary>
    public IReadOnlyList<(string scheduleName, string sheetTab, string uid)> ReadSheetNames(string path)
    {
        using var fs = File.OpenRead(path);
        IWorkbook wb = WorkbookFactory.Create(fs);

        var metaSheet = wb.GetSheet("cowork_meta")
                        ?? throw new System.InvalidOperationException("Not a Transom workbook (no cowork_meta sheet).");
        var metaSb = new System.Text.StringBuilder();
        for (int r = 0; ; r++)
        {
            var s = metaSheet.GetRow(r)?.GetCell(0)?.ToString();
            if (string.IsNullOrEmpty(s)) break;
            metaSb.Append(s);
        }
        using var doc = JsonDocument.Parse(metaSb.ToString());
        var list = new List<(string, string, string)>();
        foreach (var sheetMeta in doc.RootElement.GetProperty("sheets").EnumerateArray())
            list.Add((
                sheetMeta.GetProperty("scheduleName").GetString() ?? "",
                sheetMeta.GetProperty("sheetName").GetString() ?? "",
                sheetMeta.GetProperty("scheduleUniqueId").GetString() ?? ""));
        return list;
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

        // Per-column captions for the header-rename round-trip: read from the CAPTION row (below any super-header
        // band), NOT row 0. Falls back to row 0 only when there's no distinct caption row (single-row header).
        var caps = new string[anchorCol];
        var capRow = imp.CaptionRowExcel >= 0 ? ws.GetRow(imp.CaptionRowExcel) : header;
        for (int c = 0; c < anchorCol; c++)
            caps[c] = capRow?.GetCell(c)?.ToString() ?? "";
        imp.CurrentCaptions = caps;

        // ID-PRIMARY column matching (user directive 2026-06-14, supersedes the FIX-1 dup-header Pass-0):
        // a column's IDENTITY is its parameterId — it travels with the column regardless of header text or position,
        // so the WRITE keys on it (GetParam(host, col.ParameterId)) and a correctly LOCATED column always writes the
        // right parameter even if its header was renamed. The physical Excel sheet carries only header TEXT (no
        // embedded paramId), so paramId can't be read off a cell; we use it as the identity that decides WHICH meta
        // column owns a physical column, and locate the physical column by header (reorder-safe) with a deterministic
        // position rule inside same-header groups. Two distinct-paramId columns sharing a heading — Panel "TYPE"
        // 67062 + Frame "TYPE" 67025, "REMARKS" Comments -1010106 + Comments(Transom) 7728552, PARTITION "THICKNESS"
        // Width -1001000 + Framing 229593 — therefore BOTH land on their own column (the old ambiguous-duplicate skip
        // is gone). Non-writable computed columns (paramId -1) carry no real parameter; they still locate by header
        // for completeness but are never write targets (the import write/skip loops gate on Writable).
        var used = new bool[anchorCol];

        // Group meta columns by header text; within a shared header, order by EXPORTED position (stable identity).
        // A unique header → one meta column → its single physical column (plain reorder-safe header match). A shared
        // header → the i-th exported column maps to the i-th physical column still carrying that text: deterministic,
        // and since each meta column has its own distinct paramId, BOTH land on the correct parameter. (Empty headers
        // fall through to the legacy/positional paths below.)
        foreach (var grp in imp.Columns.Where(c => !string.IsNullOrEmpty(c.Header)).GroupBy(c => c.Header))
        {
            var physical = new List<int>();
            for (int c = 0; c < anchorCol; c++)
                if (!used[c] && headers[c] == grp.Key) physical.Add(c);

            var metaCols = grp.OrderBy(c => c.Col).ToList();
            int n = System.Math.Min(metaCols.Count, physical.Count);
            for (int i = 0; i < n; i++)
            {
                metaCols[i].ExcelCol = physical[i];
                metaCols[i].Matched = true;   // located by header text (reorder-safe); MatchedByPosition stays false
                used[physical[i]] = true;
            }
            // Surplus on either side (count mismatch under this header) stays unmatched → positional fallback or an
            // honest "renamed/removed" skip below; never a cross-count guess.
        }

        // Legacy workbooks (no stored header) → exported position, when that cell is free.
        foreach (var col in imp.Columns)
        {
            if (col.Matched || !string.IsNullOrEmpty(col.Header)) continue;
            if (col.Col >= 0 && col.Col < anchorCol && !used[col.Col])
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

        // HONESTY FLOOR (FIX-1 carries): a column id-primary couldn't place (genuinely renamed/removed in the sheet,
        // or a count mismatch left it over) stays UNMATCHED → reported by the Importer as a real "column not in
        // spreadsheet" skip — never silently mis-written. The former AmbiguousDuplicate special-case is RETIRED: a
        // dup-NAME column now resolves to its distinct paramId via the same-header group above, so it never becomes an
        // ambiguous skip. (col.AmbiguousDuplicate is no longer set anywhere; the field + its stopgap skip site in
        // Importer are removed in this same change.)
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

        // Current super-header captions: read the workbook cell at each exported header-group's top-left. Super-
        // headers exist only with real headers (no synth row-shift), so the meta rectangle row maps 1:1 to the sheet.
        foreach (var hg in imp.HeaderGroups)
            hg.CurrentCaption = ws.GetRow(hg.Top)?.GetCell(hg.Left)?.ToString() ?? "";

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
