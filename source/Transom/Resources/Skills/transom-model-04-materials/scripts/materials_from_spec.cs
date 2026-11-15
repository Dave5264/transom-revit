// materials_from_spec.cs - builds every material in a materials spec (written by dump_materials.cs, or by hand from
// materials_spec_template.json) in the target model, so a project started from a plain template renders like the reference.
//   * Generic rows: the material gets its OWN appearance asset (named after the material), duplicated from a Generic
//     asset already in the model, or created from the library "Generic" asset when the template has none. Then: diffuse
//     colour, colour map and bump map with their real-world size, image fade, glossiness, reflectivity.
//     Shared assets are never edited in place: two materials pointing at one asset would change together.
//   * Other schemas (Prism, Metal, Wood, WallPaint) are never created here. Kept as they are when the template has them.
//     A family brings its own definition when it loads, and a project material of the same name overrides it: an opaque
//     placeholder named +Exterior Glass made every window opaque in the first test. A host type that needs a missing
//     one (a wall core, a slab) gets a plain placeholder from types_from_spec.cs instead.
//   * Maps that do not exist on disk are reported and left off.
// Run through run_file.cs with ARGS { CFG, SPEC }. One transaction per material, each with a failure preprocessor.
public class Pre : IFailuresPreprocessor
{
    public List<string> Msgs = new List<string>();
    public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
    {
        bool error = false;
        foreach (var f in a.GetFailureMessages().ToList())
        {
            Msgs.Add(f.GetSeverity() + ": " + f.GetDescriptionText());
            if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f); else error = true;
        }
        return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}
var CFG = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(@"{{CFG}}")).RootElement;
var D = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith(CFG.GetProperty("model_title").GetString()));
var spec = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(@"{{SPEC}}")).RootElement.GetProperty("materials");
var log = new List<string>();
bool IsGeneric(AppearanceAssetElement ae) { try { return ae.GetRenderingAsset().FindByName("generic_diffuse") != null; } catch { return false; } }
AppearanceAssetElement baseAe = null;
AppearanceAssetElement Base() {
    if (baseAe != null) return baseAe;
    baseAe = new FilteredElementCollector(D).OfClass(typeof(AppearanceAssetElement)).Cast<AppearanceAssetElement>().FirstOrDefault(IsGeneric);
    if (baseAe == null) {
        var lib = app.GetAssets(Autodesk.Revit.DB.Visual.AssetType.Appearance).FirstOrDefault(a => a.Name == "Generic" || a.Title == "Generic")
               ?? app.GetAssets(Autodesk.Revit.DB.Visual.AssetType.Appearance).First(a => a.FindByName("generic_diffuse") != null);
        baseAe = AppearanceAssetElement.Create(D, "SA Generic base", lib);
        log.Add("created base Generic asset from the library");
    }
    return baseAe;
}
string Str(System.Text.Json.JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
double? Num(System.Text.Json.JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDouble() : (double?)null;
Color Rgb(System.Text.Json.JsonElement e, string k) { if (!e.TryGetProperty(k, out var v) || v.ValueKind != System.Text.Json.JsonValueKind.Array) return null; var a = v.EnumerateArray().Select(x => (byte)x.GetInt32()).ToArray(); return new Color(a[0], a[1], a[2]); }
void Map(Autodesk.Revit.DB.Visual.AssetProperty prop, System.Text.Json.JsonElement m, List<string> notes) {
    var path = Str(m, "path");
    if (path == null) { if (prop.NumberOfConnectedProperties > 0) prop.RemoveConnectedAsset(); return; }
    bool local = path.Contains(":\\") || path.StartsWith("\\\\");
    if (local && !System.IO.File.Exists(path)) { notes.Add("missing map " + path); return; }
    if (prop.NumberOfConnectedProperties == 0) prop.AddConnectedAsset("UnifiedBitmapSchema");
    var bm = prop.GetSingleConnectedAsset();
    (bm.FindByName("unifiedbitmap_Bitmap") as Autodesk.Revit.DB.Visual.AssetPropertyString).Value = path;
    var w = Num(m, "width_in"); var h = Num(m, "height_in");
    if (w != null) (bm.FindByName("texture_RealWorldScaleX") as Autodesk.Revit.DB.Visual.AssetPropertyDistance).Value = w.Value;
    if (h != null) (bm.FindByName("texture_RealWorldScaleY") as Autodesk.Revit.DB.Visual.AssetPropertyDistance).Value = h.Value;
}
void SetD(Autodesk.Revit.DB.Visual.Asset a, string n, double? v) { if (v == null) return; if (a.FindByName(n) is Autodesk.Revit.DB.Visual.AssetPropertyDouble p) p.Value = v.Value; }
int made = 0, edited = 0, kept = 0, skipped = 0;
foreach (var row in spec.EnumerateArray()) {
    var name = Str(row, "name"); var schema = Str(row, "schema") ?? "Generic";
    var mat = new FilteredElementCollector(D).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == name);
    bool byTypes = !row.TryGetProperty("used_by_types", out var ub) || ub.GetBoolean();
    if (mat != null && schema != "Generic") { kept++; continue; }
    if (mat == null && schema != "Generic") { skipped++; continue; }
    var pre = new Pre(); var notes = new List<string>();
    using (var t = new Transaction(D, "Material " + name)) {
        var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
        t.Start();
        try {
            bool isNew = mat == null;
            if (isNew) mat = D.GetElement(Material.Create(D, name)) as Material;
            var ae = new FilteredElementCollector(D).OfClass(typeof(AppearanceAssetElement)).Cast<AppearanceAssetElement>().FirstOrDefault(x => x.Name == name);
            if (ae == null) ae = Base().Duplicate(name);
            using (var es = new Autodesk.Revit.DB.Visual.AppearanceAssetEditScope(D)) {
                var a = es.Start(ae.Id);
                var dif = a.FindByName("generic_diffuse") as Autodesk.Revit.DB.Visual.AssetPropertyDoubleArray4d;
                var col = Rgb(row, "diffuse_rgb") ?? Rgb(row, "shaded_rgb");
                if (col != null) dif.SetValueAsColor(col);
                if (row.TryGetProperty("color_map", out var cm) && cm.ValueKind == System.Text.Json.JsonValueKind.Object) Map(dif, cm, notes);
                else if (dif.NumberOfConnectedProperties > 0) dif.RemoveConnectedAsset();
                var bump = a.FindByName("generic_bump_map");
                if (bump != null) {
                    if (row.TryGetProperty("bump_map", out var bmj) && bmj.ValueKind == System.Text.Json.JsonValueKind.Object) { Map(bump, bmj, notes); SetD(a, "generic_bump_amount", Num(row, "bump_amount") ?? 1.0); }
                    else if (bump.NumberOfConnectedProperties > 0) bump.RemoveConnectedAsset();
                }
                SetD(a, "generic_diffuse_image_fade", Num(row, "image_fade"));
                SetD(a, "generic_glossiness", Num(row, "glossiness"));
                SetD(a, "generic_reflectivity_at_0deg", Num(row, "refl_0"));
                SetD(a, "generic_reflectivity_at_90deg", Num(row, "refl_90"));
                es.Commit(true);
            }
            mat.AppearanceAssetId = ae.Id;
            var sh = Rgb(row, "shaded_rgb"); if (sh != null) mat.Color = sh;
            mat.UseRenderAppearanceForShading = false;
            var cls = Str(row, "class"); if (!string.IsNullOrEmpty(cls)) mat.MaterialClass = cls;
            var st = t.Commit();
            if (isNew) made++; else edited++;
            if (notes.Count > 0 || pre.Msgs.Count > 0) log.Add($"{name}: {st} " + string.Join("; ", notes.Concat(pre.Msgs)));
        } catch (Exception ex) { if (t.GetStatus() == TransactionStatus.Started) t.RollBack(); log.Add(name + ": FAILED " + ex.Message); }
    }
}
log.Insert(0, $"materials: {made} created, {edited} updated, {kept} kept from the template, {skipped} non-Generic left to their families and types");
return string.Join("\n", log);
