using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using Nice3point.Revit.Toolkit.External;
using Transom.Core;
using WpfGrid = System.Windows.Controls.Grid;
using WpfTextBox = System.Windows.Controls.TextBox;

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

        // Build first (read-only, cheap) so the confirm step can show the computed header values.
        var data = RevisionNarrative.Build(doc, chosen, new RevisionNarrative.Options());

        // Confirm/adjust the project information before any file is written. Cancel aborts cleanly.
        if (!ConfirmProjectInfo(data)) return;

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

        try
        {
            OfficeIsolation.Engine.WriteRevisionNarrative(data, sdlg.FileName, templatePath);
        }
        catch (System.IO.IOException)
        {
            // Almost always the output (or template) .docx is open in Word — give a clear, actionable message
            // instead of a raw exception dialog.
            Transom.Views.ReportDialog.Show("Transom — Revision Narrative",
                "Couldn't write the Word file.",
                $"“{System.IO.Path.GetFileName(sdlg.FileName)}” may be open in Word (or the template is). "
                + "Close it and run the Revision Narrative again.", isError: true);
            return;
        }

        int sheets = data.Disciplines.Sum(d => d.Sheets.Count);
        int notes = data.Disciplines.Sum(d => d.Sheets.Sum(s => s.Notes.Count));
        var details = $"Disciplines: {data.Disciplines.Count}\nSheets: {sheets}\nNotes: {notes} (from {data.SourceCloudCount} clouds)\nSaved: {sdlg.FileName}";
        if (data.Warnings.Count > 0)
            details += "\n\nWarnings:\n- " + string.Join("\n- ", data.Warnings);

        Transom.Views.ReportDialog.Show("Transom — Revision Narrative",
            $"Generated narrative for “{data.AddendumLabel}”.", details, isError: false);
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

    /// <summary>
    ///     Confirm step before any file is written: shows the header values the narrative computed from the
    ///     model (project name, building, addendum label, dates, the referenced plan set) and lets the user
    ///     correct them. Edits go back into <paramref name="data"/>; the intro sentence is recomposed when the
    ///     referenced title/date changed. Returns false on Cancel. Code-only WPF, same style as PickRevision.
    /// </summary>
    private static bool ConfirmProjectInfo(RevisionNarrative.Data data)
    {
        var grid = new WpfGrid { Margin = new Thickness(12, 8, 12, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        WpfTextBox AddRow(string label, string value)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            WpfGrid.SetRow(lbl, r); WpfGrid.SetColumn(lbl, 0);
            var box = new WpfTextBox { Text = value, MinWidth = 380, Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            WpfGrid.SetRow(box, r); WpfGrid.SetColumn(box, 1);
            grid.Children.Add(lbl); grid.Children.Add(box);
            return box;
        }

        var addendum = AddRow("Narrative title:", data.AddendumLabel);
        var issueDate = AddRow("Issue date:", data.IssueDate);
        var building = AddRow("Building name:", data.BuildingName);
        var project = AddRow("Project name:", data.ProjectName);
        var address = AddRow("Address:", string.Join(", ", data.AddressLines));
        var projNo = AddRow("Project number line:", data.ProjectNumberLine);
        var refTitle = AddRow("Revising the set titled:", data.RefTitle);
        var refDate = AddRow("That set's date:", data.RefDate);

        var ok = new Button { Content = "Looks right — continue", IsDefault = true, MinWidth = 150, Margin = new Thickness(0, 12, 8, 12) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 12, 12, 12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Confirm the project information for this narrative (read from the model — edit anything that's off):",
            Margin = new Thickness(12, 12, 12, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 520,
        });
        panel.Children.Add(grid);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = "Transom — Revision Narrative",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
        };
        ok.Click += (_, _) => { win.DialogResult = true; };
        if (win.ShowDialog() != true) return false;

        data.AddendumLabel = addendum.Text.Trim();
        data.IssueDate = issueDate.Text.Trim();
        data.BuildingName = building.Text.Trim();
        data.ProjectName = project.Text.Trim();
        data.ProjectNumberLine = projNo.Text.Trim();
        data.AddressLines = address.Text.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        if (refTitle.Text.Trim() != data.RefTitle || refDate.Text.Trim() != data.RefDate)
        {
            data.RefTitle = refTitle.Text.Trim();
            data.RefDate = refDate.Text.Trim();
            data.IntroSentence = RevisionNarrative.ComposeIntro(data.RefTitle, data.RefDate);
        }
        return true;
    }
}
