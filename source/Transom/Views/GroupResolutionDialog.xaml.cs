using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Transom.Core;

namespace Transom.Views;

/// <summary>
///     Per-parameter group-conflict picker. Shown once for each distinct blue (project/shared) or yellow
///     (built-in) column an import touches inside Revit groups, presenting the resolution paths with their
///     pros/cons. Mirrors <see cref="ConflictDialog"/>'s theming. <see cref="Result"/> is the chosen
///     <see cref="GroupResolution"/>, or null when the user cancels the whole import.
/// </summary>
public partial class GroupResolutionDialog : Window
{
    private readonly List<(RadioButton rb, GroupResolution res)> _map = new();

    public GroupResolution? Result { get; private set; }

    public GroupResolutionDialog(GroupResolutionPrompt p)
    {
        InitializeComponent();
        ApplyTheme();

        var kind = p.IsBuiltin ? "built-in" : "project/shared";
        var groups = string.Join(", ", p.GroupNames);
        HeadingText.Text = $"Group conflict — Column / parameter: “{p.Field}”";
        SubText.Text = $"{p.InstanceCount} edit(s) to “{p.Field}” target {kind} parameters on members of " +
                       $"group(s): {groups}. Group members can’t be edited directly — choose how to resolve this column:";

        var unavailable = new List<string>();

        // BLUE only — built-in params can't vary by instance, so option 1 is omitted for yellow.
        if (!p.IsBuiltin)
            AddOption(GroupResolution.Vary,
                "1.  Flip the parameter to “can vary by group instance” so values can be edited",
                "Fast and reduces data duplication.",
                "Schedule will display <varies> if even one instance is off.");

        // Option 2 — only when the column's values are consistent per type (a type param holds one value per type).
        if (p.Option2Available)
            AddOption(GroupResolution.NewTypeParam,
                "2.  Put the values into a new type parameter and update all affected schedules to display it",
                "Best flexibility moving forward — no more group conflicts.",
                "Duplicate parameter data may cause confusion.  (Only schedules being imported are changed.)");
        else
            unavailable.Add("• Option 2 (new type parameter) is unavailable here: values differ between instances " +
                            "of the same type, so they can’t live in a single type parameter.");

        // Option 3 is performed automatically by Transom (GroupDanceApplier) — available regardless of Claude.
        AddOption(GroupResolution.GroupDance,
            "3.  The group dance (automatic): Transom ungroups one instance, edits, regroups, repoints the siblings, renames, and purges the old type",
            "Keeps parameters intact; each group is verified, with full rollback if anything goes wrong.",
            "Batch group actions are inherently risky — Transom refuses (skips) unsafe groups: attached detail, nested groups, excluded/varying members, single-instance, or read-only params.");

        // Option 4 — only when Claude-assist is on.
        if (p.AssistEnabled)
            AddOption(GroupResolution.ClaudeAssist,
                "4.  Claude-Assist: update manually the old-fashioned way",
                "No BIM configuration or strategy changes.",
                "Slow.  Transom launches ClickHelper; Claude opens each group, edits, verifies, finishes, and hands back a report.");
        else
            unavailable.Add("• Option 4 (Claude-Assist) is unavailable: set Claude mode to “Assist (write)” to enable it.");

        AddOption(GroupResolution.Skip, "5.  Skip — leave this column unchanged", "", "");

        // Default to the SAFE option (Skip) so a fast "Apply choice" click never silently mutates the model;
        // the user must deliberately pick a resolution path.
        RadioButton? def = null;
        foreach (var (rb, res) in _map) if (res == GroupResolution.Skip) { def = rb; break; }
        if (def == null && _map.Count > 0) def = _map[0].rb;
        if (def != null) def.IsChecked = true;

        if (unavailable.Count > 0)
        {
            UnavailableNote.Text = string.Join("\n", unavailable);
            UnavailableNote.Visibility = System.Windows.Visibility.Visible;
        }
    }

    private void AddOption(GroupResolution res, string title, string pros, string cons)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };

        var rb = new RadioButton
        {
            Content = title,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)Resources["Text"],
        };
        _map.Add((rb, res));
        panel.Children.Add(rb);

        var indent = new Thickness(22, 2, 0, 0);
        if (pros.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = "Pros:  " + pros, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Resources["Pros"], Margin = indent,
            });
        if (cons.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = "Cons:  " + cons, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Resources["Cons"], Margin = new Thickness(22, 1, 0, 0),
            });

        OptionsPanel.Children.Add(panel);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (rb, res) in _map)
            if (rb.IsChecked == true) { Result = res; break; }
        // "Apply choice" must never be read as a cancel: if somehow nothing is selected, treat it as Skip
        // (only the Cancel button returns null = cancel the import).
        Result ??= GroupResolution.Skip;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = null;   // null = cancel the whole import
        DialogResult = false;
        Close();
    }

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
        Set("Pros", "#7FBF86");
        Set("Cons", "#E0A458");
        Set("InfoBg", "#243B53");
    }
}
