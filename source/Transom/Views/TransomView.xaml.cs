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

        // Ask how to handle edits that target group members (skip / abort / hand to Claude).
        viewModel.GroupResolver = prompt =>
        {
            var dlg = new GroupConflictDialog(prompt) { Owner = this };
            dlg.ShowDialog();
            return dlg.Decision;
        };
    }

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e) => Close();

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
        Set("Hint", "#8C8C8C");
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
    }
}
