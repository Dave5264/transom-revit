---
name: transom-model-01-project
description: Stage 1 of transom-model. Creates the Revit model for a CAD-to-Revit render model - new file from a template, the elevation datum (internal Z 0 = main floor, levels reading their true elevations), levels and their plans, outside elevation markers with full crops, SA ISO 3D views, optional worksharing - all by script, verified on 2026-11-11. Use when starting a model from CAD drawings, when levels read the wrong elevation, or before linking any CAD.
hooks:
  PreToolUse:
    - matcher: "mcp__transom__execute_revit_code"
      hooks:
        - type: command
          command: 'powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME/.claude/skills/transom/hooks/transom_hooks.ps1" revit-pre'
    - matcher: "mcp__transom-acad__.*"
      hooks:
        - type: command
          command: 'powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME/.claude/skills/transom/hooks/transom_hooks.ps1" acad-pre'
    - matcher: "Bash|PowerShell"
      hooks:
        - type: command
          command: 'powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME/.claude/skills/transom/hooks/transom_hooks.ps1" shell-pre'
  PostToolUse:
    - matcher: "mcp__transom__execute_revit_code"
      hooks:
        - type: command
          command: 'powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME/.claude/skills/transom/hooks/transom_hooks.ps1" revit-post'
    - matcher: "Bash|PowerShell"
      hooks:
        - type: command
          command: 'powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME/.claude/skills/transom/hooks/transom_hooks.ps1" shell-post'
---

# Stage 1: the model, the datum, the levels, the views

Part of `transom-model`. Every script below runs through `transom/scripts/run_file.cs` with `ARGS { CFG }`.
These steps were recreated in a fresh project on 2026-11-11 and documented as they ran. The traps below are what that
run hit.

## Inputs

`model_config.json` (template in `transom/reference/`): `model_title`, `model_path`, `template`, `datum_ft`,
`levels` (name, true elevation, plan view name, bottom up, including the level above the top walls), `building_box_in`
and `site_box_in` ([xmin, ymin, xmax, ymax], model inches), `work_dir`.

Where the levels come from: the level datums on the drafted elevations (title blocks, "EX" = existing to remain). On
the first project: LOWER 90'-0", MAIN 100'-0", UPPER 111'-6", ROOF 120'-10 1/2". The elevation drawing's Y = 0 was MAIN.

## Steps

1. **New model**: `scripts/new_project.cs`. Creates the file from the template, saves it to `model_path`, and opens it
   as the active document (`uiapp.OpenAndActivateDocument` works from the bridge). Never overwrites an existing file.
   The first project instead began as a stripped copy of an office project (281 unplaced rooms and wrong levels
   purged). Use the office template if there is one: it brings view templates, line styles and title blocks.
2. **Datum and levels**: `scripts/levels.cs`. What the first project's model holds, and this script reproduces exactly:
   - internal Z 0 = the main floor. Every script in these skills assumes it: LOWER -10 ft, MAIN 0, UPPER 11.5,
     ROOF 20.875 internal.
   - the Project Base Point at the internal origin, shared elevation 100'. The Survey Point therefore sits at internal
     Z -100.
   - every level type's Elevation Base = Survey Point, so a level at internal 0 reads 100'-0".
   - one floor plan per level, named `<LEVEL> - PLAN`.
3. **Views**: `scripts/views.cs`. NORTH/SOUTH/EAST/WEST ELEVATION from one marker per facade, placed 30 ft outside the
   building box, crop 10 ft past each side and from the lowest level - 6 ft to the top level + 16 ft, far clip off.
   SA ISO - NE/NW/SE/SW: isometric, Shading with Edges, Medium, section box = the site box. Template elevations are
   renamed "(template)", never deleted.
4. **Worksharing (optional)**: `scripts/worksharing.cs` with `CENTRAL` = the central path. Enables worksharing (Shared
   Levels and Grids, Workset1), creates `Site` and moves site elements onto it, saves as central. The first project was
   workshared, with a local copy per user. A demo can stay a single file. The hook asks before the save.
5. **Log it** in `MODEL PLAN.md`: model path, the datum, each level's internal Z, the view ids.

## Checks

- `levels.cs` prints each level's internal Z and what it reads: they must match the drafted datums.
- Run `transom-model-02-cad/scripts/check_elevation_views.cs` once the plans are linked. Every elevation must PASS:
  marker in front of the building, crop wide and tall enough, far clip off.

## Traps (each one hit on the first project or its re-creation)

- **The Project Base Point's Elev parameter moves the base point itself.** Setting it to 100 sent the point to
  internal Z 100 and the levels still read 0'. Move the shared coordinates instead:
  `ActiveProjectLocation.SetProjectPosition(XYZ.Zero, new ProjectPosition(ew, ns, datum, angle))`.
- **`Level.Elevation` and `LEVEL_ELEV` are measured from the level type's base.** With the base at the Survey Point,
  setting them to an internal value lands the level `datum` too low (-110 instead of -10). `Level.Create(doc, z)` takes
  INTERNAL z: on the first project `Level.Create(doc, 90)` made a level that read 190'. Move a level by the difference
  in `ProjectElevation` instead.
- **A template level whose plan is the active view can't be deleted** ("One or more of the elementIds cannot be
  deleted"). Reuse template levels nearest the wanted heights (rename and move them) and report the rest.
- **An elevation marker inside the building** cuts it like a section. Put one marker per facade outside the box.
- **A tight section box hides mesh elements it cuts** (Enscape planting stand-ins). Box the SA ISO views to the site.
- **Revit's Home screen and an empty window title are normal** until a document is open. The bridge needs an active
  document: launch Revit with a .rvt path.

## Replay

The same four scripts with the sandbox config. Verified 2026-11-11: levels at -10 / 0 / 11.5 / 20.875 ft internal,
reading 90' / 100' / 111'-6" / 120'-10 1/2", identical to the first project's model.
