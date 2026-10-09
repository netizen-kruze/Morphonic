using System;
using System.Collections.Generic;

namespace Morphonic.Audio;

// Linux: a virtual microphone other programs can pick, made of two
// PulseAudio-protocol modules PipeWire implements — a null sink the
// converted voice plays into, and a remapped source of that sink's
// monitor, so the voice shows up under "input devices" in VRChat (via
// Proton), Discord or OBS as "Morphonic-Voice-Mic". Created while Morphonic runs,
// removed on exit. On Windows a virtual cable needs a driver (VB-CABLE);
// the README explains that route.
public static class VirtualMic
{
    public const string SinkName = "morphonic_voice";
    public const string SourceName = "morphonic_mic";
    public const string SinkDescription = "Morphonic-Voice";
    public const string SourceDescription = "Morphonic-Voice-Mic";

    private static readonly List<int> Modules = new();
    private static string _status = "not created";
    public static string Status
    {
        get => OperatingSystem.IsWindows() ? WindowsVirtualMic.Status : _status;
        private set => _status = value;
    }

    // Linux: always (PipeWire modules, created per run). Windows: once
    // Morphonic's own cable driver is installed (WindowsVirtualMic).
    public static bool Supported => OperatingSystem.IsLinux() || (OperatingSystem.IsWindows() && WindowsVirtualMic.Installed);

    // The output whose audio other programs read as the microphone.
    public static bool IsVirtualOutput(AudioDevice d) =>
        OperatingSystem.IsWindows()
            ? d.Name.Contains(WindowsVirtualMic.RenderName, StringComparison.OrdinalIgnoreCase)
            : d.Id == SinkName;

    public static bool Exists() =>
        OperatingSystem.IsWindows() ? WindowsVirtualMic.Installed :
        OperatingSystem.IsLinux() &&
        LinuxHost.Capture("pactl", new[] { "list", "short", "sinks" }).Contains(SinkName, StringComparison.Ordinal);

    public static bool Create(out string error)
    {
        error = "";
        if (OperatingSystem.IsWindows()) { if (WindowsVirtualMic.Installed) return true; error = "the Morphonic virtual microphone driver is not installed (Settings → Virtual microphone)"; return false; }
        if (!Supported) { error = "virtual microphones are a Linux feature here"; return false; }
        if (Exists())
        {
            // Already there. Left behind by a run of this data folder that
            // did not exit cleanly, it is adopted so Remove still takes it
            // down at exit; made by another running Morphonic (another data
            // folder) or by hand, it is used and left alone.
            Modules.Clear();
            var found = FindModules(LinuxHost.Capture("pactl", new[] { "list", "short", "modules" }));
            var record = ReadRecord();
            Modules.AddRange(ModulesToAdopt(found, record, ProcessAlive));
            Status = Modules.Count > 0
                ? "present (adopted modules " + string.Join(", ", Modules) + " of a run that did not exit cleanly)"
                : "present (made by another program or Morphonic; left alone)";
            return true;
        }
        var sink = LinuxHost.Capture("pactl", new[]
        {
            "load-module", "module-null-sink", "sink_name=" + SinkName,
            "sink_properties=device.description=" + SinkDescription,
        }).Trim();
        if (!int.TryParse(sink, out var sinkModule))
        {
            error = "pactl could not create the virtual output (is pipewire-pulse running? " + (sink.Length > 0 ? sink : "no answer") + ")";
            Status = "failed: " + error;
            return false;
        }
        Modules.Add(sinkModule);
        var source = LinuxHost.Capture("pactl", new[]
        {
            "load-module", "module-remap-source", "master=" + SinkName + ".monitor", "source_name=" + SourceName,
            "source_properties=device.description=" + SourceDescription,
        }).Trim();
        if (int.TryParse(source, out var sourceModule)) Modules.Add(sourceModule);
        else ErrorLog.WriteNote("VirtualMic", "the remapped source was not created (" + source + "); programs can still pick 'Monitor of " + SinkDescription + "'");
        WriteRecord();
        Status = "created (modules " + string.Join(", ", Modules) + ")";
        return true;
    }

    // Whether this process holds modules Remove would unload.
    public static bool Owned => Modules.Count > 0;

    public static void Remove()
    {
        if (!OperatingSystem.IsLinux()) return;   // the Windows driver is persistent
        // Newest first: the remapped source goes before the sink it reads.
        for (int i = Modules.Count - 1; i >= 0; i--)
            LinuxHost.Capture("pactl", new[] { "unload-module", Modules[i].ToString() });
        Modules.Clear();
        try { File.Delete(RecordPath); } catch { }
        Status = "removed";
    }

    // ── ownership record ───────────────────────────────────────────
    // <data dir>/virtualmic.json: the pid that loaded the modules and their
    // ids, written at creation and deleted at a clean exit. Its presence
    // after a start means that run never got to Remove.

    public sealed record Record(int Pid, int[] Modules);

    private static string RecordPath => Path.Combine(AppPaths.DataDir, "virtualmic.json");

    private static void WriteRecord()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(RecordPath, System.Text.Json.JsonSerializer.Serialize(new Record(Environment.ProcessId, Modules.ToArray())));
        }
        catch (Exception ex) { ErrorLog.WriteEntry("VirtualMic.Record", ex); }
    }

    private static Record? ReadRecord()
    {
        try
        {
            if (!File.Exists(RecordPath)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<Record>(File.ReadAllText(RecordPath));
        }
        catch { return null; }
    }

    private static bool ProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        if (pid == Environment.ProcessId) return true;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    // Pure: of the modules now carrying our names (`found`), those a dead
    // run of this data folder recorded as its own. Nothing is adopted
    // without a record, or while the recording process still runs.
    internal static List<int> ModulesToAdopt(IReadOnlyList<int> found, Record? record, Func<int, bool> alive)
    {
        var adopt = new List<int>();
        if (record == null || record.Modules == null || alive(record.Pid)) return adopt;
        foreach (var id in found)
            if (Array.IndexOf(record.Modules, id) >= 0) adopt.Add(id);
        return adopt;
    }

    // The ids of our modules in "pactl list short modules" output
    // ("<id>\t<module>\t<arguments>"): the null sink named SinkName and
    // the remapped source named SourceName, sink first.
    internal static List<int> FindModules(string pactlShortModules)
    {
        var sinks = new List<int>();
        var sources = new List<int>();
        foreach (var raw in (pactlShortModules ?? "").Split('\n'))
        {
            var parts = raw.Trim('\r').Split('\t');
            if (parts.Length < 3 || !int.TryParse(parts[0].Trim(), out var id)) continue;
            var args = " " + parts[2] + " ";
            if (parts[1] == "module-null-sink" && args.Contains(" sink_name=" + SinkName + " ", StringComparison.Ordinal)) sinks.Add(id);
            else if (parts[1] == "module-remap-source" && args.Contains(" source_name=" + SourceName + " ", StringComparison.Ordinal)) sources.Add(id);
        }
        sinks.AddRange(sources);
        return sinks;
    }
}
