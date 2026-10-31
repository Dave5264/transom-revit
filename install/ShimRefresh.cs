using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Transom.Core;
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
///     runs as the installing USER and <c>%LocalAppData%</c> resolves to THAT user's profile (admin-free). A shim that is
///     running (Claude Code open) is renamed aside and replaced, as the add-in does. The copy is best-effort: a file
///     that can't be replaced is skipped (never fails the install) and the add-in's first-launch copy
///     (<c>EnsureBundledShimAndAutoRegister</c>) remains as the self-heal fallback.
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
            // CustomActionData (UsesProperties = "INSTALLDIR=[INSTALLDIR],ACADDIR=[ACADDIR]"). INSTALLDIR is the Revit
            // Addins ROOT, not a versioned dir: the payload sits one Revit-year folder below it, nested under "Transom"
            // (the .addin manifest points at "Transom\Transom.dll"), so the Revit shim source is
            // <INSTALLDIR>\<year>\Transom (Directory table: INSTALL2025 = INSTALLDIR\2025, Transom under that). This
            // used to look in <INSTALLDIR>\Transom, which never exists, so only Revit's first launch ever copied.
            var installDir = Data(session, "INSTALLDIR");
            if (string.IsNullOrEmpty(installDir))
                session.Log("Transom #105: INSTALLDIR not provided to the shim-refresh action; skipping the Revit shims.");
            else
            {
                var sourceDir = FindRevitShimDir(installDir);
                if (sourceDir != null)
                    CopyAll(session, sourceDir, ShimFiles, destDir, "Revit first-launch");
                else
                    session.Log($"Transom #105: no '{ShimFiles[0]}' under '{Path.Combine(installDir, "<year>", "Transom")}' for any Revit year; skipping the Revit shims (Revit first-launch will self-heal).");
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
    /// <paramref name="destDir"/>. Never throws; a missing file, or one that couldn't be replaced, is logged and
    /// skipped.</summary>
    private static void CopyAll(Session session, string sourceDir, string[] names, string destDir, string fallback)
    {
        Directory.CreateDirectory(destDir);
        int copied = 0;
        foreach (var name in names)
        {
            var src = Path.Combine(sourceDir, name);
            var dst = Path.Combine(destDir, name);
            if (!File.Exists(src))
            {
                session.Log($"Transom #105: bundled shim '{name}' not found at '{src}'; skipping it.");
                continue;
            }

            // The MCP servers in this trio are RUNNING whenever Claude Code is open, and Windows won't overwrite a
            // running exe — a plain File.Copy left them stale through every install. DeployFile (the add-in's own
            // helper) renames the running exe aside and copies into the freed path; the running server keeps its
            // old image until it exits, and the client's next launch gets the new one.
            if (!DeployFile.CopyIfNewer(src, dst))
                session.Log($"Transom #105: couldn't replace '{dst}' (locked and couldn't be renamed aside). {fallback} copy will self-heal.");
            // Count only what is verifiably on disk: a copy once reported success here while the file stayed stale.
            else if (!SameContent(src, dst))
                session.Log($"Transom #105: '{dst}' doesn't verifiably match '{src}' after the refresh. {fallback} copy will self-heal.");
            else
                copied++;
        }
        session.Log($"Transom #105: refreshed {copied}/{names.Length} shim file(s) from '{sourceDir}' into '{destDir}'.");
    }

    /// <summary>True when both files hold the same bytes. Reads with full sharing, so a running exe can be read.</summary>
    private static bool SameContent(string a, string b)
    {
        try
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            return Hash(a).AsSpan().SequenceEqual(Hash(b));
        }
        catch { return false; }

        static byte[] Hash(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return SHA256.HashData(stream);
        }
    }

    /// <summary>
    ///     The <c>&lt;year&gt;\Transom</c> folder under <paramref name="installDir"/> that holds the shim, newest Revit
    ///     year first, or null. Only the years the user ticked have files on disk, and each year carries the same
    ///     trio, so the first match is as good as any. Never throws.
    /// </summary>
    private static string? FindRevitShimDir(string installDir)
    {
        try
        {
            return Directory.GetDirectories(installDir)
                .Select(dir => (dir, name: Path.GetFileName(dir)))
                .Where(d => d.name.Length == 4 && int.TryParse(d.name, out _))
                .OrderByDescending(d => int.Parse(d.name))
                .Select(d => Path.Combine(d.dir, "Transom"))
                .FirstOrDefault(ShimPresentIn);
        }
        catch { return null; }
    }

    private static bool ShimPresentIn(string dir)
    {
        try { return File.Exists(Path.Combine(dir, ShimFiles[0])); }
        catch { return false; }
    }
}
