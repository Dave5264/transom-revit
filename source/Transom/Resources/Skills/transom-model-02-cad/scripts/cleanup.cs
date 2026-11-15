// cleanup.cs - execute_autocad_code (document context) on the -Clean copy. DRY_RUN = true with readOnly:true reports what
// would go; then DRY_RUN = false. Rule: delete linework that will not physically exist when the project is built; keep
// everything that will (site included). Never decides by layer name for building objects.
var DRY_RUN = {{DRY_RUN}};   // ARGS: true = report only (pair with readOnly: true), false = erase

var IC = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
var annoTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    "ANNO","IDEN","NOTE","NOTES","TEXT","TXT","DIM","DIMS","DIMEN","DIMENSION","DIMENSIONS","SYMB","SYMBOL","SYMBOLS",
    "TAG","TAGS","KEYN","KEYNOTE","KEYNOTES","REVS","REVISION","REVISIONS","CLOUD","TTLB","TITLE","TITLEBLOCK",
    "BORDER","BORD","NPLT","NOPLOT","NOPLT","VPORT","VPORTS","VIEWPORT","VIEWPORTS","LEGEND","PATT","HATCH","HTCH",
    "DEFPOINTS","CALLOUT","CALLOUTS","LEADER","LEADERS",
    "SETBACK","SETBACKS","SETB","EASEMENT","EASEMENTS","ESMT","ESMNT","ZONING","MATCHLINE","DEMO","DEMOLITION",
    "CONSTRUCTIONLINES","CONSTRUCTIONLINE","CLINE","CLINES","XLINE","XLINES" };
var nonPhysical = new System.Text.RegularExpressions.Regex(
    @"^[A-Z]{1,2}-PROP(-|$)|PROP(ERTY)?[-_ ]?LINE|LOT[-_ ]?LINE|ROW[-_ ]?LINE|^[A-Z]{1,2}-GRID(-|$)", IC);
Func<string, bool> annoLayer = layer =>
{
    var n = layer.Contains("|") ? layer.Substring(layer.LastIndexOf('|') + 1) : layer;
    if (nonPhysical.IsMatch(n)) return true;
    foreach (var tok in n.Split(new[] { '-', '_', ' ', '.', '$', '~' }, StringSplitOptions.RemoveEmptyEntries))
        if (annoTokens.Contains(tok)) return true;
    return false;
};
var annoBlockName = new System.Text.RegularExpressions.Regex(
    @"\b(section|sect|elevation|elev|callout|north|arrow|tag|title|titleblock|border|keynote|revision|rev ?cloud|bubble|grid ?head|match ?line|scale ?bar|key ?plan|level ?(mark|head)|datum|target)\b", IC);
var aecAnno = new System.Text.RegularExpressions.Regex(
    @"^AecDb(Dim|ScheduleTable|Space|Zone|AreaGroup|Area|BdgSectionLine|BdgElevLine|BdgSection|ClipVolume|Camera|ColumnGrid)$", IC);
var aecBuilding = new System.Text.RegularExpressions.Regex(
    @"^AecDb(Wall|Door|Window|WindowAssembly|CurtainWall|Opening|Slab|RoofSlab|Roof|Stair|Railing|Column|Member|Beam|Brace)$", IC);
var tagStyle = new System.Text.RegularExpressions.Regex(
    @"tag|callout|section|elevation|title|north|bubble|label|keynote|revision|datum", IC);

// AEC MV blocks are tags when anchored by a tag/bubble anchor, or when their style is named like one.
// The AEC managed types aren't referenced by the script host, so this goes through reflection.
Func<Entity, bool> isTagMvb = e =>
{
    var ty = e.GetType();
    try
    {
        if (ty.GetProperty("IsAnchored")?.GetValue(e) as bool? == true)
        {
            var an = ((ObjectId)ty.GetProperty("AnchorId").GetValue(e)).ObjectClass.DxfName ?? "";
            if (an.Contains("TAG") || an.Contains("BUB")) return true;
        }
        var style = tr.GetObject((ObjectId)ty.GetProperty("StyleId").GetValue(e), OpenMode.ForRead);
        return tagStyle.IsMatch(style.GetType().GetProperty("Name")?.GetValue(style) as string ?? "");
    }
    catch { return false; }
};

// Why an entity is non-physical, or null. inheritAnno: inside a block, layer 0 takes the insert's layer.
Func<Entity, ObjectId, bool, string> annoReason = (e, id, inheritAnno) =>
{
    string cls = id.ObjectClass.Name, dxf = id.ObjectClass.DxfName ?? "";
    if (e is DBText || e is MText) return "text";
    if (e is Dimension) return "dimension";
    if (e is Leader || e is MLeader) return "leader";
    if (e is Table) return "table";
    if (e is FeatureControlFrame) return "tolerance";
    if (e is Wipeout) return "wipeout";
    if (e is RasterImage) return "image";
    if (e is UnderlayReference) return "underlay";
    if (e is Ole2Frame) return "ole";
    if (e is DBPoint) return "point";
    if (e is Ray || e is Xline) return "construction line";
    if (e is Hatch || e is Solid || dxf == "MPOLYGON") return "hatch/fill";
    if (aecAnno.IsMatch(cls)) return "aec annotation";
    if (cls == "AecDbMvBlockRef" && isTagMvb(e)) return "aec tag";
    if (e is BlockReference || cls.StartsWith("AecDb")) return null;   // blocks are scored; building objects never go by layer
    bool onAnno = e.Layer == "0" ? inheritAnno : annoLayer(e.Layer);
    return onAnno ? "non-physical layer" : null;
};

// Recursive content score of a block definition: (physical, non-physical, holds a building object).
var memo = new Dictionary<string, Tuple<int, int, bool>>();
Func<ObjectId, bool, int, Tuple<int, int, bool>> score = null;
score = (btrId, inheritAnno, depth) =>
{
    var key = btrId.Handle + "|" + inheritAnno;
    if (memo.TryGetValue(key, out var s)) return s;
    memo[key] = Tuple.Create(0, 0, false);
    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
    if (btr.IsFromExternalReference || depth > 20) return memo[key] = Tuple.Create(1, 0, false);
    int geo = 0, anno = 0; bool bldg = false;
    foreach (ObjectId id in btr)
    {
        var e = tr.GetObject(id, OpenMode.ForRead) as Entity; if (e == null) continue;
        if (annoReason(e, id, inheritAnno) != null) { anno++; continue; }
        if (e is BlockReference br)
        {
            var sc = score(br.BlockTableRecord, e.Layer == "0" ? inheritAnno : annoLayer(e.Layer), depth + 1);
            geo += sc.Item1; anno += sc.Item2; bldg |= sc.Item3; continue;
        }
        geo++;
        if (aecBuilding.IsMatch(id.ObjectClass.Name)) bldg = true;
    }
    return memo[key] = Tuple.Create(geo, anno, bldg);
};
Func<BlockReference, string> blockName = br =>
    br.IsDynamicBlock ? ((BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)).Name : br.Name;

var report = new SortedDictionary<string, int>();
Action<string, Entity, ObjectId> count = (reason, e, id) =>
{
    var k = reason + " | " + e.Layer + " | " + (string.IsNullOrEmpty(id.ObjectClass.DxfName) ? id.ObjectClass.Name : id.ObjectClass.DxfName);
    report[k] = report.TryGetValue(k, out var c) ? c + 1 : 1;
};

// Pass 1: model space
var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
var msr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
var erase = new List<ObjectId>(); var kept = new List<ObjectId>(); int total = 0;
foreach (ObjectId id in msr)
{
    var e = tr.GetObject(id, OpenMode.ForRead) as Entity; if (e == null) continue;
    total++;
    var r = annoReason(e, id, false);
    if (r == null && e is BlockReference br)
    {
        var sc = score(br.BlockTableRecord, annoLayer(e.Layer), 0);
        if (sc.Item1 == 0 && sc.Item2 > 0) r = "annotation-only block";
        else if (sc.Item1 == 0 && sc.Item2 == 0) r = "empty block";
        else if (!sc.Item3 && sc.Item2 >= sc.Item1 && annoBlockName.IsMatch(blockName(br))) r = "annotation block (by name)";
    }
    if (r != null) { count(r, e, id); erase.Add(id); } else kept.Add(id);
}

// Pass 2: non-physical content nested inside the blocks that stay (dynamic block definitions included)
var reach = new HashSet<ObjectId>();
Action<ObjectId> walk = null;
walk = btrId =>
{
    if (!reach.Add(btrId)) return;
    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
    if (btr.IsFromExternalReference) return;
    foreach (ObjectId id in btr)
        if (tr.GetObject(id, OpenMode.ForRead) is BlockReference br && !(br is Table))
        { walk(br.BlockTableRecord); if (br.IsDynamicBlock) walk(br.DynamicBlockTableRecord); }
};
var attrs = new List<ObjectId>();
foreach (var id in kept)
    if (tr.GetObject(id, OpenMode.ForRead) is BlockReference br && !(br is Table))
    {
        walk(br.BlockTableRecord); if (br.IsDynamicBlock) walk(br.DynamicBlockTableRecord);
        foreach (ObjectId a in br.AttributeCollection) attrs.Add(a);
    }
var inner = new List<ObjectId>();
foreach (var btrId in reach)
{
    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
    if (btr.IsFromExternalReference || btr.IsLayout) continue;
    foreach (ObjectId id in btr)
    {
        var e = tr.GetObject(id, OpenMode.ForRead) as Entity; if (e == null) continue;
        var r = annoReason(e, id, false);
        if (e is BlockReference nb && !(nb is Table))
        {
            foreach (ObjectId a in nb.AttributeCollection) attrs.Add(a);
            if (r == null) { var sc = score(nb.BlockTableRecord, annoLayer(e.Layer), 0); if (sc.Item1 == 0 && sc.Item2 > 0) r = "annotation-only block"; }
        }
        if (r != null) { count("in block: " + r, e, id); inner.Add(id); }
    }
}
foreach (var a in attrs) count("attribute text", (Entity)tr.GetObject(a, OpenMode.ForRead), a);

Print($"{System.IO.Path.GetFileName(db.Filename)}: model space {total}, removing {erase.Count}, keeping {kept.Count}; nested removals {inner.Count}; attributes {attrs.Count}");
foreach (var kv in report) Print($"  {kv.Value,5}  {kv.Key}");
if (DRY_RUN) return "DRY RUN - nothing erased";
foreach (var id in erase.Concat(inner).Concat(attrs))
{
    var o = tr.GetObject(id, OpenMode.ForWrite, false, true);   // forceOpenOnLockedLayer
    if (!o.IsErased) o.Erase();
}
return $"erased {erase.Count + inner.Count + attrs.Count}";
