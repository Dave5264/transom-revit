using System;

namespace Transom.Core;

/// <summary>
///     Detects whether the Claude desktop app is running — an informational row in the Settings status panel.
///     (It used to gate/grey the Claude ribbon buttons via IExternalCommandAvailability; those buttons are
///     retired, and setup/registration never actually required Claude to be running.)
/// </summary>
public static class ClaudeDetector
{
    private static bool _last;
    private static DateTime _checkedUtc = DateTime.MinValue;

    /// <summary>True when a Claude desktop process is running. Cached ~1.5s.</summary>
    public static bool IsRunning()
    {
        if ((DateTime.UtcNow - _checkedUtc).TotalMilliseconds < 1500) return _last;
        _checkedUtc = DateTime.UtcNow;
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("Claude"); // Claude Desktop runs as "Claude.exe"
            _last = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
        }
        catch { _last = false; }
        return _last;
    }
}
