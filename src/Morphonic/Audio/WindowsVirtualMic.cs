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

// Windows: Morphonic's own virtual audio cable, a kernel driver built from
// driver/MorphonicCable and carried inside this exe. Installed once from
// Settings (one administrator prompt), it adds an output "Morphonic Voice"
// and a microphone "Morphonic Microphone" that stay until removed; the
// voice plays into the output and other programs read the microphone.
//
// Windows loads a kernel driver only when Microsoft has signed it
// (attestation signing through the Hardware Dev Center, see
// driver/README.md). An unsigned package is refused by Windows at the
// install step, and that refusal is reported as it is.
[SupportedOSPlatform("windows")]
public static class WindowsVirtualMic
{
    public const string HardwareId = "Root\\MorphonicCable";
    public const string RenderName = "Morphonic Voice";
    public const string CaptureName = "Morphonic Microphone";
    public const string InfName = "MorphonicCable.inf";
    private static readonly string[] PackageFiles = { "MorphonicCable.inf", "MorphonicCable.sys", "MorphonicCable.cat" };

    // The package is embedded as driver/<file> when the driver was built
    // before publishing (build.ps1 -Driver); a build without it can still
    // explain the situation.
    public static bool PackageAvailable =>
        PackageFiles.All(f => Assembly.GetExecutingAssembly().GetManifestResourceInfo("driver/" + f) != null);

    public static bool PackageSigned
    {
        get
        {
            var cat = Assembly.GetExecutingAssembly().GetManifestResourceStream("driver/MorphonicCable.cat");
            if (cat == null) return false;
            using var ms = new MemoryStream();
            cat.CopyTo(ms);
            // A catalog signed through the Hardware Dev Center carries the
            // "Microsoft Windows Hardware Compatibility Publisher" chain.
            return Encoding.Unicode.GetString(ms.ToArray()).Contains("Microsoft Windows Hardware", StringComparison.Ordinal) ||
                   Encoding.ASCII.GetString(ms.ToArray()).Contains("Microsoft Windows Hardware", StringComparison.Ordinal);
        }
    }

    // Installed = the device node exists; usable = its endpoints are present.
    public static bool Installed => DevicesByHardwareId().Length > 0;

    public static string Status
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return "n/a";
            if (Installed) return "installed";
            if (!PackageAvailable) return "this build carries no driver package";
            return "not installed";
        }
    }

    public static string ExtractPackage()
    {
        var dir = Path.Combine(AppPaths.DataDir, "driver");
        Directory.CreateDirectory(dir);
        foreach (var f in PackageFiles)
        {
            using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("driver/" + f)
                ?? throw new FileNotFoundException("the driver package is not part of this build: " + f);
            using var dst = File.Create(Path.Combine(dir, f));
            src.CopyTo(dst);
        }
        return dir;
    }

    // From the running (non-elevated) app: unpack, then run this same exe
    // elevated for the SetupAPI calls. Returns the elevated run's message.
    public static (bool Ok, string Message) Install()
    {
        if (!PackageAvailable) return (false, "this build carries no driver package (build.ps1 -Driver)");
        string dir;
        try { dir = ExtractPackage(); }
        catch (Exception ex) { return (false, "could not unpack the driver: " + ex.Message); }
        return RunElevated("--install-virtual-mic", dir);
    }

    public static (bool Ok, string Message) Remove() => RunElevated("--remove-virtual-mic", null);

    private static (bool Ok, string Message) RunElevated(string verb, string? arg)
    {
        var log = Path.Combine(AppPaths.DataDir, "driver-install.log");
        try { File.Delete(log); } catch { }
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add(verb);
        if (arg != null) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(AppPaths.DataDir);
        psi.ArgumentList.Add("--log");
        psi.ArgumentList.Add(log);
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("the elevated process did not start");
            p.WaitForExit();
            var message = File.Exists(log) ? File.ReadAllText(log).Trim() : "";
            if (message.Length == 0) message = p.ExitCode == 0 ? "done" : $"the elevated step failed (exit {p.ExitCode})";
            return (p.ExitCode == 0, message);
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
    public static (bool Ok, string Message) InstallElevated(string packageDir)
    {
        var inf = Path.Combine(packageDir, InfName);
        if (!File.Exists(inf)) return (false, "driver package not found: " + inf);
        if (Installed) return (true, "the Morphonic virtual microphone is already installed");

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
            var hwid = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref info, SPDRP_HARDWAREID, hwid, (uint)hwid.Length))
                return (false, "SetupDiSetDeviceRegistryProperty: " + LastError());
            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref info))
                return (false, "SetupDiCallClassInstaller(DIF_REGISTERDEVICE): " + LastError());
            if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, HardwareId, inf, INSTALLFLAG_FORCE, out var reboot))
            {
                var err = LastError();
                // Undo the device node so the next attempt starts clean.
                SetupDiCallClassInstaller(DIF_REMOVE, set, ref info);
                return (false, "Windows refused the driver: " + err + (err.Contains("signature", StringComparison.OrdinalIgnoreCase) || err.Contains("signed", StringComparison.OrdinalIgnoreCase)
                    ? " — the package in this build is not signed by Microsoft's Hardware Dev Center (see driver/README.md)" : ""));
            }
            return (true, "installed: output \"" + RenderName + "\" and microphone \"" + CaptureName + "\"" + (reboot ? " (a restart may be needed before they appear)" : ""));
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    public static (bool Ok, string Message) RemoveElevated()
    {
        int removed = 0;
        var set = SetupDiGetClassDevsW(IntPtr.Zero, "ROOT", IntPtr.Zero, DIGCF_ALLCLASSES);
        if (set == InvalidHandle) return (false, "SetupDiGetClassDevs: " + LastError());
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                if (!HardwareIdOf(set, ref info).Equals(HardwareId, StringComparison.OrdinalIgnoreCase)) continue;
                if (SetupDiCallClassInstaller(DIF_REMOVE, set, ref info)) removed++;
                else return (false, "could not remove the device: " + LastError());
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        // The driver package stays in the driver store until deleted.
        var oem = FindOemInf(Capture("pnputil", "/enum-drivers"));
        if (oem != null) Capture("pnputil", "/delete-driver " + oem + " /uninstall /force");
        return (true, removed == 0 && oem == null ? "the Morphonic virtual microphone was not installed"
            : $"removed ({removed} device(s){(oem != null ? ", driver package " + oem : "")})");
    }

    // pnputil /enum-drivers lists blocks like:
    //   Published Name:     oem42.inf
    //   Original Name:      morphoniccable.inf
    internal static string? FindOemInf(string enumOutput)
    {
        string? published = null;
        foreach (var raw in enumOutput.Split('\n'))
        {
            var line = raw.Trim();
            var m = Regex.Match(line, @"^Published Name\s*:\s*(oem\d+\.inf)", RegexOptions.IgnoreCase);
            if (m.Success) { published = m.Groups[1].Value; continue; }
            if (Regex.IsMatch(line, @"^Original Name\s*:\s*" + Regex.Escape(InfName) + @"\s*$", RegexOptions.IgnoreCase) && published != null)
                return published;
        }
        return null;
    }

    private static string[] DevicesByHardwareId()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        var found = new System.Collections.Generic.List<string>();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, "ROOT", IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (set == InvalidHandle) return Array.Empty<string>();
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                var id = HardwareIdOf(set, ref info);
                if (id.Equals(HardwareId, StringComparison.OrdinalIgnoreCase)) found.Add(id);
            }
        }
        catch (Exception ex) { ErrorLog.WriteEntry("WindowsVirtualMic", ex); }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return found.ToArray();
    }

    private static string HardwareIdOf(IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var buf = new byte[2048];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, SPDRP_HARDWAREID, out _, buf, (uint)buf.Length, out var needed)) return "";
        var s = Encoding.Unicode.GetString(buf, 0, (int)Math.Min(needed, buf.Length));
        return s.Split('\0')[0];
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
