# Revit Click Helper — UI automation for commands with no Revit API

Some Revit commands have **no API** — notably **Edit Group** and **Finish** (finish group edit
mode). This subsystem lets Claude invoke them anyway, by driving Revit's UI from a **separate
process** through Windows UI Automation. It is the right-hand column of the architecture diagram:

```
        Mouse clicks ┌──────────────────┐  Screenshots
   ┌─────────────────│  Click Helper.exe │◀──────────────┐
   ▼                 │ (RevitGroupClick) │               │
┌─────────┐          └──────────────────┘               │
│  Revit  │                   ▲  UI visual data / results│
│ on screen│                  │                          │
└─────────┘          ┌──────────────────┐                │
                     │  Click Helper MCP │  (this project)│
                     │(RevitClickHelperMcp)               │
                     └──────────────────┘                │
                              ▲  tools/call    │ image+json
                              │                ▼
                          ┌───────┐
                          │ Claude │
                          └───────┘
```

Two pieces, mapped to the diagram boxes:

| Diagram box | Project | What it is |
|---|---|---|
| **Click Helper.exe** | `../RevitGroupClick` | Standalone console exe. Finds/clicks Revit controls via UI Automation; can screenshot the window. |
| **Click Helper MCP** | `RevitClickHelperMcp` (here) | stdio MCP server. Exposes tools to Claude and fulfils them by running the exe. |

The existing **Transom** add-in + `Transom.McpShim` are the *left* column (real Revit API). The two
columns are independent MCP servers; Claude orchestrates both. Use the Transom/API path to **select**
a group and to **verify** model state; use this Click Helper path to **press the buttons** the API
can't.

## Why a separate process (load-bearing)

The Transom bridge runs tool calls on Revit's **API/UI thread** via an `ExternalEvent`. If you tried
to UI-Automation-click a ribbon button from there, you'd be invoking the button while Revit's own
message pump is blocked running your handler — it deadlocks/no-ops. A separate process clicks while
Revit's pump is free. That's why Click Helper is its own exe, not code inside the add-in.

## What we learned about Revit's ribbon (AdWindows)

- Ribbon commands are exposed as **`ControlType.Pane` leaves**, often with **no invoke pattern** and
  no clickable point (e.g. the on-ribbon `Edit Group` is a Pane, automation id `2007`). So matching
  by `ControlType.Button` alone misses them — the helper scans **all** control types by Name.
- WPF ribbons **virtualize inactive tabs**: a contextual tab's buttons aren't in the UI Automation
  tree until that tab is the active one. (Selecting a group via the API does activate the
  "Modify | Model Groups" tab, which is why the buttons appear.)
- When a group is selected / being edited, Revit *also* renders an on-screen, **invokable** twin of
  each command (e.g. `Edit Group` button, and `Finish`/`Cancel` with ids
  `ID_FINISH_GROUP_EDIT_MODE` / `ID_CANCEL_GROUP_EDIT_MODE`). The helper prefers the enabled,
  on-screen, invokable element and fires `InvokePattern`; it falls back to a **bounding-rect-center
  mouse click** for panes that expose no pattern.
- `Process.MainWindowHandle` is frequently `0` for Revit — the helper enumerates the process's
  top-level windows instead.
- The helper declares **per-monitor-v2 DPI awareness** so rectangles, cursor moves, and screen
  capture agree in physical pixels (Revit can sit on a left monitor at negative X).

## MCP tools

| Tool | Args | Maps to exe |
|---|---|---|
| `revit_status` | `pid?` | `status` |
| `revit_edit_group` | `pid?` | `edit` |
| `revit_finish_group` | `pid?` | `finish` |
| `revit_cancel_group` | `pid?` | `cancel` |
| `revit_click_by_id` | `automationId`, `pid?` | `click-id <id>` |
| `revit_click_xy` | `x`, `y`, `pid?` | `click-xy <x> <y>` |
| `revit_find` | `text`, `pid?` | `find <text>` |
| `revit_screenshot` | `screen?`, `pid?` | `screenshot` — returns an MCP **image** |

`revit_screenshot` defaults to **PrintWindow** (captures Revit's own pixels even when it's behind
other windows, without stealing focus; the 3D viewport may be blank). Pass `screen: true` to bring
Revit forward and grab the composited screen (faithful viewport, takes focus).

Recommended flow for a group edit:
1. (Transom/API) select the group → 2. `revit_edit_group` → 3. do edits via the API →
4. `revit_finish_group` → 5. (Transom/API) verify.

## Build

```sh
# the exe (Click Helper.exe)
dotnet build "../RevitGroupClick/RevitGroupClick.csproj" -c Release

# the MCP server (this project) — self-contained single-file in Release
dotnet publish RevitClickHelperMcp.csproj -c Release
```

The server locates the exe automatically (sibling → `REVIT_CLICK_HELPER_EXE` env →
`../RevitGroupClick/bin/.../RevitGroupClick.exe`). Override with `--exe <path>`.

## Register with Claude

Mirror the Transom registration: merge one entry into the user MCP configs
(`%AppData%\Claude\claude_desktop_config.json` and `%UserProfile%\.claude.json`):

```jsonc
{
  "mcpServers": {
    "revit-click-helper": {
      "command": "C:\\path\\to\\RevitClickHelperMcp.exe",
      "args": ["--exe", "C:\\path\\to\\RevitGroupClick.exe"]
    }
  }
}
```

(Leave `args` off if the two exes sit side by side, or set the `REVIT_CLICK_HELPER_EXE` env var.)

## Dev test clients

- `test_client.py` — full handshake + a few tools/call (status, find, screenshot).
- `call_tool.py <tool> [jsonArgs]` — one-shot caller, e.g. `python call_tool.py revit_edit_group`.

## Verified

On Revit 2025.4, end-to-end through the MCP server: `revit_edit_group` entered group-edit mode
(confirmed by the appearance of `ID_FINISH_GROUP_EDIT_MODE`), `revit_finish_group` exited it
(controls gone), both via `InvokePattern`. `revit_screenshot` returned a live PNG of the Revit
window while it was occluded and unfocused.
