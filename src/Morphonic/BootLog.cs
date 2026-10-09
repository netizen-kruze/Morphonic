using System;
using System.IO;
using System.Linq;

namespace Morphonic;

// Snapshot of the last launch, overwritten every boot:
// <data dir>/last_boot.log. For "it started but looked wrong" reports —
// records what the app actually saw at startup (the machine, which
// settings file it read, the devices, the voice, the acceleration) so the
// cause is visible without a debugger. Later events of note (a session
// summary, a pace change, a speed check) are appended.
public static class BootLog
{
    // Tests point this at a temp file.
    internal static string? PathOverride { get; set; }

    private static string LogPath => PathOverride ?? AppPaths.BootLogPath;

    public static void Write(string version, string[] args, AppSettings settings, string machine,
        int inputs, int outputs, int voices, string acceleration)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            int entries = -1;
            try { entries = Directory.EnumerateFileSystemEntries(AppPaths.DataDir).Count(); } catch { }
            var lines = new[]
            {
                $"started:        {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"version:        {version}",
                $"machine:        {machine}",
                $"exe:            {Environment.ProcessPath}",
                $"args:           {(args.Length == 0 ? "(none)" : string.Join(" ", args))}",
                $"run before:     {(AppSettings.HasRunBefore() ? "yes" : "no")} (per-user marker, not per data folder)",
                $"data folder:    {AppPaths.DataDir} — {(entries < 0 ? "NOT listable" : entries + " entries, the app's own included")}",
                $"settings from:  {AppSettings.LastLoadSource}",
                $"voice:          {(settings.VoiceId.Length == 0 ? "(none chosen)" : settings.VoiceId)} ({voices} voice file(s) in the library)",
                $"microphone:     {Device(settings.InputDeviceIndex, settings.InputDeviceName)} ({inputs} input device(s) listed)",
                $"output:         {Device(settings.OutputDeviceIndex, settings.OutputDeviceName)} ({outputs} output device(s) listed)",
                $"pitch/block:    {settings.PitchSemitones:+0;-0;0} st, block {settings.BlockMs} ms, context {settings.ExtraMs} ms, crossfade {settings.CrossfadeMs} ms",
                $"acceleration:   {acceleration}",
            };
            File.WriteAllLines(LogPath, lines);
        }
        catch { /* diagnostics must never affect startup */ }
    }

    private static string Device(int index, string name) =>
        index <= 0 || name.Length == 0 ? "system default" : $"index {index} '{name}'";

    public static void Append(string line)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {line}" + Environment.NewLine); }
        catch { }
    }
}
