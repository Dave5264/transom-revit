using System;
using System.IO;

namespace Transom.Core;

/// <summary>
///     Copies a bundled helper executable to its per-user location, even when the destination is a RUNNING
///     process.
///     <para>
///     Why this exists: both helper executables live in <c>%LocalAppData%\Transom\mcp\</c> and are launched
///     by the user's MCP client. <c>Transom.ClickHelper.Mcp.exe</c> and <c>Transom.McpShim.exe</c> are
///     long-running SERVER processes, so whenever Claude Code is open the destination file is locked and a
///     plain <see cref="File.Copy(string,string,bool)"/> throws <see cref="IOException"/>. Both deploy paths
///     used to swallow that in a catch, so after an upgrade the per-user copies silently stayed at the OLD
///     version for as long as the user kept Claude Code running — which is most of the time. The symptom is
///     nasty to diagnose: the tools work, they just behave like the previous build.
///     </para>
///     <para>
///     The fix is the standard self-updater trick: Windows refuses to OVERWRITE a running image but is happy
///     to RENAME it, because the running process keeps its open handle to the moved file. So rename the live
///     file aside and copy the new one into the freed path. The already-running server keeps using the old
///     image until it exits; the next launch picks up the new one.
///     </para>
/// </summary>
internal static class DeployFile
{
    private const string StaleSuffix = ".old";

    /// <summary>
    ///     Copies <paramref name="src"/> over <paramref name="dest"/> when it is missing or out of date.
    ///     Never throws. Returns true when <paramref name="dest"/> ends up matching <paramref name="src"/>.
    /// </summary>
    public static bool CopyIfNewer(string src, string dest)
    {
        try
        {
            if (!File.Exists(src)) return false;   // nothing bundled — leave whatever is installed
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            PurgeStale(dest);

            bool needCopy;
            try
            {
                needCopy = !File.Exists(dest)
                           || new FileInfo(src).Length != new FileInfo(dest).Length
                           || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dest);
            }
            catch { needCopy = !File.Exists(dest); }
            if (!needCopy) return true;

            try
            {
                File.Copy(src, dest, true);
                return true;
            }
            catch (IOException) { /* locked by a running server — rename it aside below */ }
            catch (UnauthorizedAccessException) { /* same, reported differently */ }

            // Rename-aside. If the MOVE fails there is nothing further to try: the destination is both
            // unwritable and unmovable, so report failure rather than pretending the refresh happened.
            var aside = dest + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + StaleSuffix;
            try
            {
                File.Move(dest, aside);
                File.Copy(src, dest, true);
                return true;
            }
            catch
            {
                // Put it back if the copy failed after a successful move, so we never leave NO file at dest.
                try { if (!File.Exists(dest) && File.Exists(aside)) File.Move(aside, dest); } catch { /* give up */ }
                return false;
            }
        }
        catch { return false; }
    }

    /// <summary>Deletes previously renamed-aside copies whose process has since exited. Best-effort: one that
    /// is still running simply stays locked and is retried on a later launch.</summary>
    private static void PurgeStale(string dest)
    {
        try
        {
            var dir = Path.GetDirectoryName(dest);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, Path.GetFileName(dest) + ".*" + StaleSuffix))
            {
                try { File.Delete(f); } catch { /* still running — leave it for next time */ }
            }
        }
        catch { /* purging is optional */ }
    }
}
