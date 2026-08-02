using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Transom.Core;

/// <summary>
///     Registers (and unregisters) the bundled, self-contained <c>Transom.McpShim.exe</c> with the user's
///     MCP clients by merging a single <c>mcpServers.transom</c> entry into their user-level config files.
///     Admin-free (everything lives under the user profile), idempotent, non-clobbering, atomic, and
///     fail-safe: a config that doesn't parse is left untouched rather than overwritten. See
///     the F4 config-merge design, Option A (note archived locally, not in this repo).
/// </summary>
public static class McpRegistration
{
    public sealed class Result
    {
        public int Updated;
        public int Skipped;
        public int Errors;
        public readonly List<string> Messages = new();
    }

    /// <summary>Per-user install location of the shim (matches install/BundledMcp.wxs).</summary>
    public static string ShimPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Transom", "mcp", "Transom.McpShim.exe");

    /// <summary>
    ///     Whether the shim file is present. Uses a directory enumeration (not just <c>File.Exists</c>), which
    ///     has proven more reliable than <c>File.Exists</c> in Revit's isolated add-in runtime — the latter was
    ///     returning false for a file that demonstrably exists, causing a spurious "shim not found" warning.
    /// </summary>
    public static bool ShimPresent()
    {
        try
        {
            if (File.Exists(ShimPath)) return true;
            var dir = Path.GetDirectoryName(ShimPath);
            var name = Path.GetFileName(ShimPath);
            if (dir == null || !Directory.Exists(dir)) return false;
            foreach (var f in Directory.EnumerateFiles(dir))
                if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>The shim as shipped inside the add-in payload — it is published next to <c>Transom.dll</c>
    /// (same folder as this assembly). This is the source for the first-run copy to <see cref="ShimPath"/>.</summary>
    public static string BundledShimPath
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
            return Path.Combine(dir, "Transom.McpShim.exe");
        }
    }

    /// <summary>Ensures the self-contained shim is present at the per-user location (<see cref="ShimPath"/>),
    /// copying it from the add-in payload when missing or out of date. Admin-free (LocalAppData, no elevation).
    /// Returns true if the shim is present afterward. Safe to call repeatedly — the copy is guarded.</summary>
    public static bool EnsureShimInstalled()
    {
        try
        {
            var dest = ShimPath;
            var src = BundledShimPath;
            if (!File.Exists(src)) return ShimPresent(); // nothing bundled to copy from; report current state

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            bool needCopy;
            try
            {
                needCopy = !File.Exists(dest)
                           || new FileInfo(src).Length != new FileInfo(dest).Length
                           || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dest);
            }
            catch { needCopy = !File.Exists(dest); }

            if (needCopy) File.Copy(src, dest, true);
            return ShimPresent();
        }
        catch { return ShimPresent(); }
    }

    /// <summary>First-run convenience (Option A): install the bundled shim, then register it with the user's
    /// Claude clients once — or again if the bridge port changed since the last registration. Idempotent and
    /// guarded so it can run on every add-in startup. Never throws.</summary>
    public static void EnsureBundledShimAndAutoRegister()
    {
        try
        {
            if (!EnsureShimInstalled()) return; // no shim -> nothing to register yet

            var settings = TransomSettings.Load();
            var port = settings.BridgeSelfHostPort;
            if (settings.McpRegisteredPort == port) return; // already registered with this port

            var res = Register(port);
            if (res.Updated > 0)
            {
                settings.McpRegisteredPort = port;
                settings.Save();
            }
        }
        catch { /* startup must never fail because of registration */ }
    }

    private static IEnumerable<(string label, string path)> Targets()
    {
        yield return ("Claude Desktop", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json"));
        yield return ("Claude Code", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"));
    }

    /// <summary>Merges the <c>transom</c> server entry into each installed MCP client's user config.</summary>
    public static Result Register(int port)
    {
        var res = new Result();
        var shim = ShimPath;
        if (!ShimPresent())
            res.Messages.Add("⚠ Shim not found at " + shim + " (install or publish it first).");

        var desired = new JsonObject
        {
            // Explicit stdio transport so Claude Code unambiguously loads the server (the documented shape).
            ["type"] = "stdio",
            ["command"] = shim,
            ["args"] = new JsonArray("--port", port.ToString()),
        };
        var desiredJson = desired.ToJsonString();

        foreach (var (label, path) in Targets())
        {
            try
            {
                var parent = Path.GetDirectoryName(path)!;
                if (!Directory.Exists(parent)) { res.Messages.Add($"{label}: not installed — skipped."); res.Skipped++; continue; }

                JsonObject root;
                if (!File.Exists(path))
                {
                    root = new JsonObject();
                }
                else
                {
                    var text = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(text)) { root = new JsonObject(); }
                    else
                    {
                        JsonNode? parsed;
                        try { parsed = JsonNode.Parse(text); }
                        catch { res.Messages.Add($"{label}: config isn't valid JSON — left untouched."); res.Errors++; continue; }
                        if (parsed is not JsonObject obj) { res.Messages.Add($"{label}: unexpected config shape — left untouched."); res.Errors++; continue; }
                        root = obj;
                    }
                }

                if (root["mcpServers"] is not JsonObject servers)
                {
                    servers = new JsonObject();
                    root["mcpServers"] = servers;
                }

                // Idempotent: if our entry already equals the desired value, write nothing.
                if (servers["transom"] is { } existing && existing.ToJsonString() == desiredJson)
                {
                    res.Messages.Add($"{label}: already registered — no change."); res.Skipped++; continue;
                }

                // Only ever touch our single key; every other server is round-tripped untouched.
                servers["transom"] = desired.DeepClone();

                // One-time backup, then atomic temp-write + replace (with a copy fallback if Replace can't,
                // e.g. cross-volume or a transient lock).
                var bak = path + ".transom.bak";
                if (File.Exists(path) && !File.Exists(bak)) { try { File.Copy(path, bak); } catch { /* best-effort */ } }
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                try { if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); }
                catch { File.Copy(tmp, path, true); try { File.Delete(tmp); } catch { /* ignore */ } }

                // Verify the write actually landed — don't claim success we can't confirm (the cause of a
                // misleading "registered ✓" when the file was locked/unwritable).
                bool verified = false;
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject chk
                        && chk["mcpServers"] is JsonObject chkServers
                        && chkServers["transom"] is { } got && got.ToJsonString() == desiredJson)
                        verified = true;
                }
                catch { /* fall through to failure */ }

                if (verified) { res.Messages.Add($"{label}: registered ✓"); res.Updated++; }
                else { res.Messages.Add($"{label}: write did not verify — config left unchanged"); res.Errors++; }
            }
            catch (Exception ex) { res.Messages.Add($"{label}: failed — {ex.Message}"); res.Errors++; }
        }

        return res;
    }

    /// <summary>Removes the <c>transom</c> entry (only if it points at our shim) from each client config.</summary>
    public static Result Unregister()
    {
        var res = new Result();
        var shim = ShimPath;
        foreach (var (label, path) in Targets())
        {
            try
            {
                if (!File.Exists(path)) { res.Skipped++; continue; }
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root
                    || root["mcpServers"] is not JsonObject servers
                    || servers["transom"] is not JsonObject entry) { res.Skipped++; continue; }

                // Only remove if it's ours (command points at our shim path).
                if (entry["command"]?.GetValue<string>() != shim) { res.Skipped++; continue; }

                servers.Remove("transom");
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
