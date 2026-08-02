using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Transom.Views;

/// <summary>
///     A reusable result/error dialog with a <b>Copy details</b> button, so users can copy the full
///     diagnostic text (paths, what was checked, messages, exceptions) to paste into a bug report —
///     something a Revit <c>TaskDialog</c> can't do. Code-only WPF (no XAML resource), owned by the Hub
///     when it is open and by the Revit main window otherwise. The copied text includes a header
///     (Transom version + timestamp + title) so it stands alone as a report.
/// </summary>
public static class ReportDialog
{
    public static void Show(string title, string heading, string details, bool isError = false)
    {
        var report = BuildReport(title, heading, details);

        // UI-03: this was the only window in Views/ with no theming — it hardcoded Brushes.Black and kept WPF's
        // default light chrome, so in a dark Revit an operation that failed popped a bright white dialog among
        // otherwise-dark windows. Same palette values as every other dialog's ApplyTheme().
        bool dark;
        try { dark = Autodesk.Revit.UI.UIThemeManager.CurrentTheme == Autodesk.Revit.UI.UITheme.Dark; }
        catch { dark = false; }

        // Fully qualified: Autodesk.Revit.DB.Color is also in scope across this project.
        Brush Themed(string darkHex, string lightHex) =>
            new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(dark ? darkHex : lightHex));

        var bg = Themed("#2D2D30", "#FFFFFF");
        var surface = Themed("#3A3A3D", "#FFFFFF");
        var text = Themed("#E8E8E8", "#26251F");
        var line = Themed("#4A4A4D", "#E3E1D9");
        var error = Themed("#E8736B", "#B22222");   // Firebrick in light, brightened for the dark background

        var headingBlock = new TextBlock
        {
            Text = heading,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(14, 14, 14, 8),
            MaxWidth = 660,
            Foreground = isError ? error : text,
        };
        var box = new TextBox
        {
            Text = details,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Margin = new Thickness(14, 0, 14, 10),
            MinWidth = 580,
            MaxWidth = 660,
            MaxHeight = 340,
            Padding = new Thickness(6),
            Background = surface,
            Foreground = text,
            BorderBrush = line,
        };
        var copy = new Button { Content = CopyLabel, Width = 110, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(6, 2, 6, 2) };
        var close = new Button { Content = "Close", Width = 80, IsCancel = true, IsDefault = true, Padding = new Thickness(6, 2, 6, 2) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 0, 14, 14) };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var root = new StackPanel();
        root.Children.Add(headingBlock);
        root.Children.Add(box);
        root.Children.Add(buttons);

        var win = new Window
        {
            Title = title,
            Content = root,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            Background = bg,
            Foreground = text,
            FontFamily = new FontFamily("Segoe UI"),
        };

        // UI-03: prefer the Hub as owner when it is open — three of the five call sites are the Hub's own
        // viewmodel, and a real WPF Owner is what makes CenterOwner work (WindowInteropHelper.Owner sets only
        // the native owner HWND, which WPF's CenterOwner does not consult — the same trap StartupCommand
        // documents). Fall back to the process main window, which is what this dialog always used.
        var hub = TransomView.Instance;
        if (hub != null && hub.IsLoaded)
        {
            win.Owner = hub;
            win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            try
            {
                new System.Windows.Interop.WindowInteropHelper(win)
                { Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle };
            }
            catch { /* owner is best-effort */ }
        }

        // The label used to latch on "Copied ✓", so a second copy gave no feedback at all. Revert it shortly
        // after, so every click confirms.
        var revert = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        revert.Tick += (_, _) => { revert.Stop(); copy.Content = CopyLabel; };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(report); copy.Content = "Copied ✓"; }
            catch { copy.Content = "Copy failed"; }
            revert.Stop();
            revert.Start();
        };
        close.Click += (_, _) => win.Close();
        win.Closed += (_, _) => revert.Stop();

        win.ShowDialog();
    }

    private const string CopyLabel = "Copy details";

    private static string BuildReport(string title, string heading, string details)
    {
        string version;
        try { version = Transom.Core.AppInfo.Version; } catch { version = "?"; }
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return $"Transom {version} · {stamp}\n{title}\n\n{heading}\n\n{details}";
    }
}
