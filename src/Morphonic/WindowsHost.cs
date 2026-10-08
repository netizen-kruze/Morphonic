using System;
using System.Runtime.InteropServices;

namespace Morphonic;

// The few Windows-only pieces the host needs before a window exists: a
// message box, the WebView2 runtime check, and the single-instance mutex.
public static class WindowsHost
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    // A WinExe has no console of its own; the terminal switches (--bench,
    // --install-gpu, --help) borrow the parent's so their output shows.
    public static void AttachParentConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { AttachConsole(-1); } catch { }
    }

    public const uint MB_OK = 0, MB_YESNO = 0x4, MB_ICONERROR = 0x10, MB_ICONWARNING = 0x30, MB_ICONINFORMATION = 0x40;
    public const int IDYES = 6;

    public static int MessageBox(string text, uint type)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine(text); return 0; }
        try { return MessageBoxW(IntPtr.Zero, text, "Morphonic", type); } catch { return 0; }
    }

    // Photino needs the WebView2 Evergreen Runtime (preinstalled on Win11
    // and current Win10; a fresh machine without it gets a blank window).
    // The runtime registers its client key with a "pv" version.
    public static bool WebView2Present()
    {
        if (!OperatingSystem.IsWindows()) return true;
        var keys = new[]
        {
            @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        };
        foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        foreach (var key in keys)
        {
            try
            {
                using var rk = root.OpenSubKey(key);
                if (rk?.GetValue("pv") is string pv && pv.Length > 0 && pv != "0.0.0.0") return true;
            }
            catch { }
        }
        return false;
    }

    public static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { ErrorLog.WriteEntry("OpenUrl", ex); }
    }
}
