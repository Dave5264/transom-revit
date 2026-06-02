using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>
///     Runs the export inside Revit's API context. Reads each selected schedule and writes one workbook
///     (a sheet each). When Claude-assist staging is on, writes to the exchange folder + a run-log and
///     reports the staged path for a later Finalize.
/// </summary>
public sealed class ExportEventHandler : IExternalEventHandler
{
    public List<long> ScheduleIds = new();
    public string OutputPath = "";
    public string DocTitle = "";
    public bool Stage;
    public string ExchangeFolder = "";
    public Action<string> ReportStatus = _ => { };
    public Action<string> OnStaged = _ => { };

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = DocUtil.Resolve(app, DocTitle);
            if (doc == null) { ReportStatus("Export failed: project not found."); return; }
            var reader = new ScheduleReader(doc);
            var tables = new List<ScheduleTable>();
            foreach (var id in ScheduleIds.Distinct())
                if (doc.GetElement(new ElementId(id)) is ViewSchedule vs)
                {
                    var t = reader.Read(vs);
                    tables.Add(t);
                    if (t.Companion != null) tables.Add(t.Companion); // editable component params of combined fields
                }

            if (tables.Count == 0)
            {
                ReportStatus("Export failed: no schedules found.");
                return;
            }

            int elems = tables.Sum(t => t.ElementRowCount);

            if (Stage && !string.IsNullOrWhiteSpace(ExchangeFolder))
            {
                Directory.CreateDirectory(ExchangeFolder);
                var staged = Path.Combine(ExchangeFolder, Path.GetFileName(OutputPath));
                new ExcelWriter().WriteMany(tables, staged);
                RunLog.WriteExport(ExchangeFolder, tables, staged);
                OnStaged(staged);
                ReportStatus($"Staged {tables.Count} schedule(s) to the exchange folder. Verify with Claude, then Finalize.");
            }
            else
            {
                new ExcelWriter().WriteMany(tables, OutputPath);
                ReportStatus($"Exported {tables.Count} schedule(s) ({elems} element rows) to {OutputPath}");
            }
        }
        catch (Exception ex)
        {
            ReportStatus("Export failed: " + ex.Message);
        }
    }

    public string GetName() => "Transom Export";
}
