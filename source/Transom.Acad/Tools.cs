using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using static Transom.Acad.Json;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Transom.Acad;

/// <summary>
///     The fixed tool set. Application-context tools take no Document; document tools receive the active
///     drawing and run in command context (see <see cref="Dispatcher"/>). Every method runs on AutoCAD's main
///     thread and returns a dictionary the bridge serializes.
/// </summary>
internal static class Tools
{
    // ================================================================= application context

    public static Dictionary<string, object?> Status()
    {
        var dm = AcApp.DocumentManager;
        var active = dm.MdiActiveDocument;
        var docs = new List<object>();
        foreach (Document d in dm)
        {
            docs.Add(new
            {
                name = Path.GetFileName(d.Name),
                path = d.IsNamedDrawing ? d.Name : null,
                active = d == active,
                readOnly = d.IsReadOnly,
                saved = IsSaved(d),
            });
        }

        var result = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["bridge"] = new { version = Plugin.Version, port = Plugin.Port },
            ["product"] = AcApp.GetSystemVariable("PRODUCT")?.ToString(),
            ["acadver"] = AcApp.GetSystemVariable("ACADVER")?.ToString(),
            ["activeDocument"] = active == null ? null : Path.GetFileName(active.Name),
            ["documents"] = docs,
        };
        if (active != null)
        {
            var cmd = active.CommandInProgress;
            result["commandInProgress"] = string.IsNullOrEmpty(cmd) ? null : cmd;
            int ins = Convert.ToInt32(AcApp.GetSystemVariable("INSUNITS"), CultureInfo.InvariantCulture);
            result["insunits"] = new { code = ins, name = UnitName(ins) };
            result["currentLayout"] = AcApp.GetSystemVariable("CTAB")?.ToString();
            result["currentLayer"] = AcApp.GetSystemVariable("CLAYER")?.ToString();
        }
        return result;
    }

    public static Dictionary<string, object?> CancelCommand()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return Fail(Dispatcher.NoDrawing);
        var was = doc.CommandInProgress;
        doc.SendStringToExecute("\x03\x03", true, false, false);
        return new()
        {
            ["ok"] = true,
            ["wasRunning"] = string.IsNullOrEmpty(was) ? null : was,
            ["note"] = "Esc was queued to the command line. Call status to confirm the command ended.",
        };
    }

    public static Dictionary<string, object?> OpenDrawing(JsonElement args)
    {
        var raw = Str(args, "path");
        if (string.IsNullOrWhiteSpace(raw)) return Fail("missing 'path'");
        var path = Path.GetFullPath(raw);
        if (!File.Exists(path)) return Fail($"file not found: {path}");

        var dm = AcApp.DocumentManager;
        foreach (Document d in dm)
        {
            if (d.IsNamedDrawing && string.Equals(Path.GetFullPath(d.Name), path, StringComparison.OrdinalIgnoreCase))
            {
                dm.MdiActiveDocument = d;
                return new() { ["ok"] = true, ["document"] = Path.GetFileName(d.Name), ["alreadyOpen"] = true };
            }
        }

        bool readOnly = Bool(args, "readOnly");
        var doc = dm.Open(path, readOnly, null);
        dm.MdiActiveDocument = doc;
        return new() { ["ok"] = true, ["document"] = Path.GetFileName(doc.Name), ["readOnly"] = doc.IsReadOnly };
    }

    public static Dictionary<string, object?> NewDrawing(JsonElement args)
    {
        var template = Str(args, "template") is { Length: > 0 } t ? t : "acad.dwt";
        var dm = AcApp.DocumentManager;
        var doc = dm.Add(template);
        dm.MdiActiveDocument = doc;
        return new() { ["ok"] = true, ["document"] = Path.GetFileName(doc.Name), ["template"] = template };
    }

    public static Dictionary<string, object?> CloseDrawing(JsonElement args)
    {
        var dm = AcApp.DocumentManager;
        var name = Str(args, "name");
        var doc = string.IsNullOrEmpty(name) ? dm.MdiActiveDocument : FindDocument(name);
        if (doc == null) return Fail(string.IsNullOrEmpty(name) ? Dispatcher.NoDrawing : $"no open drawing matches '{name}'");

        var label = Path.GetFileName(doc.Name);
        if (Bool(args, "save"))
        {
            var saveAs = Str(args, "saveAs");
            var target = !string.IsNullOrEmpty(saveAs) ? Path.GetFullPath(saveAs) : doc.IsNamedDrawing ? doc.Name : null;
            if (target == null) return Fail($"{label} has never been saved; pass saveAs with a full .dwg path.");
            if (doc.IsReadOnly && string.Equals(target, doc.Name, StringComparison.OrdinalIgnoreCase))
                return Fail($"{label} is open read-only; pass saveAs with a different path.");
            doc.CloseAndSave(target);
            return new() { ["ok"] = true, ["closed"] = label, ["savedTo"] = target };
        }

        var saved = IsSaved(doc);
        if (saved != true && !Bool(args, "discardChanges"))
            return Fail($"{label} has unsaved changes (or its saved state is unknown). "
                        + "Pass save:true to save it, or discardChanges:true to drop the changes.");
        doc.CloseAndDiscard();
        return new() { ["ok"] = true, ["closed"] = label, ["discardedChanges"] = saved != true };
    }

    // ================================================================= document (command) context

    public static Dictionary<string, object?> ListLayers(Document doc)
    {
        var db = doc.Database;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var layers = new List<(string Name, object Row)>();
        foreach (ObjectId id in table)
        {
            var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (l.IsErased) continue;
            string? linetype = null;
            try { linetype = ((LinetypeTableRecord)tr.GetObject(l.LinetypeObjectId, OpenMode.ForRead)).Name; }
            catch { /* unresolved xref linetype */ }
            layers.Add((l.Name, new
            {
                name = l.Name,
                on = !l.IsOff,
                frozen = l.IsFrozen,
                locked = l.IsLocked,
                color = l.Color.ColorNameForDisplay,
                linetype,
                plottable = l.IsPlottable,
                current = id == db.Clayer,
                xref = l.IsDependent,
            }));
        }
        tr.Commit();
        return new()
        {
            ["ok"] = true,
            ["count"] = layers.Count,
            ["layers"] = layers.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => x.Row).ToList(),
        };
    }

    public static Dictionary<string, object?> GetEntities(Document doc, JsonElement args)
    {
        var db = doc.Database;
        int limit = Math.Clamp(Int(args, "limit", 200), 1, 2000);
        int offset = Math.Max(0, Int(args, "offset", 0));
        var types = StrArray(args, "types")?.Select(t => t.Trim().ToUpperInvariant()).ToHashSet();
        var layerRx = Str(args, "layer") is { Length: > 0 } pattern ? WildcardRegex(pattern) : null;
        var handles = StrArray(args, "handles");

        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var missing = new List<string>();
        List<ObjectId> ids;
        string scope;
        if (handles != null)
        {
            ids = ResolveHandles(db, handles, missing);
            scope = "handles";
        }
        else
        {
            var spaceId = ResolveSpace(db, tr, Str(args, "space") ?? "model", out scope, out var error);
            if (error != null) return Fail(error);
            ids = ((BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead)).Cast<ObjectId>().ToList();
        }

        var counts = new SortedDictionary<string, int>();
        var entities = new List<object>();
        int matched = 0;
        foreach (var id in ids)
        {
            var type = DxfName(id);
            if (types != null && !types.Contains(type)) continue; // cheap: no open needed
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity e) continue;
            if (layerRx != null && !layerRx.IsMatch(e.Layer)) continue;

            counts[type] = counts.GetValueOrDefault(type) + 1;
            if (matched >= offset && entities.Count < limit) entities.Add(DescribeEntity(tr, e));
            matched++;
        }
        tr.Commit();

        var result = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["scope"] = scope,
            ["matched"] = matched,
            ["returned"] = entities.Count,
            ["offset"] = offset,
            ["truncated"] = matched > offset + entities.Count,
            ["countsByType"] = counts,
            ["entities"] = entities,
        };
        if (missing.Count > 0) result["missingHandles"] = missing;
        return result;
    }

    public static Dictionary<string, object?> GetViewImage(Document doc, JsonElement args)
    {
        int width = Math.Clamp(Int(args, "width", 1280), 64, 2560);
        int height = Math.Clamp(Int(args, "height", 800), 64, 2560);
        using var bitmap = doc.CapturePreviewImage((uint)width, (uint)height);
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return new()
        {
            ["ok"] = true,
            ["width"] = bitmap.Width,
            ["height"] = bitmap.Height,
            ["image"] = new { mimeType = "image/png", data = Convert.ToBase64String(png.ToArray()) },
        };
    }

    public static async Task<Dictionary<string, object?>> RunCommand(Document doc, JsonElement args)
    {
        var tokens = StrArray(args, "tokens");
        if (tokens == null || tokens.Length == 0) return Fail("missing 'tokens', e.g. [\"_.ZOOM\", \"_E\"]");
        Log.Write("run_command: " + Log.Truncate(string.Join(" | ", tokens), 2000));

        var before = doc.CommandInProgress;
        await doc.Editor.CommandAsync(tokens.Cast<object>().ToArray());
        var after = doc.CommandInProgress;

        var result = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["lastPrompt"] = AcApp.GetSystemVariable("LASTPROMPT")?.ToString(),
        };
        if (!string.IsNullOrEmpty(after) && after != before)
        {
            result["stillActive"] = after;
            result["note"] = "The command is still waiting for input and will block other tools. Call cancel_command.";
        }
        return result;
    }

    // ================================================================= helpers

    internal static Dictionary<string, object?> DescribeEntity(Transaction tr, Entity e)
    {
        var d = new Dictionary<string, object?>
        {
            ["handle"] = e.Handle.ToString(),
            ["type"] = DxfName(e.ObjectId),
            ["layer"] = e.Layer,
            ["color"] = e.Color.ColorNameForDisplay,
        };
        try
        {
            var ext = e.GeometricExtents;
            d["extents"] = new[] { P(ext.MinPoint), P(ext.MaxPoint) };
        }
        catch { /* empty or infinite geometry has no extents */ }

        try
        {
            switch (e)
            {
                case Line l:
                    d["start"] = P(l.StartPoint);
                    d["end"] = P(l.EndPoint);
                    d["length"] = R(l.Length);
                    break;
                case Circle c:
                    d["center"] = P(c.Center);
                    d["radius"] = R(c.Radius);
                    break;
                case Arc a:
                    d["center"] = P(a.Center);
                    d["radius"] = R(a.Radius);
                    d["startAngle"] = Deg(a.StartAngle);
                    d["endAngle"] = Deg(a.EndAngle);
                    d["length"] = R(a.Length);
                    break;
                case Polyline pl:
                    d["closed"] = pl.Closed;
                    d["length"] = R(pl.Length);
                    if (pl.Closed) d["area"] = R(pl.Area);
                    d["elevation"] = R(pl.Elevation);
                    var verts = new List<double[]>();
                    for (int i = 0; i < pl.NumberOfVertices && i < 500; i++)
                    {
                        var p = pl.GetPoint2dAt(i);
                        double bulge = pl.GetBulgeAt(i);
                        verts.Add(bulge == 0 ? new[] { R(p.X), R(p.Y) } : new[] { R(p.X), R(p.Y), R(bulge) });
                    }
                    d["vertices"] = verts;
                    if (pl.NumberOfVertices > 500) d["verticesTruncated"] = pl.NumberOfVertices;
                    break;
                case DBText t:
                    d["text"] = t.TextString;
                    d["position"] = P(t.Position);
                    d["height"] = R(t.Height);
                    d["rotation"] = Deg(t.Rotation);
                    break;
                case MText m:
                    d["text"] = m.Text;
                    d["location"] = P(m.Location);
                    d["height"] = R(m.TextHeight);
                    d["width"] = R(m.Width);
                    break;
                case BlockReference br:
                    d["block"] = EffectiveName(tr, br);
                    d["position"] = P(br.Position);
                    d["rotation"] = Deg(br.Rotation);
                    d["scale"] = new[] { R(br.ScaleFactors.X), R(br.ScaleFactors.Y), R(br.ScaleFactors.Z) };
                    var attrs = new Dictionary<string, string>();
                    foreach (ObjectId aid in br.AttributeCollection)
                        if (tr.GetObject(aid, OpenMode.ForRead) is AttributeReference ar) attrs[ar.Tag] = ar.TextString;
                    if (attrs.Count > 0) d["attributes"] = attrs;
                    if (br.IsDynamicBlock)
                    {
                        var props = new Dictionary<string, object?>();
                        foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                            if (p.PropertyName != "Origin" && p.VisibleInCurrentVisibilityState)
                                props[p.PropertyName] = p.Value is double v ? R(v) : p.Value?.ToString();
                        if (props.Count > 0) d["dynamicProperties"] = props;
                    }
                    break;
                case Dimension dim:
                    d["measurement"] = R(dim.Measurement);
                    if (!string.IsNullOrEmpty(dim.DimensionText)) d["textOverride"] = dim.DimensionText;
                    d["style"] = dim.DimensionStyleName;
                    break;
                case Hatch h:
                    d["pattern"] = h.PatternName;
                    d["area"] = R(h.Area);
                    break;
                case Curve curve:
                    d["closed"] = curve.Closed;
                    d["start"] = P(curve.StartPoint);
                    d["end"] = P(curve.EndPoint);
                    d["length"] = R(curve.GetDistanceAtParameter(curve.EndParam));
                    break;
            }
        }
        catch (System.Exception ex)
        {
            d["describeError"] = ex.Message;
        }
        return d;
    }

    private static string EffectiveName(Transaction tr, BlockReference br)
    {
        if (!br.IsDynamicBlock) return br.Name;
        return ((BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)).Name;
    }

    internal static string DxfName(ObjectId id)
    {
        var cls = id.ObjectClass;
        return string.IsNullOrEmpty(cls.DxfName) ? cls.Name : cls.DxfName;
    }

    private static ObjectId ResolveSpace(Database db, Transaction tr, string space, out string label, out string? error)
    {
        error = null;
        label = space;
        switch (space.Trim().ToLowerInvariant())
        {
            case "model":
                label = "model";
                return SymbolUtilityServices.GetBlockModelSpaceId(db);
            case "current":
                label = "current";
                return db.CurrentSpaceId;
        }

        // DBDictionary's typed enumerator yields DBDictionaryEntry, but its non-generic one (what LINQ's Cast
        // uses) yields DictionaryEntry, so collect the names in this foreach rather than with Cast<>.
        var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        var names = new List<string>();
        foreach (DBDictionaryEntry entry in layouts)
        {
            names.Add(entry.Key);
            if (!string.Equals(entry.Key, space.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            label = "layout:" + entry.Key;
            return ((Layout)tr.GetObject(entry.Value, OpenMode.ForRead)).BlockTableRecordId;
        }

        error = $"unknown space '{space}'. Use 'model', 'current', or a layout name: {string.Join(", ", names)}";
        return ObjectId.Null;
    }

    private static List<ObjectId> ResolveHandles(Database db, IEnumerable<string> handles, List<string> missing)
    {
        var ids = new List<ObjectId>();
        foreach (var h in handles)
        {
            if (long.TryParse(h.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
                && db.TryGetObjectId(new Handle(value), out var id) && !id.IsErased)
                ids.Add(id);
            else
                missing.Add(h);
        }
        return ids;
    }

    private static Regex WildcardRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Document? FindDocument(string name)
    {
        foreach (Document d in AcApp.DocumentManager)
        {
            if (string.Equals(Path.GetFileName(d.Name), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                return d;
        }
        return null;
    }

    /// <summary>The COM document's Saved flag: true when there is nothing unsaved. Null when it can't be read.</summary>
    private static bool? IsSaved(Document doc)
    {
        try
        {
            object com = doc.GetAcadDocument();
            return com.GetType().InvokeMember("Saved", BindingFlags.GetProperty, null, com, null) as bool?;
        }
        catch
        {
            return null;
        }
    }

    private static readonly string[] Units =
    {
        "Unitless", "Inches", "Feet", "Miles", "Millimeters", "Centimeters", "Meters", "Kilometers", "Microinches",
        "Mils", "Yards", "Angstroms", "Nanometers", "Microns", "Decimeters", "Dekameters", "Hectometers",
        "Gigameters", "Astronomical units", "Light years", "Parsecs", "US Survey Feet",
    };

    private static string UnitName(int code) => code >= 0 && code < Units.Length ? Units[code] : $"code {code}";
}
