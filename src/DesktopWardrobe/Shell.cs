using System;
using System.Diagnostics;

namespace DesktopWardrobe;

internal static class Shell
{
    public static void Open(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public static void ShowInExplorer(string path) =>
        Process.Start("explorer.exe", $"/select,\"{path}\"");

    /// <summary>
    /// Pins a folder to Explorer's Quick access, which also lists it in every Open/Upload dialog.
    /// Does nothing if it is already pinned.
    /// </summary>
    public static void PinToQuickAccess(string folder)
    {
        Type shellType = Type.GetTypeFromProgID("Shell.Application")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        shell.Namespace(folder).Self.InvokeVerb("pintohome");
    }
}
