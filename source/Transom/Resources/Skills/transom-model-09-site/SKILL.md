---
name: transom-model-09-site
description: Stage 9 of transom-model. The site the way the user models it - grass as a toposolid fitted to the drafted grade lines, asphalt and walks as simple graded floors (concrete flat or one constant slope) that excavate the ground, smooth curbs as railings with a curb profile hosted on the asphalt, paint and wheel stops seated on the paving, fences, enclosures, stoops and egress wells. Use when building or fixing a site for a render model, or when the site shows teeth, spikes, notches or rough curbs.
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

# Stage 9: the site

Part of `transom-model`. The user's words: "I usually just use floors for site modeling and generally match the grade
schematically instead of being exact." "Concrete is generally installed flat or at a consistent slope." "The surface
changes don't need to be that granular, they would not really be built that way anyway."

## The method


The user's method, adopted on the first project after the draped sub-division site looked rough: **grass is the toposolid; asphalt,
walks, patios and pads are FLOORS; curbs are railings with a curb profile.** Floors are simple shapes that approximate the grade the
way they would be built, not a copy of the TIN. Concrete goes in flat or at a consistent slope. The grass is graded to the floor and
curb edges, not the other way round. `site_floors.py` (project `site` folder) is the reference implementation; `site_build.py` below
still supplies the layout.

1. **Heights.** Check whether the plan has spot elevations or labelled contours. On the first project the DWG was flat and the
   contours were unlabelled, so `grade.py` fits a smoothing thin-plate spline G to every drafted grade line on the four elevations
   (finish lines where drawn, existing lines elsewhere, 6" outside the face, blended to a plane beyond 20-70 ft). Tell the user, and
   report any drafted grade step (a retaining wall) the smooth surface can't show.
2. **Layout.** `site_build.py` chains the pavement-edge polylines into an outer outline and the island round the building. Keep the
   CAD **arcs as true arcs** (fit a circle to each "Arc" polyline; residuals were under 0.001"), and snap gaps under 1" by moving a
   line end, never an arc. Walks stop at the back of the curb (pavement edge + 6").
3. **Toposolid limits.** Revit keeps at most **9,999 points** and silently thins the rest. The first site sent 15,739, and the
   thinned edge pairs made sawtooth teeth at every curb. Count before creating, and leave room for later edits.
4. **Ground.** One toposolid at G (36" grid), with NO building pit and NO well pits: point pits made notches and teeth at the stone
   base. The LOWER foundation slab excavates the building, and a gravel floor in each egress well excavates the well (step 7).
   Put grass points at G 1 1/2" outside the wall faces and the well boxes, every 12". Turn Toposolid Smooth Shading on
   (`Toposolid.SetSmoothedSurface(doc, true)`; Chaos/Enscape recommend it against faceting).
5. **Asphalt.** One floor: outline plus the island hole, true arcs. Shape points are the outline vertices, edge break points by
   Douglas-Peucker on the grade line (2" tolerance), and interior points only where a linear surface misses the grade by 3". That
   was 93 points for 21,000 sf. `SlabShapeEditor.AddPoints` takes ABSOLUTE z. A point on an existing vertex fails ("Could not add
   point"), so set those with `ModifySubElement(vertex, z - originalTop)`, and commit `Enable()` before reading the vertices.
6. **Concrete.** Each floor in runs of constant slope along its long axis (a crease only where the grade line bends by more than
   1"), cross slope at most 2%. A patio is flat at 1.5%, falling away from the building. Set the corners from that function on the
   floor's ACTUAL vertices, and put the crease points 1" inside the edges, so a slightly different outline still holds them.
7. **Excavation.** Walks, patio, pad, stoops, the foundation slab and the well floors excavate the toposolid with
   `toposolid.ExcavateBy(floor.Id)` (Revit 2025). It takes about 20-30 s each on a big toposolid, so log to a file and poll.
   - It removes the ground down to the floor's UNDERSIDE within its footprint.
   - It fails with "Can't cut joined element" when the ground's surface nearly coincides with the floor's top. Keep the ground under
     every concrete floor 3" below its top (a 30" grid inside the footprint), and walks cut every time.
   - The asphalt never cut. That held for the 21,000 sf ring, each 10,000 sf half, and a flat 1,100" x 450" test rectangle that
     crossed the grade. The 4,311 sf foundation slab cut fine, and removing overlapping toposolids, stops and paint did not help.
     So under the asphalt the ground is shaped instead:
   - the toposolid under the asphalt drops to G - 20 (a 72" grid is enough there);
   - low points 1" in from the asphalt edge every 12", at that edge's asphalt height - 14";
   - curb-line points 3" out from the edge (the curb's centreline) every 7", at asphalt edge + 5 1/2".

   The step from the deep zone up to the grass then happens inside the curb's footprint, where the solid curb hides it. Where there
   is no curb (an enclosure opening, wells at the edge), put a low point 6" in at asphalt - 6 1/2" and a flush point 1/4" out.
8. **Dense points beside an excavating walk broke its excavation** while the ground under the walk still sat near its top (step 7).
   Within 2 ft of a walk keep the curb-line points sparse. The walk covers the grass behind the curb there.
   - **Walks that meet a curb are pinned to the curb top** (asphalt edge + 6"). A walk that runs along the curb takes the curb's
     profile. An end-on walk gets a ramp between its ends, plus a cross slope fitted to the curb (up to 5%). Everything stays within 0.6".
   - **Grass edge points every 12"**, 1 1/2" outside each concrete floor, 1/2" below it. At 36" the grass dipped to each point in a V
     wherever the grade sat above the walk.
   - **Building and wells.** The LOWER slab sat about 3 1/2" inside the MAIN slab outline, so at LOWER windows the uncut strip of ground
     in the wall band showed through the glass. Run each well's gravel floor 8" into the building, past the slab edge, and out to the
     well walls' centrelines, so the cut faces hide inside the 2" walls instead of z-fighting with their inner faces.
9. **Curbs.** A railing type with no top rail, handrails, balusters or posts, and one non-continuous rail with a curb profile family
   (built by script from `Profile-Rail.rft`, as detail lines in the Ref. Level view). Rail Connections = Weld, Angled Joins = No
   Connector. **Host the railing on the asphalt floor.** A hosted railing follows the floor's shape-edited surface exactly (measured
   within 0.02"); hosted on the toposolid it follows the triangles and zig-zags. A rail Height or Offset fails ("Can't create
   Railings"), so draw the profile pre-offset:
   - origin on the path, 1/2" in from the asphalt edge;
   - face 1/4" over the asphalt, back at edge + 6";
   - top 6" above the asphalt, bottom 12 1/2" below it;
   - a 1/2" round on the face edge and 1/4" on the back.

   A slab edge can't go on a shape-edited floor's curved edges: they become HermiteSplines and `NewSlabEdge` throws.
10. **Paint and stops sit on the asphalt as built.** Read the floor's top under each one with a downward `ReferenceIntersector`
    (target the asphalt id, `FindReferenceTarget.Face`). Rebuild the paint DirectShapes 0.2" proud and 0.05" embedded, and move
    each stop to sit 0.1" into the asphalt.
11. **Fences** are 1" walls in 10 ft segments stepped to the grade. Enclosures are walls on the drafted lines. **Egress well** tops go
    4" above the grade; check every LOWER window against it.
12. **Housekeeping:**
    - Move the superseded toposolid onto a workset hidden by default (its sub-divisions follow it).
    - Check `WorksetDefaultVisibilitySettings`: an inherited "Site" workset hidden by default makes every new view lose the site.
    - Turn the contour lines off with `ToposolidType.GetContourSetting()`, `DisableItem` on each item, then `SetContourSettting`.
    - To remove toposolid points, match vertices by a nearest search, not rounded keys, and never against a list that also holds
      new points at the same positions (that deleted 2,479 new low points once). Never delete the toposolid's own boundary points.

The first version of this site (sub-divisions draped on a toposolid with 0.15" edge pairs) is superseded. `site_subdivide.cs` below
is kept for reference only.

## Scripts

| Script | Does |
|---|---|
| `grade.py` | thin-plate spline G fitted to every drafted grade line on the four elevations |
| `site_build.py` | site layout: pavement edge chained into the outer outline and the island, true arcs, walks, pads, stops, striping |
| `site_floors.py` | the floors version: asphalt outline and shape points, concrete in constant-slope runs, curb paths, ground edits; writes `site_floors.json` |
| `site_fix2.py` | walks pinned to the curb tops, well boxes, ground 3" under the concrete, grass edge points; writes `site_fix2.json` |
| `contour_levels.py`, `curb_dense.py`, `curb_sparse.py` | the ground-point experiments along the curbs that settled the final rule |

The C# that applied each step (toposolid, floors and shape points, excavations, curb type and railings, paint and stops
reseated, wells) is generic in `recipe_build.cs` (stages `topo`, `floors` with shape points, `excavate`, `rails` re-hosted on a floor, `shapes`): build the recipe rows from these scripts' output, or reuse the code.

## Replay

`recipe_build.cs` stages `floors` (site floors with their shape points: the asphalt's 93), `topo` (the grass
toposolid, 6,871 points, smoothing on), `excavate` (the 19 floors that cut it: about 10 s each, logged to
`replay_log.txt`), `rails` (the 4 curbs re-hosted on the replayed asphalt), `shapes` (striping, aisle hatch, boulders,
north box). All 19 excavations succeeded on the 2026-11-11 replay.
