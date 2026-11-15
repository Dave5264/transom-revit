// CLOSED MASSES (execute_revit_code, readOnly: true; own transaction). One Generic Model DirectShape per prism in prisms.json, comment
// MASS CHECK, so the building's form can be compared and looked at before any detail exists. A solid cannot have a hole.
// A prism is {"name","axis":"x"|"y","a":[start,end],"profile":[[v,z],...]} in model inches: a closed profile extruded along axis.
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
const string PRISMS = @"<project>\Revit Modeling\prisms.json";
var root = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(PRISMS)).RootElement;
var log = new List<string>();
var pre = new Pre();
using (var t = new Transaction(doc, "Mass check prisms")) {
  var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
  t.Start();
  foreach (var p in root.EnumerateArray()) {
    bool alongX = p.GetProperty("axis").GetString() == "x";
    double a0 = p.GetProperty("a")[0].GetDouble(), a1 = p.GetProperty("a")[1].GetDouble();
    var pts = p.GetProperty("profile").EnumerateArray().Select(q => alongX ? new XYZ(a0, q[0].GetDouble(), q[1].GetDouble()) / 12 : new XYZ(q[0].GetDouble(), a0, q[1].GetDouble()) / 12).ToList();
    var curves = new List<Curve>();
    for (int i = 0; i < pts.Count; i++) curves.Add(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
    var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { CurveLoop.Create(curves) }, alongX ? XYZ.BasisX : XYZ.BasisY, (a1 - a0) / 12);
    var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
    ds.SetShape(new List<GeometryObject> { solid });
    ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set("MASS CHECK " + p.GetProperty("name").GetString());
    log.Add(ds.Id.Value + " " + p.GetProperty("name").GetString() + " " + solid.Volume.ToString("0") + " ft3");
  }
  log.Add("commit: " + t.Commit() + (pre.Msgs.Count > 0 ? " [" + string.Join(" | ", pre.Msgs.Distinct()) + "]" : ""));
}
return string.Join("\n", log);
