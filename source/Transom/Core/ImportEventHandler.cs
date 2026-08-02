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
    public string DocTitle = "";
    public bool ProduceReport;
    public System.Collections.Generic.List<CellCorrection>? Corrections;
    /// <summary>§16 pre-analysis tab picker: when non-null, Preview analyzes ONLY these sheet tabs (the scoped
    /// ExcelReader.Read skips the rest's ReadRows + diff). Null = analyze every tab (today's behaviour). The VM sets
    /// it from the picker before raising Preview, and carries it across a corrections re-preview (§16.3).</summary>
    public System.Collections.Generic.ISet<string>? SelectedSheetTabs;

    public Action<ChangeSet> OnPreview = _ => { };
    public Action<string> OnApplied = _ => { };
    public Action<string> OnAppliedLog = _ => { };   // full apply diagnostic (incl. Revit warnings) for Copy-log
    public Action<string> OnError = _ => { };

    /// <summary>
    ///     Revit dialogs the apply may auto-answer with 7 (No/Cancel) — the side-effect prompts a parameter
    ///     write raises, where "No" is both safe and the intended answer. Fail-closed by design: anything not
    ///     listed here is recorded and left for a human, because answering "No" to an unknown prompt can
    ///     silently abandon part of a write while the apply still reports success. Grow this list only after
    ///     observing a dialog live and confirming No is the benign answer (same policy as the failure
    ///     auto-resolution allow-list in docs/design-notes/revit-api-research-notes.md).
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> AutoDismissDialogIds = new(StringComparer.Ordinal)
    {
        "TaskDialog_Rename_Corresponding_Level_and_Views",   // renaming a Level: "rename associated views?"
        "TaskDialog_Changes_To_Group",                        // group-member edit notice
    };

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = DocUtil.Resolve(app, DocTitle);
            if (doc == null) { OnError("project not found"); return; }
            if (RequestedMode == Mode.Preview)
            {
                var wb = OfficeIsolation.Engine.ReadWorkbook(WorkbookPath, SelectedSheetTabs);   // §16: scoped to picked tabs when set

                // Apply any user-supplied fixes for previously-unparseable cells IN MEMORY only — we never write
                // the user's workbook file. Values already in the schedule's unit format are accepted for this
                // import; values that parse but differ come back as reformat suggestions to confirm.
                System.Collections.Generic.List<ReformatSuggestion> reformats = new();
                if (Corrections != null && Corrections.Count > 0)
                {
                    var corr = ExcelCorrector.Apply(wb, Corrections, doc.GetUnits());
                    reformats = corr.Reformats;
                }

                var cs = new Importer().BuildChangeSet(doc, wb);
                // Merge — BuildChangeSet itself parks parse-OK-but-wrong-format cells in cs.Reformats;
                // corrector suggestions go first so ShowPreview's per-cell dedupe keeps the user-typed value.
                cs.Reformats.InsertRange(0, reformats);
                if (ProduceReport && cs.Diagnostics.Count > 0)
                {
                    var dir = System.IO.Path.GetDirectoryName(WorkbookPath) ?? ".";
                    var name = System.IO.Path.GetFileNameWithoutExtension(WorkbookPath);
                    var reportPath = System.IO.Path.Combine(dir, name + "_import-report.xlsx");
                    try { OfficeIsolation.Engine.WriteDiagnostics(wb, cs.Diagnostics, reportPath); cs.ReportPath = reportPath; }
                    catch { /* report is best-effort */ }
                }
                if (WriteRunLog) RunLog.WriteImport(ExchangeFolder, WorkbookPath, cs, "preview");
                OnPreview(cs);
            }
            else
            {
                if (PendingChangeSet == null) { OnError("No previewed changes to apply."); return; }

                // Some edits raise modal DialogBoxes (not Failures) that would block the apply — e.g. renaming a
                // Level prompts "rename associated views?". Auto-dismiss the KNOWN-SAFE ones with 7 (No/Cancel);
                // record every dialog seen. An unrecognised dialog is left alone: answering "No" to everything
                // could silently abandon part of a write whose prompt needed assent, and the user would see a
                // successful apply with only a raw DialogId in the log.
                var dialogs = new System.Collections.Generic.List<string>();
                var unhandledDialogs = new System.Collections.Generic.List<string>();
                EventHandler<Autodesk.Revit.UI.Events.DialogBoxShowingEventArgs> dh = (_, e) =>
                {
                    string id = "";
                    try { id = e.DialogId ?? ""; } catch { /* ignore */ }
                    try { dialogs.Add(id); } catch { /* ignore */ }
                    if (AutoDismissDialogIds.Contains(id))
                    {
                        try { e.OverrideResult(7); } catch { /* ignore */ }
                    }
                    else
                    {
                        try { unhandledDialogs.Add(id); } catch { /* ignore */ }
                    }
                };
                app.DialogBoxShowing += dh;
                string status;
                try { status = new Importer().Apply(doc, PendingChangeSet); }
                finally { app.DialogBoxShowing -= dh; }

                // Post-commit verification (catches silent post-apply failures), then a run-results workbook if
                // anything Failed/Unverified — written next to the source import file.
                try
                {
                    new Importer().VerifyApplied(doc, PendingChangeSet);
                    // FIX 3 + FIX 4: rebuild the apply log + status from the FINAL by-uid verified outcomes, so the
                    // counts don't carry the mid-loop type-bound over-count and the non-applied set is split into
                    // honest buckets (write-didn't-take vs no-such-parameter). Only for the clean single-commit path:
                    // a rollback or per-change recovery keeps its own authoritative log (those carry essential
                    // rollback/retry context the finalizer would drop, and their per-change transactions don't have
                    // the mid-loop type-bound over-count). Branch on the FLAGS Apply sets — this used to substring-
                    // match the user-facing status prose, so re-wording a message silently changed control flow.
                    if (!PendingChangeSet.RolledBack && !PendingChangeSet.RecoveredAfterRollback)
                        status = new Importer().FinalizeApplyReport(PendingChangeSet);

                    var runResultsPath = RunResultsWriter.Write(doc, PendingChangeSet, app, WorkbookPath);
                    if (!string.IsNullOrEmpty(runResultsPath)) status += $"  ·  run-results: {runResultsPath}";
                }
                catch { /* verification / report is best-effort — never block the apply */ }

                int autoDismissed = dialogs.Count - unhandledDialogs.Count;
                if (autoDismissed > 0) status += $"  ·  {autoDismissed} Revit prompt(s) auto-dismissed (see log)";
                // An unrecognised prompt was answered by the USER, not by us — it may have changed what the
                // apply did, so it must never be invisible on the status line.
                if (unhandledDialogs.Count > 0)
                    status += $"  ·  ⚠ {unhandledDialogs.Count} unexpected Revit prompt(s) shown during apply (see log)";

                // W1 (Task #17): if Revit's regen auto-fix deleted geometry during apply, surface it on the status
                // line too (the detail/ids are in the apply log) — a "successful" apply that removed geometry must
                // never look clean.
                if (PendingChangeSet.RevitDeletions.Count > 0)
                    status += $"  ·  ⚠ {PendingChangeSet.RevitDeletions.Count} element(s) auto-deleted by Revit during apply (see log)";

                // Apply-stage run log: outcomes are final here (VerifyApplied ran above), so this is the
                // only write that may legitimately call its entries "applied".
                if (WriteRunLog) RunLog.WriteImport(ExchangeFolder, WorkbookPath, PendingChangeSet, "apply");

                OnApplied(status);
                OnAppliedLog(PendingChangeSet.DiagnosticLog
                    + (dialogs.Count > 0
                        ? "\n\n== Revit prompts during apply ==\n  - " + string.Join("\n  - ", dialogs)
                        : "")
                    + (unhandledDialogs.Count > 0
                        ? "\n\n== NOT auto-dismissed (not on the safe list — answered by the user) ==\n  - "
                          + string.Join("\n  - ", unhandledDialogs)
                        : ""));
            }
        }
        catch (Exception ex)
        {
            // Write the type + full stack to a copyable log and name it in the status. A bare one-line
            // message left a failure inside BuildChangeSet/Apply — the most complex code in the product —
            // undebuggable from the UI, while the export path had done this properly all along.
            var logPath = WriteErrorLog(ex, DocTitle);
            OnError(ex.Message + (string.IsNullOrEmpty(logPath) ? "" : $"  ·  details: {logPath}"));
        }
    }

    /// <summary>Writes the failing exception (type, message, stack) to %TEMP%\Transom\import-errors.log and
    /// returns the path, or null if even that fails. Mirrors ExportEventHandler's log.</summary>
    private static string? WriteErrorLog(Exception ex, string docTitle)
    {
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Transom");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "import-errors.log");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Transom import error log");
            sb.AppendLine("Model: " + docTitle);
            sb.AppendLine("When:  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                sb.AppendLine(new string('=', 64));
                sb.AppendLine("ERROR: " + e.GetType().FullName + ": " + e.Message);
                sb.AppendLine("STACK:");
                sb.AppendLine(e.StackTrace ?? "(none)");
                sb.AppendLine();
            }
            System.IO.File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch { return null; }
    }

    public string GetName() => "Transom Import";
}
