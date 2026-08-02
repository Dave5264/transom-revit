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
///     discipline/sheet, orders, normalizes, and writes a letterhead narrative. Never opens a
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

        // UI-13: both code-only dialogs below are shown WITHOUT an owner unless we supply one. An ownerless
        // ShowDialog() is still modal to the application, but it is not z-ordered against Revit's main frame,
        // so it can open BEHIND Revit — the user sees an unresponsive Revit with no visible dialog, which reads
        // as a hang. Every other dialog in the add-in sets an owner.
        var revitHwnd = Application.MainWindowHandle;

        var chosen = revIds.Count == 1 ? revIds[0] : PickRevision(doc, revIds, revitHwnd);
        if (chosen == null) return; // cancelled

        // GEN-01..GEN-04: the intro boilerplate, the sheet-prefix→discipline map, the building-name parameter and
        // the title-caser's small-word list were compiled in as one practice's house style. They now come from
        // settings.json, which leaves them at exactly those defaults until someone overrides them.
        var opts = RevisionNarrative.Options.FromSettings(TransomSettings.Load());

        // Build first (read-only, cheap) so the confirm step can show the computed header values.
        var data = RevisionNarrative.Build(doc, chosen, opts);

        // Confirm/adjust the project information before any file is written. Cancel aborts cleanly.
        if (!ConfirmProjectInfo(data, revitHwnd, opts)) return;

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

    /// <summary>UI-13: gives a code-only WPF window Revit's main frame as its native owner, so it can't open
    /// behind Revit and it minimises/restores with it. Note WPF's <c>CenterOwner</c> is implemented against
    /// <c>Window.Owner</c>, NOT the interop owner, so these windows keep <c>CenterScreen</c> — the same trap
    /// StartupCommand documents for the Hub.</summary>
    private static void OwnToRevit(Window win, System.IntPtr revitHwnd)
    {
        if (revitHwnd == System.IntPtr.Zero) return;
        try { new System.Windows.Interop.WindowInteropHelper(win) { Owner = revitHwnd }; }
        catch { /* owner is best-effort */ }
    }

    /// <summary>Minimal code-only WPF picker so any number of revisions is supported (no XAML resource).</summary>
    private static ElementId? PickRevision(Document doc, System.Collections.Generic.IList<ElementId> revIds, System.IntPtr revitHwnd)
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
        OwnToRevit(win, revitHwnd);

        return win.ShowDialog() == true && combo.SelectedIndex >= 0 ? revIds[combo.SelectedIndex] : null;
    }

    private static string Trim(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();

    /// <summary>
    ///     Confirm step before any file is written: shows the header values the narrative computed from the
    ///     model (project name, building, addendum label, dates, the referenced plan set) and lets the user
    ///     correct them. Edits go back into <paramref name="data"/>; the intro sentence is recomposed when the
    ///     referenced title/date changed. Returns false on Cancel. Code-only WPF, same style as PickRevision.
    /// </summary>
    private static bool ConfirmProjectInfo(RevisionNarrative.Data data, System.IntPtr revitHwnd, RevisionNarrative.Options opts)
    {
        var grid = new WpfGrid { Margin = new Thickness(12, 8, 12, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        WpfTextBox AddRow(string label, string value, bool multiline = false)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            WpfGrid.SetRow(lbl, r); WpfGrid.SetColumn(lbl, 0);
            var box = new WpfTextBox { Text = value, MinWidth = 380, Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            if (multiline)
            {
                box.AcceptsReturn = true;
                box.TextWrapping = TextWrapping.NoWrap;
                box.MinLines = 2;
                box.VerticalAlignment = VerticalAlignment.Stretch;
            }
            WpfGrid.SetRow(box, r); WpfGrid.SetColumn(box, 1);
            grid.Children.Add(lbl); grid.Children.Add(box);
            return box;
        }

        var addendum = AddRow("Narrative title:", data.AddendumLabel);
        var issueDate = AddRow("Issue date:", data.IssueDate);
        var building = AddRow("Building name:", data.BuildingName);
        var project = AddRow("Project name:", data.ProjectName);
        // ONE LINE PER LINE. AddressLines is one-element-per-output-line, so joining/splitting on commas
        // split the standard "City, ST ZIP" line into two document lines — on the happy path, without the
        // user touching the field (the read-back is unconditional, and the dialog showed the joined text as
        // if it were correct). Newlines are what the writer actually consumes.
        var address = AddRow("Address (one line per line):", string.Join(Environment.NewLine, data.AddressLines), multiline: true);
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
        OwnToRevit(win, revitHwnd);
        if (win.ShowDialog() != true) return false;

        data.AddendumLabel = addendum.Text.Trim();
        data.IssueDate = issueDate.Text.Trim();
        data.BuildingName = building.Text.Trim();
        data.ProjectName = project.Text.Trim();
        data.ProjectNumberLine = projNo.Text.Trim();
        data.AddressLines = address.Text
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .ToList();
        if (refTitle.Text.Trim() != data.RefTitle || refDate.Text.Trim() != data.RefDate)
        {
            data.RefTitle = refTitle.Text.Trim();
            data.RefDate = refDate.Text.Trim();
            // Recompose through the SAME template Build used, or an overridden intro would silently revert to the
            // built-in phrasing the moment the user edited the referenced title/date.
            data.IntroSentence = RevisionNarrative.ComposeIntro(data.RefTitle, data.RefDate, opts.IntroTemplate);
        }
        return true;
    }
}
