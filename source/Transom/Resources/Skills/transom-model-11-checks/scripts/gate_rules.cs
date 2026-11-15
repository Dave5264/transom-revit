// RULE CHECK (execute_revit_code, readOnly: true). Reads only. FAILs on what a clean shell should never have.
const double TOL = 2.0;     // from planes.py: an offset under this between stacked walls is scatter, a larger one must be confirmed
var SKIP_TYPES = new System.Text.RegularExpressions.Regex("(?i)site|egress|retaining|foundation");
var EXPECT_FREE = new HashSet<string> { };   // "id:end" for ends that really stop: a wall rising out of a roof, an end that butts a face. Name each one.
var inv = System.Globalization.CultureInfo.InvariantCulture;
string F(double v) => v.ToString("0.###", inv);
var log = new List<string>(); int fails = 0;
var W = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
  .Where(w => w.WallType.Function == WallFunction.Exterior && w.Location is LocationCurve lc && lc.Curve is Line && !SKIP_TYPES.IsMatch(w.WallType.Name)).ToList();
var R = W.Select(w => { var c = ((LocationCurve)w.Location).Curve; var a = c.GetEndPoint(0) * 12; var b = c.GetEndPoint(1) * 12; var o = w.Orientation; bool xn = Math.Abs(o.X) > 0.9; var bb = w.get_BoundingBox(null);
  return new { w, xn, s = Math.Sign(xn ? o.X : o.Y), face = (xn ? a.X : a.Y) + (xn ? o.X : o.Y) * w.Width * 6, lo = Math.Min(xn ? a.Y : a.X, xn ? b.Y : b.X), hi = Math.Max(xn ? a.Y : a.X, xn ? b.Y : b.X), z0 = bb.Min.Z * 12, z1 = bb.Max.Z * 12 }; }).ToList();
var dup = new List<string>(); var scatter = new List<string>(); var steps = new List<string>();
for (int i = 0; i < R.Count; i++) for (int j = i + 1; j < R.Count; j++) {
  var p = R[i]; var q = R[j]; if (p.xn != q.xn || p.s != q.s) continue;
  double ov = Math.Min(p.hi, q.hi) - Math.Max(p.lo, q.lo); if (ov < 12) continue;
  double d = Math.Abs(p.face - q.face), zov = Math.Min(p.z1, q.z1) - Math.Max(p.z0, q.z0);
  if (d <= 1 && zov > 6) dup.Add(p.w.Id.Value + " + " + q.w.Id.Value + " share a plane for " + F(ov) + "in x " + F(zov) + "in tall (offset " + F(d) + ")");
  bool stacked = Math.Abs(zov) <= 40 && d <= 14;      // touching at a floor line, or a small overlap at a split
  if (stacked && d >= 1.0 / 32 && d < TOL) scatter.Add(p.w.Id.Value + "/" + q.w.Id.Value + " " + F(d) + "in over " + F(ov) + "in" + (p.w.WallType.Id == q.w.WallType.Id ? " same type" : ""));
  else if (stacked && d >= TOL) steps.Add(p.w.Id.Value + "/" + q.w.Id.Value + " " + F(d) + "in over " + F(ov) + "in");
}
log.Add("1. Duplicate or overlapping walls (FAIL; often the sign of a wall on the wrong span, so look for the hole it left): " + dup.Count); log.AddRange(dup.Select(s => "   " + s)); fails += dup.Count;
log.Add("2. Stack scatter under " + TOL + "in (FAIL; reconcile the planes): " + scatter.Count); log.AddRange(scatter.Select(s => "   " + s)); fails += scatter.Count;
log.Add("3. Steps over tolerance (confirm each against a drawing): " + steps.Count); log.AddRange(steps.Select(s => "   " + s));
log.Add("4. Exterior wall pieces: " + W.Count + " (" + string.Join(", ", W.GroupBy(w => w.WallType.Name).Select(g => g.Key + " " + g.Count())) + ")");
var free = new List<string>(); int badFree = 0;
foreach (var w in W) { var lc = (LocationCurve)w.Location; for (int e = 0; e < 2; e++) if (lc.get_ElementsAtJoin(e).Cast<Element>().All(x => x.Id == w.Id)) {
  bool off = !WallUtils.IsWallJoinAllowedAtEnd(w, e), ok = off || EXPECT_FREE.Contains(w.Id.Value + ":" + e);   // joins turned off = an end run to a plane on purpose
  free.Add(w.Id.Value + " end " + e + (off ? " (joins off, run to the plane)" : ok ? " (expected)" : "  FAIL")); if (!ok) badFree++; } }
log.Add("5. Free wall ends (FAIL unless joins were turned off on purpose or the end is named in EXPECT_FREE): " + free.Count); log.AddRange(free.Select(s => "   " + s)); fails += badFree;
foreach (var t in W.Select(w => w.WallType).GroupBy(t => t.Id).Select(g => g.First())) { var cs = t.GetCompoundStructure();
  bool ok = cs.EndCap == EndCapCondition.Exterior && cs.OpeningWrapping != OpeningWrappingCondition.None;
  log.Add("6. Type '" + t.Name + "': shell " + cs.GetNumberOfShellLayers(ShellLayerType.Exterior) + "/" + cs.GetNumberOfShellLayers(ShellLayerType.Interior) + ", " + string.Join("/", cs.GetLayers().Select(l => l.Function)) + ", ends " + cs.EndCap + ", inserts " + cs.OpeningWrapping + (ok ? "" : "  FAIL: jamb and end faces render Default Wall")); if (!ok) fails++; }
foreach (var g in W.GroupBy(w => w.WallType.Id)) {
  // A wall's surface pattern starts at the elevation of its BASE LEVEL; the base offset does not move it (measured 2026-11-07: a 2in offset change
  // left a 30% course jump unchanged, moving the base level to MAIN removed it). Walls of one coursed type on different base levels shear at every
  // joint and corner unless the levels happen to sit a whole number of courses apart, so every wall of the type takes one base level.
  var mat = doc.GetElement(g.First().WallType.GetCompoundStructure().GetLayers()[0].MaterialId) as Material;
  var fp = mat == null ? null : (doc.GetElement(mat.SurfaceForegroundPatternId) as FillPatternElement)?.GetFillPattern();
  if (fp == null || fp.Target != FillPatternTarget.Model) continue;
  double S = fp.GetFillGrids().Max(x => x.Offset) * 12;
  Level BL(Wall w) => doc.GetElement(w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT).AsElementId()) as Level;
  var datum = g.GroupBy(w => BL(w).Id).OrderByDescending(x => x.Count()).First().Key;
  double z0 = (doc.GetElement(datum) as Level).ProjectElevation * 12;
  var off = g.Where(w => { if (BL(w).Id == datum) return false; double d = Math.Abs(BL(w).ProjectElevation * 12 - z0) % S; return Math.Min(d, S - d) > 0.05; }).ToList();
  log.Add("6b. Coursing '" + g.First().WallType.Name + "' (" + F(S) + "in): walls on a base level whose pattern start is off the others' (FAIL): " + off.Count); log.AddRange(off.Select(w => "   " + w.Id.Value + " base level " + BL(w).Name)); fails += off.Count;
}
int joints = 0, miter = 0;
foreach (var w in W) { var lc = (LocationCurve)w.Location; for (int e = 0; e < 2; e++) { joints++; if (lc.get_JoinType(e) == JoinType.Miter) miter++; } }
log.Add("7. Wall ends mitered: " + miter + " of " + joints);
var noisy = new[] { "Type Mark", "Area Boundary", "off axis", "Lines may not form closed loops" };
var warn = doc.GetWarnings().Where(f => !noisy.Any(n => f.GetDescriptionText().Contains(n))).GroupBy(f => f.GetDescriptionText());
foreach (var g in warn) { log.Add("8. Warning x" + g.Count() + ": " + g.Key); foreach (var f in g.Take(8)) log.Add("   " + string.Join(",", f.GetFailingElements().Concat(f.GetAdditionalElements()).Select(id => id.Value))); }
log.Add("RULE CHECK: " + (fails == 0 ? "PASS" : "FAIL (" + fails + ")"));
return string.Join("\n", log);
