# icons-v2 — Post-Build Icon QA Checklist (ux1 → revit1)

Date: 2026-06-11. Purpose: verify the new distinct ribbon icons on screen **after the next
build** (the v2 PNGs were wired into source by code1 but the deployed `Transom.dll` from
20:27:44 predates them — they are NOT yet on the ribbon). Run this once the post-retest build
deploys and Revit is reopened. Owner of the visual judgement: ux1, from revit1's screenshots.

## What changed (the only buttons that should look different)

Three icons were re-pointed in `Application.cs`; everything else is unchanged.

| Button | Panel | Before | After (expect) | Wired at |
|---|---|---|---|---|
| **Settings** | Claude Assist | brand mark (repeat of Schedule Hub) | **white gear on brand-blue tile** | Application.cs:73-76 |
| **Revision Narrative** | Revision Tools | brand mark (repeat of Schedule Hub) | **white sheet + blue text lines + orange revision delta (corner)** | Application.cs:80-83 |
| **Claude UI Assist** | Claude Assist | plain grey cursor, no tile (off-style) | **white cursor + orange click ripples on brand-blue tile** | Application.cs:67-71 |

Unchanged (must look exactly as before — flag any drift): Schedule Hub (brand mark, intentional),
Check for Updates, Help, Claude Bridge, Register with Claude, and the Hub-window header image
(brand mark / app logo, `Views\TransomView.xaml:179`).

## Ribbon layout to screenshot

NOTE: the 8 buttons are spread across **three** Transom ribbon panels, not one. Capture all three
so every changed button is in frame. Tab target is the built-in Add-Ins tab (or whatever
`CreatePanel` lands on — confirm at load).

1. **Schedule Tools** panel — Schedule Hub | Check for Updates | Help
2. **Claude Assist** panel — Claude Bridge | Register with Claude | Claude UI Assist | Settings
   *(this panel holds 2 of the 3 changed icons: UI Assist and Settings)*
3. **Revision Tools** panel — Revision Narrative *(the 3rd changed icon)*

Screenshots revit1 should produce:
- **A — full Transom ribbon**, all three panels visible, large (32px) button images. This is the
  primary QA shot; every per-button check below is read from it.
- **B — Schedule Hub window header** (open the Hub): confirm the header still shows the brand mark
  (RibbonIcon32), unchanged. One shot.
- **C — collapsed/small ribbon state IF easily reachable** (drag the Revit window narrow so the
  panel collapses buttons to 16px, or hover the small-image state). This is the 16px legibility
  check. Optional — if it's fiddly, skip and rely on shot A plus the asset previews below; do not
  burn time fighting the ribbon to force the small state.

PLAYBOOK note for revit1: `screenshot` can silently fall back to a focus-stealing screen-grab —
confirm `method=printwindow` in the output, and that `fgIsRevit=` shows Revit had focus, so the
ribbon (not another window) is actually captured.

## Per-button verification (read from shot A)

For each of the three CHANGED buttons, confirm:

- [ ] **Settings** — glyph reads as a **gear** (not a generic blob), white on a brand-blue rounded
      tile. Distinct from Schedule Hub's brand mark sitting on the same/adjacent panel.
- [ ] **Revision Narrative** — glyph reads as a **document/sheet** with horizontal text lines, and
      the **orange triangular revision delta** is visible in a corner (the orange is the at-a-glance
      tell that distinguishes it from any other doc-like icon).
- [ ] **Claude UI Assist** — glyph reads as a **cursor/pointer** with **orange ripple arcs**, on a
      brand-blue tile (it should now match the tiled style of its neighbours, no longer a bare grey
      cursor).

Cross-button checks (the whole point of this change):
- [ ] **No two buttons share an icon.** Specifically: Settings ≠ Schedule Hub, Revision Narrative ≠
      Schedule Hub, and neither Settings nor Revision Narrative still shows the brand mark. (Before
      this change, all three carried the brand mark — that repeat is what we eliminated.)
- [ ] **All 8 icons are mutually distinct** at ribbon size — scan the row and confirm no accidental
      twins.
- [ ] **Brand consistency** — the three changed icons sit on the same brand-blue tile family as the
      rest (palette: tile #2B83C9→#13447D, glyph #E9EEF5, accent #FFAB20). UI Assist joining the
      tiled style is expected and correct.

## Rendering quality

- [ ] **No washed-out / blurry rendering.** At 32px the glyphs are crisp; transparent corners are
      clean (no white box behind the tile).
- [ ] **16px legibility** (from shot C if captured, else from the 16px asset previews in this
      folder): gear still reads as a gear, sheet+delta still distinguishable, cursor+ripple still
      distinguishable. None should collapse into an indistinct dot at small size.
- [ ] **No missing-image / broken-resource placeholder** on any button (would indicate the build
      didn't embed the new `<Resource>` entries — Settings16/32, RevisionNarrative16/32; UiAssist
      was a same-name overwrite so it rides the existing entry).

## Asset preview reference (what the source PNGs look like, ux1-verified 2026-06-11)

These are the wired assets, eyeballed at source before the build — use as the ground truth when
judging the on-screen render:
- **Settings** (32 + 16): white gear, brand-blue tile. Gear teeth still clear at 16px.
- **Revision Narrative** (32 + 16): white sheet, blue lines, orange delta bottom-corner. Delta
  still visible at 16px.
- **Claude UI Assist** (32 + 16): white cursor, orange ripples, brand-blue tile. Cursor + ripple
  still separable at 16px.

## On failure

If any check fails, send the failing shot + the specific checkbox to ux1 (not code1) — icon-asset
problems are regenerated from `generate_icons.py` in this folder (Python 3.8 + Pillow); only
re-wiring (path/csproj) goes to code1. If a button shows a broken-image placeholder, that's a
build/embed issue → code1, not an asset issue.
