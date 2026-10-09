using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Morphonic.Audio;

namespace Morphonic;

// One file to send when something is wrong: the logs, the settings, the
// model manifest and a description of the machine, zipped into the data
// folder. Settings → About → "Save a report…" and `Morphonic --report`
// (the latter works even when the window cannot open). Nothing in it
// leaves the machine unless the user sends it.
public static class SupportReport
{
    public static string Write(string version)
    {
        var dir = AppPaths.DataDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"Morphonic-report-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var name in new[] { "last_boot.log", "error.log", "error.log.1", "bench.log", "settings.json", "driver-install.log" })
            AddIfExists(zip, Path.Combine(dir, name), name);
        AddIfExists(zip, Path.Combine(dir, "models", "models_manifest.json"), "models_manifest.json");

        var sb = new StringBuilder();
        sb.AppendLine("Morphonic " + version);
        sb.AppendLine("written:   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("machine:   " + Safe(MachineProfile.Describe));
        sb.AppendLine("exe:       " + Environment.ProcessPath);
        sb.AppendLine("data:      " + dir);
        sb.AppendLine("os:        " + Environment.OSVersion.VersionString + (OperatingSystem.IsLinux() ? " / " + Safe(() => File.Exists("/etc/os-release") ? File.ReadAllText("/etc/os-release").Split('\n').FirstOrDefault(l => l.StartsWith("PRETTY_NAME=")) ?? "" : "") : ""));
        foreach (var env in new[] { "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP", "WAYLAND_DISPLAY", "DISPLAY", "GDK_BACKEND", "WEBKIT_DISABLE_DMABUF_RENDERER", "PULSE_SERVER", "LD_PRELOAD" })
            sb.AppendLine($"{env,-9}  {Environment.GetEnvironmentVariable(env) ?? "(unset)"}");
        sb.AppendLine();
        sb.AppendLine("inputs:");
        foreach (var d in Safe(() => AudioDevices.Inputs().Select(x => "  " + x.Name).ToArray(), Array.Empty<string>())) sb.AppendLine(d);
        sb.AppendLine("outputs:");
        foreach (var d in Safe(() => AudioDevices.Outputs().Select(x => "  " + x.Name).ToArray(), Array.Empty<string>())) sb.AppendLine(d);
        sb.AppendLine();
        sb.AppendLine("virtual mic: " + Safe(() => VirtualMic.Status));
        if (OperatingSystem.IsLinux())
        {
            var missing = Safe(() => LinuxHost.MissingUiDependencies().Select(m => m.Library + " MISSING (dnf install " + m.Package + ")").ToArray(), Array.Empty<string>());
            sb.AppendLine("ui libraries: " + (missing.Length == 0 ? "all present" : string.Join(", ", missing)));
            foreach (var tool in new[] { "pw-record", "pw-play", "pactl", "parec", "pacat", "xdg-open", "notify-send" })
                sb.AppendLine($"  {tool,-12} {Which(tool) ?? "not found"}");
        }
        sb.AppendLine();
        sb.AppendLine("data folder:");
        foreach (var f in Safe(() => Directory.EnumerateFileSystemEntries(dir).Select(p => "  " + Path.GetFileName(p) + (File.Exists(p) ? "  " + new FileInfo(p).Length + " B" : "/")).ToArray(), Array.Empty<string>()))
            sb.AppendLine(f);
        var entry = zip.CreateEntry("system.txt");
        using (var w = new StreamWriter(entry.Open())) w.Write(sb.ToString());
        return path;
    }

    private static void AddIfExists(ZipArchive zip, string path, string name)
    {
        try { if (File.Exists(path)) zip.CreateEntryFromFile(path, name); }
        catch (Exception ex) { ErrorLog.WriteNote("SupportReport", name + ": " + ex.Message); }
    }

    private static string? Which(string tool)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var p = Path.Combine(dir, tool); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    private static string Safe(Func<string> f) { try { return f(); } catch (Exception ex) { return "(" + ex.Message + ")"; } }
    private static T Safe<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }
}
