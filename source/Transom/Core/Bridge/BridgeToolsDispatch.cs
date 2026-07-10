using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Core;

/// <summary>
///     Routes the extended tool names to their ported handlers (implemented in BridgeToolsViews /
///     BridgeToolsElements / BridgeToolsCreate). <see cref="BridgeTools.Handle"/> calls this from its switch's
///     default arm, so the original five schedule tools + execute_revit_code stay on their dedicated paths and
///     everything else falls through to here. Returns null for a genuinely unknown tool so Handle can answer
///     with its "unknown tool" error.
/// </summary>
public static partial class BridgeTools
{
    internal static string? DispatchParity(UIApplication app, Document doc, string tool, JsonElement args) => tool switch
    {
        // --- views / sheets / annotation (BridgeToolsViews) ---
        "get_revit_view"            => GetRevitView(app, doc, args),
        "list_revit_views"          => ListRevitViews(app, doc, args),
        "get_current_view_info"     => GetCurrentViewInfo(app, doc, args),
        "get_current_view_elements" => GetCurrentViewElements(app, doc, args),
        "create_view"               => CreateView(app, doc, args),
        "set_active_view"           => SetActiveView(app, doc, args),
        "create_sheet"              => CreateSheet(app, doc, args),
        "export_document"           => ExportDocument(app, doc, args),
        "create_detail_line"        => CreateDetailLine(app, doc, args),
        "tag_walls"                 => TagWalls(app, doc, args),
        "tag_elements"              => TagElements(app, doc, args),
        "create_dimensions"         => CreateDimensions(app, doc, args),

        // --- elements / analysis (BridgeToolsElements) ---
        "get_revit_model_info"      => GetRevitModelInfo(app, doc, args),
        "list_families"             => ListFamilies(app, doc, args),
        "list_family_categories"    => ListFamilyCategories(app, doc, args),
        "load_family"               => LoadFamily(app, doc, args),
        "place_family"              => PlaceFamily(app, doc, args),
        "list_levels"               => ListLevels(app, doc, args),
        "list_category_parameters"  => ListCategoryParameters(app, doc, args),
        "get_element_properties"    => GetElementProperties(app, doc, args),
        "get_selected_elements"     => GetSelectedElements(app, doc, args),
        "modify_element"            => ModifyElement(app, doc, args),
        "delete_elements"           => DeleteElements(app, doc, args),
        "transform_elements"        => TransformElements(app, doc, args),
        "ai_element_filter"         => AiElementFilter(app, doc, args),
        "analyze_model_statistics"  => AnalyzeModelStatistics(app, doc, args),
        "export_room_data"          => ExportRoomData(app, doc, args),
        "get_material_quantities"   => GetMaterialQuantities(app, doc, args),
        "check_clashes"             => CheckClashes(app, doc, args),
        "color_splash"              => ColorSplash(app, doc, args),
        "clear_colors"              => ClearColors(app, doc, args),

        // --- creation / MEP / interop (BridgeToolsCreate) ---
        "create_line_based_element"    => CreateLineBasedElement(app, doc, args),
        "create_surface_based_element" => CreateSurfaceBasedElement(app, doc, args),
        "create_level"                 => CreateLevel(app, doc, args),
        "create_grid"                  => CreateGrid(app, doc, args),
        "create_structural_framing"    => CreateStructuralFraming(app, doc, args),
        "create_room"                  => CreateRoom(app, doc, args),
        "create_room_separation"       => CreateRoomSeparation(app, doc, args),
        "create_duct"                  => CreateDuct(app, doc, args),
        "create_pipe"                  => CreatePipe(app, doc, args),
        "create_mep_system"            => CreateMepSystem(app, doc, args),
        "create_schedule"              => CreateSchedule(app, doc, args),
        "export_ifc"                   => ExportIfc(app, doc, args),
        "link_file"                    => LinkFile(app, doc, args),
        "save_document"                => SaveDocument(app, doc, args),

        _ => null,
    };
}
