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
        Closed += (_, _) => { Instance = null; viewModel.Detach(); };

        // UI-08: with two Revit sessions open — the case the project switcher exists to serve — the taskbar and
        // Alt-Tab showed two identical "Transom" entries with no way to tell which document each was editing.
        // Track SelectedProject rather than binding Title, so an empty project name falls back cleanly to the
        // bare product name instead of rendering a dangling em-dash.
        UpdateTitle(viewModel);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TransomViewModel.SelectedProject)) UpdateTitle(viewModel);
        };

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

        // Option-2 follow-ups (user request 2026-07-12): after a 2a/2b choice, the "also replace in these
        // schedules" checklist, then the "what happens to the old values" chooser. Closing either without
        // Continue returns null → the viewmodel takes the safe default (no extra schedules / leave old values).
        viewModel.Option2SchedulesResolver = prompt =>
        {
            var dlg = new Option2SchedulesDialog(prompt) { Owner = this };
            dlg.ShowDialog();
            return dlg.Result;
        };
        viewModel.Option2OldValuesResolver = prompt =>
        {
            var dlg = new Option2OldValuesDialog(prompt) { Owner = this };
            dlg.ShowDialog();
            return dlg.Result;
        };

        // Tell the user built-in group edits were staged for Claude-assist.
        viewModel.ClaudeStagedNotice = path =>
            System.Windows.MessageBox.Show(this,
                "Built-in parameter edits on grouped elements were staged for Claude-assist.\n\n" +
                "Files are ready for Claude:\n" + path + "\n\n" +
                "Run Claude to apply them — the grouped built-in edits are written through Revit's Edit Group mode " +
                "(step-by-step is in the guide beside the file).",
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

    /// <summary>The Settings / Claude Skills status panels re-check their layers whenever the tab is brought
    /// up, so they're current without the user clicking Refresh (checks are cheap file/process reads). Claude
    /// Skills also re-reads the library folder — Claude may have saved a new skill since the window opened.</summary>
    private void Tabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return; // ignore bubbled child selections (DataGrid, ComboBox)
        if (Tabs.SelectedItem is not System.Windows.Controls.TabItem ti || DataContext is not TransomViewModel vm) return;
        switch (ti.Header as string)
        {
            case "Settings":
                vm.RefreshClaudeStatusCommand.Execute(null);
                break;
            case "Claude Skills":
                vm.RefreshSkillsCommand.Execute(null);
                vm.RefreshClaudeStatusCommand.Execute(null);
                break;
        }
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

    // Same first-click treatment for the Discard button next to Confirm.
    private void DiscardButton_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Button { DataContext: Core.ProposedChange change } && DataContext is TransomViewModel vm
            && vm.DiscardRowCommand.CanExecute(change))
        {
            vm.DiscardRowCommand.Execute(change);
            e.Handled = true;
        }
    }

    private void ClaudeHelp_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        new ClaudeAssistHelpDialog { Owner = this }.ShowDialog();
    }

    /// <summary>The "?" next to a red "Claude app running" row — the process check can miss Claude Code
    /// (terminal / VS Code / node hosts), so this dialog offers the Claude Assist guide export as the way
    /// to let a running Claude session diagnose and finish the connection itself.</summary>
    private void ClaudeRunningHelp_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        new ClaudeConnectHelpDialog { Owner = this, DataContext = DataContext }.ShowDialog();
    }

    /// <summary>The "Show me what you can do" Settings button — a dialog explaining the demo export
    /// (Claude builds a small shed in a fresh project), with the export right there.</summary>
    private void Showcase_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        new ShowcaseDialog { Owner = this, DataContext = DataContext }.ShowDialog();
    }

    /// <summary>The "Why?" link next to the bypass-permissions advisory — explains the focus-steal failure mode
    /// in depth (user-reported: approval prompts pull focus off Revit mid-sequence and UI-assist clicks miss).</summary>
    private void BypassWhy_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(this,
            "When Claude applies staged edits with UI-assist, it drives Revit's own user interface: it enters " +
            "Edit Group mode, clicks the member in the drawing area, types the new value into the Properties " +
            "palette, and clicks Finish. For those clicks and keystrokes to land, the Revit window must stay in " +
            "front and keep keyboard/mouse focus for the whole sequence.\n\n" +
            "If Claude is running with normal permission prompts, each tool call can pop an “Allow this?” " +
            "dialog in the Claude window. That prompt takes Windows focus away from Revit at that exact moment — " +
            "and Claude has no way to see the focus change. Its next click lands on the Claude window (or on " +
            "nothing), the Edit Group session is left half-finished, and Claude gets confused about why its " +
            "clicks aren’t landing. The result is stalled or partially-applied edits that look like Revit " +
            "misbehaving when it’s really a focus war between the two windows.\n\n" +
            "Running Claude with bypass permissions (for example “claude --dangerously-skip-permissions”, or " +
            "the client’s session-wide “don’t ask again” approval) means no prompt appears mid-sequence, " +
            "Revit keeps focus, and the click–type–finish sequence completes reliably.\n\n" +
            "Scope note: bypass only relaxes Claude’s own tool-approval prompts on this machine. The Transom " +
            "bridge itself is unchanged — loopback-only, per-user, session-token gated, no admin rights.",
            "Why bypass permissions?", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    private void LegendMoreInfo_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        new ExportLegendDialog { Owner = this }.ShowDialog();
    }

    /// <summary>UI-08: "Transom — &lt;project&gt;", or plain "Transom" when no document is open.</summary>
    private void UpdateTitle(TransomViewModel vm) =>
        Title = string.IsNullOrWhiteSpace(vm.SelectedProject) ? "Transom" : "Transom — " + vm.SelectedProject;

    /// <summary>UI-11: the bridge port's Apply. The box is display-only until this runs, so tabbing out of the
    /// field can no longer restart a live bridge. Validates the range (a failed binding conversion used to be
    /// swallowed silently) and confirms before disturbing a running Claude session.</summary>
    private void BridgePortApply_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not TransomViewModel vm) return;

        var text = (BridgePortBox.Text ?? "").Trim();
        if (!int.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var port)
            || port < 1024 || port > 65535)
        {
            BridgePortError.Text = "Enter a whole number between 1024 and 65535. " +
                                   "Ports below 1024 need administrator rights, which Transom deliberately never asks for.";
            BridgePortError.Visibility = System.Windows.Visibility.Visible;
            BridgePortBox.Focus();
            BridgePortBox.SelectAll();
            return;
        }

        BridgePortError.Visibility = System.Windows.Visibility.Collapsed;
        if (port == vm.BridgePort) return;   // no-op edit — never restart the bridge for it

        if (vm.IsClaudeAssistEnabled &&
            System.Windows.MessageBox.Show(this,
                $"Change the bridge port to {port}?\n\n" +
                "Claude Assist is on, so this restarts the running bridge and rewrites the Claude Code " +
                "registration. Claude Code loads MCP servers only at startup, so you'll need to restart it " +
                "afterwards before the tools work again.",
                "Transom — Bridge port",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.OK)
        {
            BridgePortBox.Text = vm.BridgePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }

        vm.BridgePort = port;   // OnBridgePortChanged persists, re-registers and restarts
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
