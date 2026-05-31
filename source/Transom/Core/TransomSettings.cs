using System;
using System.IO;
using System.Text.Json;

namespace Transom.Core;

/// <summary>Persisted add-in settings (bridge port + Claude exchange folder), stored under %AppData%\Transom.</summary>
public sealed class TransomSettings
{
    public int BridgePort { get; set; } = 48884;
    public string ExchangeFolder { get; set; } = "";

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
