using System;

namespace Transom.Core;

/// <summary>
///     Detects whether a Claude client is running — an informational row in the Settings status panel.
///     Two signals, strongest first: (1) Transom's own MCP server processes (Transom.McpShim /
///     Transom.ClickHelper.Mcp) exist ONLY while a Claude session has the Transom tools loaded, so they see
///     Claude Code under any host the name check can't (node, VS Code, a terminal); (2) a process named
///     "Claude" (Claude Desktop, or Claude Code's native claude.exe). A red result can still be a false
///     negative — the "?" help next to the row covers that case.
/// </summary>
public static class ClaudeDetector
{
    private static bool _last;
    private static string _lastVia = "";
    private static DateTime _checkedUtc = DateTime.MinValue;

    /// <summary>Status-row detail for a positive detection; empty when nothing was detected.</summary>
    public static string DetectedVia { get { IsRunning(); return _lastVia; } }

    /// <summary>True when a Claude client (or a live Transom MCP session) is running. Cached ~1.5s.</summary>
    public static bool IsRunning()
    {
        if ((DateTime.UtcNow - _checkedUtc).TotalMilliseconds < 1500) return _last;
        _checkedUtc = DateTime.UtcNow;

        if (AnyProcess("Transom.McpShim") || AnyProcess("Transom.ClickHelper.Mcp"))
        {
            _last = true; _lastVia = "connected — Transom MCP servers are loaded";
        }
        else if (AnyProcess("Claude")) // Claude Desktop / Claude Code's native binary run as "claude.exe"
        {
            _last = true; _lastVia = "detected";
        }
        else
        {
            _last = false; _lastVia = "";
        }
        return _last;
    }

    private static bool AnyProcess(string name)
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName(name);
            var found = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return found;
        }
        catch { return false; }
    }
}
