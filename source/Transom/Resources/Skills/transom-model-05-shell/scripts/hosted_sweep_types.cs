// hosted_sweep_types.cs - builds the roof fascia types and wall sweep types of a types spec (fascia_types, sweep_types
// written by dump_types.cs) in the target model, so a clean project needs no other model open: each type is duplicated
// from a template type of its kind and given its profile (a loaded profile family type, found by type name, e.g.
// "SA 1.5 x 7.875" of Fascia_Flat) and its material. Load the profiles first (family library or build_families.cs).
// Run through run_file.cs with ARGS { CFG, SPEC }. One transaction with a failure preprocessor. Safe to rerun.
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
var spec = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(@"{{SPEC}}")).RootElement;
var log = new List<string>();
ElementId Profile(string n) => new FilteredElementCollector(D).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
    .FirstOrDefault(s => s.Name == n && s.Category?.Id.Value == (long)BuiltInCategory.OST_ProfileFamilies)?.Id ?? ElementId.InvalidElementId;
ElementId Mat(string n) => new FilteredElementCollector(D).OfClass(typeof(Material)).FirstOrDefault(m => m.Name == n)?.Id ?? ElementId.InvalidElementId;
var pre = new Pre();
using (var t = new Transaction(D, "Fascia and sweep types from spec")) {
    var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
    t.Start();
    foreach (var (key, cat) in new[] { ("fascia_types", BuiltInCategory.OST_Fascia), ("sweep_types", BuiltInCategory.OST_Cornices) }) {
        if (!spec.TryGetProperty(key, out var rows)) continue;
        foreach (var r in rows.EnumerateArray()) {
            var name = r.GetProperty("name").GetString(); var prof = r.GetProperty("profile").GetString(); var mat = r.GetProperty("material").GetString();
            var all = new FilteredElementCollector(D).OfCategory(cat).WhereElementIsElementType().Cast<ElementType>().ToList();
            if (all.Count == 0) { log.Add($"no {cat} type in the model to duplicate for {name}"); continue; }
            var et = all.FirstOrDefault(x => x.Name == name) ?? all[0].Duplicate(name);
            var pid = Profile(prof); if (pid == ElementId.InvalidElementId) log.Add($"{name}: profile '{prof}' not loaded");
            et.LookupParameter("Profile")?.Set(pid);
            var mid = Mat(mat); if (mid != ElementId.InvalidElementId) et.LookupParameter("Material")?.Set(mid);
            log.Add($"{name}: profile {prof}, material {mat}");
        }
    }
    log.Add("commit: " + t.Commit() + (pre.Msgs.Count > 0 ? " [" + string.Join(" | ", pre.Msgs.Distinct()) + "]" : ""));
}
return string.Join("\n", log);
