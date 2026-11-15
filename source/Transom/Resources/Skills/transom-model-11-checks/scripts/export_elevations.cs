// EXPORT ELEVATIONS (execute_revit_code, readOnly: true). One DWG per elevation, hidden lines removed, from temporary duplicates inside a
// TransactionGroup that is rolled back. A plain Duplicate carries no view-specific CAD, so only the model is exported.
const string OUT = @"<scratch>\rvt_elev_export";
var FACES = new[] { "SOUTH", "NORTH", "EAST", "WEST" };                     // view name = FACE + SUFFIX
const string SUFFIX = " ELEVATION";
System.IO.Directory.CreateDirectory(OUT);
var log = new List<string>();
using (var tg = new TransactionGroup(doc, "temp elevation export")) {
  tg.Start();
  var made = new List<KeyValuePair<string, ElementId>>();
  using (var t = new Transaction(doc, "temp duplicates")) {
    t.Start();
    foreach (var name in FACES) {
      var v = new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>().First(x => !x.IsTemplate && x.Name == name + SUFFIX);
      var dupId = v.Duplicate(ViewDuplicateOption.Duplicate);
      var d = (View)doc.GetElement(dupId);
      d.Name = "ZZ_TEMP_EXP_" + name;
      d.AreAnnotationCategoriesHidden = true;
      foreach (var bic in new[] { BuiltInCategory.OST_Toposolid, BuiltInCategory.OST_Topography, BuiltInCategory.OST_Site, BuiltInCategory.OST_Planting, BuiltInCategory.OST_Entourage }) {
        var c = doc.Settings.Categories.get_Item(bic);
        if (c != null && d.CanCategoryBeHidden(c.Id)) d.SetCategoryHidden(c.Id, true);
      }
      made.Add(new KeyValuePair<string, ElementId>(name, dupId));
    }
    t.Commit();
  }
  foreach (var m in made) {
    var opt = new DWGExportOptions { MergedViews = true, FileVersion = ACADVersion.R2018, SharedCoords = false, ExportingAreas = false, TargetUnit = ExportUnit.Inch };
    log.Add("export " + m.Key + " " + doc.Export(OUT, "RVT_" + m.Key, new List<ElementId> { m.Value }, opt));
  }
  tg.RollBack();
}
return string.Join("\n", log) + "\ntemp left=" + new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(x => x.Name.StartsWith("ZZ_TEMP_"));
