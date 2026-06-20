using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Transom.Core;

namespace Transom.Views;

public partial class ConflictDialog : Window
{
    private readonly List<(RadioButton rb, ConflictOption opt)> _map = new();

    public ConflictOption? Result { get; private set; }

    public ConflictDialog(TypeConflict c)
    {
        InitializeComponent();
        ApplyTheme();

        HeadingText.Text = $"“{c.Field}” conflict on type ‘{c.TypeName}’";
        SubText.Text = $"Different values were entered for this type parameter, which applies to all " +
                       $"{c.InstancesAffected} instance(s) of the type. Current value: “{c.CurrentDisplay}”. " +
                       "Choose the value to apply:";

        var first = true;
        foreach (var opt in c.Options)
        {
            // Label the user's typed value(s) "(entered value)" so they can tell their own input apart from the
            // type's current value (e.g. "2.5  (entered value)" vs "3'-0"").
            var entered = opt.IsEntered ? "   (entered value)" : "";
            var rb = new RadioButton
            {
                Content = opt.Parseable ? $"{opt.Display}{entered}" : $"{opt.Display}{entered}   (can't parse — unavailable)",
                IsEnabled = opt.Parseable,
                Margin = new Thickness(0, 4, 0, 4),
                IsChecked = first && opt.Parseable,
            };
            if (first && opt.Parseable) first = false;
            _map.Add((rb, opt));
            OptionsPanel.Children.Add(rb);
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (rb, opt) in _map)
            if (rb.IsChecked == true) { Result = opt; break; }
        DialogResult = true;
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
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
    }
}
