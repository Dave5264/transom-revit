using System.Collections.Generic;

namespace Transom.Core;

/// <summary>
///     The spreadsheet/document engine boundary. Implemented by Transom.Office.dll (which owns every NPOI /
///     SixLabors reference) and loaded into a dedicated AssemblyLoadContext via <see cref="OfficeIsolation"/>,
///     so the NPOI closure never binds in Revit's shared default context — the same first-loader-wins clash
///     the Roslyn isolation solved for pyRevit.
///     RULE: no NPOI/SixLabors/etc. type may ever appear in these signatures — Transom-defined DTOs and
///     framework types only, because these types cross the load-context boundary and must have one identity.
/// </summary>
public interface IOfficeEngine
{
    /// <summary>Write one or more schedules to .xlsx/.xls/.csv (mirrors the old ExcelWriter.WriteMany).</summary>
    void WriteWorkbooks(List<ScheduleTable> tables, string path,
        Dictionary<(string uid, int paramId), ApplyOutcome>? outcomes = null,
        Dictionary<(string uid, int paramId), string>? attempted = null);

    /// <summary>Read a Transom workbook back (mirrors ExcelReader.Read).</summary>
    ImportWorkbook ReadWorkbook(string path, ISet<string>? selectedSheetTabs);

    /// <summary>Cheap tab-picker scan (mirrors ExcelReader.ReadSheetNames).</summary>
    IReadOnlyList<(string scheduleName, string sheetTab, string uid)> ReadSheetNames(string path);

    /// <summary>Write the revision narrative .docx (mirrors RevisionNarrativeDocxWriter.Write).</summary>
    void WriteRevisionNarrative(RevisionNarrative.Data data, string outputPath, string? templatePath = null);

    /// <summary>Write the import diagnostics workbook (mirrors DiagnosticsWriter.Write).</summary>
    void WriteDiagnostics(ImportWorkbook wb, List<CellDiagnostic> diags, string path);
}
