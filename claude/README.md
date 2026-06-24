# Claude drop-in files

These are optional guidance files for **Claude Code** working with Transom's MCP
bridge. They are not required for the add-in to function — they just make Claude
Code connect cleanly on the first turn.

- **`CLAUDE.md`** — drop-in instructions telling Claude Code the `transom` MCP
  server exists, how to connect (call `status` first), the available tools, and the
  safe write workflow. Place it where Claude Code auto-loads instructions: copy it to
  your project root as `CLAUDE.md`, or to `~/.claude/CLAUDE.md` to apply it globally.

- **`transom-connect.md`** — a connect-and-verify command/prompt. Run it to call
  `status`, confirm which model you're attached to, and list the schedules (or get
  the specific fix if the bridge isn't reachable).

To use the bridge, click **"Set up Claude"** in the Transom ribbon, then restart
Claude Code so it picks up the new MCP server.
