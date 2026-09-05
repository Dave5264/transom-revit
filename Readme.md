<div align="center">

<table align="center"><tr><td><pre>
████████╗██████╗  █████╗ ███╗   ██╗███████╗ ██████╗ ███╗   ███╗
╚══██╔══╝██╔══██╗██╔══██╗████╗  ██║██╔════╝██╔═══██╗████╗ ████║
   ██║   ██████╔╝███████║██╔██╗ ██║███████╗██║   ██║██╔████╔██║
   ██║   ██╔══██╗██╔══██║██║╚██╗██║╚════██║██║   ██║██║╚██╔╝██║
   ██║   ██║  ██║██║  ██║██║ ╚████║███████║╚██████╔╝██║ ╚═╝ ██║
   ╚═╝   ╚═╝  ╚═╝╚═╝  ╚═╝╚═╝  ╚═══╝╚══════╝ ╚═════╝ ╚═╝     ╚═╝
</pre></td></tr></table>


**Edit Revit schedules anywhere and import them back safely, drive the live model from Claude Code, and
enhance your renders with AI.**

[![Latest release](https://img.shields.io/github/v/release/Dave5264/transom-revit?label=latest%20release&color=2ea44f&logo=github)](https://github.com/Dave5264/transom-revit/releases/latest)

### ⬇ [Download the installer](https://github.com/Dave5264/transom-revit/releases/download/v1.9.15/Transom-1.9.15-SingleUser.msi)

**One click, no admin rights** — installs into your per-user Revit add-ins folder.
Double-click the `.msi`, then start Revit. Supports **Revit 2025, 2026 & 2027**. Free.

</div>

Transom is an Autodesk Revit add-in that facilitates **schedule editing** and provides **integration with
Claude Code**, alongside an **AI render enhancer**. Most project schedules can be exported, edited anywhere,
and imported back into the model safely — and the export **color-codes every cell by what can actually be
written back**, so you know before you type. The awkward Revit cases are handled rather than excluded: type
parameters, group headers, non-itemized rows, and parameters on elements inside model groups each have a
defined path back.

Each part stands alone — the round-trip needs no Claude client, the Claude layer works on any part of the
model whether schedules are involved or not, and the render enhancer works on image files with nothing open
at all. None of them needs administrator rights.

### Schedules — export, edit anywhere, import back

Export the schedules you tick (each becomes a sheet) to `.xlsx` / `.xls`, or `.csv` for a display-only copy.
The sheet looks like Revit does — merged headers, grouping, subtotals, fonts, colors, hidden columns,
calculated and combined fields, Revit's own row order — so you can edit it in Excel, in Sheets, or on a
machine with no Revit installed.

Bring it back through **Import → Preview → Apply**. The preview lists every change, what each one touches,
and which schedules are affected; format-mismatched edits (`2'6` for `2'-6"`) hold **Apply** greyed until you
confirm or discard them. What you apply is written **atomically** — type parameters included — and each value
is re-read to verify; if Revit rejects one change, Transom retries the rest individually so a single bad value
can't discard your other edits, and every change is reported Applied or Failed. Cells Transom can't write are
listed and skipped, never half-applied.

Every cell is colored by **what an edit to it will actually touch**, classified at export time from the live
model. White and green import directly — green meaning the value is shared beyond the row, like a type
parameter. Blue, yellow and red mark elements inside **model groups**, which Revit locks down: blue imports
cleanly, yellow and red need a decision first ([see below](#grouped-parameters--claude-assist)). Grey can't be
written at all — Revit computes or locks it. A model with no groups is only ever white, green and grey. The
legend below is on the Export tab itself, and its **More information…** dialog covers every color and every
import option in full.

<table>
<tr>
<td width="50%"><img src="docs/images/hub-export.jpg" alt="Transom Schedule Hub, Export tab. The cell-color legend reads: normal — instance parameter, edits the element(s) in that row, nothing else; green — type parameter (shared value), edits every element of the type or under the header; blue — project parameter in a model group, imports fine, each element keeps its own value; yellow — built-in data parameter in a model group, Transom asks how to apply it on import; red — geometry-driving parameter, may only be changed through group edit mode, requires Claude-Assist to automate; grey — not importable, Revit computes it or locks it. Below the legend is a checklist of the model's schedules." width="100%"></td>
<td width="50%"><img src="docs/images/hub-import-preview.jpg" alt="Transom Schedule Hub, Import tab: a preview listing 11 changes with element, field, old value, new value and scope, plus the schedules the import will change" width="100%"></td>
</tr>
<tr>
<td><em><b>Export</b> — tick the schedules to write out; the color legend sits above the list.</em></td>
<td><em><b>Import → Preview</b> — every change with its old and new value, the scope it will reach, and which schedules it touches. Rows on group members are flagged for a decision before anything is written.</em></td>
</tr>
</table>

### Claude Code integration

The integration layer connects the live Revit model to Claude through a **local bridge** — loopback only
(`127.0.0.1`), authenticated with a per-session token, bundled with the add-in. No separate install, no admin
rights, nothing leaves your machine.

Ask Claude Code for model work in plain language: cross-check a schedule, apply staged edits, create sheets
and views, run bulk edits, tag things. The in-process `execute_revit_code` tool gives it the **full Revit
API**, alongside ~35 purpose-built tools (views, elements, creation, MEP) each live-verified against a real
project model. A second server, `transom-ui-assist`, lets Claude **take over the Revit interface** where the
API has no path at all — above all **Edit Group mode** ([below](#grouped-parameters--claude-assist)).

A built-in **skill library** (Schedule Hub → Claude Skills) keeps reusable workflows as `.md` files in a
per-user folder, so they're available in **every project**: import your own, keep the ones Claude writes for
you, and **Stage** copies a skill's path to your clipboard to paste into Claude Code. Two ship with the
add-in — a read-only **schedule inventory** (a safe first thing to try) and **elevation door/window
tagging** that recognises visible openings optically, so ones hidden behind the facade never get tagged.

Turning it on, once:

1. Turn **Claude Assist** on in Transom Settings (Schedule Hub → Settings tab) — the first ON registers
   Transom's MCP servers and starts the bridge.
2. Restart Claude Code so it launches the shim and picks up the new servers.

<table>
<tr>
<td width="50%"><img src="docs/images/hub-settings-claude-assist.jpg" alt="Transom Schedule Hub, Settings tab: the Claude Assist toggle switched on, with a status checklist showing the bridge listening on 127.0.0.1:48810, the session token, the deployed MCP shim, both servers registered with Claude Code, and Claude connected" width="100%"></td>
<td width="50%"><img src="docs/images/hub-claude-skills.jpg" alt="Transom Schedule Hub, Claude Skills tab: the skill library listing elevation-door-window-tags and schedule-inventory, with Stage, Import and Remove buttons, the selected skill's description, and the live bridge status checklist" width="100%"></td>
</tr>
<tr>
<td><em><b>Settings</b> — one <b>Claude Assist</b> toggle, and a status checklist that shows every layer between Revit and Claude Code so a broken connection names itself.</em></td>
<td><em><b>Claude Skills</b> — the skill library: import your own, keep the ones Claude writes, and <b>Stage</b> one to paste straight into Claude Code.</em></td>
</tr>
</table>

Drop the guidance file from [`claude/`](claude/) (`CLAUDE.md`) into your project root or `~/.claude/CLAUDE.md`
so Claude Code already knows the tools and the safe-write workflow — see [`claude/README.md`](claude/README.md)
for exactly where it goes. Not sure what to ask for? **Show me what you can do** exports a demo script Claude
can run in a fresh project while you watch. The supported client is **Claude Code** (Claude Cowork runs in a
VM that can't reach a host-side bridge), and it should run with bypass permissions on — otherwise its
permission prompts steal focus from Revit and UI-assist clicks silently miss.

### AI render enhancement — AIRE

**AI Render Enhancer** has its own ribbon button and two tabs. **Enhance** batch-improves architectural
renders through OpenAI's image models — photoreal grass, planting, lighting and concrete texture, with the
camera angle, perspective, geometry, mullions, trim lines and overall composition held exactly as Revit
produced them. **Video** takes one finished render and turns it into a short generated clip ([below](#video--one-render-one-clip)).
Both work on image **files**, not on the model, so they need no open project — and the window runs without
Revit at all: tick **AIRE standalone app** when installing and it gets a Start Menu shortcut of its own.

<table>
<tr><td><img src="docs/images/aire.jpg" alt="Transom AI Render Enhancer window: an API key field with a Saved Keys dropdown, input and output folder pickers, model/resolution/quality selectors, a Prompt card with a saved-prompt dropdown and a Pop Out button, a checkable queue of render images with their resolutions, the estimated cost for the checked images, and a progress bar" width="100%"></td></tr>
<tr><td><em><b>AI Render Enhancer</b> — the queue, the settings that drive cost, and the estimate for exactly what you have ticked.</em></td></tr>
</table>

- **Point and tick.** **Scan Folder**, or drag images and folders straight onto the list. `.png`, `.jpg`,
  `.jpeg` and `.webp` go in; `<name>_enhanced.png` comes out. AIRE skips its own outputs, so re-scanning a
  folder you have already enhanced won't re-bill it. Defaults are **`gpt-image-2`** at 3840×2160 and high
  quality; the older `gpt-image-1.5`, `-1` and `-1-mini` are selectable but top out at 1536×1024.
- **Nothing is spent without a confirmation.** The image count, model, resolution, quality and an estimated
  cost are shown before a batch starts, and **Cancel Batch** stops one already under way. Every batch writes
  `logs\enhancement_log_<timestamp>.csv` beside the outputs — a row per image with settings, status, elapsed
  time and estimated cost. Treat the figure as an approximation for deciding whether to press go, not a bill;
  **Open OpenAI Billing** goes to the authority.
- **Prompts and keys are worth keeping.** Name a prompt that works and **Save** it, then pick it back out of
  the dropdown on any later run; **Pop Out** edits the same text in a resizable window. Keys work the same
  way — save one per OpenAI account and switch from **Saved Keys**, each encrypted separately.
- **Bring your own key.** A built-in walkthrough covers creating one. It is encrypted per Windows user (DPAPI)
  in `%AppData%\Transom\aire.json`, never written in plain text, and goes nowhere but `api.openai.com`.
- **Claude can drive it** over the same bridge: `aire_enhance` starts a batch and returns a job id plus the
  cost estimate, `aire_job_status` polls it, `aire_cancel_job` stops it. The key is deliberately not a tool
  argument — the bridge reads it from your encrypted store, so it never passes through Claude or the socket.
  One batch runs at a time no matter which way it was started.

#### Video — one render, one clip

The **Video** tab takes a single render — ideally the 4K `_enhanced.png` the Enhance tab just made — and
turns it into a few seconds of generated motion through [Higgsfield](https://cloud.higgsfield.ai), which
routes to its own DoP camera models, Kling, Veo, Seedance, Hailuo, Sora and Wan behind one credential. It is
a hero shot with motion, not a walkthrough: clips run 2–12 seconds and top out at 1080p.

<table>
<tr><td><img src="docs/images/aire-video.jpg" alt="Transom AI Render Enhancer window on the Video tab: Key ID and Secret fields with a Saved Keys dropdown, an output folder picker, a model dropdown showing Higgsfield DoP Standard, clip duration/resolution/aspect dropdowns, two camera-preset dropdowns with strength sliders, a Motion Prompt card with its own saved prompts and a Pop Out button, a large source-render thumbnail with its filename, size and aspect ratio, and a Generate Clip button beside the estimated clip cost" width="100%"></td></tr>
<tr><td><em><b>Video</b> — one render, one model, only the settings that model accepts, and Higgsfield's own price for exactly that request before anything is sent.</em></td></tr>
</table>

- **The price is the vendor's, not a guess.** The cost on the tab is Higgsfield's estimate for exactly the
  request that would be sent, refreshed whenever a setting changes; **Generate Clip** is refused outright if
  that estimate cannot be obtained. The confirmation names the model, duration, resolution, aspect ratio,
  camera preset, source file, destination folder and the exact credits and dollars.
- **Every setting comes from the model.** The 21 image-to-video models are generated from Higgsfield's
  published API spec into a catalog (`%AppData%\Transom\higgsfield-models.json` overrides the built-in one),
  so each offers only the durations, resolutions and aspect ratios it accepts; a setting a model lacks is shown
  as such, not hidden. Camera presets — up to two, each with a strength slider — appear only on the Higgsfield
  DoP models, the only ones that take them; **Load Presets** reads the named list from your account. A render
  whose aspect ratio a model cannot produce gets a warning before Generate, not a silent crop after.
- **Cancel means what Higgsfield means by it.** A queued request can be cancelled and is refunded; once
  generation starts it will finish and be charged, so **Cancel** is live only while the clip is uploading or
  queued, and says why otherwise. Long runs — a 10 s Master-tier clip can take eight minutes — show elapsed
  time and when the service was last polled.
- **The clip lands beside the render.** `<name>_clip.mp4` in the output folder (it defaults to the Enhance
  output folder), plus a `logs\video_log_<timestamp>.csv` row with the request id, the estimate and what was
  charged. Higgsfield keeps outputs for about a week; AIRE downloads immediately and never just records a URL.
- **Same guard rails as Enhance.** One paid job at a time across Revit and the standalone app — an image
  batch and a clip can never run together. Credentials are a **Key ID** and **Secret** pair, encrypted per
  Windows user like the OpenAI key, with their own **Saved Keys**; the tab keeps its own saved prompts and
  pop-out editor. Claude has no video tool over the bridge; Higgsfield ships its own MCP server for that.

*AIRE is far newer than the schedule round-trip and has had much less mileage. Treat early batches and clips
as a trial, and check the first costs against your real OpenAI and Higgsfield usage.*

> **Status:** v1.9.15 (Revit 2025/2026/2027) — AIRE gets a **Video tab**: one finished render in, one short
> generated clip out, through Higgsfield's access layer to their DoP camera models, Kling, Veo, Seedance,
> Hailuo, Sora and Wan. The price shown is the vendor's own estimate for exactly that request, every setting
> comes from the model's published API schema, cancel is offered only while it would still be refunded, and
> the clip is downloaded beside its render with a CSV row for the charge. Same one-job-at-a-time lock as the
> image batches. Dropdowns a model disables now stay legible in the Dark theme.
>
> v1.9.14 gave AIRE a **reusable prompt library, saved API keys, and a pop-out prompt editor** — name a prompt
> and save it, save one key per OpenAI account and switch from a dropdown, and edit the prompt in a resizable
> window that follows the theme. Saved prompts and keys are written to disk the moment you save them.
>
> v1.9.13 gave the standalone AIRE shortcut its own icon, generated at
> 16 through 256 px from the same master as the ribbon art, so the Start Menu, taskbar and Alt-Tab each show a
> real frame rather than an upscaled one. No functional change from v1.9.12.
>
> v1.9.12 brought **AIRE without Revit.** The installer offers an optional
> Start Menu shortcut that runs the AI Render Enhancer as its own app, so a folder of renders can be enhanced
> without opening Revit at all. Same window, same settings, same encrypted key as the ribbon version; a second
> launch brings the existing window forward rather than opening AIRE twice.
>
> Making that safe also closed a real hole that predates it: AIRE's "one batch at a time" guard only ever
> applied *within one process*, so two Transom processes — Revit 2025 and 2026 open together, say — could each
> start a batch against the same paid OpenAI account. That guard now holds across processes.
>
> v1.9.11 gave AIRE a **Cancel Batch** button. Until then the only way
> to stop a running batch was to ask Claude (`aire_cancel_job`); the window itself had no way out. Stopping a
> run also used to throw away the CSV log for the images it had already generated *and billed for*, and report
> the batch as failed — cancelling before the first image did that every single time. A cancelled batch now
> always writes its log, reports itself as cancelled rather than complete, and counts the images it never
> attempted.
>
> v1.9.10 was two deployment/robustness fixes on top of v1.9.9. The bundled
> helper executables are now refreshed on every Revit start **even while Claude Code holds them open** (they
> are running processes, so the old plain-overwrite silently failed and left you on the previous build's
> helpers indefinitely). And AIRE now retries a request that OpenAI rejected as rate-limited or that failed
> server-side — the stand-alone AIRE.exe got that free from the OpenAI SDK, and the port had lost it, so one
> transient blip could permanently fail an image mid-batch. Timeouts are deliberately **not** retried, since
> the image may already have been generated and billed.
>
> v1.9.9 was a **correctness and honesty release** from a full line-by-line
> audit of the codebase, plus a new tool — the [**AI Render Enhancer**](#ai-render-enhancement--aire)
> described above.
> The most important v1.9.9 audit fixes: a multi-row schedule split by a hidden group field (a window
> schedule grouped by Level, say) could write your edits to a *different* row's instances when the level names
> sorted "1, 10, 2" — that ordering is now numeric, direction-aware, and refuses to guess rather than writing to
> the wrong elements. Applying an import while a re-preview was still running could silently discard your typed
> corrections and apply the older values; Apply now waits. Every bridge write checks whether Revit actually kept
> the transaction, so a write Revit rolled back is reported as failed instead of succeeded. Reports got more
> honest too: read-only cells no longer render the same red as genuine failures, header renames aren't counted
> as applied unless they verified, and the run log distinguishes *proposed* from *applied*.
> Before that, v1.9.8 fixed export row reporting; v1.9.7 made Transom roughly **half the size on disk**. The three bundled helper
> executables each carried an uncompressed copy of the .NET runtime, and Roslyn's compiler messages shipped
> translated into 13 languages that nothing reads; compressing the single-file bundles and dropping the unused
> translations takes the payload from ~318 MB to ~163 MB per Revit version, with no functional change. Before
> that, v1.9.6 was a housekeeping release: the installer now lists **Transom** as
> the Publisher in Add/Remove Programs instead of the Windows username of whoever cut the build, the revision
> narrative no longer hard-codes a firm name into the project-number line (edit it in the confirm dialog), and
> the elevation-tagging skill discovers elevation sheets from the model instead of assuming an A300-series
> numbering convention. Before that, v1.9.5 shipped five fixes from live option-2 UI testing, and v1.9.2
> restored the Excel engine that had been dropped from the v1.8.0–v1.9.1 installers. Full history on the
> [releases page](https://github.com/Dave5264/transom-revit/releases).

### Grouped parameters — Claude-Assist

Parameters on elements inside a Revit **model group** are the case that stops most schedule tools — Transom's
blue, yellow and red cells. A project or shared *instance* parameter can vary per group instance, so Transom
enables that flag and writes it (blue). But a **built-in** parameter (Comments, Finish…) or a
**geometry-driving** one can only be changed from inside **Edit Group** mode; no API path exists at all. On
import, Transom asks per affected column:

- **New parameter (2a type / 2b instance)** — create one, repoint the schedule column onto it, and write your
  edits there. Nothing is ungrouped and the built-in keeps its old values underneath. Never offered for
  geometry-driving parameters, where a new parameter would change the schedule while the geometry stayed put.
- **Claude-Assist** — Transom stages a `.json` plus a step-by-step `.md` and doesn't touch the model itself;
  the Claude Code session already on the bridge enters Edit Group mode, sets the parameter in the Properties
  palette, finishes the group, then verifies the value and the member count. Works through excluded members,
  attached detail groups and nested groups.
- **Skip** — leave the column untouched, including its ungrouped elements.

An Edit Group edit lands on the **group definition**, so every instance of that group type gets the same
value; for per-instance values take 2b instead. Claude-Assist drives the live UI, so try it on a throwaway
model first, and never Synchronize with Central mid-run on a workshared model.

## Install

The **[per-user installer](https://github.com/Dave5264/transom-revit/releases/latest)** needs no
administrator rights:

1. Download **`Transom-…-SingleUser.msi`** from the [latest release](https://github.com/Dave5264/transom-revit/releases/latest).
2. Double-click it — it installs into `%AppData%\Autodesk\Revit\Addins\` for the current user only.
3. Launch Revit; Transom is on the ribbon. To remove it later: *Apps & features → Transom*.

**Always use the SingleUser installer** — Claude-Assist's install-time setup only runs there. A machine-wide
**MultiUser** MSI is built from this codebase for **IT / firm-wide deployment**, but deliberately isn't linked
here: it requires admin rights and, running as `SYSTEM`, defers the MCP shim to Revit's first launch. Build it
from source if you genuinely need it.

<details>
<summary><b>Building from source</b> — targets, build commands, repo layout</summary>

| Revit | Runtime | Build configuration |
|-------|---------|---------------------|
| 2025  | .NET 8  | `Debug.R25` / `Release.R25` |
| 2026  | .NET 8  | `Debug.R26` / `Release.R26` |
| 2027  | .NET 10 | `Debug.R27` / `Release.R27` |

Built on the [Nice3point Revit SDK](https://github.com/Nice3point/RevitTemplates) (multi-version +
dynamic-loading isolation); Excel via [NPOI](https://github.com/nissl-lab/npoi).

```shell
dotnet build source/Transom/Transom.csproj -c Debug.R25   # Revit 2025 (.NET 8)
dotnet build source/Transom/Transom.csproj -c Debug.R27   # Revit 2027 (.NET 10)
```

A successful build deploys the add-in to `%AppData%\Autodesk\Revit\Addins\<version>\` — so **close Revit before
building**, or the copy fails on a locked DLL.

For the MSI installer, build each configuration directly and then run the installer project. Do **not** use
`build/`'s `dotnet run -- pack`: its compile step can drop a Revit configuration and still exit 0, producing an
installer that silently omits a whole Revit version.

| Path | Description |
|------|-------------|
| `source/Transom/` | the add-in (commands, views, view-models, core logic) |
| `source/Transom.Aire/` | AIRE: the OpenAI engine, the Higgsfield client and generated model catalog, both job runners, encrypted settings and the window (Revit-free — files + HTTPS) |
| `source/Transom.Aire.App/` | the standalone AIRE host (same window, its own process) |
| `build/`, `install/` | ModularPipelines build + WiX installer |
| `branding/` | ribbon icon + generator |
| `docs/` | `design-notes/` (legend copy source of truth + Revit-API research notes), `parity-tool-status.md` (bridge-tool review state), `images/` (screenshots on this page) |

</details>
