using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Transom.Core;

/// <summary>
///     Optional Claude UI-Assist feature. Installs the two bundled, self-contained executables —
///     <c>Transom.ClickHelper.Mcp.exe</c> (the stdio MCP server Claude talks to) and
///     <c>Transom.ClickHelper.exe</c> (the out-of-process UI-automation engine it drives) — to the
///     per-user MCP folder, and merges a single <c>mcpServers.transom-ui-assist</c> entry into the
///     user's Claude clients so Claude gains tools for the Revit commands that have NO API (Edit Group,
///     Finish, dialog handling, Properties-cell editing).
///
///     Deliberately separate from <see cref="McpRegistration"/> (the data bridge) and NEVER run on
///     startup — it is invoked only when the user clicks the UI-Assist button, so Transom keeps working
///     as a standalone product for users who don't have Claude. Admin-free (everything under the user
///     profile), idempotent, non-clobbering, atomic, fail-safe (a config that doesn't parse is left
///     untouched rather than overwritten). Mirrors McpRegistration's proven merge logic.
/// </summary>
public static class ClickHelperRegistration
{
    /// <summary>MCP server key written into the user's Claude config(s).</summary>
    public const string ServerKey = "transom-ui-assist";

    public sealed class Result
    {
        public int Updated;
        public int Skipped;
        public int Errors;
        public readonly List<string> Messages = new();
    }

    // --- per-user install locations (shared with the shim: %LocalAppData%\Transom\mcp\) ---

    private static string McpDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transom", "mcp");

    /// <summary>Installed MCP server exe (the command Claude launches).</summary>
    public static string McpServerPath => Path.Combine(McpDir, "Transom.ClickHelper.Mcp.exe");

    /// <summary>Installed UI-automation engine exe (the server passes this via --exe).</summary>
    public static string EnginePath => Path.Combine(McpDir, "Transom.ClickHelper.exe");

    // --- bundled sources (shipped next to Transom.dll in the add-in payload) ---

    private static string AddInDir
    {
        get
        {
            string dir;
            try
            {
                var loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
                dir = string.IsNullOrEmpty(loc) ? AppContext.BaseDirectory : Path.GetDirectoryName(loc) ?? AppContext.BaseDirectory;
            }
            catch { dir = AppContext.BaseDirectory; }
            return dir;
        }
    }

    private static string BundledMcpServerPath => Path.Combine(AddInDir, "Transom.ClickHelper.Mcp.exe");
    private static string BundledEnginePath => Path.Combine(AddInDir, "Transom.ClickHelper.exe");

    /// <summary>True when both installed executables are present at the per-user location.</summary>
    public static bool Installed() => FilePresent(McpServerPath) && FilePresent(EnginePath);

    /// <summary>
    ///     Copies both bundled executables to the per-user MCP folder when missing or out of date
    ///     (admin-free, guarded, repeatable). Returns true if both are present afterward.
    /// </summary>
    public static bool EnsureInstalled()
    {
        try
        {
            Directory.CreateDirectory(McpDir);
            CopyIfNewer(BundledMcpServerPath, McpServerPath);
            CopyIfNewer(BundledEnginePath, EnginePath);
            return Installed();
        }
        catch { return Installed(); }
    }

    /// <summary>Human-readable diagnostics for the setup dialog: every relevant path and whether it exists.</summary>
    public static string Diagnostics()
    {
        string Row(string label, string path) => $"{label,-22}{(FilePresent(path) ? "[found]   " : "[MISSING] ")}{path}";
        return string.Join("\n", new[]
        {
            "Add-in folder:        " + AddInDir,
            Row("Bundled MCP server: ", BundledMcpServerPath),
            Row("Bundled engine:     ", BundledEnginePath),
            Row("Installed MCP server:", McpServerPath),
            Row("Installed engine:   ", EnginePath),
        });
    }

    private static void CopyIfNewer(string src, string dest)
    {
        if (!File.Exists(src)) return; // nothing bundled to copy from; leave whatever is already installed
        bool needCopy;
        try
        {
            needCopy = !File.Exists(dest)
                       || new FileInfo(src).Length != new FileInfo(dest).Length
                       || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dest);
        }
        catch { needCopy = !File.Exists(dest); }
        if (needCopy) File.Copy(src, dest, true);
    }

    /// <summary>Directory-enumeration presence check (more reliable than File.Exists in Revit's add-in runtime).</summary>
    private static bool FilePresent(string path)
    {
        try
        {
            if (File.Exists(path)) return true;
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (dir == null || !Directory.Exists(dir)) return false;
            foreach (var f in Directory.EnumerateFiles(dir))
                if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch { return false; }
    }

    private static IEnumerable<(string label, string path)> Targets()
    {
        yield return ("Claude Desktop", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json"));
        yield return ("Claude Code", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"));
    }

    /// <summary>Merges the <c>transom-ui-assist</c> server entry into each installed Claude client config.</summary>
    public static Result Register()
    {
        var res = new Result();
        if (!Installed())
            res.Messages.Add("⚠ UI-Assist components not found at " + McpDir + " (install them first).");

        // command = the MCP server exe; it locates the engine via --exe (an absolute installed path).
        var desired = new JsonObject
        {
            ["command"] = McpServerPath,
            ["args"] = new JsonArray("--exe", EnginePath),
        };
        var desiredJson = desired.ToJsonString();

        foreach (var (label, path) in Targets())
        {
            try
            {
                var parent = Path.GetDirectoryName(path)!;
                if (!Directory.Exists(parent)) { res.Messages.Add($"{label}: not installed — skipped."); res.Skipped++; continue; }

                JsonObject root;
                if (!File.Exists(path)) { root = new JsonObject(); }
                else
                {
                    var text = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(text)) root = new JsonObject();
                    else
                    {
                        JsonNode? parsed;
                        try { parsed = JsonNode.Parse(text); }
                        catch { res.Messages.Add($"{label}: config isn't valid JSON — left untouched."); res.Errors++; continue; }
                        if (parsed is not JsonObject obj) { res.Messages.Add($"{label}: unexpected config shape — left untouched."); res.Errors++; continue; }
                        root = obj;
                    }
                }

                if (root["mcpServers"] is not JsonObject servers) { servers = new JsonObject(); root["mcpServers"] = servers; }

                if (servers[ServerKey] is { } existing && existing.ToJsonString() == desiredJson)
                { res.Messages.Add($"{label}: already registered — no change."); res.Skipped++; continue; }

                servers[ServerKey] = desired.DeepClone(); // only ever touch our single key

                var bak = path + ".transom.bak";
                if (File.Exists(path) && !File.Exists(bak)) { try { File.Copy(path, bak); } catch { /* best-effort */ } }
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                try { if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); }
                catch { File.Copy(tmp, path, true); try { File.Delete(tmp); } catch { /* ignore */ } }

                bool verified = false;
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject chk
                        && chk["mcpServers"] is JsonObject chkServers
                        && chkServers[ServerKey] is { } got && got.ToJsonString() == desiredJson)
                        verified = true;
                }
                catch { /* fall through */ }

                if (verified) { res.Messages.Add($"{label}: registered ✓"); res.Updated++; }
                else { res.Messages.Add($"{label}: write did not verify — config left unchanged"); res.Errors++; }
            }
            catch (Exception ex) { res.Messages.Add($"{label}: failed — {ex.Message}"); res.Errors++; }
        }

        return res;
    }

    /// <summary>Removes the <c>transom-ui-assist</c> entry (only if it points at our server) from each config.</summary>
    public static Result Unregister()
    {
        var res = new Result();
        foreach (var (label, path) in Targets())
        {
            try
            {
                if (!File.Exists(path)) { res.Skipped++; continue; }
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root
                    || root["mcpServers"] is not JsonObject servers
                    || servers[ServerKey] is not JsonObject entry) { res.Skipped++; continue; }

                if (entry["command"]?.GetValue<string>() != McpServerPath) { res.Skipped++; continue; } // only ours

                servers.Remove(ServerKey);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Replace(tmp, path, null);
                res.Messages.Add($"{label}: unregistered ✓"); res.Updated++;
            }
            catch (Exception ex) { res.Messages.Add($"{label}: failed — {ex.Message}"); res.Errors++; }
        }
        return res;
    }
}
