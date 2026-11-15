---
name: transom-model-08-details
description: Stage 8 of transom-model. Exterior details read from the drafted elevations and placed by script - the base band and stone line, corner boards, window and door trim rings, flat-roof caps and two-tier canopy edges, ridge vent caps, PTAC grilles at the windows next to doors, Juliet rails re-seated on the trim face. Use after the shell and openings pass their checks, or when asked for trim, bands, caps, grilles or a model that reads like the drawings.
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

# Stage 8: exterior details

Part of `transom-model`. Everything here is read from the drawings first, scripted, tested on one instance, then
placed and checked. Families come from stage 7, materials from stage 4.

## Rules from the user who shaped this

- Use industry-standard sizes where the drawing has none. Trim stands 1/2" proud of the siding (5/4 trim over lap siding).
- Where the drawing shows a shape but not its height, model the shape as drawn, with a height equal to its shorter plan dimension.
- Place a detail only where it is drafted. The bay and some secondary corners had no drafted corner boards, so they got none.
- Make a placeholder material for every kind of surface, with a best-guess colour and a matching texture, so the user can drop in
  his own textures later. Give every portion of the site its own material so it renders correctly: asphalt, curb, walk, patio,
  existing pad, stoop, wheel stop, paint, grass, fence, CMU, boulder, egress well, site concrete wall.
- Build the site the way it would really be built. Pavement sits 6" below the adjacent grass inside a 6" concrete curb.
- Model hardscape as floors on a grass toposolid. Keep the floors simple: they approximate the grade, and concrete goes in flat or
  at a consistent slope. Curbs must look smooth (true arcs, a real curb profile).
- Check the light-and-material result in a Realistic export. An image nobody looked at checks nothing.

## Base band and stone line

1. Read the band's two horizontal lines per face from the drafting (e.g. -16 and -11.5). The stone top goes to the band top. The
   siding base comes down to the band top with its base LEVEL unchanged, so the surface-pattern courses stay aligned.
2. Put the band on the stone walls as a wall sweep: duplicate a `Dimension Lumber-Profile` type (`b` = projection, `d` = height) and
   a `Trim Board` sweep type, then `WallSweep.Create(wall, typeId, new WallSweepInfo(WallSweepType.Sweep, false) { ... })`.
   - The profile is centred on its insertion point.
   - The height that sticks is the instance parameter "Offset From Level" (`WALL_SWEEP_OFFSET_PARAM`), measured from the wall's
     base LEVEL. `DistanceMeasuredFrom.Top` was ignored. A sweep created in the same transaction as a base-offset change sits at
     the old base, so set the parameter afterwards.
   - Sweeps on joined walls wrap outside corners on their own.
3. A sweep stops at the inner face of a wall that owns the corner. Where that leaves a gap, use one face-based board for the whole
   face instead.
4. Read the bay and bump undersides against the band. On the first project the drafted bay bottom was the band top. The bay was
   raised: the slab sketch was edited in a `SketchEditScope` to drop the bump, and the bay got its own floor.
5. Where the drafting shows no band (a stair bay), run the siding down past grade.

## Corner boards

`corners.py` finds convex outside corners from `wall_faces.json` (two perpendicular siding faces whose vertical edges meet). Place
boards only at corners where the drafting shows a pair of vertical lines 4.8-6.2" apart running the corner's height. Two boards per
corner, 5 1/2" wide and 1/2" proud. The board on one face runs 1/2" past the corner to cover the other's edge. They run from the band
top to the face's top edge at the corner (the soffit).

## Window and door trim rings

`trim_rings.py` groups openings per host wall (gaps up to 7.5" are one group: mulled pairs and door + window). For each group it finds
the drafted rectangle round it: head, bottom and jamb lines ending on common corners. The rules for what the drafting leaves open
are in the script header:
- LOWER well windows get no ring.
- An entry door at the floor gets no bottom piece.
- A window that lost its grille gets a bottom equal to its head width.
- A unit hidden in the elevation copies its twin's ring.
- A side more than 1" off the others is read as a neighbour's line.

The ring minus its units, its grille blocks and the base band is cut into rectangles, and each rectangle is one `SA Trim Board`.

## Flat roof caps and two-tier canopy edges

Where the drawing shows a cap standing proud of a flat roof's edge, use a native Roof Fascia with a duplicated `Fascia_Flat`
profile set to the cap's thickness and height, on the roof's exposed top edges (the method of `roof_edge_fascia.cs`). Where a wall
rises to the cap, move the roof edge out to just behind the wall face (0.1"), so the cap stands proud of the siding and no faces
are coplanar. The fascia type class is `Autodesk.Revit.DB.Architecture.FasciaType`. Collect fascias with
`OfCategory(OST_Fascia)`, because `OfClass(Fascia)` throws.

## Ridge caps

Make two thin footprint roofs per ridge, with the long edges slope-defining at the main roof's pitch:
- a vent body (±19" here, its underside on the main roof plane);
- a cap board (±22.7", its underside 3.7" above the ridge).

Each has a single-layer type: `CompoundStructure.CreateSingleLayerCompoundStructure`, with the drafted vertical thickness × cos(pitch).
Set Two Cut - Square through the full depth (`FASCIA_DEPTH_PARAM` = thickness). For the base offset, put the underside at the edge
at ridge + gap - halfwidth × pitch; that landed exactly. Then measure the bbox top and correct by the difference.

## PTAC grilles

Only at windows next to a door: 14 of the 16 drafted PTAC blocks on the first project (the user's ruling). `ptac_find.py`
finds the drafted blocks, and `ptac_blocks.json` holds them. Each grille is an `SA PTAC Grille 42x15.6` or `42x18` on
the wall's exterior face, centred on the drafted block. Placing a face-based family on a wall face by point and direction is the generic code in `transom-model/scripts/recipe_build.cs` (stage `instances`).

## Juliet rails onto the trim face

Rails placed before the trim exist sit on the wall face. Once the trim rings are on, move each rail so its back sits on
the trim face, 1/2" proud, and make it black throughout (move the railing's path by the trim thickness along the wall's outward normal).

## Bridge traps

## Bridge traps (cost real time on the first project)

- **Every launch of Revit or AutoCAD shows an unsigned add-in prompt.** The bridge does not load until it is answered, so clear it
  before waiting on the port. Revit: `revit_find("Always Load")`, then `revit_click_by_id`. AutoCAD: the prompt is "Security -
  Unsigned Executable File" and "Always Load" is a Pane with no AutomationId. Find it with PowerShell UI Automation (descendants of the
  root, filtered by the acad.exe ProcessId) and click the centre of its BoundingRectangle.
- **"timed out waiting for Revit (request not applied)" usually means the request IS running.** Toposolid sub-divisions take about
  20 s each and a 15k-point toposolid takes 5 s. Poll `Get-Process -Id <revit>` until it is Responding, then read back what exists.
  Write a progress log to a file from inside long scripts, so the result survives the bridge timeout.
- **A write without a failure handler can raise a modal dialog that blocks the bridge.** Check `revit_list_dialogs` first; reading
  the dialog's text through UI Automation tells you whether OK or Cancel is safe. The inner transaction of a `SketchEditScope` needs
  its own failure handler too. Collinear sketch lines raise "Highlighted lines overlap", which is harmless.
- An element left over from a rolled-back test can report "deleted in the Central Model" and refuse deletion. Leave it, and it
  clears at the next sync.

## Scripts

`corners.py` (convex corners from `wall_faces.json`), `trim_rings.py` (the drafted rectangle round each opening group),
`place_boards.cs` (boards from either file onto the wall faces), `looks.cs` (close-up 3D views in a rolled-back
transaction group), `missing_map.py`, `ptac_find.py`. Placing any recorded family instance (face-based or not) is the generic code in `transom-model/scripts/recipe_build.cs`.

## Replay

`recipe_build.cs` stages `instances` (292 face-based boards and grilles re-hosted on the same wall faces), `sweeps`
(the base band), and the ridge caps come back as roofs from the roof table.
