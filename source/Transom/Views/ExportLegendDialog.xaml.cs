using System.Windows;

namespace Transom.Views;

/// <summary>The Export tab's "More information" dialog — the full technical story behind the cell-color
/// legend. Copy source of truth: docs/design-notes/export-legend-copy.md.</summary>
public partial class ExportLegendDialog : Window
{
    public ExportLegendDialog()
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
        Set("Surface", "#3A3A3D");
        Set("Text", "#E8E8E8");
        Set("Muted", "#B4B4B4");
        Set("Line", "#4A4A4D");
        Set("Accent", "#4C9DE0");
        Set("InfoBg", "#23323F");
        Set("AccentLine", "#3E6E9E");
        // Brightened legend term colours so they stay legible on the dark background (same values as TransomView).
        Set("LegendGreen", "#3FB36A");
        Set("LegendYellow", "#D8A24A");
        Set("LegendRed", "#E8736B");
    }
}
