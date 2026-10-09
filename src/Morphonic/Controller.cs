using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Morphonic.Audio;
using Morphonic.Rvc;

namespace Morphonic;

// Owns all voice-changer state and logic: the session (devices, engine,
// converter), settings, the voice library, component and pack downloads,
// and the speed check. Talks to the UI through an injected (type, payload)
// send delegate and handles the UI's {action,...} messages; the UI's
// message switch is the authoritative contract list.
public sealed class VoiceController : IDisposable
{
    private readonly Action<string, object?> _send;
    private readonly AppSettings _settings;
    private readonly ModelManager _models = new();

    // Session pieces. The converter (three loaded ONNX sessions) outlives a
    // Stop so the next Start on the same voice is instant.
    private VoiceConverter? _converter;
    private string _converterKey = "";
    private RealtimeEngine? _engine;
    private IAudioInput? _input;
    private IAudioOutput? _output, _monitor;
    private RingBuffer? _monitorRing;
    // The sidetone device runs on its own clock: whatever it falls behind
    // by piles up in the ring. Past MonitorMaxMs the backlog is cut to
    // MonitorCushionMs, so the sidetone never lags more than that.
    private const int MonitorMaxMs = 300, MonitorCushionMs = 60;
    private int _monitorMax, _monitorCushion;
    private long _monitorCatchUps;
    private string _sessionLabel = "";
    private volatile bool _loading;
    private string _loadingLabel = "";
    private long _sessionStartedAt;

    // Settings are written from the UI message path and from session
    // work; all mutate+save sequences serialize here.
    private readonly object _settingsLock = new();
    // Session lifecycle runs on the UI message path, the capture thread
    // (failures) and the worker: serialize it. Lock order: session outer,
    // settings inner.
    private readonly object _sessionLock = new();

    private CancellationTokenSource? _downloadCts;
    private string? _downloadingId;
    private long _lastProgressSentAt;
    private int _benchRunning;
    private int _checkingUpdate;
    private volatile bool _virtualMicBusy;
    private string _converting = "";

    private readonly System.Threading.Timer _meterTimer;
    private int _meterIn = -1, _meterOut = -1;
    private PaceMonitor _pace = new();
    private long _lastPaceSentAt;
    private PaceMonitor.PaceStatus _lastPaceStatus;
    private bool _paceBehindLogged;
    private bool _safeBoot;

    // The host supplies the native file dialog (window thread only).
    public Func<string[]?>? PickFiles { get; set; }
    public event Action? RestartRequested;

    public bool IsRunning => _engine != null;
    public AppSettings Settings => _settings;

    public VoiceController(Action<string, object?> sendToUi)
    {
        _send = sendToUi;
        _settings = AppSettings.Load();
        _models.OnProgress += (id, done, total, stage) => SendProgress(id, done, total, stage);
        _meterTimer = new System.Threading.Timer(_ => MeterTick(), null, 100, 100);
    }

    public void ArmSafeBoot(bool safeBoot) => _safeBoot = safeBoot;

    // The host calls this on the page's first message (window thread).
    public void UiConnected()
    {
        if (_safeBoot)
            _send("toast", new { ok = false, msg =
                "Morphonic didn't start cleanly last time — details: " + AppPaths.ErrorLogPath });
        // A first run that never touches a setting would otherwise leave no
        // settings file for the boot log to report on.
        lock (_settingsLock) if (!AppSettings.FileExists) _settings.Save();
    }

    private static void RunOffUiThread(Action work) =>
        Task.Run(() =>
        {
            try { work(); }
            catch (Exception ex) { ErrorLog.WriteEntry("SessionWork", ex); }
        });

    // ── messages ───────────────────────────────────────────────────

    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "getState":
                SendDevices();
                SendVoices();
                SendModels();
                SendState();
                break;

            case "ping":
                break;

            // The machine profile runs lspci / nvidia-smi on Linux (seconds
            // on a slow box): never on the window thread.
            case "getDocs":
                RunOffUiThread(() => _send("docs", new
                {
                    version = Version,
                    readme = ReadEmbeddedDoc("docs/README.md"),
                    license = ReadEmbeddedDoc("docs/LICENSE.txt"),
                    notice = ReadEmbeddedDoc("docs/NOTICE.txt"),
                    machine = MachineProfile.Describe(),
                    installedAt = LinuxInstaller.InstalledAt(),
                    dataDir = AppPaths.DataDir,
                    platform = OperatingSystem.IsWindows() ? "windows" : "linux",
                }));
                break;

            case "start":
                RunOffUiThread(() =>
                {
                    lock (_sessionLock)
                    {
                        if (IsRunning) { SendState(); return; }
                        lock (_settingsLock)
                        {
                            if (msg["inputIndex"]?.Value<int?>() is int ii) SetInput(ii);
                            if (msg["outputIndex"]?.Value<int?>() is int oi) SetOutput(oi);
                            _settings.Save();
                        }
                        Start();
                    }
                });
                break;

            case "stop":
                RunOffUiThread(() => { lock (_sessionLock) Stop("stopped"); });
                break;

            // Remembering a device re-reads the device list, which on Linux
            // runs pw-dump (seconds while PipeWire restarts): off the
            // window thread, like every other device listing.
            case "setInputDevice":
                {
                    int index = msg["index"]?.Value<int?>() ?? 0;
                    RunOffUiThread(() =>
                    {
                        lock (_settingsLock) { SetInput(index); _settings.Save(); }
                        SendSavedToast();
                        RestartIfRunning();
                    });
                }
                break;

            case "setOutputDevice":
                {
                    int index = msg["index"]?.Value<int?>() ?? 0;
                    RunOffUiThread(() =>
                    {
                        lock (_settingsLock) { SetOutput(index); _settings.Save(); }
                        SendSavedToast();
                        RestartIfRunning();
                    });
                }
                break;

            // Hear yourself (sidetone): "auto", "off", or an output index.
            case "setSidetone":
            case "setMonitorDevice":
                {
                    var mode = msg["mode"]?.ToString() ?? "";
                    int idx = mode == "auto" || mode == "off" ? -1 : msg["index"]?.Value<int?>() ?? -1;
                    RunOffUiThread(() =>
                    {
                        lock (_settingsLock)
                        {
                            _settings.MonitorDeviceIndex = idx;
                            _settings.MonitorDeviceName = idx < 0 ? "" : AudioDevices.NameAt(AudioDevices.Outputs(), idx);
                            _settings.SidetoneAuto = mode == "auto" || (mode.Length == 0 && idx < 0 && _settings.SidetoneAuto);
                            _settings.Save();
                        }
                        SendSavedToast();
                        SendDevices();
                        RestartIfRunning();
                    });
                }
                break;

            case "setVoice":
                {
                    var id = Path.GetFileName(msg["id"]?.ToString() ?? "");
                    if (!VoiceLibrary.Exists(id) || !id.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                    {
                        _send("toast", new { ok = false, msg = "That voice isn't an .onnx file in the library" });
                        break;
                    }
                    lock (_settingsLock) { _settings.VoiceId = id; _settings.Save(); }
                    SendVoices();
                    SendDevices();   // the page's Start button reads the chosen voice from this payload
                    SendState();
                    RestartIfRunning();
                }
                break;

            case "rescanDevices":
                AudioDevices.Invalidate();
                SendDevices();
                break;

            case "importVoice":
                {
                    // The native dialog must run on the window thread — which
                    // is where messages arrive.
                    string[]? picked = null;
                    try { picked = PickFiles?.Invoke(); }
                    catch (Exception ex) { ErrorLog.WriteEntry("PickFiles", ex); }
                    if (picked == null || picked.Length == 0) break;
                    RunOffUiThread(() =>
                    {
                        string? arrived = null;
                        foreach (var file in picked)
                        {
                            var (ok, message) = VoiceLibrary.Import(file, out var added);
                            if (ok && added != null) arrived = added;
                            else _send("toast", new { ok, msg = message });
                        }
                        Arrived(arrived);
                    });
                }
                break;

            case "openVoicesFolder":
                OpenFolder(VoiceLibrary.Dir);
                break;

            // Settings -> About: the data folder (error.log, last_boot.log).
            // Always AppPaths.DataDir; the page never supplies a path.
            case "openDataFolder":
                OpenFolder(AppPaths.DataDir);
                break;

            // Settings -> About: one zip with the logs, settings and machine
            // description, in the data folder, which then opens.
            case "saveReport":
                try
                {
                    var report = SupportReport.Write(Version);
                    _send("toast", new { ok = true, msg = "Report saved: " + Path.GetFileName(report) + " — send that file along with what you saw" });
                    OpenFolder(AppPaths.DataDir);
                }
                catch (Exception ex) { _send("toast", new { ok = false, msg = "Could not write the report: " + ex.Message }); }
                break;

            // A dropped file arrives from the page in pieces (the page has
            // no path to give); each piece is acknowledged so the page
            // never runs ahead of the disk.
            case "importBegin":
                {
                    var name = Path.GetFileName(msg["name"]?.ToString() ?? "");
                    long size = msg["size"]?.Value<long>() ?? 0;
                    VoiceLibrary.AbortImport(name);
                    var (ok, err) = VoiceLibrary.BeginImport(name, size);
                    if (!ok) _send("toast", new { ok = false, msg = err });
                    _send("importAck", new { name, ok, received = 0L });
                }
                break;

            case "importChunk":
                {
                    var name = Path.GetFileName(msg["name"]?.ToString() ?? "");
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(msg["data"]?.ToString() ?? ""); }
                    catch { bytes = Array.Empty<byte>(); }
                    var (ok, err) = VoiceLibrary.AppendImport(name, bytes);
                    if (!ok) { VoiceLibrary.AbortImport(name); _send("toast", new { ok = false, msg = err }); }
                    _send("importAck", new { name, ok, received = VoiceLibrary.ImportedBytes(name) });
                }
                break;

            case "importEnd":
                {
                    var name = Path.GetFileName(msg["name"]?.ToString() ?? "");
                    RunOffUiThread(() =>
                    {
                        var (ok, message) = VoiceLibrary.EndImport(name, out var added);
                        if (ok && added != null) Arrived(added);
                        else
                        {
                            _send("toast", new { ok, msg = message });
                            SendVoices();
                            ConvertNextPending();
                        }
                    });
                }
                break;

            case "importAbort":
                VoiceLibrary.AbortImport(Path.GetFileName(msg["name"]?.ToString() ?? ""));
                break;

            case "openUrl":
                OpenUrl(msg["url"]?.ToString() ?? "");
                break;

            // Windows: install / remove Morphonic's virtual microphone driver
            // (one administrator prompt, run by a second copy of this exe).
            case "installVirtualMic":
            case "removeVirtualMic":
                {
                    if (!OperatingSystem.IsWindows()) break;
                    if (_virtualMicBusy) { _send("toast", new { ok = false, msg = "The virtual microphone is already being changed" }); break; }
                    if (IsRunning) { _send("toast", new { ok = false, msg = "Stop the voice first" }); break; }
                    bool install = msg["action"]?.ToString() == "installVirtualMic";
                    _virtualMicBusy = true;
                    SendDevices();
                    _ = Task.Run(() =>
                    {
                        var (ok, message) = install ? WindowsVirtualMic.Install() : WindowsVirtualMic.Remove();
                        _virtualMicBusy = false;
                        if (ok && install) lock (_settingsLock) { _settings.VirtualMic = true; _settings.Save(); }
                        WindowsVirtualMic.Invalidate();
                        AudioDevices.Invalidate();
                        BootLog.Append($"virtual microphone {(install ? "install" : "remove")} {(ok ? "ok" : "failed")}: {message}");
                        _send("toast", new { ok, msg = (install ? "Virtual microphone: " : "Virtual microphone removed: ") + message });
                        SendDevices();
                    });
                }
                break;

            // Settings -> About: one request to the release feed, on the
            // user's press only. The answer is shown; the download is theirs
            // to make in the browser.
            case "checkUpdate":
                {
                    if (!UpdateCheck.Configured) { _send("toast", new { ok = false, msg = "Update checks are not configured in this build" }); break; }
                    if (Interlocked.Exchange(ref _checkingUpdate, 1) == 1) break;
                    _send("update", new { checking = true });
                    var current = typeof(VoiceController).Assembly.GetName().Version ?? new Version(1, 0, 0);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var r = await UpdateCheck.CheckAsync(current, CancellationToken.None).ConfigureAwait(false);
                            BootLog.Append(r.Available
                                ? $"update check: {r.Latest} is available (running {r.Current})"
                                : $"update check: {r.Current} is the latest");
                            _send("update", new { current = r.Current, latest = r.Latest, available = r.Available, url = r.Url,
                                notes = r.Notes, assetName = r.AssetName, assetSize = r.AssetSize, published = r.Published });
                        }
                        catch (Exception ex)
                        {
                            ErrorLog.WriteNote("UpdateCheck", ex.Message);
                            BootLog.Append("update check failed: " + ex.Message);
                            _send("update", new { error = "Could not check: " + ex.Message });
                        }
                        finally { Interlocked.Exchange(ref _checkingUpdate, 0); }
                    });
                }
                break;

            // The fallback converter's packages (PyTorch and friends) with
            // the user's own Python and pip, on request.
            case "installConverter":
                {
                    if (_installingConverter) { _send("toast", new { ok = false, msg = "The converter is already being installed" }); break; }
                    _installingConverter = true;
                    SendState();
                    _send("toast", new { ok = true, msg = "Installing the fallback converter's packages with pip (about 2 GB; this takes a few minutes)…" });
                    _ = VoiceLibrary.InstallConverterAsync().ContinueWith(t =>
                    {
                        _installingConverter = false;
                        var (ok, message) = t.IsCompletedSuccessfully ? t.Result : (false, "install failed: " + t.Exception?.GetBaseException().Message);
                        _send("toast", new { ok, msg = message });
                        SendState();
                    });
                }
                break;

            // Find voices: Hugging Face's public API, only on request.
            case "searchVoices":
                {
                    var q = msg["query"]?.ToString() ?? "";
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var results = await VoiceHub.SearchAsync(q, CancellationToken.None);
                            _send("voiceSearch", new { query = q, results = results.Select(r => new { id = r.Id, likes = r.Likes, downloads = r.Downloads, updated = r.Updated }) });
                        }
                        catch (Exception ex) { _send("voiceSearch", new { query = q, error = "search failed: " + ex.Message }); }
                    });
                }
                break;

            case "listVoiceFiles":
                {
                    var repo = msg["repo"]?.ToString() ?? "";
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var files = await VoiceHub.ListFilesAsync(repo, CancellationToken.None);
                            _send("voiceFiles", new
                            {
                                repo,
                                files = files.Select(f =>
                                {
                                    var name = VoiceLibrary.LibraryName(repo, f.Path);
                                    return new { path = f.Path, sizeBytes = f.SizeBytes, sha256 = f.Sha256, libraryName = name, inLibrary = InLibrary(name) };
                                }),
                            });
                        }
                        catch (Exception ex) { _send("voiceFiles", new { repo, error = "could not list the files: " + ex.Message }); }
                    });
                }
                break;

            case "downloadVoiceFile":
                {
                    if (_hubCts != null) { _send("toast", new { ok = false, msg = "A voice download is already running" }); break; }
                    var repo = msg["repo"]?.ToString() ?? "";
                    var path = msg["path"]?.ToString() ?? "";
                    long size = msg["sizeBytes"]?.Value<long>() ?? 0;
                    var sha = msg["sha256"]?.ToString();
                    var name = Path.GetFileName(path);   // the page tracks progress by the Hub file name
                    var libraryName = VoiceLibrary.LibraryName(repo, path);
                    var readyId = Path.GetFileNameWithoutExtension(libraryName) + ".onnx";
                    if (VoiceLibrary.Exists(readyId))
                    {
                        // Already fetched and converted: just make it the voice.
                        _send("voiceProgress", new { name, received = size, total = size, done = true, ok = true });
                        Adopt(readyId, "is already in your library");
                        break;
                    }
                    var cts = new CancellationTokenSource();
                    _hubCts = cts;
                    var ct = cts.Token;
                    _send("voiceProgress", new { name, received = 0L, total = size, done = false });
                    _ = Task.Run(async () =>
                    {
                        var (ok, message, added) = await VoiceHub.DownloadAsync(repo, path, size, string.IsNullOrEmpty(sha) ? null : sha,
                            (got, total) => SendVoiceProgress(name, got, total), ct);
                        Interlocked.CompareExchange(ref _hubCts, null, cts);
                        cts.Dispose();
                        _send("voiceProgress", new { name, received = size, total = size, done = true, ok });
                        BootLog.Append($"voice download {(ok ? "ok" : "failed")}: {repo}/{path} — {message}");
                        if (!ok) { _send("toast", new { ok = false, msg = message }); SendVoices(); return; }
                        // `added` is the voice file itself — for a zip, the
                        // checkpoint or export it held, never the zip's name.
                        Arrived(added, message);
                    });
                }
                break;

            // The page's Cancel during a Hub download; the source may have
            // just been disposed by the finishing download.
            case "cancelVoiceDownload":
                {
                    var cts = _hubCts;
                    try { cts?.Cancel(); } catch (ObjectDisposedException) { }
                }
                break;

            // The loaded converter is shared with a session that may be
            // starting right now: its disposal happens under the session
            // lock, where "running" and "loading" are both settled.
            case "deleteVoice":
                {
                    var id = Path.GetFileName(msg["id"]?.ToString() ?? "");
                    RunOffUiThread(() =>
                    {
                        lock (_sessionLock)
                        {
                            if ((IsRunning || _loading) && id == _settings.VoiceId)
                            {
                                _send("toast", new { ok = false, msg = "Stop the voice before deleting it" });
                                return;
                            }
                            if (id == _converterKeyVoice) DisposeConverter();
                        }
                        if (VoiceLibrary.Delete(id))
                        {
                            if (id == _settings.VoiceId) lock (_settingsLock) { _settings.VoiceId = ""; _settings.Save(); }
                            _send("toast", new { ok = true, msg = id + " removed" });
                        }
                        SendVoices();
                        SendDevices();
                        SendState();
                    });
                }
                break;

            case "convertVoice":
                {
                    var id = Path.GetFileName(msg["id"]?.ToString() ?? "");
                    if (_converting.Length > 0) { _send("toast", new { ok = false, msg = "A conversion is already running" }); break; }
                    StartConvert(id, auto: false);
                }
                break;

            case "config":
                {
                    lock (_settingsLock)
                    {
                        _settings.PitchSemitones = Math.Clamp(msg["pitch"]?.Value<int>() ?? _settings.PitchSemitones, -24, 24);
                        _settings.SpeakerId = Math.Max(0, msg["speakerId"]?.Value<int>() ?? _settings.SpeakerId);
                        _settings.BlockMs = Math.Clamp(msg["blockMs"]?.Value<int>() ?? _settings.BlockMs, 100, 1000);
                        _settings.ExtraMs = Math.Clamp(msg["extraMs"]?.Value<int>() ?? _settings.ExtraMs, 500, 5000);
                        _settings.CrossfadeMs = Math.Clamp(msg["crossfadeMs"]?.Value<int>() ?? _settings.CrossfadeMs, 20, 200);
                        _settings.NoiseGateDb = Math.Clamp(msg["noiseGateDb"]?.Value<int>() ?? _settings.NoiseGateDb, -60, 0);
                        _settings.RmsMixRate = Math.Clamp(msg["rmsMixRate"]?.Value<double>() ?? _settings.RmsMixRate, 0, 1);
                        _settings.OutputGainDb = Math.Clamp(msg["outputGainDb"]?.Value<int>() ?? _settings.OutputGainDb, -24, 24);
                        var accel = msg["acceleration"]?.ToString();
                        if (accel is "auto" or "gpu" or "cpu") _settings.Acceleration = accel;
                        _settings.VirtualMic = msg["virtualMic"]?.Value<bool>() ?? _settings.VirtualMic;
                        _settings.Save();
                    }
                    // Pitch and speaker apply live; the buffer geometry
                    // needs a new engine.
                    bool live = msg["pitch"] != null || msg["speakerId"] != null;
                    bool geometry = msg["blockMs"] != null || msg["extraMs"] != null || msg["crossfadeMs"] != null ||
                                    msg["noiseGateDb"] != null || msg["rmsMixRate"] != null || msg["outputGainDb"] != null;
                    if (live)
                    {
                        var c = _converter;
                        if (c != null) { c.PitchSemitones = _settings.PitchSemitones; c.SpeakerId = _settings.SpeakerId; }
                    }
                    SendDevices();
                    // The runtime binds at process start: a changed
                    // acceleration choice needs a restart to take effect.
                    if (msg["acceleration"] != null)
                        _send("toast", new { ok = true, msg = "Acceleration setting saved — it applies after restarting Morphonic", action = new { label = "Restart Morphonic", send = new { action = "restartApp" } } });
                    else SendSavedToast();
                    if (geometry) RestartIfRunning();
                }
                break;

            case "recommendedDefaults":
                {
                    var tier = HardwareTier.Detect();
                    var (block, extra) = HardwareTier.Recommended(tier);
                    lock (_settingsLock)
                    {
                        _settings.BlockMs = block;
                        _settings.ExtraMs = extra;
                        _settings.CrossfadeMs = 50;
                        _settings.NoiseGateDb = -60;
                        _settings.RmsMixRate = 0.5;
                        _settings.OutputGainDb = 0;
                        _settings.Acceleration = "auto";
                        _settings.Save();
                    }
                    SendDevices();
                    SendModels();
                    _send("toast", new { ok = true, msg = $"Recommended defaults applied for {HardwareTier.Label(tier)}: {block} ms blocks, {extra / 1000.0:0.#} s context, 50 ms crossfade" });
                    RestartIfRunning();
                }
                break;

            case "getModels":
                SendModels();
                break;

            case "downloadModel":
                StartDownload(msg["id"]?.ToString() ?? "");
                break;

            case "cancelDownload":
                {
                    var cts = _downloadCts;
                    try { cts?.Cancel(); } catch (ObjectDisposedException) { }
                }
                break;

            case "deleteModel":
                {
                    var id = msg["id"]?.ToString() ?? "";
                    RunOffUiThread(() =>
                    {
                        lock (_sessionLock)
                        {
                            if (IsRunning || _loading) { _send("toast", new { ok = false, msg = "Stop the voice before deleting components" }); return; }
                            if (id == GpuPack.Id)
                            {
                                GpuPack.Delete();
                                _send("toast", new { ok = true, msg = "GPU acceleration removed — takes effect after restarting Morphonic" });
                            }
                            else
                            {
                                DisposeConverter();
                                _models.Delete(id);
                                var m = ModelCatalog.Find(id);
                                if (m?.Kind == ModelKind.Voice && _settings.VoiceId == m.File.FileName)
                                    lock (_settingsLock) { _settings.VoiceId = ""; _settings.Save(); }
                            }
                        }
                        SendModels();
                        SendVoices();
                        SendDevices();   // carries componentsReady: the page's first-run switch
                        SendState();
                    });
                }
                break;

            case "verifyModels":
                _ = Task.Run(() =>
                {
                    var (ok, bad) = ModelManager.VerifyFiles();
                    var (gok, gbad) = GpuPack.VerifyFiles();
                    ok += gok;
                    bad.AddRange(gbad);
                    _send("toast", bad.Count == 0
                        ? new { ok = true, msg = $"All {ok} installed file(s) verified" }
                        : new { ok = false, msg = $"Verification FAILED: {string.Join(", ", bad)} — delete and re-download" });
                });
                break;

            case "runBench":
                RunBench();
                break;

            case "restartApp":
                RestartRequested?.Invoke();
                break;

            case "installDesktop":
                RunOffUiThread(() =>
                {
                    var result = LinuxInstaller.Install();
                    _send("toast", new { ok = result.Ok, msg = result.Message });
                    _send("install", new { installedAt = LinuxInstaller.InstalledAt() });
                });
                break;
        }
    }

    private string _converterKeyVoice => _converterKey.Split('|')[0];

    private void SetInput(int index)
    {
        _settings.InputDeviceIndex = index;
        _settings.InputDeviceName = AudioDevices.NameAt(AudioDevices.Inputs(), index);
    }

    private void SetOutput(int index)
    {
        _settings.OutputDeviceIndex = index;
        _settings.OutputDeviceName = AudioDevices.NameAt(AudioDevices.Outputs(), index);
    }

    // An output other programs read as a microphone: a VB-CABLE on Windows,
    // the app's own virtual sink on Linux. Playing into one, you hear
    // nothing yourself unless sidetone is on.
    private static bool IsVirtualOutput(AudioDevice d) =>
        d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
        d.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
        VirtualMic.IsVirtualOutput(d);

    private void RestartIfRunning() =>
        RunOffUiThread(() =>
        {
            lock (_sessionLock)
            {
                if (!IsRunning) return;
                Stop("restarting with the new settings");
                Start();
            }
        });

    // ── session ────────────────────────────────────────────────────

    private void Start()
    {
        lock (_sessionLock)
        {
            Stop("replaced");
            string? problem = null;
            if (Volatile.Read(ref _benchRunning) != 0) problem = "Wait for the speed check to finish";
            else if (!ModelManager.ComponentsReady()) problem = "The content encoder and pitch model must be downloaded first — see Models";
            else if (_settings.VoiceId.Length == 0) problem = "Choose a voice first — see Voices";
            else if (!VoiceLibrary.Exists(_settings.VoiceId)) problem = $"The voice {_settings.VoiceId} is no longer in the library";
            if (problem != null)
            {
                _send("state", new { running = false, error = problem });
                _send("toast", new { ok = false, msg = problem });
                return;
            }
            _loading = true;
            _loadingLabel = VoiceName();
            SendState();
            try
            {
                StartSession();
            }
            catch (Exception ex)
            {
                ErrorLog.WriteEntry("StartSession", ex);
                _loading = false;
                try { Stop("failed to start"); } catch { }
                _send("state", new { running = false, error = "the voice could not start: " + ex.Message });
                _send("toast", new { ok = false, msg = "The voice could not start — " + ex.Message });
            }
            finally { _loading = false; }
        }
    }

    private VoiceConverter LoadConverter()
    {
        var key = _settings.VoiceId + "|" + OnnxHost.Label;
        if (_converter != null && _converterKey == key) return _converter;
        DisposeConverter();
        var encoderPath = ModelManager.PathFor(ModelCatalog.Find(ModelCatalog.EncoderId)!);
        var pitchPath = ModelManager.PathFor(ModelCatalog.Find(ModelCatalog.PitchId)!);
        var voicePath = VoiceLibrary.PathFor(_settings.VoiceId);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _converter = BuildConverter(encoderPath, pitchPath, voicePath);
        }
        catch (Exception ex) when (OnnxHost.Active != Accelerator.Cpu)
        {
            // A GPU provider that cannot build a session is given up on for
            // this run; the CPU always can.
            OnnxHost.DemoteToCpu(ex.Message);
            _send("toast", new { ok = false, msg = $"GPU acceleration failed to start ({ex.Message}) — running on the CPU" });
            key = _settings.VoiceId + "|" + OnnxHost.Label;
            _converter = BuildConverter(encoderPath, pitchPath, voicePath);
        }
        _converterKey = key;
        BootLog.Append($"loaded {_settings.VoiceId} ({_converter.Voice.SampleRate} Hz, {_converter.Voice.Convention}" +
                       $"{(_converter.Voice.HasSkipHead ? ", tail decode" : ", full decode")}) on {OnnxHost.Label} in {sw.ElapsedMilliseconds} ms");
        return _converter;
    }

    private static VoiceConverter BuildConverter(string encoderPath, string pitchPath, string voicePath)
    {
        ContentEncoder? enc = null;
        RmvpePitch? pitch = null;
        try
        {
            enc = new ContentEncoder(encoderPath);
            pitch = new RmvpePitch(pitchPath);
            var voice = new VoiceModel(voicePath);
            return new VoiceConverter(enc, pitch, voice);
        }
        catch
        {
            pitch?.Dispose();
            enc?.Dispose();
            throw;
        }
    }

    private void DisposeConverter()
    {
        _converter?.Dispose();
        _converter = null;
        _converterKey = "";
    }

    private void AbandonConverter()
    {
        ErrorLog.WriteNote("Session", "the conversion worker did not stop within 5 s; its runtime sessions were abandoned rather than disposed under it");
        BootLog.Append("conversion worker stuck in a pass — runtime sessions abandoned; the next start loads fresh ones");
        _converter = null;
        _converterKey = "";
    }

    private void StartSession()
    {
        var converter = LoadConverter();
        converter.PitchSemitones = _settings.PitchSemitones;
        converter.SpeakerId = _settings.SpeakerId;
        var engine = new RealtimeEngine(converter, new EngineConfig
        {
            BlockMs = _settings.BlockMs,
            ExtraMs = _settings.ExtraMs,
            CrossfadeMs = _settings.CrossfadeMs,
            NoiseGateDb = _settings.NoiseGateDb,
            RmsMixRate = _settings.RmsMixRate,
            OutputGainDb = _settings.OutputGainDb,
        });
        _pace = new PaceMonitor();
        _lastPaceStatus = PaceMonitor.PaceStatus.Unknown;
        _lastPaceSentAt = 0;
        _paceBehindLogged = false;
        var mine = engine;
        engine.OnPass += OnPass;
        engine.OnFailed += ex => SessionFailed(mine, "conversion failed: " + ex.Message, ex);

        var inputs = AudioDevices.Inputs();
        var outputs = AudioDevices.Outputs();
        int inIdx = AudioDevices.Resolve(inputs, _settings.InputDeviceIndex, _settings.InputDeviceName);
        int outIdx = AudioDevices.Resolve(outputs, _settings.OutputDeviceIndex, _settings.OutputDeviceName);
        // Virtual microphone on and no explicit output: play into the
        // virtual output (the PipeWire sink on Linux, the Morphonic Voice
        // cable on Windows), where other programs pick the voice up.
        if (VirtualMic.Supported && _settings.VirtualMic && outIdx == 0)
        {
            int vm = Array.FindIndex(outputs, d => d.Id.Length > 0 && VirtualMic.IsVirtualOutput(d));
            if (vm > 0) outIdx = vm;
        }

        var input = AudioDevices.OpenInput(inIdx > 0 ? inputs[inIdx] : null);
        var output = AudioDevices.OpenOutput(outIdx > 0 ? outputs[outIdx] : null);
        // Sidetone: the chosen device, or — automatically — the default
        // output when the voice goes into a virtual cable / the virtual
        // microphone, so the speaker hears what the other side hears.
        IAudioOutput? monitor = null;
        bool intoCable = outIdx > 0 && IsVirtualOutput(outputs[outIdx]);
        if (_settings.MonitorDeviceIndex >= 0)
        {
            int monIdx = AudioDevices.Resolve(outputs, _settings.MonitorDeviceIndex, _settings.MonitorDeviceName);
            monitor = AudioDevices.OpenOutput(monIdx > 0 ? outputs[monIdx] : null);
        }
        else if (_settings.SidetoneAuto && intoCable)
        {
            monitor = AudioDevices.OpenOutput(null);
        }
        if (monitor != null)
        {
            _monitorRing = new RingBuffer(engine.SampleRate * 2);
            _monitorMax = engine.SampleRate * MonitorMaxMs / 1000;
            _monitorCushion = engine.SampleRate * MonitorCushionMs / 1000;
            _monitorCatchUps = 0;
        }
        input.OnFailed += ex => SessionFailed(mine, "microphone capture failed — " + ex.Message, ex);
        output.OnFailed += ex => SessionFailed(mine, "playback failed — " + ex.Message, ex);
        if (monitor != null) monitor.OnFailed += ex => SessionFailed(mine, "monitor playback failed — " + ex.Message, ex);

        _engine = engine;
        _input = input;
        _output = output;
        _monitor = monitor;
        try
        {
            // The first pass pays one-time costs (GPU kernels, caches):
            // half a second on DirectML. Paid here on silence, so the first
            // spoken block is not the one that stalls.
            engine.ProcessBlock(new float[engine.Block16]);
            engine.Start();
            output.Start(PullMain, engine.SampleRate);
            monitor?.Start(PullMonitor, engine.SampleRate);
            input.Start(samples => _engine?.PushInput(samples));
        }
        catch
        {
            Stop("failed to open audio");
            throw;
        }
        _sessionLabel = $"{VoiceName()} · {OnnxHost.Label} · {converter.Voice.SampleRate / 1000} kHz";
        _sessionStartedAt = Environment.TickCount64;
        BootLog.Append($"voice started: {_sessionLabel}; in: {input.Backend}; out: {output.Backend}" +
                       (monitor != null ? $"; sidetone: {monitor.Backend}{(_settings.MonitorDeviceIndex < 0 ? " (auto)" : "")}" : "; sidetone: off") +
                       $"; block {engine.BlockFrames * 10} ms, context {engine.ExtraFrames * 10} ms, pipeline delay {engine.LatencyMs} ms");
        SendState();
    }

    private int PullMain(float[] buf)
    {
        var engine = _engine;
        if (engine == null) { Array.Clear(buf); return 0; }
        int n = engine.PullOutput(buf);
        _monitorRing?.Write(buf);
        return n;
    }

    private int PullMonitor(float[] buf)
    {
        var ring = _monitorRing;
        if (ring == null) { Array.Clear(buf); return 0; }
        if (TrimBacklog(ring, _monitorMax, _monitorCushion)) _monitorCatchUps++;
        int n = ring.Read(buf);
        if (n < buf.Length) Array.Clear(buf, n, buf.Length - n);
        return n;
    }

    // A reader on a slower clock than the writer: once the backlog passes
    // `max` samples, the oldest are dropped so `cushion` remain. Returns
    // whether anything was dropped.
    internal static bool TrimBacklog(RingBuffer ring, int max, int cushion)
    {
        int backlog = ring.Count;
        if (max <= 0 || backlog <= max) return false;
        ring.Skip(backlog - Math.Max(0, Math.Min(cushion, max)));
        return true;
    }

    // Device unplugged, driver failure, a pass that threw: wind the
    // session down and say so, instead of converting silence forever. A
    // session that has since replaced this one is not ours to stop.
    private void SessionFailed(RealtimeEngine mine, string reason, Exception ex)
    {
        ErrorLog.WriteEntry("Session", ex);
        RunOffUiThread(() =>
        {
            lock (_sessionLock)
            {
                if (!ReferenceEquals(_engine, mine)) return;
                Stop(reason);
            }
            _send("toast", new { ok = false, msg = "Voice stopped — " + reason });
        });
    }

    public void Stop(string why)
    {
        lock (_sessionLock)
        {
            if (_engine == null && _input == null && _output == null) return;
            if (_engine != null)
                BootLog.Append($"voice session ended ({why}) after {(Environment.TickCount64 - _sessionStartedAt) / 60000.0:0.0} min — " +
                               $"{_pace.Summary()}, {_engine.Underruns} underrun(s), {_engine.SkippedSamples / 16} ms skipped, playback cushion {_engine.PrimeHoldMs} ms" +
                               (_monitor != null ? $", sidetone catch-ups {_monitorCatchUps}" : "") + $"; {MemoryLine()}");
            try { _input?.Dispose(); } catch { }
            bool workerDone = true;
            try { workerDone = _engine?.Stop() ?? true; } catch { }
            try { _output?.Dispose(); } catch { }
            try { _monitor?.Dispose(); } catch { }
            try { _engine?.Dispose(); } catch { }
            // The worker never came back from its pass (a hung inference
            // runtime): the sessions it is still inside are dropped, not
            // disposed. The next start loads fresh ones.
            if (!workerDone) AbandonConverter();
            _input = null;
            _output = null;
            _monitor = null;
            _monitorRing = null;
            _engine = null;
            _sessionLabel = "";
            SendState();
            _meterIn = _meterOut = -1;
            _send("meter", new { input = 0f, output = 0f });
        }
    }

    private static string MemoryLine()
    {
        long rss = 0, managed = 0;
        try { rss = Environment.WorkingSet; } catch { }
        try { managed = GC.GetTotalMemory(false); } catch { }
        return $"rss {rss / 1_048_576} MB, managed {managed / 1_048_576} MB";
    }

    // ── pace ───────────────────────────────────────────────────────

    private void OnPass(PassInfo pass)
    {
        long now = Environment.TickCount64;
        var status = _pace.Record(pass, now);
        bool changed = status != _lastPaceStatus;
        if (changed || now - _lastPaceSentAt >= 1000)
        {
            _lastPaceSentAt = now;
            _lastPaceStatus = status;
            _send("pace", new
            {
                status = status.ToString(),
                load = Math.Round(_pace.Load, 2),
                lagMs = _pace.LagMs,
                passMs = pass.PassMs,
                blockMs = pass.BlockMs,
                latencyMs = (_engine?.LatencyMs ?? 0) + _pace.LagMs,
                underruns = _engine?.Underruns ?? 0,
            });
            if (changed && status == PaceMonitor.PaceStatus.Behind && !_paceBehindLogged)
            {
                _paceBehindLogged = true;
                BootLog.Append($"conversion falling behind: {_pace.Describe()} — {_sessionLabel}");
            }
            else if (changed && status == PaceMonitor.PaceStatus.KeepingUp && _paceBehindLogged)
            {
                _paceBehindLogged = false;
                BootLog.Append($"conversion keeping up again: {_pace.Describe()}");
            }
        }
        if (_pace.ShouldWarn(now)) SendPaceAdvice();
    }

    // Offered once per session after ten seconds of falling behind: the
    // likeliest fix for THIS machine, one click away.
    private void SendPaceAdvice()
    {
        string head = $"The voice is falling behind — each {_pace.Last.BlockMs} ms block takes {_pace.Last.PassMs} ms to convert. ";
        string msg;
        object action;
        bool gpuTier = HardwareTier.Detect() == Tier.Gpu;
        if (OnnxHost.Active == Accelerator.Cpu && gpuTier && !GpuPack.IsInstalled())
        {
            msg = head + "Your graphics card can run this many times faster — install GPU acceleration on the Models screen.";
            action = new { label = "Open Models", view = "models" };
        }
        else if (OnnxHost.Active == Accelerator.Cpu && gpuTier && GpuPack.IsInstalled() && !OnnxHost.PackArmedAtStartup)
        {
            msg = head + "GPU acceleration is installed but not active yet — Morphonic has to restart to load it.";
            action = new { label = "Restart Morphonic", send = new { action = "restartApp" } };
        }
        else if (_settings.BlockMs < 500)
        {
            int next = _settings.BlockMs < 250 ? 250 : _settings.BlockMs < 350 ? 350 : 500;
            msg = head + $"A larger block ({next} ms) costs a little delay but gives each pass more time.";
            action = new { label = $"Use {next} ms blocks", send = new { action = "config", blockMs = next } };
        }
        else if (_settings.ExtraMs > 1000)
        {
            msg = head + "Less context (1 s) makes every pass cheaper.";
            action = new { label = "Use 1 s context", send = new { action = "config", extraMs = 1000 } };
        }
        else
        {
            msg = head + "This machine is at its limit for real-time conversion; GPU acceleration is the fix.";
            action = new { label = "Open Models", view = "models" };
        }
        BootLog.Append($"pace advice: {msg}");
        _send("toast", new { ok = false, msg, action });
    }

    // ── downloads ──────────────────────────────────────────────────

    private void StartDownload(string id)
    {
        if (_downloadingId != null) { _send("toast", new { ok = false, msg = "A download is already running" }); return; }
        if (id == GpuPack.Id)
        {
            _downloadingId = id;
            _downloadCts = new CancellationTokenSource();
            var ct = _downloadCts.Token;
            SendModels();
            _ = Task.Run(async () =>
            {
                var (ok, error) = await GpuPack.DownloadAsync((received, total) => SendProgress(id, received, total), ct);
                FinishDownload();
                _send("toast", ok
                    ? new { ok = GpuPack.CudaRuntimePresent(), msg = "GPU acceleration installed — restart Morphonic to activate it", action = new { label = "Restart Morphonic", send = new { action = "restartApp" } } }
                    : new { ok = false, msg = error ?? "download failed", action = new { } });
                SendModels();
            });
            return;
        }
        var info = ModelCatalog.Find(id);
        if (info == null) return;
        _downloadingId = id;
        _downloadCts = new CancellationTokenSource();
        var token = _downloadCts.Token;
        SendModels();
        _ = Task.Run(async () =>
        {
            var (ok, error) = await _models.DownloadAsync(id, token);
            FinishDownload();
            _send("toast", ok
                ? new { ok = true, msg = info.Assembled ? $"{info.DisplayName} downloaded, assembled and verified" : $"{info.DisplayName} downloaded and verified" }
                : new { ok = false, msg = error ?? "download failed" });
            if (ok && info.Kind == ModelKind.Voice && _settings.VoiceId.Length == 0)
                lock (_settingsLock) { _settings.VoiceId = info.File.FileName; _settings.Save(); }
            SendModels();
            SendVoices();
            SendDevices();
            SendState();
        });
    }

    private bool _installingConverter;
    private CancellationTokenSource? _hubCts;
    private long _lastVoiceProgressAt;

    private void SendVoiceProgress(string name, long received, long total)
    {
        long now = Environment.TickCount64;
        if (now - _lastVoiceProgressAt < 150 && received != total) return;
        _lastVoiceProgressAt = now;
        _send("voiceProgress", new { name, received, total, done = false });
    }

    // Links on the Voices screen (the libraries named there) and the
    // release page from the update check open in the system browser;
    // nothing a page could inject.
    private static readonly string[] LinkHosts = { "huggingface.co", "weights.com", "www.weights.com", "voice-models.com", "www.voice-models.com", "applio.org", "www.applio.org", "github.com", "vb-audio.com", "www.vb-audio.com" };

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || !LinkHosts.Contains(u.Host)) return;
        try
        {
            if (OperatingSystem.IsWindows()) WindowsHost.OpenUrl(url);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", url) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) { ErrorLog.WriteEntry("OpenUrl", ex); }
    }

    // Converts one .pth; an automatic run (after an import) goes on to
    // the next unconverted one, so a dropped zip of voices is ready
    // without a click. A file the app cannot convert keeps its Convert
    // button and is not retried automatically.
    private readonly HashSet<string> _autoTried = new(StringComparer.OrdinalIgnoreCase);

    private void StartConvert(string id, bool auto)
    {
        _converting = id;
        if (auto) _autoTried.Add(id);
        SendVoices();
        _ = VoiceLibrary.ConvertAsync(id, note => _send("toast", new { ok = true, msg = note })).ContinueWith(t =>
        {
            _converting = "";
            var (ok, message) = t.IsCompletedSuccessfully ? t.Result : (false, "conversion failed: " + t.Exception?.GetBaseException().Message);
            var onnx = Path.GetFileNameWithoutExtension(id) + ".onnx";
            bool wanted = ok && string.Equals(id, _wanted, StringComparison.OrdinalIgnoreCase);
            if (wanted) _wanted = "";
            if (wanted || (ok && _settings.VoiceId.Length == 0))
            {
                Adopt(onnx, "is ready");
            }
            else
            {
                _send("toast", new { ok, msg = message });
                if (ok) _highlight = onnx;
                SendVoices();
                SendDevices();
                SendState();
            }
            if (auto) ConvertNextPending();
        });
    }

    // A voice the user asked for by name (a Find voices download) becomes
    // the chosen voice as soon as it is usable, unless a session is
    // running — then it is only pointed out, and Use switches to it.
    private string _wanted = "";
    private string _highlight = "";

    private static bool InLibrary(string libraryName) =>
        VoiceLibrary.Exists(libraryName) || VoiceLibrary.Exists(Path.GetFileNameWithoutExtension(libraryName) + ".onnx");

    private void Adopt(string onnxId, string what)
    {
        var shown = Path.GetFileNameWithoutExtension(onnxId);
        _highlight = onnxId;
        if (IsRunning && !string.Equals(_settings.VoiceId, onnxId, StringComparison.OrdinalIgnoreCase))
        {
            _send("toast", new { ok = true, msg = $"{shown} {what} — press Use in the library to switch to it" });
        }
        else
        {
            lock (_settingsLock) { _settings.VoiceId = onnxId; _settings.Save(); }
            _send("toast", new { ok = true, msg = $"{shown} {what} and is now the active voice — press Start voice" });
        }
        SendVoices();
        SendDevices();
        SendState();
    }

    private void ConvertNextPending()
    {
        if (_converting.Length > 0) return;
        var next = VoiceLibrary.Scan().FirstOrDefault(v => v.Kind == "pth" && !_autoTried.Contains(v.Id));
        if (next != null) StartConvert(next.Id, auto: true);
    }

    // A voice just landed in the library by any route (drop, dialog, Hub):
    // an export is adopted at once; a checkpoint is marked wanted, so the
    // conversion that follows adopts it when ready. Null = nothing landed.
    private void Arrived(string? id, string? note = null)
    {
        if (id == null) { SendVoices(); ConvertNextPending(); return; }
        if (id.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) { Adopt(id, "is in your library"); return; }
        _wanted = id;
        _highlight = id;
        _send("toast", new { ok = true, msg = note ?? $"{Path.GetFileName(id)} added — converting to an ONNX voice now" });
        SendVoices();
        ConvertNextPending();
    }

    private void FinishDownload()
    {
        _downloadingId = null;
        _downloadCts?.Dispose();
        _downloadCts = null;
    }

    private void SendProgress(string id, long received, long total, ModelStage stage = ModelStage.Download)
    {
        long now = Environment.TickCount64;
        if (now - _lastProgressSentAt < 150 && received != total) return;
        _lastProgressSentAt = now;
        _send("modelProgress", new { id, received, total, stage = stage == ModelStage.Assemble ? "assemble" : "download" });
    }

    // ── speed check ────────────────────────────────────────────────

    private void RunBench()
    {
        if (IsRunning || _loading) { _send("toast", new { ok = false, msg = "Stop the voice before running the speed check" }); return; }
        if (!ModelManager.ComponentsReady() || _settings.VoiceId.Length == 0 || !VoiceLibrary.Exists(_settings.VoiceId))
        {
            _send("toast", new { ok = false, msg = "The speed check needs the components and a chosen voice" });
            return;
        }
        if (Interlocked.Exchange(ref _benchRunning, 1) != 0) return;
        _send("bench", new { running = true, note = "starting…" });
        _ = Task.Run(async () =>
        {
            try
            {
                var encoderPath = ModelManager.PathFor(ModelCatalog.Find(ModelCatalog.EncoderId)!);
                var pitchPath = ModelManager.PathFor(ModelCatalog.Find(ModelCatalog.PitchId)!);
                var voicePath = VoiceLibrary.PathFor(_settings.VoiceId);
                var config = new EngineConfig { BlockMs = _settings.BlockMs, ExtraMs = _settings.ExtraMs, CrossfadeMs = _settings.CrossfadeMs };
                var label = $"{VoiceName()} on {OnnxHost.Label}";
                // The bench loads its own copy; memory for two is not worth
                // it. A session that got in first keeps its converter and
                // the bench steps aside (Start refuses while the bench runs).
                lock (_sessionLock)
                {
                    if (IsRunning || _loading) { _send("bench", new { running = false, error = "the voice is running — stop it and run the speed check again" }); return; }
                    DisposeConverter();
                }
                var report = await Benchmark.RunAsync(new[] { (label, (Func<VoiceConverter>)(() => BuildConverter(encoderPath, pitchPath, voicePath))) },
                    config, note => _send("bench", new { running = true, note }), CancellationToken.None);
                var text = Benchmark.FormatReport(report);
                try { File.AppendAllText(AppPaths.BenchLogPath, text + Environment.NewLine); }
                catch (Exception ex) { ErrorLog.WriteEntry("Bench.log", ex); }
                BootLog.Append("speed check — " + report.Summary);
                _send("bench", new
                {
                    running = false,
                    machine = report.Machine,
                    summary = report.Summary,
                    rows = report.Rows.Select(r => new { label = r.Label, loadMs = r.LoadMs, avgPassMs = r.AvgPassMs, maxPassMs = r.MaxPassMs, blockMs = r.BlockMs, load = r.Load, verdict = r.Verdict, note = r.Note }),
                });
            }
            catch (Exception ex)
            {
                ErrorLog.WriteEntry("Bench", ex);
                _send("bench", new { running = false, error = ex.Message });
            }
            finally { _benchRunning = 0; }
        });
    }

    // ── payloads ───────────────────────────────────────────────────

    private string VoiceName()
    {
        var entry = VoiceLibrary.Scan().FirstOrDefault(v => v.Id == _settings.VoiceId);
        return entry?.Name ?? Path.GetFileNameWithoutExtension(_settings.VoiceId);
    }

    private void SendSavedToast()
    {
        var error = AppSettings.LastSaveError;
        _send("toast", error.Length == 0
            ? new { ok = true, msg = "Saved" }
            : new { ok = false, msg = "Settings could not be saved — " + error + " (this session keeps them; the next start will not)" });
    }

    private int _devicesInFlight;
    private volatile bool _devicesPending;

    // Listing devices runs child processes on Linux (seconds when PipeWire
    // is restarting): never on the window thread. Requests that arrive
    // while one is running are folded into one more pass.
    private void SendDevices()
    {
        if (Interlocked.Exchange(ref _devicesInFlight, 1) != 0) { _devicesPending = true; return; }
        RunOffUiThread(() =>
        {
            try { do { _devicesPending = false; SendDevicesNow(); } while (_devicesPending); }
            finally { Interlocked.Exchange(ref _devicesInFlight, 0); }
        });
    }

    private void SendDevicesNow()
    {
        var inputs = AudioDevices.Inputs();
        var outputs = AudioDevices.Outputs();
        _send("devices", new
        {
            platform = OperatingSystem.IsWindows() ? "windows" : "linux",
            inputs = inputs.Select(d => d.Name),
            outputs = outputs.Select(d => d.Name),
            savedInput = AudioDevices.Resolve(inputs, _settings.InputDeviceIndex, _settings.InputDeviceName),
            savedOutput = AudioDevices.Resolve(outputs, _settings.OutputDeviceIndex, _settings.OutputDeviceName),
            savedMonitor = _settings.MonitorDeviceIndex < 0 ? -1 : AudioDevices.Resolve(outputs, _settings.MonitorDeviceIndex, _settings.MonitorDeviceName),
            sidetone = _settings.MonitorDeviceIndex >= 0 ? "device" : _settings.SidetoneAuto ? "auto" : "off",
            outputIsVirtual = outputs.Select((d, i) => (d, i)).Any(x => x.i == AudioDevices.Resolve(outputs, _settings.OutputDeviceIndex, _settings.OutputDeviceName) && x.i > 0 && IsVirtualOutput(x.d)),
            hasCable = outputs.Any(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)),
            virtualMic = _settings.VirtualMic,
            virtualMicStatus = VirtualMic.Status,
            virtualMicInstalled = VirtualMic.Supported,
            // Install is offered only for a package Windows will load (signed
            // through the Hardware Dev Center); an unsigned one is reported.
            virtualMicPackage = OperatingSystem.IsWindows() && WindowsVirtualMic.PackageAvailable && WindowsVirtualMic.PackageSigned,
            virtualMicUnsigned = OperatingSystem.IsWindows() && WindowsVirtualMic.PackageAvailable && !WindowsVirtualMic.PackageSigned,
            virtualMicVendor = OperatingSystem.IsWindows() ? WindowsVirtualMic.Vendor : "",
            virtualMicCredit = OperatingSystem.IsWindows() ? WindowsVirtualMic.Credit : "",
            virtualMicRender = OperatingSystem.IsWindows() ? WindowsVirtualMic.RenderName : "",
            virtualMicCapture = OperatingSystem.IsWindows() ? WindowsVirtualMic.CaptureName : "",
            virtualMicBusy = _virtualMicBusy,
            pitch = _settings.PitchSemitones,
            speakerId = _settings.SpeakerId,
            blockMs = _settings.BlockMs,
            extraMs = _settings.ExtraMs,
            crossfadeMs = _settings.CrossfadeMs,
            noiseGateDb = _settings.NoiseGateDb,
            rmsMixRate = _settings.RmsMixRate,
            outputGainDb = _settings.OutputGainDb,
            acceleration = _settings.Acceleration,
            accelActive = OnnxHost.Label,
            accelStatus = OnnxHost.Status,
            componentsReady = ModelManager.ComponentsReady(),
            voiceId = _settings.VoiceId,
            voiceName = _settings.VoiceId.Length > 0 ? VoiceName() : "",
            pythonFound = VoiceLibrary.FindPython() != null,
            installingConverter = _installingConverter,
        });
    }

    private void SendVoices() =>
        _send("voices", new
        {
            voices = VoiceLibrary.Scan().Select(v => new
            {
                id = v.Id, name = v.Name, kind = v.Kind, sizeBytes = v.SizeBytes, sampleRate = v.SampleRate,
                speakers = v.Speakers, skipHead = v.SkipHead, source = v.Source,
            }),
            active = _settings.VoiceId,
            converting = _converting,
            folder = VoiceLibrary.Dir,
            highlight = Interlocked.Exchange(ref _highlight, ""),   // shown once: the row to scroll to
        });

    private void SendModels()
    {
        var tier = HardwareTier.Detect();
        _send("models", new
        {
            tier = tier.ToString(),
            tierLabel = HardwareTier.Label(tier),
            tierNote = tier == Tier.Gpu ? "" : HardwareTier.GpuVerdict,
            accelActive = OnnxHost.Label,
            accelStatus = OnnxHost.Status,
            downloading = _downloadingId ?? "",
            models = ModelCatalog.Models.Select(m => new
            {
                id = m.Id,
                displayName = m.DisplayName,
                description = m.Description,
                kind = m.Kind.ToString().ToLowerInvariant(),
                sizeBytes = OfflinePayload.Has(m.Id) ? m.SizeBytes : m.DownloadBytes,
                included = OfflinePayload.Has(m.Id),
                assembled = m.Assembled,
                license = m.License,
                attribution = m.Attribution,
                installed = ModelManager.IsInstalled(m),
                required = m.Kind == ModelKind.Component,
            }).Append(new
            {
                id = GpuPack.Id,
                displayName = GpuPack.DisplayName,
                description = GpuPack.Description,
                kind = "pack",
                sizeBytes = GpuPack.SizeBytes,
                included = false,
                assembled = false,
                license = GpuPack.License,
                attribution = GpuPack.Attribution,
                installed = GpuPack.IsInstalled(),
                required = false,
            }),
        });
    }

    private void SendState() =>
        _send("state", new
        {
            running = IsRunning,
            loading = _loading,
            label = _loading ? _loadingLabel : _sessionLabel,
            voiceName = _settings.VoiceId.Length > 0 ? VoiceName() : "",
            accel = OnnxHost.Label,
            latencyMs = _engine?.LatencyMs ?? 0,
        });

    private void MeterTick()
    {
        var engine = _engine;
        if (engine == null) return;
        int inPct = (int)MathF.Round(Math.Clamp(engine.InputLevel, 0f, 1f) * 100f);
        int outPct = (int)MathF.Round(Math.Clamp(engine.OutputLevel, 0f, 1f) * 100f);
        if (inPct == _meterIn && outPct == _meterOut) return;
        _meterIn = inPct;
        _meterOut = outPct;
        _send("meter", new { input = inPct / 100f, output = outPct / 100f });
    }

    private static string Version => typeof(VoiceController).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private static string ReadEmbeddedDoc(string name)
    {
        using var s = typeof(VoiceController).Assembly.GetManifestResourceStream(name);
        if (s == null) return "";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var psi = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true }
                : new System.Diagnostics.ProcessStartInfo("xdg-open", dir) { UseShellExecute = false };
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { ErrorLog.WriteEntry("OpenFolder", ex); }
    }

    public void Dispose()
    {
        try { _downloadCts?.Cancel(); } catch { }
        using (var done = new ManualResetEvent(false))
            if (_meterTimer.Dispose(done)) done.WaitOne(1000);
        Stop("exiting");
        DisposeConverter();
    }
}
