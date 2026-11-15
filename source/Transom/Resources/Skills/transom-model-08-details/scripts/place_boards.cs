// PLACE FACE BOARDS (execute_revit_code, readOnly: true; own transaction). Reads trim_rings.json (trim_rings.py) and places one SA Trim Board
// per piece on the host wall's exterior face. u is the elevation's horizontal axis: SOUTH u = x, NORTH u = -x, EAST u = y, WEST u = -y.
// The same placement serves corner boards and single bands: centre point on the face, Width along the wall, Height up, Depth = projection.
public class Pre : IFailuresPreprocessor
{
    public List<string> Msgs = new List<string>();
    public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
    {
        bool error = false;
        foreach (var f in a.GetFailureMessages().ToList()) { Msgs.Add(f.GetSeverity() + ": " + f.GetDescriptionText()); if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f); else error = true; }
        return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}
const string RINGS = @"<scratch>\trim_rings.json";
const double DEPTH = 0.5;                                   // 1/2" proud (5/4 trim over lap siding)
double I = 1.0/12; var log = new List<string>();
var sym = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().First(s => s.FamilyName == "SA Trim Board");
var root = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(RINGS)).RootElement;
var faces = new Dictionary<long, Tuple<Reference, PlanarFace, XYZ>>();
foreach (var FACE in new[]{ "SOUTH", "WEST", "NORTH", "EAST" }) {
  var pre = new Pre(); int n = 0, ri = 0;
  using (var t = new Transaction(doc, "Window and door trim " + FACE)) {
    var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
    t.Start();
    foreach (var r in root.EnumerateArray()) {
      if (r.GetProperty("face").GetString() != FACE) continue; ri++;
      long hid = r.GetProperty("host").GetInt64();
      if (!faces.ContainsKey(hid)) { var w = doc.GetElement(new ElementId(hid)) as Wall; var fr = HostObjectUtils.GetSideFaces(w, ShellLayerType.Exterior).First();
        faces[hid] = Tuple.Create(fr, w.GetGeometryObjectFromReference(fr) as PlanarFace, ((w.Location as LocationCurve).Curve as Line).Direction); }
      var hf = faces[hid];
      foreach (var p in r.GetProperty("pieces").EnumerateArray()) {
        double u0 = p[0].GetDouble(), u1 = p[1].GetDouble(), z0 = p[2].GetDouble(), z1 = p[3].GetDouble(), uc = (u0 + u1) / 2, zc = (z0 + z1) / 2;
        XYZ pt = FACE == "SOUTH" ? new XYZ(uc*I, hf.Item2.Origin.Y, zc*I) : FACE == "NORTH" ? new XYZ(-uc*I, hf.Item2.Origin.Y, zc*I)
               : FACE == "EAST" ? new XYZ(hf.Item2.Origin.X, uc*I, zc*I) : new XYZ(hf.Item2.Origin.X, -uc*I, zc*I);
        var fi = doc.Create.NewFamilyInstance(hf.Item1, pt, hf.Item3, sym);
        fi.LookupParameter("Width").Set((u1 - u0)*I); fi.LookupParameter("Height").Set((z1 - z0)*I); fi.LookupParameter("Depth").Set(DEPTH*I);
        fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set($"SHELL TRIM {FACE} ring {ri}");
        n++;
      }
    }
    log.Add(FACE + ": " + t.Commit() + $" {n} boards on {ri} rings");
  }
  log.AddRange(pre.Msgs.Distinct().Take(6));
}
return string.Join("\n", log);
