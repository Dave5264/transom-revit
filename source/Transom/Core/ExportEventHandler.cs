using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>
///     Runs the export inside Revit's API context, raised from the modeless dialog via an
///     <see cref="ExternalEvent"/>. Reads each selected schedule and writes one workbook (a sheet each).
/// </summary>
public sealed class ExportEventHandler : IExternalEventHandler
{
    public List<long> ScheduleIds = new();
    public string OutputPath = "";
    public Action<string> ReportStatus = _ => { };

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = app.ActiveUIDocument.Document;
            var reader = new ScheduleReader(doc);
            var tables = new List<ScheduleTable>();
            foreach (var id in ScheduleIds.Distinct())
                if (doc.GetElement(new ElementId(id)) is ViewSchedule vs)
                    tables.Add(reader.Read(vs));

            if (tables.Count == 0)
            {
                ReportStatus("Export failed: no schedules found.");
                return;
            }

            new ExcelWriter().WriteMany(tables, OutputPath);
            int elems = tables.Sum(t => t.ElementRowCount);
            ReportStatus($"Exported {tables.Count} schedule(s) ({elems} element rows) to {OutputPath}");
        }
        catch (Exception ex)
        {
            ReportStatus("Export failed: " + ex.Message);
        }
    }

    public string GetName() => "Transom Export";
}
