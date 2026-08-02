# Transom — Revit bridge (drop-in guidance for Claude)

> **Where this goes.** Drop this file in as `CLAUDE.md` (Claude Code: project root
> or `~/.claude/CLAUDE.md`). It is
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
  this session. Tell the user to **(1)** turn **Claude Assist on** in Transom
  Settings (Schedule Hub → Settings tab; the first ON registers the servers),
  then **(2)** restart Claude Code so it launches the shim. New MCP servers are
  only picked up at startup.
- ❌ `status` errors or times out → the bridge isn't reachable. Most likely one of:
  - **Claude Assist is off** → in Revit, open Transom Settings and turn the
    **Claude Assist** toggle on (its status panel should show "Bridge listening
    127.0.0.1:48810");
  - **no document is open** → open the model you want to work on;
  - the **port was changed** → changing it in Settings re-registers and restarts
    the bridge, but Claude Code must be restarted to pick up the new `--port`.

Don't guess your way past a failed `status` — surface the specific remediation above.

## The tools

### The schedule round-trip tools

| Tool | Use |
|------|-----|
| `status` | Health check; returns add-in version + open document title. **Call first.** |
| `list_schedules` | Discover schedules — returns `[{id, name}]`. |
| `read_schedule` | Read one schedule (by `id` or `name`) → compact view: columns (with `header`/`binding`/`writable`) and rows keyed by element `uniqueId`. This is how you "see" the data before editing. |
| `set_parameter` | Write **one** parameter on an element (`uniqueId` + `value`, plus `parameterId` *or* `fieldName`). Verifies the write and rolls back on failure. |
| `set_parameters` | Write **many** edits in a **single transaction** (`{edits:[…]}`). Per-edit verify; whole batch rolls back on a fatal error. Prefer this for multiple cells. |
| `execute_revit_code` | Run arbitrary **C#** against the Revit API in-process (Roslyn). The escape hatch when no purpose-built tool fits. Not Python — see the tool's own description for the script contract. |

### …and roughly forty more

**The table above is not the inventory.** The `transom` server also advertises ~38 purpose-built model
tools — element query/filter/properties/modify, transforms, views, sheets, levels, rooms, dimensions,
detail lines, tagging, material quantities, model statistics, colour splash, and more. **Call `tools/list`
(or just look at your available `mcp__transom__*` tools) rather than assuming this file is exhaustive** —
if a task looks like it needs a Revit operation, there is probably a tool for it.

A few are deliberately gated off in this build pending review, and the bridge refuses them independently:
`check_clashes`, `load_family`, `place_family`, `list_families`, `export_document`, `export_ifc`,
`save_document`.

**Wire contract for the model tools: every point, length and offset is in MILLIMETRES.** Angles are
degrees. The bridge converts to Revit's internal feet for you — don't pre-convert.

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
- **Parameters on elements inside Revit groups — know which kind:**
  - A **project/shared** instance param ("blue" cell) can vary per group instance, so
    `set_parameter` works on a group member — **but only once the parameter is ALREADY set to
    "vary by group instance".** The bridge does **not** enable that flag for you: it is a
    one-way model change, so only the Hub's import flow (option 1, with the user's explicit
    consent) ever sets it. If the flag isn't set yet, your write fails verification and rolls
    back with *"write not verified — value did not take"*, which does **not** name this as the
    cause. When you see that on a grouped project/shared param, tell the user to run the edit
    through the Schedule Hub's import once and pick option 1 — don't retry.
  - **Identity built-ins that Revit lets differ between group instances — `Mark` and `Number`
    (door/room number) — DO write directly on a group member**, exactly like an ungrouped
    element. Live-verified. Don't route these to the Edit Group flow.
  - **Every other built-in** instance param on a group member (Comments, Finish, the geometry
    ones like Sill/Head Height, …) **cannot** vary per instance — Revit refuses a direct write:
    *"Changes to groups are allowed only in group edit mode."* Do **not** retry `set_parameter`.
    Apply it through the **Claude-Assist group-edit flow** (next section) — the manual Revit
    "Edit Group" UI via the **`transom-ui-assist`** MCP.
- A **type** parameter write affects every instance of that type — the result includes
  `instancesAffected`. Confirm with the user before changing type params.
- Values are passed as strings; the bridge coerces to the parameter's storage type.

## Claude-Assist group-edit flow (grouped BUILT-IN params)

When the user picks **Claude-Assist** for a grouped built-in column, Transom writes a
**group-edits JSON** (`{"tool":"Transom","kind":"group-edits"}`) plus a how-to markdown
(`Transom - Apply staged edits with Claude.md`) into a folder, for you to apply. These can't
go through `set_parameter` (the bridge refuses grouped built-ins, above) — you apply them by
**driving Revit's real "Edit Group" mode in the UI**, which requires a SECOND MCP server:

- **`transom-ui-assist`** (the ClickHelper UI-automation MCP — registered alongside the data
  bridge). It exposes screenshot / find / click / type / key / dialogs tools to drive the
  Revit window like a human. Inside Edit Group mode the **data bridge / Revit API is
  unavailable** — `transom-ui-assist` is the only way to select the member, set the param in
  the Properties palette, and Finish. The Revit API's only role is BEFORE entering / AFTER
  finishing: select+zoom, the red color-override locator, and the post-Finish verify.
- **Per-entry routing** (the JSON's `note` is authoritative): an **empty `group`** = an
  ungrouped instance → a plain `set_parameter` (no Edit Group mode). A **non-empty `group`
  with `parameterId < 0`** = the grouped built-in → the Edit-Group-mode UI path. A built-in
  edited this way goes **uniform across the whole group type** (its definition); per-instance
  **divergent** built-in values aren't possible while grouped — those need an instance shared
  param (import **option 2b**), which never ungroups and is exclusion-safe.
- Full step-by-step is in the staged `Transom - Apply staged edits with Claude.md` (excluded
  members / attached detail groups / nested groups do **not** block the manual path — a person
  edits through them; proven live). Throwaway model first; if workshared, **never** sync mid-run.

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
