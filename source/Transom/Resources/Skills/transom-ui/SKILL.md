---
name: transom-ui
description: Drive Revit's desktop UI through Transom's ClickHelper tools (mcp__transom-ui-assist__*) for what the API can't do - modal dialogs, the Properties palette, Edit Group mode, the Transom Hub - and test a Revit add-in end to end through the real interface; also keep Revit or AutoCAD in front during a live demonstration (tile, view per step, screenshots, contact sheet). Use when asked to click through Revit, answer a dialog, set a value in the Properties palette, edit inside a group, test the add-in in the UI, or present a build live.
when_to_use: '"test it in Revit", "click through the UI", "answer that dialog", "set it in the Properties palette", "edit the group", "drive the Hub", "show the build live", "keep Revit on screen".'
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

# Driving and testing Revit's UI

API checks prove an add-in loaded. They don't prove the UI works: a dialog that never opens, a disabled button or a
wrong status line only show up when you drive the interface. Pair the UI tools with the API: select and verify
through `transom`, click through `transom-ui-assist`. The bridge rules are in the `transom` skill.

## Setup, every time

1. **`revit_tile` first.** Revit takes 2/3 of the screen and Claude 1/3. Clicks land by screen coordinate and keys go
   to the foreground window, so an occluded Revit can't be driven. At about 50/50 the ribbon overflows and hides the
   right-hand tabs, including Transom's, behind a `>>` chevron. A missing tab is far more often a narrow window than a
   broken add-in: read the geometry in the reply before reporting anything as broken. If `revit_tile` returns 50/50,
   the helper exe in `%LocalAppData%\Transom\mcp\` is stale; say so.
2. **Launch with a model or template path** (`Start-Process Revit.exe "<path>"`), clear the unsigned add-in prompt
   (`revit_find("Always Load")`, then `revit_click_by_id`), wait for the window, then tile.
3. **Things that look like failures but aren't.** "Timed out waiting for Revit" while a model opens: the socket
   accepts before the API thread is free. The Home screen is invisible to UI Automation, and the title is empty until a
   document opens. A tiny screenshot was rooted on a tooltip under the cursor: re-tile and retake it.

## The rules that keep clicks honest

- **Screenshot after almost every click, type or scroll**, read it, and confirm the state before the next action. Wrong
  picks and missed buttons are silent. The default capture (PrintWindow) shows the UI with a black viewport;
  `screen=true` shows the model and brings Revit forward.
- **Coordinates are absolute screen pixels.** Never reuse old ones: dialogs and the Hub open at unpredictable places.
  Get live ones from `revit_find` (centerX/centerY), and prefer `revit_click_by_id` when the control has one.
- **The Transom Hub's tab content is invisible to UI Automation** (only the window chrome is exposed). Read
  coordinates off a fresh screenshot after every open. The Hub centres on the monitor, not on Revit.
- **Dialogs are separate windows**: `revit_list_dialogs` reads title, text and buttons, and `revit_click_dialog` clicks
  one (omit the button to safely dismiss it).
- **No right-click, and popups can't be observed.** `revit_click_xy` is left-click only and ignores a button argument.
  Context menus and dropdown lists vanish before any capture. Reach the result another way and confirm it through
  the API, the clipboard or a written file.
- **Edit Group mode freezes the API**: every call times out inside it. Select, read, write and verify through the API
  before `revit_edit_group` and after `revit_finish_group`, never in between. A built-in edited inside the group changes
  every instance of that group type.
- **Locating an element**: override its colour through the API, take `screen=true`, click the highlight. A selected
  group shows the override inverted.
- **Never open an older .rvt in a newer Revit**, never force-kill, and answer "No" to "save changes" on a throwaway.

## Typing into fields (tested 2026-11-15)

- **Properties palette**: `revit_scroll` the parameter into view, then `revit_type` at the value cell. The click
  selects the cell's text, so typing **replaces** the old value (no Ctrl+A needed). `enter=true` did **not** apply a
  text cell (Comments): the value only applied after a click on the palette's **Apply** button. So type, click Apply
  (or the canvas), then read the parameter back through the API.
- **Revit shortcuts**: `revit_keys` with a canvas point (it clicks there for focus first): `zf`, `tl`, `vg`.
- **File dialogs**: don't edit the filename with key combos. Accept the default name (`revit_click_dialog` "save")
  and rename the file on disk. To open a file, `revit_find` its row, click it, then Open.

## Testing an add-in through the UI

1. Launch with the test model, clear the prompt, tile.
2. Drive the feature, screenshotting between steps.
3. Verify the artifact, not the status line: an `.xlsx` or `.docx` is a zip (magic bytes `50 4B`). Read
   `xl/worksheets/sheet1.xml` or `word/document.xml` and count rows against the model. That caught a real mislabel in
   Transom: an export said "(0 element rows)" while it wrote 11 correct rows.
4. Close with `CloseMainWindow`, answer the save prompt, and delete throwaway files.

## Presenting live (a build or a demo)

`scripts/demo_window.ps1 -App Revit|acad` brings the app to the front on the left 2/3 and parks Claude on the right
third. It also moves the cursor off the canvas, because Revit highlights whatever the cursor hovers when a view
changes. It takes the foreground with a Shift tap; an Alt tap lights Revit's ribbon key tips. `scripts/demo_shot.ps1`
captures the app window as the audience sees it, and `scripts/contact_sheet.py <shots>` puts every shot on one
captioned sheet. For a staged model build, use `transom-model`'s replay (`run_stage.cs` opens each stage's view).

AutoCAD: its process handle is a tiny helper window, so these scripts find the titled frame through UI Automation.
`transom/scripts/acad_always_load.ps1` clears its unsigned plug-in prompt.
