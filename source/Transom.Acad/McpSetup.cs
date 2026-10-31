using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Transom.Core;

namespace Transom.Acad;

/// <summary>
///     Installs the bundled <c>Transom.Acad.McpShim.exe</c> to the per-user MCP folder shared with the Revit
///     helpers (<c>%LocalAppData%\Transom\mcp\</c>) and merges a single <c>mcpServers.transom-acad</c> entry
///     into the user's Claude clients. Runs on every AutoCAD startup, off the main thread: idempotent (writes
///     nothing when the entry already matches), non-clobbering (only ever touches its own key), atomic, verified,
///     and fail-safe (a config that doesn't parse is left untouched). Mirrors the Revit add-in's
///     ClickHelperRegistration, whose merge logic has shipped since v1.4.
///     <para>
///     The installer's per-user custom action (install/ShimRefresh.cs) also copies the shim at install time, so
///     Claude gets the current build before AutoCAD is ever opened. This startup copy is the self-heal fallback,
///     and the only path for the per-machine MSI, which can't write each user's profile.
///     </para>
/// </summary>
internal static class McpSetup
{
    public const string ServerKey = "transom-acad";
    private const string ShimName = "Transom.Acad.McpShim.exe";

    private static string McpDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transom", "mcp");

    /// <summary>Installed shim: the command Claude launches.</summary>
    public static string ShimPath => Path.Combine(McpDir, ShimName);

    /// <summary>The shim as shipped in the bundle's Contents folder, beside Transom.Acad.dll.</summary>
    private static string BundledShimPath =>
        Path.Combine(Path.GetDirectoryName(typeof(McpSetup).Assembly.Location) ?? AppContext.BaseDirectory, ShimName);

    /// <summary>Never throws: startup must not fail over Claude registration.</summary>
    public static void EnsureShimAndRegister(int port)
    {
        try
        {
            // DeployFile renames a RUNNING shim aside before copying: while Claude Code is open the exe is locked,
            // and a plain overwrite would silently leave the user on the previous build.
            bool deployed = DeployFile.CopyIfNewer(BundledShimPath, ShimPath);
            if (!File.Exists(ShimPath))
            {
                Log.Write($"MCP shim not installed: bundled copy {(File.Exists(BundledShimPath) ? "could not be copied" : "missing")} ({BundledShimPath}).");
                return;
            }
            if (!deployed) Log.Write($"MCP shim at {ShimPath} could not be refreshed from the bundle; using the existing copy.");

            foreach (var line in Register(port)) Log.Write("MCP registration: " + line);
        }
        catch (Exception ex)
        {
            Log.Write("MCP setup failed (non-fatal): " + ex.Message);
        }
    }

    private static IEnumerable<(string label, string path)> Targets()
    {
        yield return ("Claude Desktop", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json"));
        yield return ("Claude Code", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"));
    }

    /// <summary>Merges the <c>transom-acad</c> entry into each installed Claude client config. Returns one
    /// human-readable line per client.</summary>
    private static List<string> Register(int port)
    {
        var messages = new List<string>();
        var desired = new JsonObject
        {
            // Explicit stdio transport so Claude Code unambiguously loads the server (the documented shape).
            ["type"] = "stdio",
            ["command"] = ShimPath,
            ["args"] = new JsonArray("--port", port.ToString()),
        };
        var desiredJson = desired.ToJsonString();

        foreach (var (label, path) in Targets())
        {
            try
            {
                var parent = Path.GetDirectoryName(path)!;
                if (!Directory.Exists(parent)) { messages.Add($"{label}: not installed, skipped."); continue; }

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
                        catch { messages.Add($"{label}: config isn't valid JSON, left untouched."); continue; }
                        if (parsed is not JsonObject obj) { messages.Add($"{label}: unexpected config shape, left untouched."); continue; }
                        root = obj;
                    }
                }

                if (root["mcpServers"] is not JsonObject servers) { servers = new JsonObject(); root["mcpServers"] = servers; }

                if (servers[ServerKey] is { } existing && existing.ToJsonString() == desiredJson)
                { messages.Add($"{label}: already registered."); continue; }

                servers[ServerKey] = desired.DeepClone(); // only ever touch our single key

                // Same backup name as the Revit add-in's registrations: one pre-Transom snapshot, taken once.
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

                messages.Add(verified ? $"{label}: registered." : $"{label}: write did not verify, config left unchanged.");
            }
            catch (Exception ex) { messages.Add($"{label}: failed: {ex.Message}"); }
        }
        return messages;
    }
}
