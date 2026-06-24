---
description: Connect to the live Revit model via the Transom MCP bridge and confirm it's healthy
---

You are establishing the first connection to a Revit session through the **Transom**
MCP bridge. Do this now:

1. Call `mcp__transom__status`.
2. Report the outcome to the user:
   - **Connected** — `{"ok":true,…,"doc":"<title>"}`: say which document you're
     connected to and the add-in version, then call `mcp__transom__list_schedules`
     and show the available schedules so they can pick one.
   - **Tools missing** (no `transom` tools in this session): the MCP server isn't
     loaded. Tell them to click **"Set up Claude"** in the Transom ribbon,
     then restart Claude Code (new MCP servers load only at startup).
   - **Error / timeout**: the bridge isn't reachable. List the likely fixes — toggle
     the Transom **bridge on** in Revit (should read "listening on 127.0.0.1:48810"),
     **open a document**, or check the **port** matches what was registered.

Keep it to a short status line plus the schedule list (or the specific fix). Do not
write to the model in this command — this is connect-and-verify only.
