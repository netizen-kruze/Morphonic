using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace Morphonic.Audio;

// Windows: a virtual audio cable other programs read as a microphone,
// carried inside this exe and installed from Settings with one
// administrator prompt. Windows loads only kernel drivers Microsoft has
// signed, so the exe carries two candidates and uses the first that is:
//
//   1. Morphonic's own cable (driver/MorphonicCable: "Morphonic Voice" ->
//      "Morphonic Microphone"), once its package has been through the
//      Hardware Dev Center;
//   2. VB-CABLE by VB-Audio (www.vb-cable.com), a donationware driver whose
//      Windows 10/11 catalog carries Microsoft's signature and whose
//      licence allows shipping it inside an application: "CABLE Input"
//      (the output Morphonic plays into) -> "CABLE Output" (the microphone
//      other programs pick).
//
// Installing = creating the root-enumerated device and letting Windows
// install the package for it through SetupAPI (what devcon install does);
// removing = deleting that device and the package from the driver store.
[SupportedOSPlatform("windows")]
public static class WindowsVirtualMic
{
    public sealed record Package(string Key, string Vendor, string Inf, string[] Files, string HardwareId,
        string RenderName, string CaptureName, string Credit);

    public static readonly Package Own = new("own", "Morphonic", "MorphonicCable.inf",
        new[] { "MorphonicCable.inf", "MorphonicCable.sys", "MorphonicCable.cat" },
        "Root\\MorphonicCable", "Morphonic Voice", "Morphonic Microphone", "");

    public static readonly Package VbCable = new("vbcable", "VB-CABLE (VB-Audio)", "vbMmeCable64_win10.inf",
        new[] { "vbMmeCable64_win10.inf", "vbaudio_cable64_win10.cat", "vbaudio_cable64_win10.sys", "vbaudio_cable64arm_win10.sys", "readme.txt" },
        "VBAudioVACWDM", "CABLE Input", "CABLE Output",
        "VB-CABLE is a donationware by VB-Audio (www.vb-cable.com); all participations are welcome.");

    private static readonly Package[] Candidates = { Own, VbCable };

    private static Stream? Resource(Package p, string file) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream("driver/" + p.Key + "/" + file);

    public static bool Carried(Package p) => p.Files.All(f => Assembly.GetExecutingAssembly().GetManifestResourceInfo("driver/" + p.Key + "/" + f) != null);

    // Signed through Microsoft's Hardware Dev Center: the catalog names the
    // "Microsoft Windows Hardware Compatibility Publisher".
    public static bool Signed(Package p)
    {
        var cat = p.Files.FirstOrDefault(f => f.EndsWith(".cat", StringComparison.OrdinalIgnoreCase));
        if (cat == null) return false;
        using var s = Resource(p, cat);
        if (s == null) return false;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        const string signer = "Microsoft Windows Hardware Compatibility Publisher";
        return Encoding.ASCII.GetString(bytes).Contains(signer, StringComparison.Ordinal) ||
               Encoding.Unicode.GetString(bytes).Contains(signer, StringComparison.Ordinal);
    }

    // The package Install would use: the first carried and signed one. The
    // embedded resources never change, so this is read once.
    private static readonly Lazy<Package?> InstallableOnce = new(() => Candidates.FirstOrDefault(p => Carried(p) && Signed(p)));
    public static Package? Installable => InstallableOnce.Value;

    // Any carried package (for the explanation when none is installable).
    private static readonly Lazy<bool> PackageAvailableOnce = new(() => Candidates.Any(Carried));
    public static bool PackageAvailable => PackageAvailableOnce.Value;
    public static bool PackageSigned => Installable != null;

    // The package whose device is present on this machine, if any. Asked
    // many times per devices payload (once per output device, too), and
    // each answer is a SetupAPI enumeration: kept for a few seconds, and
    // dropped as soon as Install or Remove changed anything.
    private static readonly object PresentGate = new();
    private static Package? _present;
    private static long _presentAt = long.MinValue;
    private const int PresentCacheMs = 3000;

    public static Package? Present
    {
        get
        {
            lock (PresentGate)
            {
                long now = Environment.TickCount64;
                if (_presentAt == long.MinValue || now - _presentAt > PresentCacheMs)
                {
                    _present = Candidates.FirstOrDefault(p => DevicePresent(p.HardwareId));
                    _presentAt = now;
                }
                return _present;
            }
        }
    }

    public static void Invalidate()
    {
        lock (PresentGate) _presentAt = long.MinValue;
    }

    public static bool Installed => Present != null;

    // The names the rest of the app routes by.
    public static string RenderName => (Present ?? Installable ?? VbCable).RenderName;
    public static string CaptureName => (Present ?? Installable ?? VbCable).CaptureName;
    public static string Vendor => (Present ?? Installable ?? VbCable).Vendor;
    public static string Credit => (Present ?? Installable ?? VbCable).Credit;

    public static string Status
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return "n/a";
            if (Present is { } p) return "installed (" + p.Vendor + ")";
            if (Installable is { } i) return "not installed (" + i.Vendor + " ready)";
            if (PackageAvailable) return "carried package is not signed";
            return "this build carries no driver package";
        }
    }

    public static string ExtractPackage(Package p)
    {
        var dir = Path.Combine(AppPaths.DataDir, "driver", p.Key);
        Directory.CreateDirectory(dir);
        foreach (var f in p.Files)
        {
            using var src = Resource(p, f) ?? throw new FileNotFoundException("the driver package is not part of this build: " + f);
            using var dst = File.Create(Path.Combine(dir, f));
            src.CopyTo(dst);
        }
        return dir;
    }

    // From the running (non-elevated) app: unpack, then run this same exe
    // elevated for the SetupAPI calls. Returns the elevated run's message.
    public static (bool Ok, string Message) Install()
    {
        var p = Installable;
        if (p == null) return (false, PackageAvailable ? "the carried driver package is not signed by Microsoft, so Windows would refuse it" : "this build carries no driver package");
        string dir;
        try { dir = ExtractPackage(p); }
        catch (Exception ex) { return (false, "could not unpack the driver: " + ex.Message); }
        var result = RunElevated(new[] { "--install-virtual-mic", dir, "--inf", p.Inf, "--hwid", p.HardwareId });
        Invalidate();
        return result;
    }

    public static (bool Ok, string Message) Remove()
    {
        var p = Present;
        if (p == null) return (true, "no virtual microphone is installed");
        var result = RunElevated(new[] { "--remove-virtual-mic", "--inf", p.Inf, "--hwid", p.HardwareId });
        Invalidate();
        return result;
    }

    private static (bool Ok, string Message) RunElevated(string[] verbArgs)
    {
        var log = Path.Combine(AppPaths.DataDir, "driver-install.log");
        try { File.Delete(log); } catch { }
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var a in verbArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(AppPaths.DataDir);
        psi.ArgumentList.Add("--log");
        psi.ArgumentList.Add(log);
        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("the elevated process did not start");
            proc.WaitForExit();
            var message = File.Exists(log) ? File.ReadAllText(log).Trim() : "";
            if (message.Length == 0) message = proc.ExitCode == 0 ? "done" : $"the elevated step failed (exit {proc.ExitCode})";
            return (proc.ExitCode == 0, message);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "the administrator prompt was cancelled — nothing was changed");
        }
        catch (Exception ex)
        {
            return (false, "could not run the elevated step: " + ex.Message);
        }
    }

    // ── the elevated side (Program.cs --install-virtual-mic / --remove-virtual-mic) ──

    // devcon "install": create a root-enumerated device node with the
    // hardware id, register it, then let Windows pick the INF's driver for
    // it. Windows checks the package's signature here.
    public static (bool Ok, string Message) InstallElevated(string packageDir, string infName, string hardwareId)
    {
        var inf = Path.Combine(packageDir, infName);
        if (!File.Exists(inf)) return (false, "driver package not found: " + inf);
        if (DevicePresent(hardwareId)) return (true, "the virtual microphone is already installed");

        var classGuid = Guid.Empty;
        var className = new StringBuilder(64);
        if (!SetupDiGetINFClassW(inf, ref classGuid, className, (uint)className.Capacity, out _))
            return (false, "the INF has no device class: " + LastError());

        var set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == InvalidHandle) return (false, "SetupDiCreateDeviceInfoList: " + LastError());
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiCreateDeviceInfoW(set, className.ToString(), ref classGuid, null, IntPtr.Zero, DICD_GENERATE_ID, ref info))
                return (false, "SetupDiCreateDeviceInfo: " + LastError());
            var hwid = Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref info, SPDRP_HARDWAREID, hwid, (uint)hwid.Length))
                return (false, "SetupDiSetDeviceRegistryProperty: " + LastError());
            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref info))
                return (false, "SetupDiCallClassInstaller(DIF_REGISTERDEVICE): " + LastError());
            if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hardwareId, inf, INSTALLFLAG_FORCE, out var reboot))
            {
                var err = LastError();
                SetupDiCallClassInstaller(DIF_REMOVE, set, ref info);   // leave no half-made device behind
                return (false, "Windows refused the driver: " + err);
            }
            var p = Candidates.FirstOrDefault(c => c.HardwareId.Equals(hardwareId, StringComparison.OrdinalIgnoreCase));
            return (true, "installed: output \"" + (p?.RenderName ?? "?") + "\" and microphone \"" + (p?.CaptureName ?? "?") + "\"" +
                          (reboot ? " — Windows asks for a restart before they appear" : ""));
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    public static (bool Ok, string Message) RemoveElevated(string infName, string hardwareId)
    {
        int removed = 0;
        var set = SetupDiGetClassDevsW(IntPtr.Zero, "ROOT", IntPtr.Zero, DIGCF_ALLCLASSES);
        if (set == InvalidHandle) return (false, "SetupDiGetClassDevs: " + LastError());
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                if (!HardwareIdOf(set, ref info).Equals(hardwareId, StringComparison.OrdinalIgnoreCase)) continue;
                if (SetupDiCallClassInstaller(DIF_REMOVE, set, ref info)) removed++;
                else return (false, "could not remove the device: " + LastError());
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        // The driver package stays in the driver store until deleted.
        var oem = FindOemInf(Capture("pnputil", "/enum-drivers"), infName);
        if (oem != null) Capture("pnputil", "/delete-driver " + oem + " /uninstall /force");
        return (true, removed == 0 && oem == null ? "the virtual microphone was not installed"
            : $"removed ({removed} device(s){(oem != null ? ", driver package " + oem : "")})");
    }

    // pnputil /enum-drivers lists blocks like:
    //   Published Name:     oem42.inf
    //   Original Name:      vbmmecable64_win10.inf
    internal static string? FindOemInf(string enumOutput, string infName)
    {
        string? published = null;
        foreach (var raw in enumOutput.Split('\n'))
        {
            var line = raw.Trim();
            var m = Regex.Match(line, @"^Published Name\s*:\s*(oem\d+\.inf)", RegexOptions.IgnoreCase);
            if (m.Success) { published = m.Groups[1].Value; continue; }
            if (Regex.IsMatch(line, @"^Original Name\s*:\s*" + Regex.Escape(infName) + @"\s*$", RegexOptions.IgnoreCase) && published != null)
                return published;
        }
        return null;
    }

    private static bool DevicePresent(string hardwareId)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var set = SetupDiGetClassDevsW(IntPtr.Zero, "ROOT", IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (set == InvalidHandle) return false;
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
                if (HardwareIdOf(set, ref info).Equals(hardwareId, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception ex) { ErrorLog.WriteEntry("WindowsVirtualMic", ex); }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return false;
    }

    private static string HardwareIdOf(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var buf = new byte[2048];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, SPDRP_HARDWAREID, out _, buf, (uint)buf.Length, out var needed)) return "";
        return Encoding.Unicode.GetString(buf, 0, (int)Math.Min(needed, buf.Length)).Split('\0')[0];
    }

    private static string Capture(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60_000);
            return o;
        }
        catch (Exception ex) { ErrorLog.WriteEntry("WindowsVirtualMic." + file, ex); return ""; }
    }

    private static string LastError()
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code).Message + $" (0x{code:X8})";
    }

    // ── SetupAPI / newdev ──
    private static readonly IntPtr InvalidHandle = new(-1);
    private const uint DICD_GENERATE_ID = 1;
    private const uint SPDRP_HARDWAREID = 1;
    private const uint DIF_REMOVE = 5;
    private const uint DIF_REGISTERDEVICE = 0x19;
    private const uint DIGCF_PRESENT = 2;
    private const uint DIGCF_ALLCLASSES = 4;
    private const uint INSTALLFLAG_FORCE = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetINFClassW(string infName, ref Guid classGuid, StringBuilder className, uint classNameSize, out uint required);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwnd);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string deviceName, ref Guid classGuid, string? description, IntPtr hwnd, uint flags, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA info, uint property, byte[] buffer, uint size);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA info, uint property, out uint regType, byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwnd, string hardwareId, string infPath, uint flags, out bool rebootRequired);
}
