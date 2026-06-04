using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NPOI.XWPF.UserModel;

namespace Transom.Core;

/// <summary>
///     Renders a <see cref="RevisionNarrative.Data"/> into a .docx.
///
///     <para><b>Start from a previous narrative (recommended).</b> The file is copied <b>byte-for-byte</b> and
///     ONLY the body of <c>word/document.xml</c> is replaced. Header (logo), footers, section setup, styles,
///     theme, numbering and media are the source's exact bytes. The new body uses the document's "No Spacing"
///     style AND the <b>dominant run font sniffed from the source body</b> (e.g. Arial Narrow), since the firm's
///     narratives apply the font as direct run formatting that overrides the theme default.</para>
///
///     <para>With no template, a plain Calibri-11 document is produced via NPOI.</para>
/// </summary>
public static class RevisionNarrativeDocxWriter
{
    private const string BodyStyle = "NoSpacing";
    private const int ClosingHalfPt = 24; // 12 pt closing line

    public static void Write(RevisionNarrative.Data data, string outputPath, string? templatePath = null)
    {
        if (!string.IsNullOrWhiteSpace(templatePath) && File.Exists(templatePath))
            WriteFromTemplate(data, outputPath, templatePath!);
        else
            WritePlain(data, outputPath);
    }

    // ---- surgical: copy the source, swap only document.xml's body ----
    private static void WriteFromTemplate(RevisionNarrative.Data data, string outputPath, string templatePath)
    {
        if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(templatePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose an output file different from the template.");

        File.Copy(templatePath, outputPath, overwrite: true);

        using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Update);
        var entry = zip.GetEntry("word/document.xml") ?? throw new IOException("Template has no word/document.xml.");

        string docXml;
        using (var r = new StreamReader(entry.Open(), Encoding.UTF8)) docXml = r.ReadToEnd();

        var font = SniffBodyFont(docXml); // match the source's actual body font (overrides theme default)

        int bodyTag = docXml.IndexOf("<w:body", StringComparison.Ordinal);
        int bodyOpenEnd = docXml.IndexOf('>', bodyTag) + 1;
        int sectStart = docXml.LastIndexOf("<w:sectPr", StringComparison.Ordinal);
        int tail = sectStart >= 0 ? sectStart : docXml.LastIndexOf("</w:body>", StringComparison.Ordinal);

        var newXml = docXml.Substring(0, bodyOpenEnd) + BuildBodyXml(data, font) + docXml.Substring(tail);

        entry.Delete();
        var fresh = zip.CreateEntry("word/document.xml");
        using var w = new StreamWriter(fresh.Open(), new UTF8Encoding(false));
        w.Write(newXml);
    }

    /// <summary>Most-frequent direct run font (<c>w:rFonts w:ascii</c>) in the source body, or null.</summary>
    private static string? SniffBodyFont(string docXml)
    {
        var counts = new Dictionary<string, int>();
        foreach (Match m in Regex.Matches(docXml, "<w:rFonts[^>]*?w:ascii=\"([^\"]+)\""))
        {
            var f = m.Groups[1].Value;
            counts[f] = counts.TryGetValue(f, out var c) ? c + 1 : 1;
        }
        return counts.Count == 0 ? null : counts.OrderByDescending(kv => kv.Value).First().Key;
    }

    private static string BuildBodyXml(RevisionNarrative.Data data, string? font)
    {
        var sb = new StringBuilder();
        void Blank() => sb.Append(Para("", false, 0, 0, font));
        void Line(string text, bool bold = false, int szHalfPt = 0, int indent = 0) => sb.Append(Para(text, bold, szHalfPt, indent, font));

        Line(data.IssueDate);
        Blank();
        Line(data.AddendumLabel, bold: true);
        Blank();
        Line(data.ProjectName);
        foreach (var addr in data.AddressLines) Line(addr);
        Line(data.ProjectNumberLine);
        Blank();
        Line(data.IntroSentence);
        Blank();
        Line("Drawing Revisions:", bold: true);
        Blank();

        foreach (var disc in data.Disciplines)
        {
            Line(disc.Name, bold: true);
            Blank();
            foreach (var sheet in disc.Sheets)
            {
                Line($"{sheet.Number} – {sheet.Name.ToUpperInvariant()}"); // en-dash separator
                foreach (var note in sheet.Notes)
                {
                    var label = string.IsNullOrEmpty(note.DetailNumber) ? "[insert]" : $"Detail {note.DetailNumber}";
                    Line($"{label} - {note.Text}", indent: 360);
                }
                Blank();
            }
        }

        Line($"End of {data.AddendumLabel}", bold: true, szHalfPt: ClosingHalfPt);
        return sb.ToString();
    }

    /// <summary>One WordprocessingML paragraph: "No Spacing" style + the sniffed run font, plus bold/size as needed.</summary>
    private static string Para(string text, bool bold, int szHalfPt, int indent, string? font)
    {
        var pPr = new StringBuilder($"<w:pPr><w:pStyle w:val=\"{BodyStyle}\"/>");
        if (indent > 0) pPr.Append($"<w:ind w:left=\"{indent}\"/>");
        pPr.Append("</w:pPr>");

        var run = "";
        if (text.Length > 0)
        {
            var rPr = new StringBuilder();
            if (!string.IsNullOrEmpty(font)) rPr.Append($"<w:rFonts w:ascii=\"{Esc(font!)}\" w:hAnsi=\"{Esc(font!)}\"/>");
            if (bold) rPr.Append("<w:b/><w:bCs/>");
            if (szHalfPt > 0) rPr.Append($"<w:sz w:val=\"{szHalfPt}\"/><w:szCs w:val=\"{szHalfPt}\"/>");
            var rPrXml = rPr.Length > 0 ? $"<w:rPr>{rPr}</w:rPr>" : "";
            run = $"<w:r>{rPrXml}<w:t xml:space=\"preserve\">{Esc(text)}</w:t></w:r>";
        }
        return $"<w:p>{pPr}{run}</w:p>";
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // ---- plain fallback (no template) ----
    private static void WritePlain(RevisionNarrative.Data data, string outputPath)
    {
        const string Font = "Calibri"; const int Body = 11; const int Closing = 12;
        var doc = new XWPFDocument();

        void Blank() { var p = doc.CreateParagraph(); p.SpacingAfter = 0; }
        void Line(string text, bool bold = false, int size = Body, int indent = 0)
        {
            var p = doc.CreateParagraph(); p.SpacingAfter = 0;
            if (indent > 0) p.IndentationLeft = indent;
            var r = p.CreateRun(); r.FontFamily = Font; r.FontSize = size; r.IsBold = bold; r.SetText(text ?? "");
        }

        Line(data.IssueDate); Blank();
        Line(data.AddendumLabel, bold: true); Blank();
        Line(data.ProjectName);
        foreach (var addr in data.AddressLines) Line(addr);
        Line(data.ProjectNumberLine); Blank();
        Line(data.IntroSentence); Blank();
        Line("Drawing Revisions:", bold: true); Blank();
        foreach (var disc in data.Disciplines)
        {
            Line(disc.Name, bold: true); Blank();
            foreach (var sheet in disc.Sheets)
            {
                Line($"{sheet.Number} – {sheet.Name.ToUpperInvariant()}");
                foreach (var note in sheet.Notes)
                {
                    var label = string.IsNullOrEmpty(note.DetailNumber) ? "[insert]" : $"Detail {note.DetailNumber}";
                    Line($"{label} - {note.Text}", indent: 360);
                }
                Blank();
            }
        }
        Line($"End of {data.AddendumLabel}", bold: true, size: Closing);

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        doc.Write(fs);
    }
}
