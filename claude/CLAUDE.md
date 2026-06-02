# Transom — Revit bridge (drop-in guidance for Claude)

> **Where this goes.** Drop this file in as `CLAUDE.md` (Claude Code: project root
> or `~/.claude/CLAUDE.md`; Cowork: the workspace's instructions file). It is
> loaded automatically, so the *first* time someone asks you to do anything with
> their Revit model you already know the tools exist and how to connect cleanly —
> no setup turn needed.

You have a Model Context Protocol server named **`transom`** that talks to a live
Autodesk **Revit** session over a local, loopback-only bridge (`127.0.0.1`, default
port `48810`). It is provided by the Transom Revit add-in. The tools appear as
`mcp__transom__<name>`.

## First action: confirm the connection

When a Revit task starts (or the user asks "are you connected?"), **call
`status` first.** Interpret the result:

- ✅ `{"ok":true,"tool":"Transom","version":"…","doc":"<title>"}` → you are connected
  to that open document. Proceed. Mention the document name so the user knows which
  model you're acting on.
- ❌ The `transom` tools aren't available at all → the MCP server isn't loaded in
  this session. Tell the user to **(1)** click **"Register Claude Bridge"** in the
  Transom ribbon (one-time, registers the server), then **(2)** restart Claude
  Code / Cowork so it launches the shim. New MCP servers are only picked up at
  startup.
- ❌ `status` errors or times out → the bridge isn't reachable. Most likely one of:
  - the **bridge is toggled off** → in Revit, click the Transom **bridge toggle**
    so it reads "on / listening on 127.0.0.1:48810";
  - **no document is open** → open the model you want to work on;
  - the **port was changed** → it must match the `--port` the server was registered
    with (re-run Register after changing `TransomSettings.BridgeSelfHostPort`).

Don't guess your way past a failed `status` — surface the specific remediation above.

## The tools

| Tool | Use |
|------|-----|
| `status` | Health check; returns add-in version + open document title. **Call first.** |
| `list_schedules` | Discover schedules — returns `[{id, name}]`. |
| `read_schedule` | Read one schedule (by `id` or `name`) → compact view: columns (with `header`/`binding`/`writable`) and rows keyed by element `uniqueId`. This is how you "see" the data before editing. |
| `set_parameter` | Write **one** parameter on an element (`uniqueId` + `value`, plus `parameterId` *or* `fieldName`). Verifies the write and rolls back on failure. |
| `set_parameters` | Write **many** edits in a **single transaction** (`{edits:[…]}`). Per-edit verify; whole batch rolls back on a fatal error. Prefer this for multiple cells. |

## Safe write workflow (follow this for any edit)

1. **`read_schedule`** the target schedule and locate the exact rows/columns.
2. **Propose the specific edits to the user and get confirmation** — list each
   `uniqueId` / field / old → new value. Writes change the live model.
3. Apply with **`set_parameters`** (one transaction) when changing more than one cell.
4. **Report what the bridge verified** — it re-reads each value and returns
   `{ok, old, new, verified, binding, note}`. If `verified` is false or `ok` is
   false, say so and stop; the bridge will have rolled back.

Notes that matter:
- Only columns where `writable` is true can be set. The bridge **refuses** read-only
  and family/type-driven parameters and tells you why — relay that, don't retry blindly.
- **Instance parameters on elements inside Revit groups** are exactly what this bridge
  unlocks (a normal schedule import refuses those "blue" cells). They can legally vary
  per group instance, so editing one member is expected behavior.
- A **type** parameter write affects every instance of that type — the result includes
  `instancesAffected`. Confirm with the user before changing type params.
- Values are passed as strings; the bridge coerces to the parameter's storage type.

## Diagnostic fallback (only if asked to debug the connection)

The MCP path is the normal route. If the server isn't registered yet but you need to
prove the bridge itself is alive, you can hit it directly (same protocol the shim uses):

```
POST http://127.0.0.1:48810/call
Header: X-Transom-Token: <contents of %LocalAppData%\Transom\bridge.token>
Body:   {"tool":"status","args":{}}
```

A `{"ok":true,…}` reply confirms Revit + bridge are healthy and the only missing piece
is registering/restarting the MCP client. Don't use this as the normal path — prefer
the `mcp__transom__*` tools.
