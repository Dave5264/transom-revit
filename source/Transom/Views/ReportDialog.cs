using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Transom.Views;

/// <summary>
///     A reusable result/error dialog with a <b>Copy details</b> button, so users can copy the full
///     diagnostic text (paths, what was checked, messages, exceptions) to paste into a bug report —
///     something a Revit <c>TaskDialog</c> can't do. Code-only WPF (no XAML resource), owned by the Revit
///     main window. The copied text includes a header (Transom version + timestamp + title) so it stands
///     alone as a report.
/// </summary>
public static class ReportDialog
{
    public static void Show(string title, string heading, string details, bool isError = false)
    {
        var report = BuildReport(title, heading, details);

        var headingBlock = new TextBlock
        {
            Text = heading,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(14, 14, 14, 8),
            MaxWidth = 660,
            Foreground = isError ? Brushes.Firebrick : Brushes.Black,
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
        };
        var copy = new Button { Content = "Copy details", Width = 110, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(6, 2, 6, 2) };
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
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.CanResizeWithGrip,
        };

        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(report); copy.Content = "Copied ✓"; }
            catch { copy.Content = "Copy failed"; }
        };
        close.Click += (_, _) => win.Close();

        try
        {
            new System.Windows.Interop.WindowInteropHelper(win)
            { Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle };
        }
        catch { /* owner is best-effort */ }

        win.ShowDialog();
    }

    private static string BuildReport(string title, string heading, string details)
    {
        string version;
        try { version = Transom.Core.AppInfo.Version; } catch { version = "?"; }
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return $"Transom {version} · {stamp}\n{title}\n\n{heading}\n\n{details}";
    }
}
