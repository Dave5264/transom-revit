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
}

public sealed class RowMeta
{
    public int ExcelRow;        // advisory only
    public string? UniqueId;    // element rows only; null for header/group/blank
    public string Kind = "";    // element | columnHeader | groupHeader | blank
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

    public int RowCount;
    public int ColCount;
    public TableCell[][] Cells = System.Array.Empty<TableCell[]>();
    public List<MergeRegion> Merges = new();
    public int[] ColWidthsPx = System.Array.Empty<int>();

    public List<ColumnMeta> Columns = new();
    public List<RowMeta> Rows = new();

    public int ElementRowCount => Rows.Count(r => r.Kind == "element");
}
