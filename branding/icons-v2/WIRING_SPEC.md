# icons-v2 Wiring Spec (ux1 → code1)

Date: 2026-06-11. Purpose: eliminate icon repetition on the ribbon. `RibbonIcon16/32.png`
is currently shared by THREE buttons (Schedule Hub, Settings, Revision Narrative) plus the
hub-window header. New distinct icons are provided here; do NOT overwrite the existing
files in `source\Transom\Resources\Icons\` in place — copy these in under their new names.

## Current inventory (what repeats)

| Button (panel) | Icon files | Referenced at |
|---|---|---|
| Schedule Hub (Schedule Tools) | RibbonIcon16/32.png | source\Transom\Application.cs:29-30 |
| Check for Updates (Schedule Tools) | Update16/32.png | Application.cs:36-37 |
| Help (Schedule Tools) | Help16/32.png | Application.cs:41-42 |
| Claude Bridge (Claude Assist) | Bridge16/32.png | Application.cs:53-54 |
| Register with Claude (Claude Assist) | Register16/32.png | Application.cs:59-60 |
| Claude UI Assist (Claude Assist) | UiAssist16/32.png | Application.cs:68-69 |
| **Settings (Claude Assist)** | **RibbonIcon16/32.png — REPEAT** | Application.cs:74-75 |
| **Revision Narrative (Revision Tools)** | **RibbonIcon16/32.png — REPEAT** | Application.cs:81-82 |
| Hub window header image (not a button) | RibbonIcon32.png | Views\TransomView.xaml:179 |

Schedule Hub keeps the brand mark (main entry point), and the window header keeps it as
the app logo — both intentional, no change there.

## New assets (this folder)

| File | For | Glyph |
|---|---|---|
| Settings32.png / Settings16.png | Settings button | white gear on brand blue tile |
| RevisionNarrative32.png / RevisionNarrative16.png | Revision Narrative button | white sheet + blue text lines + orange revision delta |
| UiAssist32.png / UiAssist16.png | Claude UI Assist (OPTIONAL restyle) | white cursor + orange click ripples on brand tile — current icon is a plain grey cursor with no tile, the only off-style icon on the ribbon. Not a repeat; adopt at your discretion. |

`generate_icons.py` (this folder) is the source — palette sampled from RibbonIcon32.png
(tile #2B83C9→#13447D, glyph #E9EEF5, accent #FFAB20). Re-run with Python 3.8 + Pillow to
regenerate.

## Changes for code1

1. Copy from `branding\icons-v2\` to `source\Transom\Resources\Icons\`:
   - Settings16.png, Settings32.png
   - RevisionNarrative16.png, RevisionNarrative32.png
   - (optional) UiAssist16.png, UiAssist32.png — same names as existing files; if adopted
     this is a straight overwrite and needs no code/csproj change.

2. `source\Transom\Transom.csproj` — add inside the existing `<ItemGroup>` with the icon
   `<Resource>` entries (currently lines 44-55):
   ```xml
   <Resource Include="Resources\Icons\Settings16.png"/>
   <Resource Include="Resources\Icons\Settings32.png"/>
   <Resource Include="Resources\Icons\RevisionNarrative16.png"/>
   <Resource Include="Resources\Icons\RevisionNarrative32.png"/>
   ```

3. `source\Transom\Application.cs`:
   - Lines 74-75 (Settings button):
     `"/Transom;component/Resources/Icons/Settings16.png"` and `.../Settings32.png`
   - Lines 81-82 (Revision Narrative button):
     `"/Transom;component/Resources/Icons/RevisionNarrative16.png"` and `.../RevisionNarrative32.png`
   - Lines 29-30 (Schedule Hub): unchanged.

4. `Views\TransomView.xaml:179`: unchanged (app logo in window header).

All PNGs are 32x32 / 16x16 RGBA with transparent corners, matching the existing set.
