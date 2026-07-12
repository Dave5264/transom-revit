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

    /// <summary>Brings the window to the Settings tab — used by the Settings ribbon button.</summary>
    public void SelectSettingsTab()
    {
        foreach (var item in Tabs.Items)
            if (item is System.Windows.Controls.TabItem ti && ti.Header as string == "Settings")
            { Tabs.SelectedItem = ti; return; }
        if (Tabs.Items.Count > 0) Tabs.SelectedIndex = Tabs.Items.Count - 1;
    }

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e) => Close();

    /// <summary>The Settings status panel re-checks its layers whenever the tab is brought up, so it's
    /// current without the user clicking Refresh (checks are cheap file/process reads).</summary>
    private void Tabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return; // ignore bubbled child selections (DataGrid, ComboBox)
        if (Tabs.SelectedItem is System.Windows.Controls.TabItem ti && ti.Header as string == "Settings"
            && DataContext is TransomViewModel vm)
            vm.RefreshClaudeStatusCommand.Execute(null);
    }

    // ----- Inline confirm-strip value box (DataGrid RowDetails) -----
    // The DataGrid swallows the FIRST left-click for row selection, so a text box inside RowDetails only took focus on
    // a second click / drag. Force it to focus (and select its contents) on the first click, so a single click puts
    // the cursor in and selects the value — the user can immediately type a correction. Mirrors a normal text field.

    private void EditBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
        {
            tb.Focus();          // grab focus before the DataGrid consumes the click for row selection
            tb.SelectAll();      // select the value so a single click → type replaces it
            e.Handled = true;    // stop the click bubbling to the DataGrid (which would steal focus back)
        }
    }

    private void EditBox_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox tb) tb.SelectAll();
    }

    // The Confirm button has the SAME DataGrid-eats-the-first-click problem as the box: the grid spends the first
    // click selecting the row, so the button only fired on the second click. Run the confirm command directly on the
    // first mouse-down and mark it handled — so one click always confirms, regardless of row selection.
    private void ConfirmButton_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Button { DataContext: Core.ProposedChange change } && DataContext is TransomViewModel vm
            && vm.ConfirmRowCommand.CanExecute(change))
        {
            vm.ConfirmRowCommand.Execute(change);
            e.Handled = true;   // stop the DataGrid from consuming this click for row-selection (the second-click cause)
        }
    }

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
        Set("LegendRed", "#E8736B");
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
            AffectedScheduleRow => vm.AffectedSchedules.Select(s => s.ScheduleName),
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
        if (mi.DataContext is ScheduleEntry or AffectedScheduleRow) return mi.DataContext;
        if (mi.Parent is ContextMenu { PlacementTarget: System.Windows.FrameworkElement t }) return t.DataContext;
        return mi.DataContext;
    }

    private static string NameOf(object? item) => item switch
    {
        ScheduleEntry se => se.Name,
        AffectedScheduleRow row => row.ScheduleName,
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
