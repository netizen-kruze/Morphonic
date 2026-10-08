using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Morphonic.Audio;

public sealed record AudioDevice(string Name, string Id);

// Delivers 16 kHz mono float audio from a microphone to the engine.
public interface IAudioInput : IDisposable
{
    string Backend { get; }
    event Action<Exception>? OnFailed;
    void Start(Action<float[]> onSamples16k);
    void Stop();
}

// Plays audio pulled from the engine at `sourceRate` on a device.
public interface IAudioOutput : IDisposable
{
    string Backend { get; }
    event Action<Exception>? OnFailed;
    void Start(Func<float[], int> pull, int sourceRate);
    void Stop();
}

// Microphones and outputs on both desktops. Entry 0 is always the system
// default, which needs no device id at all. A choice is remembered as
// index + name and re-resolved every time it's used, because the list
// reorders whenever hardware comes or goes — the name identifies the
// device; the id is what the backend opens.
//   Windows: WASAPI endpoints (NAudio's MMDeviceEnumerator).
//   Linux:   PipeWire nodes (pw-dump), or PulseAudio's lists (pactl) on a
//            machine still running PulseAudio.
public static class AudioDevices
{
    public const string DefaultName = "System default";

    private static readonly object Gate = new();
    private static AudioDevice[]? _inputs, _outputs;
    private static long _cacheAt;
    private const int CacheMs = 3000;

    public static AudioDevice[] Inputs() => Cached(ref _inputs, true);
    public static AudioDevice[] Outputs() => Cached(ref _outputs, false);

    public static void Invalidate()
    {
        lock (Gate) { _inputs = null; _outputs = null; }
    }

    private static AudioDevice[] Cached(ref AudioDevice[]? cache, bool inputs)
    {
        lock (Gate)
        {
            if (cache != null && Environment.TickCount64 - _cacheAt < CacheMs) return cache;
            var list = new List<AudioDevice> { new(DefaultName, "") };
            try { list.AddRange(Enumerate(inputs)); }
            catch (Exception ex) { ErrorLog.WriteEntry("AudioDevices", ex); }
            cache = list.ToArray();
            _cacheAt = Environment.TickCount64;
            return cache;
        }
    }

    public static string NameAt(AudioDevice[] devices, int index) =>
        index >= 0 && index < devices.Length ? devices[index].Name : "";

    // Single scored pass: an exact name match wins outright; failing that,
    // the best prefix match. With no name saved, a still-in-range index is
    // honored; every dead end resolves to the default (0).
    public static int Resolve(AudioDevice[] devices, int savedIndex, string? savedName) =>
        ResolveAmong(devices.Select(d => d.Name).ToArray(), savedIndex, savedName);

    internal static int ResolveAmong(string[] names, int savedIndex, string? savedName)
    {
        if (string.IsNullOrEmpty(savedName) || savedName == DefaultName)
            return savedIndex >= 0 && savedIndex < names.Length ? savedIndex : 0;
        int best = 0, bestRank = 0;
        for (int i = 0; i < names.Length; i++)
        {
            int rank = Rank(names[i], savedName);
            if (rank <= bestRank) continue;
            best = i;
            bestRank = rank;
            if (rank == 2) break;
        }
        return best;
    }

    private static int Rank(string reported, string saved)
    {
        if (reported.Length == 0) return 0;
        if (string.Equals(reported, saved, StringComparison.Ordinal)) return 2;
        return saved.StartsWith(reported, StringComparison.Ordinal) || reported.StartsWith(saved, StringComparison.Ordinal) ? 1 : 0;
    }

    private static IEnumerable<AudioDevice> Enumerate(bool inputs)
    {
        if (OperatingSystem.IsWindows()) return WasapiAudio.Enumerate(inputs);
        if (!OperatingSystem.IsLinux()) return Array.Empty<AudioDevice>();
        var dump = LinuxHost.Capture("pw-dump", Array.Empty<string>(), 5000);
        if (dump.Length > 0)
        {
            var fromPipeWire = ParsePwDump(dump, inputs);
            if (fromPipeWire.Count > 0) return fromPipeWire;
        }
        return ParsePactlShort(LinuxHost.Capture("pactl", new[] { "list", "short", inputs ? "sources" : "sinks" }, 5000), inputs);
    }

    // pw-dump: one JSON array of every PipeWire object. Capture devices are
    // nodes whose media.class is Audio/Source (virtual ones included — an
    // echo-cancelled mic is exactly what people pick); outputs are
    // Audio/Sink nodes. Monitors are listed by pw-dump as sources too and
    // are skipped.
    internal static List<AudioDevice> ParsePwDump(string json, bool inputs)
    {
        var list = new List<AudioDevice>();
        JArray objects;
        try { objects = JArray.Parse(json); } catch { return list; }
        string wanted = inputs ? "Audio/Source" : "Audio/Sink";
        foreach (var o in objects)
        {
            if (o["type"]?.ToString() != "PipeWire:Interface:Node") continue;
            var props = o["info"]?["props"];
            if (props == null) continue;
            var cls = props["media.class"]?.ToString() ?? "";
            if (!(cls == wanted || cls.StartsWith(wanted + "/", StringComparison.Ordinal))) continue;
            var name = props["node.name"]?.ToString() ?? "";
            if (name.Length == 0 || name.EndsWith(".monitor", StringComparison.Ordinal)) continue;
            var label = props["node.description"]?.ToString() ?? props["node.nick"]?.ToString() ?? name;
            list.Add(new AudioDevice(label, name));
        }
        return list;
    }

    // "48\talsa_input.pci-…\tPipeWire\ts32le 2ch 48000Hz\tSUSPENDED"
    internal static List<AudioDevice> ParsePactlShort(string text, bool inputs)
    {
        var list = new List<AudioDevice>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            var name = parts[1].Trim();
            if (name.Length == 0) continue;
            if (inputs && name.EndsWith(".monitor", StringComparison.Ordinal)) continue;
            list.Add(new AudioDevice(name, name));
        }
        return list;
    }

    public static IAudioInput OpenInput(AudioDevice? device)
    {
        if (OperatingSystem.IsWindows()) return new WasapiInput(device);
        return new PipeWireInput(device);
    }

    public static IAudioOutput OpenOutput(AudioDevice? device)
    {
        if (OperatingSystem.IsWindows()) return new WasapiOutput(device);
        return new PipeWireOutput(device);
    }
}
