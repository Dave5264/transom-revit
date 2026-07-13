using System.Windows;
using System.Windows.Media;
using Transom.Core;

namespace Transom.Views;

/// <summary>
///     Option-2 follow-up 2 (user request 2026-07-12): after a 2a/2b conversion is committed (and any extra
///     schedules ticked), asks what happens to the OLD parameter's values — leave them (default), clear them
///     (string columns only), or write one uniform user-entered value. <see cref="Result"/> is the prompt with
///     the choice written back, or null when the window is closed without Continue (treated as Leave).
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
                       $"every element in the hidden “{p.Field}” parameter. What should happen to them?";

        if (!p.AllowClear)
        {
            ClearRb.IsEnabled = false;
            ClearNote.Visibility = System.Windows.Visibility.Visible;
        }

        // The value box is live only while "Replace them" is the selection; typing clears a prior error.
        SetRb.Checked += (_, _) => { ValueBox.IsEnabled = true; ValueBox.Focus(); };
        SetRb.Unchecked += (_, _) => { ValueBox.IsEnabled = false; ValueError.Visibility = System.Windows.Visibility.Collapsed; };
        ValueBox.TextChanged += (_, _) => ValueError.Visibility = System.Windows.Visibility.Collapsed;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (SetRb.IsChecked == true && string.IsNullOrWhiteSpace(ValueBox.Text))
        {
            ValueError.Visibility = System.Windows.Visibility.Visible;   // an empty "replace with" is Clear in disguise — make them say so
            return;
        }
        _prompt.Choice = ClearRb.IsChecked == true ? OldValueDisposition.Clear
                       : SetRb.IsChecked == true ? OldValueDisposition.SetValue
                       : OldValueDisposition.Leave;
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
