// FAMILY OPTIONS (execute_revit_code, readOnly: true; own transaction). Applies the decisions from the family audit to every placed instance and type.
// Run after each place.cs: placing resets instance parameters. Every value here must trace to a drafted unit or a user ruling, with its reason beside it.
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
// family -> instance parameter -> value. int = Yes/No (0/1). string = the name of a family type offered by that parameter.
var INSTANCE = new Dictionary<string, Dictionary<string, object>> {
  { "Window-Fixed", new Dictionary<string, object> { { "Exterior Trim Visibility", 0 } } },          // drawn with no projecting sill or apron
  { "Window-Fixed-Double", new Dictionary<string, object> { { "Exterior Trim Visibility", 0 } } },
  { "Masonry_Single", new Dictionary<string, object> { { "Panel Type", "FG" } } },                   // drawn with a tall lite
};
// family -> type parameter -> value. int = Yes/No or an enumerated value, string = a material name.
var TYPE = new Dictionary<string, Dictionary<string, object>> {
  { "Window-Fixed-Double", new Dictionary<string, object> { { "Glass Panel Material", "+Exterior Glass" } } },   // one glass across the building
  { "Masonry_Single", new Dictionary<string, object> { { "Function", 1 } } },                                    // 1 = Exterior: an exterior door, not the family's interior default
};
var log = new List<string>(); int nInst = 0, nType = 0;
var pre = new Pre();
var insts = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(f => f.Host is Wall && INSTANCE.ContainsKey(f.Symbol.FamilyName) || TYPE.ContainsKey(f.Symbol.FamilyName) && f.Host is Wall).ToList();
using (var t = new Transaction(doc, "Family options from the audit")) {
  var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
  t.Start();
  foreach (var fi in insts) {
    if (!INSTANCE.TryGetValue(fi.Symbol.FamilyName, out var want)) continue;
    foreach (var kv in want) {
      var p = fi.LookupParameter(kv.Key); if (p == null) { log.Add("no parameter " + kv.Key + " on " + fi.Symbol.FamilyName); continue; }
      if (kv.Value is int iv) { if (p.AsInteger() != iv) { p.Set(iv); nInst++; } }
      else { var opt = fi.Symbol.Family.GetFamilyTypeParameterValues(p.Id).Select(i => doc.GetElement(i)).First(e => e.Name == (string)kv.Value); if (p.AsElementId() != opt.Id) { p.Set(opt.Id); nInst++; } }
    }
  }
  foreach (var sym in insts.Select(f => f.Symbol).GroupBy(s => s.Id).Select(g => g.First())) {
    if (!TYPE.TryGetValue(sym.FamilyName, out var want)) continue;
    foreach (var kv in want) {
      var p = sym.LookupParameter(kv.Key); if (p == null) { log.Add("no parameter " + kv.Key + " on " + sym.Name); continue; }
      if (kv.Value is int iv) { if (p.AsInteger() != iv) { p.Set(iv); nType++; } }
      else { var m = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().First(x => x.Name == (string)kv.Value); if (p.AsElementId() != m.Id) { p.Set(m.Id); nType++; } }
    }
  }
  log.Add("options: " + t.Commit() + ", instance values changed " + nInst + ", type values changed " + nType + (pre.Msgs.Count > 0 ? " [" + string.Join(" | ", pre.Msgs.Distinct()) + "]" : ""));
}
return string.Join("\n", log);
