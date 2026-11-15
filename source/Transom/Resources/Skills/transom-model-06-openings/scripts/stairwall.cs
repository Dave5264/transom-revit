// STAIR WALL (execute_revit_code, readOnly: true). A stair window that crosses a floor line needs ONE host wall.
// Raise the lower wall's top to UPPER+SPLIT and start the upper wall there, so the window sits wholly in the lower
// wall. Include the short return walls at the stair bay's corners on both levels, or Revit fails with
// "Can't keep elements joined / Can't make Wall" (the taller corner join swallows a 5" return).
// ORDER MATTERS: raise the upper walls' bases first (own transaction), then the lower walls' tops.
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
const double SPLIT = 36.0;                       // inches above the upper level; must clear the window head
var UPPER_WALLS = new long[] { 0 };              // stair-bay wall(s) on the upper level, incl. corner returns
var LOWER_WALLS = new long[] { 0 };              // the matching walls below, incl. corner returns
var log = new System.Text.StringBuilder();
bool Step(string name, long[] ids, BuiltInParameter bip) {
  var pre = new Pre();
  using (var t = new Transaction(doc, name)) {
    var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
    t.Start();
    foreach (var id in ids) (doc.GetElement(new ElementId(id)) as Wall).get_Parameter(bip).Set(SPLIT / 12);
    var st = t.Commit(); log.AppendLine($"{name}: {st} [{string.Join(" | ", pre.Msgs)}]");
    return st == TransactionStatus.Committed;
  }
}
using (var g = new TransactionGroup(doc, "Stair wall: one wall through the floor line")) {
  g.Start();
  bool ok = Step("upper stair walls start higher", UPPER_WALLS, BuiltInParameter.WALL_BASE_OFFSET)
         && Step("lower stair walls run higher", LOWER_WALLS, BuiltInParameter.WALL_TOP_OFFSET);
  if (ok) g.Assimilate(); else g.RollBack();
  log.AppendLine(ok ? "ASSIMILATED" : "ROLLED BACK");
}
foreach (var id in UPPER_WALLS.Concat(LOWER_WALLS)) {
  var w = doc.GetElement(new ElementId(id)) as Wall; var lc = (LocationCurve)w.Location;
  string J(int e) => string.Join(",", lc.get_ElementsAtJoin(e).Cast<Element>().Where(x => x.Id != w.Id).Select(x => x.Id.Value));
  log.AppendLine($"{id}: base+{w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble() * 12} top+{w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).AsDouble() * 12} joins [{J(0)}] [{J(1)}]");
}
return log.ToString();
