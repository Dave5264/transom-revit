using System.Windows;

namespace Transom.Views;

/// <summary>
///     Opened by the Settings tab's "Show me what you can do" button. Points brand-new users at the demo
///     export: a .md that makes Claude Code connect to Revit, wait for the word, then build a small shed
///     so they can watch the bridge work without inventing a first command. DataContext is the
///     TransomViewModel (for ExportShowcaseGuideCommand).
/// </summary>
public partial class ShowcaseDialog : Window
{
    public ShowcaseDialog()
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
