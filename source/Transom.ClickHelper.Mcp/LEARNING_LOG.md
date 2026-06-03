# Click Helper — interface learning log

Hard-won notes on driving Revit's UI via the Click Helper, so each edit gets faster. The **Rules of
thumb** are the always-read summary; **Run 1** is the worked example (it succeeded end-to-end).

## ✅ Proven end-to-end (Run 1)

Changed a **grouped window member's instance parameter** (Comments) through the **Edit Group UI
workflow** — which has no Revit API — driving *every* UI step from the Click Helper, and confirmed it
through the API. The edit propagated to **both** group instances (editing a member inside Edit Group
changes the group *definition*). Both interfaces, one clean change.

## Rules of thumb (distilled)

### Input plumbing (the stuff that bit us hardest)
1. **`SendInput` INPUT struct must be the full 40 bytes (x64).** The struct's union is sized for the
   *largest* member (MOUSEINPUT), not KEYBDINPUT. A keyboard-only 32-byte struct makes `SendInput`
   fail its `cbSize` check and **inject nothing — it returns 0**. This silently broke ALL keyboard
   (typing AND shortcuts) for a long time. If keys do nothing, check `SendInput`'s return value first.
2. **Tile Revit + Claude side by side before any clicking/typing** (`tile`). Revit must be visible and
   not occluded: mouse clicks land by screen coordinate, so an occluded Revit gets clicks on the wrong
   window. Tiling also keeps Revit reachable when Claude's permission prompts steal the foreground.
3. **Refocus-click and keystrokes must be ATOMIC (one process).** A Claude permission prompt fires
   between separate tool calls and steals focus, so a `click` then a separate `type` loses the field.
   Use `type --at=X,Y …` / `keys --at=X,Y …` — they click *then* send in the same exe invocation.
4. **`type` = field text (Unicode/WM_CHAR); `keys` = command shortcuts (virtual-key).** Different
   mechanisms. `type --enter` appends Enter in the same process to commit a Properties cell (a separate
   `key enter` would refocus and discard it).
5. **Verify input actually injected:** `type` reports `sentEvents` (chars×2) and `fgIsRevit`. `0` means
   blocked/malformed; `fgIsRevit:false` means focus didn't land.

### Focus targets
6. **Canvas shortcut** (TL, VG, ZF…): `keys --at=<a canvas point>` — clicking the drawing area gives
   it keyboard focus so the shortcut fires. (Confirmed with `VG` → the Visibility/Graphics dialog.)
7. **Field entry**: `type --at=<the value cell>` — click the exact Properties cell, then type.
8. **Never** click the title bar / QAT to "focus" — you'll fire a toolbar button by accident. Clicking
   the active view tab is the safe no-side-effect way to focus the canvas (used internally as a fallback).

### Revit behavior
9. **The Revit API is dead inside Edit Group mode** — pyRevit-Routes calls time out until you leave it.
   So selection/reads/writes happen *outside* edit mode (API), and the in-edit change is done via UI.
10. **A direct API write to a grouped member is refused** with a modal "changes to groups are allowed
    only in group edit mode … or Ungroup" error (it hangs the API until dismissed). Revit itself says:
    use Edit Group. That's the whole reason this UI workflow exists.
11. **Editing a member inside Edit Group edits the group DEFINITION → propagates to all instances.**
12. **A selected group renders an element override as the INVERSE color** (red shows cyan). Select none
    to confirm the true override color.
13. **Thin Lines ON / Lineweights OFF** makes a color-overridden element easiest to spot. Toggle with
    the `TL` shortcut (no DB API; default shortcut, no custom file on this machine → defaults apply).
14. **Group buttons via UIA `InvokePattern`** (Edit/Finish/Cancel) — focus/occlusion-independent, the
    most robust click of all. Probe mode with `find ID_FINISH_GROUP_EDIT_MODE` (present = in edit mode).
15. **Modal dialogs are separate top-level windows** → `dialogs` / `click-dialog <button>` (the main
    window's UIA tree doesn't contain them). Capture button names before invoking (the element dies).
16. **`PrintWindow` screenshots miss the accelerated viewport** (drawing area comes back black) but
    capture all UI chrome. Use `--screen` (needs Revit visible — tiling makes that reliable) to see the
    model. Don't conclude "empty view" from a black PrintWindow.
17. **Per-monitor-v2 DPI** is set so rects/cursor/capture agree in physical pixels (Revit can sit on a
    left monitor at negative X).

### Keyboard-shortcut policy (per the user)
18. Check the shortcut settings each run (they vary per user). If a needed command has no shortcut,
    add one to a NEW importable file and ask the user to import it — **never edit their existing custom
    shortcuts**. On this machine there's no custom file, so Revit defaults apply (e.g. `TL`).

## Net conclusion — what's automatable

| Action | Reliable? | How |
|---|---|---|
| Edit Group / Finish / Cancel | ✅ | UIA `InvokePattern` |
| Dismiss/answer a modal dialog | ✅ | `dialogs` + `click-dialog` |
| Read/verify model state, select | ✅ (outside edit mode) | Revit API |
| Toggle view options (Thin Lines…) | ✅ | `keys --at=<canvas>` shortcut |
| Select a specific member in-canvas | ✅ | color-override it, then `click-xy` the highlight |
| **Set a parameter value in-edit** | ✅ | tile → Edit Group → click member → `scroll` → `type --at=cell --enter` |

Earlier this log concluded parameter editing via UI was "not viable." **That was wrong — it was the
`SendInput` struct bug masquerading as a focus/permissions wall.** With the struct fixed plus tiling +
atomic click-type, the full workflow is reliable.

---

## Run 1 — change a Window member's Comments via the Edit Group workflow ✅

Target: group type `2766088`, instances A=`357649` / B=`357904`. Member idx 0 = Window
`Window-Single-Hung 4'-0"×5'-6"`, A.id=`298781`, B.id=`2847535`. Comments empty before.

Winning sequence (each step its own atomic Click Helper call):
1. API: identify member, `SetElementOverrides` red on it, zoom to it. (API works — outside edit mode.)
2. `tile` — Revit left half, Claude right.
3. `keys --at=<canvas> tl` — Thin Lines ON (confirmed lines went uniform-thin).
4. API: re-select the group (so Edit Group is available).
5. `edit` (InvokePattern) → Edit Group mode; `find ID_FINISH_GROUP_EDIT_MODE` confirms.
6. `click-xy` the red highlight → selects the member; screenshot confirms `Windows (1)` +
   `Window-Single-Hung…` (right element).
7. `scroll 130 450 -4` → Comments scrolls into view under Identity Data.
8. `type --enter --at=190,413 TRANSOM-EG-001` → value entered (`sentEvents:28`) and committed; Apply
   clicked for good measure.
9. `finish` (InvokePattern) → leaves edit mode (`find` count 0).
10. API verify: **A.Comments = B.Comments = "TRANSOM-EG-001"** → change confirmed AND propagated.

Detours that taught the rules above: `SendInput` returning 0 (struct size) was the root cause of every
"keyboard doesn't reach Revit" symptom; a direct API write first popped the "use Edit Group" modal
(dismissed via `click-dialog Cancel`); the API times out inside edit mode; the title-bar focus-click
fired a QAT button (switched to the view-tab/canvas focus); a selected group inverts the override color.
