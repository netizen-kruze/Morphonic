using System;

namespace Morphonic.Rvc;

// One window of the real-time pipeline, following RVC's rtrvc.py: the
// content encoder over the whole 16 kHz window (context + new block),
// pitch over the window's tail with a rolling cache so every frame keeps
// the estimate it got when it was newest, then the synthesizer on the
// tail frames. Owns the three ONNX sessions.
public sealed class VoiceConverter : IDisposable
{
    public ContentEncoder Encoder { get; }
    public RmvpePitch Pitch { get; }
    public VoiceModel Voice { get; }

    public int PitchSemitones { get; set; }
    public int SpeakerId { get; set; }
    public double VoicingThreshold { get; set; } = 0.03;

    private long[] _cachePitch = Array.Empty<long>();
    private float[] _cachePitchf = Array.Empty<float>();

    public VoiceConverter(ContentEncoder encoder, RmvpePitch pitch, VoiceModel voice)
    {
        Encoder = encoder;
        Pitch = pitch;
        Voice = voice;
    }

    public int SampleRate => Voice.SampleRate;
    public int Hop => Voice.Hop;

    public void ResetCache()
    {
        _cachePitch = Array.Empty<long>();
        _cachePitchf = Array.Empty<float>();
    }

    // The RMVPE window for a block: the new block plus 50 ms, rounded up so
    // the model sees a whole number of its 32-frame groups (RVC's recipe).
    public static int PitchWindow(int block16) => 5120 * ((block16 + 800 - 1) / 5120 + 1) - 160;

    // window16: the whole 16 kHz analysis window, newest samples last;
    // block16: how many of them arrived since the previous call;
    // skipHead: frames of context the decoder skips; returnFrames: frames
    // of audio wanted. Returns returnFrames * Hop samples at the voice's
    // sample rate (fewer only if the window is too short).
    public float[] Convert(float[] window16, int block16, int skipHead, int returnFrames)
    {
        int pLen = window16.Length / 160;
        if (pLen <= skipHead) throw new ArgumentException("the window has no frames past skipHead");

        // Content features: 20 ms frames, the last one duplicated, then
        // each repeated twice to land on the 10 ms grid.
        var raw = Encoder.Extract(window16, out int encFrames);
        var feats = new float[pLen * VoiceModel.FeatureDim];
        if (encFrames == 0) throw new InvalidOperationException("the content encoder produced no frames — the window is too short");
        for (int i = 0; i < pLen; i++)
        {
            int src = Math.Min(i / 2, encFrames - 1);
            Array.Copy(raw, src * VoiceModel.FeatureDim, feats, i * VoiceModel.FeatureDim, VoiceModel.FeatureDim);
        }

        // Pitch over the tail; the cache keeps earlier frames' estimates.
        if (_cachePitch.Length != pLen)
        {
            _cachePitch = new long[pLen];
            _cachePitchf = new float[pLen];
            for (int i = 0; i < pLen; i++) _cachePitch[i] = 1;
        }
        int f0len = Math.Min(window16.Length, PitchWindow(block16));
        var f0 = Pitch.Infer(window16.AsSpan(window16.Length - f0len), VoicingThreshold);
        int shift = Math.Min(block16 / 160, pLen);
        if (shift > 0)
        {
            Array.Copy(_cachePitch, shift, _cachePitch, 0, pLen - shift);
            Array.Copy(_cachePitchf, shift, _cachePitchf, 0, pLen - shift);
        }
        // RVC writes pitch[3:-1] to the end of the cache: the first three
        // frames and the last one of the estimate are edge frames.
        int n = f0.Length;
        int usable = Math.Max(0, n - 4);
        int write = Math.Min(usable, pLen);
        for (int j = 0; j < write; j++)
        {
            float hz = PitchMath.Transpose(f0[3 + (usable - write) + j], PitchSemitones);
            _cachePitchf[pLen - write + j] = hz;
            _cachePitch[pLen - write + j] = PitchMath.Coarse(hz);
        }

        var audio = Voice.Synthesize(feats, pLen, _cachePitch, _cachePitchf, SpeakerId, skipHead);
        int want = returnFrames * Voice.Hop;
        return audio.Length <= want ? audio : audio.AsSpan(0, want).ToArray();
    }

    public void Dispose()
    {
        Voice.Dispose();
        Pitch.Dispose();
        Encoder.Dispose();
    }
}
