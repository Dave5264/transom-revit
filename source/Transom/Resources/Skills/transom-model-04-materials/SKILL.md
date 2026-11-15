---
name: transom-model-04-materials
description: Stage 4 of transom-model. Materials and textures for a render model that Enscape will render on another machine - texture maps copied into the project and checked for brightness, a placeholder material per surface kind (every site portion its own) with its own Generic appearance, colour and bump maps at real-world size, built from a spec so a plain template renders like the reference. Use before creating wall, floor and roof types, when materials render wrong, or when textures go missing on another machine.
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

# Stage 4: materials and textures

Part of `transom-model`. Recreated in a new project on 2026-11-11 and documented as it ran. Materials come BEFORE the
types (stage 5), because a type built on a missing material renders as `<By Category>`.

## Rules from the user

- A placeholder material for every kind of surface, with a best-guess colour and a matching texture, so the user can
  drop in his own textures later.
- Every portion of the site gets its own material, so each renders and can be retextured: asphalt, curb, walk, patio,
  existing pad, stoop, wheel stop, paint (white, blue), grass, fence, CMU, boulder, egress well, well gravel, site
  concrete wall.
- Stone and shingle textures matched to the site photos. The siding render texture stays at about 5" courses (tile
  70.75") although the modelled pattern is 7": don't offer to change it.
- Enscape is not on the modeling machine. Set textures up in Revit (Generic appearances, local maps) for Enscape on his.

## Steps

1. **Maps**: copy the maps out of the Autodesk library into `Revit Modeling\Textures` and measure them:
   `python scripts/textures.py copy <library> <Textures> <files...>`, then `textures.py check <Textures>`. Correct
   any outlier (`darken`, `tone`), make missing bump maps (`bump`), and write `textures.json` (`manifest`, then set each
   real-world size). Add a `README.txt` saying what the folder is and how to retarget a map.
2. **Spec**: a materials spec lists name, class, shaded and diffuse colour, colour map and bump map (path, real-world
   width and height in inches), image fade, glossiness, reflectivity. For a replay use `reference\materials.json`
   (written by `dump_materials.cs` from the finished model). For a new project, start from it and change names,
   colours and maps.
3. **Build**: `scripts/materials_from_spec.cs` with `ARGS { CFG, SPEC }`. Each Generic material gets its own
   appearance asset, duplicated from a Generic asset in the model or created from the library "Generic" asset. Shared
   assets are never edited in place. Rerunning it updates.
4. **Look**: a Realistic export of a close-up of each surface. An image nobody looked at checks nothing.

Verified 2026-11-11: 32 materials (stone, shingles, siding bump, the 17 site materials, trim, fascia, rails, grilles)
built in a project from the default template, with maps and sizes identical to the reference.

## Traps

- **Never create non-Generic materials (Prism, Metal, Wood, WallPaint) in this step.** A family brings its own
  definition when it loads, and a project material of the same name wins. An opaque placeholder named
  `+Exterior Glass` would have made every window opaque. A host type that needs one gets a plain placeholder from
  `types_from_spec.cs`.
- **Direct shapes carry materials per face** (the parking striping): `dump_materials.cs` collects them from the faces.
  The first replay missed the two paint materials until it did.
- **A shaded colour is not the render colour.** Set both: `Material.Color` for shaded views, `generic_diffuse` for
  renders. `UseRenderAppearanceForShading = false`.
- Library map paths like `1\Mats\...` are relative to the Autodesk library and resolve on any machine with it. Local
  copies need the Textures folder to travel with the model.

## How the first project did it (and the measurements behind the rules)


Use a Generic appearance per material, edited in `Autodesk.Revit.DB.Visual.AppearanceAssetEditScope`. For a placeholder, duplicate
both the material and its `AppearanceAssetElement`, then set `common_Tint_toggle`/`common_Tint_color` or `generic_diffuse`.
**Measure every map's mean brightness** before trusting it. The library "asphalt" map averages 171/255 and renders like concrete,
so a darkened copy was used. The stockade fence map averages 58 and renders black, so it was tone-mapped into a cedar range. Copies
go in the project's `Textures` folder, next to `textures.json`.

