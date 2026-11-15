// LOOKS: close-up 3D views in a rolled-back TransactionGroup (execute_revit_code, readOnly: true). Edit SHOTS, keep to ~6 per call.
const string OUT = @"<scratch>\looks\";
const int PX = 1200;
System.IO.Directory.CreateDirectory(OUT);
var shots = new List<Tuple<string, XYZ, XYZ, XYZ>>();
void Close(string n, double a, double b, double c, double d, double e, double f, XYZ eye) => shots.Add(Tuple.Create(n, new XYZ(a, c, e), new XYZ(b, d, f), eye));
//SHOTS
var style = DisplayStyle.ShadingWithEdges;
//STYLE
var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(x => x.ViewFamily == ViewFamily.ThreeDimensional);
var log = new List<string>();
using (var tg = new TransactionGroup(doc, "temp looks")) {
  tg.Start();
  var made = new List<KeyValuePair<string, ElementId>>();
  using (var t = new Transaction(doc, "temp views")) {
    t.Start();
    foreach (var s in shots) {
      var v = View3D.CreateIsometric(doc, vft.Id);
      v.Name = "ZZ_TEMP_" + s.Item1;
      var sb = new BoundingBoxXYZ(); sb.Min = s.Item2 / 12; sb.Max = s.Item3 / 12; v.SetSectionBox(sb);
      var eye = s.Item4.Normalize(); var c = (s.Item2 + s.Item3) / 24; var fwd = -eye;
      var right = fwd.CrossProduct(XYZ.BasisZ).Normalize(); var up = right.CrossProduct(fwd).Normalize();
      v.SetOrientation(new ViewOrientation3D(c + eye * 300, up, fwd));
      v.DisplayStyle = style; v.DetailLevel = ViewDetailLevel.Fine; v.Scale = 24; v.AreAnnotationCategoriesHidden = true;
      foreach (var bic in new[] { BuiltInCategory.OST_Toposolid, BuiltInCategory.OST_Topography, BuiltInCategory.OST_Site, BuiltInCategory.OST_Planting, BuiltInCategory.OST_SectionBox }) {
        var cat = doc.Settings.Categories.get_Item(bic);
        if (cat != null && v.CanCategoryBeHidden(cat.Id)) v.SetCategoryHidden(cat.Id, true);
      }
      made.Add(new KeyValuePair<string, ElementId>(s.Item1, v.Id));
    }
    t.Commit();
  }
  foreach (var m in made) {
    var opt = new ImageExportOptions { ZoomType = ZoomFitType.FitToPage, PixelSize = PX, FilePath = OUT + m.Key, FitDirection = FitDirectionType.Horizontal, HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG, ExportRange = ExportRange.SetOfViews };
    opt.SetViewsAndSheets(new List<ElementId> { m.Value });
    doc.ExportImage(opt); log.Add(m.Key);
  }
  tg.RollBack();
}
return string.Join(", ", log);
