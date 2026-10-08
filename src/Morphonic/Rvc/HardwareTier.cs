using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Morphonic.Rvc;

public enum Tier
{
    Gpu,        // a usable GPU: DirectML (Windows) or CUDA (Linux) can carry the models
    CpuHigh,    // strong CPU — real-time at the default block size
    CpuLow,     // modest CPU — larger blocks
    CpuMinimal, // very weak hardware — the largest block, least context
}

// Hardware tier autodetect: a usable GPU -> GPU tier, else a CPU tier by
// cores/RAM. Classify() and JudgeCuda() are pure for unit testing;
// Detect() feeds them live machine facts once per process.
public static class HardwareTier
{
    private static readonly Lazy<Tier> Detected = new(() =>
        Classify(GpuUsable(), Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0));

    public static Tier Detect() => Detected.Value;

    // Why the GPU verdict came out the way it did — for the boot log and
    // the Models banner ("" until Detect() has run).
    public static string GpuVerdict { get; private set; } = "";

    public static Tier Classify(bool gpuUsable, int cores, double ramGB)
    {
        if (gpuUsable && ramGB >= 8) return Tier.Gpu;
        if (cores >= 8 && ramGB >= 8) return Tier.CpuHigh;
        if (cores >= 4 && ramGB >= 4) return Tier.CpuLow;
        return Tier.CpuMinimal;
    }

    public static string Label(Tier tier) => tier switch
    {
        Tier.Gpu => OperatingSystem.IsWindows() ? "GPU (DirectML)" : "GPU (NVIDIA)",
        Tier.CpuHigh => "CPU (high)",
        Tier.CpuLow => "CPU (low)",
        _ => "Minimal",
    };

    // The block size and context that keep this tier real-time: smaller
    // blocks mean less delay but more passes per second.
    public static (int BlockMs, int ExtraMs) Recommended(Tier tier) => tier switch
    {
        Tier.Gpu => (160, 2500),
        Tier.CpuHigh => (250, 1500),
        Tier.CpuLow => (350, 1000),
        _ => (500, 1000),
    };

    // Windows: DirectML runs on any DirectX 12 adapter; a card with at least
    // 2 GB of its own memory is worth the pack. Linux: the CUDA 12 build
    // needs the NVIDIA kernel module, its CUDA library, a 525+ driver and
    // a Pascal-or-newer card (compute 6.0). The driver's libcuda.so.1 alone
    // is not enough — RPM Fusion installs it whether or not the module ever
    // loads (Secure Boot without an enrolled key). Nothing here loads a
    // library (LinuxHost.LibraryPresent); the facts are files and nvidia-smi.
    public const int MinDriverMajor = 525;
    public const double MinComputeCapability = 6.0;
    public const long MinGpuBytes = 2L * 1024 * 1024 * 1024;

    private static bool GpuUsable()
    {
        if (OperatingSystem.IsWindows())
        {
            var gpus = Morphonic.MachineProfile.GpuList();
            var best = gpus.OrderByDescending(g => g.Bytes).FirstOrDefault();
            if (best.Name == null) { GpuVerdict = "no display adapter found"; return false; }
            if (best.Bytes < MinGpuBytes)
            {
                GpuVerdict = $"{best.Name} has {best.Bytes / 1048576} MB of memory — DirectML needs a card with 2 GB or more";
                return false;
            }
            GpuVerdict = $"DirectML usable ({best.Name}, {best.Bytes / 1073741824.0:0} GB)";
            return true;
        }
        if (!OperatingSystem.IsLinux()) return false;
        var (usable, verdict) = JudgeCuda(
            LinuxHost.LibraryPresent("libcuda.so.1"),
            File.Exists("/proc/driver/nvidia/version"),
            File.Exists("/dev/dxg"),
            NvidiaDriverMajor(),
            NvidiaComputeCapability());
        GpuVerdict = verdict;
        return usable;
    }

    // Pure: libcuda.so.1 on disk, the kernel module loaded (/proc/driver/nvidia),
    // a WSL guest (/dev/dxg — the driver is the host's), the driver's major
    // version when known, the first GPU's compute capability when nvidia-smi
    // could say. Unknown facts give the card the benefit of the doubt.
    internal static (bool Usable, string Verdict) JudgeCuda(bool libcuda, bool moduleLoaded, bool wsl, int? driverMajor, double? computeCap)
    {
        if (!libcuda)
            return (false, moduleLoaded
                ? "NVIDIA driver loaded but its CUDA library (libcuda.so.1) is not installed — on Fedora: sudo dnf install xorg-x11-drv-nvidia-cuda-libs"
                : "no NVIDIA driver library");
        if (!moduleLoaded && !wsl)
            return (false, "libcuda.so.1 is installed but the NVIDIA kernel module is not loaded");
        if (driverMajor is { } d && d < MinDriverMajor)
            return (false, $"NVIDIA driver {d} is older than CUDA 12 needs ({MinDriverMajor} or newer)");
        if (computeCap is { } c && c < MinComputeCapability)
            return (false, $"GPU compute capability {c.ToString("0.0", CultureInfo.InvariantCulture)} is below the minimum " +
                           $"({MinComputeCapability.ToString("0.0", CultureInfo.InvariantCulture)}, Pascal or newer)");
        var facts = new System.Collections.Generic.List<string>();
        if (driverMajor != null) facts.Add($"driver {driverMajor}");
        if (computeCap != null) facts.Add($"compute {computeCap.Value.ToString("0.0", CultureInfo.InvariantCulture)}");
        return (true, facts.Count == 0 ? "CUDA usable" : $"CUDA usable ({string.Join(", ", facts)})");
    }

    // "NVRM version: NVIDIA UNIX Open Kernel Module for x86_64  580.82.07  Release Build" -> 580
    internal static int? ParseDriverMajor(string text)
    {
        var m = Regex.Match(text ?? "", @"\b(\d{3})\.\d+");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    private static int? NvidiaDriverMajor()
    {
        try
        {
            return File.Exists("/proc/driver/nvidia/version")
                ? ParseDriverMajor(File.ReadAllText("/proc/driver/nvidia/version"))
                : null;
        }
        catch { return null; }
    }

    internal static double? ParseComputeCapability(string text)
    {
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            return double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        return null;
    }

    private static double? NvidiaComputeCapability()
    {
        if (!OperatingSystem.IsLinux()) return null;
        return ParseComputeCapability(LinuxHost.Capture("nvidia-smi", new[] { "--query-gpu=compute_cap", "--format=csv,noheader" }));
    }
}
