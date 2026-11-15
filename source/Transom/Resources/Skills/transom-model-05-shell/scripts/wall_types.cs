// WALL TYPES (execute_revit_code, readOnly: true; own transaction). Do this before any wall exists: changing types late disrupts joins.
// Puts the exterior finish outside the core, sets real finish functions, and wraps at ends and inserts, so jamb and end faces
// carry the finish instead of Default Wall. First layer = exterior, last layer = interior.
public class Pre : IFailuresPreprocessor
{
    public List<string> Msgs = new List<string>();
    public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
    {
        bool error = false;
        foreach (var f in a.GetFailureMessages().ToList())
        {
            Msgs.Add(f.GetSeverity() + ": " + f.GetDescriptionText() + " {" + string.Join(",", f.GetFailingElementIds().Select(i => i.Value)) + "}");
            if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f); else error = true;
        }
        return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}
var TYPES = new[] { "SA EXT - Lap Siding", "SA EXT - Stone Base" };
var log = new List<string>();
var pre = new Pre();
using (var t = new Transaction(doc, "Wall types: finish outside the core, wrapped")) {
  var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
  t.Start();
  foreach (var name in TYPES) {
    var wt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(x => x.Name == name);
    var cs = wt.GetCompoundStructure();
    int n = cs.LayerCount;
    cs.SetNumberOfShellLayers(ShellLayerType.Exterior, 1);
    cs.SetNumberOfShellLayers(ShellLayerType.Interior, 1);
    cs.SetLayerFunction(0, MaterialFunctionAssignment.Finish1);
    cs.SetLayerFunction(n - 1, MaterialFunctionAssignment.Finish2);
    cs.EndCap = EndCapCondition.Exterior;
    cs.OpeningWrapping = OpeningWrappingCondition.Exterior;
    IDictionary<int, CompoundStructureError> e1; IDictionary<int, int> e2;
    if (!cs.IsValid(doc, out e1, out e2)) throw new Exception(name + " would be invalid: " + string.Join(",", e1.Select(k => k.Key + ":" + k.Value)));
    wt.SetCompoundStructure(cs);
    var r = wt.GetCompoundStructure();
    log.Add(name + ": " + string.Join("/", r.GetLayers().Select(l => l.Function)) + ", shell " + r.GetNumberOfShellLayers(ShellLayerType.Exterior) + "/" + r.GetNumberOfShellLayers(ShellLayerType.Interior) + ", ends " + r.EndCap + ", inserts " + r.OpeningWrapping);
  }
  log.Add("commit: " + t.Commit() + (pre.Msgs.Count > 0 ? " [" + string.Join(" | ", pre.Msgs.Distinct()) + "]" : ""));
}
return string.Join("\n", log);
