---
name: transom-model-07-families
description: Stage 7 of transom-model. Families for a render model, by script - a parametric face-based trim board, PTAC wall grilles, a curb stop, a rail profile drawn pre-offset for a railing curb - built from Revit's own family templates, flex-tested, saved and loaded with placeholder materials; railing types (curb, Juliet bars) from a spec; every family option audited against the drafting; a model's families exported to a library. Use when a detail needs a family, when families must be rebuilt or carried to a new project, or when a family option renders wrong.
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

# Stage 7: families

Part of `transom-model`. Recreated in a new project on 2026-11-11: every family built and flex-tested with the same
results as the first time (trim board 4 face locks; flex 10x30x1, 60x5.5x0.75, 4.5x48x0.5 exact; grilles 42 x 15.6 x 1.5
with 9 solids and 42 x 18 x 1.5 with 10; curb stop 72 x 6 x 4; curb profile usage Railing).

## Rule: families are design decisions

Every visual option on a family or type in use gets a decision, traced to a drafted unit and to what makes sense for
the condition. A default left on is a fault: the first project shipped a sill and apron on every window of a building
that has neither, because the family came that way.

## Scripted families

`scripts/build_families.cs` with `ARGS { CFG, FAMILIES }`, `FAMILIES` = any of `trim_board, ptac_grille, curb_stop,
curb_profile`. Templates come from `family_templates` in the config, files go to `families_dir`. What each one is, and
why, is in the script header.


`SA Trim Board` is one parametric face-based family that covers corner boards, trim rings, bands that sweeps can't do, and
head trim. Built from `Generic Model face based.rft` with instance `Width`, `Height` and `Depth` and a type `Trim Material`, then
flex-tested. Its origin is the board's centre. Place it with `doc.Create.NewFamilyInstance(faceRef, centrePoint, wallDirection,
symbol)`, using the exterior face from `HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior)`: Width runs along the wall,
Height up, Depth out of the face. Grilles and curb stops are built the same way, with blades and frames as extrusions.

Pattern for a new scripted family (all four follow it):
1. `app.NewFamilyDocument(<template>.rft)`. Face-based for anything that sits on a wall face, Generic Model for a free
   object, Profile-Rail / Profile for a rail or sweep section.
2. One transaction **with a failure preprocessor**. The family document can raise dialogs too.
3. Reference planes, EQ dimensions to keep the origin centred, labelled dimensions for the instance parameters,
   extrusions locked to the planes (`NewAlignment` on each side face).
4. Material parameters associated with each solid (`AssociateElementParameterToFamilyParameter(MATERIAL_ID_PARAM)`).
5. Flex-test: set extreme values, read the bounding box, end on the default.
6. `SaveAs` to `families_dir`, then `Close(false)`.
7. Load and give each type its placeholder materials: `LoadFamily` with an `IFamilyLoadOptions` (see the traps), then
   `symbol.LookupParameter("Trim Material").Set(material)`.

## Profiles for sweeps and fascia

- Base band: duplicate a `Dimension Lumber-Profile` type (b = projection, d = height), e.g. `SA 1/2 x 4 1/2`.
  The profile is centred on its insertion point.
- Roof fascia and caps: duplicate a `Fascia_Flat` profile type to the board size (`SA 1.5 x 7.875`, `SA 1.7 x 4.14`,
  `SA 1.6 x 7.52`). The fascia type class is `Autodesk.Revit.DB.Architecture.FasciaType`.

## Railing types

`scripts/railing_types.cs`: `MODE=dump` writes a model's railing types to a spec, `MODE=build` creates them (`SUFFIX`
for a test copy). Verified 2026-11-11: the curb and the Juliet rail rebuilt from the spec identical to the originals.
- Curb: no top rail, no handrails, no balusters or posts, ONE non-continuous rail at height 0 and offset 0 with the
  curb profile and the curb material, Rail Connections = Weld, Angled Joins = No Connector.
- A handrail position can only be set while that handrail exists ("The rail has no secondary hand rail").

## Audit the families against the drafting


Before the first placement, and again for any new family. This isn't a formality: on the first project every window shipped with a sill and apron the
building doesn't have, because the family's default was left on.

1. Plot one drafted unit of each kind (window, pair, French door, slider, entry door) from the elevation linework.
2. Run `family_audit.cs` to list every visual option. It works before anything is placed if you name the families.
3. **Render each option that changes the look,** in a temporary view with the option set in turn, inside a TransactionGroup that is rolled back. Compare
   each with the drafted unit, and pick. Ask whether it makes sense for the condition, not only whether it matches: a window in lap siding has no projecting
   stone-style sill, and a full-lite door at the floor has no apron.
4. Write the decisions and reasons into `family_options.cs` and the plan file. Run it after every `place.cs`, because placing resets instance parameters.

What the first project's audit decided:

| Option | Default | Decision | Why |
|---|---|---|---|
| Windows: `Exterior Trim Visibility` | Yes, `with Sill` | No | Drafted with no projecting sill or apron. The user's ruling |
| Glass Panel Material | `+Exterior Glass` on some types, `_Glass Windows` (0 transparency) on others | One glass across the building | The second read as opaque panels |
| Door: `Panel Type` | `F`, flush | `FG` | The door is drafted with a tall 23.5" x 67" lite. `G` and `N` were far too small |
| Door: `Function` | Interior | Exterior | It's an exterior door |

The user answered the open question (2026-11-07). The plain rectangle about 4.5" outside each drafted window group is a flat trim band
around the whole group. There is none between mulled units, so a per-window `Picture Frame` option would draw trim that isn't there. The
hatched blocks under the windows of the door groups are PTAC grilles. Neither is a window option: model both as separate elements, and
look for both on every facade during the audit.

`scripts/family_audit.cs` lists every visual option of the families in use. `scripts/family_options.cs` sets them.

## Carrying families to another project

`scripts/export_families.cs` (`ARGS { TARGET, OUT }`) saves every loadable family the model uses (placed, profiles of
railing, fascia and sweep types, baluster and post families) to a library folder. EditFamily opens a copy, so the model
is untouched. The first project's 60 families are in `Revit Modeling\Families\library\`, including the Enscape asset
families. Types made in the project (the window types) come along in the export.

## Traps

- **`LoadFamily` without `IFamilyLoadOptions` silently returns false** when a nested shared family (window trim) is
  already in the project. Both window families went missing on the first replay. Use load options that keep the
  project's nested copy.
- A family transaction needs its own failure preprocessor (the revit-pre hook refuses one without).
- `OfClass(typeof(Fascia))` throws: collect fascias with `OfCategory(OST_Fascia)`.
- Never leave a test family loaded under the real name. Build test copies with a suffix and delete them.
