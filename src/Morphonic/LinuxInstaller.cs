using System;
using System.IO;
using System.Text;

namespace Morphonic;

// The single Linux binary installs itself — no package, no root, no
// script: a copy of the running file goes to ~/.local/share/Morphonic/app, an
// app-grid entry to ~/.local/share/applications, the icon into the hicolor
// theme. Reached as "Morphonic --install" and from Settings. Uninstall undoes
// exactly that; --purge also removes the data folder (voices included) and
// the has-run marker.
public static class LinuxInstaller
{
    public static string AppDir => Path.Combine(LinuxHost.DataHome, "Morphonic", "app");
    public static string InstalledBinary => Path.Combine(AppDir, "Morphonic");
    public static string DesktopFile => Path.Combine(LinuxHost.DataHome, "applications", "morphonic.desktop");
    private static string IconRoot => Path.Combine(LinuxHost.DataHome, "icons", "hicolor");
    private static string SvgIcon => Path.Combine(IconRoot, "scalable", "apps", "morphonic.svg");
    private static string PngIcon => Path.Combine(IconRoot, "256x256", "apps", "morphonic.png");

    public sealed record Result(bool Ok, string Message);

    public static string? InstalledAt()
    {
        try { return File.Exists(DesktopFile) && File.Exists(InstalledBinary) ? InstalledBinary : null; }
        catch { return null; }
    }

    public static bool RunningFromAppDir
    {
        get
        {
            try
            {
                var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
                return dir != null && Path.GetFullPath(dir) == Path.GetFullPath(AppDir);
            }
            catch { return false; }
        }
    }

    private const UnixFileMode Executable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public static Result Install()
    {
        if (!OperatingSystem.IsLinux()) return new Result(false, "the app-grid install is only available on Linux");
        try
        {
            var source = Environment.ProcessPath;
            if (string.IsNullOrEmpty(source) || !File.Exists(source))
                return new Result(false, "cannot find the running binary to copy");
            Directory.CreateDirectory(AppDir);
            if (!RunningFromAppDir)
            {
                // Write beside, then rename over: a running binary can't be
                // opened for writing, but its directory entry can be replaced.
                var tmp = InstalledBinary + ".tmp";
                File.Copy(source, tmp, overwrite: true);
                File.SetUnixFileMode(tmp, Executable);
                File.Move(tmp, InstalledBinary, overwrite: true);
            }
            else
            {
                File.SetUnixFileMode(InstalledBinary, Executable);
            }
            WriteResource("install/morphonic.svg", SvgIcon);
            WriteResource("ui/app.png", PngIcon);
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
            File.WriteAllText(DesktopFile, DesktopEntry(InstalledBinary));
            LinuxHost.Capture("update-desktop-database", new[] { Path.GetDirectoryName(DesktopFile)! });
            LinuxHost.Capture("gtk-update-icon-cache", new[] { "-q", "-t", "-f", "--ignore-theme-index", IconRoot });
            return new Result(true, $"Morphonic is in your app grid (installed to {InstalledBinary}).");
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("LinuxInstaller.Install", ex);
            return new Result(false, "install failed: " + ex.Message);
        }
    }

    public static Result Uninstall(bool purge)
    {
        if (!OperatingSystem.IsLinux()) return new Result(false, "only available on Linux");
        try
        {
            TryDelete(DesktopFile);
            TryDelete(SvgIcon);
            TryDelete(PngIcon);
            if (Directory.Exists(AppDir)) Directory.Delete(AppDir, recursive: true);
            LinuxHost.Capture("update-desktop-database", new[] { Path.GetDirectoryName(DesktopFile)! });
            var data = Path.Combine(LinuxHost.DataHome, "Morphonic");
            if (!purge)
                return new Result(true, $"Removed Morphonic from the app grid. Your settings, voices and models are still in {data} " +
                                        "(--uninstall --purge removes them too).");
            if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
            var marker = Path.Combine(LinuxHost.ConfigHome, "Morphonic");
            if (Directory.Exists(marker)) Directory.Delete(marker, recursive: true);
            // WebKitGTK keeps the page's storage and cache under the binary's
            // own name (the download's file name, or "Morphonic" once installed).
            foreach (var name in PurgeNames(Path.GetFileName(Environment.ProcessPath ?? "")))
            {
                foreach (var dir in new[] { Path.Combine(LinuxHost.DataHome, name), Path.Combine(LinuxHost.CacheHome, name) })
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
            }
            return new Result(true, "Removed Morphonic and all of its data.");
        }
        catch (Exception ex)
        {
            return new Result(false, "uninstall failed: " + ex.Message);
        }
    }

    // The folder names --purge deletes under ~/.local/share and ~/.cache:
    // "Morphonic", and the running binary's own file name only while it
    // still begins with "Morphonic" (every released file does). A binary
    // someone renamed could carry the name of another program's folder,
    // and that folder is not ours to delete; its few MB of page cache stay.
    internal static IReadOnlyList<string> PurgeNames(string binaryName)
    {
        var names = new List<string> { "Morphonic" };
        if (binaryName.StartsWith("Morphonic", StringComparison.OrdinalIgnoreCase) &&
            binaryName.IndexOfAny(new[] { '/', '\\' }) < 0 &&
            !names.Contains(binaryName))
            names.Add(binaryName);
        return names;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void WriteResource(string name, string dest)
    {
        using var src = typeof(LinuxInstaller).Assembly.GetManifestResourceStream(name);
        if (src == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var dst = File.Create(dest);
        src.CopyTo(dst);
    }

    internal static string DesktopEntry(string execPath) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=Morphonic\n" +
        "Comment=Real-time RVC voice changer, running on your own PC\n" +
        "Exec=" + ExecQuote(execPath) + "\n" +
        "Icon=morphonic\n" +
        "Terminal=false\n" +
        "StartupWMClass=Morphonic\n" +
        "Categories=AudioVideo;Audio;\n" +
        "Keywords=voice;changer;RVC;microphone;\n" +
        "StartupNotify=false\n";

    // Desktop-entry quoting: double quotes around a path with reserved
    // characters, backslash-escaping the quote, backslash, dollar and
    // backtick inside; a literal percent is written as %% (field codes).
    internal static string ExecQuote(string path)
    {
        if (path.IndexOfAny(new[] { ' ', '\t', '"', '\\', '$', '`', '\'', '%' }) < 0) return path;
        var sb = new StringBuilder("\"");
        foreach (var c in path)
        {
            if (c is '\\' or '"' or '$' or '`') sb.Append('\\');
            if (c == '%') sb.Append('%');
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
