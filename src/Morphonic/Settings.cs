using System;
using System.IO;
using System.Text.Json;

namespace Morphonic;

// App settings, one JSON file in the data folder (settings.json). Property
// names are part of the on-disk format — existing files keep loading
// across releases. Values are clamped on use, never on load, so a hand
// edit outside the range cannot make the file unreadable.
public class AppSettings
{
    // Devices are remembered as index + name and re-resolved every time
    // they are used (the lists reorder whenever hardware comes or goes).
    public int InputDeviceIndex { get; set; } = 0;
    public string InputDeviceName { get; set; } = "";
    public int OutputDeviceIndex { get; set; } = 0;
    public string OutputDeviceName { get; set; } = "";
    // Sidetone: a second output that plays the converted voice back to you.
    // MonitorDeviceIndex >= 0 names the device; -1 with SidetoneAuto = the
    // system default output whenever the main output is a virtual cable or
    // the virtual microphone (where you would otherwise hear nothing);
    // -1 without SidetoneAuto = off.
    public int MonitorDeviceIndex { get; set; } = -1;
    public string MonitorDeviceName { get; set; } = "";
    public bool SidetoneAuto { get; set; } = true;

    // The voice: a file name in the voices folder ("" = none chosen yet).
    public string VoiceId { get; set; } = "";
    public int SpeakerId { get; set; } = 0;

    public int PitchSemitones { get; set; } = 0;       // -24 .. +24
    public int BlockMs { get; set; } = 250;            // how much audio each pass converts
    public int ExtraMs { get; set; } = 2500;           // context before the block the encoder sees
    public int CrossfadeMs { get; set; } = 50;         // SOLA overlap between blocks
    public int NoiseGateDb { get; set; } = -60;        // -60 = off
    public double RmsMixRate { get; set; } = 0.5;      // 1 = output keeps its own loudness, 0 = follows the input
    public int OutputGainDb { get; set; } = 0;

    // "auto" = GPU pack when installed and usable, else CPU; "cpu" forces the CPU.
    public string Acceleration { get; set; } = "auto";
    // Linux: create the "Morphonic Voice" virtual microphone while running.
    public bool VirtualMic { get; set; } = true;

    private static readonly JsonSerializerOptions OnDisk = new() { WriteIndented = true };

    // Tests point this at a temp file.
    internal static string? PathOverride { get; set; }

    // How the last Load() resolved — shown in last_boot.log for support.
    public static string LastLoadSource { get; private set; } = "not loaded";
    public static string LastSaveError { get; private set; } = "";

    private static string SettingsPath => PathOverride ?? Path.Combine(AppPaths.DataDir, "settings.json");
    public static bool FileExists => File.Exists(SettingsPath);

    // "Has this user run the app before?" lives outside the data folder so
    // it stays answerable when the folder itself is what is missing:
    // HKCU\Software\Morphonic on Windows, ~/.config/Morphonic/last_run on Linux.
    internal static bool? HasRunBeforeOverride { get; set; }

    public static bool HasRunBefore()
    {
        if (HasRunBeforeOverride is bool o) return o;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Morphonic");
                return k?.GetValue("LastRunVersion") != null;
            }
            return File.Exists(Path.Combine(LinuxHost.ConfigHome, "Morphonic", "last_run"));
        }
        catch { return false; }
    }

    public static void MarkHasRun(string version)
    {
        if (HasRunBeforeOverride != null) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Morphonic");
                k?.SetValue("LastRunVersion", version);
                return;
            }
            var dir = Path.Combine(LinuxHost.ConfigHome, "Morphonic");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "last_run"), version + Environment.NewLine);
        }
        catch { /* best effort */ }
    }

    public static AppSettings Load()
    {
        var path = SettingsPath;
        if (!File.Exists(path))
        {
            LastLoadSource = "defaults (no settings file yet)";
            return new AppSettings();
        }
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), OnDisk)
                         ?? throw new InvalidDataException("settings file deserialized to null");
            LastLoadSource = "settings file";
            return loaded;
        }
        catch (Exception ex)
        {
            // A damaged file must never silently become the new truth (the
            // next Save would then persist defaults over the user's choices).
            // Log it, keep a copy for diagnosis, and fall back to the
            // previous good save that Save() leaves as .bak.
            ErrorLog.WriteEntry("AppSettings.Load", ex);
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { }
            var bak = path + ".bak";
            if (File.Exists(bak))
            {
                try
                {
                    var restored = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(bak), OnDisk);
                    if (restored != null)
                    {
                        ErrorLog.WriteNote("AppSettings.Load", "settings file was unreadable; restored the previous good copy (settings.json.bak)");
                        LastLoadSource = "backup (settings.json.bak) — the settings file was unreadable";
                        return restored;
                    }
                }
                catch (Exception ex2) { ErrorLog.WriteEntry("AppSettings.Load(backup)", ex2); }
            }
            LastLoadSource = "defaults — the settings file was unreadable and no backup exists";
            return new AppSettings();
        }
    }

    // Atomic: the content lands in a temp file, then File.Replace swaps it
    // in and keeps the previous file as .bak. A process kill mid-write can
    // therefore never truncate the live file, and Load() always has a
    // fallback. Returns false (and records why) when the disk refused.
    public bool Save()
    {
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, OnDisk));
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
            else File.Move(tmp, path);
            LastSaveError = "";
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("AppSettings.Save", ex);
            LastSaveError = ex.Message;
            return false;
        }
    }

    internal static void ResetForTests()
    {
        LastLoadSource = "not loaded";
        LastSaveError = "";
        HasRunBeforeOverride = null;
    }
}
