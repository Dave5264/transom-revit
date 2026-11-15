// FAMILY AUDIT (execute_revit_code, readOnly: true). Lists every visual option on the families in use, so each one gets a decision instead of a default.
// Use before placing and again after any new family. FAMILIES empty = everything hosted in a wall now. Compare each option with a drafted unit and with
// a rendered close-up of that option. Type parameters are read from the first type of each family.
var FAMILIES = new string[] { };                                    // e.g. "Window-Fixed", when nothing is placed yet
var HIDE = new System.Text.RegularExpressions.Regex("(?i)ifc|omniclass|assembly|workset|comment|mark|phase|thermal|analytic|detail reference|family|^type|^level$|schedule|fire|light|solar|heat|construction");
var log = new List<string>();
var placed = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(f => f.Host is Wall && f.Category != null && (f.Category.Id.Value == (long)BuiltInCategory.OST_Windows || f.Category.Id.Value == (long)BuiltInCategory.OST_Doors)).ToList();
var names = FAMILIES.Length > 0 ? FAMILIES : placed.Select(f => f.Symbol.FamilyName).Distinct().ToArray();
foreach (var name in names) {
  var syms = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().Where(s => s.FamilyName == name).ToList();
  var fi = placed.FirstOrDefault(f => f.Symbol.FamilyName == name);
  var sym = fi != null ? fi.Symbol : syms.First();
  log.Add("=== " + name + " (" + placed.Count(f => f.Symbol.FamilyName == name) + " placed; types in use: " + string.Join(", ", placed.Where(f => f.Symbol.FamilyName == name).Select(f => f.Symbol.Name).Distinct()) + ")");
  void Dump(string label, Element e) {
    foreach (Parameter p in e.Parameters) {
      if (p.Definition == null || p.IsReadOnly || p.StorageType == StorageType.None || HIDE.IsMatch(p.Definition.Name)) continue;
      string v = p.StorageType == StorageType.ElementId ? (doc.GetElement(p.AsElementId())?.Name ?? "") : p.AsValueString();
      if (string.IsNullOrEmpty(v)) continue;
      string opts = "";
      if (p.StorageType == StorageType.ElementId) { try { var o = sym.Family.GetFamilyTypeParameterValues(p.Id).Select(i => doc.GetElement(i)).Select(x => x is FamilySymbol fs ? fs.FamilyName + ":" + fs.Name : x?.Name).Where(n => n != null).Take(14).ToList(); if (o.Count > 0) opts = "  options: " + string.Join(", ", o); } catch { } }
      log.Add("  " + label + " " + p.Definition.Name + " = " + v + opts);
    }
  }
  Dump("type", sym);
  if (fi != null) Dump("inst", fi);
}
return string.Join("\n", log);
