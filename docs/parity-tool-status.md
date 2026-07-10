# Parity-tool live test status — v1.6.0

Live-tested 2026-07-10 against `test_doblackMR4M2.rvt` (workshared local, Revit 2025.4, ~34k elements)
through the full Claude Code → shim → bridge path. Core focus for shipping is the schedule
export/import feature set; the periphery tools below marked DISABLED are gated in
`ParityTools.cs` (not advertised) and `BridgeToolsDispatch.cs` (polite refusal) until reviewed.

## Core tools — all verified working ✅

`status` (reports 1.6.x), `list_schedules`, `read_schedule`, `set_parameter`, `set_parameters`
(both verified writes incl. the new rollback-on-timeout transaction path), `execute_revit_code`
(read-only and transactional modes).

## Parity tools verified working (35) ✅

- Views/annotation: `list_revit_views`, `get_revit_view`*, `get_current_view_info`,
  `get_current_view_elements`*, `set_active_view`, `create_view`, `create_sheet`,
  `create_detail_line`, `create_dimensions`, `tag_walls` (779 tags), `tag_elements`
  (both flagged tag overloads work live)
- Elements/analysis: `get_revit_model_info`, `list_family_categories`, `list_levels`,
  `list_category_parameters`, `get_element_properties`, `get_selected_elements`,
  `modify_element`, `delete_elements`, `transform_elements` (move/copy/rotate/mirror)*,
  `ai_element_filter`, `analyze_model_statistics`, `export_room_data`,
  `get_material_quantities`, `color_splash`*, `clear_colors`
- Creation/MEP/interop: `create_level`, `create_grid`, `create_line_based_element`,
  `create_surface_based_element`, `create_structural_framing`, `create_room`,
  `create_room_separation`, `create_duct`, `create_pipe`, `create_mep_system`,
  `create_schedule`, `link_file` (DWG link verified; flagged import path works)

\* = works with a known limitation, see "Improvements" below.

## DISABLED pending review (7) ⛔

| Tool | Why | Root cause / fix sketch |
|---|---|---|
| `check_clashes` | Times out (>30 s) on realistic scopes (1,678 walls) | Runs `ElementIntersectsElementFilter` collector once per set-A element (`BridgeToolsElements.cs` ~1362). Add a `BoundingBoxIntersectsFilter` quick-filter prefilter and/or restrict set-A collector to the intersection candidates. |
| `load_family` | Silently fails; reports `already_loaded` when nothing loaded | `doc.LoadFamily` returns false OUTSIDE a transaction in this doc but works INSIDE one (verified live). The "manages its own transaction" comment at `BridgeToolsElements.cs:311` is wrong here — wrap in `InTransaction`, and distinguish load-failure from already-loaded (check family presence before/after). |
| `place_family` | Unusable via MCP — arg contract mismatch | Shim schema advertises flat `x`/`y`/`z`; bridge requires `location:{x,y,z}` (`BridgeToolsElements.cs:351`). Placement logic itself verified working via direct HTTP call. Fix bridge to accept both shapes (bridge-side fix avoids a shim redeploy). |
| `list_families` | Ignores `contains` and `limit` args (returned 50 unfiltered on `contains:"door", limit:10`) | Arg-name mismatch in `ListFamilies` — align with advertised schema. |
| `export_document` | PNG output is a ~150 px thumbnail — `resolution` (DPI) is applied as pixel size. PDF verified working. | Fix `ImageExportOptions` mapping (use `ZoomFitToPage`/`PixelSize` correctly), then re-enable. |
| `export_ifc` | Exceeds the 30 s bridge timeout (the export itself completed — 64 MB IFC written after the waiter gave up) | Not broken, just slow. Needs a per-tool timeout budget (see below) or async job pattern. |
| `save_document` | Deliberately untested (test model is workshared; policy: never save/sync during tests) | Review worksharing behavior (Save vs SynchronizeWithCentral) before enabling. |

## Cross-cutting issues found

1. **30 s bridge timeout vs heavy tools** (`BridgeEventHandler.RunOnRevitThread` default 30000 ms):
   clash detection, whole-model IFC export, and large view renders can all legitimately exceed it.
   Consider a per-tool timeout map (e.g. 120 s for exports/clashes). Note the timed-out operation
   KEEPS RUNNING in Revit and serializes subsequent requests behind it.
2. **`execute_revit_code` cold start**: first call in a session times out (>8 s `MaxRun`) while
   Roslyn JITs; the retry succeeds instantly. Exclude compile time from the run cap or warm up
   Roslyn in the background when the bridge toggles on.
3. **Oversized responses flood the MCP client**: `get_current_view_elements` returned 1.9 MB on a
   full floor plan (needs `max_elements`/summary cap); `get_revit_view` returns base64 in JSON
   (~140 KB for one sheet — consider MCP image content or write-to-file-and-return-path).
4. Bridge requests are serialized on the Revit API thread — a slow call starves parallel callers
   into the 30 s timeout. Consider documenting per-tool expected cost in descriptions.

## Improvements (working tools, minor)

- `transform_elements` mirror: creates the mirrored copy but does not report `new_element_ids`
  (copy does). Also consider `mirrorCopies:false` semantics.
- `color_splash`: type-level parameters (e.g. "Type Name") resolve as `None` for every element —
  fall back to the element type when the instance lacks the parameter.
- `delete_elements`: on failure, report WHICH id was undeletable and why (hit: active view;
  pinned import instance). Error currently is just "ElementId cannot be deleted".
- `ai_element_filter`: message pluralization ("Found 230 doorss").
- `create_schedule` `fields_not_found` is good; consider mapping "Level" → "Base Level" for columns.

## Remaining to test

- App-side (ribbon) schedule Excel **export → edit → import** round-trip on this machine —
  the reliability pass touched NPOI disposal, ScheduleLoadEventHandler dialog handling, and the
  "file is open" message; needs one manual (or UI-assist-driven) smoke pass.
- Revision Narrative confirm-project-info dialog (item 4 of the v1.6.0 scope) — live pass.
- `link_file` with DXF/DGN/RVT modes and `import` mode (only DWG `link` verified).
- The 7 disabled tools, after their fixes.

## Release blockers (unchanged)

- SingleUser MSI can't build on this machine (no VS C++ workload; winget install fails 1602).
  Cut the release on the build machine per `CLAUDE.md` §3, or install the workload here.
