using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Transom.Core;

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
    public string GetApiKey()
    {
        if (string.IsNullOrEmpty(ProtectedApiKey)) return "";
        try
        {
            var protectedBytes = Convert.FromBase64String(ProtectedApiKey);
            var plain = Dpapi.Unprotect(protectedBytes);
            return plain == null ? "" : Encoding.UTF8.GetString(plain);
        }
        catch { return ""; }
    }

    /// <summary>Encrypts and stores the API key (empty clears it). Call <see cref="Save"/> to persist.</summary>
    public void SetApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) { ProtectedApiKey = ""; return; }
        var blob = Dpapi.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()));
        ProtectedApiKey = blob == null ? "" : Convert.ToBase64String(blob);
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
