using System.IO;
using NPOI.XWPF.UserModel;

namespace Transom.Core;

/// <summary>
///     Renders a <see cref="RevisionNarrative.Data"/> into a .docx using NPOI's XWPF engine (shipped via
///     NPOI.OOXML — no new dependency).
///
///     <para><b>Start from a previous narrative (recommended).</b> When <paramref name="templatePath"/> points
///     at an existing narrative/letterhead .docx, the document is opened as a copy and its <b>body content is
///     cleared</b>, then the new narrative is written into it. Everything else is inherited <b>exactly</b>:
///     the first-page header (logo), the footers (firm address / page number), the page size and margins
///     (the section properties), and the styles/fonts (the body uses the document's <c>No Spacing</c> style,
///     so the typeface and spacing match the source). Pick last addendum's narrative and you get a
///     pixel-identical shell with fresh content.</para>
///
///     <para>With no template, a plain document is produced (Calibri 11, the firm default).</para>
/// </summary>
public static class RevisionNarrativeDocxWriter
{
    private const string Font = "Calibri";
    private const int Body = 11;     // pt — fallback when there is no template
    private const int Closing = 12;  // "End of Addendum X" was 12 pt in the source
    private const string BodyStyle = "NoSpacing"; // Word's "No Spacing" — what the firm narratives use

    public static void Write(RevisionNarrative.Data data, string outputPath, string? templatePath = null)
    {
        bool fromTemplate = !string.IsNullOrWhiteSpace(templatePath) && File.Exists(templatePath);

        XWPFDocument doc;
        if (fromTemplate)
        {
            // Load the source fully into memory (so output may even reuse the same name), then clear ONLY the
            // body — headers, footers, section setup, styles, theme and fonts all remain untouched.
            byte[] bytes = File.ReadAllBytes(templatePath!);
            using var ms = new MemoryStream(bytes);
            doc = new XWPFDocument(ms);
            for (int i = doc.BodyElements.Count - 1; i >= 0; i--) doc.RemoveBodyElement(i);
        }
        else
        {
            doc = new XWPFDocument();
        }

        // Local writers capture `doc` and `fromTemplate`. When starting from a template we apply the document's
        // own body style and DON'T hardcode a font, so the appearance matches the source exactly.
        void Blank()
        {
            var p = doc.CreateParagraph();
            p.SpacingAfter = 0;
            if (fromTemplate) p.Style = BodyStyle;
        }

        void Line(string text, bool bold = false, int size = Body, int indent = 0)
        {
            var p = doc.CreateParagraph();
            p.SpacingAfter = 0;
            if (fromTemplate) p.Style = BodyStyle;
            if (indent > 0) p.IndentationLeft = indent;
            var r = p.CreateRun();
            if (!fromTemplate) { r.FontFamily = Font; r.FontSize = size; } // explicit only without a template
            else if (size != Body) r.FontSize = size;                      // direct override for the 12pt closing
            r.IsBold = bold;
            r.SetText(text ?? "");
        }

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
                    // "Detail N - text" when the cloud's detail location is known; "[insert] - text" when it
                    // isn't (cloud drawn directly on the sheet / detail not discoverable) for the user to fill.
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
