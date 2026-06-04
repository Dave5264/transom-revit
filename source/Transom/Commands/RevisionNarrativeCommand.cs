using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using Nice3point.Revit.Toolkit.External;
using Transom.Core;

namespace Transom.Commands;

/// <summary>
///     Generates a Revision Narrative (.docx) from the revision clouds of a selected revision — fully
///     stand-alone (no Claude/MCP). Reads each cloud's Comments, resolves sheet + detail number, groups by
///     discipline/sheet, orders, normalizes, and writes the firm's letterhead narrative. Never opens a
///     transaction, so it cannot modify the model (Manual mode + read-only logic only).
/// </summary>
[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public class RevisionNarrativeCommand : ExternalCommand
{
    public override void Execute()
    {
        var doc = UiDocument.Document;

        var revIds = Revision.GetAllRevisionIds(doc);
        if (revIds.Count == 0)
        {
            TaskDialog.Show("Transom — Revision Narrative", "This model has no revisions.");
            return;
        }

        var chosen = revIds.Count == 1 ? revIds[0] : PickRevision(doc, revIds);
        if (chosen == null) return; // cancelled

        // Optional: start from a previous narrative / letterhead .docx. Its header, footer, page setup, styles
        // and fonts are reused exactly; only the body is replaced. Cancel = produce a plain document.
        string? templatePath = null;
        var tdlg = new OpenFileDialog
        {
            Title = "Start from a previous narrative (.docx) — reuses header/footer/fonts, replaces body. Cancel to skip.",
            Filter = "Word document (*.docx)|*.docx",
            CheckFileExists = true,
        };
        if (tdlg.ShowDialog() == true) templatePath = tdlg.FileName;

        var rev = doc.GetElement(chosen) as Revision;
        var safeName = (rev?.Description ?? "Revision").Replace('/', '-').Replace('\\', '-');
        var sdlg = new SaveFileDialog
        {
            Title = "Save Revision Narrative as…",
            Filter = "Word document (*.docx)|*.docx",
            FileName = $"{safeName}_Revision Narrative.docx",
            DefaultExt = ".docx",
        };
        if (sdlg.ShowDialog() != true) return;

        var data = RevisionNarrative.Build(doc, chosen, new RevisionNarrative.Options());
        RevisionNarrativeDocxWriter.Write(data, sdlg.FileName, templatePath);

        int sheets = data.Disciplines.Sum(d => d.Sheets.Count);
        int notes = data.Disciplines.Sum(d => d.Sheets.Sum(s => s.Notes.Count));
        var msg = $"Generated narrative for “{data.AddendumLabel}”.\n\n" +
                  $"Disciplines: {data.Disciplines.Count}\nSheets: {sheets}\nNotes: {notes}\n\n" +
                  $"Saved: {sdlg.FileName}";
        if (data.Warnings.Count > 0)
            msg += "\n\nWarnings:\n- " + string.Join("\n- ", data.Warnings);

        TaskDialog.Show("Transom — Revision Narrative", msg);
    }

    /// <summary>Minimal code-only WPF picker so any number of revisions is supported (no XAML resource).</summary>
    private static ElementId? PickRevision(Document doc, System.Collections.Generic.IList<ElementId> revIds)
    {
        var combo = new System.Windows.Controls.ComboBox { Margin = new Thickness(12, 4, 12, 12), MinWidth = 360 };
        foreach (var id in revIds)
        {
            var r = doc.GetElement(id) as Revision;
            var label = r == null ? id.ToString() : $"{Trim(r.Description)}  ({Trim(r.RevisionDate)})";
            combo.Items.Add(label);
        }
        combo.SelectedIndex = revIds.Count - 1; // default to the most recent

        var ok = new Button { Content = "Generate", IsDefault = true, Width = 90, Margin = new Thickness(12, 0, 12, 12), HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Select the revision to narrate:", Margin = new Thickness(12, 12, 12, 4) });
        panel.Children.Add(combo);
        panel.Children.Add(ok);

        var win = new Window
        {
            Title = "Transom — Revision Narrative",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
        };
        ok.Click += (_, _) => { win.DialogResult = true; };

        return win.ShowDialog() == true && combo.SelectedIndex >= 0 ? revIds[combo.SelectedIndex] : null;
    }

    private static string Trim(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
}
