using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Morphonic.Audio;

// Windows audio through WASAPI in shared mode (games and calls keep using
// the same devices), event-driven with small buffers. Capture and render
// both run in the device's own mix format; channels are folded or
// duplicated and the rate converted here, so NAudio never has to insert
// a resampler of its own.
[SupportedOSPlatform("windows")]
internal static class WasapiAudio
{
    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    public static IEnumerable<AudioDevice> Enumerate(bool inputs)
    {
        var list = new List<AudioDevice>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var d in enumerator.EnumerateAudioEndPoints(inputs ? DataFlow.Capture : DataFlow.Render, DeviceState.Active))
        {
            try { list.Add(new AudioDevice(d.FriendlyName, d.ID)); }
            catch { }
            finally { d.Dispose(); }
        }
        return list;
    }

    public static MMDevice Open(AudioDevice? device, bool input)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (device != null && device.Id.Length > 0)
        {
            try { return enumerator.GetDevice(device.Id); }
            catch (Exception ex) { ErrorLog.WriteNote("WasapiAudio", $"device '{device.Name}' could not be opened ({ex.Message}); using the default"); }
        }
        return enumerator.GetDefaultAudioEndpoint(input ? DataFlow.Capture : DataFlow.Render, Role.Console);
    }

    // Interleaved device samples -> mono float, whatever the format.
    public static int ToMono(byte[] buffer, int bytes, WaveFormat format, float[] mono)
    {
        int channels = format.Channels;
        int frames;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat ||
            (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype))
        {
            frames = bytes / (4 * channels);
            for (int f = 0; f < frames; f++)
            {
                float acc = 0;
                for (int c = 0; c < channels; c++) acc += BitConverter.ToSingle(buffer, (f * channels + c) * 4);
                mono[f] = acc / channels;
            }
            return frames;
        }
        if (format.BitsPerSample == 16)
        {
            frames = bytes / (2 * channels);
            for (int f = 0; f < frames; f++)
            {
                float acc = 0;
                for (int c = 0; c < channels; c++) acc += BitConverter.ToInt16(buffer, (f * channels + c) * 2) / 32768f;
                mono[f] = acc / channels;
            }
            return frames;
        }
        if (format.BitsPerSample == 32)
        {
            frames = bytes / (4 * channels);
            for (int f = 0; f < frames; f++)
            {
                float acc = 0;
                for (int c = 0; c < channels; c++) acc += BitConverter.ToInt32(buffer, (f * channels + c) * 4) / 2147483648f;
                mono[f] = acc / channels;
            }
            return frames;
        }
        throw new NotSupportedException($"capture format {format} is not supported");
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WasapiInput : IAudioInput
{
    private readonly AudioDevice? _device;
    private MMDevice? _mm;
    private WasapiCapture? _capture;
    private Resampler? _resampler;
    private float[] _mono = new float[4096];
    private float[] _out = new float[4096];
    private Action<float[]>? _onSamples;
    private volatile bool _stopping;

    public string Backend { get; private set; } = "WASAPI";
    public event Action<Exception>? OnFailed;

    public WasapiInput(AudioDevice? device) { _device = device; }

    public void Start(Action<float[]> onSamples16k)
    {
        _onSamples = onSamples16k;
        _mm = WasapiAudio.Open(_device, input: true);
        _capture = new WasapiCapture(_mm, useEventSync: true, audioBufferMillisecondsLength: 20);
        var format = _capture.WaveFormat;
        Backend = $"WASAPI {_mm.FriendlyName} ({format.SampleRate} Hz, {format.Channels} ch)";
        _resampler = new Resampler(format.SampleRate, RealtimeEngine16k.Rate);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) =>
        {
            if (_stopping) return;
            OnFailed?.Invoke(e.Exception ?? new InvalidOperationException("microphone capture stopped unexpectedly"));
        };
        _capture.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            var format = _capture!.WaveFormat;
            int frames = e.BytesRecorded / (format.BlockAlign);
            if (_mono.Length < frames) _mono = new float[frames * 2];
            int n = WasapiAudio.ToMono(e.Buffer, e.BytesRecorded, format, _mono);
            int need = _resampler!.MaxOutput(n);
            if (_out.Length < need) _out = new float[need * 2];
            int got = _resampler.Process(_mono.AsSpan(0, n), _out);
            if (got > 0) _onSamples?.Invoke(_out.AsSpan(0, got).ToArray());
        }
        catch (Exception ex)
        {
            if (!_stopping) OnFailed?.Invoke(ex);
        }
    }

    public void Stop()
    {
        _stopping = true;
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        try { _mm?.Dispose(); } catch { }
        _mm = null;
    }

    public void Dispose() => Stop();
}

// Playback: NAudio pulls from the provider on its own thread; the
// provider pulls converted audio from the engine, converts the rate to
// the device's and spreads mono across the device's channels.
[SupportedOSPlatform("windows")]
internal sealed class WasapiOutput : IAudioOutput
{
    private readonly AudioDevice? _device;
    private MMDevice? _mm;
    private WasapiOut? _out;
    private volatile bool _stopping;

    public string Backend { get; private set; } = "WASAPI";
    public event Action<Exception>? OnFailed;

    public WasapiOutput(AudioDevice? device) { _device = device; }

    public void Start(Func<float[], int> pull, int sourceRate)
    {
        _mm = WasapiAudio.Open(_device, input: false);
        var mix = _mm.AudioClient.MixFormat;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
        Backend = $"WASAPI {_mm.FriendlyName} ({mix.SampleRate} Hz, {mix.Channels} ch)";
        var provider = new PullProvider(format, pull, sourceRate);
        _out = new WasapiOut(_mm, AudioClientShareMode.Shared, useEventSync: true, latency: 30);
        _out.PlaybackStopped += (_, e) =>
        {
            if (_stopping) return;
            OnFailed?.Invoke(e.Exception ?? new InvalidOperationException("playback stopped unexpectedly"));
        };
        _out.Init(provider);
        _out.Play();
    }

    public void Stop()
    {
        _stopping = true;
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        _out = null;
        try { _mm?.Dispose(); } catch { }
        _mm = null;
    }

    public void Dispose() => Stop();

    private sealed class PullProvider : IWaveProvider
    {
        private readonly Func<float[], int> _pull;
        private readonly Resampler? _resampler;
        private readonly int _channels;
        private readonly float[] _src;
        private float[] _queue = new float[8192];
        private int _queued;

        public WaveFormat WaveFormat { get; }

        public PullProvider(WaveFormat format, Func<float[], int> pull, int sourceRate)
        {
            WaveFormat = format;
            _pull = pull;
            _channels = format.Channels;
            _resampler = sourceRate == format.SampleRate ? null : new Resampler(sourceRate, format.SampleRate);
            _src = new float[Math.Max(256, sourceRate / 100)]; // 10 ms of source per pull
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / (4 * _channels);
            while (_queued < frames)
            {
                _pull(_src);   // fills _src (silence where nothing is ready)
                if (_resampler == null) Enqueue(_src, _src.Length);
                else
                {
                    var tmp = new float[_resampler.MaxOutput(_src.Length)];
                    int n = _resampler.Process(_src, tmp);
                    Enqueue(tmp, n);
                }
            }
            for (int f = 0; f < frames; f++)
            {
                var bytes = BitConverter.GetBytes(_queue[f]);
                for (int c = 0; c < _channels; c++)
                    Buffer.BlockCopy(bytes, 0, buffer, offset + (f * _channels + c) * 4, 4);
            }
            Array.Copy(_queue, frames, _queue, 0, _queued - frames);
            _queued -= frames;
            return frames * 4 * _channels;
        }

        private void Enqueue(float[] data, int n)
        {
            if (_queued + n > _queue.Length) Array.Resize(ref _queue, (_queued + n) * 2);
            Array.Copy(data, 0, _queue, _queued, n);
            _queued += n;
        }
    }
}

// The one rate the engine takes in; named here so the audio layer does
// not depend on the engine type.
internal static class RealtimeEngine16k
{
    public const int Rate = 16000;
}
