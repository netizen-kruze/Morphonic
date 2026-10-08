using System;
using System.IO;

namespace Morphonic;

// Shared locations for the app's files. User-local on both desktops, so
// model weights and voices can never end up inside the app folder or a
// repository checkout.
public static class AppPaths
{
    // Windows: %APPDATA%\Morphonic. Linux: ~/.local/share/Morphonic (XDG_DATA_HOME).
    // Settings, logs, the boot sentinel, the unpacked UI, the components,
    // the voices and the GPU pack all live under it. --data-dir overrides
    // it (tools/smoke.*).
    public static string DataDir { get; set; } = DefaultDataDir();

    private static string DefaultDataDir() => OperatingSystem.IsLinux()
        ? Path.Combine(LinuxHost.DataHome, "Morphonic")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Morphonic");

    // The downloaded components (content encoder, pitch model).
    public static string ModelDir => Path.Combine(DataDir, "models");

    // Voice models the user imports (.onnx, plus .pth files waiting for
    // conversion).
    public static string VoiceDir => Path.Combine(DataDir, "voices");

    // The unpacked UI, re-extracted on every launch.
    public static string UiDir => Path.Combine(DataDir, "ui");

    public static string Rid => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

    // The optional GPU pack: a different build of the inference runtime
    // (DirectML on Windows, CUDA on Linux) that replaces the bundled CPU
    // build when present.
    public static string GpuDir => Path.Combine(DataDir, "runtimes", "gpu", Rid);

    public static string ErrorLogPath => Path.Combine(DataDir, "error.log");
    public static string BootLogPath => Path.Combine(DataDir, "last_boot.log");
    public static string BenchLogPath => Path.Combine(DataDir, "bench.log");

    internal static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".morphonic-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
