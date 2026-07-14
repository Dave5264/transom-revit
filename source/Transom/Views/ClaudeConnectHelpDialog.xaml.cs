using System.Windows;

namespace Transom.Views;

/// <summary>
///     Opened by the "?" next to a red "Claude app running" status row. The process check can miss a live
///     Claude Code session (terminal / VS Code / node hosts), so this dialog points the user at the Claude
///     Assist guide export — a running Claude session can read it, test the bridge itself, and walk the
///     user through connecting. DataContext is the TransomViewModel (for ExportClaudeAssistGuideCommand).
/// </summary>
public partial class ClaudeConnectHelpDialog : Window
{
    public ClaudeConnectHelpDialog()
    {
        InitializeComponent();
        ApplyTheme();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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
        Set("Text", "#E8E8E8");
        Set("Muted", "#B4B4B4");
        Set("Line", "#4A4A4D");
        Set("Accent", "#4C9DE0");
        Set("InfoBg", "#23323F");
        Set("AccentLine", "#3E6E9E");
    }
}
