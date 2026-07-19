using System.Windows;
using System.Windows.Media;
using Transom.Core;

namespace Transom.Views;

/// <summary>
///     Option-2 follow-up 2 (user request 2026-07-12; reworked per user direction 2026-07-18): after a 2a/2b
///     conversion is committed (and any extra schedules ticked), tells the user the OLD parameter's values were
///     left in place — Transom can't edit them on group members, and it never half-applies a column. When Claude
///     Assist is ON, additionally offers the opt-in "have Claude update them" path, which reveals the cleanup
///     choice: clear (string columns only) or one uniform replacement value. The choice is all-or-nothing for
///     the column — API-writable elements are written directly, grouped members staged for Claude's Edit Group
///     pass. <see cref="Result"/> is the prompt with the choice written back, or null when the window closes
///     without Continue (treated as Leave).
/// </summary>
public partial class Option2OldValuesDialog : Window
{
    private readonly Option2OldValuesPrompt _prompt;

    public Option2OldValuesPrompt? Result { get; private set; }

    public Option2OldValuesDialog(Option2OldValuesPrompt p)
    {
        _prompt = p;
        InitializeComponent();
        ApplyTheme();

        string newName = string.IsNullOrWhiteSpace(p.NewParamName) ? $"{p.Field} (Transom)" : p.NewParamName;
        HeadingText.Text = $"Old values — “{p.Field}”";
        SubText.Text = $"The column now shows the new parameter “{newName}”, but the old values still exist on " +
                       $"every element in the original “{p.Field}” parameter.";
        WarnText.Text = $"The old “{p.Field}” data has been left in place: Revit doesn't allow editing this " +
                        "parameter on model-group members outside Edit Group mode, and Transom won't change only " +
                        "part of a column — so no element's old value was touched, grouped or not." +
                        (p.AssistEnabled
                            ? " If you want them cleaned up, Claude can apply the change for you (grouped members " +
                              "via Edit Group; the rest written directly)."
                            : " To clean them up, turn on Claude Assist in Settings and re-import, or edit the " +
                              "groups yourself in Edit Group mode.");
        if (p.AssistEnabled) ClaudeBtn.Visibility = System.Windows.Visibility.Visible;

        if (!p.AllowClear)
        {
            ClearRb.IsEnabled = false;
            ClearNote.Visibility = System.Windows.Visibility.Visible;
        }

        // The value box is live only while "Replace them" is the selection; interacting clears a prior error.
        SetRb.Checked += (_, _) => { ValueBox.IsEnabled = true; ValueBox.Focus(); ChoiceError.Visibility = System.Windows.Visibility.Collapsed; };
        SetRb.Unchecked += (_, _) => { ValueBox.IsEnabled = false; ValueError.Visibility = System.Windows.Visibility.Collapsed; };
        ClearRb.Checked += (_, _) => ChoiceError.Visibility = System.Windows.Visibility.Collapsed;
        ValueBox.TextChanged += (_, _) => ValueError.Visibility = System.Windows.Visibility.Collapsed;
    }

    /// <summary>Stage 1 acknowledge — old values stay (Leave). Same as closing the window.</summary>
    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Opt-in to the Claude-assisted cleanup — swap the warning for the Clear/Replace choice.</summary>
    private void Claude_Click(object sender, RoutedEventArgs e)
    {
        WarnPanel.Visibility = System.Windows.Visibility.Collapsed;
        ChoicePanel.Visibility = System.Windows.Visibility.Visible;
    }

    /// <summary>Backing out of stage 2 = leave the old values in place (no partial anything).</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        // Opting in requires an explicit choice — there is no "Leave" radio here (Cancel is the way out),
        // so nothing selected is a validation stop, not a silent default.
        if (ClearRb.IsChecked != true && SetRb.IsChecked != true)
        {
            ChoiceError.Visibility = System.Windows.Visibility.Visible;
            return;
        }
        if (SetRb.IsChecked == true && string.IsNullOrWhiteSpace(ValueBox.Text))
        {
            ValueError.Visibility = System.Windows.Visibility.Visible;   // an empty "replace with" is Clear in disguise — make them say so
            return;
        }
        _prompt.Choice = ClearRb.IsChecked == true ? OldValueDisposition.Clear : OldValueDisposition.SetValue;
        _prompt.NewValue = SetRb.IsChecked == true ? ValueBox.Text.Trim() : "";
        Result = _prompt;
        DialogResult = true;
    }

    /// <summary>Matches Revit's UI theme (same palette swap as GroupResolutionDialog).</summary>
    private void ApplyTheme()
    {
        bool dark;
        try { dark = Autodesk.Revit.UI.UIThemeManager.CurrentTheme == Autodesk.Revit.UI.UITheme.Dark; }
        catch { return; }
        if (!dark) return;

        void Set(string key, string hex) =>
            Resources[key] = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));

        Set("Bg", "#2D2D30");
        Set("Surface", "#3A3A3D");
        Set("Text", "#E8E8E8");
        Set("Muted", "#B4B4B4");
        Set("Line", "#4A4A4D");
        Set("Accent", "#4C9DE0");
        Set("Error", "#E8736B");
    }
}
