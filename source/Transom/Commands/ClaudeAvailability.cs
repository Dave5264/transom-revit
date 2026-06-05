using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Transom.Commands;

/// <summary>
///     Detects whether the Claude desktop app is running, so the Claude ribbon buttons can grey out when it isn't.
///     Revit re-evaluates command availability on UI context changes (selection / document / idle), so the buttons
///     update shortly after Claude starts or stops — not necessarily the instant it happens.
/// </summary>
public static class ClaudeDetector
{
    private static bool _last;
    private static DateTime _checkedUtc = DateTime.MinValue;

    /// <summary>True when a Claude desktop process is running. Cached ~1.5s because Revit polls availability constantly.</summary>
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

/// <summary>Greys a Claude ribbon button when the Claude app isn't running (its tooltip says so). Instantiated by Revit.</summary>
public class ClaudeAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => ClaudeDetector.IsRunning();
}
