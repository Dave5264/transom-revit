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
///     only <c>word/document.xml</c> (body) and <c>word/numbering.xml</c> are edited. Header (logo), footers,
///     section setup, styles, theme and media are the source's exact bytes. The new body uses the document's
///     "No Spacing" style + the dominant run font sniffed from the source (e.g. Arial Narrow), and each
///     sheet's revision items are a <b>native Word numbered list that restarts at 1</b> (a per-sheet
///     <c>w:num</c> over the source's decimal list, referenced from each note's <c>w:numPr</c>).</para>
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

    // ---- surgical: copy the source, swap document.xml body + add per-sheet numbering ----
    private static void WriteFromTemplate(RevisionNarrative.Data data, string outputPath, string templatePath)
    {
        if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(templatePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose an output file different from the template.");

        File.Copy(templatePath, outputPath, overwrite: true);

        using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Update);
        var docEntry = zip.GetEntry("word/document.xml") ?? throw new IOException("Template has no word/document.xml.");

        string docXml;
        using (var r = new StreamReader(docEntry.Open(), Encoding.UTF8)) docXml = r.ReadToEnd();

        var numEntry = zip.GetEntry("word/numbering.xml");
        string? numXml = null;
        if (numEntry != null) using (var r = new StreamReader(numEntry.Open(), Encoding.UTF8)) numXml = r.ReadToEnd();

        var font = SniffBodyFont(docXml);

        // Set up native numbering: reuse a decimal list definition; allocate one restarting list per sheet.
        bool canNumber = numXml != null;
        string? absId = null;
        int startNumId = 1;
        string numBase = numXml ?? "";
        if (canNumber)
        {
            absId = FindDecimalAbstractNum(numXml!);
            startNumId = MaxNumId(numXml!) + 1;
            if (absId == null)
            {
                int newAbs = MaxAbstractNumId(numXml!) + 1;
                absId = newAbs.ToString();
                numBase = InsertAbstractNum(numXml!, newAbs);
            }
        }

        var numIds = new List<int>();
        var body = BuildBodyXml(data, font, canNumber, startNumId, numIds, absId);

        int bodyTag = docXml.IndexOf("<w:body", StringComparison.Ordinal);
        int bodyOpenEnd = docXml.IndexOf('>', bodyTag) + 1;
        int sectStart = docXml.LastIndexOf("<w:sectPr", StringComparison.Ordinal);
        int tail = sectStart >= 0 ? sectStart : docXml.LastIndexOf("</w:body>", StringComparison.Ordinal);
        var newDocXml = docXml.Substring(0, bodyOpenEnd) + body + docXml.Substring(tail);

        // Write numbering.xml first (add the per-sheet <w:num> entries), then document.xml.
        if (canNumber && numIds.Count > 0)
        {
            var additions = string.Concat(numIds.Select(id =>
                $"<w:num w:numId=\"{id}\"><w:abstractNumId w:val=\"{absId}\"/><w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"1\"/></w:lvlOverride></w:num>"));
            int ni = numBase.LastIndexOf("</w:numbering>", StringComparison.Ordinal);
            var newNum = numBase.Substring(0, ni) + additions + numBase.Substring(ni);
            numEntry!.Delete();
            var fn = zip.CreateEntry("word/numbering.xml");
            using (var nw = new StreamWriter(fn.Open(), new UTF8Encoding(false))) nw.Write(newNum);
        }

        docEntry.Delete();
        var fd = zip.CreateEntry("word/document.xml");
        using (var dw = new StreamWriter(fd.Open(), new UTF8Encoding(false))) dw.Write(newDocXml);
    }

    private static string BuildBodyXml(RevisionNarrative.Data data, string? font, bool canNumber, int startNumId, List<int> numIds, string? absId)
    {
        var sb = new StringBuilder();
        int next = startNumId;
        void Blank() => sb.Append(Para("", false, 0, 0, font, 0));
        void Line(string text, bool bold = false, int szHalfPt = 0) => sb.Append(Para(text, bold, szHalfPt, 0, font, 0));
        void Note(string text, int numId) => sb.Append(Para(text, false, 0, canNumber ? 0 : 360, font, numId));

        Line(data.IssueDate);
        Blank();
        Line(data.AddendumLabel, bold: true);
        Blank();
        if (!string.IsNullOrWhiteSpace(data.BuildingName)) Line(data.BuildingName); // above the project name
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
                int numId = 0;
                if (canNumber) { numId = next++; numIds.Add(numId); } // a fresh list (restarts at 1) per sheet
                foreach (var note in sheet.Notes)
                {
                    var label = string.IsNullOrEmpty(note.DetailNumber) ? "[insert]" : $"Detail {note.DetailNumber}";
                    Note($"{label} - {note.Text}", numId);
                }
                Blank();
            }
        }

        Line($"End of {data.AddendumLabel}", bold: true, szHalfPt: ClosingHalfPt);
        return sb.ToString();
    }

    /// <summary>One WordprocessingML paragraph. numId &gt; 0 attaches native numbering; the source list's own
    /// indent then applies, so no manual indent is added for numbered items.</summary>
    private static string Para(string text, bool bold, int szHalfPt, int indent, string? font, int numId)
    {
        var pPr = new StringBuilder($"<w:pPr><w:pStyle w:val=\"{BodyStyle}\"/>");
        if (numId > 0) pPr.Append($"<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"{numId}\"/></w:numPr>");
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

    // ---- numbering.xml helpers ----
    private static string? FindDecimalAbstractNum(string numXml)
    {
        foreach (Match m in Regex.Matches(numXml, "<w:abstractNum [^>]*?w:abstractNumId=\"(\\d+)\".*?</w:abstractNum>", RegexOptions.Singleline))
            if (m.Value.Contains("<w:numFmt w:val=\"decimal\"")) return m.Groups[1].Value;
        return null;
    }

    private static int MaxNumId(string numXml)
    {
        int max = 0;
        foreach (Match m in Regex.Matches(numXml, "<w:num w:numId=\"(\\d+)\""))
            max = Math.Max(max, int.Parse(m.Groups[1].Value));
        return max;
    }

    private static int MaxAbstractNumId(string numXml)
    {
        int max = -1;
        foreach (Match m in Regex.Matches(numXml, "<w:abstractNum [^>]*?w:abstractNumId=\"(\\d+)\""))
            max = Math.Max(max, int.Parse(m.Groups[1].Value));
        return max;
    }

    private static string InsertAbstractNum(string numXml, int newAbs)
    {
        var abs = $"<w:abstractNum w:abstractNumId=\"{newAbs}\"><w:multiLevelType w:val=\"singleLevel\"/>" +
                  "<w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/>" +
                  "<w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"720\" w:hanging=\"360\"/></w:pPr></w:lvl></w:abstractNum>";
        int idx = numXml.IndexOf("<w:num ", StringComparison.Ordinal);
        if (idx < 0) idx = numXml.LastIndexOf("</w:numbering>", StringComparison.Ordinal);
        return numXml.Substring(0, idx) + abs + numXml.Substring(idx);
    }

    /// <summary>Most-frequent direct run font (<c>w:rFonts/@w:ascii</c>) in the source body, or null.</summary>
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
        if (!string.IsNullOrWhiteSpace(data.BuildingName)) Line(data.BuildingName);
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
                int n = 1;
                foreach (var note in sheet.Notes)
                {
                    var label = string.IsNullOrEmpty(note.DetailNumber) ? "[insert]" : $"Detail {note.DetailNumber}";
                    Line($"{n++}. {label} - {note.Text}", indent: 360);
                }
                Blank();
            }
        }
        Line($"End of {data.AddendumLabel}", bold: true, size: Closing);

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        doc.Write(fs);
    }
}
