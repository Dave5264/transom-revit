# Transom — agent guide (read this first)

Transom is an Autodesk **Revit add-in** (C#/.NET, WPF) that round-trips Revit schedules ↔ Excel, with an optional **Claude-Assist** layer that lets a Claude client read/write the live model over a local MCP bridge. This file is for a **Claude agent** picking up the codebase on any machine — especially to **build, deploy, and cut installer releases** while testing Claude-Assist.

Repo: `Dave5264/transom-revit`. Default branch: `main`. Primary dev path on the maintainer's machine: `C:\Users\daveo\dev\Revit Coding\ScheduleExcel`.

---

## 1. Layout (the parts that matter)

| Path | What it is |
|---|---|
| `source/Transom/` | The add-in: `Commands/`, `Views/` (WPF/XAML), `ViewModels/`, `Core/`. Entry: `Application.cs` (ribbon). Version: `Core/AppInfo.cs`. |
| `source/Transom.McpShim/` | The **data-bridge MCP server** (`Program.cs`) — stdio JSON-RPC, forwards `tools/call` to the in-Revit loopback HTTP bridge on `127.0.0.1:48810`. Self-contained AOT exe. **Exception: the `aire_*` tools do NOT go over the bridge** — it references `Transom.Aire.Core` and answers them in-process, so AIRE works with Revit closed. |
| `source/Transom.Aire.Core/` | AIRE's Revit-free, **WPF-free** half: `AireEngine`, `AireJob`/`AireVideoJob`/`AireJobManager`, `AireSettings` (DPAPI by P/Invoke, no NuGet), `HiggsfieldClient`/`HiggsfieldCatalog`, `AireLogReport`, `ImageHeader`, and `AireBridge` — the single implementation of every `aire_*` tool. Referenced by the shim, by `Transom.Aire` (the window) and by `Transom`. **Keep WPF out of it**: a `UseWPF` reference here would drag the desktop runtime into the shim's single-file bundle and end headless AIRE. |
| `source/Transom.ClickHelper/` + `source/Transom.ClickHelper.Mcp/` | The **UI-Assist** engine + its **MCP server** (`transom-ui-assist`) — drives Revit's desktop UI (screenshots/clicks) for things the API can't do (e.g. Edit-Group mode). |
| `source/Transom.Acad/` | **Transom for AutoCAD** — an AutoCAD 2027 (R26.0, .NET 10) autoloader plug-in, optional installer feature. Starts a loopback bridge on `127.0.0.1:48830` (token in `%LocalAppData%\Transom\acad\bridge.token`, every call and C# snippet logged to `bridge.log` there). Hands each request to AutoCAD's main thread (`MainThread.cs`, a hidden WinForms control — AutoCAD's counterpart of `ExternalEvent`). Writes run in **command context** (`ExecuteInCommandContextAsync`: locked, one undo step), reads in **application context with no document lock** — command context leaves an empty undo step per call, and so does a document lock taken from application context, which is why read-only `execute_autocad_code` uses an unlocked `OpenCloseTransaction` and therefore can't write or use `ed.Command` (verified live 2026-10-30: one write, every read tool, one `U` removes the write). Tools in `Tools.cs`, C# execution in `AcadCodeExecutor.cs` (**must stay `public`** — snippets compile as a separate assembly and can't see a `Globals` nested in an internal class: CS0122). On startup `McpSetup.cs` copies the bundled shim to `%LocalAppData%\Transom\mcp\` (via the linked `Transom/Core/DeployFile.cs`) and registers `transom-acad` in the Claude configs. References `AutoCAD.NET` 26.0.0 from NuGet, compile-only, so building needs no AutoCAD install. Every build stages the ready-to-install bundle at `bin/<Config>/bundle/Transom.Acad.bundle/` (`StageAcadBundle` target; `-p:DeployAcad=true` also copies it into `%AppData%\Autodesk\ApplicationPlugins` for the dev loop — close AutoCAD first). |
| `source/Transom.Acad.Scripting/` | Roslyn for the AutoCAD side, loaded into its own `AssemblyLoadContext` like `Transom.Scripting`: AutoCAD 2027 ships `Microsoft.CodeAnalysis` 4.10 next to `acad.exe`. Compiles on the bridge thread, runs on AutoCAD's main thread. |
| `source/Transom.Acad.McpShim/` | The AutoCAD **MCP server** (`transom-acad`) — same stdio framing as `Transom.McpShim`, separate server and port so the Revit and AutoCAD tool sets never collide. Splits an `image` field in a bridge reply into an MCP image item (`get_view_image`). |
| `source/Transom/Core/BridgeServer.cs` / `BridgeEventHandler.cs` / `BridgeTools.cs` (+ `Core/Bridge/*`) | The in-Revit loopback HTTP bridge (TcpListener on 48810) + the tool dispatch. **Core tools:** `status`, `list_schedules`, `read_schedule`, `set_parameter`, `set_parameters`, `execute_revit_code`. **Plus ~45 extended model tools** (views/elements/creation/MEP/interop) in `Core/Bridge/BridgeTools{Views,Elements,Create}.cs`, routed via `BridgeToolsDispatch.cs`, shared helpers in `BridgeToolsShared.cs`; the shim advertises them via `Transom.McpShim/ParityTools.cs`. Parity-tool wire contract: **all points/lengths are millimeters**; every write goes through `InTransaction` (warning-suppressed, rolls back if the request's waiter already timed out). |
| `source/Transom/Core/McpRegistration.cs` + `ClickHelperRegistration.cs` | Register the two MCP servers into the Claude client config (`~/.claude.json` `mcpServers`). Idempotent, non-clobbering, atomic, admin-free. |
| `install/` | WixSharp installer (`Installer.cs`, `ShimRefresh.cs` custom action). |
| `build/` | ModularPipelines build project (`dotnet run -- pack`) — **flaky, see §3; prefer the manual runbook**. |
| `claude/` | Drop-in guidance shipped with the add-in: `CLAUDE.md` + `transom-connect.md` (the USER's Claude client guidance, different from this file). The ClickHelper UI-automation guidance now lives in the `transom-ui-assist` MCP server's `instructions` + the staged how-to (`ClaudeGuideMarkdown`), not a shipped playbook file. |
| `docs/` | `design-notes/export-legend-copy.md` (the **copy source of truth** for the cell-colour legend — edit wording there first, then mirror into `TransomView.xaml` + `ExportLegendDialog.xaml`), `design-notes/revit-api-research-notes.md` (live-verified Revit API behaviour cited from the code), `parity-tool-status.md` (bridge-tool review state; named in a user-visible error, so it must resolve on GitHub). **Historical design notes and install post-mortems were archived out of the repo on 2026-08-01** — see `dev/Revit Coding/Transom-dev-notes-archive/` on the maintainer's machine. Don't re-add development notes here; this repo is public. |

---

## 2. Claude-Assist architecture (how the pieces connect)

1. A Claude client (**Claude Code** is the supported target; Cowork is out of scope — its VM can't reach a host stdio shim) launches the registered **stdio MCP servers** from `~/.claude.json`.
2. `Transom.McpShim.exe` (server name `transom`) speaks **MCP stdio** to the client and makes a loopback **HTTP** call to the in-Revit bridge for each tool call.
3. `Transom.ClickHelper.Mcp.exe` (server name `transom-ui-assist`) exposes UI-automation tools; used for grouped built-ins that the API can't edit in place.
4. The in-Revit bridge runs only when **Revit is open** and **Claude Assist** is **on** in Settings (auto-starts with Revit while enabled; listening on `127.0.0.1:48810`, auth via a per-session token in `%LocalAppData%\Transom\bridge.token`).
5. `Transom.Acad.McpShim.exe` (server name `transom-acad`) does the same for **AutoCAD 2027**: bridge on `127.0.0.1:48830`, token in `%LocalAppData%\Transom\acad\bridge.token`, header `X-Transom-Acad-Token`. There is no Assist toggle: installing the AutoCAD feature is the opt-in, so the bridge starts with AutoCAD and the plug-in (re)registers `transom-acad` on every startup. Tools: `status`, `list_layers`, `get_entities`, `run_command`, `cancel_command`, `execute_autocad_code`, `get_view_image`, `open_drawing`, `new_drawing`, `close_drawing`. Every tool refuses with a reason while a user command is active (`cancel_command` sends Esc Esc). One AutoCAD at a time: a second instance can't bind 48830 and its `TRANSOMACAD` command says so.

**MCP wire facts (load-bearing — a past bug here made Assist non-functional):**
- **stdio transport is NEWLINE-DELIMITED JSON-RPC** — one message per `\n`-terminated line, **no embedded newlines**, stdout carries **only** valid MCP messages, diagnostics go to **stderr**. (NOT LSP `Content-Length` framing — that was the bug fixed in v1.4.7.)
- `initialize` MUST **echo the client's requested `protocolVersion`** if supported (the shim echoes whatever the client sends; current version `2025-06-18`). Do not hardcode a version.
- The registration entry in `~/.claude.json` should include `"type":"stdio"` alongside `command`/`args` (Code treats command-bearing entries as stdio by default, but include `type` explicitly).
- **Smoke test:** `source/Transom.McpShim/smoke-test.ps1` pipes newline-delimited `initialize`+`tools/list` into the built exes and asserts the framing + echoed version + tool list. **Run it after any shim change.**
- **AIRE tools are testable with nothing running.** They are in-process, so piping a `tools/call` for `aire_video_models` into the exe exercises the real code path with no Revit, no window and no network. `aire_video` argument validation (unknown parameter, bad duration, motions on a model that ignores them) is all refused **before** credentials are read or any HTTP is sent, so those cases are free to test too. `estimate_only:true` is the first thing that touches the network — free at the vendor, but it does use the user's real key, so treat it as a live call.

**Quick manual connect check (no Revit needed — proves the handshake):** launch the published `%LocalAppData%\Transom\mcp\Transom.McpShim.exe`, write `{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"x","version":"1"}}}\n` then `{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}\n` to its stdin, read stdout → expect two single-line JSON responses, `protocolVersion` echoed, and **~40 tools** —
the 6 core ones (`status`, `list_schedules`, `read_schedule`, `set_parameter`, `set_parameters`,
`execute_revit_code`) plus `ParityTools.AddTo`'s enabled parity surface. Assert a **subset**, never an exact count
(`smoke-test.ps1` does it this way); "5 tools" was the pre-parity expectation and now reads as a failure. (A full `status` *call* additionally needs Revit open + bridge ON.)

---

## 3. BUILD, DEPLOY & CUT A RELEASE (the runbook)

> This is the authoritative release runbook. If a build/publish detail here ever disagrees with observed behavior, live behavior is the source of truth.

### Local dev deploy (every build IS a deploy)
A plain `dotnet build source/Transom/Transom.csproj -c Debug.R2x` **is** the local deploy — `DeployAddin=true` copies the payload to `%AppData%\Autodesk\Revit\Addins\<year>\Transom\`. Consequences:
- **WARN THE USER to close Revit BEFORE building — up front, not after errors appear.** A running Revit holds the deployed DLLs; the failure shows up as file-copy/lock errors or a flaky "N Error(s)" build that mysteriously succeeds on retry (that retry only worked because the user closed Revit in the meantime). Check `Get-Process Revit` immediately before each build; if it's running, ask the user to close it — **never kill it yourself**.
- Strictly, only the matching version locks (Debug.R25 ↔ Revit 2025, .R26 ↔ 2026, .R27 ↔ 2027), but the safe default is **all** Revit closed, since a deploy pass usually builds all three.
- Revit loads the add-in at startup — after a deploy the user must **restart Revit** to pick it up; there is no hot reload.
- Config map: `Debug/Release.R25`/`.R26` = .NET 8, `.R27` = .NET 10 → Revit 2025/2026/2027.
- **The Office engine now builds automatically** with Transom: `Transom.csproj`'s `BuildOfficeEngine` target (AfterTargets=Build) builds `Transom.Office.csproj` (`BuildProjectReferences=false`), whose `CopyIntoTransom` target stages `Transom.Office.dll` + the NPOI closure into the output, `publish/Transom/`, and the dev Addins folder. So a plain `dotnet build source/Transom/Transom.csproj -c …` now deploys the engine too — no separate Office build needed. (Historical: it was NOT auto-built before, which dropped the engine from the v1.8.0–v1.9.1 MSIs; the installer now hard-fails if the engine is absent from the harvest — see `install/Installer.cs` `AssertEngineHarvested`.)

### Hard rules
- **Every build auto-deploys the add-in DLL** (`DeployAddin=true`) to `%AppData%\Autodesk\Revit\Addins\<ver>\` — so **Revit MUST be CLOSED** before building, or the copy fails (locked DLL). Re-confirm Revit is closed immediately before each build. Never force-kill Revit; the user closes it.
- **A published GitHub release version is IMMUTABLE.** Never re-publish a different binary under an existing version tag — bump `AppInfo.Version` to a new number.
- **Do NOT use `dotnet run -- pack`** — its CompileProjectModule dies silently building 3 Revit configs back-to-back, and an exit-0 can mask a dropped config/MSI. Use the **csproj-direct** runbook below.
- **Build the CSPROJ per config for a release.** The `.sln` R26/Release mappings were REPAIRED on 2026-07-31 (they used to point `Debug.R26`, `Release.R26` and plain `Release` at `Debug.R25`, so the project was built as **Debug.R25** — not "not built" — and a debug DLL was deployed into the live **2025** add-ins folder while `bin/Release.R26` never appeared and the installer silently omitted Revit-2026). The solution now builds each configuration correctly, but the release runbook still goes csproj-direct: it resolves the right OutDir/PublishDir per config and keeps one stale `publish` folder from colliding with another (see the duplicate-component-ID note in step 2).
- **Toolchain (all three required on the build machine):**
  - **.NET SDK 10** (per `global.json`, rollForward latestMinor). R25/R26 target .NET 8, R27 targets .NET 10 — all three build standalone.
  - **WiX 7.x on PATH** — check `wix --version` (this machine: `wix 7.0.0`, global tool at `~/.dotnet/tools`). If building `Installer.csproj` complains about a missing UI extension, run `wix extension add -g WixToolset.UI.wixext`.
  - **VS "Desktop development with C++" workload + a Windows SDK** — the SingleUser MSI's managed custom action is packed via NativeAOT/MakeSfxCA, whose native link needs `link.exe`/`vswhere`. `install/Installer.cs` (`EnsureVsWhereOnPath`) prepends the VS Installer dir to PATH so `vswhere` resolves; if the SfxCA pack still fails with `'vswhere' is not recognized` / `link.exe ... code 123` / MSB3073 → WIX0103 "Cannot find Binary", the C++ workload isn't installed. **Failure mode to know:** when the SfxCA pack fails, the SingleUser MSI can be dropped silently while the overall build still returns exit 0 (only MultiUser builds) — so don't trust the exit code alone. `AssertBuilt` guards this by throwing if either MSI is missing; if it throws, fix the toolchain rather than shipping one MSI.
    - **"Platform linker not found" while `link.exe` IS installed** (seen 2026-10-30, ILCompiler 10.0.8): `vswhere -all` lists the VS install but with `isComplete=False` — an interrupted VS update — and NativeAOT's linker discovery skips incomplete installs. The real fix is to repair/finish the install in the Visual Studio Installer. Workaround that needs no repair: run the pack from a VS x64 developer environment with `IlcUseEnvironmentalTools=true`, e.g. a `.cmd` that does `call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"`, `set IlcUseEnvironmentalTools=true`, then step 5's `Installer.exe` line. ILCompiler then takes `link.exe` and the SDK libs from that environment. **Run step 5's `dotnet build install/Installer.csproj` BEFORE `vcvars64.bat`, never inside it:** vcvars sets `Platform=x64`, so a build in that environment lands in `install\bin\x64\Release\…` while the `Installer.exe` line runs the previous build's exe from `install\bin\Release\…` and packs its OLD custom action without any error (seen 2026-10-31). To check, the packed CA's strings should match `ShimRefresh.cs`.

### Steps (Revit closed)
1. **Bump version** — `source/Transom/Core/AppInfo.cs` `Version` → new value; update `Readme.md` download link + Status. Commit to `main` (no branch). Push.
   **Version increments are 0.0.1** (maintainer policy, 2026-07-13): 1.9.0 → 1.9.1 → 1.9.2 …, regardless of feature size. Only jump further if the maintainer explicitly says so in the session.
2. **Clean** — `rm -rf source/Transom/bin source/Transom/obj` (a stale `Debug.R25/publish` next to `Release.R25/publish` collides at "2025" → duplicate WiX component IDs).
3. **Build each Revit config (csproj-direct, NOT the .sln)** with the version:
   ```
   dotnet build source/Transom/Transom.csproj -c Release.R25 -p:Version=<v>
   dotnet build source/Transom/Transom.csproj -c Release.R26 -p:Version=<v>
   dotnet build source/Transom/Transom.csproj -c Release.R27 -p:Version=<v>
   ```
   (R25/R26 = .NET 8, R27 = .NET 10. Building the .sln for R26 silently omits it — always csproj-direct. Do NOT pass `-p:DeployAddin=false` — it suppresses the `publish/` folder the installer harvests.) **VERIFY** `find source/Transom/bin -type d -name publish` lists **exactly** `Release.R25`, `Release.R26`, `Release.R27` — no `Debug.R25` (the dev-deploy can re-create one, and it would collide at "2025" in step 5's harvest → duplicate WiX component IDs; that's why step 5 passes the three dirs explicitly, never a `*/publish` glob).
4. **Publish + bundle the 3 helper exes** so they sit next to `Transom.dll` in each `publish/Transom/` (the add-in reads them from there; missing → "broken on fresh install"), **and publish the AIRE standalone app WITH the version**:
   ```
   dotnet publish source/Transom.McpShim/Transom.McpShim.csproj -c Release -r win-x64
   dotnet publish source/Transom.ClickHelper/Transom.ClickHelper.csproj -c Release -r win-x64
   dotnet publish source/Transom.ClickHelper.Mcp/Transom.ClickHelper.Mcp.csproj -c Release -r win-x64
   dotnet publish source/Transom.Aire.App/Transom.Aire.App.csproj -c Release -r win-x64 --self-contained false -p:Version=<v>
   ```
   The AIRE line is not optional and not "only when AIRE changed": the installer harvests that app from its own `source/Transom.Aire.App/bin/Release/net8.0-windows/win-x64/publish/` (never from `publish/Transom/`), step 3's builds never refresh it, and `AssertAireAppPublished` fails the pack if the exe is missing **or its file version ≠ `<v>`** — that check is what stops a forgotten publish from shipping last release's AIRE under this release's MSI. Both MSIs offer the app as a tickable feature (SingleUser → `%LocalAppData%\Transom\aire` + the user's Start Menu; MultiUser → `Program Files\Transom\aire` + the all-users Start Menu).
   Re-run step 3 (the three csproj builds) so the csproj `<None Include … Condition="Exists(…)">` copies the **2 ClickHelper** exes into each `publish/Transom/`. The **shim is NOT in the csproj `<None>`** (the flaky pipeline handled it) — copy it into all three by hand after that rebuild:
   ```
   for c in Release.R25 Release.R26 Release.R27; do
     cp source/Transom.McpShim/bin/Release/net8.0/win-x64/publish/Transom.McpShim.exe \
        "source/Transom/bin/$c/publish/Transom/Transom.McpShim.exe"; done
   ```
   **Then stage the AutoCAD plug-in bundle, WITH the version, shim first** (the build copies the published shim into the bundle; the reverse order stages a bundle without one and only warns):
   ```
   dotnet publish source/Transom.Acad.McpShim/Transom.Acad.McpShim.csproj -c Release -r win-x64 -p:Version=<v>
   dotnet build source/Transom.Acad/Transom.Acad.csproj -c Release -p:Version=<v>
   ```
   Not optional either: the installer harvests `source/Transom.Acad/bin/Release/bundle/Transom.Acad.bundle/`, and `AssertAcadPublished` fails the pack if that bundle is incomplete **or** if `Transom.Acad.dll` / `Transom.Acad.McpShim.exe` aren't file version `<v>`. These builds don't deploy anywhere and don't need Revit or AutoCAD closed. The three `Transom.Acad*` projects are deliberately **not in `Transom.sln`** (its configuration mappings have broken release builds before); build them csproj-direct.
   **VERIFY** each `source/Transom/bin/Release.R2x/publish/Transom/` holds, next to `Transom.dll`: `Transom.ClickHelper.exe`, `Transom.ClickHelper.Mcp.exe`, `Transom.McpShim.exe`, **`Transom.Office.dll` + `NPOI.Core.dll`/`NPOI.OOXML.dll` + `Transom.Office.deps.json`** (the Excel engine — NPOI 2.7.x ships split assemblies, there is no single `NPOI.dll` — auto-built by the `BuildOfficeEngine` target; if absent, the installer's `AssertEngineHarvested` will fail the pack). For a connect-fix release, confirm those exes are the **fixed** build (don't ship a stale shim).
5. **Build + run the installer** (produces both MSIs into `output/`):
   ```
   dotnet build install/Installer.csproj -c Release
   ./install/bin/Release/net10.0-windows/Installer.exe <v> \
     "source/Transom/bin/Release.R25/publish" \
     "source/Transom/bin/Release.R26/publish" \
     "source/Transom/bin/Release.R27/publish"
   ```
   (The AutoCAD plug-in bundle adds ~31 MB to EACH MSI, mostly its self-contained shim — a 1.9.18 payload packed with it on 2026-10-30 came to SingleUser 177,549,628 B / MultiUser 167,501,353 B, delta 10.0 MB as expected.)
   Verify — **judge by the delta, not the absolutes.** SingleUser must come out **~10 MB larger than MultiUser**; that difference IS the SfxCA-packed install-time-shim custom action, which is SingleUser-only. Two MSIs of near-equal size mean the CA didn't pack: investigate, don't ship. Absolute sizes creep up with every content addition, so treat them as a dated reference point and never as a threshold — **as of v1.9.11 (2026-08-16): SingleUser 145,736,337 B (~139 MB), MultiUser 135,729,517 B (~129 MB)**, with v1.9.10's published assets within ~700 bytes of both. (This read "~126 MB / ~116 MB" until 2026-08-16, by which point it was ~13 MB stale and made a correct build look broken — hence the delta-first framing.) `AssertBuilt` fails the build loudly if either MSI is missing. Sanity-check the SingleUser MSI's **CustomAction** table — e.g. PowerShell `$db = (New-Object -ComObject WindowsInstaller.Installer).OpenDatabase('output\Transom-<v>-SingleUser.msi',0)` then a `SELECT \`Action\` FROM \`CustomAction\`` view → `RefreshLocalAppDataShim` must be present; confirm the bundled `Transom.McpShim.exe` is the freshly-built one (compare its hash to the just-published exe), not a stale copy.
6. **Cut the GitHub release:**
   ```
   gh release create v<v> -R Dave5264/transom-revit \
     --title "Transom <v>" --notes-file <notes> --target main \
     "output/Transom-<v>-SingleUser.msi" "output/Transom-<v>-MultiUser.msi"
   ```
   `--notes-file` takes a real file path (write the notes to a temp `.md` first); or use inline `--notes "..."`. Use `--target main` (a branch name or a FULL sha — a SHORT sha → `HTTP 422 target_commitish is invalid`); the tag is created on that branch's HEAD, so push first. **VERIFY:** `gh release list` shows v<v> = Latest, and the README download URL (already bumped in step 1) returns HTTP 200 (`curl -sI -o /dev/null -w '%{http_code}' <url>`).

### Installer behavior to know
- **SingleUser (per-user) MSI** is the one users install — admin-free. Its custom action (`install/ShimRefresh.cs`) copies the shim trio into `%LocalAppData%\Transom\mcp\` **at install time** (deferred + impersonated → writes the installing user's profile). It reads them from `<INSTALLDIR>\<year>\Transom`, newest Revit year first (INSTALLDIR is the Addins root), renames a running shim aside with the add-in's `DeployFile` (linked into `Installer.csproj`), and counts a file only after a byte-for-byte check, so the log's "refreshed N/M" is real. The add-in's first-launch `EnsureBundledShimAndAutoRegister` (`Application.cs` OnStartup) is the self-heal fallback.
- **AutoCAD 2027 plug-in** (both MSIs): a top-level feature, **off by default and ticked automatically when AutoCAD 2027 is installed** — an AppSearch reads the default value of `HKLM\SOFTWARE\Autodesk\AutoCAD\R26.0\InstalledProducts` (the install path, written by every R26.0 product including AutoCAD Architecture) into `ACAD2027_DIR`, and a feature `Condition` raises the level when it's set (`Win64 = true` on the search is load-bearing: WixSharp defaults it to false). Payload: the staged bundle (`PackageContents.xml` with `Platform="AutoCAD*"`, SeriesMin/Max `R26.0`, `LoadOnAutoCADStartup`) into **SingleUser → `%AppData%\Autodesk\ApplicationPlugins\Transom.Acad.bundle`**, **MultiUser → `%ProgramFiles%\Autodesk\ApplicationPlugins\Transom.Acad.bundle`**, explicit Dir ids at every level (the WIX0091 dedup issue). The location isn't configurable: AutoCAD's autoloader only scans those folders. The DLL is unsigned, so AutoCAD asks before loading it from the per-user folder (and again after every upgrade); Program Files is believed to be implicitly trusted, which would skip the prompt — unverified. The SingleUser custom action also copies `Transom.Acad.McpShim.exe` from the bundle's `Contents` into `%LocalAppData%\Transom\mcp\` (ACADDIR rides in `UsesProperties`); the plug-in's startup copy is the fallback and the only path on MultiUser. Uninstall leaves the per-user shim copy and the `transom-acad` config entry behind, same as the Revit shims.
- **MultiUser (per-machine) MSI** runs as SYSTEM → can't populate each user's `%LocalAppData%`; relies on the first-launch fallback. The custom action is SingleUser-only. (The **AIRE standalone app** feature is NOT SingleUser-only: since v1.9.16 both MSIs offer it — MultiUser puts it in `Program Files\Transom\aire` with an all-users Start Menu shortcut, which a per-machine MSI CAN write; only the payload root differs.)
- **Distribution policy:** SingleUser is the product. The MultiUser target is kept **fully buildable** in the codebase (`BuildMultiUserUserMsi()` in `install/Installer.cs` — do NOT remove it) but is **intentionally not surfaced as a one-click download** on the README landing page, so a self-serve user can't grab it by mistake (it requires admin and gives the less-seamless first-launch shim path). MultiUser exists only for **IT / firm-wide machine deployment** — an admin installing once for all users on a shared/imaged machine. The README's prominent download link must always point at `…-SingleUser.msi`. Whether to also attach the MultiUser `.msi` to a GitHub release is a separate choice (build it on demand from source); if attached, the README still shouldn't link it directly.

---

## 3b. VERIFYING A BUILD IN THE REAL REVIT UI

Bridge/MCP calls prove the add-in **loaded**. They do not prove the UI works — they can't catch a wrong status
string, a dialog that never opens, or a disabled button. Before calling a release verified, click through
Schedule Hub for real. Lessons paid for the hard way:

- **Tile Revit ~2/3 of the screen, Claude ~1/3 — never 50/50.** Revit's ribbon reflows to window width, and at
  960px on a 1920px screen the tab strip **overflows and silently hides the right-hand tabs, including
  `Transom`**. It looks exactly like the add-in failed to load. `Transom.ClickHelper tile` already splits
  2/3–1/3; the `revit_tile` MCP tool may be served by a **stale ClickHelper in `%LocalAppData%\Transom\mcp\`**
  that still does 50/50 — check the geometry in the reply before diagnosing anything.
- **Every rebuild re-triggers the "Security - Unsigned Add-In" dialog** (new DLL = new signature check) and the
  add-in does not load until it's answered. Clear it first: `revit_find "Always Load"` → click that
  `automationId`. `revit_list_dialogs` can return a polluted button list here; `revit_find` is reliable.
- **`timed out waiting for Revit (request not applied)` during a large model open is not a failure** — the
  bridge socket accepts before Revit's API thread frees up. Poll `status` and retry.
- **Revit's Home screen is invisible to UI Automation** — empty `MainWindowTitle` until a document is open.
- **Never open an older `.rvt` in a newer Revit** (irreversible upgrade). Test a newer Revit against a **copy**
  or a throwaway `Ctrl+N` project, and close with "do not save".
- **To replace text in a pre-filled field** (e.g. a Save As path): click it, `ctrl+a`, then type — `revit_type`
  alone appends.
- **Verify the artifact, not the status line.** `.xlsx`/`.docx` are zips: check for `50 4B`, then unzip and
  count rows in `xl/worksheets/sheet1.xml` against what the model holds. That is exactly how the
  "(0 element rows)" mislabel was found — a type-anchored schedule exported 11 correct rows while reporting
  zero, because the counter only counted itemized instance rows (see `ScheduleTable.DataRowCount`).

---

## 4. Conventions & safety
- **Commit to `main`, no feature branches** (maintainer preference). Don't `--no-verify`. End commit messages with the standard `Co-Authored-By` trailer.
- **Multi-line commit messages: write the message to a temp file and `git commit -F <file>`.** A PowerShell 5.1 here-string (`@'…'@`) passed to `git commit -m` has broken in practice here (line-ending mangling splits it into bogus pathspec args). `-F` sidesteps quoting entirely and is also the reliable path for `gh release create --notes-file`.
- **Workshared models:** never Synchronize/Save a workshared model during a test; on a close prompt answer "do not save" / "keep ownership" — never sync. Use throwaway/test models for Assist runs.
- **Test the published binary, not the build flavor:** the smoke test builds a framework-dependent exe; the *shipped* exe is self-contained (different md5, same framing code). Verify against the actual `%LocalAppData%` exe when proving connect.
- **Version source of truth:** `AppInfo.Version`. After bumping, the deployed DLL's FileVersion and the MSI version should match it.

---

## 5. Claude-Assist client support
- The supported client is **Claude Code** (it reads the `~/.claude.json` registration the Claude Assist toggle writes, runs locally with filesystem access, and reaches the loopback bridge).
- **Claude Cowork is not supported**: it runs Claude Code inside a local VM that can't spawn/reach a host-side stdio shim, so the registration the add-in writes doesn't reach it. Don't document or wire a Cowork path without solving that bridge first.
