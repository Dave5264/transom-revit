using System;
using System.IO;
using System.Text.Json;

namespace Transom.Core;

/// <summary>Persisted add-in settings (bridge port + Claude exchange folder), stored under %AppData%\Transom.</summary>
public sealed class TransomSettings
{
    /// <summary>Port for Transom's own in-process Claude-assist bridge (loopback TcpListener). High port,
    /// user-configurable. This is the ONLY bridge port — the retired external-pyRevit probe port (48884) was
    /// removed (G1): Transom never listened on it, so probing it gave false offline/false positive readings.</summary>
    public int BridgeSelfHostPort { get; set; } = 48810;

    public string ExchangeFolder { get; set; } = "";

    /// <summary>Occasionally show a cheerful message after an action. On by default — toggle in Settings.</summary>
    public bool EncouragingMessages { get; set; } = true;

    /// <summary>The bridge port the MCP shim was last auto-registered with (0 = never). Drives one-time
    /// first-run registration of the bundled shim with Claude, and re-registration when the port changes.</summary>
    public int McpRegisteredPort { get; set; }

    /// <summary>Master Claude-Assist switch (Settings toggle). While true the bridge auto-starts with Revit,
    /// exports stage to the exchange folder, and grouped built-in edits may route to the staged Claude path.
    /// Replaces the old unpersisted Off/Verify/Assist "Claude mode" (true = the old Assist).</summary>
    public bool ClaudeAssistEnabled { get; set; }

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Transom", "settings.json");

    public static TransomSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<TransomSettings>(File.ReadAllText(FilePath)) ?? new TransomSettings();
        }
        catch { /* fall through to defaults */ }
        return new TransomSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are best-effort */ }
    }
}
