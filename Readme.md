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

**One click, no admin rights.** Installs into your per-user Revit add-ins folder.
Double-click the `.msi`, then start Revit. Supports **Revit 2025, 2026 & 2027**. Free.

</div>

Transom is a Revit add-in that does three things. It exports your schedules to a spreadsheet and imports your
edits back safely. It connects the live model to **Claude Code**. And it enhances your renders with AI.

Each part works on its own. The schedule editor doesn't need Claude. The Claude layer works anywhere in the model,
schedules or not. The render enhancer only reads image files, so nothing has to be open. None of it needs
administrator rights.

### Schedules — export, edit anywhere, import back

Most project schedules can go out to a spreadsheet and come back in safely. The export tells you up front
what each cell will do when it returns.

- **Export what you tick**, one sheet each, to `.xlsx` / `.xls`, or `.csv` for a display-only copy. The sheet
  looks like Revit does, down to merged headers, subtotals, fonts, colors and Revit's own row order.
- **Edit it anywhere.** Excel, Sheets, or a machine with no Revit on it.
- **Bring it back through Import → Preview → Apply.** The preview lists every change and which schedules it
  hits. Type `2'6` for `2'-6"` and that row holds **Apply** greyed out until you confirm or discard it.
- **Nothing half-applies.** Writes are atomic, type parameters included, then read back to verify. If Revit
  rejects one change, Transom retries the rest one at a time, so one bad value can't throw away your other
  edits. Cells Transom can't write are listed and skipped.
- **Every cell is colored** by what an edit to it will actually touch. White and green import directly. Blue,
  yellow and red are elements inside **model groups**, which Revit locks down, and yellow and red need a
  decision first ([see below](#automate-bulk-group-editing-with-claude)). Grey can't be written at all. The full
  legend is on the Export tab.

<table>
<tr>
<td width="50%"><img src="docs/images/hub-export.jpg" alt="Transom Schedule Hub, Export tab. The cell-color legend reads: normal — instance parameter, edits the element(s) in that row, nothing else; green — type parameter (shared value), edits every element of the type or under the header; blue — project parameter in a model group, imports fine, each element keeps its own value; yellow — built-in data parameter in a model group, Transom asks how to apply it on import; red — geometry-driving parameter, may only be changed through group edit mode, requires Claude-Assist to automate; grey — not importable, Revit computes it or locks it. Below the legend is a checklist of the model's schedules." width="100%"></td>
<td width="50%"><img src="docs/images/hub-import-preview.jpg" alt="Transom Schedule Hub, Import tab: a preview listing 11 changes with element, field, old value, new value and scope, plus the schedules the import will change" width="100%"></td>
</tr>
<tr>
<td><em><b>Export</b>: tick the schedules you want. The color legend sits right above the list.</em></td>
<td><em><b>Import → Preview</b>: every change with its old and new value, how far it reaches, and which schedules it touches. Rows on group members get flagged before anything is written.</em></td>
</tr>
</table>

### Claude Code integration

Transom connects your open Revit model to Claude Code. The connection never leaves your computer, and
there's nothing extra to install.

- **Ask in plain language.** Cross-check a schedule, apply your staged edits, create sheets and views, run
  bulk edits, tag things.
- **Claude gets the whole of Revit**, not a fixed menu. It can run code directly in your model, and it has
  about 35 ready-made tools for views, elements, creation and MEP, every one tested against a real project.
- **It can use the interface too**, clicking through Revit itself for the jobs the software gives no other
  way to do. Mostly that means editing inside groups ([below](#automate-bulk-group-editing-with-claude)).
- **Teach it once, use it everywhere.** Save a working request as a skill and it's there in every project.
  Two come with the add-in: a read-only schedule inventory, which is a safe first thing to try, and
  elevation door and window tagging that spots the openings that are actually visible, so it won't tag ones
  hidden behind the facade.

Turning it on, once:

1. Turn **Claude Assist** on in Transom Settings (Schedule Hub → Settings tab). That sets everything up the
   first time you switch it on.
2. Restart Claude Code so it picks up the connection.

<table>
<tr>
<td width="50%"><img src="docs/images/hub-settings-claude-assist.jpg" alt="Transom Schedule Hub, Settings tab: the Claude Assist toggle switched on, with a status checklist showing the bridge listening on 127.0.0.1:48810, the session token, the deployed MCP shim, both servers registered with Claude Code, and Claude connected" width="100%"></td>
<td width="50%"><img src="docs/images/hub-claude-skills.jpg" alt="Transom Schedule Hub, Claude Skills tab: the skill library listing elevation-door-window-tags and schedule-inventory, with Stage, Import and Remove buttons, the selected skill's description, and the live bridge status checklist" width="100%"></td>
</tr>
<tr>
<td><em><b>Settings</b>: one <b>Claude Assist</b> toggle, and a checklist of every layer between Revit and Claude Code, so a broken one is obvious.</em></td>
<td><em><b>Claude Skills</b>: the skill library. Import your own, keep the ones Claude writes, and <b>Stage</b> one to paste straight into Claude Code.</em></td>
</tr>
</table>

A few things worth knowing:

- **Drag and drop one instruction file into Claude Code** and it has everything it needs to automate Revit.
  Take it from [`claude/`](claude/), and [`claude/README.md`](claude/README.md) says where to put it.
- Stuck for a first request? **Show me what you can do** hands Claude a demo it runs in a fresh project while
  you watch.
- Use **Claude Code**, not Claude Cowork. Cowork runs in the cloud and can't reach Revit on your machine.
- Run it with bypass permissions on. Otherwise its permission prompts keep stealing focus from Revit and
  Claude's clicks land in the wrong place.

### AI render enhancement — AIRE

**AI Render Enhancer** has its own ribbon button and two tabs. **Enhance** improves a batch of renders, and
**Video** turns one render into a short clip ([below](#video--one-render-one-clip)). Both work on image
**files**, not the model, so you don't need a project open. You don't need Revit either. Tick
**AIRE standalone app** when you install and it gets its own Start Menu shortcut.

<table>
<tr><td><img src="docs/images/aire.jpg" alt="Transom AI Render Enhancer window: an API key field with a Saved Keys dropdown, input and output folder pickers, model/resolution/quality selectors, a Prompt card with a saved-prompt dropdown and a Pop Out button, a checkable queue of render images with their resolutions, the estimated cost for the checked images, and a progress bar" width="100%"></td></tr>
<tr><td><em><b>AI Render Enhancer</b>: the queue, the settings that drive the cost, and the estimate for exactly what you have ticked.</em></td></tr>
</table>

- **Your render, made photoreal.** Grass, planting, lighting and concrete texture come back looking real,
  while the camera angle, perspective, geometry, mullions and trim lines stay exactly where Revit put them.
  It's your building, not a new one.
- **A whole folder at a time.** Drag in images or a folder, tick the ones you want, and get 4K back. Your
  originals are never touched, and finished images are skipped next time so you never pay twice for the same
  render.
- **See the price before you spend it.** Every batch tells you what it will cost and waits for your go-ahead.
  Stop it part way through whenever you want. Every run leaves a spreadsheet of what it did and what it cost.
- **Your account, your key, your machine.** Use your own OpenAI key. It's encrypted on your computer and goes
  nowhere but OpenAI. Save a prompt that works, or a key per account, and pick them from a list.
- **Or let Claude do the work.** Ask Claude Code to enhance a folder and it runs the same batches for you,
  without ever seeing your key. Only one job runs at a time, either way.

#### Video — one render, one clip

The **Video** tab turns one finished render into a few seconds of motion, ideally the 4K image the Enhance
tab just made. One account at [Higgsfield](https://cloud.higgsfield.ai) reaches their own camera-move models
plus Kling, Veo, Seedance, Hailuo, Sora and Wan. This is a hero shot, not a walkthrough. Clips run 2 to 12
seconds and top out at 1080p.

<table>
<tr><td><img src="docs/images/aire-video.jpg" alt="Transom AI Render Enhancer window on the Video tab: Key ID and Secret fields with a Saved Keys dropdown, an output folder picker, a model dropdown showing Higgsfield DoP Standard, clip duration/resolution/aspect dropdowns, two camera-preset dropdowns with strength sliders, a Motion Prompt card with its own saved prompts and a Pop Out button, a large source-render thumbnail with its filename, size and aspect ratio, and a Generate Clip button beside the estimated clip cost" width="100%"></td></tr>
<tr><td><em><b>Video</b>: one render, one model, only the settings that model accepts, and Higgsfield's own price for that exact request before anything is sent.</em></td></tr>
</table>

- **No surprise bills.** The price on screen is Higgsfield's own price for that exact clip, updated as you
  change settings. If it can't get you a price, it won't let you spend anything.
- **Only the settings that actually work.** Pick a model and you're offered the clip lengths, sizes and
  shapes that model really supports, so you can't set up a job it will reject. Camera moves show up on the
  models that take them. If your render is the wrong shape, you're warned before you pay, not cropped after.
- **Cancel while it still counts.** Stop a clip that's still waiting its turn and you get your money back.
  Once it starts rendering it will finish and be charged, and the button tells you which side of that line
  you're on.
- **The file is yours straight away.** Your clip downloads into your own folder with a spreadsheet line for
  what it cost. Higgsfield only keeps clips for about a week, so Transom never leaves you holding a link
  that expires.
- **The same safety rails.** One paid job at a time, whether you started it in Revit or on its own. Your
  Higgsfield login is encrypted on your machine just like your OpenAI key, and you're not limited to one.

*AIRE is much newer than the schedule editor and has had a lot less mileage. Treat your early batches and
clips as a trial, and check the first costs against your real OpenAI and Higgsfield usage.*

> **Status:** v1.9.15 (Revit 2025/2026/2027) gives AIRE the **Video tab** described above. Recent releases
> before it: a **prompt library, saved API keys and a pop-out editor** (v1.9.14), **AIRE as a standalone app**
> that runs without Revit (v1.9.12), a **Cancel Batch** button (v1.9.11), and a line-by-line **correctness
> audit** of the schedule editor (v1.9.9). Full history is on the
> [releases page](https://github.com/Dave5264/transom-revit/releases).

### Automate bulk group editing with Claude

Elements inside a Revit **model group** are where most schedule tools give up. Revit will only let you change
them from inside **Edit Group** mode, one group at a time, by hand. There is no automation for it and no way
around it. Transom hands that job to Claude Code instead, and you watch it work.

- **Claude does the clicking.** It opens the group, sets the parameter the way you would, closes it, then
  checks the value and the member count before moving on to the next one.
- **A column at a time, not a group at a time.** Every group instance your edits touch goes in one run,
  instead of you opening each one by hand.
- **You watch the whole thing.** Transom writes out the instructions and never touches the model itself.
  Every step happens in your own Revit window, in front of you.
- **The awkward cases are covered.** Nested groups, attached detail groups and excluded members are all
  handled.

Not every column needs Claude. Blue cells import on their own. For the yellow and red ones, Transom offers
two other ways through:

- **Write to a new parameter.** Transom makes one, points the schedule column at it, and puts your edits
  there. Nothing is ungrouped and the original values stay underneath. You choose whether the new parameter
  is shared across the type or set on each element. This isn't offered where the parameter drives geometry,
  since the schedule would change while the model stayed put.
- **Skip it.** Leave that column alone.

Editing inside a group changes the group itself, so every copy of that group gets the same value. When you
need them to differ, put the edit on a new per-element parameter instead. Claude is driving the real
interface here, so try it on a throwaway model first, and never Synchronize with Central part way through.

## Install

1. Download **`Transom-…-SingleUser.msi`** from the [latest release](https://github.com/Dave5264/transom-revit/releases/latest).
2. Double-click it. It installs into `%AppData%\Autodesk\Revit\Addins\` for the current user only, with no
   administrator rights.
3. Launch Revit and Transom is on the ribbon. To remove it later: *Apps & features → Transom*.

**Always use the SingleUser installer.** Claude-Assist's install-time setup only runs there. There's a
machine-wide **MultiUser** MSI built from this codebase for IT and firm-wide deployment, but it isn't linked
here on purpose. It needs admin rights, and it can't finish setting up the Claude connection until Revit's
first launch. Build it from source if you genuinely need it.

<details>
<summary><b>Building from source</b>: targets, build commands, repo layout</summary>

| Revit | Runtime | Build configuration |
|-------|---------|---------------------|
| 2025  | .NET 8  | `Debug.R25` / `Release.R25` |
| 2026  | .NET 8  | `Debug.R26` / `Release.R26` |
| 2027  | .NET 10 | `Debug.R27` / `Release.R27` |

Built on the [Nice3point Revit SDK](https://github.com/Nice3point/RevitTemplates) (multi-version +
dynamic-loading isolation). Excel via [NPOI](https://github.com/nissl-lab/npoi).

```shell
dotnet build source/Transom/Transom.csproj -c Debug.R25   # Revit 2025 (.NET 8)
dotnet build source/Transom/Transom.csproj -c Debug.R27   # Revit 2027 (.NET 10)
```

A successful build deploys the add-in to `%AppData%\Autodesk\Revit\Addins\<version>\`, so **close Revit
before building** or the copy fails on a locked DLL.

For the MSI installer, build each configuration directly and then run the installer project. Do **not** use
`build/`'s `dotnet run -- pack`. Its compile step can drop a Revit configuration and still exit 0, which
gives you an installer that silently omits a whole Revit version.

| Path | Description |
|------|-------------|
| `source/Transom/` | the add-in (commands, views, view-models, core logic) |
| `source/Transom.Aire/` | AIRE: the OpenAI engine, the Higgsfield client and generated model catalog, both job runners, encrypted settings and the window (Revit-free: files + HTTPS) |
| `source/Transom.Aire.App/` | the standalone AIRE host (same window, its own process) |
| `build/`, `install/` | ModularPipelines build + WiX installer |
| `branding/` | ribbon icon + generator |
| `docs/` | `design-notes/` (legend copy source of truth + Revit-API research notes), `parity-tool-status.md` (bridge-tool review state), `images/` (screenshots on this page) |

</details>
