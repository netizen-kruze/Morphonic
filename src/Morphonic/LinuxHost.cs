using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Morphonic;

// Everything the app needs from the Linux desktop in one place: XDG
// folders, a modal message before the window exists, the libraries the
// WebKitGTK window needs, child processes without Steam's overlay preload,
// and a single-instance lock. Every helper is best-effort and never throws
// — a missing tool degrades a message, not the app. On Windows every
// member answers "not applicable" so callers need no platform checks.
public static class LinuxHost
{
    // ── XDG base directories ───────────────────────────────────────

    public static string Home
    {
        get
        {
            var h = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrWhiteSpace(h)) h = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(h) ? Path.Combine(Path.GetTempPath(), "morphonic-home") : h;
        }
    }

    // ~/.local/share unless XDG_DATA_HOME says otherwise: the one data folder.
    public static string DataHome => Env("XDG_DATA_HOME") ?? Path.Combine(Home, ".local", "share");

    // ~/.config: only the tiny "has run before" marker lives here, outside
    // the data folder on purpose (see AppSettings.HasRunBefore).
    public static string ConfigHome => Env("XDG_CONFIG_HOME") ?? Path.Combine(Home, ".config");

    // ~/.cache: WebKitGTK's page cache lands here under the binary's name.
    public static string CacheHome => Env("XDG_CACHE_HOME") ?? Path.Combine(Home, ".cache");

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public static bool HasDisplay => Env("WAYLAND_DISPLAY") != null || Env("DISPLAY") != null;

    // ── environment ────────────────────────────────────────────────

    // On Unix, .NET keeps its own copy of the environment: SetEnvironmentVariable
    // changes what managed code and children see, but not the C environ
    // that native code (WebKitGTK and the web process it spawns) reads
    // with getenv. A variable meant for native code must go both ways.
    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

    public static void SetProcessEnv(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        if (!OperatingSystem.IsLinux()) return;
        try { setenv(name, value, 1); } catch { }
    }

    // ── window prerequisites ───────────────────────────────────────

    // What Photino.Native links or opens at runtime, with the Fedora
    // package that provides each. WebKitGTK is opened lazily by name, so a
    // missing one would otherwise surface as a crash inside the window
    // constructor instead of a sentence.
    public static readonly (string Library, string Package)[] UiDependencies =
    {
        ("libgtk-3.so.0", "gtk3"),
        ("libnotify.so.4", "libnotify"),
        ("libwebkit2gtk-4.1.so.0", "webkit2gtk4.1"),
    };

    public static List<(string Library, string Package)> MissingUiDependencies()
    {
        var missing = new List<(string, string)>();
        if (!OperatingSystem.IsLinux()) return missing;
        foreach (var dep in UiDependencies)
            if (!LibraryPresent(dep.Library)) missing.Add(dep);
        return missing;
    }

    // Is a shared library installed? Answered from the loader's cache
    // (ldconfig -p) and the usual library folders — never by loading it.
    // Loading GTK, WebKitGTK or the CUDA driver just to look, and then
    // unloading it again, leaves the process crashing in the window
    // constructor: those libraries cannot be unloaded safely.
    public static bool LibraryPresent(string soname) =>
        OperatingSystem.IsLinux() && KnownLibraries.Value.ContainsKey(soname);

    // Where an installed library lives; null when it is not installed.
    public static string? LibraryPath(string soname) =>
        OperatingSystem.IsLinux() && KnownLibraries.Value.TryGetValue(soname, out var path) ? path : null;

    private static readonly Lazy<Dictionary<string, string>> KnownLibraries = new(() =>
    {
        var libs = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var cache = Capture("ldconfig", new[] { "-p" });
            if (cache.Length == 0) cache = Capture("/sbin/ldconfig", new[] { "-p" });
            foreach (var (name, path) in ParseLdconfig(cache)) libs.TryAdd(name, path);
        }
        catch { }
        var dirs = new List<string>
        {
            "/usr/lib64", "/usr/lib", "/usr/lib/x86_64-linux-gnu", "/usr/local/lib64", "/usr/local/lib",
            "/usr/lib/wsl/lib", "/usr/local/cuda/lib64", "/usr/local/cuda/targets/x86_64-linux/lib",
        };
        var ldPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (!string.IsNullOrEmpty(ldPath)) dirs.AddRange(ldPath.Split(':', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.so*")) libs.TryAdd(Path.GetFileName(file), file);
            }
            catch { }
        }
        return libs;
    });

    // "\tlibgtk-3.so.0 (libc6,x86-64) => /usr/lib64/libgtk-3.so.0"
    //   -> ("libgtk-3.so.0", "/usr/lib64/libgtk-3.so.0")
    internal static IEnumerable<(string Name, string Path)> ParseLdconfig(string text)
    {
        foreach (var raw in (text ?? "").Split('\n'))
        {
            if (raw.Length == 0 || !char.IsWhiteSpace(raw[0])) continue; // entries are indented, headers are not
            var line = raw.Trim();
            int space = line.IndexOf(' ');
            int arrow = line.IndexOf("=>", StringComparison.Ordinal);
            if (space <= 0 || arrow < 0) continue;
            yield return (line[..space], line[(arrow + 2)..].Trim());
        }
    }

    // ── messages without a window ──────────────────────────────────

    // A modal notice for the moments before (or instead of) the window:
    // zenity on GNOME, kdialog on KDE, a desktop notification, and always
    // stderr — whichever exists first.
    public static void Alert(string title, string text, bool error = true)
    {
        Console.Error.WriteLine($"{title}: {text}");
        if (!OperatingSystem.IsLinux() || !HasDisplay) return;
        var attempts = new (string File, string[] Args)[]
        {
            ("zenity", new[] { error ? "--error" : "--info", "--no-markup", "--title=" + title, "--text=" + text, "--width=460" }),
            ("kdialog", new[] { error ? "--error" : "--msgbox", text, "--title", title }),
            ("notify-send", new[] { "-a", "Morphonic", title, text }),
        };
        foreach (var a in attempts)
            if (TryRun(a.File, a.Args, 300_000)) return;
    }

    public static void Notify(string title, string text)
    {
        if (!OperatingSystem.IsLinux() || !HasDisplay) return;
        TryRun("notify-send", new[] { "-a", "Morphonic", title, text }, 3000);
    }

    private static bool TryRun(string file, string[] args, int waitMs)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            StripSteamPreload(psi);
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(waitMs);
            return true;
        }
        catch { return false; }
    }

    // ── child processes ────────────────────────────────────────────

    // Runs a tool and returns its stdout, "" when it is missing, fails or
    // exceeds the timeout. Informational commands only.
    public static string Capture(string file, string[] args, int timeoutMs = 4000)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            StripSteamPreload(psi);
            using var p = Process.Start(psi);
            if (p == null) return "";
            p.BeginErrorReadLine();
            var stdout = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return "";
            }
            return stdout.Wait(timeoutMs) ? stdout.Result : "";
        }
        catch { return ""; }
    }

    // Steam runs launch-option commands with its overlay in LD_PRELOAD and
    // its 2012-era runtime in LD_LIBRARY_PATH. Helper processes (dialogs,
    // pw-record, pactl) must not inherit either.
    public static void StripSteamPreload(ProcessStartInfo psi)
    {
        if (psi.Environment.TryGetValue("LD_PRELOAD", out var value) &&
            value != null && value.Contains("gameoverlayrenderer", StringComparison.Ordinal))
            psi.Environment.Remove("LD_PRELOAD");
        if (psi.Environment.TryGetValue("LD_LIBRARY_PATH", out var libPath) && IsSteamRuntimeLibraryPath(libPath))
        {
            if (psi.Environment.TryGetValue("SYSTEM_LD_LIBRARY_PATH", out var system) && !string.IsNullOrEmpty(system))
                psi.Environment["LD_LIBRARY_PATH"] = system;
            else
                psi.Environment.Remove("LD_LIBRARY_PATH");
        }
    }

    internal static bool IsSteamRuntimeLibraryPath(string? value) =>
        value != null && (value.Contains("steam-runtime", StringComparison.Ordinal) ||
                          value.Contains("/ubuntu12_32/", StringComparison.Ordinal) ||
                          value.Contains("/ubuntu12_64/", StringComparison.Ordinal));

    // Launched by Steam (a launch option), this process carries the overlay
    // in LD_PRELOAD — a preload known to hang or blank GTK/WebKit programs.
    // Re-run ourselves once without it and mirror the child's exit code.
    public static int? ReexecWithoutSteamPreload(string[] args)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var preload = Environment.GetEnvironmentVariable("LD_PRELOAD");
        var libPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        bool overlay = !string.IsNullOrEmpty(preload) && preload.Contains("gameoverlayrenderer", StringComparison.Ordinal);
        bool scout = IsSteamRuntimeLibraryPath(libPath);
        if (!overlay && !scout) return null;
        if (Environment.GetEnvironmentVariable("MORPHONIC_REEXEC") != null) return null;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return null;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["MORPHONIC_REEXEC"] = "1";
            if (overlay) psi.Environment.Remove("LD_PRELOAD");
            if (scout) StripSteamPreload(psi);
            using var p = Process.Start(psi);
            if (p == null) return null;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch { return null; }
    }

    // ── single instance ────────────────────────────────────────────

    // Holds an exclusive lock on a file in the data folder for the life of
    // the process (per user, across login sessions and launchers — a named
    // mutex is per login session on Linux). Returns null when another
    // instance already holds it; on any other failure the app runs
    // unguarded — better two windows than none.
    public static IDisposable? TryLockInstance(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // First without the lock: a folder that cannot take the file at
            // all (full disk, read-only) must not read as "already running".
            using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try { stream.SetLength(0); stream.Write(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString())); stream.Flush(); } catch { }
            return new InstanceLock(stream, path);
        }
        catch (IOException) { return null; }
        catch (Exception ex) { ErrorLog.WriteEntry("SingleInstance", ex); return new MemoryStream(); }
    }

    // The lock is the open handle; the file itself is tidied away on a
    // clean exit so a support bundle never lists a stale lock.
    private sealed class InstanceLock : IDisposable
    {
        private readonly FileStream _stream;
        private readonly string _path;
        public InstanceLock(FileStream stream, string path) { _stream = stream; _path = path; }
        public void Dispose()
        {
            try { _stream.Dispose(); } catch { }
            try { File.Delete(_path); } catch { }
        }
    }
}
