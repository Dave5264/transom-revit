using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>
///     Runs the export inside Revit's API context, raised from the modeless dialog via an
///     <see cref="ExternalEvent"/>. The dialog stays open (Revit's main thread is free, so the
///     MCP bridge and Revit UI keep responding); model work is marshalled here.
/// </summary>
public sealed class ExportEventHandler : IExternalEventHandler
{
    /// <summary>ElementId value of the schedule to export (re-fetched in API context).</summary>
    public long ScheduleId;

    public string OutputPath = "";

    /// <summary>Reports a status line back to the UI (the view-model marshals it to the UI thread).</summary>
    public Action<string> ReportStatus = _ => { };

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = app.ActiveUIDocument.Document;
            if (doc.GetElement(new ElementId(ScheduleId)) is not ViewSchedule vs)
            {
                ReportStatus("Export failed: schedule not found.");
                return;
            }

            var table = new ScheduleReader(doc).Read(vs);
            new ExcelWriter().Write(table, OutputPath);
            ReportStatus($"Exported {table.ElementRowCount} element rows " +
                         $"({table.RowCount} rows x {table.ColCount} cols) to {OutputPath}");
        }
        catch (Exception ex)
        {
            ReportStatus("Export failed: " + ex.Message);
        }
    }

    public string GetName() => "Transom Export";
}
