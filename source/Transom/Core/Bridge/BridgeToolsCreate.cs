using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>
///     extended creation / MEP / interop tools, ported from the IronPython handlers in
///     revit_mcp (building.py, structure.py, rooms.py, mep.py, documentation.py, interop.py, document.py).
///     Response field names and defaults mirror the Python; all wire lengths are MILLIMETERS.
/// </summary>
public static partial class BridgeTools
{
    /// <summary>Array-of-objects arg reader (missing/wrong-kind → empty list).</summary>
    private static List<JsonElement> ArrayArg(JsonElement args, string prop)
    {
        var list = new List<JsonElement>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(prop, out var a)
            && a.ValueKind == JsonValueKind.Array)
            list.AddRange(a.EnumerateArray());
        return list;
    }

    /// <summary>Child object property, or a default (Undefined) JsonElement.</summary>
    private static JsonElement PropOr(JsonElement obj, string prop)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) ? v : default;

    /// <summary>Connector manager of a duct/pipe (MEPCurve) or family instance, or null.</summary>
    private static ConnectorManager? ConnectorManagerOf(Element e) => e switch
    {
        MEPCurve mc => mc.ConnectorManager,
        FamilyInstance fi => fi.MEPModel?.ConnectorManager,
        _ => null,
    };

    // --- create_line_based_element ------------------------------------------

    /// <summary>Batch-creates line-based elements: walls (Wall.Create) and beams (structural framing instances).</summary>
    internal static string CreateLineBasedElement(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var elements = ArrayArg(args, "elements");
            if (elements.Count == 0) return Err("No elements provided — pass an array of element definitions");

            var levelMap = new Dictionary<string, Level>(StringComparer.OrdinalIgnoreCase);
            foreach (var lv in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                levelMap[ElemName(lv)] = lv;
            if (levelMap.Count == 0) return Err("No levels found in the project — create levels first");

            var wallTypeMap = new Dictionary<string, WallType>(StringComparer.OrdinalIgnoreCase);
            foreach (var wt in new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>())
                wallTypeMap[ElemName(wt)] = wt;

            var beamTypeMap = new Dictionary<string, FamilySymbol>(StringComparer.OrdinalIgnoreCase);
            foreach (var bt in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_StructuralFraming)
                         .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
                beamTypeMap[ElemName(bt)] = bt;

            return InTransaction(doc, "Create Line-Based Elements", () =>
            {
                var created = new List<object?>();
                var errors = new List<string>();

                for (int idx = 0; idx < elements.Count; idx++)
                {
                    var elem = elements[idx];
                    try
                    {
                        var elementType = StrArg(elem, "element_type");
                        if (string.IsNullOrEmpty(elementType))
                        { errors.Add($"Element {idx}: element_type is required"); continue; }

                        var sp = PointArg(elem, "start_point");
                        var ep = PointArg(elem, "end_point");
                        if (sp == null || ep == null)
                        { errors.Add($"Element {idx}: start_point and end_point are required"); continue; }

                        if (sp.DistanceTo(ep) < 0.001)
                        { errors.Add($"Element {idx}: Start and end points must be different (zero-length element)"); continue; }

                        var line = Line.CreateBound(sp, ep);

                        var levelName = StrArg(elem, "level_name");
                        Level? level;
                        if (!string.IsNullOrEmpty(levelName))
                        {
                            if (!levelMap.TryGetValue(levelName!, out level))
                            {
                                var available = string.Join(", ", levelMap.Keys.OrderBy(k => k, StringComparer.Ordinal));
                                errors.Add($"Element {idx}: Level '{levelName}' not found. Available levels: {available}");
                                continue;
                            }
                        }
                        else level = levelMap.Values.OrderBy(l => l.Elevation).First();

                        if (elementType == "wall")
                        {
                            var typeName = StrArg(elem, "type_name");
                            WallType? wallType;
                            if (!string.IsNullOrEmpty(typeName))
                            {
                                if (!wallTypeMap.TryGetValue(typeName!, out wallType))
                                {
                                    var available = string.Join(", ", wallTypeMap.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(10));
                                    errors.Add($"Element {idx}: Wall type '{typeName}' not found. Available types: {available}");
                                    continue;
                                }
                            }
                            else if (wallTypeMap.Count > 0) wallType = wallTypeMap.Values.First();
                            else
                            { errors.Add($"Element {idx}: No wall types available — load wall families into the project"); continue; }

                            var heightFt = MmToFt(DblOf(elem, "height", 3000));
                            var offsetFt = MmToFt(DblOf(elem, "offset"));
                            var structural = BoolArg(elem, "structural");

                            var wall = Wall.Create(doc, line, wallType!.Id, level!.Id, heightFt, offsetFt, false, structural);

                            created.Add(new Dictionary<string, object?>
                            {
                                ["id"] = wall.Id.Value,
                                ["name"] = StrArg(elem, "name") ?? "",
                                ["type"] = ElemName(wallType),
                                ["level"] = ElemName(level),
                                ["element_type"] = "wall",
                            });
                        }
                        else if (elementType == "beam")
                        {
                            var typeName = StrArg(elem, "type_name");
                            FamilySymbol? beamType;
                            if (!string.IsNullOrEmpty(typeName))
                            {
                                if (!beamTypeMap.TryGetValue(typeName!, out beamType))
                                {
                                    var available = string.Join(", ", beamTypeMap.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(10));
                                    errors.Add($"Element {idx}: Beam type '{typeName}' not found. Available types: {available}");
                                    continue;
                                }
                            }
                            else if (beamTypeMap.Count > 0) beamType = beamTypeMap.Values.First();
                            else
                            { errors.Add($"Element {idx}: No beam types available — load structural framing families"); continue; }

                            if (!beamType!.IsActive) { beamType.Activate(); doc.Regenerate(); }

                            var beam = doc.Create.NewFamilyInstance(line, beamType, level, StructuralType.Beam);

                            created.Add(new Dictionary<string, object?>
                            {
                                ["id"] = beam.Id.Value,
                                ["name"] = StrArg(elem, "name") ?? "",
                                ["type"] = ElemName(beamType),
                                ["level"] = ElemName(level!),
                                ["element_type"] = "beam",
                            });
                        }
                        else
                        {
                            errors.Add($"Element {idx}: element_type '{elementType}' not supported — use 'wall' or 'beam'");
                        }
                    }
                    catch (Exception elemEx) { errors.Add($"Element {idx}: {elemEx.Message}"); }
                }

                var payload = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["status"] = "success",
                    ["created"] = created,
                    ["count"] = created.Count,
                    ["message"] = $"Created {created.Count} element(s)",
                };
                if (errors.Count > 0) payload["errors"] = errors;
                return Json(payload);
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // --- create_surface_based_element ---------------------------------------

    /// <summary>
    ///     Batch-creates surface-based elements: floors/ceilings via Floor.Create, roofs via NewFootPrintRoof.
    ///     Boundary accepts a point array [{x,y,z},...] or segments [{p0:{},p1:{}},...] (auto-detected).
    /// </summary>
    internal static string CreateSurfaceBasedElement(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var elements = ArrayArg(args, "elements");
            if (elements.Count == 0) return Err("No elements provided — pass an array of element definitions");

            var levelMap = new Dictionary<string, Level>(StringComparer.OrdinalIgnoreCase);
            foreach (var lv in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                levelMap[ElemName(lv)] = lv;
            if (levelMap.Count == 0) return Err("No levels found — create levels first");

            var floorTypeMap = new Dictionary<string, FloorType>(StringComparer.OrdinalIgnoreCase);
            foreach (var ft in new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>())
                floorTypeMap[ElemName(ft)] = ft;

            var roofTypeMap = new Dictionary<string, RoofType>(StringComparer.OrdinalIgnoreCase);
            foreach (var rt in new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>())
                roofTypeMap[ElemName(rt)] = rt;

            (double X, double Y, double Z) MmXyz(JsonElement p) => (DblOf(p, "x"), DblOf(p, "y"), DblOf(p, "z"));

            return InTransaction(doc, "Create Surface-Based Elements", () =>
            {
                var created = new List<object?>();
                var errors = new List<string>();

                for (int idx = 0; idx < elements.Count; idx++)
                {
                    var elem = elements[idx];
                    try
                    {
                        var elementType = StrArg(elem, "element_type");
                        if (string.IsNullOrEmpty(elementType))
                        { errors.Add($"Element {idx}: element_type is required"); continue; }
                        if (elementType != "floor" && elementType != "roof" && elementType != "ceiling")
                        { errors.Add($"Element {idx}: element_type must be 'floor', 'roof', or 'ceiling'"); continue; }

                        var boundary = ArrayArg(elem, "boundary");
                        if (boundary.Count < 3)
                        { errors.Add($"Element {idx}: Boundary requires at least 3 points/segments, got {boundary.Count}"); continue; }

                        // Auto-detect point-array vs segment format (mm coordinates throughout).
                        var segments = new List<((double X, double Y, double Z) A, (double X, double Y, double Z) B)>();
                        bool isPoints = boundary[0].ValueKind == JsonValueKind.Object
                                        && (boundary[0].TryGetProperty("x", out _) || boundary[0].TryGetProperty("y", out _));
                        if (isPoints)
                        {
                            var points = boundary.Select(MmXyz).ToList();
                            // Drop an explicit closing point so the wrap-around doesn't make a zero-length segment.
                            if (points.Count > 1)
                            {
                                var f = points[0];
                                var l = points[^1];
                                if (Math.Abs(f.X - l.X) < 0.001 && Math.Abs(f.Y - l.Y) < 0.001 && Math.Abs(f.Z - l.Z) < 0.001)
                                    points.RemoveAt(points.Count - 1);
                            }
                            for (int pi = 0; pi < points.Count; pi++)
                                segments.Add((points[pi], points[(pi + 1) % points.Count]));
                        }
                        else
                        {
                            foreach (var seg in boundary)
                                segments.Add((MmXyz(PropOr(seg, "p0")), MmXyz(PropOr(seg, "p1"))));
                        }

                        // Validate closed polygon (1mm tolerance).
                        var fp = segments[0].A;
                        var lp = segments[^1].B;
                        var dist = Math.Sqrt(Math.Pow(fp.X - lp.X, 2) + Math.Pow(fp.Y - lp.Y, 2) + Math.Pow(fp.Z - lp.Z, 2));
                        if (dist > 1.0)
                        {
                            errors.Add($"Element {idx}: Boundary must form a closed polygon — last point ({lp.X}, {lp.Y}, {lp.Z}) does not match first point ({fp.X}, {fp.Y}, {fp.Z})");
                            continue;
                        }

                        var levelName = StrArg(elem, "level_name");
                        Level? level;
                        if (!string.IsNullOrEmpty(levelName))
                        {
                            if (!levelMap.TryGetValue(levelName!, out level))
                            {
                                var available = string.Join(", ", levelMap.Keys.OrderBy(k => k, StringComparer.Ordinal));
                                errors.Add($"Element {idx}: Level '{levelName}' not found. Available levels: {available}");
                                continue;
                            }
                        }
                        else level = levelMap.Values.OrderBy(l => l.Elevation).First();

                        XYZ Ft((double X, double Y, double Z) p) => new(MmToFt(p.X), MmToFt(p.Y), MmToFt(p.Z));

                        if (elementType == "floor" || elementType == "ceiling")
                        {
                            var typeName = StrArg(elem, "type_name");
                            FloorType? floorType = null;
                            if (!string.IsNullOrEmpty(typeName))
                            {
                                if (!floorTypeMap.TryGetValue(typeName!, out floorType))
                                {
                                    var available = string.Join(", ", floorTypeMap.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(10));
                                    errors.Add($"Element {idx}: Floor type '{typeName}' not found. Available: {available}");
                                    continue;
                                }
                            }
                            else if (floorTypeMap.Count > 0)
                            {
                                // Prefer a real (non-foundation) floor type — the raw first type often lands on
                                // "Foundation Slab", which Revit categorizes as a Structural Foundation.
                                foreach (var ft in floorTypeMap.Values)
                                {
                                    try { if (!ft.IsFoundationSlab) { floorType = ft; break; } }
                                    catch { /* keep looking */ }
                                }
                                floorType ??= floorTypeMap.Values.First();
                            }
                            else
                            { errors.Add($"Element {idx}: No floor types available — load floor families"); continue; }

                            var loop = new CurveLoop();
                            foreach (var seg in segments)
                                loop.Append(Line.CreateBound(Ft(seg.A), Ft(seg.B)));

                            var floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorType!.Id, level!.Id);

                            var offsetMm = DblOf(elem, "offset");
                            if (offsetMm != 0)
                            {
                                var offsetParam = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                                if (offsetParam != null && !offsetParam.IsReadOnly) offsetParam.Set(MmToFt(offsetMm));
                            }

                            created.Add(new Dictionary<string, object?>
                            {
                                ["id"] = floor.Id.Value,
                                ["name"] = StrArg(elem, "name") ?? "",
                                ["type"] = ElemName(floorType),
                                ["level"] = ElemName(level),
                                ["element_type"] = elementType,
                            });
                        }
                        else // roof
                        {
                            var typeName = StrArg(elem, "type_name");
                            RoofType? roofType;
                            if (!string.IsNullOrEmpty(typeName))
                            {
                                if (!roofTypeMap.TryGetValue(typeName!, out roofType))
                                {
                                    var available = string.Join(", ", roofTypeMap.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(10));
                                    errors.Add($"Element {idx}: Roof type '{typeName}' not found. Available: {available}");
                                    continue;
                                }
                            }
                            else if (roofTypeMap.Count > 0) roofType = roofTypeMap.Values.First();
                            else
                            { errors.Add($"Element {idx}: No roof types available — load roof families"); continue; }

                            var curveArray = new CurveArray();
                            foreach (var seg in segments)
                                curveArray.Append(Line.CreateBound(Ft(seg.A), Ft(seg.B)));

                            var roof = doc.Create.NewFootPrintRoof(curveArray, level!, roofType!, out ModelCurveArray _);

                            created.Add(new Dictionary<string, object?>
                            {
                                ["id"] = roof.Id.Value,
                                ["name"] = StrArg(elem, "name") ?? "",
                                ["type"] = ElemName(roofType),
                                ["level"] = ElemName(level),
                                ["element_type"] = "roof",
                            });
                        }
                    }
                    catch (Exception elemEx) { errors.Add($"Element {idx}: {elemEx.Message}"); }
                }

                var payload = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["status"] = "success",
                    ["created"] = created,
                    ["count"] = created.Count,
                    ["message"] = $"Created {created.Count} element(s)",
                };
                if (errors.Count > 0) payload["errors"] = errors;
                return Json(payload);
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // --- create_level --------------------------------------------------------

    /// <summary>Batch-creates levels at mm elevations (Level.Create), optionally named.</summary>
    internal static string CreateLevel(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var levels = ArrayArg(args, "levels");
            if (levels.Count == 0) return Err("No levels provided — pass an array of level definitions");

            return InTransaction(doc, "Create Levels", () =>
            {
                var created = new List<object?>();
                var errors = new List<string>();

                for (int idx = 0; idx < levels.Count; idx++)
                {
                    var lv = levels[idx];
                    try
                    {
                        var elevationMm = DblArg(lv, "elevation");
                        if (elevationMm == null)
                        { errors.Add($"Level {idx}: elevation is required"); continue; }

                        var newLevel = Level.Create(doc, MmToFt(elevationMm.Value));

                        var name = StrArg(lv, "name");
                        if (!string.IsNullOrEmpty(name)) newLevel.Name = name!;

                        created.Add(new Dictionary<string, object?>
                        {
                            ["id"] = newLevel.Id.Value,
                            ["name"] = ElemName(newLevel),
                            ["elevation_mm"] = elevationMm.Value,
                        });
                    }
                    catch (Exception lvEx) { errors.Add($"Level {idx}: {lvEx.Message}"); }
                }

                var payload = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["status"] = "success",
                    ["created"] = created,
                    ["count"] = created.Count,
                    ["message"] = $"Created {created.Count} level(s)",
                };
                if (errors.Count > 0) payload["errors"] = errors;
                return Json(payload);
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // --- create_grid ---------------------------------------------------------

    /// <summary>Batch-creates grid lines (Grid.Create); zero-length definitions are skipped.</summary>
    internal static string CreateGrid(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var grids = ArrayArg(args, "grids");
            if (grids.Count == 0) return Err("No grids provided");

            return InTransaction(doc, "Create Grids", () =>
            {
                var created = new List<object?>();

                foreach (var gridDef in grids)
                {
                    var start = PointArg(gridDef, "start_point") ?? XYZ.Zero;
                    var end = PointArg(gridDef, "end_point") ?? XYZ.Zero;
                    if (start.DistanceTo(end) < 0.001) continue; // zero length — skip, like the Python

                    var grid = Grid.Create(doc, Line.CreateBound(start, end));

                    var name = StrArg(gridDef, "name");
                    if (!string.IsNullOrEmpty(name))
                    {
                        try { grid.Name = name!; } catch { /* duplicate/invalid name — keep auto name */ }
                    }

                    created.Add(new Dictionary<string, object?>
                    {
                        ["id"] = grid.Id.Value,
                        ["name"] = ElemName(grid),
                    });
                }

                return Json(new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["status"] = "success",
                    ["created"] = created,
                    ["count"] = created.Count,
                    ["message"] = $"Created {created.Count} grid line{(created.Count != 1 ? "s" : "")}",
                });
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // --- create_structural_framing --------------------------------------------

    /// <summary>
    ///     Batch-creates structural framing (beams). Point Z is treated as an offset from the target level:
    ///     the level's elevation is added so a beam lands on its level like walls/floors do.
    /// </summary>
    internal static string CreateStructuralFraming(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var elements = ArrayArg(args, "elements");
            if (elements.Count == 0) return Err("No elements provided");

            var framingSymbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
            if (framingSymbols.Count == 0)
                return Err("No structural framing types found — load beam families into the project");

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();

            return InTransaction(doc, "Create Structural Framing", () =>
            {
                var created = new List<Dictionary<string, object?>>();

                foreach (var elemDef in elements)
                {
                    var start = PointArg(elemDef, "start_point") ?? XYZ.Zero;
                    var end = PointArg(elemDef, "end_point") ?? XYZ.Zero;
                    if (start.DistanceTo(end) < 0.001) continue; // zero length — skip, like the Python

                    var typeName = StrArg(elemDef, "type_name");
                    var targetSymbol = !string.IsNullOrEmpty(typeName)
                        ? framingSymbols.FirstOrDefault(s =>
                            string.Equals(ElemName(s), typeName, StringComparison.OrdinalIgnoreCase))
                        : null;
                    targetSymbol ??= framingSymbols[0];

                    var levelName = StrArg(elemDef, "level_name");
                    var targetLevel = !string.IsNullOrEmpty(levelName)
                        ? levels.FirstOrDefault(l =>
                            string.Equals(ElemName(l), levelName, StringComparison.OrdinalIgnoreCase))
                        : null;
                    targetLevel ??= levels.FirstOrDefault();

                    if (!targetSymbol.IsActive) { targetSymbol.Activate(); doc.Regenerate(); }

                    var levelElev = targetLevel?.Elevation ?? 0.0;
                    if (Math.Abs(levelElev) > 1e-9)
                    {
                        start = new XYZ(start.X, start.Y, start.Z + levelElev);
                        end = new XYZ(end.X, end.Y, end.Z + levelElev);
                    }

                    var beam = doc.Create.NewFamilyInstance(
                        Line.CreateBound(start, end), targetSymbol, targetLevel, StructuralType.Beam);

                    created.Add(new Dictionary<string, object?>
                    {
                        ["id"] = beam.Id.Value,
                        ["name"] = StrArg(elemDef, "name") ?? "",
                        ["type"] = ElemName(targetSymbol),
                        ["level"] = targetLevel != null ? ElemName(targetLevel) : "Unknown",
                    });
                }

                var levelMsg = created.Count > 0 && created[0]["level"] is string lvl && !string.IsNullOrEmpty(lvl)
                    ? $" on {lvl}" : "";

                return Json(new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["status"] = "success",
                    ["created"] = created.Cast<object?>().ToList(),
                    ["count"] = created.Count,
                    ["message"] = $"Created {created.Count} structural framing element{(created.Count != 1 ? "s" : "")}{levelMsg}",
                });
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // --- create_room -----------------------------------------------------------

    /// <summary>Creates a room on a level — placed at {location} (mm UV) or unplaced in the last phase.</summary>
    internal static string CreateRoom(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var levelName = StrArg(args, "level_name");
            if (string.IsNullOrEmpty(levelName)) return Err("level_name is required");

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var targetLevel = levels.FirstOrDefault(l =>
                string.Equals(ElemName(l), levelName, StringComparison.OrdinalIgnoreCase));
            if (targetLevel == null)
            {
                var available = string.Join(", ", levels.Select(ElemName));
                return Err($"Level '{levelName}' not found. Available levels: {available}");
            }

            var phases = doc.Phases;
            if (phases == null || phases.Size == 0) return Err("No phases found in the project");
            var phase = phases.get_Item(phases.Size - 1);

            return InTransaction(doc, "Create Room", () =>
            {
                Autodesk.Revit.DB.Architecture.Room? room;
                var location = PropOr(args, "location");
                if (location.ValueKind == JsonValueKind.Object)
                {
                    var uv = new UV(MmToFt(DblOf(location, "x")), MmToFt(DblOf(location, "y")));
                    room = doc.Create.NewRoom(targetLevel, uv);
                }
                else
                {
                    room = doc.Create.NewRoom(phase);
                }

                if (room == null)
                    return Err("Failed to create room — no enclosed area found at the specified location. Add walls or room separation lines first.");

                var roomName = StrArg(args, "name");
                if (!string.IsNullOrEmpty(roomName))
                {
                    var nameParam = room.get_Parameter(BuiltInParameter.ROOM_NAME);
                    if (nameParam != null && !nameParam.IsReadOnly) nameParam.Set(roomName);
                }

                var roomNumber = StrArg(args, "number");
                if (!string.IsNullOrEmpty(roomNumber))
                {
                    var numberParam = room.get_Parameter(BuiltInParameter.ROOM_NUMBER);
                    if (numberParam != null && !numberParam.IsReadOnly) numberParam.Set(roomNumber);
                }

                // The Python reads these post-commit; regenerate so area/name resolve inside the transaction.
                try { doc.Regenerate(); } catch { /* best effort */ }

                double area = 0.0;
                try
                {
                    var areaParam = room.get_Parameter(BuiltInParameter.ROOM_AREA);
                    if (areaParam != null && areaParam.HasValue)
                        area = Math.Round(areaParam.AsDouble() * 0.092903, 2); // sq ft to sq m
                }
                catch { /* unplaced rooms have no area */ }

                string actualName = "", actualNumber = "";
                try { actualName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? ""; } catch { }
                try { actualNumber = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? ""; } catch { }

                var label = !string.IsNullOrEmpty(actualName) ? actualName
                    : !string.IsNullOrEmpty(actualNumber) ? actualNumber : "Unnamed";

                return Json(new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["status"] = "success",
                    ["room_id"] = room.Id.Value,
                    ["name"] = actualName,
                    ["number"] = actualNumber,
                    ["level"] = levelName,
                    ["area"] = area,
                    ["message"] = $"Room '{label}' created on level '{levelName}'",
                });
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // --- create_room_separation -------------------------------------------------

    /// <summary>Creates room separation lines (NewRoomBoundaryLines) in a named plan view or the active plan view.</summary>
    internal static string CreateRoomSeparation(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var lines = ArrayArg(args, "lines");
            if (lines.Count == 0) return Err("No lines provided");

            var viewName = StrArg(args, "view_name");
            View? targetView;
            if (!string.IsNullOrEmpty(viewName))
            {
                targetView = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .FirstOrDefault(v => !v.IsTemplate
                                         && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase));
                if (targetView == null) return Err($"View '{viewName}' not found");
            }
            else
            {
                var activeView = doc.ActiveView;
                if (activeView != null && activeView.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.AreaPlan)
                    targetView = activeView;
                else
                    return Err("Active view is not a plan view — specify a view_name or switch to a plan view");
            }

            return InTransaction(doc, "Create Room Separation Lines", () =>
            {
                var curveArray = new CurveArray();
                foreach (var lineDef in lines)
                {
                    var start = PointArg(lineDef, "start_point") ?? XYZ.Zero;
                    var end = PointArg(lineDef, "end_point") ?? XYZ.Zero;
                    curveArray.Append(Line.CreateBound(start, end));
                }

                var sketchPlane = targetView.SketchPlane;
                if (sketchPlane == null && targetView.GenLevel != null)
                    sketchPlane = SketchPlane.Create(doc, targetView.GenLevel.Id);

                var separator = doc.Create.NewRoomBoundaryLines(sketchPlane, curveArray, targetView);

                var createdIds = new List<object?>();
                if (separator != null)
                    foreach (ModelCurve mc in separator)
                        createdIds.Add(mc.Id.Value);

                return Json(new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["status"] = "success",
                    ["line_count"] = createdIds.Count,
                    ["line_ids"] = createdIds,
                    ["message"] = $"Created {createdIds.Count} room separation line{(createdIds.Count != 1 ? "s" : "")}",
                });
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // --- create_duct --------------------------------------------------------------

    /// <summary>Creates a duct between two mm points (Duct.Create), sizing via diameter or width/height (mm).</summary>
    internal static string CreateDuct(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var start = PointArg(args, "start_point");
            var end = PointArg(args, "end_point");
            if (start == null || end == null) return Err("start_point and end_point are required");

            var ductTypes = new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>().ToList();
            if (ductTypes.Count == 0)
                return Err("No duct types available — load a mechanical template or family first.");

            var ductTypeName = StrArg(args, "duct_type");
            DuctType? targetDuctType;
            if (!string.IsNullOrEmpty(ductTypeName))
            {
                targetDuctType = ductTypes.FirstOrDefault(dt =>
                    string.Equals(ElemName(dt), ductTypeName, StringComparison.OrdinalIgnoreCase));
                if (targetDuctType == null)
                {
                    var available = string.Join(", ", ductTypes.Select(ElemName));
                    return Err($"Duct type '{ductTypeName}' not found. Available duct types: {available}");
                }
            }
            else targetDuctType = ductTypes[0];

            var systemTypeName = StrArg(args, "system_type");
            var mechSystemTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>().ToList();
            MechanicalSystemType? targetSystemType = null;
            if (mechSystemTypes.Count > 0)
            {
                if (!string.IsNullOrEmpty(systemTypeName))
                    targetSystemType = mechSystemTypes.FirstOrDefault(st =>
                        string.Equals(ElemName(st), systemTypeName, StringComparison.OrdinalIgnoreCase));
                targetSystemType ??= mechSystemTypes[0];
            }

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var levelName = StrArg(args, "level_name");
            var targetLevel = !string.IsNullOrEmpty(levelName)
                ? levels.FirstOrDefault(l => string.Equals(ElemName(l), levelName, StringComparison.OrdinalIgnoreCase))
                : null;
            targetLevel ??= levels.OrderBy(l => Math.Abs(l.Elevation - start.Z)).FirstOrDefault();
            if (targetLevel == null) return Err("No levels found in the project");

            return InTransaction(doc, "Create Duct", () =>
            {
                var sysTypeId = targetSystemType?.Id ?? ElementId.InvalidElementId;
                var duct = Duct.Create(doc, sysTypeId, targetDuctType!.Id, targetLevel.Id, start, end);

                var diameter = DblArg(args, "diameter");
                if (diameter is { } d && d != 0)
                {
                    var p = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                    if (p != null && !p.IsReadOnly) p.Set(MmToFt(d));
                }

                var width = DblArg(args, "width");
                if (width is { } w && w != 0)
                {
                    var p = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                    if (p != null && !p.IsReadOnly) p.Set(MmToFt(w));
                }

                var height = DblArg(args, "height");
                if (height is { } h && h != 0)
                {
                    var p = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                    if (p != null && !p.IsReadOnly) p.Set(MmToFt(h));
                }

                return Json(new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["status"] = "success",
                    ["duct_id"] = duct.Id.Value,
                    ["system_type"] = targetSystemType != null ? ElemName(targetSystemType) : "None",
                    ["duct_type"] = ElemName(targetDuctType),
                    ["level"] = ElemName(targetLevel),
                    ["message"] = $"Created duct on level '{ElemName(targetLevel)}'",
                });
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // --- create_pipe ---------------------------------------------------------------

    /// <summary>Creates a pipe between two mm points (Pipe.Create), with optional diameter (mm).</summary>
    internal static string CreatePipe(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var start = PointArg(args, "start_point");
            var end = PointArg(args, "end_point");
            if (start == null || end == null) return Err("start_point and end_point are required");

            var pipeTypes = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().ToList();
            if (pipeTypes.Count == 0)
                return Err("No pipe types available — load a plumbing template or family first.");

            var pipeTypeName = StrArg(args, "pipe_type");
            PipeType? targetPipeType;
            if (!string.IsNullOrEmpty(pipeTypeName))
            {
                targetPipeType = pipeTypes.FirstOrDefault(pt =>
                    string.Equals(ElemName(pt), pipeTypeName, StringComparison.OrdinalIgnoreCase));
                if (targetPipeType == null)
                {
                    var available = string.Join(", ", pipeTypes.Select(ElemName));
                    return Err($"Pipe type '{pipeTypeName}' not found. Available pipe types: {available}");
                }
            }
            else targetPipeType = pipeTypes[0];

            var systemTypeName = StrArg(args, "system_type");
            var pipingSystemTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().ToList();
            PipingSystemType? targetSystemType = null;
            if (pipingSystemTypes.Count > 0)
            {
                if (!string.IsNullOrEmpty(systemTypeName))
                    targetSystemType = pipingSystemTypes.FirstOrDefault(st =>
                        string.Equals(ElemName(st), systemTypeName, StringComparison.OrdinalIgnoreCase));
                targetSystemType ??= pipingSystemTypes[0];
            }

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var levelName = StrArg(args, "level_name");
            var targetLevel = !string.IsNullOrEmpty(levelName)
                ? levels.FirstOrDefault(l => string.Equals(ElemName(l), levelName, StringComparison.OrdinalIgnoreCase))
                : null;
            targetLevel ??= levels.OrderBy(l => Math.Abs(l.Elevation - start.Z)).FirstOrDefault();
            if (targetLevel == null) return Err("No levels found in the project");

            return InTransaction(doc, "Create Pipe", () =>
            {
                var sysTypeId = targetSystemType?.Id ?? ElementId.InvalidElementId;
                var pipe = Pipe.Create(doc, sysTypeId, targetPipeType!.Id, targetLevel.Id, start, end);

                var diameter = DblArg(args, "diameter");
                if (diameter is { } d && d != 0)
                {
                    var p = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                    if (p != null && !p.IsReadOnly) p.Set(MmToFt(d));
                }

                return Json(new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["status"] = "success",
                    ["pipe_id"] = pipe.Id.Value,
                    ["system_type"] = targetSystemType != null ? ElemName(targetSystemType) : "None",
                    ["pipe_type"] = ElemName(targetPipeType),
                    ["level"] = ElemName(targetLevel),
                    ["message"] = $"Created pipe on level '{ElemName(targetLevel)}'",
                });
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // --- create_mep_system ------------------------------------------------------------

    /// <summary>
    ///     Groups ducts/pipes into an MEP system: renames the elements' existing system if they already
    ///     belong to one, otherwise creates a new system (SupplyAir / DomesticHotWater) from their unused connectors.
    /// </summary>
    internal static string CreateMepSystem(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var systemType = StrArg(args, "system_type");
            var systemName = StrArg(args, "system_name");

            if (string.IsNullOrEmpty(systemType)) return Err("system_type is required (mechanical or piping)");
            if (string.IsNullOrEmpty(systemName)) return Err("system_name is required");
            if (systemType != "mechanical" && systemType != "piping")
                return Err("system_type must be 'mechanical' or 'piping'");

            var elementIds = IdListArg(args, "element_ids");
            if (elementIds.Count == 0)
                return Err("element_ids is required. Provide IDs of ducts/pipes to group into a system. Hint: Create ducts or pipes first, then group them.");

            foreach (var eid in elementIds)
                if (doc.GetElement(eid) == null) return Err($"Element {eid.Value} not found");

            return InTransaction(doc, "Create MEP System", () =>
            {
                Element? newSystem = null;

                // Find existing MEP systems already attached to the provided elements' connectors.
                var existingSystems = new List<long>();
                foreach (var eid in elementIds)
                {
                    var elem = doc.GetElement(eid);
                    if (elem == null) continue;
                    var mgr = ConnectorManagerOf(elem);
                    if (mgr == null) continue;
                    foreach (Connector c in mgr.Connectors)
                    {
                        try
                        {
                            var sys = c.MEPSystem;
                            if (sys != null && !existingSystems.Contains(sys.Id.Value))
                                existingSystems.Add(sys.Id.Value);
                        }
                        catch { /* some connector domains reject MEPSystem */ }
                    }
                }

                if (existingSystems.Count > 0)
                {
                    // Rename the first existing system.
                    newSystem = doc.GetElement(new ElementId(existingSystems[0]));
                    if (newSystem != null)
                    {
                        var nameParam = newSystem.LookupParameter("System Name")
                                        ?? newSystem.LookupParameter("Comments");
                        if (nameParam != null && !nameParam.IsReadOnly) nameParam.Set(systemName);
                    }
                }
                else
                {
                    // Create a new system from the elements' unused connectors.
                    Connector? firstConnector = null;
                    var unusedConnectors = new ConnectorSet();
                    foreach (var eid in elementIds)
                    {
                        var elem = doc.GetElement(eid);
                        if (elem == null) continue;
                        var mgr = ConnectorManagerOf(elem);
                        if (mgr == null) continue;
                        foreach (Connector c in mgr.Connectors)
                        {
                            bool unused;
                            try { unused = c.MEPSystem == null; }
                            catch { unused = true; }
                            if (unused)
                            {
                                unusedConnectors.Insert(c);
                                firstConnector ??= c;
                            }
                        }
                    }

                    if (firstConnector != null)
                    {
                        newSystem = systemType == "mechanical"
                            ? doc.Create.NewMechanicalSystem(firstConnector, unusedConnectors, DuctSystemType.SupplyAir)
                            : doc.Create.NewPipingSystem(firstConnector, unusedConnectors, PipeSystemType.DomesticHotWater);
                    }
                    else
                    {
                        return Err("All connectors are already assigned to systems and could not be renamed.");
                    }
                }

                if (newSystem != null)
                {
                    var nameParam = newSystem.LookupParameter("System Name");
                    if (nameParam != null && !nameParam.IsReadOnly) nameParam.Set(systemName);
                }

                if (newSystem == null) return Err($"Failed to create {systemType} system");

                return Json(new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["status"] = "success",
                    ["system_id"] = newSystem.Id.Value,
                    ["system_name"] = systemName,
                    ["system_type"] = systemType,
                    ["element_count"] = elementIds.Count,
                    ["message"] = $"Created {systemType} system '{systemName}'",
                });
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // --- create_schedule -----------------------------------------------------------------

    /// <summary>Creates a ViewSchedule for a BuiltInCategory, adding requested (or the first 5) schedulable fields.</summary>
    internal static string CreateSchedule(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var categoryStr = StrArg(args, "category");
            if (string.IsNullOrEmpty(categoryStr)) return Err("No category provided");

            var bic = FindBuiltInCategory(categoryStr);
            if (bic == null)
                return Err($"Invalid category '{categoryStr}' — use a valid BuiltInCategory name like OST_Walls, OST_Rooms, OST_Doors");

            var fields = ArrayArg(args, "fields")
                .Where(f => f.ValueKind == JsonValueKind.String)
                .Select(f => f.GetString()!)
                .ToList();
            var scheduleName = StrArg(args, "schedule_name");

            return InTransaction(doc, "Create Schedule", () =>
            {
                var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(bic.Value));

                schedule.Name = !string.IsNullOrEmpty(scheduleName)
                    ? scheduleName!
                    : $"{categoryStr!.Replace("OST_", "")} Schedule";

                var schedDef = schedule.Definition;
                var schedulableFields = schedDef.GetSchedulableFields();

                var fieldsAdded = new List<string>();
                var fieldsFailed = new List<string>();

                if (fields.Count > 0)
                {
                    foreach (var fieldName in fields)
                    {
                        bool found = false;
                        foreach (var sf in schedulableFields)
                        {
                            try
                            {
                                if (sf.GetName(doc) == fieldName)
                                {
                                    schedDef.AddField(sf);
                                    fieldsAdded.Add(fieldName);
                                    found = true;
                                    break;
                                }
                            }
                            catch { /* skip fields that reject name/add */ }
                        }
                        if (!found) fieldsFailed.Add(fieldName);
                    }
                }
                else
                {
                    // Add the first few available fields as defaults.
                    int count = 0;
                    foreach (var sf in schedulableFields)
                    {
                        if (count >= 5) break;
                        try
                        {
                            var fname = sf.GetName(doc);
                            schedDef.AddField(sf);
                            fieldsAdded.Add(fname);
                            count++;
                        }
                        catch { /* skip fields that reject add */ }
                    }
                }

                int rowCount = 0;
                try
                {
                    doc.Regenerate();
                    rowCount = schedule.GetTableData().GetSectionData(SectionType.Body).NumberOfRows;
                }
                catch { /* best effort */ }

                var catDisplay = categoryStr!.Replace("OST_", "").ToLowerInvariant();

                var payload = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["status"] = "success",
                    ["created"] = new Dictionary<string, object?>
                    {
                        ["id"] = schedule.Id.Value,
                        ["name"] = schedule.Name,
                        ["category"] = catDisplay,
                        ["fields"] = fieldsAdded,
                        ["row_count"] = rowCount,
                    },
                    ["message"] = $"Created {catDisplay} schedule with {fieldsAdded.Count} field{(fieldsAdded.Count != 1 ? "s" : "")} and {rowCount} row{(rowCount != 1 ? "s" : "")}",
                };

                if (fieldsFailed.Count > 0)
                {
                    var available = new List<string>();
                    foreach (var sf in schedulableFields)
                    {
                        try { available.Add(sf.GetName(doc)); } catch { }
                    }
                    payload["fields_not_found"] = fieldsFailed;
                    payload["available_fields"] = available.OrderBy(a => a, StringComparer.Ordinal).Take(30).ToList();
                }

                return Json(payload);
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // --- export_ifc ------------------------------------------------------------------------

    /// <summary>Exports the model to IFC (IFC2x3 default / IFC4), optionally filtered to a named view.</summary>
    internal static string ExportIfc(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var filePath = StrArg(args, "file_path");
            if (string.IsNullOrEmpty(filePath)) return Err("file_path is required (must end in .ifc)");
            if (!filePath!.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
                return Err("file_path must end in .ifc");

            var ifcVersion = StrArg(args, "ifc_version") ?? "IFC2x3";
            var exportBaseQuantities = BoolArg(args, "export_base_quantities", true);
            var viewName = StrArg(args, "view_name");

            var outputDir = Path.GetDirectoryName(filePath) ?? "";
            var fileName = Path.GetFileName(filePath);

            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            {
                try { Directory.CreateDirectory(outputDir); }
                catch (Exception dirEx) { return Err($"Cannot create output directory: {dirEx.Message}"); }
            }

            var options = new IFCExportOptions
            {
                FileVersion = ifcVersion == "IFC4" ? IFCVersion.IFC4 : IFCVersion.IFC2x3,
                ExportBaseQuantities = exportBaseQuantities,
            };

            var targetView = FindView(doc, viewName);
            if (targetView != null) options.FilterViewId = targetView.Id;

            // The Revit API requires an open transaction for IFC export.
            return InTransaction(doc, "Export IFC", () =>
            {
                var exported = doc.Export(string.IsNullOrEmpty(outputDir) ? "." : outputDir, fileName, options);
                if (!exported) return Err("IFC export failed");

                long fileSizeKb = 0;
                try { if (File.Exists(filePath)) fileSizeKb = new FileInfo(filePath).Length / 1024; }
                catch { /* best effort */ }

                return Json(new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["status"] = "success",
                    ["file_path"] = filePath,
                    ["file_size_kb"] = fileSizeKb,
                    ["ifc_version"] = ifcVersion,
                    ["message"] = $"Exported IFC to '{filePath}' ({fileSizeKb} KB)",
                });
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // --- link_file ---------------------------------------------------------------------------

    /// <summary>
    ///     Links or imports an external file: RVT via RevitLinkType/Instance; DWG/DXF/DGN link or import;
    ///     SAT/SKP/3DM import-only (forced to import even when mode=link, like the Python).
    /// </summary>
    internal static string LinkFile(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var filePath = StrArg(args, "file_path");
            if (string.IsNullOrEmpty(filePath)) return Err("file_path is required");
            if (!File.Exists(filePath)) return Err("File not found at the specified path.");

            var mode = StrArg(args, "mode") ?? "link";
            var fileExt = Path.GetExtension(filePath)!.ToLowerInvariant();
            var fileName = Path.GetFileName(filePath);
            var fileType = fileExt.TrimStart('.').ToUpperInvariant();

            return InTransaction(doc, "Link/Import File", () =>
            {
                long? resultId = null;

                if (fileExt == ".rvt")
                {
                    var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                    var linkResult = RevitLinkType.Create(doc, modelPath, new RevitLinkOptions(false));
                    if (linkResult != null && linkResult.ElementId != ElementId.InvalidElementId)
                    {
                        resultId = linkResult.ElementId.Value;
                        var linkInstance = RevitLinkInstance.Create(doc, linkResult.ElementId);
                        if (linkInstance != null) resultId = linkInstance.Id.Value;
                    }
                    mode = "link";
                }
                else if (fileExt is ".dwg" or ".dxf" or ".dgn" or ".sat" or ".skp" or ".3dm")
                {
                    var activeView = doc.ActiveView;
                    if (activeView == null) return Err("No active view available for CAD link/import");

                    if (fileExt is ".sat" or ".3dm")
                    {
                        // Rhino (.3dm) also goes through the SAT-style importer, like the Python.
                        var id = doc.Import(filePath, new SATImportOptions(), activeView);
                        if (id != null && id != ElementId.InvalidElementId) resultId = id.Value;
                        mode = "import";
                    }
                    else if (fileExt == ".skp")
                    {
                        var id = doc.Import(filePath, new SKPImportOptions(), activeView);
                        if (id != null && id != ElementId.InvalidElementId) resultId = id.Value;
                        mode = "import";
                    }
                    else
                    {
                        var options = new DWGImportOptions { Placement = ImportPlacement.Origin };
                        ElementId linkedId;
                        if (mode == "link")
                        {
                            doc.Link(filePath, options, activeView, out linkedId);
                            mode = "link";
                        }
                        else
                        {
                            doc.Import(filePath, options, activeView, out linkedId);
                            mode = "import";
                        }
                        if (linkedId != null && linkedId != ElementId.InvalidElementId) resultId = linkedId.Value;
                    }
                }
                else
                {
                    return Err($"Unsupported file type '{fileExt}'. Supported: DWG, DXF, DGN, SAT, SKP, 3DM, RVT");
                }

                var verb = mode == "link" ? "Linked" : "Imported";
                return Json(new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["status"] = "success",
                    ["element_id"] = resultId,
                    ["file_name"] = fileName,
                    ["file_type"] = fileType,
                    ["mode"] = mode,
                    ["message"] = $"{verb} file '{fileName}'",
                });
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // --- save_document --------------------------------------------------------------------------

    /// <summary>
    ///     Saves the document: SaveAs when file_path is given, in-place Save otherwise. Never-saved documents
    ///     require a file_path. Save/SaveAs must NOT run inside a transaction — none is opened here.
    /// </summary>
    internal static string SaveDocument(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var filePath = StrArg(args, "file_path");
            var overwrite = BoolArg(args, "overwrite", true);

            string pathOnDisk;
            try { pathOnDisk = doc.PathName ?? ""; } catch { pathOnDisk = ""; }

            if (!string.IsNullOrEmpty(filePath))
            {
                try
                {
                    var parent = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
                }
                catch { /* SaveAs will surface the real error */ }

                var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                doc.SaveAs(modelPath, new SaveAsOptions { OverwriteExistingFile = overwrite });

                return Json(new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["status"] = "success",
                    ["operation"] = "save_as",
                    ["file_path"] = filePath,
                    ["message"] = $"Document saved to {filePath}",
                });
            }

            if (string.IsNullOrEmpty(pathOnDisk))
                return Err("Document has never been saved — provide a file_path to save it (e.g. C:\\Models\\ESB.rvt).");

            doc.Save();
            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["status"] = "success",
                ["operation"] = "save",
                ["file_path"] = pathOnDisk,
                ["message"] = "Document saved",
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }
}
