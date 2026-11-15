// dump_materials.cs - READ ONLY. Writes the render definition of every material the model's types and instances use
// (shaded colour, class, and the Generic appearance: tint/diffuse colour, colour map and bump map with their real-world
// size, glossiness, reflectivity, and whether the project's own types use it) to a json spec that materials_from_spec.cs rebuilds in a new project.
// Run through run_file.cs. ARGS: TARGET = title prefix of the model to read, OUT = json path.
var TARGET = "{{TARGET}}";
var OUT = @"{{OUT}}";
var d = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith(TARGET));
var used = new HashSet<long>();
var byTypes = new HashSet<long>();   // materials the project's own types use (layers, type material parameters)
void AddCs(CompoundStructure cs) { if (cs == null) return; foreach (var l in cs.GetLayers()) if (l.MaterialId != ElementId.InvalidElementId) { used.Add(l.MaterialId.Value); byTypes.Add(l.MaterialId.Value); } }
var usedTypes = new HashSet<long>();
foreach (var e in new FilteredElementCollector(d).WhereElementIsNotElementType().ToElements()) {
    var t = e.GetTypeId(); if (t != null && t != ElementId.InvalidElementId) usedTypes.Add(t.Value);
    try { foreach (var m in e.GetMaterialIds(false)) used.Add(m.Value); } catch { }
}
foreach (var ds in new FilteredElementCollector(d).OfClass(typeof(DirectShape)).Cast<DirectShape>())   // direct shapes carry
    foreach (var go in ds.get_Geometry(new Options())) {                                                      // materials per face
        if (go is Solid so) foreach (Face f in so.Faces) { if (f.MaterialElementId != ElementId.InvalidElementId) used.Add(f.MaterialElementId.Value); }
        else if (go is Mesh me && me.MaterialElementId != ElementId.InvalidElementId) used.Add(me.MaterialElementId.Value);
    }
foreach (var id in usedTypes) {
    var t = d.GetElement(new ElementId(id));
    if (t is HostObjAttributes h) AddCs(h.GetCompoundStructure());
    foreach (Parameter p in t?.Parameters ?? new ParameterSet()) if (p.Definition != null && p.StorageType == StorageType.ElementId && p.Definition.GetDataType() == SpecTypeId.Reference.Material) { var m = p.AsElementId(); if (m != ElementId.InvalidElementId) { used.Add(m.Value); byTypes.Add(m.Value); } }
}
object Map(Autodesk.Revit.DB.Visual.AssetProperty prop) {
    if (prop == null || prop.NumberOfConnectedProperties == 0) return null;
    var bm = prop.GetSingleConnectedAsset(); if (bm == null) return null;
    var path = (bm.FindByName("unifiedbitmap_Bitmap") as Autodesk.Revit.DB.Visual.AssetPropertyString)?.Value;
    var sx = (bm.FindByName("texture_RealWorldScaleX") as Autodesk.Revit.DB.Visual.AssetPropertyDistance)?.Value;
    var sy = (bm.FindByName("texture_RealWorldScaleY") as Autodesk.Revit.DB.Visual.AssetPropertyDistance)?.Value;
    return new Dictionary<string, object> { ["path"] = path, ["width_in"] = sx, ["height_in"] = sy };
}
double? Dbl(Autodesk.Revit.DB.Visual.Asset a, string n) => (a?.FindByName(n) as Autodesk.Revit.DB.Visual.AssetPropertyDouble)?.Value;
var rows = new List<Dictionary<string, object>>();
foreach (var id in used.OrderBy(x => x)) {
    var m = d.GetElement(new ElementId(id)) as Material; if (m == null) continue;
    var row = new Dictionary<string, object> { ["name"] = m.Name, ["class"] = m.MaterialClass,
        ["shaded_rgb"] = new[] { (int)m.Color.Red, m.Color.Green, m.Color.Blue }, ["transparency"] = m.Transparency,
        ["used_by_types"] = byTypes.Contains(id) };   // false = only inside loaded families: they bring their own definition
    var ae = d.GetElement(m.AppearanceAssetId) as AppearanceAssetElement;
    var a = ae?.GetRenderingAsset();
    if (a != null) {
        row["appearance"] = ae.Name;
        var dif = a.FindByName("generic_diffuse") as Autodesk.Revit.DB.Visual.AssetPropertyDoubleArray4d;
        row["schema"] = a.FindByName("generic_diffuse") != null ? "Generic" : a.Name;
        if (dif != null) { var c = dif.GetValueAsColor(); row["diffuse_rgb"] = new[] { (int)c.Red, c.Green, c.Blue }; row["color_map"] = Map(dif); }
        row["bump_map"] = Map(a.FindByName("generic_bump_map"));
        row["bump_amount"] = Dbl(a, "generic_bump_amount"); row["image_fade"] = Dbl(a, "generic_diffuse_image_fade");
        row["glossiness"] = Dbl(a, "generic_glossiness"); row["refl_0"] = Dbl(a, "generic_reflectivity_at_0deg"); row["refl_90"] = Dbl(a, "generic_reflectivity_at_90deg");
    }
    rows.Add(row);
}
System.IO.File.WriteAllText(OUT, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["model"] = d.Title, ["materials"] = rows }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
return $"{rows.Count} materials ({rows.Count(r => r.ContainsKey("schema") && (string)r["schema"] == "Generic")} Generic) -> {OUT}";
