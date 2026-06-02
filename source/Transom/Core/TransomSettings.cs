using System;
using System.IO;
using System.Text.Json;

namespace Transom.Core;

/// <summary>Persisted add-in settings (bridge port + Claude exchange folder), stored under %AppData%\Transom.</summary>
public sealed class TransomSettings
{
    public int BridgePort { get; set; } = 48884;

    /// <summary>Port for Transom's own in-process Claude-assist bridge (loopback TcpListener). High port,
    /// user-configurable; kept distinct from <see cref="BridgePort"/> (the external community bridge probe).</summary>
    public int BridgeSelfHostPort { get; set; } = 48810;

    public string ExchangeFolder { get; set; } = "";

    /// <summary>Occasionally show a cheerful message after an action. On by default — toggle in Settings.</summary>
    public bool EncouragingMessages { get; set; } = true;

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
