using System;
using System.IO;

namespace Transom.Core;

/// <summary>
///     Pixel dimensions straight out of an image file's header, without WPF.
///     <para>
///     AIRE used <c>System.Windows.Media.Imaging.BitmapDecoder</c> for this, which was fine while the engine
///     lived in a WPF assembly. It was also the LAST desktop dependency in AIRE's core, and the one thing
///     stopping the MCP shim — a plain net8.0 exe with no desktop runtime — from running AIRE headlessly.
///     A few dozen lines of header parsing buys Revit-free, window-free operation, so it is worth it.
///     </para>
///     <para>
///     Only the formats AIRE accepts are parsed (see <c>AireEngine.IsEnhanceableImage</c>): PNG, JPEG and
///     WebP, plus BMP and GIF because they cost three lines each. Anything else returns null, which callers
///     already treat as "unknown resolution" — the same contract the WIC decoder had for a file it could not
///     read. Dimensions feed a cost ESTIMATE and a display string; they are never used to transform pixels.
///     </para>
/// </summary>
internal static class ImageHeader
{
    /// <summary>Width/height from the file header, or null when the format is unknown or the file is short.</summary>
    public static (int Width, int Height)? TryReadSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(fs);

            // 32 bytes covers every fixed-offset format below; JPEG re-reads from the stream as it walks.
            var head = reader.ReadBytes(32);
            if (head.Length < 12) return null;

            if (IsPng(head)) return ReadPng(head);
            if (IsGif(head)) return (LeU16(head, 6), LeU16(head, 8));
            if (IsBmp(head)) return ReadBmp(head);
            if (IsWebp(head)) return ReadWebp(fs, reader, head);
            if (head[0] == 0xFF && head[1] == 0xD8) return ReadJpeg(fs, reader);
            return null;
        }
        catch
        {
            // Unreadable, locked, truncated — same answer the decoder gave: unknown.
            return null;
        }
    }

    // ---- PNG: 8-byte signature, then an IHDR chunk whose width/height are big-endian at 16 and 20 ----
    private static bool IsPng(byte[] h) =>
        h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47
        && h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A;

    private static (int, int)? ReadPng(byte[] h)
    {
        if (h.Length < 24) return null;
        if (h[12] != (byte)'I' || h[13] != (byte)'H' || h[14] != (byte)'D' || h[15] != (byte)'R') return null;
        return (BeI32(h, 16), BeI32(h, 20));
    }

    // ---- GIF: "GIF87a"/"GIF89a", logical screen size little-endian at 6 ----
    private static bool IsGif(byte[] h) =>
        h[0] == (byte)'G' && h[1] == (byte)'I' && h[2] == (byte)'F' && h[3] == (byte)'8';

    // ---- BMP: "BM", DIB header width/height little-endian at 18/22 ----
    private static bool IsBmp(byte[] h) => h[0] == (byte)'B' && h[1] == (byte)'M';

    private static (int, int)? ReadBmp(byte[] h)
    {
        if (h.Length < 26) return null;
        int w = LeI32(h, 18);
        // A negative height means a top-down bitmap; the magnitude is still the pixel count.
        int raw = LeI32(h, 22);
        int hgt = raw == int.MinValue ? 0 : Math.Abs(raw);
        return w > 0 && hgt > 0 ? (w, hgt) : null;
    }

    // ---- WebP: RIFF container, three possible chunks ----
    private static bool IsWebp(byte[] h) =>
        h[0] == (byte)'R' && h[1] == (byte)'I' && h[2] == (byte)'F' && h[3] == (byte)'F'
        && h[8] == (byte)'W' && h[9] == (byte)'E' && h[10] == (byte)'B' && h[11] == (byte)'P';

    private static (int, int)? ReadWebp(FileStream fs, BinaryReader reader, byte[] head)
    {
        // The first 32 bytes already hold the chunk fourcc (12) and enough payload for every variant.
        if (head.Length < 30) { fs.Position = 0; head = reader.ReadBytes(32); if (head.Length < 30) return null; }

        // "VP8X" — extended format; canvas size is (value + 1), 24-bit little-endian at 24 and 27.
        if (head[12] == 'V' && head[13] == 'P' && head[14] == '8' && head[15] == 'X')
            return (LeU24(head, 24) + 1, LeU24(head, 27) + 1);

        // "VP8L" — lossless; 14 bits of width-1 then 14 bits of height-1, packed after the 0x2F signature.
        if (head[12] == 'V' && head[13] == 'P' && head[14] == '8' && head[15] == 'L')
        {
            if (head[20] != 0x2F) return null;
            uint bits = (uint)(head[21] | (head[22] << 8) | (head[23] << 16) | (head[24] << 24));
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        // "VP8 " — lossy; a 3-byte frame tag, the 3-byte start code 9D 01 2A, then 14-bit width and height.
        if (head[12] == 'V' && head[13] == 'P' && head[14] == '8' && head[15] == ' ')
        {
            if (head[23] != 0x9D || head[24] != 0x01 || head[25] != 0x2A) return null;
            return (LeU16(head, 26) & 0x3FFF, LeU16(head, 28) & 0x3FFF);
        }

        return null;
    }

    // ---- JPEG: walk the marker segments to the first Start-Of-Frame ----
    private static (int, int)? ReadJpeg(FileStream fs, BinaryReader reader)
    {
        fs.Position = 2; // past SOI
        while (fs.Position < fs.Length - 1)
        {
            // Markers are 0xFF followed by a type byte; runs of 0xFF are legal padding between segments.
            if (reader.ReadByte() != 0xFF) continue;
            byte marker = reader.ReadByte();
            while (marker == 0xFF) marker = reader.ReadByte();

            // Standalone markers carry no length: SOI/EOI and the RSTn restart markers.
            if (marker == 0xD8 || marker == 0xD9 || (marker >= 0xD0 && marker <= 0xD7)) continue;

            int length = (reader.ReadByte() << 8) | reader.ReadByte();
            if (length < 2) return null;

            // SOF0-3, SOF5-7, SOF9-11, SOF13-15 all start: precision(1), height(2), width(2).
            // The gaps are DHT (C4), JPGA (C8) and DAC (CC), which are not frame headers.
            bool isSof = marker >= 0xC0 && marker <= 0xCF
                         && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isSof)
            {
                if (length < 7) return null;
                reader.ReadByte(); // sample precision
                int h = (reader.ReadByte() << 8) | reader.ReadByte();
                int w = (reader.ReadByte() << 8) | reader.ReadByte();
                return w > 0 && h > 0 ? (w, h) : null;
            }

            // Not a frame header — skip its payload and keep walking.
            fs.Position += length - 2;
        }
        return null;
    }

    // ---- byte helpers ----
    private static int BeI32(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    private static int LeI32(byte[] b, int i) => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);
    private static int LeU24(byte[] b, int i) => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16);
    private static int LeU16(byte[] b, int i) => b[i] | (b[i + 1] << 8);
}
