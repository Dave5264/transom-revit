// ATTACH WALLS TO ROOFS (execute_revit_code, readOnly: true; own transaction). Attach only after the roofs are final.
// A wall gets an attachment to each roof whose footprint box covers its line for at least MIN_FRACTION of its length. A low roof passing beside a taller wall
// would cut that wall off, so name those pairs in SKIP. Read the printed pairs before trusting the result, then check with the envelope check and by looking.
public class Pre : IFailuresPreprocessor
{
    public List<string> Msgs = new List<string>();
    public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
    {
        bool error = false;
        foreach (var f in a.GetFailureMessages().ToList())
        {
            Msgs.Add(f.GetSeverity() + ": " + f.GetDescriptionText() + " {" + string.Join(",", f.GetFailingElementIds().Select(i => i.Value)) + "}");
            if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f); else error = true;
        }
        return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}
// Run through run_file.cs with ARGS { CFG, SKIP_ROOFS }. SKIP_ROOFS = comma-separated comment prefixes of roofs that must
// never take a wall (ridge vents and caps sit over the gables and would grab them), e.g. "SHELL RIDGE".
var CFG = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(@"{{CFG}}")).RootElement;
var D = app.Documents.Cast<Document>().First(x => !x.IsLinked && x.Title.StartsWith(CFG.GetProperty("model_title").GetString()));
var SKIP_ROOFS = "{{SKIP_ROOFS}}".Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
string COMMENT_PREFIX = CFG.GetProperty("tag").GetString();   // walls to attach
const double XMIN = -1e9, XMAX = 1e9, YMIN = -1e9, YMAX = 1e9;   // model inches
const double TOP_MIN = 100, MIN_FRACTION = 0.25;        // TOP_MIN: skip walls whose top is below this model Z (inches)
Func<Wall, Element, bool> SKIP = (w, r) => SKIP_ROOFS.Any(p => (r.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").StartsWith(p));   // add project rules here, e.g. r.Id.Value == 123 && w.get_BoundingBox(null).Max.Z * 12 > 140
var BASE = new long[][] { };                            // { wall id, roof id } pairs whose BASE sits on the roof
var log = new List<string>();
bool InBox(XYZ p) => p.X * 12 >= XMIN && p.X * 12 <= XMAX && p.Y * 12 >= YMIN && p.Y * 12 <= YMAX;
var walls = new FilteredElementCollector(D).OfClass(typeof(Wall)).Cast<Wall>()
  .Where(w => (w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).AsString() ?? "").StartsWith(COMMENT_PREFIX))
  .Where(w => { var c = ((LocationCurve)w.Location).Curve; return InBox(c.GetEndPoint(0)) && InBox(c.GetEndPoint(1)); })
  .Where(w => w.get_BoundingBox(null).Max.Z * 12 > TOP_MIN).ToList();
var roofs = new FilteredElementCollector(D).OfCategory(BuiltInCategory.OST_Roofs).WhereElementIsNotElementType().ToList();
var pre = new Pre(); int added = 0;
using (var t = new Transaction(D, "Attach walls to roofs")) {
  var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); fo.SetClearAfterRollback(true); t.SetFailureHandlingOptions(fo);
  t.Start();
  foreach (var w in walls) {
    var c = ((LocationCurve)w.Location).Curve; var a = c.GetEndPoint(0); var b = c.GetEndPoint(1);
    double wx0 = Math.Min(a.X, b.X), wx1 = Math.Max(a.X, b.X), wy0 = Math.Min(a.Y, b.Y), wy1 = Math.Max(a.Y, b.Y), z0 = w.get_BoundingBox(null).Min.Z;
    bool alongX = wx1 - wx0 > wy1 - wy0; double len = Math.Max(wx1 - wx0, wy1 - wy0);
    var got = new List<long>();
    foreach (var r in roofs) {
      var bb = r.get_BoundingBox(null); if (bb == null || bb.Max.Z <= z0 || SKIP(w, r)) continue;
      double ox = Math.Min(wx1, bb.Max.X) - Math.Max(wx0, bb.Min.X), oy = Math.Min(wy1, bb.Max.Y) - Math.Max(wy0, bb.Min.Y);
      if ((alongX && ox >= MIN_FRACTION * len && wy0 >= bb.Min.Y && wy0 <= bb.Max.Y) || (!alongX && oy >= MIN_FRACTION * len && wx0 >= bb.Min.X && wx0 <= bb.Max.X)) {
        try { w.AddAttachment(r.Id, AttachmentLocation.Top); added++; got.Add(r.Id.Value); } catch (Exception ex) { log.Add("attach " + w.Id.Value + " to " + r.Id.Value + ": " + ex.Message); }
      }
    }
    log.Add(w.Id.Value + " [" + w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).AsString() + "] -> " + string.Join(",", got));
  }
  foreach (var p in BASE) (D.GetElement(new ElementId(p[0])) as Wall).AddAttachment(new ElementId(p[1]), AttachmentLocation.Base);
  log.Add("attach: " + t.Commit() + " " + added + " on " + walls.Count + " walls" + (pre.Msgs.Count > 0 ? " [" + string.Join(" | ", pre.Msgs.Distinct()) + "]" : ""));
}
return string.Join("\n", log);
