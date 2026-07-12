using System.Collections.Generic;
using Transom.Core;

namespace Transom.Office;

/// <summary>
///     The <see cref="IOfficeEngine"/> implementation Transom.dll instantiates by reflection (see
///     Core/OfficeIsolation.cs) inside the dedicated "Transom.Office" AssemblyLoadContext. Thin delegation
///     to the moved implementations — no logic here. Keep the type name/namespace stable: OfficeIsolation
///     looks it up as the literal string "Transom.Office.OfficeEngine".
/// </summary>
public sealed class OfficeEngine : IOfficeEngine
{
    public void WriteWorkbooks(List<ScheduleTable> tables, string path,
        Dictionary<(string uid, int paramId), ApplyOutcome>? outcomes = null,
        Dictionary<(string uid, int paramId), string>? attempted = null)
        => new ExcelWriter().WriteMany(tables, path, outcomes, attempted);

    public ImportWorkbook ReadWorkbook(string path, ISet<string>? selectedSheetTabs)
        => new ExcelReader().Read(path, selectedSheetTabs);

    public IReadOnlyList<(string scheduleName, string sheetTab, string uid)> ReadSheetNames(string path)
        => new ExcelReader().ReadSheetNames(path);

    public void WriteRevisionNarrative(RevisionNarrative.Data data, string outputPath, string? templatePath = null)
        => RevisionNarrativeDocxWriter.Write(data, outputPath, templatePath);

    public void WriteDiagnostics(ImportWorkbook wb, List<CellDiagnostic> diags, string path)
        => DiagnosticsWriter.Write(wb, diags, path);
}
