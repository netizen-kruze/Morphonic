using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Photino.NET;
using Morphonic.Audio;
using Morphonic.Rvc;

namespace Morphonic;

// Photino host: a WebView2 (Windows) or WebKitGTK (Linux) window showing
// wwwroot/, speaking the {action,...} / {type,payload} JSON contract.
// Outbound messages queue in a channel until the page has loaded and sent
// its first request, then drain through a single dispatcher so
// window-thread marshaling stays in one place.
//
// The gate is the page's first message, NOT Photino's WindowCreated: that
// event fires right after the native window is constructed, before the web
// view is attached, and a message sent into a web view that is not there
// yet is a native crash.
internal static class Program
{
    private static PhotinoWindow _window = null!;
    private static readonly Channel<string> ToUi = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private static readonly TaskCompletionSource UiReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static volatile bool _exiting;
    private static volatile bool _windowUp;
    private static long _bootTick;
    private static long _lastPingAt;
    private static bool _autoStart;
    private const int PageConnectWatchdogMs = 20_000;
    // The page pings every 5 s. A browser that throttles a hidden page's
    // timers (the window sits in the tray) may slow them to once a minute;
    // the timeout stays well clear of that, so a healthy page is never
    // reloaded for being hidden.
    private const int HeartbeatTimeoutMs = 150_000;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    private const int SW_HIDE = 0, SW_SHOW = 5;

    [STAThread]
    private static int Main(string[] args)
    {
        _bootTick = Environment.TickCount64;
        InstallCrashHandlers();
        if (args.Any(a => a is "--help" or "-h" or "--bench" or "--install-gpu" or "--fetch-models" or "--pack-offline" or "--install" or "--uninstall" or "--convert" or "--report")) WindowsHost.AttachParentConsole();
        if (args.Contains("--help") || args.Contains("-h")) { Console.WriteLine(Usage); return 0; }
        // A support bundle from the terminal: works when the window cannot open.
        if (args.Contains("--report"))
        {
            if (ArgValue(args, "--data-dir") is { } rd) AppPaths.DataDir = Path.GetFullPath(rd);
            try { Console.WriteLine("report written: " + SupportReport.Write(Version)); return 0; }
            catch (Exception ex) { Console.Error.WriteLine("could not write the report: " + ex.Message); return 1; }
        }
        if (args.Contains("--install")) return Report(LinuxInstaller.Install());
        if (args.Contains("--uninstall")) return Report(LinuxInstaller.Uninstall(purge: args.Contains("--purge")));
        if (ArgValue(args, "--data-dir") is { } dataDir) AppPaths.DataDir = Path.GetFullPath(dataDir);
        // The elevated half of Settings -> Virtual microphone (Windows):
        // started by the running app with "runas"; the outcome goes to --log.
        if (args.Contains("--install-virtual-mic") || args.Contains("--remove-virtual-mic")) return VirtualMicElevated(args);
        // Test hook: the update check against a local server instead of github.com.
        if (ArgValue(args, "--update-feed") is { } feed) UpdateCheck.FeedUrl = feed;
        if (args.Contains("--bench")) return RunBench();
        if (args.Contains("--install-gpu")) return InstallGpu();
        if (args.Contains("--fetch-models")) return FetchModels(args.Contains("--no-voice"));
        if (args.Contains("--pack-offline")) return PackOffline(args);
        if (args.Contains("--convert")) return FileConvert.Run(args);
        // Test hooks (tools/smoke.*): start the voice as soon as the page is
        // up, and leave cleanly after N seconds so the session summary is
        // written.
        bool autoStart = args.Contains("--auto-start");
        int runSeconds = int.TryParse(ArgValue(args, "--run-seconds"), out var rs) ? rs : 0;
        if (LinuxHost.ReexecWithoutSteamPreload(args) is int reexecCode) return reexecCode;
        WaitForPredecessor(args);
        // Live audio is latency work: trade a little memory for GC pauses
        // that stay short during long sessions.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        // Two instances would double-capture the mic and race the settings
        // file. A named mutex on Windows; a lock file on Linux, where .NET
        // scopes named mutexes to the login session.
        bool first = true;
        using var mutex = OperatingSystem.IsWindows() ? new Mutex(initiallyOwned: true, "Morphonic-single-instance", out first) : null;
        if (mutex != null && !first) return 0;
        using var instanceLock = OperatingSystem.IsLinux() ? LinuxHost.TryLockInstance(Path.Combine(AppPaths.DataDir, "instance.lock")) : null;
        if (OperatingSystem.IsLinux() && instanceLock == null)
        {
            Console.Error.WriteLine("Morphonic is already running (instance.lock in the data folder is held).");
            LinuxHost.Notify("Morphonic", "Morphonic is already running.");
            return 0;
        }

        if (OperatingSystem.IsWindows())
        {
            // Zero-telemetry policy: this app's only network traffic is the
            // user-initiated downloads. WebView2's own background networking
            // is switched off via its documented environment hook.
            // Test hook (tools/ui_walkthrough.py): --debug-port N exposes the
            // page over the DevTools protocol on localhost so the real window
            // can be driven and screenshotted. Off unless asked for.
            int debugPort = int.TryParse(ArgValue(args, "--debug-port"), out var dp) ? dp : 0;
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                "--disable-background-networking --disable-component-update " +
                "--disable-domain-reliability --disable-breakpad --no-pings" +
                (debugPort > 0 ? $" --remote-debugging-port={debugPort}" : ""));
            if (!WindowsHost.WebView2Present())
            {
                int pick = WindowsHost.MessageBox(
                    "Morphonic needs Microsoft Edge WebView2, which isn't installed.\n\n" +
                    "Open the Microsoft download page now? (Small installer, one click.)",
                    WindowsHost.MB_YESNO | WindowsHost.MB_ICONINFORMATION);
                if (pick == WindowsHost.IDYES) WindowsHost.OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");
                return 1;
            }
        }
        else
        {
            var missing = LinuxHost.MissingUiDependencies();
            if (missing.Count > 0)
            {
                LinuxHost.Alert("Morphonic",
                    "Morphonic needs " + string.Join(", ", missing.Select(m => m.Library)) + " to show its window.\n\n" +
                    "Install with:\n  sudo dnf install " + string.Join(" ", missing.Select(m => m.Package).Distinct()));
                return 1;
            }
            if (!LinuxHost.HasDisplay)
            {
                const string noDisplay = "no display (neither WAYLAND_DISPLAY nor DISPLAY is set) — the window cannot open";
                Console.Error.WriteLine("Morphonic: " + noDisplay + ".");
                ErrorLog.WriteNote("Display", noDisplay);   // so --report can say why a launch went nowhere
                return 1;
            }
            // WebKitGTK's DMA-BUF renderer is known to blank or crash on the
            // proprietary NVIDIA driver; its GL path works there.
            if (Environment.GetEnvironmentVariable("WEBKIT_DISABLE_DMABUF_RENDERER") == null && File.Exists("/proc/driver/nvidia/version"))
                LinuxHost.SetProcessEnv("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
        }

        var version = Version;
        var unfinished = BootSentinel.Arm(version);
        if (unfinished != null) CrashRecord.CollectInBackground(unfinished);
        bool uiOk = false;
        try { ExtractUiAssets(); uiOk = true; }
        catch (Exception ex) { ErrorLog.WriteEntry("ExtractUiAssets", ex); }
        if (!uiOk || !File.Exists(Path.Combine(AppPaths.UiDir, "wwwroot", "index.html")))
        {
            var text = $"Morphonic couldn't unpack its interface files into {AppPaths.UiDir}.\n\nDetails were written to {AppPaths.ErrorLogPath}.";
            if (OperatingSystem.IsWindows()) WindowsHost.MessageBox(text, WindowsHost.MB_ICONERROR); else LinuxHost.Alert("Morphonic", text);
            BootSentinel.Clear();
            return 1;
        }
        VoiceLibrary.UnpackTools();
        Download.SetUserAgent($"Morphonic/{version}");

        var machineTask = Task.Run(() => MachineProfile.Describe());
        using var ctrl = new VoiceController(SendToUi);
        _controller = ctrl;
        OnnxHost.Configure(ctrl.Settings.Acceleration);
        if (VirtualMic.Supported && ctrl.Settings.VirtualMic)
        {
            if (!VirtualMic.Create(out var vmError)) ErrorLog.WriteNote("VirtualMic", vmError);
            AudioDevices.Invalidate();
        }

        using var tray = OperatingSystem.IsWindows() ? new TrayService(Path.Combine(AppPaths.UiDir, "app.ico")) : null;
        // Windows: the WebView2 profile lives in the data folder. Photino's
        // default is one folder shared by every Photino app on the machine,
        // and WebView2 refuses to start when that folder is already in use
        // with different browser arguments: two apps open at once, or a
        // lingering browser process, would leave this window blank.
        // Linux: Photino's WebKitGTK backend ignores this path and uses the
        // default web context, which keeps its storage and cache under
        // ~/.local/share/<binary name>/ and ~/.cache/<binary name>/
        // (LinuxInstaller.Uninstall --purge removes them).
        _window = new PhotinoWindow()
            .SetLogVerbosity(0)
            .SetTemporaryFilesPath(Path.Combine(AppPaths.DataDir, "webview"))
            .SetTitle("Morphonic")
            .SetSize(960, 660)
            .SetMinSize(760, 540)
            .Center()
            .SetResizable(true)
            .RegisterWindowCreatedHandler((_, _) => _windowUp = true)
            .RegisterWebMessageReceivedHandler((_, raw) => OnUiMessage(ctrl, raw));
        if (OperatingSystem.IsWindows())
        {
            // Closing the window hides to the tray; the voice keeps running.
            _window.RegisterWindowClosingHandler((_, _) =>
            {
                if (_exiting) return false;
                ShowWindow(_window.WindowHandle, SW_HIDE);
                return true;
            });
            var ico = Path.Combine(AppPaths.UiDir, "app.ico");
            if (File.Exists(ico)) _window.SetIconFile(ico);
            if (tray != null)
            {
                tray.OnOpen += () =>
                {
                    if (!_windowUp) return;
                    _window.Invoke(() => { ShowWindow(_window.WindowHandle, SW_SHOW); SetForegroundWindow(_window.WindowHandle); });
                };
                // Exit from the tray: as on Linux, a start still loading its
                // voice must not open the devices after this.
                tray.OnExit += () => { _exiting = true; ctrl.BeginShutdown(); CloseWindowOrExit(); };
            }
        }
        else
        {
            // Closing the window quits; a start still loading its voice must
            // not open the devices after the close.
            _window.RegisterWindowClosingHandler((_, _) => { _exiting = true; ctrl.BeginShutdown(); return false; });
            var png = Path.Combine(AppPaths.UiDir, "app.png");
            if (File.Exists(png)) _window.SetIconFile(png);
            try
            {
                _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; RequestExit("SIGTERM"); });
                _sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; RequestExit("SIGINT"); });
                // The terminal it was started from closed: the same clean exit
                // (session summary, virtual microphone removed), not a kill.
                _sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => { ctx.Cancel = true; RequestExit("SIGHUP"); });
            }
            catch (Exception ex) { ErrorLog.WriteEntry("SignalHandlers", ex); }
        }
        ctrl.PickFiles = () => _window.ShowOpenFile("Choose an RVC voice (.pth, .onnx, or the .zip a voice library gave you)", null, true,
            new[] { ("RVC voice", new[] { "*.pth", "*.onnx", "*.zip" }) });
        ctrl.RestartRequested += RestartApp;
        ctrl.ArmSafeBoot(unfinished != null);
        _autoStart = autoStart && unfinished == null;   // a safe boot never auto-starts
        using var runTimer = runSeconds > 0
            ? new System.Threading.Timer(_ => RequestExit($"--run-seconds {runSeconds}"), null, runSeconds * 1000, Timeout.Infinite)
            : null;

        _ = RunUiDispatcherAsync();
        BootLog.Write(version, args, ctrl.Settings,
            machineTask.Wait(3000) ? machineTask.Result : "(still profiling — see the machine: line below)",
            AudioDevices.Inputs().Length - 1, AudioDevices.Outputs().Length - 1, VoiceLibrary.Scan().Count, OnnxHost.Status);
        if (!machineTask.IsCompleted)
            _ = machineTask.ContinueWith(t => BootLog.Append("machine: " + (t.IsCompletedSuccessfully ? t.Result : "profile failed")));
        BootLog.Append("gpu: " + HardwareTier.GpuVerdict + "; pack " + (GpuPack.IsInstalled() ? "installed" : "not installed") + "; runtime: " + GpuPack.RuntimeStatus);
        BootLog.Append("components: " + (ModelManager.ComponentsReady() ? "ready" : "missing") + "; virtual mic: " + VirtualMic.Status);
        if (unfinished != null)
            BootLog.Append($"previous start ({unfinished.Version} at {unfinished.StartedAt:HH:mm:ss}, pid {unfinished.Pid}) " +
                           "never reached the window — crashed or was killed; the crash record goes to error.log");
        AppSettings.MarkHasRun(version);

        using var watchdog = new System.Threading.Timer(_ =>
        {
            if (UiReady.Task.IsCompleted) return;
            var note = $"page has not connected {PageConnectWatchdogMs / 1000} s after start — the window is probably blank";
            BootLog.Append("ui: " + note);
            ErrorLog.WriteNote("Ui", note);
        }, null, PageConnectWatchdogMs, Timeout.Infinite);

        // After the page connected it pings every 5 s; a web process that
        // crashed goes silent while the voice runs on — the page is
        // reloaded and the event logged, at most once a minute.
        var indexPath = Path.Combine(AppPaths.UiDir, "wwwroot", "index.html");
        using var heartbeat = new System.Threading.Timer(_ =>
        {
            if (!UiReady.Task.IsCompleted || _exiting) return;
            long silent = Environment.TickCount64 - Interlocked.Read(ref _lastPingAt);
            if (silent < HeartbeatTimeoutMs) return;
            Interlocked.Exchange(ref _lastPingAt, Environment.TickCount64);
            var note = $"ui: no heartbeat from the page for {silent / 1000} s — reloading it";
            BootLog.Append(note);
            ErrorLog.WriteNote("Ui", note);
            try { _window.Invoke(() => _window.Load(indexPath)); } catch (Exception ex) { ErrorLog.WriteEntry("UiReload", ex); }
        }, null, HeartbeatTimeoutMs, 15_000);

        _window.Load(indexPath);
        _window.WaitForClose();
        _exiting = true;
        ctrl.Stop("exiting");
        if (VirtualMic.Supported)
        {
            var had = VirtualMic.Status;
            bool mine = VirtualMic.Owned;
            VirtualMic.Remove();
            if (OperatingSystem.IsLinux() && mine) BootLog.Append($"virtual mic: {VirtualMic.Status} (was: {had})");
        }
        BootSentinel.Clear();
        return 0;
    }

    private static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private const string Usage =
        "Morphonic — real-time RVC voice changer\n" +
        "  Morphonic                              open the app\n" +
        "  Morphonic --bench                      Settings > Speed check from a terminal, report on stdout\n" +
        "  Morphonic --install-gpu                download GPU acceleration from a terminal, progress on stdout\n" +
        "  Morphonic --fetch-models [--no-voice]  the Models screen's downloads from a terminal (components, and the sample voice)\n" +
        "  Morphonic --convert <in.wav> <out.wav> [--pitch <semitones>]\n" +
        "                                     run a recording through the live pipeline with the chosen voice, to judge it without a microphone\n" +
        "  Morphonic --pack-offline <models dir> <out file> [--base <binary>]\n" +
        "                                     write the offline build: this binary (or --base) with the catalog's model files inside (build.ps1 -Offline)\n" +
        "  Morphonic --install                    (Linux) copy this binary to ~/.local/share/Morphonic/app and add it to the app grid\n" +
        "  Morphonic --uninstall [--purge]        (Linux) remove that install; --purge also removes settings, voices and models\n" +
        "  Morphonic --report                     write a support bundle (logs, settings, machine) as a zip in the data folder, print its path\n" +
        "  --data-dir <dir>                   use another data folder (tools/smoke.*)\n" +
        "  --debug-port <n>                   (Windows) expose the page over the DevTools protocol for tools/ui_walkthrough.py\n" +
        "  --update-feed <url>                point Settings > Check for updates at another release document (tests)\n" +
        "  --install-virtual-mic <dir> [--inf <name> --hwid <id>]\n" +
        "                                     (Windows, administrator) install the virtual microphone driver package in <dir>; Settings does this for you\n" +
        "  --remove-virtual-mic [--inf <name> --hwid <id>]\n" +
        "                                     (Windows, administrator) remove it\n";

    private static int Report(LinuxInstaller.Result result)
    {
        Console.WriteLine(result.Message);
        return result.Ok ? 0 : 1;
    }

    // "--pack-offline <modelsDir> <out> [--base <binary>]": the offline
    // build, a copy of this binary (or of --base, e.g. the Linux file) with
    // the catalog's verified model files appended (OfflinePayload).
    private static int PackOffline(string[] args)
    {
        int i = Array.IndexOf(args, "--pack-offline");
        if (i < 0 || i + 2 >= args.Length) { Console.Error.WriteLine("usage: --pack-offline <models dir> <out file> [--base <binary>]"); return 2; }
        var modelsDir = Path.GetFullPath(args[i + 1]);
        var outPath = Path.GetFullPath(args[i + 2]);
        var basePath = ArgValue(args, "--base") is { } b ? Path.GetFullPath(b) : Environment.ProcessPath!;
        try
        {
            Console.WriteLine($"packing {Path.GetFileName(basePath)} + models from {modelsDir} -> {outPath}");
            var report = OfflinePayload.PackCatalog(basePath, modelsDir, outPath);
            Console.WriteLine(report);
            Console.WriteLine($"done: {outPath} ({new FileInfo(outPath).Length / 1_000_000} MB), carries {OfflinePayload.Read(outPath)?.Count ?? 0} model(s)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("pack failed: " + ex.Message);
            try { File.Delete(outPath); } catch { }
            return 1;
        }
    }

    // "--fetch-models": the Models screen's component (and sample voice)
    // downloads from a terminal, for headless setups and the smoke tests.
    private static int FetchModels(bool noVoice)
    {
        Download.SetUserAgent($"Morphonic/{Version}");
        var manager = new ModelManager();
        string? lastLine = null;
        manager.OnProgress += (id, done, total, stage) =>
        {
            var verb = stage == ModelStage.Assemble ? "assembling" : OfflinePayload.Has(id) ? "unpacking" : "downloading";
            var line = $"  {verb} {id}: {done / 1_000_000} / {total / 1_000_000} MB";
            if (line == lastLine) return;
            lastLine = line;
            Console.Write("\r" + line.PadRight(60));
        };
        int failed = 0;
        foreach (var m in ModelCatalog.Models)
        {
            if (noVoice && m.Kind == ModelKind.Voice) continue;
            if (ModelManager.IsInstalled(m)) { Console.WriteLine($"{m.DisplayName}: already installed"); continue; }
            Console.WriteLine(OfflinePayload.Has(m.Id)
                ? $"{m.DisplayName}: {m.SizeBytes / 1_000_000} MB included in this build, no download"
                : $"{m.DisplayName}: {m.DownloadBytes / 1_000_000} MB from {m.Download.Url}" + (m.Assembled ? " (assembled here)" : ""));
            var (ok, error) = manager.DownloadAsync(m.Id).GetAwaiter().GetResult();
            Console.WriteLine();
            Console.WriteLine(ok ? $"  {m.File.FileName}: verified, {m.SizeBytes / 1_000_000} MB" : "  FAILED: " + error);
            if (!ok) failed++;
        }
        var (verified, bad) = ModelManager.VerifyFiles();
        Console.WriteLine($"verify: {verified} file(s) match their pinned hashes" + (bad.Count > 0 ? "; BAD: " + string.Join(", ", bad) : ""));
        return failed == 0 && bad.Count == 0 ? 0 : 1;
    }

    // "--install-gpu": the Models screen's download from a terminal.
    private static int InstallGpu()
    {
        Download.SetUserAgent($"Morphonic/{Version}");
        if (GpuPack.IsInstalled())
        {
            Console.WriteLine("GPU acceleration is already installed in " + GpuPack.InstallDir);
        }
        else
        {
            Console.WriteLine($"Downloading GPU acceleration ({GpuPack.SizeBytes / 1_000_000} MB) into {GpuPack.InstallDir} ...");
            long lastPct = -1;
            var (ok, error) = GpuPack.DownloadAsync((received, total) =>
            {
                long pct = total > 0 ? received * 100 / total : 0;
                if (pct / 5 == lastPct / 5 && received != total) return;
                lastPct = pct;
                Console.WriteLine($"  {received / 1_000_000} of {total / 1_000_000} MB ({pct}%)");
            }).GetAwaiter().GetResult();
            if (!ok) { Console.WriteLine("GPU acceleration: " + error); return 1; }
            Console.WriteLine("GPU acceleration installed — it loads on the next start.");
        }
        HardwareTier.Detect();
        GpuPack.PreloadRuntime();
        Console.WriteLine("gpu: " + HardwareTier.GpuVerdict + "; runtime: " + GpuPack.RuntimeStatus);
        return 0;
    }

    // "--bench": the speed check without a window — the same controller
    // code path, the table on stdout. Honors the saved acceleration setting.
    private static int RunBench()
    {
        VoiceLibrary.UnpackTools();
        using var done = new ManualResetEventSlim();
        int code = 1;
        using var ctrl = new VoiceController((type, payload) =>
        {
            if (type == "toast") { Console.WriteLine(JObject.FromObject(payload ?? new { })["msg"]?.ToString()); return; }
            if (type != "bench") return;
            var json = JObject.FromObject(payload ?? new { });
            if (json["running"]?.Value<bool>() == true)
            {
                var note = json["note"]?.ToString();
                if (!string.IsNullOrEmpty(note)) Console.WriteLine("... " + note);
                return;
            }
            if (json["error"] != null) Console.WriteLine("speed check failed: " + json["error"]);
            else
            {
                Console.WriteLine(json["machine"]?.ToString());
                foreach (var row in json["rows"] as JArray ?? new JArray())
                    Console.WriteLine($"{row["label"],-40} load {row["loadMs"],6} ms   avg pass {row["avgPassMs"],5} ms   max {row["maxPassMs"],5} ms   " +
                                      $"per {row["blockMs"]} ms block   load {row["load"]}×   {row["verdict"]}");
                Console.WriteLine(json["summary"]?.ToString());
                code = 0;
            }
            done.Set();
        });
        OnnxHost.Configure(ctrl.Settings.Acceleration);
        Console.WriteLine("acceleration: " + OnnxHost.Status);
        ctrl.HandleMessage("runBench", new JObject());
        if (!done.Wait(TimeSpan.FromMinutes(15))) { Console.WriteLine("speed check timed out"); return 1; }
        return code;
    }

    private static int VirtualMicElevated(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        bool ok; string message;
        try
        {
            var inf = ArgValue(args, "--inf") ?? WindowsVirtualMic.VbCable.Inf;
            var hwid = ArgValue(args, "--hwid") ?? WindowsVirtualMic.VbCable.HardwareId;
            (ok, message) = args.Contains("--install-virtual-mic")
                ? WindowsVirtualMic.InstallElevated(ArgValue(args, "--install-virtual-mic") ?? "", inf, hwid)
                : WindowsVirtualMic.RemoveElevated(inf, hwid);
        }
        catch (Exception ex) { ok = false; message = ex.Message; }
        if (ArgValue(args, "--log") is { } log)
        {
            try { File.WriteAllText(log, message); } catch { }
        }
        else Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static PosixSignalRegistration? _sigterm, _sigint, _sighup;
    private static VoiceController? _controller;

    private static void RequestExit(string why)
    {
        if (_exiting) return;
        _exiting = true;
        BootLog.Append($"exit requested by {why}");
        // A start still loading its voice must not open the devices after this.
        _controller?.BeginShutdown();
        CloseWindowOrExit();
        _ = Task.Delay(10_000).ContinueWith(_ => { try { Environment.Exit(0); } catch { } });
    }

    private static void CloseWindowOrExit()
    {
        if (_windowUp)
        {
            try { _window.Invoke(() => _window.Close()); return; } catch { }
        }
        BootSentinel.Clear();
        Environment.Exit(0);
    }

    private static void InstallCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            ErrorLog.WriteEntry("Unhandled", ex ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
            BootLog.Append($"unhandled exception: {ex?.GetType().Name}: {ex?.Message}");
            var text = "Morphonic hit an unexpected error and has to close.\n\n" +
                       $"{ex?.GetType().Name}: {ex?.Message}\n\nDetails were written to {AppPaths.ErrorLogPath}.";
            if (OperatingSystem.IsWindows()) WindowsHost.MessageBox(text, WindowsHost.MB_ICONERROR); else LinuxHost.Alert("Morphonic", text);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.WriteEntry("UnobservedTask", e.Exception);
            e.SetObserved();
        };
    }

    // "--after <pid>": launched by a running instance about to exit — let
    // it release the single-instance lock first.
    private static void WaitForPredecessor(string[] args)
    {
        int i = Array.IndexOf(args, "--after");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(30_000);
        }
        catch { }
    }

    // Relaunch (the GPU pack and the acceleration choice bind at native
    // load time): start a fresh process that waits for this one, then leave.
    private static void RestartApp()
    {
        if (_exiting) return;
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
            // The new process keeps this one's arguments (--data-dir, test
            // hooks) apart from a previous --after, which is replaced.
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            int after = args.IndexOf("--after");
            if (after >= 0) args.RemoveRange(after, Math.Min(2, args.Count - after));
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--after");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(psi)?.Dispose();
            BootLog.Append("restarting (user's choice)");
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("RestartApp", ex);
            return;
        }
        _exiting = true;
        _ = Task.Run(() => { try { _window.Invoke(() => _window.Close()); } catch { } });
    }

    private static void SendToUi(string type, object? payload) =>
        ToUi.Writer.TryWrite(JsonConvert.SerializeObject(new { type, payload }));

    private static async Task RunUiDispatcherAsync()
    {
        await UiReady.Task.ConfigureAwait(false);
        await foreach (var json in ToUi.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_exiting) return;
            try { _window.Invoke(() => _window.SendWebMessage(json)); }
            catch (Exception ex) { ErrorLog.WriteEntry("UiDispatcher", ex); }
        }
    }

    // Runs on the window thread — the first call is the proof that the page
    // is up and listening, which releases everything queued during boot.
    private static void OnUiMessage(VoiceController ctrl, string raw)
    {
        Interlocked.Exchange(ref _lastPingAt, Environment.TickCount64);
        if (UiReady.TrySetResult())
        {
            BootLog.Append($"ui: page connected {Environment.TickCount64 - _bootTick} ms after start, {ToUi.Reader.Count} queued message(s) released");
            BootSentinel.Clear();
            ctrl.UiConnected();
            if (_autoStart)
            {
                BootLog.Append("auto-start requested (--auto-start)");
                ctrl.HandleMessage("start", new JObject());
            }
        }
        try
        {
            var msg = JObject.Parse(raw);
            ctrl.HandleMessage(msg["action"]?.ToString() ?? "", msg);
        }
        catch (Exception ex) { ErrorLog.WriteEntry("OnUiMessage", ex); }
    }

    // The UI lives inside the executable and is re-extracted into the data
    // folder on every launch — a few hundred KB.
    private static void ExtractUiAssets()
    {
        var asm = typeof(Program).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("ui/", StringComparison.Ordinal)) continue;
            var rel = name[3..].Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var dest = Path.Combine(AppPaths.UiDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest)) { try { File.SetAttributes(dest, FileAttributes.Normal); } catch { } }
            using var src = asm.GetManifestResourceStream(name)!;
            using var dst = File.Create(dest);
            src.CopyTo(dst);
        }
    }
}
