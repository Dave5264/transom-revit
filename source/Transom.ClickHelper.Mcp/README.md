# Transom Click Helper — Claude UI-Assist for Revit commands with no API

Some Revit commands have **no API** — notably **Edit Group**, **Finish**, modal dialogs, and editing a
parameter value in the **Properties palette**. This optional, on-demand Transom feature lets Claude
drive those by automating Revit's UI from a **separate process** (Windows UI Automation + synthesized
mouse/keyboard input).

```
        clicks / keys ┌───────────────────────┐  screenshots
   ┌──────────────────│  Transom.ClickHelper   │◀───────────────┐
   ▼                  │  (UI-automation engine)│                │
┌──────────┐         └───────────────────────┘                │
│  Revit   │                  ▲  UI visual data / results       │
│ on screen│                  │                                 │
└──────────┘         ┌───────────────────────┐                 │
                     │ Transom.ClickHelper.Mcp │  (this project) │
                     │      (MCP server)       │                 │
                     └───────────────────────┘                 │
                              ▲  tools/call     │ image + json
                              │                 ▼
                          ┌────────┐
                          │ Claude │
                          └────────┘
```

| Piece | Project | What it is |
|---|---|---|
| **Engine** | `../Transom.ClickHelper` | Standalone console exe. Finds/clicks Revit controls via UI Automation, types/scrolls, captures the window, tiles, handles dialogs. |
| **MCP server** | `Transom.ClickHelper.Mcp` (here) | stdio MCP server. Exposes tools to Claude and fulfils each by running the engine out-of-process. |

This is the **right-hand column**; the Transom **data bridge** (`Transom.McpShim`, real Revit API) is the
left column. Both are independent MCP servers Claude orchestrates: use the **API path** to select a
group and verify model state, and this **UI path** to press the buttons / edit the cells the API can't.

## Why a separate process (load-bearing)

The Transom bridge runs tool calls on Revit's **API/UI thread** via an `ExternalEvent`. Clicking a
ribbon button from there would fire it while Revit's own message pump is blocked running your handler —
it deadlocks/no-ops. A separate process clicks while Revit's pump is free, so the engine is its own exe.

## How it ships (part of Transom)

Both exes are published self-contained and bundled next to `Transom.dll`
(`build/Modules/PublishClickHelperModule.cs`). In Revit, the **"Claude UI Assist"** ribbon button
(`UiAssistSetupCommand`) does a one-time install to `%LocalAppData%\Transom\mcp\` and merges a
`transom-ui-assist` entry into the user's Claude configs (`ClickHelperRegistration`, admin-free,
idempotent). It **never auto-runs**, so Transom stays standalone for users without Claude.

## MCP tools

| Tool | Args | Engine command |
|---|---|---|
| `revit_status` | `pid?` | `status` |
| `revit_tile` | `revitSide?`, `pid?` | `tile` — **do first**: Revit visible & side-by-side |
| `revit_edit_group` / `revit_finish_group` / `revit_cancel_group` | `pid?` | `edit` / `finish` / `cancel` (InvokePattern) |
| `revit_find` | `text`, `pid?` | `find <text>` — controls + ids + click centers |
| `revit_click_by_id` | `automationId`, `pid?` | `click-id <id>` |
| `revit_click_xy` | `x`, `y`, `pid?` | `click-xy` (e.g. a colour-highlighted member) |
| `revit_keys` | `shortcut`, `x?`, `y?`, `pid?` | `keys` — view shortcut (canvas focus-click) |
| `revit_type` | `text`, `x`, `y`, `enter?`, `pid?` | `type` — click a value cell + type (+commit) |
| `revit_scroll` | `x`, `y`, `notches`, `pid?` | `scroll` — bring a parameter into view |
| `revit_list_dialogs` / `revit_click_dialog` | (`button?`), `pid?` | `dialogs` / `click-dialog` |
| `revit_screenshot` | `screen?`, `pid?` | `screenshot` — returns an MCP **image** |

The MCP `initialize` reply ships a condensed **operating manual** (the `instructions` field) so Claude
uses the field-tested techniques. The full detail is in [`LEARNING_LOG.md`](LEARNING_LOG.md). Key rules:

- **Tile first.** Keyboard/clicks go to the visible foreground window.
- **The Revit API is dead inside Edit Group mode** — select/read/write via the API only before/after.
- **Properties value cells aren't UIA-addressable** → `revit_type` (click+type, `enter=true` to commit)
  is the only way to set a parameter value; `revit_scroll` to reveal it first.
- **Editing a member inside Edit Group edits the group DEFINITION** → it propagates to all instances.

Group-parameter-edit flow: API selects the group → `revit_edit_group` → click the member → `revit_scroll`
→ `revit_type` (enter) → `revit_finish_group` → verify via API. Modal in the way? `revit_list_dialogs` +
`revit_click_dialog`.

## Build (dev)

```sh
dotnet build "../Transom.ClickHelper/Transom.ClickHelper.csproj" -c Release   # engine
dotnet publish Transom.ClickHelper.Mcp.csproj -c Release                       # this server (self-contained)
```

The server locates the engine automatically (sibling → `TRANSOM_CLICKHELPER_EXE` env →
`../Transom.ClickHelper/bin/.../Transom.ClickHelper.exe`). Override with `--exe <path>`.

## Register with Claude

Normally turning **Claude Assist** on in Transom Settings does this. The manual equivalent merges one entry
into `%AppData%\Claude\claude_desktop_config.json` and `%UserProfile%\.claude.json`:

```jsonc
{
  "mcpServers": {
    "transom-ui-assist": {
      "command": "C:\\...\\Transom.ClickHelper.Mcp.exe",
      "args": ["--exe", "C:\\...\\Transom.ClickHelper.exe"]
    }
  }
}
```

(Leave `args` off if the two exes sit side by side, or set the `TRANSOM_CLICKHELPER_EXE` env var.)

## Dev test clients

- `test_client.py` — full handshake + a few tools/call (status, find, screenshot).
- `call_tool.py <tool> [jsonArgs]` — one-shot caller, e.g. `python call_tool.py revit_edit_group`.

## Verified (Revit 2025.4)

End-to-end through the MCP server: tile → highlight a grouped window member (API) → `revit_edit_group`
→ click it → `revit_scroll` → `revit_type` (Comments = `TRANSOM-EG-001`, committed) → `revit_finish_group`
→ API confirms the change landed on **both** group instances (definition-level propagation). Dialog
handling, keyboard shortcuts (`tl`/`vg`), and text entry all confirmed working.
