using System.IO;
using NPOI.XWPF.UserModel;

namespace Transom.Core;

/// <summary>
///     Renders a <see cref="RevisionNarrative.Data"/> into a .docx using NPOI's XWPF engine (already shipped
///     via NPOI.OOXML — no new dependency). Reproduces the firm's narrative layout reverse-engineered from
///     the existing Addendum A document:
///       date · (blank) · <b>Addendum X</b> · (blank) · project name · address lines · firm project number ·
///       (blank) · boilerplate sentence · (blank) · <b>Drawing Revisions:</b> · per discipline <b>header</b> ·
///       per sheet "NUMBER – NAME" · numbered notes (restart at 1 per sheet) · <b>End of Addendum X</b>.
///
///     When <paramref name="templatePath"/> points at a letterhead .docx (logo header + firm footer, empty
///     body), the body is appended into a COPY of it so the letterhead is preserved (answer #9). Otherwise a
///     plain document is produced.
/// </summary>
public static class RevisionNarrativeDocxWriter
{
    private const string Font = "Calibri";
    private const int Body = 11;   // pt
    private const int Closing = 12; // "End of Addendum X" was 12pt in the source

    public static void Write(RevisionNarrative.Data data, string outputPath, string? templatePath = null)
    {
        XWPFDocument doc;
        if (!string.IsNullOrWhiteSpace(templatePath) && File.Exists(templatePath))
        {
            // Load the template fully into memory so we can write back to a (possibly same-named) output.
            byte[] bytes = File.ReadAllBytes(templatePath);
            using var ms = new MemoryStream(bytes);
            doc = new XWPFDocument(ms);
        }
        else
        {
            doc = new XWPFDocument();
        }

        Line(doc, data.IssueDate);
        Blank(doc);
        Line(doc, data.AddendumLabel, bold: true);
        Blank(doc);
        Line(doc, data.ProjectName);
        foreach (var addr in data.AddressLines) Line(doc, addr);
        Line(doc, data.ProjectNumberLine);
        Blank(doc);
        Line(doc, data.IntroSentence);
        Blank(doc);
        Line(doc, "Drawing Revisions:", bold: true);
        Blank(doc);

        foreach (var disc in data.Disciplines)
        {
            Line(doc, disc.Name, bold: true);
            Blank(doc);
            foreach (var sheet in disc.Sheets)
            {
                Line(doc, $"{sheet.Number} – {sheet.Name.ToUpperInvariant()}"); // en-dash separator
                foreach (var note in sheet.Notes)
                {
                    // "Detail N - text" when the cloud's detail location is known; "[insert] - text" when it
                    // isn't (cloud drawn directly on the sheet / detail not discoverable) for the user to fill.
                    var label = string.IsNullOrEmpty(note.DetailNumber) ? "[insert]" : $"Detail {note.DetailNumber}";
                    Line(doc, $"{label} - {note.Text}", indentTwips: 360);
                }
                Blank(doc);
            }
        }

        Line(doc, $"End of {data.AddendumLabel}", bold: true, size: Closing);

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        doc.Write(fs);
    }

    private static void Blank(XWPFDocument doc)
    {
        var p = doc.CreateParagraph();
        p.SpacingAfter = 0;
    }

    private static void Line(XWPFDocument doc, string text, bool bold = false, int size = Body, int indentTwips = 0)
    {
        var p = doc.CreateParagraph();
        p.SpacingAfter = 0;
        if (indentTwips > 0) p.IndentationLeft = indentTwips;
        var r = p.CreateRun();
        r.FontFamily = Font;
        r.FontSize = size;
        r.IsBold = bold;
        r.SetText(text ?? "");
    }
}
