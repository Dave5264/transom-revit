namespace Transom.Core;

/// <summary>Per-cell style captured from the Revit schedule (RGB packed as 0xRRGGBB, -1 = none/invalid).</summary>
public sealed class CellStyleInfo
{
    public string FontName = "";
    public double TextSize;
    public bool Bold;
    public bool Italic;
    public bool Underline;
    public string HAlign = "Left";   // Left | Center | Right
    public string VAlign = "Top";    // Top  | Middle | Bottom
    public int TextColor = -1;       // 0xRRGGBB or -1
    public int BackColor = -1;       // 0xRRGGBB or -1
    public int BorderTop;            // 0=none, 1=thin, 2=medium, 3=thick
    public int BorderBottom;
    public int BorderLeft;
    public int BorderRight;
}

public sealed class TableCell
{
    public string Text = "";
    public CellStyleInfo Style = new();
}

/// <summary>A genuine merge region (Revit returns 1x1 bounds for unmerged cells; we de-dupe).</summary>
public sealed class MergeRegion
{
    public int Top;
    public int Bottom;
    public int Left;
    public int Right;
}

public sealed class ColumnMeta
{
    public int Col;
    public string FieldName = "";
    public string Header = "";           // the displayed column heading (for header-based matching on import)
    public int ParameterId;
    public string Binding = "instance"; // instance | type | none
    public bool Writable;
    public bool Hidden;
    public string? SpecTypeId;          // null when not measurable
    public bool ImportEditable = true;  // false = can't be written back on import (read-only / family-type / unsupported) -> greyed on export
}

public sealed class RowMeta
{
    public int ExcelRow;        // advisory only
    public string? UniqueId;    // anchor: instance UniqueId (element rows) or type UniqueId (type rows); null for header/blank
    public string Kind = "";    // element | type | columnHeader | groupHeader | blank
    public Dictionary<int, string>? Bindings; // col -> instance|type|none, resolved per element (multi-category)

    /// <summary>For grouped "type" rows: the instances this row represents (bulk instance write-back). Null when itemized or ambiguous.</summary>
    public List<string>? InstanceIds;

    /// <summary>Column indices that can never be written on import for this row's element (greyed on export).</summary>
    public HashSet<int>? FrozenCols;

    /// <summary>Grouped-element instance params that are PROJECT/shared params — Transom applies these itself
    /// (sets "vary by group instance" then writes). Blue on export.</summary>
    public HashSet<int>? GroupProjectCols;

    /// <summary>Grouped-element instance params that are BUILT-IN params — can't vary, need the Claude-assist
    /// definition-swap. Yellow on export when Claude-assist is enabled, otherwise a distinct grey.</summary>
    public HashSet<int>? GroupBuiltinCols;

    /// <summary>Column indices whose edit is a BULK write (a type parameter → every instance of that type) — green on export.</summary>
    public HashSet<int>? BulkCols;

    /// <summary>
    ///     When set, this group-HEADER row is editable: changing the value in column <see cref="GroupHeaderEdit.Col"/>
    ///     bulk-writes the grouping parameter to every element under that header. Lets a user rename a group
    ///     (e.g. a hidden Sheet Discipline shown only in the header) and have it apply to all members.
    /// </summary>
    public GroupHeaderEdit? GroupHeaderEdit;
}

/// <summary>An editable group-header: its value cell drives a bulk write of the grouping parameter to its members.</summary>
public sealed class GroupHeaderEdit
{
    public int Col;                       // the cell column that holds the group value (editable)
    public int ParameterId;               // the grouping field's parameter
    public string FieldName = "";         // grouping field name (for the preview)
    public string Binding = "instance";  // instance | type
    public string? SpecTypeId;            // null unless a measurable double
    public List<string> InstanceIds = new(); // the elements under this header (bulk-write targets)
}

/// <summary>In-memory model of one rendered schedule: the visible grid plus round-trip metadata.</summary>
public sealed class ScheduleTable
{
    public string ScheduleName = "";
    public string ScheduleUniqueId = "";
    public int Category;
    public string SourceModelGuid = "";
    public string SourceModelTitle = "";
    public bool RoundTrippable = true;

    /// <summary>Whether Claude-assist is enabled at export time. Drives built-in grouped cells: yellow (enabled)
    /// vs a distinct grey (disabled). Set by the export caller from the current Claude mode.</summary>
    public bool ClaudeAssistEnabled;

    /// <summary>
    ///     True when Revit's Body section row 0 is the column-heading row (the schedule's "Show Headers" is on).
    ///     When false (headers turned off), the Body starts straight at data with no field-name row, so the writer
    ///     synthesizes a header row — otherwise import can't match columns by header. See ExcelWriter.WriteSheet.
    /// </summary>
    public bool HasHeaderRow = true;

    public int RowCount;
    public int ColCount;
    public TableCell[][] Cells = System.Array.Empty<TableCell[]>();
    public List<MergeRegion> Merges = new();
    public int[] ColWidthsPx = System.Array.Empty<int>();

    public List<ColumnMeta> Columns = new();
    public List<RowMeta> Rows = new();

    /// <summary>Optional companion table exposing the editable component parameters of any combined fields, anchored to the same rows.</summary>
    public ScheduleTable? Companion;

    public int ElementRowCount => Rows.Count(r => r.Kind == "element");
}
