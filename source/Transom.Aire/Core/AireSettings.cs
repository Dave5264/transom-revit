using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Transom.Core;

/// <summary>One named entry in the reusable prompt library, stored in aire.json.</summary>
public sealed class AireSavedPrompt
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>
///     One named OpenAI account key. DPAPI-protected exactly like <see cref="AireSettings.ProtectedApiKey"/> —
///     no plaintext at rest, and a blob written under a different Windows profile simply fails to decrypt and
///     is reported as absent rather than throwing.
/// </summary>
public sealed class AireSavedApiKey
{
    public string Name { get; set; } = "";
    public string ProtectedKey { get; set; } = "";
}

/// <summary>
///     Persisted AIRE (AI Render Enhancer) preferences, stored under %AppData%\Transom\aire.json —
///     separate from settings.json so the two features can't torn-write each other. The OpenAI API key
///     is DPAPI-encrypted per user (CryptProtectData, no plaintext at rest); everything else is the
///     last-used UI state, which doubles as the defaults for the bridge's aire_enhance tool.
/// </summary>
public sealed class AireSettings
{
    /// <summary>DPAPI-encrypted OpenAI API key, base64. Empty = no key saved. Never store plaintext here.</summary>
    public string ProtectedApiKey { get; set; } = "";

    public string InputFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Model { get; set; } = AireEngine.DefaultModel;
    public string Size { get; set; } = AireEngine.DefaultSize;
    public string Quality { get; set; } = AireEngine.DefaultQuality;
    public string Theme { get; set; } = "Light";

    /// <summary>The reusable prompt library shown in the window's "Saved prompts" dropdown.</summary>
    public List<AireSavedPrompt> SavedPrompts { get; set; } = new();

    /// <summary>Named keys for multiple OpenAI accounts. The one currently in use is still
    /// <see cref="ProtectedApiKey"/> — that is what the bridge reads, so switching accounts in the
    /// window rewrites it rather than making the bridge understand named keys.</summary>
    public List<AireSavedApiKey> SavedApiKeys { get; set; } = new();

    /// <summary>Last dropdown selections, so reopening the window comes back where it was left.</summary>
    public string SelectedPromptName { get; set; } = "";
    public string SelectedApiKeyName { get; set; } = "";

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Transom", "aire.json");

    public static AireSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AireSettings>(File.ReadAllText(FilePath)) ?? new AireSettings();
        }
        catch { /* fall through to defaults */ }
        return new AireSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Same write-to-temp + rename as TransomSettings: a torn aire.json must not eat the saved key.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* settings are best-effort */ }
    }

    /// <summary>Decrypts and returns the saved API key, or "" when none is saved or decryption fails
    /// (different user profile / corrupted blob — treated as "no key", never as an exception).</summary>
    public string GetApiKey() => Unprotect(ProtectedApiKey);

    /// <summary>Encrypts and stores the API key (empty clears it). Call <see cref="Save"/> to persist.</summary>
    public void SetApiKey(string apiKey) => ProtectedApiKey = Protect(apiKey);

    // ---- saved prompts -------------------------------------------------------

    /// <summary>The saved prompt with this name, or null. Names are matched case-insensitively so
    /// "Exterior" and "exterior" can't become two entries the user reads as one.</summary>
    public AireSavedPrompt? FindPrompt(string name) =>
        SavedPrompts.FirstOrDefault(p => string.Equals(p.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds or replaces a named prompt and keeps the library alphabetical. Call <see cref="Save"/>.</summary>
    public void UpsertPrompt(string name, string text)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;
        var existing = FindPrompt(name);
        if (existing != null) { existing.Text = text ?? ""; }
        else SavedPrompts.Add(new AireSavedPrompt { Name = name, Text = text ?? "" });
        SortLibraries();
    }

    public void RemovePrompt(string name)
    {
        var existing = FindPrompt(name);
        if (existing != null) SavedPrompts.Remove(existing);
    }

    // ---- saved API keys ------------------------------------------------------

    public AireSavedApiKey? FindApiKey(string name) =>
        SavedApiKeys.FirstOrDefault(k => string.Equals(k.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Decrypts the named key, or "" when it is unknown or was protected by another user profile.</summary>
    public string GetSavedApiKey(string name) => Unprotect(FindApiKey(name)?.ProtectedKey ?? "");

    /// <summary>Adds or replaces a named key (encrypting it here — the caller only ever holds plaintext).
    /// A blank key is refused rather than saved as an empty account. Call <see cref="Save"/>.</summary>
    public void UpsertApiKey(string name, string apiKey)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || string.IsNullOrWhiteSpace(apiKey)) return;
        var blob = Protect(apiKey);
        var existing = FindApiKey(name);
        if (existing != null) existing.ProtectedKey = blob;
        else SavedApiKeys.Add(new AireSavedApiKey { Name = name, ProtectedKey = blob });
        SortLibraries();
    }

    public void RemoveApiKey(string name)
    {
        var existing = FindApiKey(name);
        if (existing != null) SavedApiKeys.Remove(existing);
    }

    private void SortLibraries()
    {
        SavedPrompts = SavedPrompts.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        SavedApiKeys = SavedApiKeys.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Protect(string plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return "";
        var blob = Dpapi.Protect(Encoding.UTF8.GetBytes(plain.Trim()));
        return blob == null ? "" : Convert.ToBase64String(blob);
    }

    private static string Unprotect(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return "";
        try
        {
            var plain = Dpapi.Unprotect(Convert.FromBase64String(base64));
            return plain == null ? "" : Encoding.UTF8.GetString(plain);
        }
        catch { return ""; }
    }

    /// <summary>
    ///     Minimal DPAPI (CryptProtectData/CryptUnprotectData, current-user scope) via P/Invoke, so we don't
    ///     add the System.Security.Cryptography.ProtectedData package to the Revit-shared dependency closure
    ///     for two calls. UI_FORBIDDEN: never let crypt32 pop a dialog inside Revit.
    /// </summary>
    private static class Dpapi
    {
        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public static byte[]? Protect(byte[] plain) => Transform(plain, protect: true);
        public static byte[]? Unprotect(byte[] cipher) => Transform(cipher, protect: false);

        private static byte[]? Transform(byte[] input, bool protect)
        {
            var inBlob = new DATA_BLOB { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
            try
            {
                Marshal.Copy(input, 0, inBlob.pbData, input.Length);
                bool ok = protect
                    ? CryptProtectData(ref inBlob, "Transom AIRE API key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, out var outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
                if (!ok) return null;
                try
                {
                    var result = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                    return result;
                }
                finally { LocalFree(outBlob.pbData); }
            }
            catch { return null; }
            finally { Marshal.FreeHGlobal(inBlob.pbData); }
        }
    }
}
