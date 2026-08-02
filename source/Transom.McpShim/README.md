# Transom.McpShim

A tiny, self-contained **stdio MCP server** that bridges an MCP client (Claude
Desktop / Claude Code) to the **Transom** Revit add-in's loopback HTTP bridge.

This is **feature F3** of the bundled-MCP milestone — see
`BUNDLED_MCP_PLAN.md (retired 2026-07-13; archived locally)`. It has **no Revit references**; it only speaks
Model Context Protocol on stdin/stdout and HTTP to `127.0.0.1`.

## What it is

```
Claude Desktop / Code
   |  MCP (stdio, JSON-RPC 2.0, newline-delimited — NOT Content-Length framed; see v1.4.7)
   v
Transom.McpShim   (this self-contained exe)
   |  HTTP POST 127.0.0.1:<port>/call   {"tool":name,"args":{...}}
   v
Transom bridge (TcpListener, inside Revit)
```

Each MCP `tools/call` is forwarded verbatim to the bridge's `POST /call`
endpoint and the bridge's JSON response is returned to the model as a single
text content item. `isError` is set when the bridge replies with `"ok": false`
(or an unreachable/garbled response).

### Tools advertised

These five are declared inline in `Program.cs` — the **schedule** surface this shim was built for:

| tool             | args                                                              |
|------------------|------------------------------------------------------------------|
| `status`         | (none)                                                            |
| `list_schedules` | (none)                                                            |
| `read_schedule`  | `{ id?: number, name?: string }`                                  |
| `set_parameter`  | `{ uniqueId, parameterId?, fieldName?, value, binding? }`         |
| `set_parameters` | `{ edits: [ { uniqueId, parameterId?, fieldName?, value, binding? } ] }` |

They are **not the whole list**. `ParityTools.AddTo` appends the ported parity surface — element
query/filter/modify, transforms, views, sheets, levels, rooms, dimensions, tagging, materials,
colour splash and more — so `tools/list` returns roughly forty entries, not five. `ParityTools.Disabled`
gates a few off (`check_clashes`, `load_family`, `place_family`, `list_families`, `export_document`,
`export_ifc`, `save_document`); the bridge refuses those independently, so a stale shim can't reach them.
`ParityTools.cs` is the authoritative list — read it rather than extending this table, which will
drift again.

The actual Revit work (binding resolution, group-aware writes, read-only
refusal, verify/rollback) lives in the in-Revit bridge (F2); this shim is a
thin, dependency-light forwarder.

## How to run

The bridge port is resolved in this order:

1. `--port <n>` command-line argument (also accepts `--port=<n>`)
2. `TRANSOM_BRIDGE_PORT` environment variable
3. default `48810`

```
Transom.McpShim.exe --port 48810
```

The process reads MCP messages from **stdin** and writes responses to
**stdout** until EOF. All diagnostics go to **stderr only** — stdout is the
protocol channel and must never carry non-protocol output.

You normally don't launch it by hand; the MCP client spawns it (see below).

## How it's registered

Add a `transom` server entry to the **user-level** MCP config (no admin). For
Claude Desktop that is `%APPDATA%\Claude\claude_desktop_config.json`; Claude
Code uses its own user config. The installer wiring / idempotent merge is feature **F4**, implemented in
`source/Transom/Core/McpRegistration.cs` — one-time `.transom.bak` backup, `.tmp` write + atomic
`File.Replace`, and a hard refusal to touch a config that doesn't parse.

```jsonc
{
  "mcpServers": {
    "transom": {
      "command": "%LOCALAPPDATA%\\Transom\\mcp\\Transom.McpShim.exe",
      "args": ["--port", "48810"]
    }
  }
}
```

## Building (maintainers)

Self-contained, single-file publish so it runs with **no admin and no separate
.NET runtime install**:

```
dotnet publish source/Transom.McpShim/Transom.McpShim.csproj -c Release -r win-x64
```

The publish-time settings (`PublishSingleFile`, `SelfContained`,
`RuntimeIdentifier=win-x64`, `IncludeNativeLibrariesForSelfExtract`,
`InvariantGlobalization`) are baked into the `.csproj`, so the bare command
above produces a single self-extracting `Transom.McpShim.exe` under
`bin/Release/net8.0/win-x64/publish/`. Dependencies are limited to
`System.Text.Json` and `System.Net.Http` (both in the shared framework) — no
external NuGet packages.
