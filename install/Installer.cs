using Installer;
using WixSharp;
using WixSharp.CommonTasks;
using WixSharp.Controls;

const string outputName = "Transom";
const string projectName = "Transom";

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
        Manufacturer = Environment.UserName,
        ProductIcon = @"install\Resources\Icons\ShellIcon.ico"
    }
};

var wixEntities = Generator.GenerateWixEntities(args[1..]);
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
    project.Dirs =
    [
        new InstallDir(versioning.VersionPrefix.Major >= 2027 ? @"%ProgramFiles%\Autodesk\Revit\Addins" : @"%CommonAppDataFolder%\Autodesk\Revit\Addins", wixEntities)
    ];
    project.Actions = []; // #105: NOT on the per-machine MSI (runs as SYSTEM — can't write each user's %LocalAppData%)
    project.BuildMsi();
}