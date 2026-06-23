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

    [CustomAction]
    public static ActionResult RefreshLocalAppDataShim(Session session)
    {
        try
        {
            // DEFERRED custom actions can't read installer properties directly — the install dir is passed in via
            // CustomActionData (UsesProperties = "INSTALLDIR=[INSTALLDIR]"). The add-in payload nests under a
            // "Transom" subfolder of the versioned install dir (the .addin manifest points at "Transom\Transom.dll"),
            // so the shim source is <INSTALLDIR>\Transom.
            var installDir = session.CustomActionData["INSTALLDIR"];
            if (string.IsNullOrEmpty(installDir))
            {
                session.Log("Transom #105: INSTALLDIR not provided to the shim-refresh action; skipping.");
                return ActionResult.Success; // never fail the install over this
            }

            var sourceDir = Path.Combine(installDir, "Transom");
            if (!Directory.Exists(sourceDir))
            {
                // Fallback: some layouts may place the payload directly in the install dir.
                if (ShimPresentIn(installDir)) sourceDir = installDir;
                else
                {
                    session.Log($"Transom #105: shim source not found under '{sourceDir}' or '{installDir}'; skipping (Revit first-launch will self-heal).");
                    return ActionResult.Success;
                }
            }

            var destDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Transom", "mcp");
            Directory.CreateDirectory(destDir);

            int copied = 0;
            foreach (var name in ShimFiles)
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
                    // A running Claude client can hold the exe locked — best-effort: leave it, the add-in's
                    // first-launch copy is the self-heal fallback for this case.
                    session.Log($"Transom #105: couldn't copy '{name}' (likely in use): {ex.Message}. First-launch copy will self-heal.");
                }
                catch (UnauthorizedAccessException ex)
                {
                    session.Log($"Transom #105: access denied copying '{name}': {ex.Message}. First-launch copy will self-heal.");
                }
            }

            session.Log($"Transom #105: refreshed {copied}/{ShimFiles.Length} shim file(s) into '{destDir}'.");
            return ActionResult.Success;
        }
        catch (Exception ex)
        {
            // The shim refresh must NEVER fail the install — the Revit first-launch copy is the fallback.
            try { session.Log($"Transom #105: shim refresh failed (non-fatal): {ex.Message}"); } catch { /* ignore */ }
            return ActionResult.Success;
        }
    }

    private static bool ShimPresentIn(string dir)
    {
        try { return File.Exists(Path.Combine(dir, ShimFiles[0])); }
        catch { return false; }
    }
}
