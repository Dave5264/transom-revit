// Import-side DTOs shared across the Transom.dll / Transom.Office.dll boundary. They deliberately
// contain NO NPOI (or other isolated-library) types: Transom.Office is loaded into a dedicated
// AssemblyLoadContext (Core/OfficeIsolation.cs) and these cross it, so they must resolve in the
// shared default context. Split out of ExcelReader.cs when the reader moved to Transom.Office.
using System.Collections.Generic;

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

    // The four dicts below are keyed by SHEET ROW (the meta's excelRow, emitted as the actual sheet row), NOT by
    // uniqueId: a type-organized schedule renders many rows under one TYPE uid, so uid-keying collapses these
    // per-rendered-row values last-writer-wins (every row of a type would get the last row's InstanceIds → wrong
    // bulk-write target + a baseline-key miss → phantom edits). Sheet-row keys are unique per rendered row.

    /// <summary>Row kind per rendered row: sheetRow -> "element" | "type" | "group".</summary>
    public Dictionary<int, string> RowKinds = new();

    /// <summary>Type/group rows: sheetRow -> the instances that row represents (bulk write-back).</summary>
    public Dictionary<int, List<string>> RowInstanceIds = new();

    /// <summary>Multi-type rows: sheetRow -> the full list of type uids a type edit must fan out to.</summary>
    public Dictionary<int, List<string>> RowAggregatedTypeUids = new();

    /// <summary>Editable group-header rows: sheetRow -> its bulk-write spec.</summary>
    public Dictionary<int, GroupHeaderEdit> RowGroupHeaderEdits = new();

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
    /// <summary>Stable path of the exporting model (empty in workbooks from older exports). The
    /// cross-model tiebreaker: two models can share a CreationGUID via Save-As, so the GUID alone
    /// can miss a cross-model import.</summary>
    public string SourceModelPath = "";
    public List<ImportSheet> Sheets = new();
}
