using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Morphonic.Rvc;

namespace Morphonic;

// One line describing the machine — CPU, threads, RAM, GPUs, OS, hardware
// tier, runtime — for the boot log and the speed check, so a report from
// another machine says what it ran on without anyone asking. Read from the
// registry (Windows) or /proc, /sys, os-release and the driver tools
// (Linux) once; nothing here touches the network.
public static class MachineProfile
{
    private static readonly Lazy<string> Cached = new(Build);

    public static string Describe() => Cached.Value;

    private static string Build()
    {
        var parts = new List<string>();
        try
        {
            var cpu = Cpu();
            if (cpu.Length > 0) parts.Add(cpu);
            parts.Add($"{Environment.ProcessorCount} threads");
            parts.Add($"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0:0} GB RAM");
            var gpus = Gpus();
            if (gpus.Count > 0) parts.Add("GPU: " + string.Join(", ", gpus));
            parts.Add(Os());
            parts.Add("tier " + HardwareTier.Label(HardwareTier.Detect()));
            parts.Add($".NET {Environment.Version} {RuntimeInformation.ProcessArchitecture}");
        }
        catch (Exception ex) { parts.Add("profile error: " + ex.Message); }
        return string.Join(" · ", parts);
    }

    private static string Cpu()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (k?.GetValue("ProcessorNameString") as string ?? "").Trim();
            }
            if (!File.Exists("/proc/cpuinfo")) return "";
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (!line.StartsWith("model name", StringComparison.Ordinal)) continue;
                int colon = line.IndexOf(':');
                if (colon > 0) return Regex.Replace(line[(colon + 1)..].Trim(), "\\s+", " ");
            }
        }
        catch { }
        return "";
    }

    // Windows: display adapters from the device class key (entries without a
    // memory size are virtual displays and are skipped). Linux: NVIDIA cards
    // through the driver's own tool, every display device through lspci,
    // with VRAM from sysfs where the driver exposes it (amdgpu does).
    public static List<(string Name, long Bytes)> GpuList()
    {
        var list = new List<(string, long)>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (cls == null) return list;
                foreach (var sub in cls.GetSubKeyNames())
                {
                    if (sub.Length != 4 || !int.TryParse(sub, out _)) continue;
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is not string desc || desc.Length == 0) continue;
                    var mem = k.GetValue("HardwareInformation.qwMemorySize");
                    if (mem == null) continue;
                    long bytes = mem switch { long l => l, int i => i, _ => 0 };
                    list.Add((desc, bytes));
                }
                return list;
            }
            if (!OperatingSystem.IsLinux()) return list;
            bool viaSmi = false;
            var smi = LinuxHost.Capture("nvidia-smi", new[] { "--query-gpu=name,memory.total", "--format=csv,noheader,nounits" });
            foreach (var line in smi.Split('\n'))
            {
                var parts = line.Split(',');
                if (parts.Length < 2 || parts[0].Trim().Length == 0) continue;
                viaSmi = true;
                list.Add((parts[0].Trim(), long.TryParse(parts[1].Trim(), out var mib) ? mib * 1048576L : 0));
            }
            foreach (var line in LinuxHost.Capture("lspci", new[] { "-mm" }).Split('\n'))
            {
                var f = PciFields(line);
                if (f == null) continue;
                var (slot, cls, vendor, device) = f.Value;
                if (!(cls.Contains("VGA", StringComparison.Ordinal) ||
                      cls.Contains("3D controller", StringComparison.Ordinal) ||
                      cls.Contains("Display controller", StringComparison.Ordinal))) continue;
                if (viaSmi && vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add((ShortVendor(vendor) + " " + DeviceLabel(device), SysfsVram(slot)));
            }
        }
        catch { }
        return list;
    }

    private static List<string> Gpus() =>
        GpuList().Select(g => g.Bytes > 0 ? $"{g.Name} ({g.Bytes / 1073741824.0:0} GB)" : g.Name).ToList();

    // lspci -mm: slot, then quoted class, vendor, device, subsystem vendor,
    // subsystem device, with unquoted -r/-p revision tokens in between.
    internal static (string Slot, string Class, string Vendor, string Device)? PciFields(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        int space = line.IndexOf(' ');
        if (space <= 0) return null;
        var slot = line[..space];
        var quoted = new List<string>();
        foreach (Match m in Regex.Matches(line[space..], "\"([^\"]*)\"")) quoted.Add(m.Groups[1].Value);
        return quoted.Count < 3 ? null : (slot, quoted[0], quoted[1], quoted[2]);
    }

    // "GB203 [GeForce RTX 5080]" -> "GeForce RTX 5080"
    internal static string DeviceLabel(string device)
    {
        int open = device.LastIndexOf('[');
        return open >= 0 && device.EndsWith(']') ? device[(open + 1)..^1] : device;
    }

    internal static string ShortVendor(string vendor)
    {
        if (vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (vendor.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) ||
            vendor.Contains("AMD", StringComparison.Ordinal)) return "AMD";
        if (vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        int space = vendor.IndexOf(' ');
        return space > 0 ? vendor[..space] : vendor;
    }

    private static long SysfsVram(string slot)
    {
        try
        {
            var address = slot.Count(ch => ch == ':') >= 2 ? slot : "0000:" + slot;
            var path = Path.Combine("/sys/bus/pci/devices", address, "mem_info_vram_total");
            if (File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var bytes)) return bytes;
        }
        catch { }
        return 0;
    }

    private static string Os()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var product = k?.GetValue("ProductName") as string ?? "Windows";
                var display = k?.GetValue("DisplayVersion") as string ?? "";
                int build = int.TryParse(k?.GetValue("CurrentBuild") as string, out var b) ? b : 0;
                int ubr = k?.GetValue("UBR") is int u ? u : 0;
                return WindowsLabel(product, display, build, ubr);
            }
            if (!OperatingSystem.IsLinux()) return Environment.OSVersion.VersionString;
            string pretty = "", name = "", version = "";
            if (File.Exists("/etc/os-release"))
            {
                foreach (var line in File.ReadLines("/etc/os-release"))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line[..eq];
                    var value = line[(eq + 1)..].Trim().Trim('"');
                    if (key == "PRETTY_NAME") pretty = value;
                    else if (key == "NAME") name = value;
                    else if (key == "VERSION_ID") version = value;
                }
            }
            var kernel = File.Exists("/proc/sys/kernel/osrelease") ? File.ReadAllText("/proc/sys/kernel/osrelease").Trim() : "";
            return LinuxLabel(pretty.Length > 0 ? pretty : (name + " " + version).Trim(), kernel,
                Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "",
                Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "");
        }
        catch { return Environment.OSVersion.VersionString; }
    }

    // The registry still says "Windows 10" on Windows 11 — the build tells.
    internal static string WindowsLabel(string product, string display, int build, int ubr)
    {
        if (build >= 22000 && product.Contains("Windows 10", StringComparison.Ordinal))
            product = product.Replace("Windows 10", "Windows 11");
        var sb = new StringBuilder(product);
        if (display.Length > 0) sb.Append(' ').Append(display);
        if (build > 0) sb.Append(" build ").Append(build).Append('.').Append(ubr);
        return sb.ToString();
    }

    // "Fedora Linux 44 (Workstation Edition), kernel 6.15.4-200.fc44.x86_64, GNOME on wayland"
    internal static string LinuxLabel(string distro, string kernel, string sessionType, string desktop)
    {
        var sb = new StringBuilder(distro.Length > 0 ? distro : "Linux");
        if (kernel.Length > 0) sb.Append(", kernel ").Append(kernel);
        if (desktop.Length > 0 || sessionType.Length > 0)
        {
            sb.Append(", ");
            if (desktop.Length > 0) sb.Append(desktop);
            if (desktop.Length > 0 && sessionType.Length > 0) sb.Append(" on ");
            sb.Append(sessionType);
        }
        return sb.ToString();
    }
}
