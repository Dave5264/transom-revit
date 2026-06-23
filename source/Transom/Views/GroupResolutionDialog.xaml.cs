using System.Collections.Generic;
using System.Linq;
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

    // #97: the prompt (so Apply_Click can write the confirmed name back) + the inline new-param-name controls,
    // shown ONLY while a new-parameter option (2a TYPE / 2b INSTANCE) is the selected radio.
    private readonly GroupResolutionPrompt _prompt;
    private StackPanel? _namePanel;          // label + box + error, toggled live with the 2a/2b selection
    private TextBox? _nameBox;
    private TextBlock? _nameError;

    public GroupResolution? Result { get; private set; }

    public GroupResolutionDialog(GroupResolutionPrompt p)
    {
        _prompt = p;
        InitializeComponent();
        ApplyTheme();

        var kind = p.IsBuiltin ? "built-in" : "project/shared";
        var groups = string.Join(", ", p.GroupNames);
        // #93B: when the schedule column was given a custom heading that differs from the underlying parameter's real
        // name, show BOTH so the user knows which actual parameter this resolves (a renamed column otherwise hides it).
        bool customHeader = !string.IsNullOrEmpty(p.Header) && p.Header != p.Field;
        HeadingText.Text = customHeader
            ? $"Group conflict — Column “{p.Header}” (parameter: {p.Field})"
            : $"Group conflict — Column / parameter: “{p.Field}”";
        SubText.Text = $"{p.InstanceCount} edit(s) to “{p.Field}” target {kind} parameters on members of " +
                       $"group(s): {groups}. Group members can’t be edited directly — choose how to resolve this column:";

        var unavailable = new List<string>();

        // BLUE only — built-in params can't vary by instance, so option 1 is omitted for yellow.
        if (!p.IsBuiltin)
            AddOption(GroupResolution.Vary,
                "1.  Flip the parameter to “can vary by group instance” so values can be edited",
                "Fast and reduces data duplication.",
                "Schedule will display <varies> if even one instance is off.");

        // Option 2 — REPLACE the source column with one new parameter (the original values merged with the
        // user's edits). The binding (type vs instance) is inferred at build time and carried in Option2Mode;
        // when ambiguous we offer BOTH (type first), with an explanatory note on the inferred binding.
        switch (p.Option2Mode)
        {
            case Option2Mode.AutoType:
                // Label "2a" (not a bare "2") so the suffix convention is consistent everywhere a choice is shown:
                // every type-parameter choice is "2a", every instance-parameter choice is "2b" — no unsuffixed "2".
                // (Here only the type option is offered, since the binding is unambiguously type.)
                AddOption(GroupResolution.NewTypeParam,
                    "2a.  Replace the column with a new type parameter",
                    "Moves the existing values AND your edits into one type parameter — one column, original data preserved, no more group conflicts.",
                    "Stored as a new shared parameter (the column keeps its heading).");
                break;

            case Option2Mode.AmbiguousPreferType:
                // Both offered → label them per the user convention: "2a" = type, "2b" = instance. (User-directed
                // 2026-06-22: no "Recommended" tag on any option — the type option is listed first but not marked.)
                AddOption(GroupResolution.NewTypeParam,
                    "2a.  Replace the column with a new type parameter",
                    "Moves the existing values AND your edits into one type parameter — one column, original data preserved, no more group conflicts.",
                    "Stored as a new shared parameter (the column keeps its heading).");
                // TASK #93A: offer the new-INSTANCE-parameter path (2b) ONLY when option 1 (Vary) is NOT available —
                // i.e. for a BUILT-IN param. For a project/shared (blue) cell, Vary already preserves per-instance
                // values without adding a parameter, so 2b is redundant and is suppressed (use option 1 instead).
                if (p.IsBuiltin)
                    AddOption(GroupResolution.NewInstanceParam,
                        "2b.  Replace the column with a new instance parameter",
                        "Moves the existing values AND your edits into one instance parameter — preserves each element's own value.",
                        "Stored as a new shared parameter (the column keeps its heading).");
                AddNote(p.BindingNote);
                break;

            case Option2Mode.AmbiguousPreferInstance:
                // This mode means the schedule is ITEMIZED BY INSTANCE (Importer.ComputeOption2Mode sets it when
                // !organizedByType). Per-instance values can differ, so a single TYPE parameter can't preserve them —
                // the apply path's ColumnRejectedItemized would reject a type conversion outright. So DON'T offer the
                // type option here at all (it was a guaranteed-to-fail choice, and — before this — it was even
                // mislabeled "2b", so picking "2b" ran the doomed type path → 0 applied/no write). Offer ONLY the
                // new-INSTANCE-parameter path ("2b", per the user convention 2/2a=type, 2b=instance). User-confirmed
                // 2026-06-18: an itemized-by-instance schedule must not present the type-parameter conversion.
                // TASK #93A: 2b ONLY when Vary is not available (built-in). A project/shared (blue) cell in this mode
                // is resolved by option 1 (Vary), which preserves each instance's own value without adding a parameter
                // — so 2b is suppressed and a one-line note points the user to option 1.
                if (p.IsBuiltin)
                    AddOption(GroupResolution.NewInstanceParam,
                        "2b.  Replace the column with a new instance parameter",
                        "Moves the existing values AND your edits into one instance parameter — preserves each element's own value.",
                        "Stored as a new shared parameter (the column keeps its heading).");
                else
                    AddNote("Per-instance values vary across these elements → use option 1 (Vary), which preserves each element's own value.");
                break;

            default: // None — option 2 doesn't apply to this column
                unavailable.Add(p.IsGeometryDriven
                    ? "• Option 2 (new parameter) is unavailable here: this is a geometry-driving parameter " +
                      "(e.g. sill/head height), so replacing it would desync the schedule from the 3D model. " +
                      "Edit it in Revit's Edit Group mode, or use Claude-Assist."
                    : "• Option 2 (new parameter) is unavailable here: values differ between instances " +
                      "of the same type, so they can’t live in a single type parameter.");
                break;
        }

        // A grouped built-in that can't be written in place: when option 2 isn't available and Claude-Assist is off,
        // the only resolution left is Skip — say so honestly (no in-place write path exists for it here). Wording
        // owned by ux1; keep it free of any removed-feature reference.
        if (p.IsBroken && p.Option2Mode == Option2Mode.None && !p.AssistEnabled)
            unavailable.Add("• This grouped built-in can't be written directly here. Convert via option 2 if its "
                            + "values are uniform per type, enable Claude-Assist, or edit it in Revit's Edit Group mode.");

        // Option 3 — only when Claude-assist is on.
        if (p.AssistEnabled)
            AddOption(GroupResolution.ClaudeAssist,
                "3.  Claude-Assist: update manually the old-fashioned way",
                "No BIM configuration or strategy changes.",
                "Slow.  Transom launches ClickHelper; Claude opens each group, edits, verifies, finishes, and hands back a report.");
        else
            unavailable.Add("• Option 3 (Claude-Assist) is unavailable: set Claude mode to “Assist (write)” to enable it.");

        AddOption(GroupResolution.Skip, "4.  Skip — leave this column unchanged", "", "");

        // #97: inline new-parameter-NAME editor — shown ONLY when a new-param option (2a TYPE / 2b INSTANCE) is the
        // selected radio (HIDDEN for Vary/Claude-Assist/Skip, which create no parameter). Built once here and toggled
        // live by the 2a/2b radios' Checked/Unchecked below. Pre-filled with the default suggestion "<field> (Transom)".
        BuildNamePanel(p);

        // Default to the SAFE option (Skip) so a fast "Apply choice" click never silently mutates the model;
        // the user must deliberately pick a resolution path.
        RadioButton? def = null;
        foreach (var (rb, res) in _map) if (res == GroupResolution.Skip) { def = rb; break; }
        if (def == null && _map.Count > 0) def = _map[0].rb;
        if (def != null) def.IsChecked = true;

        // Wire visibility AFTER setting the default so the initial state reflects the default radio (Skip → hidden).
        UpdateNameVisibility();

        if (unavailable.Count > 0)
        {
            UnavailableNote.Text = string.Join("\n", unavailable);
            UnavailableNote.Visibility = System.Windows.Visibility.Visible;
        }
    }

    /// <summary>#97: True iff a new-parameter option (2a TYPE / 2b INSTANCE) is the currently-selected radio.</summary>
    private bool NewParamSelected() =>
        _map.Any(m => m.rb.IsChecked == true
                      && m.res is GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam);

    /// <summary>#97: builds the (initially hidden) "New parameter name" editor: a label, an editable box pre-filled
    /// with the default suggestion, and an inline error line for validation failures. Appended under the options.</summary>
    private void BuildNamePanel(GroupResolutionPrompt p)
    {
        _namePanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = System.Windows.Visibility.Collapsed };
        _namePanel.Children.Add(new TextBlock
        {
            Text = "New parameter name:",
            FontWeight = FontWeights.Medium, FontSize = 12,
            Foreground = (Brush)Resources["Text"], Margin = new Thickness(0, 0, 0, 3),
        });
        _nameBox = new TextBox
        {
            Text = $"{p.Field} (Transom)",   // default suggestion (matches ApplyNewParam's fallback)
            FontSize = 12, Padding = new Thickness(4, 3, 4, 3),
        };
        _nameBox.TextChanged += (_, _) => { if (_nameError != null) _nameError.Visibility = System.Windows.Visibility.Collapsed; };
        _namePanel.Children.Add(_nameBox);
        _nameError = new TextBlock
        {
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Resources["Cons"], Margin = new Thickness(0, 3, 0, 0),
            Visibility = System.Windows.Visibility.Collapsed,
        };
        _namePanel.Children.Add(_nameError);
        OptionsPanel.Children.Add(_namePanel);
    }

    /// <summary>#97: show the name editor only while a 2a/2b radio is selected; hide it otherwise. Called on every
    /// radio Checked/Unchecked (wired in AddOption) and once after the default selection.</summary>
    private void UpdateNameVisibility()
    {
        if (_namePanel == null) return;
        _namePanel.Visibility = NewParamSelected()
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    private void AddOption(GroupResolution res, string title, string pros, string cons)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };

        var rb = new RadioButton
        {
            Content = title,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)Resources["Text"],
            // Each option lives in its OWN StackPanel, so WPF's implicit "same parent" radio grouping
            // doesn't apply — without a shared GroupName the buttons aren't mutually exclusive (the
            // default Skip selection can't be switched off). An explicit GroupName ties them into one group.
            GroupName = "GroupResolution",
        };
        // #97: any selection change re-evaluates whether the new-param-name editor should show (2a/2b) or hide.
        rb.Checked += (_, _) => UpdateNameVisibility();
        rb.Unchecked += (_, _) => UpdateNameVisibility();
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

    /// <summary>Appends a muted, italic, wrapped note under the options (e.g. the type-vs-instance recommendation rationale).</summary>
    private void AddNote(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        OptionsPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontStyle = FontStyles.Italic,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Resources["Muted"],
            Margin = new Thickness(22, 2, 0, 6),
        });
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        GroupResolution? picked = null;
        foreach (var (rb, res) in _map)
            if (rb.IsChecked == true) { picked = res; break; }
        // "Apply choice" must never be read as a cancel: if somehow nothing is selected, treat it as Skip
        // (only the Cancel button returns null = cancel the import).
        picked ??= GroupResolution.Skip;

        // #97: for a new-parameter option (2a/2b), VALIDATE the entered name before committing — non-empty and free of
        // characters Revit forbids in a (shared) parameter name. On failure, show the inline error and DO NOT close, so
        // the user can fix it. On success, write the confirmed name back onto the prompt for the apply path.
        if (picked is GroupResolution.NewTypeParam or GroupResolution.NewInstanceParam && _nameBox != null)
        {
            var entered = (_nameBox.Text ?? "").Trim();
            if (!IsValidParamName(entered, out var why))
            {
                if (_nameError != null) { _nameError.Text = why; _nameError.Visibility = System.Windows.Visibility.Visible; }
                _nameBox.Focus();
                _nameBox.SelectAll();
                return;   // block OK until the name is valid
            }
            _prompt.ChosenParamName = entered;
        }

        Result = picked;
        DialogResult = true;
        Close();
    }

    /// <summary>#97: validates a user-entered new-parameter name. Rejects empty and the characters Revit disallows in a
    /// (shared) parameter name (F5: <c>{ } [ ] | ; &lt; &gt; ? ` ~ \ : * "</c> plus control chars). Existing-name
    /// collisions are NOT rejected here — EnsureSharedParam reuses a same-spec definition or disambiguates with a "(2)"
    /// suffix, so a collision is handled safely downstream rather than blocking the user.</summary>
    private static bool IsValidParamName(string name, out string why)
    {
        if (string.IsNullOrWhiteSpace(name)) { why = "Enter a name for the new parameter."; return false; }
        const string illegal = "{}[]|;<>?`~\\:*\"";
        foreach (var ch in name)
            if (illegal.IndexOf(ch) >= 0 || char.IsControl(ch))
            {
                why = "The name can't contain { } [ ] | ; < > ? ` ~ \\ : * \" or control characters.";
                return false;
            }
        why = "";
        return true;
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
