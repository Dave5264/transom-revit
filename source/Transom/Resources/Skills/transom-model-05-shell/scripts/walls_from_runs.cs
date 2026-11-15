// walls_from_runs.cs - run through run_file.cs with ARGS { CFG, RUNS, LIMITS }; own transactions. Builds runs.json from planes.py as continuous walls.
// Exterior face on the plane, outward normal on the left of travel (never Wall.Flip), centerlines ending on the neighbouring
// wall's centerline so Revit joins the corner. Miters are set in a second transaction: set_JoinType does nothing before commit.
// Every wall of one material takes that material's DATUM level as its base and reaches its real base by offset: Revit starts a wall's
// surface pattern at its base level, so walls on different base levels shear the coursing at every joint (rule check 6b).
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
// Settings come from the project config ({{CFG}}) and the call's ARGS:
//   RUNS   = the runs file (planes.py output, or a recorded one from the reference set)
//   LIMITS = "xmin,ymin,xmax,ymax" in model inches to build one mass at a time (a run must lie wholly inside), or "" for all
var CFG = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(@"{{CFG}}")).RootElement;
var D = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith(CFG.GetProperty("model_title").GetString()));
string RUNS = @"{{RUNS}}";
var mvin = CFG.GetProperty("cad_to_model_in").EnumerateArray().Select(e => e.GetDouble()).ToArray();
double DX = mvin[0], DY = mvin[1];                       // CAD frame to model frame, inches (the Phase 3 transform)
var lim = "{{LIMITS}}".Split(',', StringSplitOptions.RemoveEmptyEntries).Select(double.Parse).ToArray();
double XMIN = lim.Length == 4 ? lim[0] : -1e9, YMIN = lim.Length == 4 ? lim[1] : -1e9, XMAX = lim.Length == 4 ? lim[2] : 1e9, YMAX = lim.Length == 4 ? lim[3] : 1e9;
string TAG = CFG.GetProperty("tag").GetString();
var LEVELS = CFG.GetProperty("levels").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();   // bottom up, including the level above the top walls
var RUNLEVEL = new[] { "LOWER", "MAIN", "UPPER" }.ToDictionary(k => k, k => LEVELS.First(n => n.StartsWith(k)));   // run level codes from planes.py
var TYPES = CFG.GetProperty("wall_types_by_material").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
var DATUM = CFG.GetProperty("datum_level_by_material").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());   // one base level per material, its lowest
var log = new List<string>();
var lv = LEVELS.Select(n => new FilteredElementCollector(D).OfClass(typeof(Level)).Cast<Level>().First(l => l.Name == n)).ToList();
var wts = TYPES.ToDictionary(k => k.Key, k => new FilteredElementCollector(D).OfClass(typeof(WallType)).Cast<WallType>().First(x => x.Name == k.Value));
var made = new List<Tuple<Wall, double, double, bool>>();   // wall, nx, ny, flag
var pre = new Pre();
var root = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(RUNS)).RootElement;
using (var t = new Transaction(D, "Shell walls from reconciled planes")) {
  var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
  t.Start();
  foreach (var r in root.EnumerateArray()) {
    bool xn = r.GetProperty("axis").GetString() == "X";
    int s = r.GetProperty("facing").GetInt32();
    double c = r.GetProperty("c").GetDouble() + (xn ? DX : DY);
    double off = xn ? DY : DX;
    double u0 = r.GetProperty("u0").GetDouble() + off, u1 = r.GetProperty("u1").GetDouble() + off;
    double loX = xn ? c : u0, hiX = xn ? c : u1, loY = xn ? u0 : c, hiY = xn ? u1 : c;
    if (loX < XMIN || hiX > XMAX || loY < YMIN || hiY > YMAX) continue;
    var wt = wts[r.GetProperty("material").GetString()];
    double w = wt.Width * 12;
    double e0 = u0 - r.GetProperty("n0").GetInt32() * w / 2, e1 = u1 - r.GetProperty("n1").GetInt32() * w / 2;
    double cl = c - s * w / 2;
    XYZ P(double u) => (xn ? new XYZ(cl, u, 0) : new XYZ(u, cl, 0)) / 12;
    bool forward = xn ? s < 0 : s > 0;       // travel = (ny, -nx) so the outward normal is on the left
    var line = forward ? Line.CreateBound(P(e0), P(e1)) : Line.CreateBound(P(e1), P(e0));
    int ib = Array.IndexOf(LEVELS, RUNLEVEL[r.GetProperty("base").GetString()]);
    int it = Array.IndexOf(LEVELS, RUNLEVEL[r.GetProperty("top").GetString()]) + 1;
    var dl = lv[Array.IndexOf(LEVELS, DATUM[r.GetProperty("material").GetString()])];
    double zb = lv[ib].ProjectElevation + (r.TryGetProperty("base_offset", out var bo) ? bo.GetDouble() / 12 : 0);   // a junction wall can start above a level
    var wl = Wall.Create(D, line, wt.Id, dl.Id, lv[it].ProjectElevation - dl.ProjectElevation, 0, false, false);
    wl.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(lv[it].Id);
    wl.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).Set((int)WallLocationLine.FinishFaceExterior);
    if (r.TryGetProperty("top_offset", out var tp)) wl.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).Set(tp.GetDouble() / 12);   // top BEFORE base: a base above the current top fails to regenerate
    wl.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).Set(zb - dl.ProjectElevation);
    wl.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set(TAG + " " + r.GetProperty("material").GetString() + " " + (r.TryGetProperty("tag", out var tg) ? tg.GetString() : "plane " + r.GetProperty("plane").GetInt32()));
    made.Add(Tuple.Create(wl, xn ? (double)s : 0.0, xn ? 0.0 : (double)s, true));
  }
  D.Regenerate();
  foreach (var m in made) if (m.Item1.Orientation.X * m.Item2 + m.Item1.Orientation.Y * m.Item3 < 0.99) throw new Exception("wall " + m.Item1.Id.Value + " faces the wrong way");
  log.Add("create: " + t.Commit() + " walls " + made.Count + (pre.Msgs.Count > 0 ? " [" + string.Join(" | ", pre.Msgs.Distinct()) + "]" : ""));
}
var pre2 = new Pre(); int mitered = 0;
using (var t = new Transaction(D, "Miter exterior corners")) {
  var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre2); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
  t.Start();
  foreach (var m in made) {
    var lc = (LocationCurve)m.Item1.Location;
    for (int e = 0; e < 2; e++) {
      var others = lc.get_ElementsAtJoin(e).Cast<Element>().Where(x => x.Id != m.Item1.Id).OfType<Wall>().ToList();
      if (others.Count != 1 || others[0].WallType.Id != m.Item1.WallType.Id) continue;       // an L of one type only; a T or a change of material stays as it is
      if (Math.Abs(others[0].Orientation.DotProduct(m.Item1.Orientation)) > 0.01) continue;
      if (lc.get_JoinType(e) != JoinType.Miter) { lc.set_JoinType(e, JoinType.Miter); mitered++; }
    }
  }
  log.Add("miter: " + t.Commit() + " " + mitered + (pre2.Msgs.Count > 0 ? " [" + string.Join(" | ", pre2.Msgs.Distinct()) + "]" : ""));
}
foreach (var m in made) {
  var lc = (LocationCurve)m.Item1.Location; var a = lc.Curve.GetEndPoint(0) * 12; var b = lc.Curve.GetEndPoint(1) * 12; var bb = m.Item1.get_BoundingBox(null);
  string J(int e) => lc.get_JoinType(e) + "[" + string.Join(",", lc.get_ElementsAtJoin(e).Cast<Element>().Where(x => x.Id != m.Item1.Id).Select(x => x.Id.Value)) + "]";
  log.Add($"{m.Item1.Id.Value} {m.Item1.WallType.Name} ({a.X:0.0},{a.Y:0.0})-({b.X:0.0},{b.Y:0.0}) z {bb.Min.Z * 12:0.0}..{bb.Max.Z * 12:0.0} {J(0)} {J(1)}");
}
return string.Join("\n", log);
