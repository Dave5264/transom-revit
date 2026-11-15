---
name: transom-model-10-landscape
description: Stage 10 of transom-model. Landscapes the site with Enscape placeholder assets copied from another model that the user opens as an asset library - inventory the assets, copy only the types used, design a new planting plan from the site (lawn and bed zones, trees by clearance, a foundation row, entry accents, seating), place everything on the surface below it, tag it, and check it in views boxed to the whole site. Use when asked to landscape or furnish the site, or to copy Enscape assets between models.
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

# Stage 10: the landscape

Part of `transom-model`. The user opened another of his models (another of the user's models, a cloud model on Autodesk Docs)
and asked for "a landscape for this building". Then: "you will need to change the landscaping design of course".
The library model is never written to.

1. **The bridge's `doc` is the ACTIVE document.** Once the user opens another model, every `doc` call goes to it. Resolve both
   models by title (`app.Documents`), write only to the target, and never start a transaction in the library model. Afterwards,
   check that `IsModified` is still false.
2. **Inventory.** Enscape placeholders are families named `Enscape AssetDefinition - <name>`, in the Planting category,
   one-level-based. Read their sizes from placed instances, or place one of each in a rolled-back TransactionGroup and read the
   bounding box. The target model may already hold some.
3. **Copy only the types you will use**: `ElementTransformUtils.CopyElements(src, symbolIds, target, Transform.Identity, opts)`,
   with an `IDuplicateTypeNamesHandler` that returns UseDestinationTypes. Run it in a transaction on the TARGET.
4. **Design from the site, with nothing taken from the source layout.** Map the plantable zones first (`planting_zones.py`): lawn =
   site minus paving + 8", walks, footprint, wells, stoops, fence, enclosure. Foundation beds = 54" off the walls, minus 60" door
   zones and 18" round the wells. Then (`planting_plan.py`):
   - trees placed by hand per zone, each checked for lawn, a wall clearance by size (London plane 15 ft down to Japanese maple 3 ft),
     distance from hardscape, and canopy overlap, and nudged within 7 ft if it fails;
   - the foundation row 30" off the wall: evergreen anchors at the corners, low shrubs under windows, a laurel-laurel-grass rhythm
     between, a lavender and cranesbill edge at 48" where the bed is deep, and a boxwood and lavender row at 17" in narrow strips;
   - entry accents, seating on the patio, benches facing the walks.

   The building's west lawn turned out 10 ft wide (the west lot reaches into the island), so it took slim birches, not maples.
5. **Placement.** `NewFamilyInstance(xy, symbol, level, NonStructural)`, then set `INSTANCE_ELEVATION_PARAM` to the surface under
   it. Get that surface from a downward `ReferenceIntersector` on the toposolid (`FindReferenceTarget.All`), or on the patio floor
   for furniture. Give plants a random rotation. Turn each seat so its back (the mean of its top 20% of vertices) faces away from
   the table or the walk. Tag every instance with Comments `LANDSCAPE <zone>`.
6. **Checking it.** In Revit the assets are white low-poly stand-ins; the real plants appear only in Enscape. A section box hides any
   mesh element it cuts, so trees whose canopies overhang the site edge vanish from a tight box. That is a view artifact: give
   the check views a box 300" bigger than the site.

## Scripts

`planting_zones.py` (lawn and bed zones, outward normals at the openings; writes `openings_out.json` and
`planting_zones.png`), `planting_plan.py` (the tree list by hand, the foundation row, edges, accents, seating; writes
`planting.json` and `planting_plan.png`), `asset_sizes.txt` (the measured sizes of each Enscape asset). Placement is the generic code in `transom-model/scripts/recipe_build.cs` (stage `instances`: point, rotation, elevation, comments).

## The result on the first project

217 instances tagged `LANDSCAPE <zone>`: 46 trees, 165 shrubs and perennials, patio seating, 2 benches. Boulder 3 moved
off the curb; all three boulders 6" into the grade.

## Replay

The Enscape families are in the family library (`recipe_build.cs` stage `families`). Stage `instances` places all 215
planting pieces and the 2 benches at their recorded points, rotations and elevations.
