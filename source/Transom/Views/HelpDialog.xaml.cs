using System.Windows;
using Transom.Core;

namespace Transom.Views;

public partial class HelpDialog : Window
{
    public HelpDialog()
    {
        InitializeComponent();
        ApplyTheme();
        VersionText.Text = "Transom v" + AppInfo.Version;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Issues_Click(object sender, RoutedEventArgs e) => Open(AppInfo.IssuesUrl);
    private void Releases_Click(object sender, RoutedEventArgs e) => Open(AppInfo.ReleasesUrl);

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* nothing to open */ }
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
        Set("Hint", "#8C8C8C");
        Set("Line", "#4A4A4D");
        Set("Accent", "#4C9DE0");
        Set("InfoBg", "#23323F");
        Set("AccentLine", "#3E6E9E");
        Set("WarnBg", "#3A3320");
        Set("WarnLine", "#6E5E2E");
        Set("Warn", "#D9B65A");
    }
}
