using System;
using System.IO;

namespace Morphonic;

// The file the user runs. Usually the binary itself; from an AppImage, the
// .AppImage: the binary then sits in a read-only mount (/tmp/.mount_…)
// that the AppImage runtime removes when the app exits, so relaunching,
// updating or installing must name the .AppImage, never the binary inside.
//
// $APPIMAGE is believed only when this process really runs from the mount
// it describes ($APPDIR): a plain binary started from a terminal that is
// itself an AppImage inherits that terminal's $APPIMAGE.
public static class AppFile
{
    public static string? AppImage => FromAppImage(Environment.GetEnvironmentVariable("APPIMAGE"),
        Environment.GetEnvironmentVariable("APPDIR"), Environment.ProcessPath);

    public static bool IsAppImage => AppImage != null;

    public static string? Current => AppImage ?? Environment.ProcessPath;

    internal static string? FromAppImage(string? appImage, string? appDir, string? processPath)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(appImage) || string.IsNullOrEmpty(appDir) || string.IsNullOrEmpty(processPath)) return null;
        try
        {
            var dir = Path.GetFullPath(appDir).TrimEnd('/') + "/";
            if (!Path.GetFullPath(processPath).StartsWith(dir, StringComparison.Ordinal)) return null;
            return File.Exists(appImage) ? Path.GetFullPath(appImage) : null;
        }
        catch { return null; }
    }
}
