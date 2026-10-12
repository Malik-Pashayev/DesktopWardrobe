using System;
using System.Threading;
using System.Windows;

namespace DesktopWardrobe;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\DesktopWardrobe.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
            return;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var wardrobe = new Wardrobe();
        using var tray = new TrayIcon(wardrobe, app.Shutdown);
        wardrobe.Error += tray.ShowError;

        new RackWindow(wardrobe).Show();
        app.Run();
    }
}
