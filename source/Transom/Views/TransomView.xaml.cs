using System.Linq;
using System.Windows.Controls;
using Transom.ViewModels;

namespace Transom.Views;

public sealed partial class TransomView
{
    /// <summary>The single open instance (modeless singleton), or null when closed.</summary>
    public static TransomView? Instance { get; private set; }

    public TransomView(TransomViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        ApplyTheme();
        Instance = this;
        Closed += (_, _) => Instance = null;

        // Show a modal resolver for each type-param conflict during import preview.
        viewModel.ConflictResolver = conflict =>
        {
            var dlg = new ConflictDialog(conflict) { Owner = this };
            dlg.ShowDialog();
            return dlg.Result;
        };

        // Per-parameter group-conflict resolver: one multi-option dialog for each distinct blue (project)
        // or yellow (built-in) column the import touches inside Revit groups. Returns the chosen path, or
        // null to cancel the whole import. Supersedes the old coarse all-fields "vary?" confirm.
        viewModel.GroupConflictResolver = prompt =>
        {
            var dlg = new GroupResolutionDialog(prompt) { Owner = this };
            dlg.ShowDialog();
            return dlg.Result;
        };

        // Tell the user built-in group edits were staged for Claude-assist.
        viewModel.ClaudeStagedNotice = path =>
            System.Windows.MessageBox.Show(this,
                "Built-in parameter edits on grouped elements were staged for Claude-assist.\n\n" +
                "Files are ready for Claude:\n" + path + "\n\n" +
                "Run Claude to perform the group definition-swap and apply them.",
                "Ready for Claude", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e) => Close();

    private void ClaudeHelp_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        new ClaudeAssistHelpDialog { Owner = this }.ShowDialog();
    }

    /// <summary>Matches Revit's UI theme — swaps the palette brushes to dark when Revit is dark.</summary>
    private void ApplyTheme()
    {
        bool dark;
        try { dark = Autodesk.Revit.UI.UIThemeManager.CurrentTheme == Autodesk.Revit.UI.UITheme.Dark; }
        catch { return; }
        if (!dark) return;

        void Set(string key, string hex) =>
            Resources[key] = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

        Set("Bg", "#2D2D30");
        Set("Surface", "#3A3A3D");
        Set("Text", "#E8E8E8");
        Set("Muted", "#B4B4B4");
        Set("Hint", "#A8A8A8");
        Set("Line", "#4A4A4D");
        Set("Line2", "#5A5A5D");
        Set("Accent", "#4C9DE0");
        Set("AccentLine", "#3E6E9E");
        Set("InfoBg", "#23323F");
        Set("Ok", "#2FA882");
        Set("AccentHover", "#2E4456");
        Set("AltRow", "#333336");
        Set("WarnBg", "#3A3320");
        Set("WarnLine", "#5A4A22");
        Set("Warn", "#D8A24A");
        // Legend term colours brightened for the dark surface (the light #1E7A34 / #B8860B are unreadable on dark).
        Set("LegendGreen", "#3FB36A");
        Set("LegendYellow", "#D8A24A");
    }

    // ----- Right-click "copy name" on the export (FilteredSchedules) and import (AffectedSchedules) lists -----

    /// <summary>Copies the right-clicked schedule's name to the clipboard.</summary>
    private void CopyScheduleName_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is MenuItem mi) CopyToClipboard(NameOf(ItemFor(mi)));
    }

    /// <summary>Copies every name in the list the right-clicked schedule belongs to (one per line).</summary>
    private void CopyAllScheduleNames_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || DataContext is not TransomViewModel vm) return;
        var names = ItemFor(mi) switch
        {
            ScheduleEntry => vm.FilteredSchedules.Select(s => s.Name),
            Core.SheetSummary => vm.AffectedSchedules.Select(s => s.ScheduleName),
            _ => Enumerable.Empty<string>(),
        };
        CopyToClipboard(string.Join(System.Environment.NewLine, names.Where(n => !string.IsNullOrWhiteSpace(n))));
    }

    /// <summary>
    ///     The schedule row a context-menu click targets. A ContextMenu inherits its PlacementTarget's
    ///     DataContext (the bound row), which the MenuItem inherits in turn; fall back to reading the
    ///     PlacementTarget explicitly if that inheritance is ever absent.
    /// </summary>
    private static object? ItemFor(MenuItem mi)
    {
        if (mi.DataContext is ScheduleEntry or Core.SheetSummary) return mi.DataContext;
        if (mi.Parent is ContextMenu { PlacementTarget: System.Windows.FrameworkElement t }) return t.DataContext;
        return mi.DataContext;
    }

    private static string NameOf(object? item) => item switch
    {
        ScheduleEntry se => se.Name,
        Core.SheetSummary ss => ss.ScheduleName,
        _ => "",
    };

    private static void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        // The clipboard can be transiently locked by another app; a failed copy shouldn't crash the add-in.
        try { System.Windows.Clipboard.SetText(text); }
        catch { /* clipboard busy — ignore */ }
    }
}
