using System.Text.Json.Nodes;

namespace Transom.McpShim;

/// <summary>
/// Declarative MCP tool definitions mirroring the revit-mcp-server pyRevit extension
/// (revit-mcp-server.extension/tools/*.py). The shim forwards every tools/call generically
/// to the HTTP bridge, so parity with the Python server is achieved purely by advertising
/// the same tool names and input schemas here — no per-tool handler code is needed.
///
/// Conventions mirrored from the Python FastMCP definitions:
///   - All coordinates, lengths, offsets, and dimensions are in MILLIMETERS.
///   - Angles are in degrees.
///   - Optional parameters note their server-side defaults in the description.
/// </summary>
internal static class ParityTools
{
    /// <summary>Append every parity tool definition to the tools/list array.</summary>
    public static void AddTo(JsonArray tools)
    {
        // ------------------------------------------------------ status / model info

        tools.Add(Tool(
            "get_revit_model_info",
            "Get comprehensive information about the current Revit model — project name, file "
            + "path, and high-level contents. Use this early in a session to orient yourself in "
            + "the open document.",
            NoArgsSchema()));

        // ----------------------------------------------------------------- views

        tools.Add(Tool(
            "get_revit_view",
            "Export a specific Revit view as an image so you can visually inspect the model. "
            + "Use list_revit_views first to discover exportable view names.",
            Schema(
                new JsonObject
                {
                    ["view_name"] = Prop("string", "Name of the view to export as an image."),
                },
                "view_name")));

        tools.Add(Tool(
            "list_revit_views",
            "Get a list of all exportable views in the current Revit model, grouped by view "
            + "type. Use this to find view names for get_revit_view, set_active_view, or "
            + "export_document.",
            NoArgsSchema()));

        tools.Add(Tool(
            "get_current_view_info",
            "Get detailed information about the currently active view in Revit: name, type, id, "
            + "scale, detail level, crop box status, discipline, and template status. Use it to "
            + "understand the drawing context before creating view-specific elements.",
            NoArgsSchema()));

        tools.Add(Tool(
            "get_current_view_elements",
            "Get all elements visible in the currently active view, with id, name, type, "
            + "category, level, and location for each, plus summary counts by category. Useful "
            + "for understanding what is on screen before selecting, tagging, or editing.",
            NoArgsSchema()));

        // -------------------------------------------------------------- families

        tools.Add(Tool(
            "place_family",
            "Place a family instance (furniture, door, window, equipment, etc.) at a location "
            + "in the Revit model. Coordinates are in millimeters. Use list_families to find "
            + "loadable family/type names first, and load_family if the family is not yet in "
            + "the project.",
            Schema(
                new JsonObject
                {
                    ["family_name"] = Prop("string", "Name of the family to place (e.g. \"Desk\")."),
                    ["type_name"] = Prop("string", "Family type name (optional; uses the family's default type if omitted)."),
                    ["x"] = Prop("number", "X coordinate of the placement point in millimeters (defaults to 0)."),
                    ["y"] = Prop("number", "Y coordinate of the placement point in millimeters (defaults to 0)."),
                    ["z"] = Prop("number", "Z coordinate of the placement point in millimeters (defaults to 0)."),
                    ["rotation"] = Prop("number", "Rotation about the placement point in degrees (defaults to 0)."),
                    ["level_name"] = Prop("string", "Target level name (optional; server picks a level if omitted)."),
                    ["properties"] = Dict("Optional dictionary of parameter name to value pairs to set on the new instance (e.g. {\"Mark\": \"F-01\"})."),
                },
                "family_name")));

        tools.Add(Tool(
            "list_families",
            "Get a flat list of available family types in the current Revit model, optionally "
            + "filtered by a substring. Use this to discover exact family and type names before "
            + "calling place_family.",
            Schema(
                new JsonObject
                {
                    ["contains"] = Prop("string", "Only return families whose name contains this substring (optional)."),
                    ["limit"] = Prop("integer", "Maximum number of results to return (defaults to 50)."),
                })));

        tools.Add(Tool(
            "list_family_categories",
            "Get a list of all family categories present in the current Revit model. Use this "
            + "to narrow down what kinds of families are available before listing or placing them.",
            NoArgsSchema()));

        tools.Add(Tool(
            "load_family",
            "Load a Revit family (.rfa file) from disk into the active document. Use this when "
            + "a needed family (furniture, doors, windows, equipment) is not already loaded in "
            + "the project — load it first, then place_family can place its types.",
            Schema(
                new JsonObject
                {
                    ["file_path"] = Prop("string", "Full path to the .rfa family file accessible to the machine running Revit, e.g. \"C:\\\\ProgramData\\\\Autodesk\\\\RVT 2027\\\\Libraries\\\\English\\\\Furniture\\\\Chair.rfa\"."),
                },
                "file_path")));

        // ---------------------------------------------------------------- levels

        tools.Add(Tool(
            "list_levels",
            "Get a list of all levels in the current Revit model with their names and "
            + "elevations. Call this before creating level-dependent elements (walls, floors, "
            + "rooms) so you can reference valid level names.",
            NoArgsSchema()));

        // ---------------------------------------------------------------- colors

        tools.Add(Tool(
            "color_splash",
            "Color elements in a category based on their parameter values — elements sharing a "
            + "value get the same color. Useful for visually auditing a model (e.g. color walls "
            + "by \"Type Name\" or rooms by \"Department\"). Use list_category_parameters to "
            + "discover valid parameter names first, and clear_colors to undo.",
            Schema(
                new JsonObject
                {
                    ["category_name"] = Prop("string", "Name of the category to color (e.g. \"Walls\", \"Doors\", \"Windows\")."),
                    ["parameter_name"] = Prop("string", "Name of the parameter to color by (e.g. \"Mark\", \"Type Name\")."),
                    ["use_gradient"] = Prop("boolean", "Use gradient coloring instead of distinct colors (defaults to false)."),
                    ["custom_colors"] = StringArray("Optional list of custom colors in hex format (e.g. [\"#FF0000\", \"#00FF00\"])."),
                },
                "category_name", "parameter_name")));

        tools.Add(Tool(
            "clear_colors",
            "Remove all color overrides previously applied to elements in a category (e.g. by "
            + "color_splash), returning them to their default appearance.",
            Schema(
                new JsonObject
                {
                    ["category_name"] = Prop("string", "Name of the category to clear colors from (e.g. \"Walls\", \"Doors\")."),
                },
                "category_name")));

        tools.Add(Tool(
            "list_category_parameters",
            "List the parameters available on elements of a category, with their types and "
            + "sample values. Use this to discover valid parameter names for color_splash or "
            + "for reading/writing element data.",
            Schema(
                new JsonObject
                {
                    ["category_name"] = Prop("string", "Name of the category to inspect (e.g. \"Walls\", \"Doors\")."),
                },
                "category_name")));

        // -------------------------------------------------------------- building

        tools.Add(Tool(
            "create_line_based_element",
            "Create walls, beams, and other line-based building elements in Revit. Each element "
            + "runs from a start point to an end point (in millimeters) and supports batch "
            + "creation — pass multiple elements in one call.",
            Schema(
                new JsonObject
                {
                    ["elements"] = ArrayOf(
                        Schema(
                            new JsonObject
                            {
                                ["element_type"] = Prop("string", "\"wall\" or \"beam\"."),
                                ["start_point"] = Point("Start point of the element in millimeters."),
                                ["end_point"] = Point("End point of the element in millimeters."),
                                ["type_name"] = Prop("string", "Family type name, e.g. \"Generic - 200mm\" (optional)."),
                                ["level_name"] = Prop("string", "Target level name (optional, defaults to the first level)."),
                                ["height"] = Prop("number", "Element height in millimeters (optional, defaults to 3000)."),
                                ["offset"] = Prop("number", "Offset from the base level in millimeters (optional, defaults to 0)."),
                                ["structural"] = Prop("boolean", "Mark as structural (optional, defaults to false)."),
                                ["name"] = Prop("string", "Description for reference (optional)."),
                            },
                            "element_type", "start_point", "end_point"),
                        "List of line-based element definitions."),
                },
                "elements")));

        tools.Add(Tool(
            "create_surface_based_element",
            "Create floors, roofs, ceilings, and other surface-based building elements. Each "
            + "element needs a closed boundary polygon (list of line segments where the last "
            + "point connects back to the first). All dimensions in millimeters; supports batch "
            + "creation.",
            Schema(
                new JsonObject
                {
                    ["elements"] = ArrayOf(
                        Schema(
                            new JsonObject
                            {
                                ["element_type"] = Prop("string", "\"floor\", \"roof\", or \"ceiling\"."),
                                ["boundary"] = ArrayOf(
                                    Schema(
                                        new JsonObject
                                        {
                                            ["p0"] = Point("Segment start point in millimeters."),
                                            ["p1"] = Point("Segment end point in millimeters."),
                                        },
                                        "p0", "p1"),
                                    "Line segments forming a closed polygon (minimum 3 segments; the last point must connect back to the first)."),
                                ["type_name"] = Prop("string", "Family type name (optional)."),
                                ["level_name"] = Prop("string", "Target level name (optional)."),
                                ["offset"] = Prop("number", "Offset from the level in millimeters (optional, defaults to 0)."),
                                ["name"] = Prop("string", "Description for reference (optional)."),
                            },
                            "element_type", "boundary"),
                        "List of surface-based element definitions."),
                },
                "elements")));

        tools.Add(Tool(
            "create_level",
            "Create building levels (floor elevations) in Revit. Levels define floor-to-floor "
            + "heights and must exist before placing walls, floors, or other level-dependent "
            + "elements. Supports batch creation.",
            Schema(
                new JsonObject
                {
                    ["levels"] = ArrayOf(
                        Schema(
                            new JsonObject
                            {
                                ["elevation"] = Prop("number", "Elevation in millimeters from the project origin."),
                                ["name"] = Prop("string", "Level name, e.g. \"Ground Floor\" (optional, auto-assigned)."),
                            },
                            "elevation"),
                        "List of level definitions."),
                },
                "levels")));

        // --------------------------------------------------------------- editing

        tools.Add(Tool(
            "delete_elements",
            "Delete one or more elements from the Revit model by id. Deleting a host element "
            + "(e.g. a wall with doors) cascades to its hosted elements. All deletions happen in "
            + "a single transaction — if any fails, none are deleted.",
            Schema(
                new JsonObject
                {
                    ["element_ids"] = IntArray("List of Revit element ids to delete."),
                },
                "element_ids")));

        tools.Add(Tool(
            "modify_element",
            "Modify one or more instance parameter values on a Revit element. Returns old and "
            + "new values for confirmation. Use get_element_properties first to see which "
            + "parameters exist and are writable.",
            Schema(
                new JsonObject
                {
                    ["element_id"] = Prop("integer", "Revit element id to modify."),
                    ["parameters"] = Dict("Dictionary of parameter name to new value pairs, e.g. {\"Mark\": \"EW-01\", \"Comments\": \"Updated via MCP\"}."),
                },
                "element_id", "parameters")));

        tools.Add(Tool(
            "get_selected_elements",
            "Get details of elements currently selected in the Revit UI — ids, categories, "
            + "types, and key parameters. Returns an empty list (not an error) when nothing is "
            + "selected. Useful when the user says \"the elements I've selected\".",
            NoArgsSchema()));

        // ------------------------------------------------------------- structure

        tools.Add(Tool(
            "create_grid",
            "Create grid lines for the structural layout of a building. Each grid runs from a "
            + "start point to an end point (in millimeters); names auto-assign (A, B, C... or "
            + "1, 2, 3...) if not provided. Supports batch creation.",
            Schema(
                new JsonObject
                {
                    ["grids"] = ArrayOf(
                        Schema(
                            new JsonObject
                            {
                                ["start_point"] = Point("Grid line start point in millimeters."),
                                ["end_point"] = Point("Grid line end point in millimeters."),
                                ["name"] = Prop("string", "Grid line name (optional, auto-assigned)."),
                            },
                            "start_point", "end_point"),
                        "List of grid line definitions."),
                },
                "grids")));

        tools.Add(Tool(
            "create_structural_framing",
            "Create structural beams and framing elements. Beams run along a line between two "
            + "points at the given level's elevation; each point's z is an OFFSET from that "
            + "level (pass z=0 to sit on the level). All dimensions in millimeters; supports "
            + "batch creation.",
            Schema(
                new JsonObject
                {
                    ["elements"] = ArrayOf(
                        Schema(
                            new JsonObject
                            {
                                ["start_point"] = Point("Beam start point in millimeters (z is an offset from the level)."),
                                ["end_point"] = Point("Beam end point in millimeters (z is an offset from the level)."),
                                ["type_name"] = Prop("string", "Beam family type name (optional)."),
                                ["level_name"] = Prop("string", "Target level name — sets the beam elevation (optional)."),
                                ["name"] = Prop("string", "Description for reference (optional)."),
                            },
                            "start_point", "end_point"),
                        "List of beam definitions."),
                },
                "elements")));

        // ------------------------------------------------------------ annotation

        tools.Add(Tool(
            "create_dimensions",
            "Create dimension annotations for elements in the current view. Works in plan, "
            + "section, and elevation views; the dimension line is placed at an offset from the "
            + "elements for readability.",
            Schema(
                new JsonObject
                {
                    ["element_ids"] = IntArray("List of element ids to dimension."),
                    ["dimension_type"] = Prop("string", "Type of dimension — \"linear\", \"aligned\", or \"angular\" (defaults to \"linear\")."),
                },
                "element_ids")));

        tools.Add(Tool(
            "tag_walls",
            "Tag all untagged walls in the current Revit view. Places a wall tag centered on "
            + "every visible wall segment that does not already have one.",
            Schema(
                new JsonObject
                {
                    ["use_leader"] = Prop("boolean", "Show leader lines on the tags (defaults to false)."),
                    ["tag_type_name"] = Prop("string", "Specific wall tag family type name (optional, uses the first available)."),
                })));

        // -------------------------------------------------------------- analysis

        tools.Add(Tool(
            "ai_element_filter",
            "Filter and find Revit elements by category, type name, view visibility, and "
            + "spatial bounds. Combine filters for precise queries — e.g. all doors within a "
            + "bounding box that are visible in the current view. Bounding box coordinates are "
            + "in millimeters.",
            Schema(
                new JsonObject
                {
                    ["category"] = Prop("string", "BuiltInCategory name, e.g. \"OST_Walls\", \"OST_Doors\" (optional)."),
                    ["type_name"] = Prop("string", "Filter by family type name or partial match (optional)."),
                    ["visible_in_view"] = Prop("boolean", "Only include elements visible in the active view (defaults to false)."),
                    ["bounding_box_min"] = Point("Spatial filter minimum corner in millimeters (optional)."),
                    ["bounding_box_max"] = Point("Spatial filter maximum corner in millimeters (optional)."),
                    ["max_elements"] = Prop("integer", "Maximum results to return (defaults to 50)."),
                })));

        tools.Add(Tool(
            "export_room_data",
            "Export data for all rooms defined in the Revit model — names, numbers, levels, "
            + "areas, perimeters, and departments — in a structured format suitable for "
            + "analysis and reporting.",
            NoArgsSchema()));

        tools.Add(Tool(
            "get_material_quantities",
            "Get material quantities (areas and volumes) aggregated across the model, "
            + "optionally filtered by category. Useful for quantity takeoffs and cost "
            + "estimation.",
            Schema(
                new JsonObject
                {
                    ["categories"] = StringArray("Categories to include, e.g. [\"OST_Walls\", \"OST_Floors\"] (optional, defaults to all categories)."),
                })));

        tools.Add(Tool(
            "analyze_model_statistics",
            "Analyze the Revit model and return element counts grouped by category — a "
            + "high-level overview of how many walls, doors, windows, floors, etc. exist. "
            + "Useful for progress tracking and model health checks.",
            NoArgsSchema()));

        // --------------------------------------------------------- documentation

        tools.Add(Tool(
            "create_sheet",
            "Create a drawing sheet in Revit for construction documentation. Each sheet has a "
            + "title block, sheet number, and name; views can be placed on it afterwards.",
            Schema(
                new JsonObject
                {
                    ["sheet_number"] = Prop("string", "Sheet number, e.g. \"A101\" (optional, auto-assigned)."),
                    ["sheet_name"] = Prop("string", "Sheet title, e.g. \"Ground Floor Plan\" (defaults to \"Unnamed Sheet\")."),
                    ["title_block_name"] = Prop("string", "Title block family name (optional, uses the first available)."),
                })));

        tools.Add(Tool(
            "create_schedule",
            "Create a schedule (quantity takeoff view) for an element category — e.g. a wall "
            + "schedule with types, lengths, and areas. The schedule appears as a new view in "
            + "the Revit project browser.",
            Schema(
                new JsonObject
                {
                    ["category"] = Prop("string", "BuiltInCategory name, e.g. \"OST_Walls\", \"OST_Rooms\"."),
                    ["fields"] = StringArray("Parameter names to include as columns, e.g. [\"Family and Type\", \"Length\", \"Area\", \"Mark\"] (optional, uses a default set)."),
                    ["schedule_name"] = Prop("string", "Name for the schedule view (optional, auto-generated)."),
                },
                "category")));

        tools.Add(Tool(
            "export_document",
            "Export a Revit view or sheet to a file on disk. Supported formats: PDF, PNG, JPG, "
            + "DWG. Exports the active view when no view name is given.",
            Schema(
                new JsonObject
                {
                    ["view_name"] = Prop("string", "Name of the view or sheet to export (optional, uses the active view)."),
                    ["format"] = Prop("string", "Output format — \"pdf\", \"png\", \"jpg\", or \"dwg\" (defaults to \"pdf\")."),
                    ["resolution"] = Prop("integer", "DPI for image formats (defaults to 300; ignored for PDF/DWG)."),
                })));

        // ----------------------------------------------------------------- rooms

        tools.Add(Tool(
            "create_room",
            "Create a room on a level in the Revit model. Rooms must be placed inside enclosed "
            + "areas (bounded by walls or room separation lines); if no location is given, "
            + "Revit auto-places the room. Location coordinates are in millimeters.",
            Schema(
                new JsonObject
                {
                    ["level_name"] = Prop("string", "Target level name (e.g. \"Level 1\")."),
                    ["location"] = Point2D("Optional placement point in millimeters."),
                    ["name"] = Prop("string", "Room name (e.g. \"Living Room\") (optional)."),
                    ["number"] = Prop("string", "Room number (e.g. \"101\") (optional)."),
                },
                "level_name")));

        tools.Add(Tool(
            "create_room_separation",
            "Create room separation lines, which act as invisible walls for room boundary "
            + "calculation. Use these when physical walls don't fully enclose a space (e.g. "
            + "open-plan areas, corridors). All coordinates in millimeters.",
            Schema(
                new JsonObject
                {
                    ["lines"] = ArrayOf(
                        Schema(
                            new JsonObject
                            {
                                ["start_point"] = Point("Segment start point in millimeters."),
                                ["end_point"] = Point("Segment end point in millimeters."),
                            },
                            "start_point", "end_point"),
                        "List of line segments defining the separation boundary."),
                    ["level_name"] = Prop("string", "Target level name (optional, defaults to the active view's level)."),
                    ["view_name"] = Prop("string", "Plan view name (optional, defaults to the active view)."),
                },
                "lines")));

        // ------------------------------------------------------- view management

        tools.Add(Tool(
            "create_view",
            "Create a new view in the Revit model — floor plan, ceiling plan, section, "
            + "elevation, or 3D. Floor and ceiling plans require a level name; sections require "
            + "a section box definition (dimensions in millimeters).",
            Schema(
                new JsonObject
                {
                    ["view_type"] = Prop("string", "Type of view — \"floor_plan\", \"ceiling_plan\", \"section\", \"elevation\", or \"3d\"."),
                    ["name"] = Prop("string", "Display name for the new view."),
                    ["level_name"] = Prop("string", "Required for floor_plan and ceiling_plan — the level to show."),
                    ["section_box"] = Schema(
                        new JsonObject
                        {
                            ["origin"] = Point("Center point of the section box in millimeters."),
                            ["direction"] = Vector("View direction vector (unitless components)."),
                            ["up"] = Vector("Up direction vector (unitless components)."),
                            ["width"] = Prop("number", "Section width in millimeters."),
                            ["height"] = Prop("number", "Section height in millimeters."),
                            ["depth"] = Prop("number", "Section depth in millimeters."),
                        })
                        .WithDescription("Required for section views — defines the cut plane."),
                },
                "view_type", "name")));

        tools.Add(Tool(
            "set_active_view",
            "Switch the active view in the Revit UI to the specified view. Use "
            + "list_revit_views first to find available view names.",
            Schema(
                new JsonObject
                {
                    ["view_name"] = Prop("string", "Name of the view to activate."),
                },
                "view_name")));

        // --------------------------------------------------------------- tagging

        tools.Add(Tool(
            "tag_elements",
            "Tag specific elements with annotation symbols in a view. Tags display element "
            + "properties like type name, mark, or room name/number, and work with walls, "
            + "doors, windows, rooms, and other taggable categories. Offset is in millimeters.",
            Schema(
                new JsonObject
                {
                    ["element_ids"] = IntArray("List of element ids to tag."),
                    ["view_name"] = Prop("string", "View to place tags in (optional, defaults to the active view)."),
                    ["tag_type_name"] = Prop("string", "Tag family type name (optional, auto-detects an appropriate tag)."),
                    ["add_leader"] = Prop("boolean", "Show a leader line from the tag to the element (defaults to false)."),
                    ["orientation"] = Prop("string", "Tag orientation — \"horizontal\" or \"vertical\" (defaults to \"horizontal\")."),
                    ["offset"] = Point2D("Tag offset from the element center in millimeters (optional)."),
                },
                "element_ids")));

        // ------------------------------------------------------------- transform

        tools.Add(Tool(
            "transform_elements",
            "Move, copy, rotate, or mirror elements in the Revit model. All coordinates and "
            + "distances are in millimeters, angles in degrees. Each operation requires its own "
            + "extra fields: vector for move/copy, axis_point + angle for rotate, mirror_plane "
            + "for mirror.",
            Schema(
                new JsonObject
                {
                    ["element_ids"] = IntArray("List of element ids to transform."),
                    ["operation"] = Prop("string", "Transform type — \"move\", \"copy\", \"rotate\", or \"mirror\"."),
                    ["vector"] = Point("Translation vector in millimeters (required for move/copy)."),
                    ["axis_point"] = Point("Rotation center in millimeters (required for rotate)."),
                    ["angle"] = Prop("number", "Rotation angle in degrees (required for rotate)."),
                    ["mirror_plane"] = Schema(
                        new JsonObject
                        {
                            ["origin"] = Point("A point on the mirror plane in millimeters."),
                            ["normal"] = Vector("Mirror plane normal direction (unitless components)."),
                        },
                        "origin", "normal")
                        .WithDescription("Mirror plane definition (required for mirror)."),
                },
                "element_ids", "operation")));

        // ------------------------------------------------------------------- MEP

        tools.Add(Tool(
            "create_duct",
            "Create a duct between two points in the Revit model. Requires a project with "
            + "mechanical families loaded (MEP template). Specify diameter for round ducts, or "
            + "width + height for rectangular. All dimensions in millimeters.",
            Schema(
                new JsonObject
                {
                    ["start_point"] = Point("Duct start point in millimeters."),
                    ["end_point"] = Point("Duct end point in millimeters."),
                    ["system_type"] = Prop("string", "System type name (e.g. \"Supply Air\"). Auto-detects if omitted."),
                    ["duct_type"] = Prop("string", "Duct type name (e.g. \"Round Duct\"). Auto-detects if omitted."),
                    ["level_name"] = Prop("string", "Level name (optional, defaults to the nearest level)."),
                    ["diameter"] = Prop("number", "Round duct diameter in millimeters (optional)."),
                    ["width"] = Prop("number", "Rectangular duct width in millimeters (optional)."),
                    ["height"] = Prop("number", "Rectangular duct height in millimeters (optional)."),
                },
                "start_point", "end_point")));

        tools.Add(Tool(
            "create_pipe",
            "Create a pipe between two points in the Revit model. Requires a project with "
            + "plumbing families loaded (MEP template). All dimensions in millimeters.",
            Schema(
                new JsonObject
                {
                    ["start_point"] = Point("Pipe start point in millimeters."),
                    ["end_point"] = Point("Pipe end point in millimeters."),
                    ["system_type"] = Prop("string", "System type name (e.g. \"Domestic Hot Water\"). Auto-detects if omitted."),
                    ["pipe_type"] = Prop("string", "Pipe type name (e.g. \"Copper\"). Auto-detects if omitted."),
                    ["level_name"] = Prop("string", "Level name (optional, defaults to the nearest level)."),
                    ["diameter"] = Prop("number", "Pipe diameter in millimeters (optional)."),
                },
                "start_point", "end_point")));

        tools.Add(Tool(
            "create_mep_system",
            "Create a mechanical or piping system and optionally add duct/pipe elements to it. "
            + "Groups MEP elements into a named system for organization and analysis.",
            Schema(
                new JsonObject
                {
                    ["system_type"] = Prop("string", "\"mechanical\" or \"piping\"."),
                    ["system_name"] = Prop("string", "Display name for the system (e.g. \"Level 1 Supply Air\")."),
                    ["element_ids"] = IntArray("Optional list of duct/pipe element ids to add to the system."),
                },
                "system_type", "system_name")));

        // ------------------------------------------------------------ parameters

        tools.Add(Tool(
            "get_element_properties",
            "Get all properties and parameters of a Revit element — category, family, type, and "
            + "a complete list of instance (and optionally type) parameters with values, storage "
            + "types, and read-only status. Use this before modify_element to see what is "
            + "writable.",
            Schema(
                new JsonObject
                {
                    ["element_id"] = Prop("integer", "Revit element id to inspect."),
                    ["include_type_params"] = Prop("boolean", "Include type parameters in addition to instance parameters (defaults to true)."),
                },
                "element_id")));

        // --------------------------------------------------------------- interop

        tools.Add(Tool(
            "export_ifc",
            "Export the Revit model to IFC format at the given path. Supports IFC2x3 and IFC4; "
            + "optionally filter by view to export only visible elements.",
            Schema(
                new JsonObject
                {
                    ["file_path"] = Prop("string", "Output file path (must end in .ifc)."),
                    ["ifc_version"] = Prop("string", "\"IFC2x3\" (default) or \"IFC4\"."),
                    ["export_base_quantities"] = Prop("boolean", "Include IFC base quantities (defaults to true)."),
                    ["view_name"] = Prop("string", "Export only elements visible in this view (optional)."),
                },
                "file_path")));

        tools.Add(Tool(
            "link_file",
            "Link or import an external file (DWG, DXF, DGN, or RVT) into the Revit model. "
            + "Linked files maintain a live connection to the source; imported files are "
            + "embedded copies. Placement offset is in millimeters.",
            Schema(
                new JsonObject
                {
                    ["file_path"] = Prop("string", "Path to the file to link or import (DWG, DXF, DGN, or RVT)."),
                    ["mode"] = Prop("string", "\"link\" (default, maintains connection) or \"import\" (embeds a copy)."),
                    ["position"] = Point("Optional placement offset in millimeters."),
                },
                "file_path")));

        // -------------------------------------------------------------- document

        tools.Add(Tool(
            "save_document",
            "Save the active Revit document to disk so work persists. If file_path is given (or "
            + "the document was started from a template and never saved), performs Save As to "
            + "that path; otherwise saves in place. Use before closing or restarting Revit.",
            Schema(
                new JsonObject
                {
                    ["file_path"] = Prop("string", "Full .rvt path to save to, e.g. \"C:\\\\Models\\\\ESB.rvt\" (required the first time a template-based model is saved)."),
                    ["overwrite"] = Prop("boolean", "Overwrite an existing file at that path (defaults to true)."),
                })));

        // ---------------------------------------------------------------- detail

        tools.Add(Tool(
            "create_detail_line",
            "Create a detail line in a Revit view for annotation. Detail lines are "
            + "view-specific 2D elements — they only appear in the view where they are created "
            + "(unlike model lines) and must go in a plan, section, or detail view. Coordinates "
            + "in millimeters.",
            Schema(
                new JsonObject
                {
                    ["start_point"] = Point("Line start point in millimeters."),
                    ["end_point"] = Point("Line end point in millimeters."),
                    ["view_name"] = Prop("string", "Target view name (optional, defaults to the active view)."),
                    ["line_style"] = Prop("string", "Line style name (e.g. \"Medium Lines\"); uses the default style if omitted."),
                },
                "start_point", "end_point")));

        // ----------------------------------------------------------------- clash

        tools.Add(Tool(
            "check_clashes",
            "Detect hard clashes (geometric interferences) between model elements — e.g. ducts "
            + "through beams, pipes through walls. With no categories, a default physical scope "
            + "is checked against itself; with only set_a, set_a is checked against itself; with "
            + "both, set_a is checked against set_b (cross-discipline). Categories accept "
            + "BuiltInCategory ids (\"OST_DuctCurves\") or friendly aliases (\"ducts\", "
            + "\"pipes\", \"beams\", \"walls\"). Clash locations are reported in millimeters.",
            Schema(
                new JsonObject
                {
                    ["set_a_categories"] = StringArray("First element set, e.g. [\"beams\", \"OST_StructuralColumns\"] (optional)."),
                    ["set_b_categories"] = StringArray("Second element set to check set_a against, e.g. [\"ducts\", \"pipes\"] (optional)."),
                    ["max_clashes"] = Prop("integer", "Maximum clashes to return (defaults to 200)."),
                })));
    }

    // -------------------------------------------------------- schema helpers
    // Private copies of Program.cs's helper shapes (Program's are private to it).

    private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
    };

    private static JsonObject NoArgsSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false,
    };

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    /// <summary>Object schema with the given properties; "required" is emitted only when non-empty.</summary>
    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Length > 0)
        {
            var req = new JsonArray();
            foreach (string name in required)
            {
                req.Add(name);
            }
            schema["required"] = req;
        }

        schema["additionalProperties"] = false;
        return schema;
    }

    /// <summary>Attach a description to an already-built object schema (fluent, for nested schemas).</summary>
    private static JsonObject WithDescription(this JsonObject schema, string description)
    {
        schema["description"] = description;
        return schema;
    }

    /// <summary>3D point: {x, y, z} — all coordinates in MILLIMETERS.</summary>
    private static JsonObject Point(string description) => new()
    {
        ["type"] = "object",
        ["description"] = description,
        ["properties"] = new JsonObject
        {
            ["x"] = Prop("number", "X coordinate in millimeters."),
            ["y"] = Prop("number", "Y coordinate in millimeters."),
            ["z"] = Prop("number", "Z coordinate in millimeters."),
        },
        ["required"] = new JsonArray { "x", "y", "z" },
        ["additionalProperties"] = false,
    };

    /// <summary>2D point: {x, y} — coordinates in MILLIMETERS.</summary>
    private static JsonObject Point2D(string description) => new()
    {
        ["type"] = "object",
        ["description"] = description,
        ["properties"] = new JsonObject
        {
            ["x"] = Prop("number", "X coordinate in millimeters."),
            ["y"] = Prop("number", "Y coordinate in millimeters."),
        },
        ["required"] = new JsonArray { "x", "y" },
        ["additionalProperties"] = false,
    };

    /// <summary>Direction vector: {x, y, z} — unitless components (NOT millimeters).</summary>
    private static JsonObject Vector(string description) => new()
    {
        ["type"] = "object",
        ["description"] = description,
        ["properties"] = new JsonObject
        {
            ["x"] = Prop("number", "X component (unitless direction)."),
            ["y"] = Prop("number", "Y component (unitless direction)."),
            ["z"] = Prop("number", "Z component (unitless direction)."),
        },
        ["required"] = new JsonArray { "x", "y", "z" },
        ["additionalProperties"] = false,
    };

    /// <summary>Array schema with the given item schema.</summary>
    private static JsonObject ArrayOf(JsonObject items, string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = items,
    };

    private static JsonObject StringArray(string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new JsonObject { ["type"] = "string" },
    };

    private static JsonObject IntArray(string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new JsonObject { ["type"] = "integer" },
    };

    /// <summary>Free-form dictionary parameter (Python Dict[str, Any]).</summary>
    private static JsonObject Dict(string description) => new()
    {
        ["type"] = "object",
        ["description"] = description,
        ["additionalProperties"] = true,
    };
}
