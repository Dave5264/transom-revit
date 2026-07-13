# Transom â€” MCP config merge (admin-free shim registration)

Design doc for **F4** of `BUNDLED_MCP_PLAN.md (retired 2026-07-13; archived locally)`. Covers how the bundled
`Transom.McpShim.exe` (installed per-user by `install/BundledMcp.wxs` into
`%LocalAppData%\Transom\mcp\`) gets registered with the user's MCP client(s)
**without administrator rights**, idempotently, without clobbering other servers.

Companion file: `install/BundledMcp.wxs` (the per-user WiX component group).

---

## 1. What we register, and where

The MCP client launches the shim as a **stdio** server. Registration means adding
one `transom` entry to the client's **user-level** config files. We never touch a
machine-level / `Program Files` / `HKLM` config.

### 1a. Target config files (all per-user, all writable without admin)

| Client | Config path | Servers key |
|--------|-------------|-------------|
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` | `mcpServers` |
| Claude Code (user scope) | `%USERPROFILE%\.claude.json` | `mcpServers` |

> Claude Code historically also accepts `%APPDATA%\Claude\claude_code_config.json`
> and project-scoped `.mcp.json`. We deliberately write only the **user scope**
> (`~/.claude.json` `mcpServers`) so the shim is available in every project without
> per-project edits. If the user prefers project scope they can copy the same
> entry into a repo `.mcp.json` by hand. Both files live under the user profile â€”
> no elevation.

### 1b. The exact entry to merge

`command` is the **resolved per-user path** of the installed shim (read from the
HKCU value `Software\Transom\Mcp\ShimPath` that `BundledMcp.wxs` writes, or
computed as `%LocalAppData%\Transom\mcp\Transom.McpShim.exe`). `args` pins the
loopback port (constraint #2; default 48810, kept in sync with
`TransomSettings.BridgeSelfHostPort`).

```jsonc
{
  "mcpServers": {
    "transom": {
      "command": "C:\\Users\\<you>\\AppData\\Local\\Transom\\mcp\\Transom.McpShim.exe",
      "args": ["--port", "48810"]
    }
  }
}
```

Notes:
- In real JSON the backslashes in `command` must be escaped (`\\`) â€” shown above.
- We add **only** the `transom` key under the existing `mcpServers` object. Every
  other server the user already has is left byte-for-byte intact.
- No `env`, no `cwd` needed: the shim is self-contained and resolves its bridge
  target from `--port` (falling back to `TRANSOM_BRIDGE_PORT`, then 48810).

---

## 2. Idempotent, non-clobbering merge algorithm

Run this identically for each target config file. It is safe to run any number of
times (first-run, every-run, or from a ribbon button).

```
for each configPath in [claude_desktop_config.json, ~/.claude.json]:
    if not exists(parentDir(configPath)):       # e.g. %APPDATA%\Claude not created yet
        skip this file        # client not installed â†’ nothing to register for it
    if not exists(configPath):
        root = {}             # create a fresh, minimal config
    else:
        text = read(configPath)
        if text is empty/whitespace: root = {}
        else:
            try: root = parseJson(text)
            except: BACK OFF â€” do NOT overwrite; log + surface to user, return
                    (never destroy a config we cannot parse)

    if "mcpServers" not in root or root["mcpServers"] is not an object:
        root["mcpServers"] = {}

    desired = { "command": <resolved shim path>, "args": ["--port", <port>] }

    existing = root["mcpServers"].get("transom")
    if existing == desired:
        continue              # already correct â†’ no write, fully idempotent
    # update-or-insert ONLY our key; all sibling servers untouched
    root["mcpServers"]["transom"] = desired

    # atomic, non-destructive write:
    write(configPath + ".tmp", prettyJson(root))   # preserve 2-space indent
    backupOnce(configPath -> configPath + ".transom.bak")  # first change only
    atomicReplace(configPath + ".tmp" -> configPath)
```

Key properties:
- **Idempotent:** if the `transom` entry already equals the desired value, we
  write nothing. Re-running is a no-op.
- **Non-clobbering:** we only ever set the single `mcpServers.transom` key. Other
  servers and all unrelated top-level keys (e.g. `globalShortcut`, Claude Code
  `projects`, `numStartups`) are round-tripped untouched.
- **Fail-safe on corrupt JSON:** if the existing file does not parse, we abort
  rather than overwrite â€” we never turn a user's hand-edited config into rubble.
- **Atomic:** write temp + replace, so a crash mid-write can't truncate the file.
- **One-time backup:** `*.transom.bak` written the first time we modify, so the
  user can revert.

Removal (uninstall / "disable" button) is the mirror: parse, `delete
mcpServers.transom` only if it points at our shim path, write back atomically.

---

## 3. Two implementation options

### Option A â€” per-user first-run step inside the add-in (RECOMMENDED)

The add-in (already running as the user, in `%AppData%\â€¦\Revit\Addins`) performs
the merge on first launch after install, and exposes a **ribbon / settings
button** ("Register MCP bridge with Claude") that re-runs the same idempotent
merge on demand.

- **Pros**
  - Runs in the **user's own session** â†’ inherently no elevation, correct
    `%APPDATA%`/`%USERPROFILE%` for the actual user (an MSI custom action may run
    in a different/again user context and resolve the wrong profile).
  - Trivially **idempotent and re-runnable** â€” handles the case where the user
    installs Claude Desktop *after* Transom, or resets their config, or changes
    the port in `TransomSettings` (we just re-merge with the new port).
  - No MSI custom-action sequencing, no `Impersonate` flags, no rollback custom
    action to author. Pure managed code reusing the add-in's JSON stack.
  - Can show clear UI ("Claude Desktop not found â€” install it then click here").
  - Uninstall story is clean: a "Disable bridge" button removes the entry; even
    if the user just uninstalls the MSI, a stale entry only points at a missing
    exe (the client silently fails that one server â€” harmless).
- **Cons**
  - Registration happens at first add-in launch, not at MSI finish. (Acceptable:
    the shim is only useful once Revit + Transom are running anyway.)
  - If the user never launches Revit, registration never happens (also fine â€”
    nothing to bridge to).

### Option B â€” non-elevated WiX custom action

A deferred-but-**not**-elevated (`Impersonate="yes"`, no `Elevated`) custom
action in the SingleUser MSI runs the merge at install time.

- **Pros**
  - Registered the instant the MSI finishes, before Revit is opened.
- **Cons**
  - Must run impersonated to hit the right `%APPDATA%`; perUser MSIs already run
    unelevated, but custom-action context bugs are a classic footgun.
  - Harder to make robust/idempotent and to re-run when the user installs Claude
    later or changes the port â€” you'd need a separate repair/maintenance path.
  - Adds a binary/managed custom action to the WixSharp build and a rollback
    action to undo on failed install â€” more surface, more to test.
  - Worse UX for "Claude not installed yet" (can't prompt meaningfully at MSI
    time).

### Recommendation

**Option A.** Do the merge as a per-user first-run step in the add-in plus a
ribbon/settings button, using the shared idempotent algorithm in Â§2. Keep the MSI
(`BundledMcp.wxs`) responsible only for *placing* the shim and recording its path
in HKCU; let the add-in own *registration*. This keeps the installer dead-simple
and admin-free, and makes registration resilient to install-order and port
changes. (Option B can be added later as a convenience, reusing the same merge
code, but is not required.)

---

## 4. Why this is admin-free (maps to the three constraints)

`BUNDLED_MCP_PLAN.md (retired 2026-07-13; archived locally)` defines three load-bearing constraints. Each step here
honors them:

1. **Per-user install only.** `BundledMcp.wxs` puts the shim under
   `LocalAppDataFolder` (`%LocalAppData%\Transom\mcp\`) with an **HKCU** keypath â€”
   no `ProgramFilesFolder`, no `HKLM`, no service. The whole MSI is `perUser`
   (`Installer.cs` â†’ `BuildSingleUserMsi`, `Scope = InstallScope.perUser`), so
   Windows installs it with **no UAC prompt**. The config files we edit
   (`%APPDATA%\Claude\â€¦`, `~/.claude.json`) are inside the user's own profile â€”
   writable without elevation.
2. **Loopback TcpListener on a high port.** The shim's `--port 48810` targets
   `127.0.0.1:48810`, the in-Revit `BridgeServer`'s `TcpListener` on
   `IPAddress.Loopback`. Loopback + port > 1024 means **no URL ACL** (`netsh http
   add urlacl` = admin, avoided by not using `HttpListener`) and **no Windows
   Firewall dialog** (loopback traffic never prompts; `0.0.0.0` would). Nothing in
   registration opens a port or touches the firewall.
3. **Self-contained shim.** The exe is published single-file / self-contained, so
   registration points the MCP client at one standalone binary with **no "install
   .NET/Node" step** â€” no runtime installer (which would need admin).

**SmartScreen note (not elevation):** the shim is currently unsigned, so on first
launch Windows SmartScreen may show *"Windows protected your PC"* with a *More
info â†’ Run anyway* link. That is a **single click**, not a UAC / admin
elevation. It can be removed entirely later by code-signing the exe (out of scope
for this pass per the plan). It does not block the admin-free guarantee.

---

## 5. Verification checklist â€” confirm NO UAC prompt anywhere

Walk the full path **download â†’ install â†’ launch â†’ use** on a **standard
(non-admin) Windows user account** and confirm a UAC prompt never appears:

- [ ] **Download.** Fetch `Transom-<v>-SingleUser.msi` from the GitHub release.
      Browser may warn about an unsigned download â†’ that is a click, not UAC.
- [ ] **Install.** Double-click the MSI. Confirm:
      - [ ] No UAC shield / consent dialog appears.
      - [ ] Add-in lands in `%AppData%\Autodesk\Revit\Addins\<ver>\` (existing).
      - [ ] Shim lands in `%LocalAppData%\Transom\mcp\Transom.McpShim.exe`.
      - [ ] HKCU has `Software\Transom\Mcp\ShimPath` / `BridgePort` (no HKLM key).
      - [ ] Verify perUser: `msiexec` log shows `MSIINSTALLPERUSER=1`,
            `ALLUSERS` empty; the entry appears in *Apps & features* for the
            current user only.
- [ ] **Register (Option A).** Launch Revit â†’ Transom on ribbon â†’ click
      "Register MCP bridge with Claude" (or confirm first-run auto-merge):
      - [ ] No UAC prompt.
      - [ ] `%APPDATA%\Claude\claude_desktop_config.json` now contains a
            `mcpServers.transom` entry with the correct shim path + `--port`.
      - [ ] Any pre-existing servers in that file are still present and unchanged.
      - [ ] Re-click the button â†’ file is byte-identical (idempotent no-op).
      - [ ] Repeat for `~/.claude.json` if Claude Code is installed.
      - [ ] If a Claude config is hand-corrupted, the merge aborts and warns
            instead of overwriting.
- [ ] **Launch shim.** Start Claude Desktop / Claude Code; it spawns
      `Transom.McpShim.exe`:
      - [ ] At most a SmartScreen *Run anyway* click (unsigned) â€” **no UAC**.
      - [ ] `tools/list` shows: status, list_schedules, read_schedule,
            set_parameter, set_parameters.
- [ ] **Use.** In Revit, toggle the bridge ON (ribbon). Confirm:
      - [ ] No firewall dialog appears when `BridgeServer` starts its
            `TcpListener` on `127.0.0.1:48810` (loopback + high port).
      - [ ] A Claude `tools/call` (e.g. `status`) round-trips and returns
            `{"ok":true,...}` with no prompt of any kind.
- [ ] **Uninstall.** *Apps & features â†’ Transom â†’ Uninstall*:
      - [ ] No UAC prompt; shim folder removed.
      - [ ] (If implemented) "Disable bridge" removed the `transom` config entry,
            leaving other servers intact; otherwise the stale entry harmlessly
            points at a now-missing exe.
