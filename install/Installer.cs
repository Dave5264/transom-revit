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
        new InstallDir(@"%AppDataFolder%\Autodesk\Revit\Addins\", wixEntities)
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
        new InstallDir(@"%CommonAppDataFolder%\Autodesk\Revit\Addins", wixEntities)
    ];
    project.Actions = []; // #105: NOT on the per-machine MSI (runs as SYSTEM — can't write each user's %LocalAppData%)
    project.BuildMsi();
}