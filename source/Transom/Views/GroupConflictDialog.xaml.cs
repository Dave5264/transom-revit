using System.Windows;
using Transom.Core;

namespace Transom.Views;

public partial class GroupConflictDialog : Window
{
    public GroupDecision Decision { get; private set; } = GroupDecision.SkipGrouped;

    public GroupConflictDialog(GroupPrompt prompt)
    {
        InitializeComponent();
        ApplyTheme();

        var names = string.Join(", ", prompt.GroupNames);
        HeadingText.Text = $"{prompt.InstanceCount} edit(s) target elements inside group(s)";
        SubText.Text = $"These edits affect elements in Revit group(s): {names}. " +
                       "Group members can't be changed directly outside group-edit mode, so Transom can't " +
                       "write them in the normal import. Choose how to proceed — everything not in a group " +
                       "still applies.";

        if (prompt.AssistEnabled)
            ClaudeButton.Visibility = System.Windows.Visibility.Visible;
        else
            ClaudeHint.Visibility = System.Windows.Visibility.Visible;
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Finish(GroupDecision.SkipGrouped);
    private void Abort_Click(object sender, RoutedEventArgs e) => Finish(GroupDecision.Abort);
    private void Claude_Click(object sender, RoutedEventArgs e) => Finish(GroupDecision.ClaudeHandle);

    private void Finish(GroupDecision decision)
    {
        Decision = decision;
        DialogResult = true;
        Close();
    }

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
        Set("Line", "#4A4A4D");
        Set("Accent", "#4C9DE0");
        Set("InfoBg", "#23323F");
        Set("AccentLine", "#3E6E9E");
    }
}
