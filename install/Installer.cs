using Installer;
using WixSharp;
using WixSharp.CommonTasks;
using WixSharp.Controls;

const string outputName = "Transom";
const string projectName = "Transom";

// #105 BUILD-ENV FIX: packing a WixSharp ManagedAction compiles a native SfxCA via ILCompiler (PublishAot), whose
// native LINK step shells `vswhere.exe` (unqualified) to locate the VC++ toolchain (link.exe). On a machine where
// vswhere isn't on PATH the link fails ("'vswhere.exe' is not recognized" → link.exe exit 123 → MSB3073) and WixSharp
// emits NO SfxCA binary → the SingleUser MSI build dies with WIX0103 "Cannot find the Binary file" — while the
// CA-less MultiUser MSI still builds, so the run looks like a (partial) success. vswhere ships in a fixed location;
// make the AOT link reproducible (NOT dependent on an ambient/dev-prompt PATH) by ensuring that dir is on PATH for
// this process — ILCompiler runs as a child and inherits it. Idempotent; no-op if vswhere is already resolvable.
EnsureVsWhereOnPath();

void EnsureVsWhereOnPath()
{
    try
    {
        // Already resolvable on PATH? then nothing to do.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        bool resolvable = path.Split(System.IO.Path.PathSeparator)
            .Any(d => !string.IsNullOrEmpty(d) && System.IO.File.Exists(System.IO.Path.Combine(d, "vswhere.exe")));
        if (resolvable) return;

        // vswhere.exe ships with the VS Installer at a fixed per-machine location (independent of which VS edition).
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var vsInstallerDir = System.IO.Path.Combine(pf86, "Microsoft Visual Studio", "Installer");
        if (System.IO.File.Exists(System.IO.Path.Combine(vsInstallerDir, "vswhere.exe")))
            Environment.SetEnvironmentVariable("PATH", vsInstallerDir + System.IO.Path.PathSeparator + path);
    }
    catch { /* best-effort: if this fails the build surfaces the original vswhere error, which is the right signal */ }
}

var versioning = Versioning.CreateFromVersionStringAsync(args[0]);
var project = new Project
{
    OutDir = "output",
    Name = projectName,
    Platform = Platform.x64,
    UI = WUI.WixUI_FeatureTree,
    MajorUpgrade = MajorUpgrade.Default,
    GUID = new Guid("AE4E397D-4A58-4E96-AF24-007CCE229CCF"),
    BannerImage = @"install\Resources\Icons\BannerImage.png",
    BackgroundImage = @"install\Resources\Icons\BackgroundImage.png",
    Version = versioning.VersionPrefix,
    ControlPanelInfo =
    {
        // Fixed product name, NOT Environment.UserName — that put the building account's Windows
        // username in the MSI's Manufacturer, so every installer showed a personal username as the
        // Publisher in Add/Remove Programs, and it changed depending on who cut the release.
        Manufacturer = projectName,
        ProductIcon = @"install\Resources\Icons\ShellIcon.ico"
    }
};

var wixEntities = Generator.GenerateWixEntities(args[1..]);

// ENGINE-PRESENT GUARD — regression guard for the v1.8.0 defect where the Excel/docx engine (Transom.Office.dll
// + its NPOI closure) silently fell out of the MSI (it lives in a deliberately-unreferenced project, so nothing
// builds it automatically). If the engine isn't staged next to Transom.dll in the harvested payload, FAIL THE
// PACK — never ship an MSI that can't export (v1.8.0–v1.9.1 shipped broken this way, surfacing at runtime as a
// mislabeled "'…xlsx' is open in Excel" error).
AssertEngineHarvested(args[1..]);

project.RemoveDialogsBetween(NativeDialogs.WelcomeDlg, NativeDialogs.CustomizeDlg);

// AIRE STANDALONE — an optional Start Menu shortcut to the AI Render Enhancer running in its own process, so
// renders can be enhanced with Revit closed. It rides the existing WixUI_FeatureTree: a Feature shows up in
// CustomizeDlg as a checkbox with this Description in the pane beside it, and ConfigurableDir lights up that
// dialog's Browse button so the install location can be changed or simply clicked past — the same treatment
// the per-Revit-year add-in folders already get.
// Offered by BOTH MSIs; only the payload root differs (see AireDirs). The per-user MSI runs as the installing
// user, so %LocalAppDataFolder% is theirs. The per-machine MSI runs elevated / as SYSTEM, where that would
// resolve to the wrong profile, so it installs under %ProgramFiles% instead. The Start Menu needs no such
// split: MSI resolves %ProgramMenuFolder% to the per-user Start Menu for a per-user install and to the
// all-users Start Menu when ALLUSERS=1 — exactly what a firm-wide deployment wants. (This is unlike the shim
// custom action below, which must write into EACH user's %LocalAppData% at install time — something no
// per-machine MSI can do — and therefore really is per-user only.)
const string aireAppPublish = @"source\Transom.Aire.App\bin\Release\net8.0-windows\win-x64\publish";
AssertAireAppPublished(versioning.VersionPrefix);

var aireFeature = new Feature
{
    Name = "AIRE standalone app",
    Description =
        "AIRE (AI Render Enhancer) batch-improves architectural renders — grass, planting, lighting, "
        + "concrete — through OpenAI's image API, keeping your camera angle and geometry. It is always "
        + "available on Revit's Transom ribbon; tick this to ALSO get a Start Menu shortcut that runs it "
        + "on its own, so you can enhance renders without opening Revit. "
        + "Needs your own OpenAI API key, and every batch spends real credit — AIRE shows an estimate and "
        + "asks before it starts. About 0.4 MB.",
    Display = FeatureDisplay.expand,
    ConfigurableDir = "AIREDIR"
};

// The payload lives in ONE location rather than inside each Revit-year add-in folder: three copies would
// otherwise be installed, and the shortcut would point at whichever year's folder happened to be chosen —
// breaking if that Revit version were later removed. `payloadRoot` is the scope-appropriate home (see above):
// %LocalAppDataFolder% for the per-user MSI, %ProgramFiles% for the per-machine one (WixSharp maps that to
// ProgramFiles64Folder because the project is x64 — check the Directory table, not the string, if in doubt).
// Both "Transom" parent folders carry EXPLICIT ids. WixSharp auto-names directories by dedup ("Transom",
// "Transom.1", …) from a map it resets between the two BuildMsi calls — but the harvested add-in Dirs are
// reused across both builds and keep the ids they were dealt in the first one, so in the second build a fresh
// auto-named "Transom" lands on an id the harvest already holds (duplicate Directory 'Transom.1', WIX0091).
// Explicit ids sidestep the map entirely and keep both MSIs' Directory tables stable release to release.
Dir[] AireDirs(string payloadRoot) =>
[
    new Dir(new Id("TRANSOM_AIRE_ROOT"), $@"{payloadRoot}\Transom",
        new Dir(new Id("AIREDIR"), "aire",
            new Files(aireFeature, $@"{aireAppPublish}\*.*", f => !f.EndsWith(".pdb")))),
    new Dir(new Id("TRANSOM_AIRE_MENU"), @"%ProgramMenuFolder%\Transom",
        new ExeFileShortcut(aireFeature, "AIRE — AI Render Enhancer", "[AIREDIR]Transom.Aire.App.exe", ""))
];

// Same spirit as AssertEngineHarvested: a feature that is advertised in the UI but whose files were never
// published would install a shortcut to nothing — and one whose files were published for an EARLIER release
// would silently ship last version's app under this version's installer. That second case is the real hazard:
// step 3's csproj builds never touch this publish folder, so it only changes when `dotnet publish` is run for
// it explicitly, and nothing else in the pack would notice if that was skipped. Fail the pack in both cases.
// The exe's file version comes from the -p:Version passed to that publish, so a mismatch means exactly one
// thing: the publish was skipped, or run without this release's version.
void AssertAireAppPublished(System.Version expected)
{
    var exe = System.IO.Path.Combine(aireAppPublish, "Transom.Aire.App.exe");
    var want = Trim(expected);
    var publishCommand =
        $"  dotnet publish source/Transom.Aire.App/Transom.Aire.App.csproj -c Release -r win-x64 --self-contained false -p:Version={want}";

    if (!System.IO.File.Exists(exe))
        throw new Exception(
            $"AIRE standalone app MISSING from the payload: '{exe}' was not found. Publish it first —\n" +
            publishCommand + "\n" +
            "Refusing to ship an installer that offers an AIRE shortcut pointing at nothing.");

    var found = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileVersion;
    if (!System.Version.TryParse(found, out var actual) || Trim(actual) != want)
        throw new Exception(
            $"AIRE standalone app is STALE: '{exe}' is file version {found ?? "(none)"} but this pack is {want}. " +
            "That publish folder is only refreshed by an explicit publish, so re-run it with this release's version —\n" +
            publishCommand + "\n" +
            "Refusing to ship last release's AIRE under this release's installer.");

    Console.WriteLine($"OK: AIRE standalone app {found} matches pack version {want}");

    // Compare major.minor.build only: the exe carries a 4th component (1.9.15.0) that the pack version never has.
    static System.Version Trim(System.Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}

// #105 (seamless MCP, MUST-FIX): a DEFERRED, IMPERSONATED managed custom action that copies the shim trio from the
// install dir into %LocalAppData%\Transom\mcp\ at install time (see ShimRefresh) — so the Claude client launches the
// CURRENT shim WITHOUT needing Revit opened first (protocol-skew hazard otherwise). SingleUser (per-user) ONLY: that
// MSI runs as the user, so Impersonate=true writes the user's %LocalAppData% admin-free. The per-machine (MultiUser)
// MSI runs as SYSTEM and CANNOT target each user's %LocalAppData% at install time, so it relies on the add-in's
// Revit-first-launch copy (EnsureBundledShimAndAutoRegister), which also stays as the self-heal fallback everywhere.
//   • After InstallFiles (the trio is on disk to copy from); NOT_BeingRemoved so it fires on fresh install AND
//     upgrade/repair (refresh a stale shim) but not uninstall. Return.ignore + best-effort copy → never fails install.
//   • Deferred CAs can't read INSTALLDIR directly → it's passed via CustomActionData (UsesProperties).
var refreshShim = new ManagedAction(
    ShimRefresh.RefreshLocalAppDataShim,
    Return.ignore,
    When.After,
    Step.InstallFiles,
    Condition.NOT_BeingRemoved)
{
    Execute = Execute.deferred,
    Impersonate = true,
    UsesProperties = "INSTALLDIR=[INSTALLDIR]",
};

BuildSingleUserMsi();
BuildMultiUserUserMsi();

// #105 FALSE-SUCCESS GUARD: WixSharp's BuildMsi can FAIL one MSI (e.g. the SingleUser one, if the SfxCA didn't pack)
// while the process still exits 0 — so a dropped installer silently reads as success. FAIL LOUDLY instead: assert
// BOTH expected MSIs landed in output/ (the SingleUser one carries the #105 custom action and must never be missing).
AssertBuilt($"{outputName}-{versioning.Version}-SingleUser.msi");
AssertBuilt($"{outputName}-{versioning.Version}-MultiUser.msi");

void AssertBuilt(string msiName)
{
    var msiPath = System.IO.Path.Combine(project.OutDir, msiName);
    if (!System.IO.File.Exists(msiPath))
        throw new Exception(
            $"Installer build did NOT produce '{msiPath}'. The MSI was dropped (likely a custom-action/SfxCA pack " +
            $"failure earlier in the log) — failing the build so this is not mistaken for success.");
    Console.WriteLine($"OK: built {msiPath} ({new System.IO.FileInfo(msiPath).Length:N0} bytes)");
}

// Assert the Excel/docx engine is present next to every Transom.dll in the harvested publish payload. The engine
// (Transom.Office.dll) loads from Transom.dll's OWN folder at run time, so it must ship right beside it; NPOI.Core.dll
// is checked too as a representative of the isolated closure (NPOI 2.7.x ships split assemblies — there is NO
// single NPOI.dll). Missing → throw: the MSI would otherwise install an
// add-in that cannot export. The engine is produced by Transom.Office.csproj, whose CopyIntoTransom target stages
// it into publish/Transom/ (Transom.csproj's BuildOfficeEngine target builds Office after Transom).
void AssertEngineHarvested(string[] publishDirs)
{
    string[] required = ["Transom.Office.dll", "NPOI.Core.dll", "Transom.Office.deps.json"];
    foreach (var dir in publishDirs)
    {
        if (!System.IO.Directory.Exists(dir)) continue;
        foreach (var transomDll in System.IO.Directory.GetFiles(dir, "Transom.dll", System.IO.SearchOption.AllDirectories))
        {
            var payloadDir = System.IO.Path.GetDirectoryName(transomDll)!;
            var missing = required.Where(f => !System.IO.File.Exists(System.IO.Path.Combine(payloadDir, f))).ToArray();
            if (missing.Length > 0)
                throw new Exception(
                    $"Excel engine MISSING from harvest: '{string.Join("', '", missing)}' is not next to '{transomDll}'. " +
                    "The Transom.Office engine + NPOI closure were not staged into the publish payload — build " +
                    "source/Transom.Office/Transom.Office.csproj for this config (its CopyIntoTransom target stages it), " +
                    "then re-run the installer. Refusing to ship an MSI that cannot export (the v1.8.0 regression).");
        }
    }
}

void BuildSingleUserMsi()
{
    project.Scope = InstallScope.perUser;
    project.OutFileName = $"{outputName}-{versioning.Version}-SingleUser";
    project.Dirs =
    [
        new InstallDir(@"%AppDataFolder%\Autodesk\Revit\Addins\", wixEntities),
        ..AireDirs(@"%LocalAppDataFolder%") // optional AIRE standalone app, in the installing user's profile
    ];
    project.Actions = [refreshShim]; // #105: install-time %LocalAppData% shim refresh — per-user MSI only
    project.BuildMsi();
}

void BuildMultiUserUserMsi()
{
    project.Scope = InstallScope.perMachine;
    project.OutFileName = $"{outputName}-{versioning.Version}-MultiUser";
    // Install root for the per-machine MSI. This was a ternary on `versioning.VersionPrefix.Major >= 2027`
    // — but VersionPrefix is the TRANSOM product version (semver: 1.9.x), not a Revit year, so Major is 1
    // and the %ProgramFiles% branch could never be taken. Removed rather than repaired: the effective
    // behaviour is unchanged (every MultiUser MSI has always installed here), and inventing a
    // Revit-2027-specific root without confirming Revit 2027 actually scans it would risk deploying
    // firm-wide to a path Revit never reads. The per-year subdirectory still comes from Generator's
    // Dir ids, so year separation is preserved either way — only the root is at issue.
    // If Revit 2027 does require %ProgramFiles%, key the choice off the harvested feature year
    // ("2025"/"2026"/"2027" from Generator.TryParseVersion), never off the product version.
    project.Dirs =
    [
        new InstallDir(@"%CommonAppDataFolder%\Autodesk\Revit\Addins", wixEntities),
        ..AireDirs(@"%ProgramFiles%") // optional AIRE standalone app, machine-wide, with an all-users Start Menu shortcut
    ];
    project.Actions = []; // #105: NOT on the per-machine MSI (runs as SYSTEM — can't write each user's %LocalAppData%)
    project.BuildMsi();
}