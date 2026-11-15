// WALL FACES (execute_revit_code, readOnly: true). Writes the exterior face outline of every shell wall for slots.py.
// u runs along the wall (model y for an X-facing wall, x for a Y-facing one), z is up, inches. The largest loop is the outline,
// the others are openings. Read faces from the model, not from wall lines: a join can cut a wall's face back from its line end.
const string OUT = @"<scratch>\wall_faces.json";
const string COMMENT_PREFIX = "SHELL";
var inv = System.Globalization.CultureInfo.InvariantCulture;
var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
  .Where(w => (w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).AsString() ?? "").StartsWith(COMMENT_PREFIX)).ToList();
var rows = new List<string>();
foreach (var w in walls) {
  var n = w.Orientation; bool xn = Math.Abs(n.X) > 0.9;
  foreach (var rf in HostObjectUtils.GetSideFaces(w, ShellLayerType.Exterior)) {
    var f = w.GetGeometryObjectFromReference(rf) as Face; var pf = f as PlanarFace; if (pf == null) continue;
    var loops = new List<string>();
    foreach (var cl in f.GetEdgesAsCurveLoops()) {
      var pts = new List<string>();
      foreach (var c in cl) { var tt = c.Tessellate(); foreach (var p in tt.Take(tt.Count - 1)) { var q = p * 12; pts.Add("[" + (xn ? q.Y : q.X).ToString("0.###", inv) + "," + q.Z.ToString("0.###", inv) + "]"); } }
      loops.Add("[" + string.Join(",", pts) + "]");
    }
    rows.Add("{\"id\":" + w.Id.Value + ",\"axis\":\"" + (xn ? "X" : "Y") + "\",\"s\":" + Math.Sign(xn ? n.X : n.Y) + ",\"plane\":" + ((xn ? pf.Origin.X : pf.Origin.Y) * 12).ToString("0.###", inv)
      + ",\"type\":\"" + w.WallType.Name + "\",\"tag\":\"" + w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).AsString() + "\",\"loops\":[" + string.Join(",", loops) + "]}");
  }
}
System.IO.File.WriteAllText(OUT, "[" + string.Join(",\n", rows) + "]");
return rows.Count + " faces on " + walls.Count + " walls";
