---
name: transom-model-05-shell
description: Stage 5 of transom-model. Builds the building shell from the reconciled drawings - closed masses to prove the form, wall/floor/roof types from a spec, continuous walls on the reconciled planes (one base level per coursed material), inset slabs, footprint roofs from a roof table (joined, edges into the walls), two-tier square-cut roof edges with native fascia, walls attached to the roofs. Use after stage 3 is accepted, when walls show seams or steps at floor lines, or when roofs, slabs or edges render wrong.
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

# Stage 5: the shell

Part of `transom-model`. C# runs through `run_file.cs` with `ARGS { CFG, ... }`. Every script resolves the target by
`model_title`, runs its own transactions with a failure preprocessor, and is safe to rerun unless it says otherwise.

## Fast path

1. **Types**: `scripts/types_from_spec.cs` with `SPEC` = a types spec (`reference\types.json` for this building,
   written by `dump_types.cs`). Wall, floor, roof and toposolid types with exact layers, functions, materials, core
   boundaries and wrapping. Run stage 4 first. Verified 2026-11-11: all 21 types built in a plain project (lap siding
   6 5/8", asphalt shingle roof 11 1/8", flat TPO 13 1/4").
2. **Masses** (method only): `scripts/masses.cs` from `prisms.json`, comment `MASS CHECK`. Compare and look, then hide them.
3. **Walls**: `scripts/walls_from_runs.cs` with `RUNS` = the runs file and `LIMITS` = "xmin,ymin,xmax,ymax" (model
   inches) to build one mass at a time, "" for all. On the first project: `shell\data\runs.json` with
   `LIMITS -1e9,-1e9,332,1e9` (the west mass), then `runs_rest.json` with no limit. 57 walls. The finished model has
   58 after the hand fixes below.
4. **Slabs**: `slabs_from_loops.py` (stage 3) writes `slabs.json`. Create the floors on their levels with the
   building floor type (`recipe_build.cs` stage `floors` builds floors from loops; write slabs.json in its recipe format, or reuse its loop code). The LOWER slab is a `6" Foundation Slab`
   that later also excavates the site.
5. **Roofs**: `scripts/roofs_from_table.cs` with `TABLE` = the roof table (one row per roof: type, level, base offset,
   rafter/truss, eave cut, fascia depth, each footprint edge with slope flag, rise/run and offset). It joins the roofs
   that the table joins and writes `roof_ids.json`. Verified 2026-11-11: all 15 roofs of this building rebuilt with
   bounding boxes identical to the original to 0.000".
6. **Roof edges**: `roof_edges.py` sorts each roof's edges (exposed, into a wall, shared). `roof_edge_fascia.cs`
   pulls the exposed edges back by the board thickness and adds the native fascia (two square tiers).
7. **Attach**: `scripts/attach_walls.cs` with `SKIP_ROOFS` = comment prefixes of roofs that must never take a wall
   (`SHELL RIDGE`). Attach by overlap needs per-project SKIP rules: run cold on the first project's walls, it was
   refused ("Can't keep wall and roof joined") until the hand fixes were in. The replay attaches exactly what the
   finished model has (`recipe_build.cs` stage `attach`, from `Wall.GetAttachmentIds`).
8. **Checks**: stage 11, Check 1 (rules) and Check 3 (envelope, slots, see-through), then look from every corner.

## Hand fixes the first project needed (a recorded model keeps them, so its replay reproduces them)

- Six slots and a fin where a wall end met one wall low and another higher up: the end runs to the plane
  (`DisallowWallJoinAtEnd`, then set the curve).
- The tower east wall split at the shed, so no trimmed end shows (call 117).
- The west bay walls dropped to -22" and a 1" soffit floor under the bay (call 113).
- Junction walls J1-J7 on the spans the junction list gives (in `runs_rest.json`).
- Slabs rebuilt from the reconciled loops (call 117).
- Roof edges rebuilt as the drafted two square tiers, with the roofs moved in place by `SetGeometryCurve`
  (calls 188-211).

## Phase 9: Closed masses

Run `masses.cs`: one solid per prism, comment `MASS CHECK`. A solid cannot have a hole, so this proves the form before any detail
exists. Run the elevation comparison and look at it from all four corners and in cuts. When the user accepts the form, hide or
delete the masses (`MASS CHECK` finds them). `prisms.json` stays as the coverage reference.

## Phase 10: Wall types, then continuous walls

**10a. Wall types first.** Run `wall_types.cs`. Changing types later disrupts joins. It puts the exterior finish outside the core,
gives finishes finish functions, and wraps at ends and inserts. Unwrapped, every jamb and wall end carries `Default Wall`, which
shows as gray lines in a render. Materials go in the type, never in paint.

**10b. Walls.** Run `walls_from_runs.cs` with the Phase 3 transform. Limit it with `XMIN`..`YMAX` to build one mass and check it before
the next. It creates each run with its exterior face on the plane, Finish Face: Exterior as location line, the base on a level and
the top on the next level. The script's own notes cover direction and miters.
- A wall of a different material on the same plane (stone below siding) is a separate wall stacked exactly flush.
- **A bay that overhangs the wall below:** set its base offset to minus the slab's thickness so the bay's siding covers the slab edge,
  then give the underside a soffit. Left alone, the slab shows as a band of raw joist.
- Check joins **after** the transaction commits. Inside it, every end reads as free.
- Never `Wall.Flip()`: it moves an API-created wall a full thickness.
- **One base level per material.** Revit starts a wall's surface pattern at its base level's elevation. The base offset does not
  move it. Walls of one coursed type on different base levels shear the coursing at every joint and corner, unless the levels sit a
  whole number of courses apart. On the first project every wall based on UPPER (138", 5" off the 7" course) shifted the siding 30%
  of a course against its neighbours. A 2" offset change left that untouched. Moving the base level to MAIN removed it. The script
  bases every wall on its material's `DATUM` level and reaches the real base by offset. Rule check 6b tests it. Renders are expected
  to anchor textures the same way, but that was not tested in Enscape.
- **Top offset before base offset.** A wall whose base is set above its current top fails to regenerate.
- **When a wall's end meets one wall low down and another higher up, run that end to the plane.** The join Revit makes for the lower
  part also cuts back the upper part. That leaves a slot half or a full wall thick wherever the lower wall stops, or a fin where the
  end pokes past a corner. The first project had six slots and a fin after every other check had passed, and the user found two of
  them by looking. Fix: `WallUtils.DisallowWallJoinAtEnd(w, end)`, `doc.Regenerate()`, then set `LocationCurve.Curve` with that end on
  the face plane it meets. Copy the other coordinate from the wall's own line, never a rounded typed value, or Revit warns "slightly
  off axis". `planes.py` already ends a collinear wall above a mitred inside corner on the plane. The slot check
  (`transom-model-11-checks` 3b) finds the rest.
- **A wall rising above a low roof comes down through that roof,** its base hidden in the roof's thickness. Based on the level line
  above the roof, it leaves a gap wherever the roof slopes below that line, and the floor edge shows through (first project, over the
  east bump).
- **Opposite-facing planes at one corner go on one plane** when they are within tolerance. A return 3/4" off the wall face above it
  left a fin at the corner.

## Phase 11: Slabs

The outline is the reconciled loop. **No slab face may render from outside:**
- A slab on grade sits inside its walls. A framed floor stops about mid-wall, 3¼" in from the exterior face (`slabs_from_loops.py`).
  At ½" its edge drew through the siding as a seam at every floor line. At 1¼" it still drew through a wall seen at an angle in the
  user's 1/8" hidden-line view.
- A bay box in `slabs_from_loops.py` must overlap the inset slab, or the bay drops off as a separate piece. The script stops when a bay
  or a cut leaves more than one piece.
- When a step collapses below ¼", merge its neighbours into one straight edge. Otherwise Revit warns "line slightly off axis".
- Stop floors at stair flights with `SketchEditScope` on the outer boundary (see `transom-model-06-openings` pitfalls).
- Floors and walls shouldn't be joined with the floor winning. If they are, switch the join order.

## Phase 12: Roofs

Build from the Phase 8 table, as a joined system. Then attach the walls.
- Footprint roofs with Rafter or Truss = Truss put the **underside** at the base height along each slope-defining edge. So base
  offset = drafted top at that eave minus the vertical thickness. Drafted roof depth sets the type's thickness (11.73" vertical is
  11 1/8" perpendicular at 4:12). Probe the top surface with a vertical ray afterwards.
- Asymmetric gables: per-edge `set_Offset(curve, ...)`. An eave that jogs with the wall as ONE footprint roof failed "Can't make
  footprint roof", so build one footprint roof per eave segment on the same planes and `JoinGeometryUtils.JoinGeometry` them. Their
  seams show in 3D: look at them, and use as few as the shape allows.
- Run a low roof's high edge to the wall it meets, even if the drawing stops it a few inches short.
- **Every footprint edge that meets a wall runs into it, to the wall's centerline.** A low roof built to its drafted edges stopped 2"
  and 5/8" short of its walls. That left a slit along both edges and a post where an attached wall ran past the footprint. Attach
  also needs the footprint to cover the wall's whole line, ends included. Where an overhang passes an outside corner of the wall
  above, notch the footprint on that corner's plane.
- `NewFootPrintRoof(curves, level, type, out mca)` needs `var mca = new ModelCurveArray();` first. A null throws.
- **The roof type's edge is what renders as fascia.** Its structure layer shows at every eave and rake. Set that material to the drafted
  fascia and soffit finish, or add Roof Fascia and Soffit where the drawings show them. Raw joist at the edges is a fault.
- **Read the drafted edge and build it.** Measure it where an elevation shows the eave in cross-section, the corner where an eave
  meets a rake (`edge_corners.py`). On the first project it was two square tiers: a 1 1/2" x 7 7/8" fascia board square to the slope,
  standing proud of a roof cut square through its full 11 1/8". Revit's cuts alone can't make that: Plumb Cut hangs a vertical face,
  and Two Cut - Square gives one square face and then a level soffit. Build it with `roof_edge_fascia.cs`:
  - Rafter Cut `Two Cut - Square` with Fascia Depth equal to the roof thickness is a pure square cut. The API's `EaveCutterType` has
    no Perpendicular value, and no other integer of `ROOF_EAVE_CUT_PARAM` gave one.
  - Pull every exposed edge back by the board's thickness, an eave by T·cos(slope) in plan and a rake by T, and raise the base offset
    by T·sin(slope) so the roof plane stays put.
  - A native Roof Fascia (`doc.Create.NewFascia`) on the exposed top edges, with a `Fascia_Flat` profile duplicated to the board
    size. On a square-cut eave Revit squares the board to the slope. On a rake it keeps the board vertical with its depth square to
    the slope. Both matched the drafting, and every drafted corner then sat within 0.1" of a modeled one.
  - Sort the edges first with `roof_edges.py`. An edge that runs into a wall or another roof gets neither the pull-back nor a board.
    An edge shared with another roof for part of its length (a jog end) needs that part split off, which means rebuilding the roof.
  - Move a footprint in place with `ModelCurve.SetGeometryCurve` on every line. That keeps wall attachments and joins. Delete the roof's
    automatic sketch dimensions first: with them, moving some lines and not others fails with "Base sketch for roof is invalid".
  - Where two roofs' boards meet at an inside corner without sharing an end, Revit can't mitre them and leaves a sliver about 2" across.

**Walls to roofs.** Run `attach_walls.cs` once the roofs are final. Gable ends extend up on their own. A face over a lower roof with
no wall below takes a wall based on that lower roof, **on the span the junction list gives**. After placing one, the rule check must
show no overlap: on the first project both misplaced infill walls lay directly on top of existing walls, and the holes they were meant
to close stayed open. If attach refuses with "Can't keep wall and target joined", reset the wall's profile and attach again. On the first project the
cause was a footprint that did not cover the wall's line and ends.

## Replay

`recipe_build.cs` stages `walls`, `floors`, `attach`, `fascias` rebuild the finished shell exactly (106 walls including
the site walls, 23 floors, 40 attachments, 11 fascias), after `types_from_spec.cs` and `roofs_from_table.cs`.
