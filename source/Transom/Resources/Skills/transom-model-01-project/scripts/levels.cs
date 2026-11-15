// levels.cs - the elevation datum and the levels, as the first project's model has them:
//   * internal Z 0 = the main floor; the Project Base Point stays at the internal origin with Elev = datum_ft (100'),
//     which puts the Survey Point datum_ft below it;
//   * every level type measures from the Survey Point, so a level at internal Z 0 reads 100'-0";
//   * levels are created at internal Z = elev_ft - datum_ft (Level.Create takes INTERNAL elevation: on the first
//     project Level.Create(doc, 90) made a level that read 190'), then named; template levels not in the config are
//     deleted together with their views;
//   * one floor plan per level, named from the config.
// Run through run_file.cs with ARGS { CFG }. Own transaction with a failure preprocessor.
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
double datum = CFG.GetProperty("datum_ft").GetDouble();
var want = CFG.GetProperty("levels").EnumerateArray().Select(e => (name: e.GetProperty("name").GetString(), elev: e.GetProperty("elev_ft").GetDouble(), plan: e.GetProperty("plan").GetString())).ToList();
var log = new List<string>();
var pre = new Pre();
using (var t = new Transaction(D, "Datum, levels and plan views")) {
    var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
    t.Start();
    // 1. datum: move the SHARED coordinates so the internal origin sits at elevation `datum`. Setting the Project Base
    //    Point's Elev parameter instead moves the base point itself up `datum` feet (tested: it went to internal Z 100)
    //    and the levels still read 0'. Put the base point back on the internal origin first, then shift the location.
    var pbp = BasePoint.GetProjectBasePoint(D);
    if (Math.Abs(pbp.Position.Z) > 1e-6) pbp.get_Parameter(BuiltInParameter.BASEPOINT_ELEVATION_PARAM).Set(pbp.SharedPosition.Z - pbp.Position.Z);
    var pos = D.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
    D.ActiveProjectLocation.SetProjectPosition(XYZ.Zero, new ProjectPosition(pos.EastWest, pos.NorthSouth, datum, pos.Angle));
    D.Regenerate();
    log.Add($"datum: base point at internal Z {pbp.Position.Z:0.###}, shared Z {pbp.SharedPosition.Z:0.###}; survey point at internal Z {BasePoint.GetSurveyPoint(D).Position.Z:0.###}");
    // 2. every level type measures from the Survey Point (1); 0 would be the Project Base Point
    foreach (var lt in new FilteredElementCollector(D).OfClass(typeof(LevelType)).Cast<LevelType>())
        lt.get_Parameter(BuiltInParameter.LEVEL_RELATIVE_BASE_TYPE)?.Set(1);
    // 3. levels: a level already named right is kept; otherwise the template level nearest the wanted height is reused
    //    (renamed and moved, so its plan views come along; deleting a template level fails while its plan is the
    //    active view); the rest are created. Template levels left over are reported, never deleted unasked.
    var existing = new FilteredElementCollector(D).OfClass(typeof(Level)).Cast<Level>().ToList();
    var keep = new Level[want.Count];
    for (int i = 0; i < want.Count; i++) keep[i] = existing.FirstOrDefault(l => l.Name == want[i].name);
    var spare = existing.Where(l => !keep.Contains(l) && !want.Any(w => w.name == l.Name)).ToList();
    foreach (var i in Enumerable.Range(0, want.Count).Where(i => keep[i] == null).OrderBy(i => spare.Count == 0 ? 0 : spare.Min(l => Math.Abs(l.ProjectElevation - (want[i].elev - datum))))) {
        var near = spare.OrderBy(l => Math.Abs(l.ProjectElevation - (want[i].elev - datum))).FirstOrDefault();
        if (near != null && Math.Abs(near.ProjectElevation - (want[i].elev - datum)) < 6.0) { keep[i] = near; spare.Remove(near); log.Add($"reused template level '{near.Name}' for {want[i].name}"); }
    }
    for (int i = 0; i < want.Count; i++) {
        var lv = keep[i] ?? Level.Create(D, want[i].elev - datum);
        lv.Name = want[i].name;
        // Level.Elevation and LEVEL_ELEV are measured from the level type's base (here the Survey Point), so setting
        // them to an internal value lands datum_ft too low (tested). Move by the difference in INTERNAL Z instead.
        lv.Elevation = lv.Elevation + ((want[i].elev - datum) - lv.ProjectElevation);
        keep[i] = lv;
    }
    if (spare.Count > 0) log.Add("template levels left (not deleted): " + string.Join(", ", spare.Select(l => l.Name)));
    // 4. a floor plan per level
    var vft = new FilteredElementCollector(D).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.FloorPlan);
    var plans = new FilteredElementCollector(D).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan).ToList();
    for (int i = 0; i < want.Count; i++) {
        var lv = keep[i];
        var vp = plans.FirstOrDefault(v => v.Name == want[i].plan) ?? plans.FirstOrDefault(v => v.GenLevel?.Id == lv.Id && v.Name == lv.Name);
        if (vp == null) vp = ViewPlan.Create(D, vft.Id, lv.Id);
        vp.Name = want[i].plan;
    }
    log.Add("commit: " + t.Commit() + (pre.Msgs.Count > 0 ? " [" + string.Join(" | ", pre.Msgs.Distinct()) + "]" : ""));
}
foreach (var l in new FilteredElementCollector(D).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation))
    log.Add($"{l.Name}: internal {l.ProjectElevation:0.###} ft, reads {l.get_Parameter(BuiltInParameter.LEVEL_ELEV).AsValueString()} (Elevation {l.Elevation:0.###})");
log.Add("plans: " + string.Join(", ", new FilteredElementCollector(D).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate).Select(v => v.Name + " [" + v.ViewType + "]")));
return string.Join("\n", log);
