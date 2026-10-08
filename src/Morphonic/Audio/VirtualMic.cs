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
        if (Exists()) { Status = "present"; return true; }
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
        Status = "created (modules " + string.Join(", ", Modules) + ")";
        return true;
    }

    public static void Remove()
    {
        if (!OperatingSystem.IsLinux()) return;   // the Windows driver is persistent
        foreach (var m in Modules)
            LinuxHost.Capture("pactl", new[] { "unload-module", m.ToString() });
        Modules.Clear();
        Status = "removed";
    }
}
