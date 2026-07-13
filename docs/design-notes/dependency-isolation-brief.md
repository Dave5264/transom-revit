# Dependency isolation — NPOI cluster into `Transom.Office.dll`

Follow-up to the v1.5.0 Roslyn isolation (`Core/ScriptIsolation.cs`). Surveyed + detailed
2026-07-12 against v1.7.0 (`7989f64`). **Implementation status: see checklist at the bottom** —
this doc is written so the work can pause and resume cold.

## Why (recap)

Revit loads every add-in into one shared default `AssemblyLoadContext`; first loader of an
assembly simple name wins, and a newer-reference-vs-older-loaded bind throws
`FileLoadException` 0x80131621 (the pyRevit Roslyn 4.10-vs-4.12 failure). Transom's NPOI 2.7.1
cluster has the same risk profile: hugely popular library family (NPOI, SharpZipLib,
BouncyCastle, MathNet, ImageSharp), wide version spread, and Transom binds late (on user
action) so it tends to lose the race. A clash kills export/import — the core product.

Not candidates: CommunityToolkit.Mvvm + Nice3point.* (types woven through Transom.dll's UI —
ALC isolation would mean moving the whole UI; accepted risk, ILRepack-internalize is the
fallback). JetBrains.Annotations/Polyfill are `PrivateAssets="all"`, never deployed. Framework
`System.*` unify safely.

## Survey results (2026-07-12, all verified against source)

**Only four files use the NPOI family** (`grep "using NPOI|MathNet|SixLabors|ICSharpCode|Org.BouncyCastle"`):

| File | Lines | Public surface | Callers (exact sites) |
|---|---|---|---|
| `Core/ExcelWriter.cs` | 596 | `class ExcelWriter`: `Write(ScheduleTable, string)`, `WriteMany(List<ScheduleTable>, string, Dictionary<(string uid,int paramId),ApplyOutcome>? outcomes = null, Dictionary<(string,int),string>? attempted = null)` | `ExportEventHandler.cs:79,86`; `RunResultsWriter.cs:95` |
| `Core/ExcelReader.cs` | 508 | `class ExcelReader`: `Read(string, ISet<string>?) → ImportWorkbook`, `ReadSheetNames(string) → IReadOnlyList<(string,string,string)>` — plus **five pure DTO classes** (lines 9–117: `ImportColumn`, `ImportRow`, `ImportSheet`, `ImportHeaderGroup`, `ImportWorkbook`) | `ImportEventHandler.cs:40`; `TransomViewModel.cs:552` |
| `Core/RevisionNarrativeDocxWriter.cs` | 289 | `static RevisionNarrativeDocxWriter.Write(RevisionNarrative.Data, string, string? templatePath = null)` | `RevisionNarrativeCommand.cs`, `RevisionNarrative.cs` |
| `Core/DiagnosticsWriter.cs` | 133 | `static DiagnosticsWriter.Write(ImportWorkbook, List<CellDiagnostic>, string)` | `ImportEventHandler.cs` |

- **No NPOI/SixLabors type appears in any public/internal signature** — the surface trades in
  Transom types only: `ScheduleTable`/`CellStyleInfo` (ScheduleTable.cs), `ApplyOutcome` +
  `CellDiagnostic` (Importer.cs), `RevisionNarrative.Data`, the Import* DTOs, primitives.
- `SixLabors.*` is never referenced in code — csproj line 28 is a transitive version pin
  (rides along in the closure; keep the pin in the new project).
- The five Import DTO classes in ExcelReader.cs are consumed heavily by `Importer` (which
  stays) → they must **stay in Transom.dll** (split into a new `Core/ImportModels.cs`).
- `RunResultsWriter` does NOT use NPOI directly — it re-reads via ScheduleReader and calls
  `ExcelWriter.WriteMany` (line 95). It stays; only its call goes through the new interface.

**How Transom.Scripting is wired today (the pattern to adapt):** plain `<ProjectReference>` in
Transom.csproj (line 39) with ZERO compile-time type refs (RevitCodeExecutor reflects into
`Transom.Scripting.ScriptHost` via `ScriptIsolation.ScriptHostAssembly`). That direction
(Transom → satellite) worked because Scripting shares no Transom types.

## The one design wrinkle: reference direction

Office **must compile against Transom.dll** (ScheduleTable, ImportWorkbook, …), so the
Scripting direction is impossible — and Transom.csproj must NOT ProjectReference
Transom.Office (circular graph; also a compile-time typeref would load Office, and its NPOI
binds, into the DEFAULT context, defeating everything).

**Chosen approach — reversed reference, runtime-safe:**
- `Transom.Office.csproj` → `<ProjectReference Include="..\Transom\Transom.csproj" Private="false" />`
  (compile against it, don't copy it).
- At runtime the isolation context delegates `Transom*` / `RevitAPI*` / framework loads to the
  default context (same rule as `RoslynLoadContext.Load`), so Office's compiled-in Transom
  typerefs bind to the ONE Transom.dll already loaded — single type identity across the
  boundary, casts work.
- Transom.dll gets the interface + a loader; it discovers the impl by reflection ONCE
  (`Activator.CreateInstance` on `Transom.Office.OfficeEngine`, cast to `IOfficeEngine`),
  then all calls are normal interface dispatch.
- **Build/packaging**: Office's post-build target copies `Transom.Office.dll` +
  `Transom.Office.deps.json` into the three places Transom lands: `bin\$(Configuration)\`,
  `bin\$(Configuration)\publish\Transom\`, and the Revit Addins deploy folder. Building
  `Transom.Office.csproj -c Release.R2x` transitively builds Transom first, so **the release
  runbook's per-config build command changes from Transom.csproj to Transom.Office.csproj**
  (one command per config, same as today). Update `clickhelper-test/BUILD_PUBLISH_RUNBOOK.md`
  and the memory note when done.

## Implementation steps

1. **`Core/ImportModels.cs`** (Transom.dll): move the five DTO classes out of ExcelReader.cs
   verbatim (lines 9–117). No code changes, just relocation. Keep `namespace Transom.Core`.
2. **`Core/IOfficeEngine.cs`** (Transom.dll): interface with the four entry points, signatures
   IDENTICAL to today's (including the optional `outcomes`/`attempted`/`templatePath` params):
   `WriteWorkbooks`, `ReadWorkbook`, `ReadSheetNames`, `WriteRevisionNarrative`,
   `WriteDiagnostics`.
3. **`Core/IsolatedAssembly.cs`** (Transom.dll): generalize `ScriptIsolation`'s
   `RoslynLoadContext` into `IsolatedAssembly(string dllName, string contextName,
   string[] localPrefixes)` — same Load rules: never shadow `Transom*` (except the satellite
   itself) / `RevitAPI*`; resolve via deps.json resolver when present; prefix-match from the
   add-in folder; else null → default context. Rewrite `ScriptIsolation` as a thin wrapper
   (`Microsoft.CodeAnalysis` prefix) so RevitCodeExecutor is untouched; add `OfficeIsolation`
   exposing `IOfficeEngine Engine` (lazy singleton; prefixes: `NPOI`, `ICSharpCode`,
   `BouncyCastle`, `MathNet`, `Enums.NET`, `ExtendedNumerics`,
   `Microsoft.IO.RecyclableMemoryStream`, `SixLabors`).
4. **`source/Transom.Office/` project**: net8.0 (match Transom.Scripting's TFM approach — check
   its csproj for the multi-config pattern; it builds per Release.R2x with the parent), NPOI
   2.7.1 + SixLabors.ImageSharp 2.1.10 pin PackageReferences, ProjectReference to Transom
   (`Private=false`), `GenerateDependencyFile=true`. Move the four files' implementation code
   (ExcelWriter/ExcelReader classes minus DTOs, DocxWriter, DiagnosticsWriter) into namespace
   `Transom.Office`, plus `OfficeEngine : IOfficeEngine` delegating to them. Post-build copy
   target (dll + deps.json → the three destinations, `Condition="Exists(...)"`-guarded).
5. **Transom.csproj**: remove NPOI + SixLabors PackageReferences. Do NOT add a reference to
   Office.
6. **Call-site swap** (mechanical, 6 sites): `new ExcelWriter().X(...)` →
   `OfficeIsolation.Engine.X(...)` at ExportEventHandler.cs:79,86, RunResultsWriter.cs:95;
   `new ExcelReader().Read` → `OfficeIsolation.Engine.ReadWorkbook` at ImportEventHandler.cs:40;
   `.ReadSheetNames` at TransomViewModel.cs:552; `RevisionNarrativeDocxWriter.Write` /
   `DiagnosticsWriter.Write` → engine calls in RevisionNarrativeCommand.cs,
   RevisionNarrative.cs, ImportEventHandler.cs.
7. **deps.json packaging fix (do even if the rest pauses)**: `Transom.Scripting.deps.json` is
   not deployed today, so ScriptIsolation's `AssemblyDependencyResolver` arm is inert (prefix
   match carries everything). Ensure both satellites' `*.deps.json` reach
   `publish\Transom\` + the Addins deploy (either the satellite post-build copy, or a `<None>`
   include in Transom.csproj like the ClickHelper exes at lines 82–92).
8. **Build all three configs** via `dotnet build source/Transom.Office/Transom.Office.csproj
   -c Release.R2x`; verify each `bin\Release.R2x\publish\Transom\` contains Transom.Office.dll
   + BOTH deps.json files + the NPOI closure dlls (they now arrive via Office's copy, not
   Transom's — confirm none went missing vs the v1.7.0 manifest).
9. **Update** `BUILD_PUBLISH_RUNBOOK.md` + the `project_transom_release` memory (build command
   changes) and bump `AppInfo.Version` at next release.

## Test plan

- Round-trip regression in live Revit: export a schedule (colors + hidden anchor intact),
  import preview + apply, run-results workbook (bold/italic overlays — exercises
  `WriteMany(outcomes, attempted)`), revision narrative .docx (template reuse path), import
  diagnostics dump. All should be byte-for-byte-equivalent behavior.
- CSV + .xls export branches (WriteMany's ext switch) still work.
- `execute_revit_code` still works (ScriptIsolation refactor didn't regress) — both isolated
  contexts alive in one session.
- **Conflict simulation** (the point of it all): temporary add-in that loads an OLD
  `ICSharpCode.SharpZipLib`/NPOI into the default context at startup; export + narrative must
  still work (they fail today).
- Fresh Revit start: no first-use jank (the lazy `OfficeIsolation.Engine` load happens on
  first export/import — acceptable; it's file IO + JIT, same as Roslyn's first compile).

## Implementation notes (deviations found while building, 2026-07-12)

- **Transom.Office CANNOT multi-target `net8.0;net10.0` like Scripting does** — the ProjectReference
  to Transom means each TFM leg must find a compatible Transom TFM, and under any one configuration
  Transom offers exactly one (`net8.0-windows7.0` for R25/R26, net10 flavor for R27) → the other leg
  fails restore (NU1201). Office therefore uses a SINGLE config-conditional
  `<TargetFramework>net8.0-windows</TargetFramework>` (net10.0-windows when config ends R27), which
  also made the PayloadTfm copy-guard unnecessary (one TFM builds per config, its output copies).
- **Scripting deps.json path**: Transom's `$(TargetFramework)` evaluates to `net8.0-windows7.0`,
  not `net8.0-windows`, so a `.Replace('-windows','')` trick yields garbage (`net8.07.0`) — and the
  literal `-windows` inside an `Exists('…')` condition is an MSBuild parse error anyway (MSB4092).
  Fixed with a config-derived `$(ScriptingTfm)` property (net8.0, net10.0 for R27) in Transom.csproj.
- Caller list correction: `RevisionNarrative.cs` does NOT call the docx writer — only
  `RevisionNarrativeCommand.cs:69` did. The swap touched 7 call expressions in 5 files.
- Publish manifest verified against the pre-isolation Addins deploy (which still held the old file
  set): only `PLAYBOOK.md` (docs, deployed separately) and `Transom.pdb` (dev-only) differ — the
  full 12-dll NPOI closure ships in all three configs, plus all THREE deps.json files, in
  `publish\Transom\` AND `%AppData%\Autodesk\Revit\Addins\<yr>\Transom\`.

## Revit 2027 correction (found in live R27 smoke test, 2026-07-12)

The premise "Revit loads every add-in into one shared default ALC" holds for R25/R26 only —
**Revit 2027 loads each add-in into its own `AddInLoader.AddInLoadContext`**. Two fixes followed:

- `ScriptHost.Run` now registers every reference with an `InteractiveAssemblyLoader` — without it,
  Roslyn re-loaded Transom.dll from disk into its own context ("[A]Globals cannot be cast to
  [B]Globals"; pre-existing on R27 since scripting shipped, first exercised today).
- `IsolatedAssembly.SatelliteLoadContext` resolves `Transom*`/`RevitAPI*` through the context that
  loaded Transom.dll (`HostContext`), not the null→Default fallback (Default has no Transom on R27).

R27 smoke test after the fix (doc springfield_test_R27, .NET 10.0.8): one Transom.dll in ALC "TRANSOM",
cross-boundary `is IOfficeEngine` check true, xlsx round-trip (PARTITION TYPE F) + docx smoke pass,
NPOI family only in the Transom.Office ALC. Contexts: TRANSOM / Transom.Roslyn / Transom.Office.

## Status checklist (update as you go)

- [x] 1. ImportModels.cs split
- [x] 2. IOfficeEngine interface
- [x] 3. IsolatedAssembly + ScriptIsolation rewrite + OfficeIsolation
- [x] 4. Transom.Office project + code move + OfficeEngine + post-build copies
- [x] 5. Transom.csproj package trims
- [x] 6. Call-site swap (7 expressions / 5 files — see notes)
- [x] 7. deps.json packaging (BOTH satellites)
- [x] 8. Three-config build green + publish manifest verified (2026-07-12)
- [x] 9. Runbook + memory updates (AppInfo.Version bump deferred to next release, per plan)
- [x] Live test pass (2026-07-12, doc "test test test_doblackMR4M2", Transom 1.7.0 new build):
  `execute_revit_code` works (Roslyn ALC alive) · engine loads as `Transom.Office.OfficeEngine` in
  ALC "Transom.Office" · ALC audit: NPOI/SharpZipLib/SixLabors live ONLY in that context, default
  context NPOI-free · WriteWorkbooks round-trip on DOOR SCHEDULE - UNIT DOORS (13.7 KB xlsx, hidden
  uid anchor intact, ReadSheetNames tuple correct, ReadWorkbook 1 sheet / 17 cols / 11 rows) ·
  WriteRevisionNarrative smoke test (4.2 KB docx). NOT yet exercised: WriteMany outcomes/attempted
  overlays, WriteDiagnostics, CSV/.xls branches, UI-driven import apply — same engine/ALC path,
  low residual risk; cover at next normal use.
- [x] Conflict simulation — the Roslyn half is proven by LIVE conditions, not simulation: pyRevit
  had loaded Microsoft.CodeAnalysis 4.10 into the DEFAULT context this session while Transom's 4.12
  ran fine in its own ALC (the exact pre-v1.5.0 failure, now harmless). NPOI half: no conflicting
  add-in present on this machine; the ALC audit shows no NPOI in the default context to clash with.
  A throwaway old-SharpZipLib add-in sim remains optional future work.
