using System;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>
///     Runs import preview (read-only diff) and apply (transaction) in Revit's API context,
///     raised from the modeless dialog.
/// </summary>
public sealed class ImportEventHandler : IExternalEventHandler
{
    public enum Mode { Preview, Apply }

    public Mode RequestedMode = Mode.Preview;
    public string WorkbookPath = "";
    public ChangeSet? PendingChangeSet;
    public bool WriteRunLog;
    public string ExchangeFolder = "";

    public Action<ChangeSet> OnPreview = _ => { };
    public Action<string> OnApplied = _ => { };
    public Action<string> OnError = _ => { };

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = app.ActiveUIDocument.Document;
            if (RequestedMode == Mode.Preview)
            {
                var wb = new ExcelReader().Read(WorkbookPath);
                var cs = new Importer().BuildChangeSet(doc, wb);
                if (cs.Diagnostics.Count > 0)
                {
                    var dir = System.IO.Path.GetDirectoryName(WorkbookPath) ?? ".";
                    var name = System.IO.Path.GetFileNameWithoutExtension(WorkbookPath);
                    var reportPath = System.IO.Path.Combine(dir, name + "_import-report.xlsx");
                    try { DiagnosticsWriter.Write(wb, cs.Diagnostics, reportPath); cs.ReportPath = reportPath; }
                    catch { /* report is best-effort */ }
                }
                if (WriteRunLog) RunLog.WriteImport(ExchangeFolder, WorkbookPath, cs);
                OnPreview(cs);
            }
            else
            {
                if (PendingChangeSet == null) { OnError("No previewed changes to apply."); return; }
                OnApplied(new Importer().Apply(doc, PendingChangeSet));
            }
        }
        catch (Exception ex)
        {
            OnError(ex.Message);
        }
    }

    public string GetName() => "Transom Import";
}
