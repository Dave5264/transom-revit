using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>
///     Extended element/analysis tools: model info, family listing/loading/placement, levels,
///     element properties/editing/deletion/transforms, filtering, statistics, rooms, materials, clash
///     detection and colour splash. Behaviour and response field names ported from the IronPython
///     handlers in revit-mcp-server.extension/revit_mcp (model_info, placement, editing, parameters,
///     transforms, analysis, rooms, clash, colors). Points cross the wire in mm, areas in m².
/// </summary>
public static partial class BridgeTools
{
    private const double SqFtToSqM = 0.092903;
    private const double CuFtToCuM = 0.0283168;

    // ==================== get_revit_model_info ====================

    /// <summary>Comprehensive model overview: project info, element counts, health, levels/rooms, views, links.</summary>
    internal static string GetRevitModelInfo(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            // ---- project information ----
            Dictionary<string, object?> projectInfo;
            try
            {
                var pi = doc.ProjectInformation;
                projectInfo = new()
                {
                    ["name"] = string.IsNullOrEmpty(pi?.Name) ? doc.Title : pi!.Name,
                    ["number"] = string.IsNullOrEmpty(pi?.Number) ? "Not Set" : pi!.Number,
                    ["client"] = string.IsNullOrEmpty(pi?.ClientName) ? "Not Set" : pi!.ClientName,
                    ["file_name"] = doc.Title,
                };
            }
            catch
            {
                projectInfo = new()
                {
                    ["name"] = doc.Title, ["number"] = "Not Set",
                    ["client"] = "Not Set", ["file_name"] = doc.Title,
                };
            }

            // ---- element counts by major category ----
            var majorCategories = new (string Name, BuiltInCategory Bic)[]
            {
                ("Walls", BuiltInCategory.OST_Walls), ("Floors", BuiltInCategory.OST_Floors),
                ("Ceilings", BuiltInCategory.OST_Ceilings), ("Roofs", BuiltInCategory.OST_Roofs),
                ("Doors", BuiltInCategory.OST_Doors), ("Windows", BuiltInCategory.OST_Windows),
                ("Stairs", BuiltInCategory.OST_Stairs), ("Railings", BuiltInCategory.OST_Railings),
                ("Columns", BuiltInCategory.OST_Columns), ("Structural_Framing", BuiltInCategory.OST_StructuralFraming),
                ("Furniture", BuiltInCategory.OST_Furniture), ("Lighting_Fixtures", BuiltInCategory.OST_LightingFixtures),
                ("Plumbing_Fixtures", BuiltInCategory.OST_PlumbingFixtures),
            };
            var elementCounts = new Dictionary<string, object?>();
            int totalElements = 0;
            foreach (var (name, bic) in majorCategories)
            {
                int count;
                try
                {
                    count = new FilteredElementCollector(doc).OfCategory(bic)
                        .WhereElementIsNotElementType().GetElementCount();
                }
                catch { count = 0; }
                elementCounts[name] = count;
                totalElements += count;
            }

            // ---- warnings ----
            int warningsCount = 0, criticalWarnings = 0;
            try
            {
                var warnings = doc.GetWarnings();
                warningsCount = warnings.Count;
                criticalWarnings = warnings.Count(w => w.GetSeverity() == FailureSeverity.Error);
            }
            catch { /* keep zeros */ }

            // ---- levels ----
            var levelsInfo = new List<object?>();
            try
            {
                levelsInfo = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(l => (object?)new Dictionary<string, object?>
                    {
                        ["name"] = ElemName(l),
                        // Suffixed keys, both units (same shape as list_levels) — the old bare "elevation"
                        // emitted raw internal feet with nothing in the payload saying so.
                        ["elevation_mm"] = Math.Round(FtToMm(l.Elevation), 1),
                        ["elevation_feet"] = Math.Round(l.Elevation, 2),
                    })
                    .ToList();
            }
            catch { /* leave empty */ }

            // ---- rooms ----
            var roomsInfo = new List<object?>();
            int unplacedRooms = 0;
            try
            {
                foreach (var room in new FilteredElementCollector(doc)
                             .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType())
                {
                    try
                    {
                        var nameParam = room.LookupParameter("Name");
                        var roomName = nameParam != null && nameParam.HasValue
                            ? nameParam.AsString() ?? "Unnamed Room" : "Unnamed Room";
                        var numberParam = room.LookupParameter("Number");
                        var roomNumber = numberParam != null && numberParam.HasValue
                            ? numberParam.AsString() ?? "" : "";

                        var levelName = "Unknown Level";
                        try
                        {
                            var level = doc.GetElement(room.LevelId);
                            if (level != null) levelName = ElemName(level);
                        }
                        catch { /* keep Unknown Level */ }

                        double area = 0;
                        try { area = room.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsDouble() ?? 0; }
                        catch { /* treat as unplaced */ }
                        bool isPlaced = area > 0;
                        if (!isPlaced) unplacedRooms++;

                        var roomInfo = new Dictionary<string, object?>
                        {
                            ["name"] = roomName, ["number"] = roomNumber,
                            ["level"] = levelName, ["is_placed"] = isPlaced,
                        };
                        if (isPlaced) roomInfo["area_sqm"] = Math.Round(area * SqFtToSqM, 2);   // ROOM_AREA is ft²
                        roomsInfo.Add(roomInfo);
                    }
                    catch { /* skip room */ }
                }
            }
            catch { /* leave empty */ }

            // ---- views and sheets ----
            int sheetsCount = 0, viewsCount = 0;
            int floorPlans = 0, elevations = 0, sections = 0, threeDViews = 0, schedules = 0;
            int viewBreakdownOther = 0;
            try
            {
                sheetsCount = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Sheets)
                    .WhereElementIsNotElementType().GetElementCount();
                int otherViews = 0;
                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                {
                    // ViewSheet derives from View, so sheets came through this collector and were counted
                    // into total_views AS WELL AS into sheets_count — a caller adding the two double-counted
                    // every sheet. Sheets are reported separately; they are not views here.
                    if (v.IsTemplate || v.ViewType == ViewType.Internal || v.ViewType == ViewType.ProjectBrowser
                        || v.ViewType == ViewType.DrawingSheet)
                        continue;
                    viewsCount++;
                    switch (v.ViewType)
                    {
                        case ViewType.FloorPlan: floorPlans++; break;
                        case ViewType.Elevation: elevations++; break;
                        case ViewType.Section: sections++; break;
                        case ViewType.ThreeD: threeDViews++; break;
                        case ViewType.Schedule: schedules++; break;
                        // Legends, ceiling plans, drafting views, area plans, … so the breakdown sums to
                        // total_views instead of quietly falling short of it.
                        default: otherViews++; break;
                    }
                }
                viewBreakdownOther = otherViews;
            }
            catch { /* keep zeros */ }

            // ---- linked models ----
            var linkedModels = new List<object?>();
            try
            {
                foreach (var link in new FilteredElementCollector(doc)
                             .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    try
                    {
                        string status = "Unknown";
                        try
                        {
                            if (doc.GetElement(link.GetTypeId()) is RevitLinkType lt)
                                status = lt.GetLinkedFileStatus().ToString();
                        }
                        catch { /* keep Unknown */ }
                        linkedModels.Add(new Dictionary<string, object?>
                        {
                            ["name"] = ElemName(link),
                            ["status"] = status,
                            ["is_loaded"] = link.GetLinkDocument() != null,
                            ["is_pinned"] = link.Pinned,
                        });
                    }
                    catch { /* skip link */ }
                }
            }
            catch { /* leave empty */ }

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["project_info"] = projectInfo,
                ["element_summary"] = new Dictionary<string, object?>
                {
                    ["total_elements"] = totalElements, ["by_category"] = elementCounts,
                },
                ["model_health"] = new Dictionary<string, object?>
                {
                    ["total_warnings"] = warningsCount,
                    ["critical_warnings"] = criticalWarnings,
                    ["unplaced_rooms"] = unplacedRooms,
                },
                ["spatial_organization"] = new Dictionary<string, object?>
                {
                    ["levels"] = levelsInfo, ["rooms"] = roomsInfo, ["room_count"] = roomsInfo.Count,
                },
                ["documentation"] = new Dictionary<string, object?>
                {
                    ["total_views"] = viewsCount,
                    ["view_breakdown"] = new Dictionary<string, object?>
                    {
                        ["floor_plans"] = floorPlans, ["elevations"] = elevations,
                        ["sections"] = sections, ["3d_views"] = threeDViews, ["schedules"] = schedules,
                        ["other"] = viewBreakdownOther,   // legends, ceiling/area plans, drafting views…
                    },
                    // Sheets are NOT included in total_views (they were, alongside this count).

                    ["sheets_count"] = sheetsCount,
                },
                ["linked_models"] = new Dictionary<string, object?>
                {
                    ["count"] = linkedModels.Count, ["models"] = linkedModels,
                },
            });
        }
        catch (Exception ex)
        {
            return Err("Failed to retrieve model information: " + ex.Message);
        }
    }

    // ==================== list_families ====================

    /// <summary>Flat list of up to 50 family types (family/type/category/is_active).</summary>
    internal static string ListFamilies(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var families = new List<object?>();
            foreach (var symbol in new FilteredElementCollector(doc)
                         .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
            {
                if (families.Count >= 50) break;
                try
                {
                    families.Add(new Dictionary<string, object?>
                    {
                        ["family_name"] = symbol.Family != null ? ElemName(symbol.Family) : symbol.FamilyName,
                        ["type_name"] = ElemName(symbol),
                        ["category"] = symbol.Category?.Name ?? "Unknown",
                        ["is_active"] = symbol.IsActive,
                    });
                }
                catch { /* skip symbol */ }
            }
            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["families"] = families, ["truncated_total"] = families.Count,
            });
        }
        catch (Exception ex)
        {
            return Err("Failed to list families: " + ex.Message);
        }
    }

    // ==================== list_family_categories ====================

    /// <summary>All family-symbol categories with the number of types in each, sorted by name.</summary>
    internal static string ListFamilyCategories(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var counts = new Dictionary<string, int>();
            foreach (var symbol in new FilteredElementCollector(doc)
                         .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
            {
                string catName = "Unknown";
                try { catName = symbol.Category?.Name ?? "Unknown"; } catch { /* keep Unknown */ }
                counts[catName] = counts.TryGetValue(catName, out var c) ? c + 1 : 1;
            }
            var sorted = new Dictionary<string, object?>();
            foreach (var kv in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sorted[kv.Key] = kv.Value;

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["categories"] = sorted, ["total_categories"] = sorted.Count,
            });
        }
        catch (Exception ex)
        {
            return Err("Failed to list family categories: " + ex.Message);
        }
    }

    // ==================== load_family ====================

    /// <summary>Loads an .rfa from disk. LoadFamily manages its own transaction, so no wrapper here.</summary>
    internal static string LoadFamily(UIApplication app, Document doc, JsonElement args)
    {
        var filePath = StrArg(args, "file_path");
        if (string.IsNullOrEmpty(filePath))
            return Err("file_path is required (full path to a .rfa file)");
        if (!File.Exists(filePath))
            return Err($"Family file not found: {filePath}");

        bool loaded;
        try { loaded = doc.LoadFamily(filePath!, out _); }
        catch (Exception ex) { return Err("LoadFamily failed: " + ex.Message); }

        var famName = Path.GetFileNameWithoutExtension(filePath!);
        return Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["status"] = loaded ? "success" : "already_loaded",
            ["family_name"] = famName,
            ["file_path"] = filePath,
            ["message"] = loaded
                ? $"Loaded family '{famName}'. Its types are now available to place_family."
                : $"Family '{famName}' was already loaded (or no new types added)",
        });
    }

    // ==================== place_family ====================

    /// <summary>
    ///     Places a family instance at a point (mm). Doors/windows and OneLevelBasedHosted families snap to
    ///     the nearest wall via the host overload; otherwise the plain/level overload is used. Optional
    ///     rotation (degrees, about the vertical axis at the point) and instance-parameter properties.
    /// </summary>
    internal static string PlaceFamily(UIApplication app, Document doc, JsonElement args)
    {
        var familyName = StrArg(args, "family_name");
        if (string.IsNullOrEmpty(familyName)) return Err("No family_name provided");
        var typeName = StrArg(args, "type_name");

        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("location", out var loc) || loc.ValueKind != JsonValueKind.Object
            || !loc.TryGetProperty("x", out _) || !loc.TryGetProperty("y", out _) || !loc.TryGetProperty("z", out _))
            return Err("Invalid location - must include x, y, z coordinates");
        var point = PointOf(loc);

        double rotation = DblArg(args, "rotation") ?? 0.0;
        var levelName = StrArg(args, "level_name");

        Level? targetLevel = null;
        if (!string.IsNullOrEmpty(levelName))
        {
            targetLevel = FindLevel(doc, levelName);
            if (targetLevel == null) return Err($"Level not found: {levelName}");
        }

        return InTransaction(doc, "Place Family Instance via Transom", () =>
        {
            var symbol = FindSymbol(doc, familyName, typeName);
            if (symbol == null)
                return Err($"Family type not found: {familyName} - {typeName ?? "Any"}");

            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }

            // Wall-hosted detection (doors/windows and OneLevelBasedHosted families).
            bool needsWallHost = false;
            try { needsWallHost = symbol.Family?.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted; }
            catch { /* keep false */ }
            try
            {
                var catId = symbol.Category?.Id.Value;
                if (catId == (long)BuiltInCategory.OST_Doors || catId == (long)BuiltInCategory.OST_Windows)
                    needsWallHost = true;
            }
            catch { /* keep current value */ }

            Element? hostWall = null;
            if (needsWallHost)
            {
                double? bestDist = null;
                foreach (var w in new FilteredElementCollector(doc)
                             .OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType())
                {
                    try
                    {
                        if (w.Location is not LocationCurve lc) continue;
                        var d = lc.Curve.Distance(point);
                        if (bestDist == null || d < bestDist) { bestDist = d; hostWall = w; }
                    }
                    catch { /* skip wall */ }
                }
            }

            FamilyInstance instance;
            if (hostWall != null)
            {
                instance = targetLevel != null
                    ? doc.Create.NewFamilyInstance(point, symbol, hostWall, targetLevel, StructuralType.NonStructural)
                    : doc.Create.NewFamilyInstance(point, symbol, hostWall, StructuralType.NonStructural);
            }
            else if (needsWallHost)
            {
                // Hosted family but no wall found — fail clearly instead of creating a broken instance.
                return Err($"Family '{familyName}' must be hosted by a wall, but no wall was found near "
                           + "the requested location. Create the host wall first.");
            }
            else
            {
                instance = targetLevel != null
                    ? doc.Create.NewFamilyInstance(point, symbol, targetLevel, StructuralType.NonStructural)
                    : doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
            }

            bool rotationApplied = rotation == 0;
            if (rotation != 0)
            {
                try
                {
                    var axis = Line.CreateBound(point, point.Add(XYZ.BasisZ));
                    ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotation * Math.PI / 180.0);
                    rotationApplied = true;
                }
                catch { /* element may not support rotation — reported below, not silently claimed */ }
            }

            // Optional instance properties.
            var propertiesSet = new List<object?>();
            var propertiesFailed = new List<object?>();
            if (args.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in props.EnumerateObject())
                {
                    try
                    {
                        var param = instance.LookupParameter(prop.Name);
                        if (param == null) { propertiesFailed.Add($"{prop.Name} (not found)"); continue; }
                        if (param.IsReadOnly) { propertiesFailed.Add($"{prop.Name} (read-only)"); continue; }
                        var reason = SetParamFromJson(param, prop.Value);
                        if (reason == null) propertiesSet.Add(prop.Name);
                        else propertiesFailed.Add($"{prop.Name} ({reason})");
                    }
                    catch (Exception pex)
                    {
                        propertiesFailed.Add($"{prop.Name} (error: {pex.Message})");
                    }
                }
            }

            // Actual placed location may differ (level constraints, host snap). Report in mm.
            try { doc.Regenerate(); } catch { /* location read below falls back */ }
            var actual = point;
            try { if (instance.Location is LocationPoint lp) actual = lp.Point; } catch { /* keep requested */ }

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["element_id"] = instance.Id.Value,
                ["family_name"] = familyName,
                ["type_name"] = typeName,
                ["requested_location"] = MmPoint(point),
                ["actual_location"] = MmPoint(actual),
                ["rotation_degrees"] = rotationApplied ? rotation : 0,
                ["rotation_applied"] = rotationApplied,
                ["level"] = targetLevel != null ? levelName : null,
                ["properties_set"] = propertiesSet,
                ["properties_failed"] = propertiesFailed,
            });
        });
    }

    // ==================== list_levels ====================

    /// <summary>All levels with elevations in mm and feet, sorted ascending.</summary>
    internal static string ListLevels(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => (object?)new Dictionary<string, object?>
                {
                    ["name"] = ElemName(l),
                    ["elevation_mm"] = Math.Round(FtToMm(l.Elevation), 0),
                    ["elevation_feet"] = Math.Round(l.Elevation, 4),
                    ["id"] = l.Id.Value,
                })
                .ToList();

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["levels"] = levels, ["total_levels"] = levels.Count,
            });
        }
        catch (Exception ex)
        {
            return Err("Failed to list levels: " + ex.Message);
        }
    }

    // ==================== list_category_parameters ====================

    /// <summary>Parameters of a sample element in the named category (name/storage type/sample value).</summary>
    internal static string ListCategoryParameters(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var categoryName = StrArg(args, "category_name");
            if (string.IsNullOrEmpty(categoryName)) return Err("category_name is required");

            var category = CategoryByDisplayName(doc, categoryName!);
            if (category == null) return Err($"Category '{categoryName}' not found");

            var sample = new FilteredElementCollector(doc).OfCategoryId(category.Id)
                .WhereElementIsNotElementType().FirstElement();
            if (sample == null) return Err($"No elements found in category '{categoryName}'");

            var parameters = new List<Dictionary<string, object?>>();
            foreach (Parameter param in sample.Parameters)
            {
                try
                {
                    var display = ParamDisplayValue(doc, param);
                    parameters.Add(new Dictionary<string, object?>
                    {
                        ["name"] = param.Definition.Name,
                        ["storage_type"] = param.StorageType.ToString(),
                        ["has_value"] = param.HasValue,
                        ["sample_value"] = param.HasValue ? (display.Length > 0 ? display : "None") : "N/A",
                    });
                }
                catch { /* skip parameter */ }
            }
            parameters.Sort((a, b) => string.CompareOrdinal((string?)a["name"], (string?)b["name"]));

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["category"] = categoryName,
                ["parameter_count"] = parameters.Count,
                ["parameters"] = parameters.Cast<object?>().ToList(),
            });
        }
        catch (Exception ex)
        {
            return Err("Failed to list parameters: " + ex.Message);
        }
    }

    // ==================== get_element_properties ====================

    /// <summary>Ordered instance (and optionally type) parameters of one element, with display values.</summary>
    internal static string GetElementProperties(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            if (args.ValueKind != JsonValueKind.Object
                || !args.TryGetProperty("element_id", out var idEl) || !TryGetLong(idEl, out long elementId))
                return Err("element_id is required");

            var elem = doc.GetElement(new ElementId(elementId));
            if (elem == null) return Err("Element not found.");

            bool includeTypeParams = BoolArg(args, "include_type_params", true);

            string category = "";
            try { category = elem.Category?.Name ?? ""; } catch { /* keep empty */ }

            string family = "", typeName = "";
            Element? elemType = null;
            try
            {
                var typeId = elem.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    elemType = doc.GetElement(typeId);
                    if (elemType != null)
                    {
                        typeName = ElemName(elemType);
                        if (elemType is FamilySymbol fs && fs.Family != null) family = ElemName(fs.Family);
                        else if (elemType is ElementType et) family = et.FamilyName ?? "";
                    }
                }
            }
            catch { /* keep empties */ }

            var parameters = new List<object?>();
            var seen = new HashSet<string>();

            void Collect(Element source, bool isInstance)
            {
                IList<Parameter> ordered;
                try { ordered = source.GetOrderedParameters(); } catch { return; }
                foreach (var param in ordered)
                {
                    try
                    {
                        var name = param.Definition.Name;
                        if (!seen.Add(name)) continue;
                        parameters.Add(new Dictionary<string, object?>
                        {
                            ["name"] = name,
                            ["value"] = ParamDisplayValue(doc, param),
                            ["storage_type"] = param.StorageType.ToString(),
                            ["read_only"] = param.IsReadOnly,
                            ["group"] = ParamGroupLabel(param),
                            ["is_instance"] = isInstance,
                        });
                    }
                    catch { /* skip parameter */ }
                }
            }

            Collect(elem, true);
            if (includeTypeParams && elemType != null) Collect(elemType, false);

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["element_id"] = elementId,
                ["category"] = category,
                ["family"] = family,
                ["type"] = typeName,
                ["parameters"] = parameters,
                ["parameter_count"] = parameters.Count,
                ["message"] = $"Found {parameters.Count} parameters on element {elementId}",
            });
        }
        catch (Exception ex)
        {
            return Err("Failed to get element properties: " + ex.Message);
        }
    }

    // ==================== get_selected_elements ====================

    /// <summary>Details of the elements currently selected in the Revit UI.</summary>
    internal static string GetSelectedElements(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return Err("No active UI document");

            var elements = new List<object?>();
            foreach (var elemId in uidoc.Selection.GetElementIds())
            {
                var elem = doc.GetElement(elemId);
                if (elem == null) continue;

                var info = new Dictionary<string, object?>
                {
                    ["id"] = elemId.Value,
                    ["category"] = elem.Category?.Name ?? "Unknown",
                    ["type"] = ElemName(elem),
                };

                try
                {
                    var levelId = elem.LevelId;
                    if (levelId != null && levelId != ElementId.InvalidElementId)
                    {
                        var level = doc.GetElement(levelId);
                        if (level != null) info["level"] = ElemName(level);
                    }
                }
                catch { /* level optional */ }

                var parameters = new Dictionary<string, object?>();
                foreach (var pname in new[] { "Mark", "Comments", "Length", "Area", "Volume", "Width", "Height" })
                {
                    try
                    {
                        var p = elem.LookupParameter(pname);
                        if (p == null || !p.HasValue) continue;
                        switch (p.StorageType)
                        {
                            case StorageType.String:
                                var s = p.AsString();
                                if (!string.IsNullOrEmpty(s)) parameters[pname] = s;
                                break;
                            case StorageType.Double:
                                // Display value (project units, suffixed) — the raw AsDouble() emitted
                                // internal ft/ft²/ft³ under bare keys like "Length" with no unit hint.
                                parameters[pname] = ParamDisplayValue(doc, p);
                                break;
                            case StorageType.Integer:
                                parameters[pname] = p.AsInteger().ToString(CultureInfo.InvariantCulture);
                                break;
                        }
                    }
                    catch { /* skip parameter */ }
                }
                if (parameters.Count > 0) info["parameters"] = parameters;

                elements.Add(info);
            }

            var count = elements.Count;
            var message = count == 0 ? "No elements currently selected"
                : count == 1 ? "1 element currently selected"
                : $"{count} elements currently selected";

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["elements"] = elements, ["count"] = count, ["message"] = message,
            });
        }
        catch (Exception ex)
        {
            return Err("Failed to get selected elements: " + ex.Message);
        }
    }

    // ==================== modify_element ====================

    /// <summary>Sets one or more named parameters on an element; reports per-parameter changes/failures.</summary>
    internal static string ModifyElement(UIApplication app, Document doc, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("element_id", out var idEl) || !TryGetLong(idEl, out long elementId))
            return Err("No element_id provided");
        if (!args.TryGetProperty("parameters", out var paramsEl) || paramsEl.ValueKind != JsonValueKind.Object
            || !paramsEl.EnumerateObject().Any())
            return Err("No parameters provided");

        var elem = doc.GetElement(new ElementId(elementId));
        if (elem == null) return Err($"Element {elementId} not found in the active model");

        return InTransaction(doc, "Modify Element via Transom", () =>
        {
            var changes = new List<object?>();
            var failed = new List<object?>();

            foreach (var prop in paramsEl.EnumerateObject())
            {
                // Instance first, then the element's TYPE — a type parameter otherwise reported "not found"
                // while the recovery hint listed instance params only, confirming the wrong conclusion.
                var param = elem.LookupParameter(prop.Name);
                bool onType = false;
                if (param == null)
                {
                    try
                    {
                        var tid = elem.GetTypeId();
                        if (tid != null && tid != ElementId.InvalidElementId && doc.GetElement(tid) is { } te)
                        {
                            param = te.LookupParameter(prop.Name);
                            onType = param != null;
                        }
                    }
                    catch { /* no type / not accessible */ }
                }
                if (param == null)
                {
                    var available = new List<string>();
                    foreach (Parameter p in elem.Parameters)
                    {
                        try { available.Add(p.Definition.Name); } catch { /* skip */ }
                    }
                    var typeAvailable = new List<string>();
                    try
                    {
                        var tid = elem.GetTypeId();
                        if (tid != null && tid != ElementId.InvalidElementId && doc.GetElement(tid) is { } te2)
                            foreach (Parameter p in te2.Parameters)
                            { try { typeAvailable.Add(p.Definition.Name); } catch { /* skip */ } }
                    }
                    catch { /* no type */ }
                    available.Sort(StringComparer.Ordinal);
                    typeAvailable.Sort(StringComparer.Ordinal);
                    failed.Add(new Dictionary<string, object?>
                    {
                        ["parameter"] = prop.Name,
                        ["reason"] = "not found on the element or its type",
                        ["available_parameters"] = available.Take(20).Cast<object?>().ToList(),
                        ["available_type_parameters"] = typeAvailable.Take(20).Cast<object?>().ToList(),
                    });
                    continue;
                }
                if (param.IsReadOnly)
                {
                    failed.Add(new Dictionary<string, object?>
                    {
                        ["parameter"] = prop.Name, ["reason"] = "read-only",
                    });
                    continue;
                }

                var oldValue = ParamRawValueString(param);
                try
                {
                    var reason = SetParamFromJson(param, prop.Value);
                    if (reason != null)
                    {
                        failed.Add(new Dictionary<string, object?>
                        {
                            ["parameter"] = prop.Name,
                            ["reason"] = reason == "unsupported type" ? "unsupported storage type" : $"set failed: {reason}",
                        });
                        continue;
                    }
                    changes.Add(new Dictionary<string, object?>
                    {
                        ["parameter"] = prop.Name,
                        ["old_value"] = oldValue,
                        ["new_value"] = JsonValueString(prop.Value),
                        ["status"] = "set",
                        // A type write changes EVERY instance of that type — say so, don't let it read
                        // as a per-instance edit.
                        ["binding"] = onType ? "type" : "instance",
                    });
                }
                catch (Exception setEx)
                {
                    failed.Add(new Dictionary<string, object?>
                    {
                        ["parameter"] = prop.Name, ["reason"] = $"set failed: {setEx.Message}",
                    });
                }
            }

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["element_id"] = elementId,
                ["changes"] = changes,
                ["failed"] = failed,
                ["message"] = $"Modified {changes.Count} parameter{(changes.Count != 1 ? "s" : "")} on element {elementId}",
            });
        });
    }

    // ==================== delete_elements ====================

    /// <summary>Deletes elements (validated up front) and reports primary and cascaded deletions.</summary>
    internal static string DeleteElements(UIApplication app, Document doc, JsonElement args)
    {
        var ids = IdListArg(args, "element_ids", out int droppedIds);
        if (ids.Count == 0) return Err("No element_ids provided");
        // Refuse rather than delete a subset: an unparseable entry used to be dropped silently, so the
        // response reported a smaller deleted_count as an unqualified success.
        if (droppedIds > 0)
            return Err($"{droppedIds} entr{(droppedIds == 1 ? "y" : "ies")} in element_ids could not be read as an element id " +
                       "— nothing was deleted. Pass integers (or numeric strings) only.");

        foreach (var id in ids)
            if (doc.GetElement(id) == null)
                return Err($"Element {id.Value} not found in the active model");

        return InTransaction(doc, "Delete Elements via Transom", () =>
        {
            var deletedIds = new List<long>();
            var cascadedIds = new List<long>();
            var alreadyGone = new List<long>();

            foreach (var id in ids)
            {
                // Deleting a host CASCADES to its hosted elements, so an id later in the list may already
                // be gone — doc.Delete would throw and roll the whole batch back. Re-check liveness here
                // (the up-front validation ran before any deletion).
                if (doc.GetElement(id) == null) { alreadyGone.Add(id.Value); continue; }

                var result = doc.Delete(id);
                // Only count what Revit actually removed: Delete can return an empty set having deleted
                // nothing, which was previously still reported as a successful deletion.
                if (result == null || result.Count == 0) continue;
                deletedIds.Add(id.Value);
                foreach (var delId in result)
                    if (delId.Value != id.Value && !cascadedIds.Contains(delId.Value))
                        cascadedIds.Add(delId.Value);
            }
            cascadedIds.RemoveAll(deletedIds.Contains);

            var message = $"Deleted {deletedIds.Count} element{(deletedIds.Count != 1 ? "s" : "")}";
            if (cascadedIds.Count > 0)
                message += $" ({cascadedIds.Count} hosted element{(cascadedIds.Count != 1 ? "s" : "")} also removed)";
            if (alreadyGone.Count > 0)
                message += $"; {alreadyGone.Count} already removed by an earlier cascade";

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["deleted_count"] = deletedIds.Count,
                ["deleted_ids"] = deletedIds,
                ["cascaded_ids"] = cascadedIds,
                ["already_deleted_ids"] = alreadyGone,
                ["message"] = message,
            });
        });
    }

    // ==================== transform_elements ====================

    /// <summary>
    ///     Move/copy/rotate/mirror elements via ElementTransformUtils. Points and vectors are mm; rotation is
    ///     degrees about a vertical axis at axis_point; mirror uses Plane.CreateByNormalAndOrigin. Pinned
    ///     elements are skipped with a per-element note instead of failing the whole call.
    /// </summary>
    internal static string TransformElements(UIApplication app, Document doc, JsonElement args)
    {
        var ids = IdListArg(args, "element_ids", out int droppedXf);
        if (ids.Count == 0) return Err("element_ids is required and must not be empty");
        if (droppedXf > 0)
            return Err($"{droppedXf} entr{(droppedXf == 1 ? "y" : "ies")} in element_ids could not be read as an element id " +
                       "— nothing was transformed. Pass integers (or numeric strings) only.");

        var operation = StrArg(args, "operation");
        if (string.IsNullOrEmpty(operation)) return Err("operation is required (move, copy, rotate, mirror)");
        if (operation is not ("move" or "copy" or "rotate" or "mirror"))
            return Err($"Invalid operation '{operation}'. Use: move, copy, rotate, mirror");

        var targets = new List<ElementId>();
        var skipped = new List<object?>();
        foreach (var id in ids)
        {
            var elem = doc.GetElement(id);
            if (elem == null) return Err($"Element {id.Value} not found");
            if (elem.Pinned)
                skipped.Add(new Dictionary<string, object?>
                {
                    ["id"] = id.Value,
                    ["reason"] = "pinned — unpin it first using modify_element before transforming",
                });
            else
                targets.Add(id);
        }

        // Validate operation arguments up front (no transaction needed to reject bad input).
        XYZ? translation = null, center = null;
        double angleRad = 0;
        Plane? mirrorPlane = null;
        switch (operation)
        {
            case "move":
            case "copy":
                translation = PointArg(args, "vector");
                if (translation == null) return Err($"vector is required for {operation} operation");
                break;
            case "rotate":
                center = PointArg(args, "axis_point");
                if (center == null) return Err("axis_point is required for rotate operation");
                var angle = DblArg(args, "angle");
                if (angle == null) return Err("angle is required for rotate operation");
                angleRad = angle.Value * Math.PI / 180.0;
                break;
            case "mirror":
                if (args.ValueKind != JsonValueKind.Object
                    || !args.TryGetProperty("mirror_plane", out var mp) || mp.ValueKind != JsonValueKind.Object)
                    return Err("mirror_plane is required for mirror operation");
                var origin = mp.TryGetProperty("origin", out var o) && o.ValueKind == JsonValueKind.Object
                    ? PointOf(o) : XYZ.Zero;
                // The y=1 default applies only when `normal` is ABSENT ENTIRELY. It used to apply
                // per-component, so {"normal":{"x":1}} — a caller asking to mirror across the YZ plane —
                // became (1,1,0), a 45° plane, and the elements were mirrored about the wrong axis with
                // ok:true. An all-zero normal is now a clear error instead of a throw from Normalize().
                XYZ normal;
                if (mp.TryGetProperty("normal", out var n) && n.ValueKind == JsonValueKind.Object)
                {
                    var raw = new XYZ(DblOf(n, "x"), DblOf(n, "y"), DblOf(n, "z"));
                    if (raw.GetLength() < 1e-9)
                        return Err("mirror_plane.normal must have at least one non-zero component (e.g. {\"x\":1} to mirror across the YZ plane)");
                    normal = raw.Normalize();
                }
                else normal = XYZ.BasisY;   // no normal supplied at all → the documented default

                mirrorPlane = Plane.CreateByNormalAndOrigin(normal, origin);
                break;
        }

        return InTransaction(doc, "Transform Elements via Transom", () =>
        {
            var newElementIds = new List<long>();
            foreach (var id in targets)
            {
                switch (operation)
                {
                    case "move":
                        ElementTransformUtils.MoveElement(doc, id, translation!);
                        break;
                    case "copy":
                        var copied = ElementTransformUtils.CopyElement(doc, id, translation!);
                        if (copied != null) newElementIds.AddRange(copied.Select(c => c.Value));
                        break;
                    case "rotate":
                        var axis = Line.CreateBound(center!, new XYZ(center!.X, center.Y, center.Z + 1.0));
                        ElementTransformUtils.RotateElement(doc, id, axis, angleRad);
                        break;
                    case "mirror":
                        ElementTransformUtils.MirrorElement(doc, id, mirrorPlane!);
                        break;
                }
            }

            var verb = operation switch
            {
                "move" => "Moved", "copy" => "Copied", "rotate" => "Rotated", "mirror" => "Mirrored",
                _ => operation,
            };
            var result = new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["operation"] = operation,
                ["count"] = targets.Count,
                ["message"] = $"{verb} {targets.Count} element{(targets.Count != 1 ? "s" : "")}",
            };
            if (operation == "copy" && newElementIds.Count > 0) result["new_element_ids"] = newElementIds;
            if (skipped.Count > 0) result["skipped"] = skipped;
            return Json(result);
        });
    }

    // ==================== ai_element_filter ====================

    /// <summary>Filters elements by category / type-name substring / active-view visibility / bounding box.</summary>
    internal static string AiElementFilter(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var category = StrArg(args, "category");
            var typeName = StrArg(args, "type_name");
            bool visibleInView = BoolArg(args, "visible_in_view");
            int maxElements = IntArg(args, "max_elements", 50);

            FilteredElementCollector collector;
            if (visibleInView)
            {
                var activeView = doc.ActiveView;
                if (activeView == null) return Err("No active view for visibility filter");
                if (activeView.ViewType is ViewType.Schedule or ViewType.Internal or ViewType.DrawingSheet)
                    return Err($"Current view type '{activeView.ViewType}' does not support element filtering "
                               + "— switch to a plan, section, or 3D view");
                collector = new FilteredElementCollector(doc, activeView.Id);
            }
            else
            {
                collector = new FilteredElementCollector(doc);
            }

            if (!string.IsNullOrEmpty(category))
            {
                // GEN-05: was a case-SENSITIVE enum parse, so "Walls" and "ost_walls" both failed here while
                // working in the sibling colour/parameter tools.
                var bic = ResolveCategoryName(category);
                if (bic == null)
                    return Err($"Invalid category: {category}. Accepts \"OST_Walls\", \"Walls\", \"walls\" or a friendly alias like \"ducts\".");
                collector = collector.OfCategory(bic.Value);
            }
            collector = collector.WhereElementIsNotElementType();

            var bboxMin = PointArg(args, "bounding_box_min");
            var bboxMax = PointArg(args, "bounding_box_max");
            if (bboxMin != null && bboxMax != null)
            {
                try { collector = collector.WherePasses(new BoundingBoxIntersectsFilter(new Outline(bboxMin, bboxMax))); }
                catch { /* mirror the Python: a bad bbox is ignored, not fatal */ }
            }

            var allElements = collector.ToElements();
            var elements = new List<object?>();

            // Count every element that passes ALL filters (including type_name), not just those returned —
            // the cap stops collection but must not stop counting, or a capped response looks complete.
            int totalMatched = 0;
            bool truncated = false;

            foreach (var elem in allElements)
            {
                if (!string.IsNullOrEmpty(typeName))
                {
                    bool matches;
                    try
                    {
                        matches = ElemName(elem).IndexOf(typeName!, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!matches)
                        {
                            var typeId = elem.GetTypeId();
                            if (typeId != null && typeId != ElementId.InvalidElementId
                                && doc.GetElement(typeId) is { } typeElem)
                                matches = ElemName(typeElem).IndexOf(typeName!, StringComparison.OrdinalIgnoreCase) >= 0;
                        }
                    }
                    catch { continue; }
                    if (!matches) continue;
                }

                totalMatched++;
                if (elements.Count >= maxElements) { truncated = true; continue; }

                var info = new Dictionary<string, object?>
                {
                    ["id"] = elem.Id.Value,
                    ["category"] = elem.Category?.Name ?? "Unknown",
                    ["type"] = ElemName(elem),
                };

                try
                {
                    var levelId = elem.LevelId;
                    if (levelId != null && levelId != ElementId.InvalidElementId
                        && doc.GetElement(levelId) is { } level)
                        info["level"] = ElemName(level);
                }
                catch { /* level optional */ }

                try
                {
                    var lengthParam = elem.LookupParameter("Length");
                    if (lengthParam != null && lengthParam.HasValue)
                        info["length_mm"] = Math.Round(FtToMm(lengthParam.AsDouble()), 0);
                }
                catch { /* optional */ }
                try
                {
                    var heightParam = elem.LookupParameter("Height") ?? elem.LookupParameter("Unconnected Height");
                    if (heightParam != null && heightParam.HasValue)
                        info["height_mm"] = Math.Round(FtToMm(heightParam.AsDouble()), 0);
                }
                catch { /* optional */ }

                elements.Add(info);
            }

            var catLabel = string.IsNullOrEmpty(category)
                ? "element" : category!.Replace("OST_", "").ToLowerInvariant();

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["elements"] = elements,
                ["count"] = elements.Count,
                ["total_matched"] = totalMatched,
                ["truncated"] = truncated,
                ["message"] = $"Found {totalMatched} {catLabel}{(totalMatched != 1 ? "s" : "")} matching filters"
                              + (truncated ? $" (returning the first {elements.Count}; raise max_elements for more)" : ""),
            });
        }
        catch (Exception ex)
        {
            return Err("Failed to filter elements: " + ex.Message);
        }
    }

    // ==================== analyze_model_statistics ====================

    /// <summary>Counts model-category element instances, sorted by count descending.</summary>
    internal static string AnalyzeModelStatistics(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var categoryCounts = new Dictionary<string, int>();
            int total = 0;
            int scanned = 0;
            foreach (var elem in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                // PERF-02: walks every non-type element in the document. Cheap per element, unbounded overall.
                if ((++scanned & 0x3FF) == 0 && Abandoned())
                    return Err($"request timed out — model scan abandoned after {scanned} element(s).");
                try
                {
                    var cat = elem.Category;
                    if (cat == null || string.IsNullOrEmpty(cat.Name)) continue;
                    try { if (cat.CategoryType != CategoryType.Model) continue; }
                    catch { /* count it anyway, like the Python */ }
                    categoryCounts[cat.Name] = categoryCounts.TryGetValue(cat.Name, out var c) ? c + 1 : 1;
                    total++;
                }
                catch { /* skip element */ }
            }

            var statistics = categoryCounts.OrderByDescending(kv => kv.Value)
                .Select(kv => (object?)new Dictionary<string, object?>
                {
                    ["category"] = kv.Key, ["count"] = kv.Value,
                })
                .ToList();

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["statistics"] = statistics,
                ["total_elements"] = total,
                ["total_categories"] = statistics.Count,
                ["message"] = $"Model contains {total} element{(total != 1 ? "s" : "")} across "
                              + $"{statistics.Count} categor{(statistics.Count != 1 ? "ies" : "y")}",
            });
        }
        catch (Exception ex)
        {
            return Err("Failed to analyze model statistics: " + ex.Message);
        }
    }

    // ==================== export_room_data ====================

    /// <summary>Exports placed rooms: name/number/level, area in m², perimeter in mm, department.</summary>
    internal static string ExportRoomData(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var rooms = new List<object?>();
            foreach (var room in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType())
            {
                try
                {
                    if (room.Location == null) continue; // skip unplaced rooms

                    var info = new Dictionary<string, object?>
                    {
                        ["id"] = room.Id.Value,
                        ["name"] = "", ["number"] = "", ["level"] = "",
                        ["area_sqm"] = 0.0, ["perimeter_mm"] = 0.0, ["department"] = "",
                    };

                    try
                    {
                        var nameParam = room.get_Parameter(BuiltInParameter.ROOM_NAME);
                        info["name"] = nameParam != null ? nameParam.AsString() ?? "" : ElemName(room);
                    }
                    catch { info["name"] = ElemName(room); }

                    try
                    {
                        var numberParam = room.get_Parameter(BuiltInParameter.ROOM_NUMBER);
                        if (numberParam != null) info["number"] = numberParam.AsString() ?? "";
                    }
                    catch { /* keep empty */ }

                    try
                    {
                        var levelId = room.LevelId;
                        if (levelId != null && levelId != ElementId.InvalidElementId
                            && doc.GetElement(levelId) is { } level)
                            info["level"] = ElemName(level);
                    }
                    catch { /* keep empty */ }

                    try
                    {
                        var areaParam = room.get_Parameter(BuiltInParameter.ROOM_AREA);
                        if (areaParam != null && areaParam.HasValue)
                            info["area_sqm"] = Math.Round(areaParam.AsDouble() * SqFtToSqM, 2);
                    }
                    catch { /* keep zero */ }

                    try
                    {
                        var perimParam = room.get_Parameter(BuiltInParameter.ROOM_PERIMETER);
                        if (perimParam != null && perimParam.HasValue)
                            info["perimeter_mm"] = Math.Round(FtToMm(perimParam.AsDouble()), 0);
                    }
                    catch { /* keep zero */ }

                    try
                    {
                        var deptParam = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT);
                        if (deptParam != null) info["department"] = deptParam.AsString() ?? "";
                    }
                    catch { /* keep empty */ }

                    rooms.Add(info);
                }
                catch { /* skip room */ }
            }

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["rooms"] = rooms,
                ["count"] = rooms.Count,
                ["message"] = $"Exported data for {rooms.Count} room{(rooms.Count != 1 ? "s" : "")}",
            });
        }
        catch (Exception ex)
        {
            return Err("Failed to export room data: " + ex.Message);
        }
    }

    // ==================== get_material_quantities ====================

    /// <summary>Aggregates material areas (m²) and volumes (m³) across categories (default: arch + struct).</summary>
    internal static string GetMaterialQuantities(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var targetCategories = new List<BuiltInCategory>();
            var requested = StringListArg(args, "categories");
            if (requested.Count > 0)
            {
                // GEN-05: this used the same case-sensitive parse as ai_element_filter but SILENTLY DROPPED
                // anything it couldn't parse — so a typo produced a confident answer computed over fewer
                // categories than the caller asked for. Resolve through the shared vocabulary and say what
                // didn't resolve, like every other category-taking tool.
                var (bics, unknown) = ResolveClashCategories(requested);
                if (unknown.Count > 0)
                    return Err($"Unknown categor{(unknown.Count == 1 ? "y" : "ies")}: {string.Join(", ", unknown)}. " +
                               "Accepts \"OST_Walls\", \"Walls\", \"walls\" or a friendly alias like \"ducts\".");
                targetCategories.AddRange(bics);
            }
            else
            {
                targetCategories.AddRange(new[]
                {
                    BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors,
                    BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings,
                    BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming,
                });
            }

            // material name -> (area sqft, volume cuft, element count)
            var materialData = new Dictionary<string, (double Area, double Volume, int Count)>();
            // PERF-02: nested per-element, per-material with GetMaterialArea/GetMaterialVolume calls — the same
            // shape as the clash scan, an order of magnitude cheaper but still unbounded. Bail out once the
            // request's waiter has given up rather than finishing a computation nobody will receive.
            int scanned = 0;
            foreach (var bic in targetCategories)
            {
                try
                {
                    foreach (var elem in new FilteredElementCollector(doc)
                                 .OfCategory(bic).WhereElementIsNotElementType())
                    {
                        if ((++scanned & 0xFF) == 0 && Abandoned())
                            return Err($"request timed out — material scan abandoned after {scanned} element(s). "
                                       + "Pass a narrower `categories` list and retry.");
                        try
                        {
                            var matIds = elem.GetMaterialIds(false);
                            if (matIds == null || matIds.Count == 0) continue;
                            foreach (var matId in matIds)
                            {
                                var mat = doc.GetElement(matId);
                                if (mat == null) continue;
                                var matName = ElemName(mat);
                                if (string.IsNullOrEmpty(matName)) matName = "Unknown Material";

                                var entry = materialData.TryGetValue(matName, out var e) ? e : (0.0, 0.0, 0);
                                try { entry.Item1 += elem.GetMaterialArea(matId, false); } catch { /* skip area */ }
                                try { entry.Item2 += elem.GetMaterialVolume(matId); } catch { /* skip volume */ }
                                entry.Item3 += 1;
                                materialData[matName] = entry;
                            }
                        }
                        catch { /* skip element */ }
                    }
                }
                catch { /* skip category */ }
            }

            var materials = materialData.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (object?)new Dictionary<string, object?>
                {
                    ["name"] = kv.Key,
                    ["area_sqm"] = Math.Round(kv.Value.Area * SqFtToSqM, 2),
                    ["volume_cum"] = Math.Round(kv.Value.Volume * CuFtToCuM, 3),
                    ["element_count"] = kv.Value.Count,
                })
                .ToList();

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["materials"] = materials,
                ["total_materials"] = materials.Count,
                ["message"] = $"Material quantities from {materials.Count} "
                              + $"material{(materials.Count != 1 ? "s" : "")} across all categories",
            });
        }
        catch (Exception ex)
        {
            return Err("Failed to get material quantities: " + ex.Message);
        }
    }

    // ==================== check_clashes ====================

    /// <summary>Friendly alias → BuiltInCategory name map for clash category resolution.</summary>
    private static readonly Dictionary<string, string> ClashFriendlyNames = new()
    {
        ["walls"] = "OST_Walls", ["floors"] = "OST_Floors", ["roofs"] = "OST_Roofs",
        ["ceilings"] = "OST_Ceilings", ["columns"] = "OST_Columns",
        ["structural columns"] = "OST_StructuralColumns",
        ["structural framing"] = "OST_StructuralFraming",
        ["framing"] = "OST_StructuralFraming", ["beams"] = "OST_StructuralFraming",
        ["foundations"] = "OST_StructuralFoundation",
        ["ducts"] = "OST_DuctCurves", ["duct fittings"] = "OST_DuctFitting",
        ["pipes"] = "OST_PipeCurves", ["pipe fittings"] = "OST_PipeFitting",
        ["cable trays"] = "OST_CableTray", ["conduits"] = "OST_Conduit",
        ["plumbing fixtures"] = "OST_PlumbingFixtures",
        ["mechanical equipment"] = "OST_MechanicalEquipment",
        ["generic models"] = "OST_GenericModel",
        ["doors"] = "OST_Doors", ["windows"] = "OST_Windows",
    };

    private static readonly string[] ClashDefaultCategories =
    {
        "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings",
        "OST_StructuralFraming", "OST_StructuralColumns", "OST_StructuralFoundation",
        "OST_Columns",
        "OST_DuctCurves", "OST_DuctFitting", "OST_PipeCurves", "OST_PipeFitting",
        "OST_CableTray", "OST_Conduit", "OST_MechanicalEquipment",
    };

    /// <summary>
    ///     Hard-clash detection: for each element of set A, ElementIntersectsElementFilter finds intersecting
    ///     members of set B (deduped pairs, capped at max_clashes). Default scope is the physical
    ///     arch/struct/MEP categories checked against itself.
    /// </summary>
    internal static string CheckClashes(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            int maxClashes = IntArg(args, "max_clashes", 200);
            var aNames = StringListArg(args, "set_a_categories");
            var bNames = StringListArg(args, "set_b_categories");
            if (aNames.Count == 0 && bNames.Count == 0) aNames = ClashDefaultCategories.ToList();

            var (aBics, aUnknown) = ResolveClashCategories(aNames);
            var (bBics, bUnknown) = bNames.Count > 0
                ? ResolveClashCategories(bNames)
                : (aBics.ToList(), new List<string>()); // self-check: B mirrors A

            if (aBics.Count == 0 || bBics.Count == 0)
                return Err("No valid categories to check (unknown: "
                           + string.Join(", ", aUnknown.Concat(bUnknown))
                           + "). Use BuiltInCategory ids like OST_Walls or aliases like 'ducts', 'beams'.");

            var setA = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(aBics))
                .WhereElementIsNotElementType()
                .ToElements();
            var bFilter = new ElementMulticategoryFilter(bBics);

            var clashes = new List<object?>();
            var seen = new HashSet<(long, long)>();
            bool truncated = false;

            // PERF-02: this is |A| collector builds and O(|A|·|B|) SOLID-GEOMETRY intersection tests, and
            // max_clashes bounds the OUTPUT, not the scan — on a clean model the early exit never fires, so the
            // full sweep always runs. InTransaction consults Abandoned() before committing precisely because the
            // shim's waiter can time out mid-operation; no read-side tool did, so Revit kept grinding on the API
            // thread (UI blocked) long past the point where anyone could receive the answer, and the client's
            // retry started the whole scan again. Stop as soon as nobody is listening.
            int scanned = 0;
            foreach (var elem in setA)
            {
                // Checking every 64 elements rather than every element keeps the delegate call off the hot path.
                if ((++scanned & 0x3F) == 0 && Abandoned())
                    return Err("request timed out — clash scan abandoned after "
                               + $"{scanned} of {setA.Count} element(s). Narrow set_a_categories/set_b_categories and retry.");
                try
                {
                    long eid = elem.Id.Value;
                    var hits = new FilteredElementCollector(doc)
                        .WherePasses(bFilter)
                        .WhereElementIsNotElementType()
                        .WherePasses(new ElementIntersectsElementFilter(elem));
                    foreach (var hit in hits)
                    {
                        long hid = hit.Id.Value;
                        if (hid == eid) continue;
                        var pair = eid < hid ? (eid, hid) : (hid, eid);
                        if (!seen.Add(pair)) continue;
                        clashes.Add(new Dictionary<string, object?>
                        {
                            ["element_a"] = Describe(elem),
                            ["element_b"] = Describe(hit),
                            ["location_mm"] = BboxCenterMm(hit) ?? BboxCenterMm(elem),
                        });
                        if (clashes.Count >= maxClashes) { truncated = true; break; }
                    }
                }
                catch { /* elements without solid geometry make the intersects filter throw — skip */ }
                if (truncated) break;
            }

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["clash_count"] = clashes.Count,
                ["truncated"] = truncated,
                ["checked"] = new Dictionary<string, object?>
                {
                    ["set_a_categories"] = aNames,
                    ["set_b_categories"] = bNames.Count > 0 ? bNames : aNames,
                    ["set_a_element_count"] = setA.Count,
                },
                ["clashes"] = clashes,
                ["message"] = $"Found {clashes.Count} clash(es)",
                ["note"] = "Auto-joined concrete and geometry-less elements (e.g. rebar) are not reported "
                           + "by Revit's interference logic.",
            });
        }
        catch (Exception ex)
        {
            return Err("clash_check failed: " + ex.Message);
        }
    }

    // ==================== color_splash ====================

    /// <summary>
    ///     Colours the elements of a category (in the active view, plus other views of the same ViewType) by
    ///     the value of a parameter: distinct colours per unique value, optional blue→red gradient for
    ///     numeric parameters, optional custom hex palette. Overrides projection/cut line colours and solid
    ///     surface/cut fill.
    /// </summary>
    internal static string ColorSplash(UIApplication app, Document doc, JsonElement args)
    {
        var categoryName = StrArg(args, "category_name");
        var parameterName = StrArg(args, "parameter_name");
        if (string.IsNullOrEmpty(categoryName) || string.IsNullOrEmpty(parameterName))
            return Err("category_name and parameter_name are required");
        bool useGradient = BoolArg(args, "use_gradient");
        var customColors = StringListArg(args, "custom_colors");

        var category = CategoryByDisplayName(doc, categoryName!);
        if (category == null) return Err($"Category '{categoryName}' not found");

        var elements = new FilteredElementCollector(doc).OfCategoryId(category.Id)
            .WhereElementIsNotElementType().ToElements();
        if (elements.Count == 0) return Err($"No elements found in category '{categoryName}'");

        var activeView = doc.ActiveView;
        if (activeView == null) return Err("No active view");

        // Group elements by parameter display value; remember the raw value per group for sorting.
        var groups = new Dictionary<string, List<Element>>();
        var rawByDisplay = new Dictionary<string, object?>();
        foreach (var elem in elements)
        {
            var (raw, display) = ParamSortValue(elem, parameterName!);
            if (!groups.TryGetValue(display, out var list)) groups[display] = list = new List<Element>();
            list.Add(elem);
            if (!rawByDisplay.ContainsKey(display)) rawByDisplay[display] = raw;
        }

        var uniqueValues = groups.Keys.ToList();
        uniqueValues.Sort((a, b) =>
        {
            var ka = SplashSortKey(a, rawByDisplay[a]);
            var kb = SplashSortKey(b, rawByDisplay[b]);
            int c = ka.Rank.CompareTo(kb.Rank);
            if (c != 0) return c;
            c = ka.Num.CompareTo(kb.Num);
            return c != 0 ? c : string.CompareOrdinal(ka.Str, kb.Str);
        });
        int valueCount = uniqueValues.Count;

        // Numeric-gradient mode: gradient requested on a parameter that actually carries numbers.
        // GEN-06: this used to sniff the parameter's NAME for hardcoded English/French substrings
        // ("length", "longueur", "area", …) — a leftover from the ported IronPython source, and the only French
        // string in the codebase. That mis-classified in both directions: a numeric Depth/Elevation/Offset/
        // Diameter got flat categorical colours, a TEXT parameter called "Area Code" went into gradient mode
        // where every value landed at position 0.5 (one uniform colour for the whole category), and on a
        // non-English/French Revit the feature never engaged at all. The parameter's real type is already in
        // hand — rawByDisplay holds the raw value ParamSortValue read from p.StorageType — so use it.
        bool numericGradient = useGradient && uniqueValues.Any(v => rawByDisplay[v] is double or int);

        var positions = new Dictionary<string, double>();
        if (numericGradient)
        {
            var numeric = uniqueValues
                .Where(v => rawByDisplay[v] is double or int)
                .Select(v => Convert.ToDouble(rawByDisplay[v], CultureInfo.InvariantCulture))
                .ToList();
            if (numeric.Count > 0)
            {
                double min = numeric.Min(), max = numeric.Max();
                foreach (var v in uniqueValues)
                    positions[v] = rawByDisplay[v] is double or int && max != min
                        ? (Convert.ToDouble(rawByDisplay[v], CultureInfo.InvariantCulture) - min) / (max - min)
                        : 0.5;
            }
            else
            {
                for (int i = 0; i < valueCount; i++)
                    positions[uniqueValues[i]] = (double)i / Math.Max(1, valueCount - 1);
            }
        }

        // Colour palette in sorted-value order.
        List<Color> colors;
        if (customColors.Count > 0)
        {
            colors = customColors.Take(valueCount).Select(HexToColor).ToList();
            if (colors.Count < valueCount) colors.AddRange(DistinctColors(valueCount - colors.Count));
        }
        else if (numericGradient)
        {
            colors = uniqueValues.Select(v => LerpGradientColor(positions.TryGetValue(v, out var p) ? p : 0.5)).ToList();
        }
        else if (useGradient)
        {
            colors = GradientColors(valueCount);
        }
        else
        {
            colors = DistinctColors(valueCount);
        }

        var solidFillId = SolidFillPatternId(doc);
        // UI-05: this used to unconditionally write the overrides into EVERY non-template view sharing the active
        // view's ViewType — one call in a floor plan repainted all 40 floor plans — while reporting only
        // elements_colored, so neither Claude nor the user learned the blast radius. Now opt-in, and the response
        // states how many views were written either way.
        var otherViews = BoolArg(args, "apply_to_similar_views") ? SameTypeViews(doc, activeView) : new List<View>();

        return InTransaction(doc, "Color Elements by Parameter", () =>
        {
            var colorAssignments = new Dictionary<string, object?>();
            int elementsColored = 0;

            for (int i = 0; i < uniqueValues.Count; i++)
            {
                var value = uniqueValues[i];
                var groupElements = groups[value];
                var color = i < colors.Count ? colors[i] : new Color(255, 0, 0);

                colorAssignments[value] = new Dictionary<string, object?>
                {
                    ["color"] = ColorToHex(color),
                    ["element_count"] = groupElements.Count,
                    ["sort_index"] = i,
                };

                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(color);
                ogs.SetSurfaceForegroundPatternColor(color);
                ogs.SetCutForegroundPatternColor(color);
                ogs.SetCutLineColor(color);
                ogs.SetProjectionLineWeight(3);
                if (solidFillId != null)
                {
                    ogs.SetSurfaceForegroundPatternId(solidFillId);
                    ogs.SetCutForegroundPatternId(solidFillId);
                }

                foreach (var elem in groupElements)
                {
                    try
                    {
                        activeView.SetElementOverrides(elem.Id, ogs);
                        elementsColored++;
                        foreach (var view in otherViews)
                        {
                            try { view.SetElementOverrides(elem.Id, ogs); }
                            catch { /* don't fail if another view rejects overrides */ }
                        }
                    }
                    catch { /* skip element */ }
                }
            }

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["message"] = $"Successfully colored {elementsColored} elements in {valueCount} color groups, " +
                              (otherViews.Count == 0
                                  ? "in the active view only"
                                  : $"across {otherViews.Count + 1} views of type {activeView.ViewType}"),
                ["category"] = categoryName,
                ["parameter"] = parameterName,
                ["color_assignments"] = colorAssignments,
                ["statistics"] = new Dictionary<string, object?>
                {
                    ["total_elements"] = elements.Count,
                    ["elements_colored"] = elementsColored,
                    ["views_colored"] = otherViews.Count + 1,
                    ["unique_parameter_values"] = valueCount,
                    ["use_gradient"] = useGradient,
                    ["sorted_values"] = uniqueValues.Cast<object?>().ToList(),
                },
            };
            var viewWarning = ViewColorWarning(activeView);
            if (viewWarning != null) result["view_warning"] = viewWarning;
            return Json(result);
        });
    }

    // ==================== clear_colors ====================

    /// <summary>Removes colour overrides from a category's elements in the active view (+ same-type views).</summary>
    internal static string ClearColors(UIApplication app, Document doc, JsonElement args)
    {
        var categoryName = StrArg(args, "category_name");
        if (string.IsNullOrEmpty(categoryName)) return Err("category_name is required");

        var category = CategoryByDisplayName(doc, categoryName!);
        if (category == null) return Err($"Category '{categoryName}' not found");

        var elements = new FilteredElementCollector(doc).OfCategoryId(category.Id)
            .WhereElementIsNotElementType().ToElements();
        if (elements.Count == 0)
            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "warning",
                ["message"] = $"No elements found in category '{categoryName}'",
            });

        var activeView = doc.ActiveView;
        if (activeView == null) return Err("No active view");
        // UI-05: symmetric with color_splash — active view only unless the caller opts in. (Recovering a paint job
        // made by an older build, which always wrote to every same-type view, needs apply_to_similar_views: true.)
        var otherViews = BoolArg(args, "apply_to_similar_views") ? SameTypeViews(doc, activeView) : new List<View>();

        return InTransaction(doc, "Clear Element Colors", () =>
        {
            var empty = new OverrideGraphicSettings();
            int cleared = 0;
            foreach (var elem in elements)
            {
                try
                {
                    activeView.SetElementOverrides(elem.Id, empty);
                    cleared++;
                    foreach (var view in otherViews)
                    {
                        try { view.SetElementOverrides(elem.Id, empty); }
                        catch { /* don't fail on other views */ }
                    }
                }
                catch { /* skip element */ }
            }

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "success",
                ["message"] = $"Successfully cleared color overrides for {cleared} elements, " +
                              (otherViews.Count == 0
                                  ? "in the active view only"
                                  : $"across {otherViews.Count + 1} views of type {activeView.ViewType}"),
                ["category"] = categoryName,
                ["elements_processed"] = cleared,
                ["views_cleared"] = otherViews.Count + 1,
            });
        });
    }

    // ==================== shared element-tool helpers ====================

    /// <summary>Bounding-box centre in mm (rounded to 0.1), or null when the element has no box.</summary>
    private static Dictionary<string, object?>? BboxCenterMm(Element elem)
    {
        try
        {
            var bb = elem.get_BoundingBox(null);
            if (bb == null) return null;
            return new Dictionary<string, object?>
            {
                ["x"] = Math.Round(FtToMm((bb.Min.X + bb.Max.X) / 2.0), 1),
                ["y"] = Math.Round(FtToMm((bb.Min.Y + bb.Max.Y) / 2.0), 1),
                ["z"] = Math.Round(FtToMm((bb.Min.Z + bb.Max.Z) / 2.0), 1),
            };
        }
        catch { return null; }
    }

    /// <summary>Category from Document.Settings.Categories. GEN-05: accepts the display name ("Walls") as before,
    /// and now also every other dialect the sibling tools take (OST_Walls / ost_walls / walls / friendly alias),
    /// so one vocabulary works across the whole surface.</summary>
    private static Category? CategoryByDisplayName(Document doc, string name)
    {
        try
        {
            foreach (Category cat in doc.Settings.Categories)
                if (string.Equals(cat.Name, name, StringComparison.OrdinalIgnoreCase)) return cat;
        }
        catch { /* fall through */ }

        var bic = ResolveCategoryName(name);
        if (bic != null)
        {
            try { return Category.GetCategory(doc, bic.Value); } catch { /* not in this document */ }
        }
        return null;
    }

    /// <summary>Reads an array of strings (arg <paramref name="prop"/>) into a list; missing → empty.</summary>
    private static List<string> StringListArg(JsonElement args, string prop)
    {
        var result = new List<string>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(prop, out var arr)
            && arr.ValueKind == JsonValueKind.Array)
            foreach (var el in arr.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
                    result.Add(s);
        return result;
    }

    /// <summary>JSON scalar to its string form (Python str()-alike: True/False capitalised).</summary>
    private static string JsonValueString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => v.GetRawText(),
    };

    /// <summary>
    ///     Sets a parameter from a JSON value by storage type. Double values follow the wire contract —
    ///     lengths in mm, areas in m², volumes in m³ — and are converted to internal ft/ft²/ft³ here;
    ///     other Double specs (angle, number, …) pass through unconverted. Checks the Set() return value:
    ///     Revit can refuse a write without throwing. Returns null on success, else a short failure reason.
    /// </summary>
    private static string? SetParamFromJson(Parameter param, JsonElement value)
    {
        switch (param.StorageType)
        {
            case StorageType.String:
                return param.Set(JsonValueString(value)) ? null : "error: Revit refused the write";
            case StorageType.Integer:
                int iv;
                if (value.ValueKind == JsonValueKind.Number) iv = (int)Math.Round(value.GetDouble());
                else if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) iv = value.GetBoolean() ? 1 : 0;
                else if (!int.TryParse(JsonValueString(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out iv))
                    return "error: value is not an integer";
                return param.Set(iv) ? null : "error: Revit refused the write";
            case StorageType.Double:
                double dv;
                if (value.ValueKind == JsonValueKind.Number) dv = value.GetDouble();
                else if (!double.TryParse(JsonValueString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out dv))
                    return "error: value is not a number";
                return param.Set(WireToInternal(param, dv)) ? null : "error: Revit refused the write";
            case StorageType.ElementId:
                if (!TryGetLong(value, out long idv)) return "error: value is not an element id";
                return param.Set(new ElementId(idv)) ? null : "error: Revit refused the write";
            default:
                return "unsupported type";
        }
    }

    /// <summary>Wire→internal for Double params: mm→ft (length), m²→ft² (area), m³→ft³ (volume).</summary>
    private static double WireToInternal(Parameter param, double wire)
    {
        try
        {
            var spec = param.Definition?.GetDataType();
            if (spec == null) return wire;
            if (spec.Equals(SpecTypeId.Length)) return MmToFt(wire);
            if (spec.Equals(SpecTypeId.Area)) return wire / SqFtToSqM;
            if (spec.Equals(SpecTypeId.Volume)) return wire / CuFtToCuM;
        }
        catch { /* params without a measurable spec write raw */ }
        return wire;
    }

    /// <summary>Internal→wire for Double params, mirror of <see cref="WireToInternal"/>.</summary>
    private static double InternalToWire(Parameter param, double internalValue)
    {
        try
        {
            var spec = param.Definition?.GetDataType();
            if (spec == null) return internalValue;
            if (spec.Equals(SpecTypeId.Length)) return FtToMm(internalValue);
            if (spec.Equals(SpecTypeId.Area)) return internalValue * SqFtToSqM;
            if (spec.Equals(SpecTypeId.Volume)) return internalValue * CuFtToCuM;
        }
        catch { /* fall through to raw */ }
        return internalValue;
    }

    /// <summary>Non-display value string used as old_value by modify_element. Double values are reported
    /// in wire units (mm/m²/m³ for length/area/volume specs) so old_value is comparable to new_value.</summary>
    private static string ParamRawValueString(Parameter param)
    {
        try
        {
            return param.StorageType switch
            {
                StorageType.String => param.AsString() ?? "",
                StorageType.Integer => param.AsInteger().ToString(CultureInfo.InvariantCulture),
                StorageType.Double => Math.Round(InternalToWire(param, param.AsDouble()), 6).ToString(CultureInfo.InvariantCulture),
                StorageType.ElementId => (param.AsElementId()?.Value ?? -1).ToString(CultureInfo.InvariantCulture),
                _ => "",
            };
        }
        catch { return ""; }
    }

    /// <summary>Display-friendly parameter value (AsValueString / AsString / referenced element name).</summary>
    private static string ParamDisplayValue(Document doc, Parameter param)
    {
        try
        {
            if (!param.HasValue) return "";
            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString() ?? "";
                case StorageType.Integer:
                    return param.AsValueString() ?? param.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.Double:
                    return param.AsValueString()
                           ?? Math.Round(param.AsDouble(), 6).ToString(CultureInfo.InvariantCulture);
                case StorageType.ElementId:
                    var eid = param.AsElementId();
                    if (eid != null && eid != ElementId.InvalidElementId && doc.GetElement(eid) is { } el)
                        return ElemName(el);
                    return "";
                default:
                    return "";
            }
        }
        catch { return ""; }
    }

    /// <summary>Parameter group label via GetGroupTypeId/LabelUtils, or "Other".</summary>
    private static string ParamGroupLabel(Parameter param)
    {
        try { return LabelUtils.GetLabelForGroup(param.Definition.GetGroupTypeId()); }
        catch { return "Other"; }
    }

    /// <summary>
    ///     (raw, display) pair for colour-splash grouping/sorting: instance parameter first, then type.
    ///     Doubles/ints keep their raw value for numeric sorting; Yes/No integers become "True"/"False";
    ///     missing values are ("None","None").
    /// </summary>
    private static (object? Raw, string Display) ParamSortValue(Element elem, string parameterName)
    {
        try
        {
            var p = elem.LookupParameter(parameterName);
            if (p == null)
            {
                var typeId = elem.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                    p = elem.Document.GetElement(typeId)?.LookupParameter(parameterName);
            }
            if (p == null || !p.HasValue) return ("None", "None");

            switch (p.StorageType)
            {
                case StorageType.Double:
                    double d = p.AsDouble();
                    return (d, p.AsValueString() ?? d.ToString(CultureInfo.InvariantCulture));
                case StorageType.Integer:
                    try
                    {
                        if (p.Definition.GetDataType() == SpecTypeId.Boolean.YesNo)
                        {
                            var bv = p.AsInteger() == 1 ? "True" : "False";
                            return (bv, bv);
                        }
                    }
                    catch { /* not a yes/no */ }
                    int i = p.AsInteger();
                    return (i, p.AsValueString() ?? i.ToString(CultureInfo.InvariantCulture));
                case StorageType.String:
                    var s = p.AsString();
                    return string.IsNullOrEmpty(s) ? ("None", "None") : (s, s!);
                case StorageType.ElementId:
                    var eid = p.AsElementId();
                    if (eid != null && eid != ElementId.InvalidElementId
                        && elem.Document.GetElement(eid) is { } target)
                    {
                        var n = ElemName(target);
                        if (n.Length > 0) return (n, n);
                    }
                    return ("None", "None");
                default:
                    var vs = p.AsValueString() ?? "None";
                    return (vs, vs);
            }
        }
        catch { return ("None", "None"); }
    }

    /// <summary>Type-aware sort key: numerics first (ascending), then booleans/strings, "None" last.</summary>
    private static (double Rank, double Num, string Str) SplashSortKey(string display, object? raw)
    {
        if (display == "None") return (2, 0, "");
        if (display is "True" or "False") return (1, display == "False" ? 0 : 1, "");
        if (raw is double d) return (0, d, "");
        if (raw is int i) return (0, i, "");
        var parsed = NumericPrefix(display);
        if (parsed != null) return (0, parsed.Value, "");
        return (1.5, 0, display.ToLowerInvariant());
    }

    /// <summary>Leading numeric part of a value with a unit suffix ("300 mm" → 300), or null.</summary>
    private static double? NumericPrefix(string s)
    {
        var t = s.Trim();
        int end = t.Length;
        while (end > 0 && !(char.IsDigit(t[end - 1]) || t[end - 1] is '.' or '-' or '+')) end--;
        if (end == 0) return null;
        return double.TryParse(t.Substring(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : null;
    }

    /// <summary>Visually distinct palette: 25 preset colours, then brightness-reduced cycles.</summary>
    private static List<Color> DistinctColors(int count)
    {
        var baseColors = new (int R, int G, int B)[]
        {
            (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0), (255, 0, 255),
            (0, 255, 255), (255, 128, 0), (128, 0, 255), (255, 128, 128), (128, 255, 128),
            (128, 128, 255), (255, 255, 128), (128, 0, 0), (0, 128, 0), (0, 0, 128),
            (128, 128, 0), (128, 0, 128), (0, 128, 128), (192, 192, 192), (128, 128, 128),
            (255, 192, 203), (255, 165, 0), (255, 20, 147), (50, 205, 50), (30, 144, 255),
        };
        var colors = new List<Color>(count);
        for (int i = 0; i < count; i++)
        {
            var (r, g, b) = baseColors[i % baseColors.Length];
            int cycle = i / baseColors.Length;
            double factor = Math.Max(0.3, 1.0 - cycle * 0.15);
            colors.Add(new Color((byte)(r * factor), (byte)(g * factor), (byte)(b * factor)));
        }
        return colors;
    }

    /// <summary>Blue→red gradient palette of the given size.</summary>
    private static List<Color> GradientColors(int count)
    {
        if (count <= 1) return new List<Color> { new(255, 0, 0) };
        var colors = new List<Color>(count);
        for (int i = 0; i < count; i++)
            colors.Add(LerpGradientColor((double)i / (count - 1)));
        return colors;
    }

    /// <summary>Blue→green→red interpolation for a 0..1 gradient position.</summary>
    private static Color LerpGradientColor(double position)
    {
        position = Math.Max(0.0, Math.Min(1.0, position));
        var red = (byte)(255 * position);
        var green = (byte)(255 * (1 - Math.Abs(2 * position - 1)));
        var blue = (byte)(255 * (1 - position));
        return new Color(red, green, blue);
    }

    /// <summary>"#RRGGBB" (or "RRGGBB") to a Color; invalid input falls back to red.</summary>
    private static Color HexToColor(string hex)
    {
        try
        {
            var h = hex.TrimStart('#');
            return new Color(
                Convert.ToByte(h.Substring(0, 2), 16),
                Convert.ToByte(h.Substring(2, 2), 16),
                Convert.ToByte(h.Substring(4, 2), 16));
        }
        catch { return new Color(255, 0, 0); }
    }

    private static string ColorToHex(Color c) => $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}";

    /// <summary>The document's solid fill pattern id, or null when none exists.</summary>
    private static ElementId? SolidFillPatternId(Document doc)
    {
        try
        {
            foreach (var pe in new FilteredElementCollector(doc)
                         .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
                if (pe.GetFillPattern().IsSolidFill) return pe.Id;
        }
        catch { /* fall through */ }
        return null;
    }

    /// <summary>Non-template views sharing the active view's ViewType (excluding the active view itself).</summary>
    private static List<View> SameTypeViews(Document doc, View active)
    {
        var result = new List<View>();
        try
        {
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                if (!v.IsTemplate && v.ViewType == active.ViewType && v.Id != active.Id)
                    result.Add(v);
        }
        catch { /* leave partial */ }
        return result;
    }

    /// <summary>Warning payload when the active view's visual style hides colour overrides, else null.</summary>
    private static Dictionary<string, object?>? ViewColorWarning(View view)
    {
        try
        {
            var p = view.get_Parameter(BuiltInParameter.MODEL_GRAPHICS_STYLE);
            if (p == null) return null;
            var styleId = p.AsInteger();
            if (styleId is 0 or 4)
                return new Dictionary<string, object?>
                {
                    ["warning"] = "Current visual style may not show colors well",
                    ["recommendation"] = "Try switching to 'Hidden Line' or 'Shaded' visual style",
                    ["current_style_id"] = styleId,
                };
        }
        catch { /* no warning */ }
        return null;
    }

    /// <summary>
    ///     GEN-05: THE category resolver for this file. Six tools here take a category argument, and they used to
    ///     speak four incompatible dialects with three different unknown-name behaviours: case-SENSITIVE
    ///     <c>OST_Walls</c> (ai_element_filter, erroring; get_material_quantities, silently dropping),
    ///     case-insensitive enum + friendly aliases (check_clashes), and display name only (color_splash,
    ///     clear_colors, list_category_parameters). So "Walls" worked in three tools and failed in two, "ost_walls"
    ///     worked in one, and a typo was ignored by exactly one. For a surface whose only caller is a model
    ///     inferring usage from descriptions, that is a per-tool trial-and-error tax.
    ///     <para>Accepts, for every tool: <c>OST_Walls</c>, <c>ost_walls</c>, <c>Walls</c>, <c>walls</c>,
    ///     <c>Structural Framing</c>, and the friendly aliases (<c>ducts</c>, <c>beams</c>).</para>
    /// </summary>
    private static BuiltInCategory? ResolveCategoryName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return null;
        if (!n.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
            && ClashFriendlyNames.TryGetValue(n.ToLowerInvariant(), out var mapped))
            n = mapped;
        // FindBuiltInCategory (BridgeToolsShared) already handles OST_-prefixing, space stripping,
        // case-insensitive enum parsing and a LabelUtils display-label fallback. It existed in the shared file
        // every one of these tools includes, and had exactly one caller.
        return FindBuiltInCategory(n);
    }

    /// <summary>Resolves a list of category names to (valid, unknown) through <see cref="ResolveCategoryName"/>.</summary>
    private static (List<BuiltInCategory> Bics, List<string> Unknown) ResolveClashCategories(IEnumerable<string> names)
    {
        var bics = new List<BuiltInCategory>();
        var unknown = new List<string>();
        foreach (var raw in names)
        {
            var bic = ResolveCategoryName(raw);
            if (bic != null) bics.Add(bic.Value);
            else unknown.Add(raw);
        }
        return (bics, unknown);
    }
}
