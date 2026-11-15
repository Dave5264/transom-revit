// link_one.cs - one link by hand (link_cad.cs does the whole config). Own transaction + failure preprocessor;
// ThisViewOnly, colours preserved, AutoCorrectAlmostVHLines off; links arrive pinned: unpin, move, re-pin.
public class Pre : IFailuresPreprocessor
{
    public List<string> Msgs = new List<string>();
    public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
    {
        bool error = false;
        foreach (var f in a.GetFailureMessages().ToList())
        {
            Msgs.Add(f.GetSeverity() + ": " + f.GetDescriptionText());
            if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f); else error = true;
        }
        return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}
const long VIEW_ID = 0;                              // the matching plan/elevation/section view
const string PATH = @"<folder>\ACAD-<name>-Clean.dwg";
var MOVE = new XYZ(0, 0, 0);                         // the shared transform, in feet (see below)

var pre = new Pre();
var view = (View)doc.GetElement(new ElementId(VIEW_ID));
var opt = new DWGImportOptions { ThisViewOnly = true, ColorMode = ImportColorMode.Preserved, Unit = ImportUnit.Inch,
    Placement = ImportPlacement.Origin, AutoCorrectAlmostVHLines = false, VisibleLayersOnly = true };
using (var t = new Transaction(doc, "Link " + System.IO.Path.GetFileName(PATH)))
{
    var fho = t.GetFailureHandlingOptions(); fho.SetFailuresPreprocessor(pre); fho.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fho);
    t.Start();
    if (!doc.Link(PATH, opt, view, out ElementId id)) { t.RollBack(); return "Link returned false"; }
    var ii = (ImportInstance)doc.GetElement(id);
    ii.Pinned = false;
    ElementTransformUtils.MoveElement(doc, id, MOVE);
    ii.Pinned = true;
    Print($"commit={t.Commit()} id={id.Value} origin={ii.GetTotalTransform().Origin}");
}
foreach (var m in pre.Msgs) Print(m);
