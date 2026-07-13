using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Transom.Core;

namespace Transom.Views;

/// <summary>
///     Option-2 follow-up 1 (user request 2026-07-12): after the user commits a 2a/2b conversion, lists the
///     OTHER schedules that display the same source parameter (found by the build-time scan) so the user can
///     tick the ones whose column should be replaced in the same conversion. Mirrors
///     <see cref="GroupResolutionDialog"/>'s theming. <see cref="Result"/> is the ticked schedule uids, or
///     null when the window is closed without Continue (treated as "replace nowhere else").
/// </summary>
public partial class Option2SchedulesDialog : Window
{
    private readonly List<(CheckBox cb, string uid)> _rows = new();

    public List<string>? Result { get; private set; }

    public Option2SchedulesDialog(Option2SchedulesPrompt p)
    {
        InitializeComponent();
        ApplyTheme();

        string newName = string.IsNullOrWhiteSpace(p.NewParamName) ? $"{p.Field} (Transom)" : p.NewParamName;
        HeadingText.Text = $"Also replace “{p.Field}” in other schedules?";
        SubText.Text = $"{p.Schedules.Count} other schedule(s) also display “{p.Field}”. Ticked schedules get the " +
                       $"same conversion: their column is repointed to the new parameter “{newName}” and each " +
                       "element's current value is carried over.";

        foreach (var s in p.Schedules)
        {
            var cb = new CheckBox { Content = s.ScheduleName, IsChecked = false };
            _rows.Add((cb, s.ScheduleUid));
            ListPanel.Children.Add(cb);
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        var ticked = new List<string>();
        foreach (var (cb, uid) in _rows)
            if (cb.IsChecked == true)
                ticked.Add(uid);
        Result = ticked;
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
    }
}
