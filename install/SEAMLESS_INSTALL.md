# Making the bundled MCP bridge install & connect seamlessly

This is a post-mortem + change spec written after a real first-run failure on a
clean machine. The "Register Claude Bridge" dialog reported:

> ⚠ Shim not found at `…\Local\Transom\mcp\Transom.McpShim.exe` (install or publish it first).

Everything else was healthy — the bridge was listening on `127.0.0.1:48810`, the
`bridge.token` existed, and the add-in (`Transom.dll` v1.3.0) was deployed — but
there was **no MCP connection** because the shim executable was never on disk.

A direct probe confirmed the rest of the chain is sound once the shim exists:

```
POST http://127.0.0.1:48810/call   X-Transom-Token: <token>   {"tool":"status","args":{}}
→ {"ok":true,"tool":"Transom","version":"1.3.0","doc":"SAMPLE PROJECT_ARCH_R25"}
```

So the *only* missing piece on a fresh install is **getting `Transom.McpShim.exe`
placed and registered automatically.** This doc lists exactly what to change.

---

## Root cause: the shim is designed-for but never built or bundled

The bundled-MCP design (`docs/BUNDLED_MCP_PLAN.md` F3/F4, `MCP_CONFIG_MERGE.md`) is
correct and complete *on paper*. The gap is that **none of it is wired into the
build/installer**:

| Piece | State | Evidence |
|-------|-------|----------|
| `source/Transom.McpShim` project | ✅ exists, builds, self-contained single-file `win-x64` | `Transom.McpShim.csproj` |
| Publish step in the build pipeline | ❌ **missing** — nothing ever runs `dotnet publish` on the shim | `build/` (ModularPipelines) only compiles/packs the add-in |
| Shim placed in the MSI | ❌ **missing** — `BundledMcp.wxs` is an unwired spec | see below |
| `$(var.McpShimPublishDir)` | ❌ never defined / passed | `BundledMcp.wxs` line 70 references it; nothing sets it |
| Config-merge (`McpRegistration`) | ✅ implemented + correct | runs from the ribbon button |
| First-run auto-merge (Option A) | ⚠️ partial — only the manual ribbon button exists | `RegisterBridgeCommand` |

Specifically:

* `install/Installer.Generator.cs` globs **only the per-Revit-version add-in output**
  (`new Files(feature, "{dir}\\*.*")`) into the MSI.
* `install/Installer.cs` installs **only** to `%AppDataFolder%\Autodesk\Revit\Addins\`
  (SingleUser) / `%CommonAppData%`/`%ProgramFiles%` (MultiUser). It never references
  `BundledMcpComponents` and never merges `BundledMcp.wxs`.
* `install/BundledMcp.wxs` — the per-user component that *would* drop the shim into
  `%LocalAppData%\Transom\mcp\` and record `HKCU\Software\Transom\Mcp\ShimPath` — is a
  reviewable **spec fragment that is not included in either MSI**.

Net effect: the MSI ships the add-in but **no shim**, so `McpRegistration.ShimPresent()`
is false on first run and registration warns instead of connecting.

A secondary, dev-environment issue (not shipped to users, but it blocks *building*):
the build machine must have a **.NET SDK matching `global.json` (10.0.x)**. A clean
box may have only .NET *runtimes* (no SDK), in which case `dotnet publish` fails with
"No .NET SDKs were found." CI/build must install the SDK (admin-free option:
`dotnet-install.ps1 -Channel 10.0`).

---

## Changes required (in priority order)

### 1. Build pipeline — publish the shim before packaging  *(blocking)*

Add a publish step to the Pack pipeline (`build/Modules`), before the MSI build:

```powershell
dotnet publish source/Transom.McpShim/Transom.McpShim.csproj -c Release -r win-x64
# publish-time flags (PublishSingleFile, SelfContained, IncludeNativeLibrariesForSelfExtract,
# InvariantGlobalization) are already baked into the .csproj — no extra args needed.
```

Output: `source/Transom.McpShim/bin/Release/net8.0/win-x64/publish/Transom.McpShim.exe`
(~67 MB, bundles its own .NET 8 runtime). Capture that folder and pass it onward as
`McpShimPublishDir`. Ensure the build agent has a .NET 10 SDK (per `global.json`).

### 2. Installer — actually place the shim  *(blocking)*

Pick **one** of these (2a is the most robust and is recommended):

**2a. Ship the shim inside the add-in payload + copy-on-first-run (recommended).**
Copy the published `Transom.McpShim.exe` into the add-in folder that the MSI already
installs (`…\Revit\Addins\<ver>\`). On add-in startup, if
`%LocalAppData%\Transom\mcp\Transom.McpShim.exe` is missing or older, copy it there.
- Pros: works identically for **SingleUser and MultiUser** MSIs (a per-machine install
  can't write each user's `%LocalAppData%`; first-run, which runs *as the user*, can).
  No new WiX wiring. Self-heals if the user deletes the shim.
- Cons: ~67 MB duplicated per installed Revit version in the payload. Acceptable; or
  publish the shim into a single shared add-in subfolder rather than per-version.

**2b. Wire `BundledMcp.wxs` into the SingleUser MSI.** Either merge the fragment
(`project.WixSourceGenerated += doc => doc.MergeWith(XDocument.Load("install/BundledMcp.wxs"))`
and add `ComponentGroupRef Id="BundledMcpComponents"` to the per-user feature), or add
an equivalent WixSharp `Dir`/`File` for
`%LocalAppData%\Transom\mcp\Transom.McpShim.exe` + the HKCU keypath/`ShimPath`/`BridgePort`
values. Set `-dMcpShimPublishDir=<publish folder from step 1>`.
- Note: this covers **SingleUser only**. For MultiUser you still need the first-run
  copy from 2a, so 2a alone is simpler overall.

### 3. Registration — make it automatic on first run  *(high value)*

`McpRegistration.Register()` is already idempotent, non-clobbering, atomic, and
verifies its write. Two improvements:

1. **Auto-merge on first launch (Option A).** On add-in startup, if the shim is present
   and a one-time settings flag (`McpRegistered`) is unset, call `Register(port)` once,
   then set the flag. Keep the ribbon button for re-runs / port changes. This removes
   the "remember to click Register" step entirely.
2. **Resolve the shim path from HKCU when present.** `McpRegistration.ShimPath` currently
   only *computes* `%LocalAppData%\Transom\mcp\…`. Prefer `HKCU\Software\Transom\Mcp\ShimPath`
   (written by `BundledMcp.wxs` / the first-run copy) and fall back to the computed path,
   so a non-default install still registers the correct path.
3. **Keep `--port` in sync with `TransomSettings.BridgeSelfHostPort`.** When the user
   changes the port, re-run the merge (the entry is `{"command":shim,"args":["--port",N]}`).

### 4. Self-heal when the shim is missing  *(closes the exact failure we hit)*

When `ShimPresent()` is false, the Register dialog should do more than warn: offer a
one-click **"Install bridge components"** that performs the 2a copy from the add-in
payload. That turns the dead-end warning into a fix.

### 5. Signing (nice-to-have, not blocking)

The shim is unsigned, so first launch may show SmartScreen *"Windows protected your
PC" → More info → Run anyway* (a single click, **not** a UAC/admin prompt). Code-sign
`Transom.McpShim.exe` to remove it. Does not affect the admin-free guarantee.

---

## What stays the same (already correct — don't regress)

- **Admin-free everywhere.** Per-user MSI (`Scope = perUser`), shim under
  `%LocalAppData%`, HKCU keypath (no HKLM), loopback `TcpListener` on a high port
  (no `netsh` URL ACL, no firewall prompt), self-contained shim (no ".NET install"
  step). Keep all of it.
- **Token auth.** `bridge.token` in `%LocalAppData%\Transom\` gates the loopback
  endpoint; the shim sends it as `X-Transom-Token`. Don't weaken this — loopback is
  not an authorization boundary.
- **The config-merge algorithm** in `MCP_CONFIG_MERGE.md` §2 (idempotent,
  non-clobbering, fail-safe on corrupt JSON, one-time `.transom.bak`, atomic replace).

---

## Acceptance test

Use the existing checklist in `MCP_CONFIG_MERGE.md` §5 ("confirm NO UAC prompt
anywhere"), on a **standard (non-admin)** account. After the changes above, these two
lines — which fail today — must pass:

- [ ] Shim lands in `%LocalAppData%\Transom\mcp\Transom.McpShim.exe` after install.
- [ ] A Claude `tools/call` `status` round-trips `{"ok":true,...}` with the open
      document title, with no prompt of any kind.

One operational reminder that is **not** a code change: after registration, the MCP
client must be (re)started once so it launches the newly-registered shim. The
`claude/` drop-in next to this file primes Claude to handle that first connection
gracefully (status handshake + clear remediation).
