using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Transom.Core;

/// <summary>One named entry in a reusable prompt library, stored in aire.json.</summary>
public sealed class AireSavedPrompt
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>
///     One named account credential. DPAPI-protected exactly like <see cref="AireSettings.ProtectedApiKey"/> —
///     no plaintext at rest, and a blob written under a different Windows profile simply fails to decrypt and
///     is reported as absent rather than throwing.
///     <para>
///     Two providers share this shape. OpenAI keys are a single string; Higgsfield credentials are a PAIR
///     (key id + secret), so an entry carries an optional second protected field. <see cref="Provider"/>
///     keeps the two libraries apart — files written before the video tab existed have no Provider and
///     deserialize as OpenAI, which is what they were.
///     </para>
/// </summary>
public sealed class AireSavedApiKey
{
    public string Name { get; set; } = "";
    public string ProtectedKey { get; set; } = "";
    public string Provider { get; set; } = AireSettings.ProviderOpenAi;
    /// <summary>DPAPI-protected second half of a key pair (Higgsfield's secret). Empty for single-string keys.</summary>
    public string ProtectedSecret { get; set; } = "";
}

/// <summary>
///     Persisted AIRE (AI Render Enhancer) preferences, stored under %AppData%\Transom\aire.json —
///     separate from settings.json so the two features can't torn-write each other. Every credential is
///     DPAPI-encrypted per user (CryptProtectData, no plaintext at rest); everything else is the
///     last-used UI state, which doubles as the defaults for the bridge's aire_enhance tool.
/// </summary>
public sealed class AireSettings
{
    public const string ProviderOpenAi = "openai";
    public const string ProviderHiggsfield = "higgsfield";

    /// <summary>DPAPI-encrypted OpenAI API key, base64. Empty = no key saved. Never store plaintext here.</summary>
    public string ProtectedApiKey { get; set; } = "";

    public string InputFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Model { get; set; } = AireEngine.DefaultModel;
    public string Size { get; set; } = AireEngine.DefaultSize;
    public string Quality { get; set; } = AireEngine.DefaultQuality;
    public string Theme { get; set; } = "Light";

    /// <summary>The reusable prompt library shown in the Enhance tab's "Saved prompts" dropdown.</summary>
    public List<AireSavedPrompt> SavedPrompts { get; set; } = new();

    /// <summary>Named keys for multiple accounts, both providers. The OpenAI key currently in use is still
    /// <see cref="ProtectedApiKey"/> — that is what the bridge reads, so switching accounts in the
    /// window rewrites it rather than making the bridge understand named keys.</summary>
    public List<AireSavedApiKey> SavedApiKeys { get; set; } = new();

    /// <summary>Last dropdown selections, so reopening the window comes back where it was left.</summary>
    public string SelectedPromptName { get; set; } = "";
    public string SelectedApiKeyName { get; set; } = "";

    // ---- Video tab (Higgsfield) ---------------------------------------------

    /// <summary>The Higgsfield credential pair currently in use, DPAPI-protected. Both empty = none saved.</summary>
    public string ProtectedVideoKeyId { get; set; } = "";
    public string ProtectedVideoKeySecret { get; set; } = "";

    public string VideoOutputFolder { get; set; } = "";
    public string VideoPrompt { get; set; } = "";
    public string VideoSourceImage { get; set; } = "";
    /// <summary>Catalog path of the last model, e.g. "/higgsfield-ai/dop/standard". Empty = catalog default.</summary>
    public string VideoModel { get; set; } = "";
    /// <summary>Raw API values of the last per-model choices ("5", "1080", "16:9"); each is re-validated against
    /// the selected model's allowed values on load, so a value the model does not offer is simply dropped.</summary>
    public string VideoDuration { get; set; } = "";
    public string VideoResolution { get; set; } = "";
    public string VideoAspectRatio { get; set; } = "";
    /// <summary>Motion preset ids (Higgsfield UUIDs) and 0–1 strengths for the two camera slots.</summary>
    public string VideoMotion1 { get; set; } = "";
    public string VideoMotion2 { get; set; } = "";
    public double VideoMotion1Strength { get; set; } = 0.6;
    public double VideoMotion2Strength { get; set; } = 0.6;

    /// <summary>The Video tab's own prompt library — a motion prompt is not an enhancement prompt.</summary>
    public List<AireSavedPrompt> SavedVideoPrompts { get; set; } = new();
    public string SelectedVideoPromptName { get; set; } = "";
    public string SelectedVideoKeyName { get; set; } = "";

    /// <summary>"Enhance" or "Video" — which tab the window reopens on.</summary>
    public string ActiveTab { get; set; } = "Enhance";

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

    /// <summary>Decrypts and returns the saved OpenAI key, or "" when none is saved or decryption fails
    /// (different user profile / corrupted blob — treated as "no key", never as an exception).</summary>
    public string GetApiKey() => Unprotect(ProtectedApiKey);

    /// <summary>Encrypts and stores the OpenAI key (empty clears it). Call <see cref="Save"/> to persist.</summary>
    public void SetApiKey(string apiKey) => ProtectedApiKey = Protect(apiKey);

    /// <summary>The Higgsfield pair in use, decrypted; either half "" when unsaved or unreadable.</summary>
    public HiggsfieldCredentials GetVideoCredentials() =>
        new(Unprotect(ProtectedVideoKeyId), Unprotect(ProtectedVideoKeySecret));

    /// <summary>Encrypts and stores the Higgsfield pair (both empty clears it). Call <see cref="Save"/>.</summary>
    public void SetVideoCredentials(string keyId, string secret)
    {
        ProtectedVideoKeyId = Protect(keyId);
        ProtectedVideoKeySecret = Protect(secret);
    }

    // ---- saved prompts -------------------------------------------------------

    /// <summary>The saved enhance prompt with this name, or null. Names are matched case-insensitively so
    /// "Exterior" and "exterior" can't become two entries the user reads as one.</summary>
    public AireSavedPrompt? FindPrompt(string name) => FindIn(SavedPrompts, name);

    /// <summary>Adds or replaces a named enhance prompt and keeps the library alphabetical. Call <see cref="Save"/>.</summary>
    public void UpsertPrompt(string name, string text) => UpsertIn(SavedPrompts, name, text);

    public void RemovePrompt(string name) => RemoveFrom(SavedPrompts, name);

    public AireSavedPrompt? FindVideoPrompt(string name) => FindIn(SavedVideoPrompts, name);
    public void UpsertVideoPrompt(string name, string text) => UpsertIn(SavedVideoPrompts, name, text);
    public void RemoveVideoPrompt(string name) => RemoveFrom(SavedVideoPrompts, name);

    private static AireSavedPrompt? FindIn(List<AireSavedPrompt> library, string name) =>
        library.FirstOrDefault(p => string.Equals(p.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    private static void UpsertIn(List<AireSavedPrompt> library, string name, string text)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;
        var existing = FindIn(library, name);
        if (existing != null) existing.Text = text ?? "";
        else library.Add(new AireSavedPrompt { Name = name, Text = text ?? "" });
        library.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
    }

    private static void RemoveFrom(List<AireSavedPrompt> library, string name)
    {
        var existing = FindIn(library, name);
        if (existing != null) library.Remove(existing);
    }

    // ---- saved API keys ------------------------------------------------------

    /// <summary>The saved keys for one provider, alphabetical — each tab's dropdown shows only its own.</summary>
    public List<AireSavedApiKey> SavedApiKeysFor(string provider) =>
        SavedApiKeys.Where(k => IsProvider(k, provider)).ToList();

    public AireSavedApiKey? FindApiKey(string name, string provider = ProviderOpenAi) =>
        SavedApiKeys.FirstOrDefault(k => IsProvider(k, provider)
                                         && string.Equals(k.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool IsProvider(AireSavedApiKey key, string provider) =>
        string.Equals(string.IsNullOrEmpty(key.Provider) ? ProviderOpenAi : key.Provider, provider,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Decrypts the named OpenAI key, or "" when it is unknown or was protected by another user profile.</summary>
    public string GetSavedApiKey(string name) => Unprotect(FindApiKey(name)?.ProtectedKey ?? "");

    /// <summary>Decrypts the named Higgsfield pair; either half "" when unknown or unreadable.</summary>
    public HiggsfieldCredentials GetSavedKeyPair(string name)
    {
        var entry = FindApiKey(name, ProviderHiggsfield);
        return new HiggsfieldCredentials(Unprotect(entry?.ProtectedKey ?? ""), Unprotect(entry?.ProtectedSecret ?? ""));
    }

    /// <summary>Adds or replaces a named OpenAI key (encrypting it here — the caller only ever holds plaintext).
    /// A blank key is refused rather than saved as an empty account. Call <see cref="Save"/>.</summary>
    public void UpsertApiKey(string name, string apiKey) => UpsertCredential(name, ProviderOpenAi, apiKey, "");

    /// <summary>Adds or replaces a named Higgsfield pair. Both halves must be present. Call <see cref="Save"/>.</summary>
    public void UpsertKeyPair(string name, string keyId, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return;
        UpsertCredential(name, ProviderHiggsfield, keyId, secret);
    }

    private void UpsertCredential(string name, string provider, string key, string secret)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || string.IsNullOrWhiteSpace(key)) return;
        var existing = FindApiKey(name, provider);
        if (existing == null)
        {
            existing = new AireSavedApiKey { Name = name, Provider = provider };
            SavedApiKeys.Add(existing);
        }
        existing.ProtectedKey = Protect(key);
        existing.ProtectedSecret = Protect(secret);
        SavedApiKeys = SavedApiKeys.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void RemoveApiKey(string name, string provider = ProviderOpenAi)
    {
        var existing = FindApiKey(name, provider);
        if (existing != null) SavedApiKeys.Remove(existing);
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
