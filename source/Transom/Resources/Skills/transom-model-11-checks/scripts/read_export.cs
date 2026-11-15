// READ THE EXPORTS (execute_autocad_code, context "application"). Reads each DWG into a side database, so nothing opens in the editor and
// nothing can be saved over a file. Blocks are walked with their transforms. Writes rvt_<FACE>.csv: layer,u0,z0,u1,z1 in inches. The export is
// already in the view's (u, z) frame, so it compares with the CAD directly, with no fitting that could hide an error.
const string DIR = @"<scratch>\rvt_elev_export\";
var FACES = new[] { "SOUTH", "NORTH", "EAST", "WEST" };
var inv = System.Globalization.CultureInfo.InvariantCulture;
var report = new List<string>();
foreach (var face in FACES) {
  using (var db = new Database(false, true)) {
    db.ReadDwgFile(DIR + "RVT_" + face + ".dwg", FileOpenMode.OpenForReadAndAllShare, true, null);
    db.CloseInput(true);
    var sb = new System.Text.StringBuilder(); var counts = new Dictionary<string, int>();
    double xmin = 1e12, ymin = 1e12, xmax = -1e12, ymax = -1e12;
    using (var t = db.TransactionManager.StartOpenCloseTransaction()) {
      Action<ObjectId, Matrix3d, string, int> walk = null;
      Action<string, Point3d, Point3d> emit = (lay, a, b) => {
        sb.Append(lay).Append(',').Append(a.X.ToString("0.####", inv)).Append(',').Append(a.Y.ToString("0.####", inv)).Append(',').Append(b.X.ToString("0.####", inv)).Append(',').Append(b.Y.ToString("0.####", inv)).AppendLine();
        counts[lay] = (counts.ContainsKey(lay) ? counts[lay] : 0) + 1;
        xmin = Math.Min(xmin, Math.Min(a.X, b.X)); xmax = Math.Max(xmax, Math.Max(a.X, b.X)); ymin = Math.Min(ymin, Math.Min(a.Y, b.Y)); ymax = Math.Max(ymax, Math.Max(a.Y, b.Y));
      };
      walk = (btrId, xf, parent, depth) => {
        var btr = (BlockTableRecord)t.GetObject(btrId, OpenMode.ForRead);
        foreach (ObjectId id in btr) {
          var e = t.GetObject(id, OpenMode.ForRead) as Entity; if (e == null) continue;
          var lay = (e.Layer == "0" && parent != null) ? parent : e.Layer;
          if (e is BlockReference br) { if (depth < 12) walk(br.BlockTableRecord, xf * br.BlockTransform, lay, depth + 1); continue; }
          if (e is Line ln) { emit(lay, ln.StartPoint.TransformBy(xf), ln.EndPoint.TransformBy(xf)); continue; }
          if (e is Curve c) {
            try {
              double p0 = c.StartParam, p1 = c.EndParam;
              int n = e is Polyline pl ? Math.Max(1, (int)Math.Round(p1 - p0)) * (pl.HasBulges ? 8 : 1) : 24;
              var prev = c.GetPointAtParameter(p0).TransformBy(xf);
              for (int i = 1; i <= n; i++) { var q = c.GetPointAtParameter(p0 + (p1 - p0) * i / n).TransformBy(xf); emit(lay, prev, q); prev = q; }
            } catch { }
          }
        }
      };
      walk(SymbolUtilityServices.GetBlockModelSpaceId(db), Matrix3d.Identity, null, 0);
      t.Abort();
    }
    System.IO.File.WriteAllText(DIR + "rvt_" + face + ".csv", sb.ToString());
    report.Add(face + " " + db.Insunits + " x[" + xmin.ToString("0.##", inv) + "," + xmax.ToString("0.##", inv) + "] y[" + ymin.ToString("0.##", inv) + "," + ymax.ToString("0.##", inv) + "] " + string.Join(", ", counts.OrderByDescending(k => k.Value).Take(8).Select(k => k.Key + "=" + k.Value)));
  }
}
return string.Join("\n", report);
