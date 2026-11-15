// CHECK images (execute_revit_code, readOnly: true). ONE view per call: exports over ~4000 px blow the 30 s bridge cap.
// Elevation/plan: the view's CAD goes red and windows/doors blue inside a TransactionGroup that is rolled back, so the
// user's view graphics are untouched. A plan can be cropped to a strip (CROP = model feet x0,y0,x1,y1) for detail.
// 3D: exported as is. Then crop/zoom the PNG with PIL and compare every opening with the red linework.
const string OUT = @"<scratchpad>\views";
const long VIEW_ID = 0;
double[] CROP = null;                 // e.g. new double[] { -3, -24.5, 52, -7.5 } for a plan strip
const int PX = 3000;
var v = (View)doc.GetElement(new ElementId(VIEW_ID));
System.IO.Directory.CreateDirectory(OUT);
using (var g = new TransactionGroup(doc, "check export")) {
  g.Start();
  if (!(v is View3D)) using (var t = new Transaction(doc, "temp overrides")) {
    t.Start();
    foreach (var ii in new FilteredElementCollector(doc, v.Id).OfClass(typeof(ImportInstance)).ToElementIds())
      v.SetElementOverrides(ii, new OverrideGraphicSettings().SetProjectionLineColor(new Color(220, 0, 0)));
    var blue = new OverrideGraphicSettings().SetProjectionLineColor(new Color(0, 120, 255)).SetCutLineColor(new Color(0, 120, 255)).SetProjectionLineWeight(3).SetCutLineWeight(3);
    v.SetCategoryOverrides(new ElementId(BuiltInCategory.OST_Windows), blue); v.SetCategoryOverrides(new ElementId(BuiltInCategory.OST_Doors), blue);
    if (CROP != null) {
      var cb = v.CropBox; var tr = cb.Transform.Inverse;
      var a = tr.OfPoint(new XYZ(CROP[0], CROP[1], 0)); var b = tr.OfPoint(new XYZ(CROP[2], CROP[3], 0));
      cb.Min = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), cb.Min.Z); cb.Max = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), cb.Max.Z);
      v.CropBox = cb; v.CropBoxActive = true;
    }
    t.Commit();
  }
  var opt = new ImageExportOptions { ExportRange = ExportRange.SetOfViews, FilePath = System.IO.Path.Combine(OUT, "check"),
    HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG, ImageResolution = ImageResolution.DPI_150,
    ZoomType = ZoomFitType.FitToPage, PixelSize = PX, FitDirection = FitDirectionType.Horizontal };
  opt.SetViewsAndSheets(new List<ElementId> { v.Id });
  doc.ExportImage(opt);
  g.RollBack();
}
return System.IO.Directory.GetFiles(OUT, "check*").OrderByDescending(f => System.IO.File.GetLastWriteTime(f)).First();
