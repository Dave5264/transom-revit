# UI-Assist "components not found" — findings

Investigation of the error shown when clicking **Claude UI Assist** on the ribbon:

> **Couldn't install the UI-Assist components** — the bundled executables
> (`Transom.ClickHelper.Mcp.exe` / `Transom.ClickHelper.exe`) weren't found in the
> add-in folder. Reinstall Transom, or report this.
>
> ⚠ UI-Assist components not found at `C:\Users\doblack\AppData\Local\Transom\mcp` (install them first).

---

## Verdict

**Not a code bug in the UI-Assist feature** — the two bundled executables it installs were
**never produced and never shipped into the add-in folder**, so `EnsureInstalled()` had
nothing to copy. This is the same class of "must publish + bundle the self-contained exe"
gap previously documented for the MCP shim (see `install/SEAMLESS_INSTALL.md`). It is
**independent of the Revision Narrative feature** on this branch.

## How UI-Assist installs (the intended chain)

1. `Transom.ClickHelper` (UI-automation engine) and `Transom.ClickHelper.Mcp` (stdio MCP
   server) are each published **self-contained, single-file, win-x64** (Release) — their
   `.csproj`s set `PublishSingleFile`/`SelfContained`/`RuntimeIdentifier=win-x64` under
   `Configuration == Release`. This mirrors `Transom.McpShim`.
2. `Transom.csproj` copies those published exes **next to `Transom.dll`** in the add-in
   payload, via `Exists`-gated links:
   ```xml
   <None Include="..\Transom.ClickHelper\bin\Release\net8.0-windows\win-x64\publish\Transom.ClickHelper.exe"
         Condition="Exists('...publish\Transom.ClickHelper.exe')">
     <Link>Transom.ClickHelper.exe</Link>
     <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
   </None>
   <!-- …and Transom.ClickHelper.Mcp.exe likewise -->
   ```
3. At first click, `ClickHelperRegistration.EnsureInstalled()` copies those two exes from
   the add-in folder to `%LocalAppData%\Transom\mcp\`, then registers a `transom-ui-assist`
   MCP server pointing at them.

## Root cause

The `<None … Condition="Exists(…)">` is the load-bearing detail: **if the ClickHelper
projects have not been published, the publish outputs don't exist, the copies are silently
skipped, and `Transom.dll` ships with no ClickHelper exes beside it.** Then step 3 finds
nothing to install → the dialog's "components not found."

Two contributing factors here:
- **The official build** publishes them first (a `PublishClickHelper`-style build module is
  referenced in `Transom.csproj`'s comments). But a plain `dotnet build` of
  `source/Transom/Transom.csproj` — which is how this branch was compiled and deployed all
  session — does **not** trigger that publish.
- **The dev deploys this session copied only `Transom.dll`** to
  `…\Revit\Addins\2025\Transom\`, never the ClickHelper exes (they were never produced).

## Evidence (this machine)

| Check | Result |
|---|---|
| ClickHelper exes in `…\Revit\Addins\2025\Transom\` | **none** (only `Transom.dll` + the usual DLLs) |
| `%LocalAppData%\Transom\mcp\` contents | only `Transom.McpShim.exe` (the bridge shim); **no ClickHelper exes** |
| `source/Transom.ClickHelper`, `source/Transom.ClickHelper.Mcp` | present; configured for self-contained single-file Release publish |
| `Transom.csproj` bundling | present, but `Condition="Exists(...publish\…exe)"` — skipped when not published |

## To make UI-Assist actually work (when desired)

Not done here (the request was diagnosis + copyable error dialogs only). The fix is to
produce and ship the two exes:

1. Publish both projects (Release, win-x64), e.g.
   ```
   dotnet publish source/Transom.ClickHelper/Transom.ClickHelper.csproj -c Release -r win-x64
   dotnet publish source/Transom.ClickHelper.Mcp/Transom.ClickHelper.Mcp.csproj -c Release -r win-x64
   ```
   (or run the build's ClickHelper publish module), **then** rebuild `Transom` so the
   `Exists`-gated `<None>` copies pick them up.
2. Deploy `Transom.ClickHelper.exe` and `Transom.ClickHelper.Mcp.exe` **alongside**
   `Transom.dll` in the add-in folder (the installer must include them too — same
   per-user, admin-free placement as the shim).
3. Click **Claude UI Assist** again; `EnsureInstalled()` will copy them to
   `%LocalAppData%\Transom\mcp\` and register the `transom-ui-assist` server.

## Surfacing it going forward

The error/report dialogs now route through `Views/ReportDialog` (a **Copy details**
button), and `ClickHelperRegistration.Diagnostics()` lists every relevant path with a
`[found]`/`[MISSING]` flag. So the next time this dialog appears it will show, copyably,
exactly which bundled/installed executable is missing — turning a vague "components not
found" into an actionable report.
