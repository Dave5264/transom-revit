using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Transom.Views;

namespace Transom.Aire.App;

/// <summary>
///     Standalone AIRE. Hosts the very same <see cref="AireView"/> the Revit ribbon opens — AIRE only ever
///     touched image files and the OpenAI API, never the Revit model, so with the window's code in the
///     Revit-free Transom.Aire assembly it needs no Revit at all.
///     <para>
///     Single instance: AIRE spends real money and keeps its settings in one file, so a second window would
///     be two views of one API key arguing about the same queue. A second launch brings the existing window
///     forward instead. Note this is a separate guarantee from AireJobManager's cross-process spend lock,
///     which stops Revit's AIRE and this app from running batches at the same time.
///     </para>
/// </summary>
internal static class Program
{
    private const string SingleInstanceMutex = "Transom.Aire.Standalone.SingleInstance";
    private static readonly uint ShowExistingWindow = RegisterWindowMessage("TransomAireShowExistingWindow");

    [STAThread]
    private static int Main()
    {
        // Held for the life of the process; the OS drops it if we are killed, so a crash cannot lock the
        // user out of their own app.
        using var single = new Mutex(true, SingleInstanceMutex, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Broadcast rather than hunt for a window handle: the running instance recognises its own
            // registered message, and nothing else on the desktop knows the id.
            PostMessage(HwndBroadcast, ShowExistingWindow, IntPtr.Zero, IntPtr.Zero);
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new AireView();
        app.MainWindow = window;
        window.SourceInitialized += (_, _) => ListenForSecondLaunch(window);
        window.Show();
        return app.Run();
    }

    /// <summary>Restores and fronts the window when a second launch asks for it.</summary>
    private static void ListenForSecondLaunch(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source) return;
        source.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
        {
            if ((uint)msg != ShowExistingWindow) return IntPtr.Zero;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            // Windows only lets the foreground process steal focus; a brief Topmost flip is the standard
            // way to surface reliably without leaving the window pinned above everything.
            window.Topmost = true;
            window.Topmost = false;
            handled = true;
            return IntPtr.Zero;
        });
    }

    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
