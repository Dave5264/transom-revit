# Transom — Bundled MCP Milestone (admin-free)

Goal: ship a **Claude-assist write bridge inside Transom**, started from a ribbon button, with **no
administrator rights at any point** from download to use. This unlocks the Claude-assist write path —
including editing instance parameters on elements **inside Revit groups** (the "blue" cells the
deterministic importer refuses) — with preview/verify/rollback safety.

## The three admin-free constraints (load-bearing — every feature must honor them)
1. **Per-user install only.** All files land under `%AppData%` / `%LocalAppData%` (the existing
   `SingleUser.msi` per-user scope). No component may target `Program Files` or `HKLM`. No runtime install.
2. **Loopback TcpListener on a high port.** The bridge uses `System.Net.Sockets.TcpListener` bound to
   **`127.0.0.1`** on a **high port (default 48810, user-configurable)** — NOT `HttpListener` (its URL
   prefix needs a `netsh` reservation = admin) and NOT `0.0.0.0`/all-interfaces (triggers the admin
   firewall prompt). Loopback-only + port > 1024 ⇒ no URL ACL, no firewall dialog, no admin.
3. **Self-contained shim.** The stdio MCP shim is published **self-contained, single-file** (bundles its
   own runtime) so there is never a "install .NET runtime" step. No dependency on system Node/.NET.

## Architecture
```
Claude Desktop / Code
   |  MCP (stdio)                         launched by the MCP client (user space)
   v
Transom.McpShim (self-contained exe)     [F3]
   |  HTTP POST 127.0.0.1:48810/call      (loopback only)
   v
BridgeServer (TcpListener, in Revit)     [F1]  <- ribbon toggle button starts/stops
   |  enqueue + ExternalEvent
   v
BridgeEventHandler (Revit API thread)    [F1]
   |  BridgeTools.Handle(uiapp, json)
   v
BridgeTools  -> ScheduleReader / param writes (group-aware, verify, rollback)  [F2]
```

## Shared contract (all agents code to this — do not change it)
- Wire protocol: minimal HTTP over `TcpListener`.
  - `GET  /status` → `200` `{"ok":true,"tool":"Transom","version":"<v>","doc":"<title>"}`
  - `POST /call`  body `{"tool":"<name>","args":{...}}` → `200` `{"ok":bool,...}` (always JSON; on error `{"ok":false,"error":"..."}`)
- Tool dispatch entry point (the one seam between F1 and F2):
  `public static string BridgeTools.Handle(Autodesk.Revit.UI.UIApplication app, string requestJson)`
  takes the `{tool,args}` JSON, returns the response JSON. Never throws (catch → `{"ok":false,...}`).
- Tools exposed by F2: `status`, `list_schedules`, `read_schedule`, `set_parameter`, `set_parameters`.
- Port + enabled-state live in `TransomSettings` (integration adds `BridgeSelfHostPort` default 48810).

## Features (each = one coding subagent, disjoint NEW files only; do NOT build or deploy)

### F1 — Bridge server + Revit marshaling + ribbon toggle
New files: `source/Transom/Core/BridgeServer.cs`, `source/Transom/Core/BridgeEventHandler.cs`,
`source/Transom/Commands/BridgeToggleCommand.cs`.
- `BridgeServer`: `TcpListener` bound to `IPAddress.Loopback` on the configured port; background accept
  loop; parse minimal HTTP (request line + headers + Content-Length body); route `/status` and `/call`;
  write an HTTP/1.1 response; `Start(int port, Func<string,string> dispatch)` / `Stop()`; thread-safe;
  graceful shutdown; refuse non-loopback (it can't bind elsewhere anyway). No Revit API here.
- `BridgeEventHandler : IExternalEventHandler`: holds one pending request (requestJson) + a
  `ManualResetEventSlim` + result string; `Execute(uiapp)` calls `BridgeTools.Handle(uiapp, requestJson)`
  and signals. The dispatch delegate passed to `BridgeServer` enqueues the request, `Raise()`s the
  ExternalEvent, waits (with a timeout, e.g. 30s) for the result, returns it. Serialize one-at-a-time.
- `BridgeToggleCommand : IExternalCommand` (or Nice3point ExternalCommand): toggles the server on/off,
  reads the port from `TransomSettings`, reports status. (Ribbon button registration in Application.cs is
  done at INTEGRATION — just make the command class and note the one line needed.)

### F2 — Bridge tools (Revit operations, group-aware writes)
New file: `source/Transom/Core/BridgeTools.cs` — `public static string Handle(UIApplication app, string requestJson)`.
- `status`: `{ok,version,doc}`.
- `list_schedules`: reuse `DocUtil.UserSchedules(doc)` → `[{id,name}]`.
- `read_schedule {name|id}`: run `new ScheduleReader(doc).Read(vs)` and serialize a compact view
  (columns: fieldName/header/binding/writable; rows: uniqueId/kind + cell texts) so Claude can "see" it.
- `set_parameter {uniqueId, parameterId | fieldName, value, binding?}`: the core Claude-assist write —
  resolve element by UniqueId; resolve binding live (instance vs type) the way `Importer` does; **refuse**
  read-only / family-or-type-driven with a clear reason; for an **instance param on a group member**, set
  it directly (instance params may legally vary per group instance); for a **type param**, set on the type
  and report `instancesAffected`; wrap in a `Transaction`; **check the Set() return AND re-read to confirm**;
  roll back on failure; return `{ok,old,new,verified,binding,note}`.
- `set_parameters {edits:[...]}`: batch in ONE transaction; per-edit verify; rollback all on fatal error;
  return per-edit results.
- Mirror the safety patterns in `Importer.cs` (binding resolution, read-only checks, re-read verify).
  Never throw out of `Handle` — catch everything → `{"ok":false,"error":...}`.

### F3 — Self-contained stdio MCP shim
New project: `source/Transom.McpShim/Transom.McpShim.csproj` + `source/Transom.McpShim/Program.cs`.
- `net8.0`, console, `<PublishSingleFile>true</PublishSingleFile>`, `<SelfContained>true</SelfContained>`,
  `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`, `<IncludeNativeLibrariesForSelfExtract>true</...>`.
  No Revit references.
- Implements minimal MCP over stdio (JSON-RPC 2.0): `initialize`, `tools/list`, `tools/call`. Each
  `tools/call` forwards to `POST http://127.0.0.1:<port>/call` with `{tool,args}` and returns the bridge's
  JSON as the tool result. `tools/list` advertises: status, list_schedules, read_schedule, set_parameter,
  set_parameters (with JSON-schema input). Port via `--port` arg / `TRANSOM_BRIDGE_PORT` env (default 48810).
- Keep it dependency-light (System.Text.Json + System.Net.Http only).

### F4 — Installer wiring + MCP config merge (design + WiX fragment)
New files: `install/BundledMcp.wxs` (or fragment) + `install/MCP_CONFIG_MERGE.md`.
- WiX fragment: a **per-user** component group that installs the published shim exe into a per-user folder
  (e.g. `%LocalAppData%\Transom\mcp\`). No Program Files, no HKLM.
- Config-merge: document + provide the approach to register the shim in the **user-level** MCP config
  (`%APPDATA%\Claude\claude_desktop_config.json` and Claude Code's user config) by merging a `transom`
  server entry — done as a per-user first-run step (preferred) or a non-elevated installer custom action.
  Must be idempotent and must not clobber existing servers.
- Write `MCP_CONFIG_MERGE.md` explaining how it stays admin-free (the three constraints) + signing note
  (SmartScreen is a click, not elevation).

## Integration (performed after agents return — NOT an agent task)
- Register the ribbon toggle button in `Application.cs`; add `BridgeSelfHostPort` to `TransomSettings`.
- Wire `BridgeServer` dispatch → `BridgeEventHandler` (ExternalEvent) → `BridgeTools.Handle`.
- Add `Transom.McpShim` to `Transom.sln`; align `BridgeProbe`/settings UI with the new self-host port.
- Build `Debug.R25` (compile-only; Revit may hold the deployed DLL).

## Out of scope for this pass
Live in-Revit testing (needs deploy = Revit closed), code-signing, Claude config auto-merge UX polish.
