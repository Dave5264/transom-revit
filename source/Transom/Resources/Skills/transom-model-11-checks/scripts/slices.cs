// LOOK: 3D VIEWS AND SECTION-BOX CUTS (execute_revit_code, readOnly: true). Temporary 3D views in a TransactionGroup that is rolled back. Keep
// each call to about 10 views at PX 1500: the bridge gives up at 30 s. Then READ every image. An exported image nobody looked at checks nothing.
const string OUT = @"<scratch>\views\";
const int PX = 1500;
const int SCALE = 48;                                 // 24 for close-ups
double X0 = -60, X1 = 1260, Y0 = -320, Y1 = 490, Z0 = -135, Z1 = 395;   // model inches, around the building
System.IO.Directory.CreateDirectory(OUT);
var shots = new List<Tuple<string, XYZ, XYZ, XYZ>>();  // name, box min, box max, eye direction
void All(string n, XYZ eye) => shots.Add(Tuple.Create(n, new XYZ(X0, Y0, Z0), new XYZ(X1, Y1, Z1), eye));
void CutX(double x) => shots.Add(Tuple.Create("cut_X" + x, new XYZ(x, Y0, Z0), new XYZ(X1, Y1, Z1), new XYZ(-1, -0.35, 0.3)));
void CutY(double y) => shots.Add(Tuple.Create("cut_Y" + y, new XYZ(X0, y, Z0), new XYZ(X1, Y1, Z1), new XYZ(-0.35, -1, 0.3)));
void CutZ(double z) => shots.Add(Tuple.Create("cut_Z" + z, new XYZ(X0, Y0, Z0), new XYZ(X1, Y1, z), new XYZ(-0.25, -0.45, 1)));
void Close(string n, double a, double b, double c, double d, double e, double f, XYZ eye) => shots.Add(Tuple.Create(n, new XYZ(a, c, e), new XYZ(b, d, f), eye));
// Every call: pick what this phase needs. The four corners, a cut through each mass and each junction, a plan cut per storey and one through the roofs.
All("iso_SE", new XYZ(1, -1, 0.6)); All("iso_SW", new XYZ(-1, -1, 0.6)); All("iso_NE", new XYZ(1, 1, 0.6)); All("iso_NW", new XYZ(-1, 1, 0.6));
CutX(190); CutX(440); CutX(760); CutX(1100); CutY(60); CutY(300);
var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(x => x.ViewFamily == ViewFamily.ThreeDimensional);
var log = new List<string>();
using (var tg = new TransactionGroup(doc, "temp 3D checks")) {
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
      v.DisplayStyle = DisplayStyle.ShadingWithEdges; v.DetailLevel = ViewDetailLevel.Fine; v.Scale = SCALE; v.AreAnnotationCategoriesHidden = true;
      foreach (var bic in new[] { BuiltInCategory.OST_Toposolid, BuiltInCategory.OST_Topography, BuiltInCategory.OST_Site, BuiltInCategory.OST_Planting }) {
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
return string.Join(", ", log) + " | temp left=" + new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Count(x => x.Name.StartsWith("ZZ_TEMP_"));
