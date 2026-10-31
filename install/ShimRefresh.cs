using System;
using System.IO;
using WixToolset.Dtf.WindowsInstaller;

namespace Installer;

/// <summary>
///     #105 (seamless MCP, MUST-FIX) — install-time refresh of the per-user MCP shim trio.
///     <para>
///     The SingleUser (per-user) MSI lays the shim trio (<c>Transom.McpShim.exe</c> + <c>Transom.ClickHelper.exe</c>
///     + <c>Transom.ClickHelper.Mcp.exe</c>) next to <c>Transom.dll</c> in the add-in directory, but the registered
///     MCP config points the Claude client at the ABSOLUTE per-user path <c>%LocalAppData%\Transom\mcp\</c> — which
///     previously only Revit's first launch populated. If the client launches a STALE/ABSENT shim from there (e.g. it
///     auto-updates its MCP protocol while %LocalAppData% still holds an old shim), it can respond
///     <c>-32602 Unsupported protocol version</c> and DISCONNECT → Claude-Assist silently dead until Revit is opened.
///     </para>
///     <para>
///     This action COPIES the trio from the install dir into <c>%LocalAppData%\Transom\mcp\</c> at install time, so the
///     shim is current the moment install finishes — no Revit needed. It is scheduled DEFERRED + IMPERSONATED, so it
///     runs as the installing USER and <c>%LocalAppData%</c> resolves to THAT user's profile (admin-free). The copy is
///     best-effort: a locked/missing file is skipped (never fails the install) and the add-in's first-launch copy
///     (<c>EnsureBundledShimAndAutoRegister</c>) remains as the self-heal fallback for the exe-locked case.
///     </para>
///     <para>
///     The same action refreshes the AutoCAD plug-in's shim (<c>Transom.Acad.McpShim.exe</c>) from the bundle's
///     <c>Contents</c> folder, passed in as ACADDIR. The two halves are independent: either feature may be installed
///     without the other, and the AutoCAD plug-in's own startup copy (Transom.Acad's McpSetup) is its fallback.
///     </para>
/// </summary>
public static class ShimRefresh
{
    // The trio that the Claude client launches by absolute path from %LocalAppData%\Transom\mcp\.
    private static readonly string[] ShimFiles =
    {
        "Transom.McpShim.exe",
        "Transom.ClickHelper.exe",
        "Transom.ClickHelper.Mcp.exe",
    };

    private static readonly string[] AcadShimFiles = { "Transom.Acad.McpShim.exe" };

    [CustomAction]
    public static ActionResult RefreshLocalAppDataShim(Session session)
    {
        try
        {
            var destDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Transom", "mcp");

            // DEFERRED custom actions can't read installer properties directly — the install dirs are passed in via
            // CustomActionData (UsesProperties = "INSTALLDIR=[INSTALLDIR],ACADDIR=[ACADDIR]"). The add-in payload
            // nests under a "Transom" subfolder of the versioned install dir (the .addin manifest points at
            // "Transom\Transom.dll"), so the Revit shim source is <INSTALLDIR>\Transom.
            var installDir = Data(session, "INSTALLDIR");
            if (string.IsNullOrEmpty(installDir))
                session.Log("Transom #105: INSTALLDIR not provided to the shim-refresh action; skipping the Revit shims.");
            else
            {
                var sourceDir = Path.Combine(installDir, "Transom");
                if (!Directory.Exists(sourceDir) && ShimPresentIn(installDir)) sourceDir = installDir; // flat layout fallback
                if (Directory.Exists(sourceDir))
                    CopyAll(session, sourceDir, ShimFiles, destDir, "Revit first-launch");
                else
                    session.Log($"Transom #105: shim source not found under '{sourceDir}' or '{installDir}'; skipping (Revit first-launch will self-heal).");
            }

            // The AutoCAD bundle: ACADDIR is <...>\ApplicationPlugins\Transom.Acad.bundle. It resolves even when the
            // AutoCAD feature is deselected; then nothing is on disk there and this half simply logs and skips.
            var acadDir = Data(session, "ACADDIR");
            var acadContents = string.IsNullOrEmpty(acadDir) ? null : Path.Combine(acadDir, "Contents");
            if (acadContents != null && File.Exists(Path.Combine(acadContents, AcadShimFiles[0])))
                CopyAll(session, acadContents, AcadShimFiles, destDir, "AutoCAD plug-in startup");
            else
                session.Log("Transom #105: AutoCAD plug-in shim not installed; skipping it.");

            return ActionResult.Success;
        }
        catch (Exception ex)
        {
            // The shim refresh must NEVER fail the install — the Revit first-launch copy is the fallback.
            try { session.Log($"Transom #105: shim refresh failed (non-fatal): {ex.Message}"); } catch { /* ignore */ }
            return ActionResult.Success;
        }
    }

    /// <summary>A missing CustomActionData key must not throw: an MSI built before ACADDIR existed never sends it.</summary>
    private static string? Data(Session session, string key)
    {
        try { return session.CustomActionData.ContainsKey(key) ? session.CustomActionData[key] : null; }
        catch { return null; }
    }

    /// <summary>Best-effort copy of <paramref name="names"/> from <paramref name="sourceDir"/> into
    /// <paramref name="destDir"/>. Never throws; a locked or missing file is logged and skipped.</summary>
    private static void CopyAll(Session session, string sourceDir, string[] names, string destDir, string fallback)
    {
        Directory.CreateDirectory(destDir);
        int copied = 0;
        foreach (var name in names)
        {
            var src = Path.Combine(sourceDir, name);
            var dst = Path.Combine(destDir, name);
            try
            {
                if (!File.Exists(src))
                {
                    session.Log($"Transom #105: bundled shim '{name}' not found at '{src}'; skipping it.");
                    continue;
                }
                File.Copy(src, dst, overwrite: true); // refresh on install/upgrade/repair — overwrite a stale shim
                copied++;
            }
            catch (IOException ex)
            {
                // A running Claude client can hold the exe locked — best-effort: leave it, the host application's
                // own copy on its next launch is the self-heal fallback for this case.
                session.Log($"Transom #105: couldn't copy '{name}' (likely in use): {ex.Message}. {fallback} copy will self-heal.");
            }
            catch (UnauthorizedAccessException ex)
            {
                session.Log($"Transom #105: access denied copying '{name}': {ex.Message}. {fallback} copy will self-heal.");
            }
        }
        session.Log($"Transom #105: refreshed {copied}/{names.Length} shim file(s) from '{sourceDir}' into '{destDir}'.");
    }

    private static bool ShimPresentIn(string dir)
    {
        try { return File.Exists(Path.Combine(dir, ShimFiles[0])); }
        catch { return false; }
    }
}
