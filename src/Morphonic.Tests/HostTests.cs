using System.Text;
using Morphonic.Audio;
using Morphonic.Rvc;
using Xunit;

namespace Morphonic.Tests;

// Settings durability: saves are atomic with a .bak of the previous good
// file, and a damaged file is recovered from that backup instead of
// silently becoming defaults.
public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "morphonic-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public SettingsTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
        AppSettings.PathOverride = _path;
        ErrorLog.PathOverride = Path.Combine(_dir, "error.log");
    }

    public void Dispose()
    {
        AppSettings.PathOverride = null;
        ErrorLog.PathOverride = null;
        AppSettings.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void SaveIsAtomicAndKeepsABackup()
    {
        var s = new AppSettings { VoiceId = "nova.onnx", PitchSemitones = 7 };
        Assert.True(s.Save());
        Assert.False(File.Exists(_path + ".tmp"));
        s.PitchSemitones = 9;
        Assert.True(s.Save());
        Assert.True(File.Exists(_path + ".bak"));
        Assert.Equal(9, AppSettings.Load().PitchSemitones);
        Assert.Equal("settings file", AppSettings.LastLoadSource);
    }

    [Fact]
    public void ACorruptFileIsRecoveredFromTheBackup()
    {
        var s = new AppSettings { VoiceId = "nova.onnx" };
        s.Save();
        s.BlockMs = 350;
        s.Save();
        File.WriteAllText(_path, "{ not json");
        var loaded = AppSettings.Load();
        Assert.Equal("nova.onnx", loaded.VoiceId);
        Assert.StartsWith("backup", AppSettings.LastLoadSource);
        Assert.True(File.Exists(_path + ".corrupt"));
    }

    [Fact]
    public void MissingFileMeansDefaults()
    {
        var loaded = AppSettings.Load();
        Assert.Equal(250, loaded.BlockMs);
        Assert.Equal("auto", loaded.Acceleration);
        Assert.StartsWith("defaults", AppSettings.LastLoadSource);
    }
}

public class BootSentinelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "morphonic-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public BootSentinelTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "boot.inprogress");
        BootSentinel.PathOverride = _path;
    }

    public void Dispose()
    {
        BootSentinel.PathOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void AnUnclearedStartIsReportedWithItsDetails()
    {
        Assert.Null(BootSentinel.Arm("1.0.0"));
        var previous = BootSentinel.Arm("1.0.1");
        Assert.NotNull(previous);
        Assert.Equal("1.0.0", previous!.Version);
        Assert.Equal(Environment.ProcessId, previous.Pid);
        BootSentinel.Clear();
        Assert.Null(BootSentinel.Arm("1.0.1"));
    }

    [Fact]
    public void AGarbledMarkerStillCountsAsUnfinished()
    {
        File.WriteAllText(_path, "??");
        var previous = BootSentinel.Arm("1.0.0");
        Assert.NotNull(previous);
        Assert.Equal("?", previous!.Version);
    }

    [Fact]
    public void CrashRecordFiltersEventsNamingTheApp()
    {
        const string xml =
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Application Error'/><EventID>1000</EventID>" +
            "<TimeCreated SystemTime='2026-10-08T00:00:00Z'/><Computer>SECRET-PC</Computer></System>" +
            "<EventData><Data Name='AppName'>Morphonic.exe</Data><Data Name='ExceptionCode'>c0000005</Data></EventData></Event>" +
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Application Error'/><EventID>1000</EventID></System>" +
            "<EventData><Data Name='AppName'>Other.exe</Data></EventData></Event>";
        var lines = CrashRecord.FilterXml(xml, "Morphonic");
        Assert.Single(lines);
        Assert.Contains("c0000005", lines[0]);
        Assert.DoesNotContain("SECRET-PC", lines[0]);
        Assert.Equal(new[] { "    Oct 08 Morphonic[123]: segfault" }, CrashRecord.FilterLines("No coredumps found.\nOct 08 Morphonic[123]: segfault\n", "Morphonic"));
        // the kernel keeps the first 15 characters of a process name
        Assert.Equal("Morphonic-1.0.0", CrashRecord.ProcessComm("/home/u/Morphonic-1.0.0-linux-x64"));
    }
}

public class PlatformTests
{
    [Fact]
    public void OsLabelsReadRight()
    {
        Assert.Equal("Windows 11 Pro 24H2 build 26200.1", MachineProfile.WindowsLabel("Windows 10 Pro", "24H2", 26200, 1));
        Assert.Equal("Fedora Linux 44 (Workstation Edition), kernel 6.15.4, GNOME on wayland",
            MachineProfile.LinuxLabel("Fedora Linux 44 (Workstation Edition)", "6.15.4", "wayland", "GNOME"));
        Assert.Equal("GeForce RTX 5080", MachineProfile.DeviceLabel("GB203 [GeForce RTX 5080]"));
        Assert.Equal("NVIDIA", MachineProfile.ShortVendor("NVIDIA Corporation"));
        var f = MachineProfile.PciFields("01:00.0 \"VGA compatible controller\" \"NVIDIA Corporation\" \"GB203 [GeForce RTX 5080]\" -ra1 \"ASUSTeK\" \"Device 1\"");
        Assert.NotNull(f);
        Assert.Equal("01:00.0", f!.Value.Slot);
    }

    [Fact]
    public void LdconfigCacheLinesYieldSonames()
    {
        var text = "1234 libs found in cache `/etc/ld.so.cache'\n" +
                   "\tlibwebkit2gtk-4.1.so.0 (libc6,x86-64) => /usr/lib64/libwebkit2gtk-4.1.so.0\n" +
                   "\tlibgtk-3.so.0 (libc6,x86-64) => /usr/lib64/libgtk-3.so.0\n";
        var parsed = LinuxHost.ParseLdconfig(text).ToArray();
        Assert.Equal(new[] { "libwebkit2gtk-4.1.so.0", "libgtk-3.so.0" }, parsed.Select(e => e.Name).ToArray());
        Assert.Equal("/usr/lib64/libgtk-3.so.0", parsed[1].Path);
    }

    [Fact]
    public void SteamOverlayPreloadIsStrippedFromHelperProcesses()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("true");
        psi.Environment["LD_PRELOAD"] = "/home/u/.local/share/Steam/ubuntu12_64/gameoverlayrenderer.so";
        psi.Environment["LD_LIBRARY_PATH"] = "/home/u/.steam/steam-runtime/lib";
        psi.Environment["SYSTEM_LD_LIBRARY_PATH"] = "/opt/lib";
        LinuxHost.StripSteamPreload(psi);
        Assert.False(psi.Environment.ContainsKey("LD_PRELOAD"));
        Assert.Equal("/opt/lib", psi.Environment["LD_LIBRARY_PATH"]);
    }

    [Fact]
    public void DesktopEntryQuotesOnlyWhenNeeded()
    {
        Assert.Equal("/home/u/.local/share/Morphonic/app/Morphonic", LinuxInstaller.ExecQuote("/home/u/.local/share/Morphonic/app/Morphonic"));
        Assert.Equal("\"/home/my user/Morphonic\"", LinuxInstaller.ExecQuote("/home/my user/Morphonic"));
        Assert.Equal("\"/p/100%%/Morphonic\"", LinuxInstaller.ExecQuote("/p/100%/Morphonic"));
        var entry = LinuxInstaller.DesktopEntry("/x/Morphonic");
        Assert.Contains("Name=Morphonic", entry);
        Assert.Contains("StartupWMClass=Morphonic", entry);
    }

    [Fact]
    public void HardwareJudgementsAreExplainedAndTiered()
    {
        Assert.Equal(Tier.Gpu, HardwareTier.Classify(true, 16, 32));
        Assert.Equal(Tier.CpuHigh, HardwareTier.Classify(false, 16, 32));
        Assert.Equal(Tier.CpuLow, HardwareTier.Classify(false, 4, 8));
        Assert.Equal(Tier.CpuMinimal, HardwareTier.Classify(false, 2, 4));
        Assert.True(HardwareTier.JudgeCuda(true, true, false, 580, 12.0).Usable);
        Assert.False(HardwareTier.JudgeCuda(false, true, false, null, null).Usable);
        Assert.False(HardwareTier.JudgeCuda(true, false, false, null, null).Usable);
        Assert.True(HardwareTier.JudgeCuda(true, false, true, null, null).Usable);   // WSL
        Assert.Contains("older", HardwareTier.JudgeCuda(true, true, false, 470, null).Verdict);
        Assert.Contains("Pascal", HardwareTier.JudgeCuda(true, true, false, 580, 5.2).Verdict);
        Assert.Equal(580, HardwareTier.ParseDriverMajor("NVRM version: NVIDIA UNIX Open Kernel Module for x86_64  580.82.07  Release Build"));
        Assert.Equal(8.9, HardwareTier.ParseComputeCapability("8.9\n"));
        Assert.Equal((160, 2500), HardwareTier.Recommended(Tier.Gpu));
    }

    [Fact]
    public void GpuPackPlansOnlyTheMissingParts()
    {
        var parts = new[] { GpuPack.OrtCuda, GpuPack.CudaRuntime, GpuPack.CuDnn };
        Assert.Equal(3, GpuPack.Plan(parts, _ => false, systemRuntime: false).Count);
        Assert.Equal(new[] { "ort-cuda" }, GpuPack.Plan(parts, _ => false, systemRuntime: true).Select(p => p.Id));
        Assert.Empty(GpuPack.Plan(parts, _ => true, systemRuntime: false));
        foreach (var p in GpuPack.OrtDirectML.Files.Concat(GpuPack.CuDnn.Files)) Assert.Equal(64, p.Sha256.Length);
        Assert.Equal(64, GpuPack.DirectML.Sha256.Length);
    }

    [Fact]
    public void CatalogHashesArePinned()
    {
        foreach (var m in ModelCatalog.Models)
        {
            Assert.Equal(64, m.File.Sha256.Length);
            Assert.Equal(64, m.Download.Sha256.Length);
            Assert.True(m.File.SizeBytes > 1_000_000);
            Assert.StartsWith("https://huggingface.co/", m.Download.Url);
            if (m.Assembled) Assert.NotNull(ModelAssembler.LoadRecipe(m.Recipe!));
            else Assert.Equal(m.File.Sha256, m.Download.Sha256);
        }
        Assert.NotNull(ModelCatalog.Find(ModelCatalog.EncoderId));
        Assert.Equal(ModelKind.Voice, ModelCatalog.Find(ModelCatalog.SampleVoiceId)!.Kind);
    }
}

public class AudioDeviceTests
{
    [Fact]
    public void PipeWireNodesAreListedByDescription()
    {
        const string dump = "[" +
            "{\"id\": 30, \"type\": \"PipeWire:Interface:Node\", \"info\": {\"props\": {\"media.class\": \"Audio/Sink\", \"node.name\": \"alsa_output.pci-0000_00_1f.3.analog-stereo\", \"node.description\": \"Built-in Audio Analog Stereo\"}}}," +
            "{\"id\": 31, \"type\": \"PipeWire:Interface:Node\", \"info\": {\"props\": {\"media.class\": \"Audio/Source\", \"node.name\": \"alsa_input.usb-Blue_Yeti-00.analog-stereo\", \"node.description\": \"Yeti Stereo Microphone\"}}}," +
            "{\"id\": 32, \"type\": \"PipeWire:Interface:Node\", \"info\": {\"props\": {\"media.class\": \"Audio/Source/Virtual\", \"node.name\": \"morphonic_mic\", \"node.description\": \"Morphonic-Voice-Mic\"}}}," +
            "{\"id\": 34, \"type\": \"PipeWire:Interface:Node\", \"info\": {\"props\": {\"media.class\": \"Audio/Source\", \"node.name\": \"alsa_output.pci-0000_00_1f.3.analog-stereo.monitor\"}}}," +
            "{\"id\": 35, \"type\": \"PipeWire:Interface:Node\", \"info\": {\"props\": {\"media.class\": \"Audio/Sink\", \"node.name\": \"morphonic_voice\", \"node.description\": \"Morphonic-Voice\"}}}" +
            "]";
        var inputs = AudioDevices.ParsePwDump(dump, true);
        Assert.Equal(new[] { "Yeti Stereo Microphone", "Morphonic-Voice-Mic" }, inputs.Select(d => d.Name));
        Assert.Equal("alsa_input.usb-Blue_Yeti-00.analog-stereo", inputs[0].Id);
        var outputs = AudioDevices.ParsePwDump(dump, false);
        Assert.Equal(new[] { "Built-in Audio Analog Stereo", "Morphonic-Voice" }, outputs.Select(d => d.Name));
        Assert.Equal(VirtualMic.SinkName, outputs[1].Id);
        Assert.Empty(AudioDevices.ParsePwDump("not json", true));
    }

    [Fact]
    public void PulseAudioSourcesSkipMonitors()
    {
        var text = "48\talsa_input.pci-0000_00_1f.3.analog-stereo\tPipeWire\ts32le 2ch 48000Hz\tSUSPENDED\n" +
                   "47\talsa_output.pci-0000_00_1f.3.analog-stereo.monitor\tPipeWire\ts32le 2ch 48000Hz\tIDLE\n";
        Assert.Single(AudioDevices.ParsePactlShort(text, true));
        Assert.Equal(2, AudioDevices.ParsePactlShort(text, false).Count);
    }

    [Fact]
    public void SavedDevicesResolveByNameThenIndex()
    {
        var names = new[] { AudioDevices.DefaultName, "Headset Microphone (Index)", "USB Desk Mic" };
        Assert.Equal(2, AudioDevices.ResolveAmong(names, 1, "USB Desk Mic"));
        Assert.Equal(1, AudioDevices.ResolveAmong(names, 5, "Headset Microphone (Index) Long Name"));
        Assert.Equal(0, AudioDevices.ResolveAmong(names, 1, "Gone Mic"));
        Assert.Equal(2, AudioDevices.ResolveAmong(names, 2, ""));
        Assert.Equal(0, AudioDevices.ResolveAmong(names, 9, ""));
    }

    [Fact]
    public void AWaylandDisplayCountsOnlyWithItsSocket()
    {
        var dir = Path.Combine(Path.GetTempPath(), "morphonic-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "wayland-0"), "");
            Assert.True(LinuxHost.WaylandSocketPresent("wayland-0", dir));
            Assert.False(LinuxHost.WaylandSocketPresent("wayland-9", dir));
            Assert.False(LinuxHost.WaylandSocketPresent("wayland-0", null));
            Assert.True(LinuxHost.WaylandSocketPresent(Path.Combine(dir, "wayland-0"), null));
            Assert.False(LinuxHost.WaylandSocketPresent(null, dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void VirtualMicrophoneModulesAreFoundInPactlOutput()
    {
        // "pactl list short modules": id, module, arguments — ours by their names, sink first
        var text = "3\tmodule-null-sink\tsink_name=other sink_properties=device.description=Other\n" +
                   "17\tmodule-remap-source\tmaster=morphonic_voice.monitor source_name=morphonic_mic source_properties=device.description=Morphonic-Voice-Mic\n" +
                   "16\tmodule-null-sink\tsink_name=morphonic_voice sink_properties=device.description=Morphonic-Voice\n" +
                   "20\tmodule-null-sink\tsink_name=morphonic_voice_2\n" +
                   "garbage line\n";
        Assert.Equal(new[] { 16, 17 }, VirtualMic.FindModules(text));
        Assert.Empty(VirtualMic.FindModules(""));
        Assert.Empty(VirtualMic.FindModules("1\tmodule-null-sink\tsink_name=x\n"));
    }

    [Fact]
    public void LeftoverVirtualMicrophoneIsAdoptedOnlyFromADeadRunOfThisFolder()
    {
        var found = new[] { 16, 17 };
        // no record (another data folder's instance, or made by hand): left alone
        Assert.Empty(VirtualMic.ModulesToAdopt(found, null, _ => false));
        // the recording process still runs: left alone
        Assert.Empty(VirtualMic.ModulesToAdopt(found, new VirtualMic.Record(4242, new[] { 16, 17 }), pid => pid == 4242));
        // dead owner: its modules, and only those still carrying our names
        Assert.Equal(new[] { 16, 17 }, VirtualMic.ModulesToAdopt(found, new VirtualMic.Record(4242, new[] { 16, 17 }), _ => false));
        Assert.Equal(new[] { 17 }, VirtualMic.ModulesToAdopt(found, new VirtualMic.Record(4242, new[] { 9, 17 }), _ => false));
        Assert.Empty(VirtualMic.ModulesToAdopt(found, new VirtualMic.Record(4242, Array.Empty<int>()), _ => false));
    }

    [Fact]
    public void RecorderAndPlayerCommandsCarryTargetAndFormat()
    {
        // pw-record twice: with --raw (PipeWire 1.2+), then without (1.0, where a pipe is raw anyway)
        var rec = PipeWireAudio.CaptureCommands("alsa_input.usb-mic").ToList();
        Assert.Equal(new[] { "pw-record", "pw-record (PipeWire 1.0, no --raw)", "parec", "arecord" }, rec.Select(c => c.Label));
        Assert.All(rec.Take(2), c => Assert.Equal("pw-record", c.File));
        Assert.Contains("--raw", rec[0].Args);
        Assert.DoesNotContain("--raw", rec[1].Args);
        Assert.Contains("--target=alsa_input.usb-mic", rec[0].Args);
        Assert.Contains("--target=alsa_input.usb-mic", rec[1].Args);
        Assert.Contains("--rate=16000", rec[0].Args);
        Assert.Equal("-", rec[1].Args[^1]);
        // a chosen device must not be silently swapped for the default when it vanishes
        Assert.Equal(new[] { "-P", PipeWireAudio.DontReconnect }, rec[0].Args.SkipWhile(a => a != "-P").Take(2));
        Assert.DoesNotContain("-P", PipeWireAudio.CaptureCommands(null).First().Args);
        var play = PipeWireAudio.PlaybackCommands("morphonic_voice", 40000).ToList();
        Assert.Equal(new[] { "pw-play", "pw-play (PipeWire 1.0, no --raw)", "pacat", "aplay" }, play.Select(c => c.Label));
        Assert.Contains("--rate=40000", play[0].Args);
        Assert.Contains("--target=morphonic_voice", play[0].Args);
        Assert.DoesNotContain("--raw", play[1].Args);
        Assert.DoesNotContain(PipeWireAudio.PlaybackCommands(null, 48000).First().Args, a => a.StartsWith("--target", StringComparison.Ordinal));
    }

    [Fact]
    public void VoiceMetadataIsReadWithoutLoadingTheGraph()
    {
        // A minimal ModelProto: ir_version (field 1), a fake 1 MB graph
        // (field 7, skipped), and one metadata_props entry (field 14).
        var dir = Path.Combine(Path.GetTempPath(), "morphonic-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var meta = Encoding.UTF8.GetBytes("{\"format\":\"morphonic-rvc-voice\",\"sr\":48000,\"speakers\":3,\"skipHead\":true,\"name\":\"Nova\",\"source\":\"nova.pth\"}");
            var key = Encoding.UTF8.GetBytes("morphonic");
            using var ms = new MemoryStream();
            ms.Write(new byte[] { 0x08, 0x08 });                           // field 1 varint 8
            ms.Write(new byte[] { 0x3A }); WriteVarint(ms, 1_000_000); ms.Write(new byte[1_000_000]); // field 7
            var entry = new MemoryStream();
            entry.Write(new byte[] { 0x0A }); WriteVarint(entry, key.Length); entry.Write(key);
            entry.Write(new byte[] { 0x12 }); WriteVarint(entry, meta.Length); entry.Write(meta);
            ms.Write(new byte[] { 0x72 }); WriteVarint(ms, (int)entry.Length); ms.Write(entry.ToArray());   // field 14
            var path = Path.Combine(dir, "nova.onnx");
            File.WriteAllBytes(path, ms.ToArray());
            var m = VoiceModel.ReadMetadata(path);
            Assert.Equal(48000, m.SampleRate);
            Assert.Equal(3, m.Speakers);
            Assert.True(m.SkipHead);
            Assert.Equal("Nova", m.Name);

            AppPaths.DataDir = dir;
            Directory.CreateDirectory(AppPaths.VoiceDir);
            File.Move(path, Path.Combine(AppPaths.VoiceDir, "nova.onnx"));
            File.WriteAllBytes(Path.Combine(AppPaths.VoiceDir, "moth.pth"), new byte[10]);
            var entries = VoiceLibrary.Scan();
            Assert.Equal(2, entries.Count);
            Assert.Equal("pth", entries.First(e => e.Id == "moth.pth").Kind);
            Assert.Equal(48000, entries.First(e => e.Id == "nova.onnx").SampleRate);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void WriteVarint(Stream s, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }
}
