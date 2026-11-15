// PLACE one facade from openings_<FACE>.json (execute_revit_code, readOnly: true; own transactions in one group).
// 1 delete every window/door hosted by an exterior wall facing this way, 2 make/size the types (muntins off),
// 3 place each opening on its wall at u, set the sill, 4 force the wall cuts (batch placement leaves them stale),
// 5 verify: every opening in its host's FindInserts, at its u and sill, facing out. Anything fails -> all rolled back.
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
const string FILE = @"<project>\Revit Modeling\openings\openings_SOUTH.json";
var root = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(FILE)).RootElement;
XYZ V2(System.Text.Json.JsonElement e) => new XYZ(e[0].GetDouble(), e[1].GetDouble(), 0);
var R = V2(root.GetProperty("R")); var FACE = V2(root.GetProperty("facing")); var faceName = root.GetProperty("face").GetString();
var data = root.GetProperty("openings").EnumerateArray().Select(e => (
  fam: e.GetProperty("fam").GetString(), type: e.GetProperty("type").GetString(),
  w: e.GetProperty("w").GetDouble(), h: e.GetProperty("h").GetDouble(), sill: e.GetProperty("sill").GetDouble(),
  wall: e.GetProperty("wall").GetInt64(), uc: e.GetProperty("uc").GetDouble(), level: e.GetProperty("level").GetString(),
  tag: $"OPENING {faceName} {e.GetProperty("kind").GetString()} ({e.GetProperty("src").GetString()})")).ToArray();
var log = new System.Text.StringBuilder();
bool Run(string name, Action body) {
  var pre = new Pre();
  using (var t = new Transaction(doc, name)) {
    var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
    t.Start(); body(); var st = t.Commit();
    log.AppendLine(name + ": " + st + (pre.Msgs.Count > 0 ? "  [" + string.Join(" | ", pre.Msgs.Distinct()) + "]" : ""));
    return st == TransactionStatus.Committed;
  }
}
var old = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
  .Where(fi => fi.Category != null && (fi.Category.Id.Value == (long)BuiltInCategory.OST_Windows || fi.Category.Id.Value == (long)BuiltInCategory.OST_Doors))
  .Where(fi => fi.Host is Wall hw && hw.WallType.Function == WallFunction.Exterior && hw.Orientation.DotProduct(FACE) > 0.99).ToList();
var oldDesc = string.Join(", ", old.Select(f => f.Id.Value + " " + f.Symbol.Name));
var oldIds = old.Select(f => f.Id).ToList();
var made = new List<(FamilyInstance fi, int i)>();
using (var g = new TransactionGroup(doc, $"Openings {faceName} from the drafted elevation")) {
  g.Start();
  bool ok = Run("delete old " + faceName + " openings", () => doc.Delete(oldIds));
  log.AppendLine("deleted " + oldIds.Count + ": " + oldDesc);
  ok = ok && Run("place " + faceName + " openings", () => {
    var cache = new Dictionary<string, FamilySymbol>();
    for (int i = 0; i < data.Length; i++) {
      var d = data[i];
      if (!cache.TryGetValue(d.fam + "|" + d.type, out var sym)) {
        var syms = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().Where(s => s.FamilyName == d.fam).ToList();
        if (syms.Count == 0) throw new Exception("family not loaded: " + d.fam);
        sym = syms.FirstOrDefault(s => s.Name == d.type) ?? (syms.First().Duplicate(d.type) as FamilySymbol);
        sym.LookupParameter("Width").Set(d.w / 12); sym.LookupParameter("Height").Set(d.h / 12);
        foreach (var pn in new[] { "Muntin Visibility", "Top Muntin Visibility", "Bottom Muntin Visibility" }) { var p = sym.LookupParameter(pn); if (p != null && !p.IsReadOnly) p.Set(0); }
        if (!sym.IsActive) sym.Activate();
        cache[d.fam + "|" + d.type] = sym;
      }
      var wall = doc.GetElement(new ElementId(d.wall)) as Wall;
      var c = (wall.Location as LocationCurve).Curve; var a = c.GetEndPoint(0); var b = c.GetEndPoint(1);
      double tt = (d.uc / 12 - a.DotProduct(R)) / (b - a).DotProduct(R);
      var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First(l => l.Name == d.level);   // the level it sits on, not the wall's base
      var p0 = a + (b - a) * tt; p0 = new XYZ(p0.X, p0.Y, lvl.ProjectElevation + d.sill / 12);
      var fi = doc.Create.NewFamilyInstance(p0, sym, wall, lvl, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
      fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(d.sill / 12);
      fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set(d.tag);
      made.Add((fi, i));
    }
    doc.Regenerate();
    foreach (var (fi, i) in made) if (fi.FacingOrientation.DotProduct(FACE) < 0) fi.flipFacing();
  });
  ok = ok && Run("recompute " + faceName + " wall cuts", () => {
    foreach (var (fi, i) in made) { var p = fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM); p.Set(p.AsDouble() + 0.01); }
    doc.Regenerate();
    foreach (var (fi, i) in made) fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM).Set(data[i].sill / 12);
  });
  int bad = 0;
  if (ok) foreach (var (fi, i) in made) {
    var d = data[i]; var wall = fi.Host as Wall;
    bool cuts = wall.FindInserts(true, true, true, true).Contains(fi.Id);
    double uc = (fi.Location as LocationPoint).Point.DotProduct(R) * 12, sill = fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM).AsDouble() * 12;
    bool good = cuts && Math.Abs(uc - d.uc) < 0.05 && Math.Abs(sill - d.sill) < 0.01 && fi.FacingOrientation.DotProduct(FACE) > 0.99;
    if (!good) bad++;
    log.AppendLine($"{fi.Id.Value} {d.type,-26} wall {d.wall} uc {uc:0.00} sill {sill:0.##} cuts={cuts} {(good ? "OK" : "BAD")}");
  }
  if (ok && bad == 0) g.Assimilate(); else { g.RollBack(); log.AppendLine($"ROLLED BACK ({bad} bad)"); }
}
log.AppendLine($"{faceName}: placed {made.Count} of {data.Length}");
return log.ToString();
