# Claude UI consolidation — one Settings toggle, one status panel

**Status: implemented (v1.7.0).** Written 2026-07-12 against v1.6.1 (`85a3186`); implemented same day.
The mockup-approved refinements made it in: the bridge port sits under a collapsed "Advanced" expander,
and an empty exchange folder defaults to `Documents\Transom Exchange` on first enable.

## Problem

The Claude surface is spread across four ribbon buttons ("Set up Claude", "Claude Bridge",
"Bridge Status", "Settings") plus a "Claude mode" dropdown (Off / Verify / Assist) inside
Settings. That's five controls for what is conceptually one feature, and the mode dropdown
isn't even persisted — it silently resets to Off every Revit session.

## Target design

1. **One boolean: Claude-Assist is ON or OFF.** The Verify (read-only) middle mode is removed.
2. **One toggle in the Settings tab** turns it on/off, persisted in `settings.json`.
3. **First ON runs setup** (shim install + both MCP registrations + restart-Claude notice),
   then starts the bridge. Later ONs just start the bridge.
4. **An always-visible status panel in Settings** replaces the Bridge Status dialog: shows
   "Not set up" before first setup, otherwise the full checklist (bridge + port, session
   token, shim, both registrations, Claude app running, active model).
5. **Ribbon: the three Claude action buttons are deleted.** Only Settings remains.

## Current-state inventory (everything that must change)

### Ribbon — `Application.cs` (lines ~45–82)
| Button | Command | Disposition |
|---|---|---|
| Set up Claude | `SetupClaudeCommand` | delete button; logic → `ClaudeSetup` service called by the toggle |
| Claude Bridge | `BridgeToggleCommand` | delete button; lifecycle → `BridgeRuntime` service |
| Bridge Status | `BridgeStatusCommand` | delete button; checks → status panel in Settings |
| Settings | `SettingsCommand` | keep — move onto the "Schedule Tools" panel; delete the now-empty "Claude Assist" panel |

`ClaudeAvailability` (greys action buttons when Claude.exe isn't running) loses both its
consumers → delete the availability class; keep `ClaudeDetector` for a status row.

### Settings model — `Core/TransomSettings.cs`
- **Add** `public bool ClaudeAssistEnabled { get; set; }` (default `false`).
- No migration needed: the old `ClaudeMode` was never persisted.
- Existing `BridgeSelfHostPort`, `ExchangeFolder`, `McpRegisteredPort` stay as-is.

### View model — `ViewModels/TransomViewModel.cs`
`ClaudeMode` ("Off" | "Verify (read-only)" | "Assist (write)") has exactly four consumers;
all collapse onto the single flag (`enabled` below):

| Site | Today | Becomes |
|---|---|---|
| :233, :342 | `_claudeMode` prop + `ClaudeModes` array | delete; add `IsClaudeAssistEnabled` observable, loaded from settings in ctor (like :285–288) |
| :457 | `stage = ClaudeMode != "Off" && ExchangeFolder != ""` | `stage = enabled && ExchangeFolder != ""` |
| :463 | `ClaudeAssistEnabled = ClaudeMode.StartsWith("Assist")` | `= enabled` |
| :585 | `WriteRunLog = ClaudeMode != "Off"` | `= enabled` |
| :707 | `assist = ClaudeMode.StartsWith("Assist")` | `= enabled` |

New VM behavior:
- `OnIsClaudeAssistEnabledChanged(bool on)`:
  - **ON** → run `ClaudeSetup.EnsureAll(port)` (idempotent — shim + `McpRegistration.Register`
    + `ClickHelperRegistration`); if anything was newly registered, show the one restart-Claude
    notice (reuse `SetupClaudeCommand`'s message text); then `BridgeRuntime.Start(port)`;
    persist `true`; refresh status.
  - **OFF** → `BridgeRuntime.Stop()` (also deletes the session token); persist `false`; refresh status.
  - Setup/registration is file-I/O only — run it off the UI thread like `RefreshBridgeAsync`,
    with the toggle disabled while in flight so a double-click can't race.
- Status snapshot: new `ClaudeStatus` record (Core) computed from the same five checks
  `BridgeStatusCommand` does today (bridge running + port, token file, shim present + date,
  `~/.claude.json` has `transom` / `transom-ui-assist`), plus `ClaudeDetector.IsRunning()`
  and active doc title. VM exposes it as bound rows; refreshed on toggle, on `RefreshBridge`,
  and when the Settings tab is selected.
- `RefreshBridgeAsync` (:1470): keep the `BridgeProbe` reachability check as one status row;
  rewrite the two status strings (they currently say "Assist enabled" / "Verify (read-only)
  still works").
- `OnBridgePortChanged` (:1507): today it only saves + probes. With the toggle ON it must now
  also **re-register the shim (port is baked into the registration) and restart the listener**
  — the old flow relied on the user re-clicking "Set up Claude" after a port change.
- ClickHelper status strings (:945–954) reference "the Claude Bridge is ON" → reword to
  "Claude Assist is on in Settings".

### Bridge lifecycle — new `Core/Bridge/BridgeRuntime.cs`
Extract `BridgeToggleCommand`'s static `_server/_handler/_event` + token read/write/delete into
a static `BridgeRuntime` with `Start(port)`, `Stop()`, `IsRunning`, `TokenFilePath`.

**Threading constraint:** `ExternalEvent.Create` requires a valid Revit API context. Today that
context is the ribbon command's `Execute()`. After the move there are two valid contexts:
- `Application.OnStartup` — create the handler + `ExternalEvent` eagerly there (cheap), so the
  Settings toggle (modeless WPF, no API context) only ever calls `Start/Stop` on already-built
  objects. **This is the chosen approach.**
- The Hub's own external events are created when the Hub command constructs the VM — acceptable
  fallback, but then enabling at startup (below) wouldn't work.

**Auto-start:** in `OnStartup`, after `EnsureBundledShimAndAutoRegister`, if
`ClaudeAssistEnabled` is true → `BridgeRuntime.Start(port)` silently. Without this, a persisted
ON would show a dead bridge after every Revit restart, which is exactly the confusion this
redesign removes. (Startup already does silent shim install/registration, so a silent listener
start is consistent; it's loopback + token-gated, so no security change.)

### Setup — new `Core/ClaudeSetup.cs` (or static on `McpRegistration`)
Lift `SetupClaudeCommand.Execute()` body into `ClaudeSetup.EnsureAll(port)` returning
`(updated, errors, messages, componentsMissing)` so the VM can decide whether to show the
restart notice or an error dialog. Delete `SetupClaudeCommand`, and the already-off-ribbon
`RegisterBridgeCommand` + `UiAssistSetupCommand` (dead since #107).

### Settings tab — `Views/TransomView.xaml` (lines ~811–856)
- Delete the "Claude mode" label + ComboBox + explainer (:822–825).
- Add at the top of the Claude section: a ToggleButton/switch bound to
  `IsClaudeAssistEnabled` ("Claude Assist — let a connected Claude client review exports,
  apply staged edits, and run Revit API operations"), with the "How Claude works" button
  staying where it is.
- Replace the one-line `BridgeStatus` info border (:844–846) with the status panel:
  - toggle OFF + never set up → single line "Not set up — turn Claude Assist on to set it up."
  - otherwise → ✓/✗ rows (bridge listening on port / session token / shim deployed + date /
    data bridge registered / UI-Assist registered / Claude app running) + active model +
    Transom version, mirroring `BridgeStatusCommand`'s dialog content.
- Keep "Write bridge port" (advanced) and "Claude exchange folder" rows; retitle the section
  from "Claude QA settings" to "Claude Assist".

### Exchange folder interplay
Staging still requires a non-empty `ExchangeFolder` (:457). To keep "ON = it works": when
toggling ON with an empty folder, default it to
`%USERPROFILE%\Documents\Transom Exchange` (create on first stage), leaving it editable.
Otherwise the toggle would look on while exports silently skip staging.

### Text/doc sweep (button names appear in all of these)
- `Views/ClaudeAssistHelpDialog.xaml` :56, :80, :84 — setup steps say "Set up Claude on the
  ribbon … turn Claude Bridge ON" → "turn Claude Assist on in Settings".
- `Views/HelpDialog.xaml` :180 (still says the pre-#107 "Register with Claude"!), :213, :232.
- `ClaudeGuideMarkdown()` + staged-JSON `note` (VM :1026–1041, :1532ff) — grep for ribbon/button
  references during implementation; update if any.
- Shipped guidance: `claude/CLAUDE.md` :23, `claude/transom-connect.md` :14, `claude/README.md`
  :16 — these tell the Claude client to instruct the *user* to click the ribbon button; change
  to "enable Claude Assist in Transom Settings (Schedule Hub → Settings)".
- `Readme.md` :37–38 setup steps; Status paragraph at next release.
- Ribbon tooltip for the relocated Settings button.

## Behavioral changes to be aware of (intentional)

1. **Verify mode is gone.** Old "Verify" staged the workbook/run-log but blocked writes; now
   enabled = full assist. Anyone who wanted read-only reviews simply doesn't ask Claude to write
   (and the bridge token/loopback model is unchanged).
2. **The flag persists.** Today Claude mode resets to Off each session; the new toggle stays on,
   and the bridge auto-starts with Revit while it's on.
3. **Setup no longer requires Claude to be running** (the old buttons were greyed via
   `ClaudeAvailability`). Registration is config-file writes, so gating was unnecessary; the
   status row "Claude app running: ✗" carries that information instead.

## Implementation order

1. `TransomSettings.ClaudeAssistEnabled`; `ClaudeStatus` snapshot type.
2. `BridgeRuntime` extraction; `OnStartup` event creation + auto-start.
3. `ClaudeSetup.EnsureAll` extraction.
4. VM: swap `ClaudeMode` → `IsClaudeAssistEnabled` (4 consumer sites), toggle handler,
   status rows, port-change re-register, status-string rewrites.
5. XAML Settings tab rework.
6. Ribbon cleanup: delete 3 buttons + Claude panel, move Settings, delete
   `SetupClaudeCommand` / `BridgeToggleCommand` / `BridgeStatusCommand` /
   `RegisterBridgeCommand` / `UiAssistSetupCommand` / `ClaudeAvailability`.
7. Text/doc sweep (list above).
8. Bump `AppInfo.Version`, build R25/26/27, live-test checklist below, release.

## Test checklist

- Fresh profile (no `~/.claude.json` entries, no shim): toggle ON → setup messages + restart
  notice, bridge starts, status all ✓ (Claude-running row per reality). `transom` status tool
  answers from Claude Code after restart.
- Toggle OFF → bridge stops, token file deleted, status shows off; export stages nothing.
- Restart Revit with toggle ON → bridge listening without any clicks (status ✓).
- Change port while ON → re-registered + listener restarted on new port; Claude reconnects
  after client restart.
- Export with ON + folder set → staged workbook + run-log + guide file; with OFF → plain export.
- Grouped built-in edit routes to staged Claude path only when ON (VM :707 site).
- Both Revit 2025 + 2026 sanity pass (R27 build-only).
