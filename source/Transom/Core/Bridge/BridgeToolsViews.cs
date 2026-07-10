using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Core;

// ============================================================================================
//  extended ports — VIEW / ANNOTATION tools.
//  C# ports of the IronPython pyRevit Routes handlers in revit-mcp-server.extension/revit_mcp/
//  (views.py, view_management.py, documentation.py, detail.py, tags.py, annotation.py).
//  Wire contract: all lengths/points cross in MILLIMETERS; responses mirror the Python field
//  names; failures are Err(...) — handlers never throw and never show UI. Every model mutation
//  runs through InTransaction (warning-suppressed, rolls back on ok:false); reads and exports
//  use no transaction.
// ============================================================================================
public static partial class BridgeTools
{
    // --- get_revit_view ------------------------------------------------------

    /// <summary>
    ///     Exports a named view as a PNG (1024 px, DPI 150, fit-to-page) and returns it base64-encoded.
    ///     Port of views.py get_view. ExportImage needs no transaction; the temp file is always cleaned up.
    /// </summary>
    internal static string GetRevitView(UIApplication app, Document doc, JsonElement args)
    {
        var name = StrArg(args, "view_name");
        if (string.IsNullOrEmpty(name)) return Err("view_name is required");

        var view = FindView(doc, name);
        if (view == null) return Err($"View '{name}' not found");
        if (view.IsTemplate) return Err("Cannot export view templates");
        if (view.ViewType is ViewType.Internal or ViewType.ProjectBrowser) return Err("Cannot export internal views");

        // Unique temp dir per call: Revit appends " - <type> - <name>" to the file prefix, so the
        // only reliable way to find the output is to own the whole directory.
        var dir = Path.Combine(Path.GetTempPath(), "RevitMCPExports", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var ieo = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(dir, "export"),
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = 1024,
            };
            ieo.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(ieo);

            var file = Directory.GetFiles(dir, "*.png")
                .OrderByDescending(File.GetCreationTimeUtc).FirstOrDefault();
            if (file == null) return Err("Export failed - no image file was created");

            var bytes = File.ReadAllBytes(file);
            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["view_name"] = ElemName(view),
                ["image_base64"] = Convert.ToBase64String(bytes),
                ["format"] = "png",
            });
        }
        catch (Exception ex) { return Err("Failed to export view: " + ex.Message); }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    // --- list_revit_views ----------------------------------------------------

    /// <summary>
    ///     Lists all exportable (non-template, non-internal) views grouped by type. Port of views.py
    ///     list_views: same buckets (floor_plans/ceiling_plans/elevations/sections/3d_views/
    ///     drafting_views/schedules/other), alphabetically sorted, plus total_exportable_views.
    /// </summary>
    internal static string ListRevitViews(UIApplication app, Document doc, JsonElement args)
    {
        try
        {
            var buckets = new Dictionary<string, List<string>>
            {
                ["floor_plans"] = new(), ["ceiling_plans"] = new(), ["elevations"] = new(),
                ["sections"] = new(), ["3d_views"] = new(), ["drafting_views"] = new(),
                ["schedules"] = new(), ["other"] = new(),
            };

            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                try
                {
                    if (v.IsTemplate || v.ViewType is ViewType.Internal or ViewType.ProjectBrowser) continue;
                    var key = v.ViewType switch
                    {
                        ViewType.FloorPlan => "floor_plans",
                        ViewType.CeilingPlan => "ceiling_plans",
                        ViewType.Elevation => "elevations",
                        ViewType.Section => "sections",
                        ViewType.ThreeD => "3d_views",
                        ViewType.DraftingView => "drafting_views",
                        ViewType.Schedule => "schedules",
                        _ => "other",
                    };
                    buckets[key].Add(ElemName(v));
                }
                catch { /* skip views that reject property access */ }
            }

            var byType = new Dictionary<string, object?>();
            foreach (var (key, list) in buckets)
                byType[key] = list.OrderBy(n => n, StringComparer.Ordinal).Cast<object?>().ToList();

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["views_by_type"] = byType,
                ["total_exportable_views"] = buckets.Values.Sum(l => l.Count),
            });
        }
        catch (Exception ex) { return Err("Failed to list views: " + ex.Message); }
    }

    // --- get_current_view_info -------------------------------------------------

    /// <summary>
    ///     Detailed info about the active view (name/type/id/template/scale/crop box/family type/
    ///     detail level/discipline). Port of views.py get_current_view_info — same view_info shape.
    /// </summary>
    internal static string GetCurrentViewInfo(UIApplication app, Document doc, JsonElement args)
    {
        var view = app.ActiveUIDocument?.ActiveView;
        if (view == null) return Err("No active view found");

        var info = new Dictionary<string, object?>
        {
            ["view_name"] = ElemName(view),
            ["view_type"] = view.ViewType.ToString(),
            ["view_id"] = view.Id.Value,
            ["is_template"] = view.IsTemplate,
        };
        try { info["scale"] = view.Scale; } catch { info["scale"] = null; }
        try { info["crop_box_active"] = view.CropBoxActive; } catch { info["crop_box_active"] = false; }
        try { info["view_family_type"] = doc.GetElement(view.GetTypeId()) is { } vft ? ElemName(vft) : "Unknown"; }
        catch { info["view_family_type"] = "Unknown"; }
        try { info["detail_level"] = view.DetailLevel.ToString(); } catch { info["detail_level"] = "Unknown"; }
        try { info["discipline"] = view.Discipline.ToString(); } catch { info["discipline"] = "Unknown"; }

        return Json(new Dictionary<string, object?> { ["ok"] = true, ["view_info"] = info });
    }

    // --- get_current_view_elements ----------------------------------------------

    /// <summary>
    ///     Every element visible in the active view with id/name/type/category/level/location, grouped
    ///     by category with per-category counts. Port of views.py get_current_view_elements; unlike the
    ///     Python (which leaked internal feet), location coordinates are reported in mm per the wire contract.
    /// </summary>
    internal static string GetCurrentViewElements(UIApplication app, Document doc, JsonElement args)
    {
        var view = app.ActiveUIDocument?.ActiveView;
        if (view == null) return Err("No active view found");

        try
        {
            var elements = new List<Dictionary<string, object?>>();
            foreach (var el in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                try
                {
                    var info = new Dictionary<string, object?>
                    {
                        ["element_id"] = el.Id.Value,
                        ["name"] = ElemName(el),
                        ["element_type"] = el.GetType().Name,
                        ["category"] = el.Category?.Name ?? "Unknown",
                        ["category_id"] = el.Category?.Id.Value,
                    };

                    try
                    {
                        var lp = el.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                        var levelId = lp?.AsElementId();
                        if (levelId != null && levelId != ElementId.InvalidElementId
                            && doc.GetElement(levelId) is { } lvl)
                        {
                            info["level"] = ElemName(lvl);
                            info["level_id"] = levelId.Value;
                        }
                        else { info["level"] = null; info["level_id"] = null; }
                    }
                    catch { info["level"] = null; info["level_id"] = null; }

                    try
                    {
                        info["location"] = el.Location switch
                        {
                            LocationPoint lpt => new Dictionary<string, object?>
                            {
                                ["type"] = "point",
                                ["x"] = FtToMm(lpt.Point.X), ["y"] = FtToMm(lpt.Point.Y), ["z"] = FtToMm(lpt.Point.Z),
                            },
                            LocationCurve lc => new Dictionary<string, object?>
                            {
                                ["type"] = "curve",
                                ["start"] = MmPoint(lc.Curve.GetEndPoint(0)),
                                ["end"] = MmPoint(lc.Curve.GetEndPoint(1)),
                            },
                            _ => new Dictionary<string, object?> { ["type"] = "unknown" },
                        };
                    }
                    catch { info["location"] = new Dictionary<string, object?> { ["type"] = "unknown" }; }

                    elements.Add(info);
                }
                catch { /* skip elements that reject property access */ }
            }

            var byCategory = new Dictionary<string, object?>();
            var counts = new Dictionary<string, object?>();
            foreach (var group in elements.GroupBy(e => (string)e["category"]!))
            {
                byCategory[group.Key] = group.Cast<object?>().ToList();
                counts[group.Key] = group.Count();
            }

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["view_name"] = ElemName(view),
                ["view_id"] = view.Id.Value,
                ["total_elements"] = elements.Count,
                ["elements"] = elements.Cast<object?>().ToList(),
                ["elements_by_category"] = byCategory,
                ["category_counts"] = counts,
            });
        }
        catch (Exception ex) { return Err("Failed to get current view elements: " + ex.Message); }
    }

    // --- create_view ---------------------------------------------------------

    /// <summary>
    ///     Creates a floor_plan / ceiling_plan / section / elevation / 3d view. Port of
    ///     view_management.py create_view: plans need level_name; sections build a BoundingBoxXYZ +
    ///     Transform from origin/direction/up (mm) with width/height/depth (mm, default 10000);
    ///     elevations host an ElevationMarker on a plan view; 3d is an isometric View3D.
    /// </summary>
    internal static string CreateView(UIApplication app, Document doc, JsonElement args)
    {
        var viewType = StrArg(args, "view_type");
        var name = StrArg(args, "name");
        if (string.IsNullOrEmpty(viewType))
            return Err("view_type is required (floor_plan, ceiling_plan, section, elevation, 3d)");
        if (string.IsNullOrEmpty(name)) return Err("name is required");

        return InTransaction(doc, "Create View via MCP", () =>
        {
            View? newView;
            switch (viewType)
            {
                case "floor_plan":
                case "ceiling_plan":
                {
                    var levelName = StrArg(args, "level_name");
                    if (string.IsNullOrEmpty(levelName))
                        return Err($"level_name is required for {viewType} views");
                    var level = FindLevel(doc, levelName);
                    if (level == null) return Err($"Level '{levelName}' not found");

                    var family = viewType == "floor_plan" ? ViewFamily.FloorPlan : ViewFamily.CeilingPlan;
                    var vft = ViewFamilyTypeOf(doc, family);
                    if (vft == null) return Err($"No {viewType} view family type found in project");
                    newView = ViewPlan.Create(doc, vft.Id, level.Id);
                    break;
                }
                case "section":
                {
                    if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("section_box", out var box)
                        || box.ValueKind != JsonValueKind.Object)
                        return Err("section_box is required for section views");
                    var vft = ViewFamilyTypeOf(doc, ViewFamily.Section);
                    if (vft == null) return Err("No section view family type found");

                    var origin = box.TryGetProperty("origin", out var o) && o.ValueKind == JsonValueKind.Object
                        ? PointOf(o) : XYZ.Zero;
                    // direction/up are unit vectors — no mm conversion (matches the Python).
                    var dir = UnitVecOf(box, "direction", new XYZ(0, 1, 0));
                    var up = UnitVecOf(box, "up", new XYZ(0, 0, 1));
                    var width = MmToFt(DblOf(box, "width", 10000));
                    var height = MmToFt(DblOf(box, "height", 10000));
                    var depth = MmToFt(DblOf(box, "depth", 10000));

                    var right = dir.CrossProduct(up).Normalize();
                    var tf = Transform.Identity;
                    tf.Origin = origin;
                    tf.BasisX = right;
                    tf.BasisY = up;
                    tf.BasisZ = dir;

                    var bb = new BoundingBoxXYZ
                    {
                        Transform = tf,
                        Min = new XYZ(-width / 2.0, -height / 2.0, 0),
                        Max = new XYZ(width / 2.0, height / 2.0, depth),
                    };
                    newView = ViewSection.CreateSection(doc, vft.Id, bb);
                    break;
                }
                case "3d":
                {
                    var vft = ViewFamilyTypeOf(doc, ViewFamily.ThreeDimensional);
                    if (vft == null) return Err("No 3D view family type found");
                    newView = View3D.CreateIsometric(doc, vft.Id);
                    break;
                }
                case "elevation":
                {
                    var vft = ViewFamilyTypeOf(doc, ViewFamily.Elevation);
                    if (vft == null) return Err("No elevation view family type found");

                    var levelName = StrArg(args, "level_name");
                    var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                        .Where(p => p.ViewType == ViewType.FloorPlan && !p.IsTemplate).ToList();
                    ViewPlan? host = null;
                    if (!string.IsNullOrEmpty(levelName))
                        host = plans.FirstOrDefault(p => p.GenLevel != null
                            && string.Equals(ElemName(p.GenLevel), levelName, StringComparison.OrdinalIgnoreCase));
                    if (host == null && app.ActiveUIDocument?.ActiveView is ViewPlan active
                        && active.ViewType == ViewType.FloorPlan) host = active;
                    host ??= plans.FirstOrDefault();
                    if (host == null) return Err("No floor plan view found to host the elevation marker");

                    var marker = ElevationMarker.CreateElevationMarker(doc, vft.Id, XYZ.Zero, 1);
                    newView = marker.CreateElevation(doc, host.Id, 0);
                    break;
                }
                default:
                    return Err($"Unsupported view_type '{viewType}'. Use: floor_plan, ceiling_plan, section, elevation, 3d");
            }

            if (newView == null) return Err("view creation returned null");
            try { newView.Name = name!; }
            catch (Exception ex) { return Err($"Could not name view '{name}' (duplicate name?): {ex.Message}"); }

            string actualType;
            try { actualType = newView.ViewType.ToString(); } catch { actualType = viewType!; }

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["view_id"] = newView.Id.Value,
                ["name"] = name,
                ["view_type"] = actualType,
                ["message"] = $"Created {viewType} view '{name}'",
            });
        });
    }

    // --- set_active_view -------------------------------------------------------

    /// <summary>
    ///     Switches the Revit UI to a named view. Port of view_management.py set_active_view.
    ///     No transaction — UIDocument.ActiveView must be set outside one.
    /// </summary>
    internal static string SetActiveView(UIApplication app, Document doc, JsonElement args)
    {
        var name = StrArg(args, "view_name");
        if (string.IsNullOrEmpty(name)) return Err("view_name is required");

        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) return Err("no active document");

        var view = FindView(doc, name);
        if (view == null) return Err($"View '{name}' not found");

        try { uidoc.ActiveView = view; }
        catch (Exception ex) { return Err($"Could not activate view '{name}': {ex.Message}"); }

        return Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["view_id"] = view.Id.Value,
            ["name"] = name,
            ["view_type"] = view.ViewType.ToString(),
            ["message"] = $"Switched to view '{name}'",
        });
    }

    // --- create_sheet ----------------------------------------------------------

    /// <summary>
    ///     Creates a drawing sheet from a title block (named or first available), setting sheet number
    ///     and name. Port of documentation.py create_sheet — duplicate sheet numbers are rejected up front.
    /// </summary>
    internal static string CreateSheet(UIApplication app, Document doc, JsonElement args)
    {
        var sheetNumber = StrArg(args, "sheet_number");
        var sheetName = StrArg(args, "sheet_name") ?? "Unnamed Sheet";
        var titleBlockName = StrArg(args, "title_block_name");

        var titleBlocks = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>().ToList();
        if (titleBlocks.Count == 0)
            return Err("No title block families found — load a title block family into the project");

        var tb = !string.IsNullOrEmpty(titleBlockName)
            ? titleBlocks.FirstOrDefault(t =>
                string.Equals(ElemName(t), titleBlockName, StringComparison.OrdinalIgnoreCase)) ?? titleBlocks[0]
            : titleBlocks[0];

        if (!string.IsNullOrEmpty(sheetNumber)
            && new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Any(s => string.Equals(s.SheetNumber, sheetNumber, StringComparison.OrdinalIgnoreCase)))
            return Err($"Sheet number '{sheetNumber}' already exists in the project");

        return InTransaction(doc, "Create Sheet via MCP", () =>
        {
            if (!tb.IsActive) { tb.Activate(); doc.Regenerate(); }
            var sheet = ViewSheet.Create(doc, tb.Id);
            if (!string.IsNullOrEmpty(sheetNumber)) sheet.SheetNumber = sheetNumber!;
            sheet.Name = sheetName;

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["created"] = new Dictionary<string, object?>
                {
                    ["id"] = sheet.Id.Value,
                    ["sheet_number"] = sheet.SheetNumber,
                    ["sheet_name"] = sheet.Name,
                    ["title_block"] = ElemName(tb),
                },
                ["message"] = $"Created sheet {sheet.SheetNumber} - {sheet.Name}",
            });
        });
    }

    // --- export_document ---------------------------------------------------------

    /// <summary>
    ///     Exports a view (named, else active) to pdf / png / jpg / dwg under Documents\RevitMCPExport.
    ///     Port of documentation.py export_document. Runs without a transaction (exports don't mutate the
    ///     model); the output path is located by scanning for the freshly written file, since Revit
    ///     decorates image file names.
    /// </summary>
    internal static string ExportDocument(UIApplication app, Document doc, JsonElement args)
    {
        var viewName = StrArg(args, "view_name");
        var fmt = (StrArg(args, "format") ?? "pdf").ToLowerInvariant();
        var resolution = IntArg(args, "resolution", 300);

        if (fmt is not ("pdf" or "png" or "jpg" or "dwg"))
            return Err($"Format '{fmt}' not supported — use pdf, png, jpg, or dwg");

        View? view;
        if (!string.IsNullOrEmpty(viewName))
        {
            view = FindView(doc, viewName);
            if (view == null) return Err($"View '{viewName}' not found in the project");
        }
        else view = app.ActiveUIDocument?.ActiveView;
        if (view == null) return Err("No view available for export");

        var actualViewName = ElemName(view);
        var safeName = SafeExportFileName(actualViewName);
        var exportDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitMCPExport");

        try
        {
            Directory.CreateDirectory(exportDir);
            string filePath;

            if (fmt is "png" or "jpg")
            {
                var options = new ImageExportOptions
                {
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = resolution,
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = Path.Combine(exportDir, safeName),
                    HLRandWFViewsFileType = fmt == "png" ? ImageFileType.PNG : ImageFileType.JPEGMedium,
                };
                options.SetViewsAndSheets(new List<ElementId> { view.Id });

                var before = DateTime.UtcNow;
                doc.ExportImage(options);

                // Revit appends " - <view type> - <view name>" to the prefix; find what it wrote.
                var ext = fmt == "png" ? ".png" : ".jpg";
                filePath = Directory.GetFiles(exportDir, safeName + "*" + ext)
                    .Where(f => File.GetLastWriteTimeUtc(f) >= before.AddSeconds(-5))
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                    ?? Path.Combine(exportDir, safeName + ext);
            }
            else if (fmt == "pdf")
            {
                var pdfOptions = new PDFExportOptions { FileName = safeName, Combine = true };
                bool okExport = doc.Export(exportDir, new List<ElementId> { view.Id }, pdfOptions);
                if (!okExport)
                    return Err("PDF export failed — ensure Revit PDF printer is configured. Try format 'png' as an alternative.");
                filePath = Path.Combine(exportDir, safeName + ".pdf");
            }
            else // dwg
            {
                var dwgOptions = new DWGExportOptions();
                bool okExport = doc.Export(exportDir, safeName, new List<ElementId> { view.Id }, dwgOptions);
                if (!okExport) return Err("DWG export failed");
                filePath = Path.Combine(exportDir, safeName + ".dwg");
            }

            long sizeKb = 0;
            try { if (File.Exists(filePath)) sizeKb = new FileInfo(filePath).Length / 1024; } catch { }

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["exported"] = new Dictionary<string, object?>
                {
                    ["view_name"] = actualViewName,
                    ["format"] = fmt,
                    ["file_path"] = filePath,
                    ["file_size_kb"] = sizeKb,
                },
                ["message"] = $"Exported '{actualViewName}' to {fmt.ToUpperInvariant()} ({sizeKb} KB)",
            });
        }
        catch (Exception ex) { return Err($"{fmt.ToUpperInvariant()} export failed: {ex.Message}"); }
    }

    // --- create_detail_line ----------------------------------------------------

    /// <summary>
    ///     Creates a detail line between two points (mm) in a named or active plan/section/detail view,
    ///     with optional line_style (an OST_Lines subcategory name). Port of detail.py create_detail_line.
    /// </summary>
    internal static string CreateDetailLine(UIApplication app, Document doc, JsonElement args)
    {
        var start = PointArg(args, "start_point");
        var end = PointArg(args, "end_point");
        if (start == null || end == null) return Err("start_point and end_point are required");

        var (view, viewError) = NamedOrActiveView(app, doc, args);
        if (view == null) return Err(viewError);

        if (view.ViewType is not (ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.Section
            or ViewType.Detail or ViewType.Elevation or ViewType.DraftingView or ViewType.AreaPlan))
            return Err("Cannot create detail line — the specified view is not a plan or detail view.");

        Line line;
        try { line = Line.CreateBound(start, end); }
        catch (Exception ex) { return Err("Invalid line geometry: " + ex.Message); }

        var lineStyle = StrArg(args, "line_style");

        return InTransaction(doc, "Create Detail Line via MCP", () =>
        {
            var curve = doc.Create.NewDetailCurve(view, line);
            if (curve == null) return Err("Failed to create detail line");

            if (!string.IsNullOrEmpty(lineStyle))
            {
                try
                {
                    var lineCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                    foreach (Category sub in lineCat.SubCategories)
                        if (string.Equals(sub.Name, lineStyle, StringComparison.OrdinalIgnoreCase))
                        {
                            var gs = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                            if (gs != null) curve.LineStyle = doc.GetElement(gs.Id);
                            break;
                        }
                }
                catch { /* line style is best-effort, matching the Python */ }
            }

            var viewNameOut = ElemName(view);
            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["line_id"] = curve.Id.Value,
                ["view_name"] = viewNameOut,
                ["message"] = $"Created detail line in view '{viewNameOut}'",
            });
        });
    }

    // --- tag_walls --------------------------------------------------------------

    /// <summary>
    ///     Tags every untagged wall in the active view at its curve midpoint (by-category tag, optional
    ///     leader, optional tag_type_name to pick the wall tag symbol). Port of annotation.py tag_walls,
    ///     including the per-wall skipped[] reasons.
    /// </summary>
    internal static string TagWalls(UIApplication app, Document doc, JsonElement args)
    {
        var useLeader = BoolArg(args, "use_leader");
        var tagTypeName = StrArg(args, "tag_type_name");

        var view = app.ActiveUIDocument?.ActiveView;
        if (view == null) return Err("No active view");

        var tagSymbols = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_WallTags).OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>().ToList();
        if (tagSymbols.Count == 0)
            return Err("No wall tag types found — load wall tag families into the project");

        var tagSymbol = !string.IsNullOrEmpty(tagTypeName)
            ? tagSymbols.FirstOrDefault(s =>
                string.Equals(ElemName(s), tagTypeName, StringComparison.OrdinalIgnoreCase)) ?? tagSymbols[0]
            : tagSymbols[0];

        var walls = new FilteredElementCollector(doc, view.Id)
            .OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType().ToList();
        if (walls.Count == 0) return Err("Current view has no walls to tag");

        var taggedWallIds = new HashSet<long>();
        foreach (var tag in new FilteredElementCollector(doc, view.Id)
                     .OfCategory(BuiltInCategory.OST_WallTags).WhereElementIsNotElementType()
                     .OfType<IndependentTag>())
        {
            try
            {
                foreach (var id in tag.GetTaggedLocalElementIds()) taggedWallIds.Add(id.Value);
            }
            catch { /* multi-doc tags can reject this */ }
        }

        return InTransaction(doc, "Tag Walls via MCP", () =>
        {
            int placed = 0, alreadyTagged = 0;
            var skipped = new List<object?>();

            if (!tagSymbol.IsActive) { tagSymbol.Activate(); doc.Regenerate(); }

            foreach (var wall in walls)
            {
                long wallId = wall.Id.Value;
                if (taggedWallIds.Contains(wallId)) { alreadyTagged++; continue; }

                try
                {
                    if (wall.Location is not LocationCurve lc)
                    {
                        skipped.Add(new Dictionary<string, object?>
                        { ["wall_id"] = wallId, ["reason"] = "Wall has no location curve to anchor a tag" });
                        continue;
                    }

                    var mid = lc.Curve.Evaluate(0.5, true);
                    var tag = IndependentTag.Create(doc, view.Id, new Reference(wall), useLeader,
                        TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, mid);
                    if (tag != null) placed++;
                    else skipped.Add(new Dictionary<string, object?>
                    { ["wall_id"] = wallId, ["reason"] = "IndependentTag.Create returned no tag" });
                }
                catch (Exception ex)
                {
                    skipped.Add(new Dictionary<string, object?> { ["wall_id"] = wallId, ["reason"] = ex.Message });
                }
            }

            var payload = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["tags_placed"] = placed,
                ["walls_already_tagged"] = alreadyTagged,
                ["walls_total"] = walls.Count,
                ["message"] = $"Tagged {placed} wall{(placed != 1 ? "s" : "")} ({alreadyTagged} already tagged, "
                              + $"{skipped.Count} failed, {walls.Count} total in view)",
            };
            if (skipped.Count > 0) payload["skipped"] = skipped;
            return Json(payload);
        });
    }

    // --- tag_elements -------------------------------------------------------------

    /// <summary>
    ///     Tags specific elements (element_ids) in a named or active view, mapping each element's
    ///     category to its tag category (walls/doors/windows/rooms/floors/structural framing+columns);
    ///     optional leader, horizontal/vertical orientation, {x,y} mm offset, and tag_type_name.
    ///     Port of tags.py tag_elements — untaggable elements land in skipped[], never fail the call.
    /// </summary>
    internal static string TagElements(UIApplication app, Document doc, JsonElement args)
    {
        var ids = IdListArg(args, "element_ids");
        if (ids.Count == 0) return Err("element_ids is required and must not be empty");

        var (view, viewError) = NamedOrActiveView(app, doc, args);
        if (view == null) return Err(viewError);

        var addLeader = BoolArg(args, "add_leader");
        var orientation = StrArg(args, "orientation") == "vertical"
            ? TagOrientation.Vertical : TagOrientation.Horizontal;
        var tagTypeName = StrArg(args, "tag_type_name");

        double offX = 0, offY = 0;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("offset", out var off)
            && off.ValueKind == JsonValueKind.Object)
        {
            offX = MmToFt(DblOf(off, "x"));
            offY = MmToFt(DblOf(off, "y"));
        }

        return InTransaction(doc, "Tag Elements via MCP", () =>
        {
            var tagIds = new List<object?>();
            var skipped = new List<object?>();

            void Skip(long id, string reason) => skipped.Add(new Dictionary<string, object?>
            { ["element_id"] = id, ["reason"] = reason });

            foreach (var id in ids)
            {
                var el = doc.GetElement(id);
                if (el == null) { Skip(id.Value, "Element not found"); continue; }
                if (el.Category == null) { Skip(id.Value, "Element has no category"); continue; }

                var tagType = FindTagTypeFor(doc, el, tagTypeName);
                if (tagType == null)
                {
                    Skip(id.Value, $"No tag type found for category '{el.Category.Name}'");
                    continue;
                }

                if (!tagType.IsActive) { tagType.Activate(); doc.Regenerate(); }

                var point = TagAnchorPoint(el, view, offX, offY);
                if (point == null) { Skip(id.Value, "Cannot determine element location"); continue; }

                try
                {
                    var tag = IndependentTag.Create(doc, tagType.Id, view.Id, new Reference(el),
                        addLeader, orientation, point);
                    if (tag != null) tagIds.Add(tag.Id.Value);
                    else Skip(id.Value, "Tag creation returned null — no tag type available for this category");
                }
                catch (Exception ex) { Skip(id.Value, "Tag failed: " + ex.Message); }
            }

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["tagged_count"] = tagIds.Count,
                ["tag_ids"] = tagIds,
                ["skipped"] = skipped,
                ["message"] = $"Tagged {tagIds.Count} element{(tagIds.Count != 1 ? "s" : "")}, {skipped.Count} skipped",
            });
        });
    }

    // --- create_dimensions -----------------------------------------------------

    /// <summary>
    ///     Creates a linear dimension across curve-located elements in the active view: extracts a
    ///     geometry Reference per element (FamilyInstance "Center" reference first, then
    ///     ComputeReferences geometry scan over curves/solid edges/faces), builds the dimension line
    ///     offset 3 ft perpendicular to the first–last midpoint axis, then doc.Create.NewDimension.
    ///     Port of annotation.py create_dimensions; elements without an extractable reference are
    ///     reported per-element in skipped[], not fatal.
    /// </summary>
    internal static string CreateDimensions(UIApplication app, Document doc, JsonElement args)
    {
        var ids = IdListArg(args, "element_ids");
        if (ids.Count == 0) return Err("No element_ids provided");
        _ = StrArg(args, "dimension_type"); // accepted for parity; only linear is implemented (as in the Python)

        var view = app.ActiveUIDocument?.ActiveView;
        if (view == null) return Err("No active view");
        if (view.ViewType is not (ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.Section
            or ViewType.Elevation or ViewType.Detail or ViewType.EngineeringPlan or ViewType.AreaPlan))
            return Err("Current view does not support dimensions — switch to a plan, section, or elevation view");

        var refs = new ReferenceArray();
        var points = new List<XYZ>();
        var skipped = new List<object?>();

        void Skip(long id, string reason) => skipped.Add(new Dictionary<string, object?>
        { ["element_id"] = id, ["reason"] = reason });

        foreach (var id in ids)
        {
            var el = doc.GetElement(id);
            if (el == null) { Skip(id.Value, "Element not found or not visible in the current view"); continue; }

            try
            {
                if (el.Location is LocationCurve lc)
                {
                    Reference? r = null;
                    if (el is FamilyInstance fi)
                        try { r = fi.GetReferenceByName("Center"); } catch { /* no named reference */ }
                    r ??= FirstGeometryReference(el, view);
                    if (r != null)
                    {
                        refs.Append(r);
                        points.Add(lc.Curve.Evaluate(0.5, true));
                    }
                    else Skip(id.Value, "Could not extract a geometry reference");
                }
                else if (el.Location is LocationPoint lp)
                {
                    // The Python collects the point but never a reference for point-located elements.
                    points.Add(lp.Point);
                    Skip(id.Value, "Point-located element contributes no dimension reference");
                }
                else Skip(id.Value, "Element has no usable location");
            }
            catch (Exception ex) { Skip(id.Value, "Could not get reference: " + ex.Message); }
        }

        if (refs.Size < 2)
            return Err("Need at least 2 elements with valid geometry references to create a dimension");

        return InTransaction(doc, "Create Dimensions via MCP", () =>
        {
            Line dimLine;
            if (points.Count >= 2)
            {
                var p1 = points[0];
                var p2 = points[^1];
                double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 0.001)
                {
                    const double offsetDist = 3.0; // feet, as in the Python
                    double nx = -dy / len, ny = dx / len;
                    dimLine = Line.CreateBound(
                        new XYZ(p1.X + nx * offsetDist, p1.Y + ny * offsetDist, p1.Z),
                        new XYZ(p2.X + nx * offsetDist, p2.Y + ny * offsetDist, p2.Z));
                }
                else dimLine = Line.CreateBound(p1, p2);
            }
            else dimLine = Line.CreateBound(new XYZ(0, 10, 0), new XYZ(100, 10, 0));

            var created = new List<object?>();
            var dim = doc.Create.NewDimension(view, dimLine, refs);
            if (dim != null)
            {
                string? value = null;
                try { value = dim.ValueString; } catch { /* segmented dims reject ValueString */ }
                created.Add(new Dictionary<string, object?>
                {
                    ["id"] = dim.Id.Value,
                    ["elements_dimensioned"] = ids.Select(i => (object?)i.Value).ToList(),
                    ["value"] = string.IsNullOrEmpty(value) ? "N/A" : value,
                });
            }

            return Json(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["created"] = created,
                ["count"] = created.Count,
                ["skipped"] = skipped,
                ["message"] = $"Created {created.Count} dimension annotation{(created.Count != 1 ? "s" : "")} in the current view",
            });
        });
    }

    // --- private helpers (view/annotation tools only) ---------------------------

    /// <summary>Element categories → their tag categories (mirror of the Python CATEGORY_TO_TAG_CATEGORY).</summary>
    private static readonly Dictionary<BuiltInCategory, BuiltInCategory> ElementToTagCategory = new()
    {
        [BuiltInCategory.OST_Walls] = BuiltInCategory.OST_WallTags,
        [BuiltInCategory.OST_Doors] = BuiltInCategory.OST_DoorTags,
        [BuiltInCategory.OST_Windows] = BuiltInCategory.OST_WindowTags,
        [BuiltInCategory.OST_Rooms] = BuiltInCategory.OST_RoomTags,
        [BuiltInCategory.OST_Floors] = BuiltInCategory.OST_FloorTags,
        [BuiltInCategory.OST_StructuralFraming] = BuiltInCategory.OST_StructuralFramingTags,
        [BuiltInCategory.OST_StructuralColumns] = BuiltInCategory.OST_StructuralColumnTags,
    };

    /// <summary>View from args "view_name" (error if named but missing), else the active view.</summary>
    private static (View? view, string error) NamedOrActiveView(UIApplication app, Document doc, JsonElement args)
    {
        var name = StrArg(args, "view_name");
        if (!string.IsNullOrEmpty(name))
        {
            var named = FindView(doc, name);
            return named != null ? (named, "") : (null, $"View '{name}' not found");
        }
        var active = app.ActiveUIDocument?.ActiveView;
        return active != null ? (active, "") : (null, "No active view");
    }

    /// <summary>First ViewFamilyType of the given family, or null.</summary>
    private static ViewFamilyType? ViewFamilyTypeOf(Document doc, ViewFamily family)
        => new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == family);

    /// <summary>Reads a {x,y,z} unit-vector object (NOT mm) with a fallback; normalized.</summary>
    private static XYZ UnitVecOf(JsonElement obj, string prop, XYZ fallback)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var v)
            || v.ValueKind != JsonValueKind.Object) return fallback;
        var vec = new XYZ(DblOf(v, "x"), DblOf(v, "y"), DblOf(v, "z"));
        return vec.GetLength() < 1e-9 ? fallback : vec.Normalize();
    }

    /// <summary>View name sanitized for use as an export file name (spaces/invalid chars → underscore).</summary>
    private static string SafeExportFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => c == ' ' || c == '/' || invalid.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars);
        return string.IsNullOrEmpty(safe) ? "export" : safe;
    }

    /// <summary>Tag FamilySymbol for an element's category via the category→tag map (named type preferred).</summary>
    private static FamilySymbol? FindTagTypeFor(Document doc, Element el, string? tagTypeName)
    {
        try
        {
            if (el.Category == null || !ElementToTagCategory.TryGetValue(el.Category.BuiltInCategory, out var tagBic))
                return null;
            var symbols = new FilteredElementCollector(doc).OfCategory(tagBic).OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>().ToList();
            if (!string.IsNullOrEmpty(tagTypeName))
            {
                var named = symbols.FirstOrDefault(s =>
                    string.Equals(ElemName(s), tagTypeName, StringComparison.OrdinalIgnoreCase));
                if (named != null) return named;
            }
            return symbols.FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Tag anchor: location point / curve midpoint / view bbox center, plus an {x,y} offset (feet).</summary>
    private static XYZ? TagAnchorPoint(Element el, View view, double offX, double offY)
    {
        try
        {
            switch (el.Location)
            {
                case LocationPoint lp:
                    return new XYZ(lp.Point.X + offX, lp.Point.Y + offY, lp.Point.Z);
                case LocationCurve lc:
                    var mid = lc.Curve.Evaluate(0.5, true);
                    return new XYZ(mid.X + offX, mid.Y + offY, mid.Z);
            }
        }
        catch { /* fall through to bbox */ }
        try
        {
            var bb = el.get_BoundingBox(view);
            if (bb != null)
                return new XYZ((bb.Min.X + bb.Max.X) / 2.0 + offX,
                    (bb.Min.Y + bb.Max.Y) / 2.0 + offY,
                    (bb.Min.Z + bb.Max.Z) / 2.0);
        }
        catch { }
        return null;
    }

    /// <summary>
    ///     First geometry Reference found on an element (ComputeReferences, view-specific): bare curves,
    ///     then solid edges/faces, then instance geometry — mirrors the Python's best-effort scan.
    /// </summary>
    private static Reference? FirstGeometryReference(Element el, View view)
    {
        try
        {
            var geom = el.get_Geometry(new Options { ComputeReferences = true, View = view });
            return geom == null ? null : ScanForReference(geom);
        }
        catch { return null; }
    }

    private static Reference? ScanForReference(GeometryElement geom)
    {
        foreach (var obj in geom)
        {
            switch (obj)
            {
                case Curve c when c.Reference != null:
                    return c.Reference;
                case Solid s:
                    foreach (Edge e in s.Edges) if (e.Reference != null) return e.Reference;
                    foreach (Face f in s.Faces) if (f.Reference != null) return f.Reference;
                    break;
                case GeometryInstance gi:
                    var r = ScanForReference(gi.GetInstanceGeometry());
                    if (r != null) return r;
                    break;
            }
        }
        return null;
    }
}
