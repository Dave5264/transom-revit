// export_families.cs - READ ONLY on the model (EditFamily opens a copy). Saves every loadable family the model uses -
// placed instances (windows, doors, planting, generic models, site), profiles used by railing, fascia and sweep types,
// and baluster/post families - to a library folder, so a new project can load exactly the same families.
// Long: it logs to OUT\export_log.txt as it goes; if the bridge times out, poll Revit until it responds, then read the log.
// Run through run_file.cs. ARGS: TARGET = title prefix of the model to read, OUT = library folder.
var TARGET = "{{TARGET}}";
var OUT = @"{{OUT}}";
var d = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith(TARGET));
System.IO.Directory.CreateDirectory(OUT);
var logPath = System.IO.Path.Combine(OUT, "export_log.txt");
System.IO.File.WriteAllText(logPath, $"export from {d.Title} {DateTime.Now:s}\n");
void L(string s) => System.IO.File.AppendAllText(logPath, s + "\n");
var fams = new Dictionary<long, Family>();
void AddSym(ElementId sid) { if (sid == null || sid == ElementId.InvalidElementId) return; if (d.GetElement(sid) is FamilySymbol fs && fs.Family != null && !fs.Family.IsInPlace) fams[fs.Family.Id.Value] = fs.Family; }
foreach (var i in new FilteredElementCollector(d).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()) if (!i.Symbol.Family.IsInPlace) fams[i.Symbol.Family.Id.Value] = i.Symbol.Family;
var usedTypes = new HashSet<long>();
foreach (var e in new FilteredElementCollector(d).WhereElementIsNotElementType().ToElements()) { var t = e.GetTypeId(); if (t != null && t != ElementId.InvalidElementId) usedTypes.Add(t.Value); }
foreach (var tid in usedTypes) {
    var t = d.GetElement(new ElementId(tid)); if (t == null) continue;
    foreach (Parameter p in t.Parameters) if (p.StorageType == StorageType.ElementId) AddSym(p.AsElementId());   // profiles, posts, balusters, rail types
    if (t is Autodesk.Revit.DB.Architecture.RailingType rt) {
        try { var rs = rt.RailStructure; for (int k = 0; k < rs.GetNonContinuousRailCount(); k++) AddSym(rs.GetNonContinuousRail(k).ProfileId); } catch { }
        try { var pat = rt.BalusterPlacement.BalusterPattern; for (int k = 0; k < pat.GetBalusterCount(); k++) AddSym(pat.GetBaluster(k).BalusterFamilyId); } catch { }
        try { var pp = rt.BalusterPlacement.PostPattern; AddSym(pp.StartPost.BalusterFamilyId); AddSym(pp.EndPost.BalusterFamilyId); AddSym(pp.CornerPost.BalusterFamilyId); } catch { }
        try { var top = d.GetElement(rt.TopRailType); if (top != null) foreach (Parameter p in top.Parameters) if (p.StorageType == StorageType.ElementId) AddSym(p.AsElementId()); } catch { }
    }
}
int ok = 0, fail = 0;
foreach (var f in fams.Values.OrderBy(f => f.Name)) {
    if (!f.IsEditable) { L("skip (not editable) " + f.Name); continue; }
    Document fd = null;
    try {
        fd = d.EditFamily(f);
        var path = System.IO.Path.Combine(OUT, string.Concat(f.Name.Split(System.IO.Path.GetInvalidFileNameChars())) + ".rfa");
        fd.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 });
        L($"saved {f.FamilyCategory?.Name} | {f.Name} | {f.GetFamilySymbolIds().Count} types");
        ok++;
    } catch (Exception ex) { L("FAILED " + f.Name + ": " + ex.Message); fail++; }
    finally { try { fd?.Close(false); } catch { } }
}
L($"done: {ok} saved, {fail} failed");
return $"{ok} families saved, {fail} failed -> {OUT} (log export_log.txt)";
