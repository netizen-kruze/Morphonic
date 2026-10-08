using System;

namespace Morphonic.Rvc;

// The log-mel front end the RMVPE pitch model was trained on, reproduced
// from RVC's MelSpectrogram(is_half, 128, 16000, 1024, 160, None, 30, 8000):
// a centered STFT (1024-point, periodic Hann, hop 160, reflect padding —
// torch.stft's defaults), magnitudes, librosa's HTK mel filter bank with
// Slaney area normalisation, then log(max(mel, 1e-5)). One frame per
// 10 ms of 16 kHz audio, plus one: frames = samples / 160 + 1.
public sealed class MelSpectrogram
{
    public const int SampleRate = 16000, NFft = 1024, Hop = 160, Bins = 128;
    public const double FMin = 30, FMax = 8000, Clamp = 1e-5;
    private const int Freqs = NFft / 2 + 1;

    private readonly Fft _fft = new(NFft);
    private readonly float[] _window = new float[NFft];
    // Sparse filter bank: each mel bin spans a contiguous range of FFT bins.
    private readonly int[] _start = new int[Bins], _length = new int[Bins];
    private readonly float[][] _weights = new float[Bins][];

    public MelSpectrogram()
    {
        for (int i = 0; i < NFft; i++) _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / NFft)); // periodic Hann
        var basis = FilterBank();
        for (int m = 0; m < Bins; m++)
        {
            int s = -1, e = -1;
            for (int k = 0; k < Freqs; k++)
            {
                if (basis[m, k] <= 0) continue;
                if (s < 0) s = k;
                e = k;
            }
            _start[m] = Math.Max(s, 0);
            _length[m] = s < 0 ? 0 : e - s + 1;
            _weights[m] = new float[_length[m]];
            for (int k = 0; k < _length[m]; k++) _weights[m][k] = (float)basis[m, _start[m] + k];
        }
    }

    public static int FrameCount(int samples) => samples / Hop + 1;

    // Returns [Bins * frames] in bin-major order (bin b, frame t at b * frames + t)
    // — the [1, 128, T] layout the ONNX model takes.
    public float[] Compute(ReadOnlySpan<float> audio)
    {
        int n = audio.Length;
        int frames = FrameCount(n);
        int pad = NFft / 2;
        // Reflect padding, as torch.stft(center=True) does.
        var padded = new float[n + 2 * pad];
        audio.CopyTo(padded.AsSpan(pad));
        for (int i = 0; i < pad; i++)
        {
            padded[pad - 1 - i] = Reflect(audio, i + 1);
            padded[pad + n + i] = Reflect(audio, n - 2 - i);
        }

        var re = new float[NFft];
        var im = new float[NFft];
        var mag = new float[Freqs];
        var mel = new float[Bins * frames];
        for (int t = 0; t < frames; t++)
        {
            int offset = t * Hop;
            for (int i = 0; i < NFft; i++) { re[i] = padded[offset + i] * _window[i]; im[i] = 0f; }
            _fft.Transform(re, im);
            for (int k = 0; k < Freqs; k++) mag[k] = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
            for (int m = 0; m < Bins; m++)
            {
                float acc = 0f;
                var w = _weights[m];
                int s = _start[m];
                for (int k = 0; k < w.Length; k++) acc += w[k] * mag[s + k];
                mel[m * frames + t] = MathF.Log(MathF.Max(acc, (float)Clamp));
            }
        }
        return mel;
    }

    // Reflection index for signals shorter than the pad (torch would refuse;
    // the realtime windows here are always long enough, this is for safety).
    private static float Reflect(ReadOnlySpan<float> x, int i)
    {
        int n = x.Length;
        if (n == 1) return x[0];
        int period = 2 * (n - 1);
        i = ((i % period) + period) % period;
        if (i >= n) i = period - i;
        return x[i];
    }

    // librosa.filters.mel(sr=16000, n_fft=1024, n_mels=128, fmin=30, fmax=8000, htk=True):
    // triangular filters on an HTK-mel grid, each scaled to unit area (norm="slaney").
    internal static double[,] FilterBank()
    {
        var basis = new double[Bins, Freqs];
        var fftFreqs = new double[Freqs];
        for (int k = 0; k < Freqs; k++) fftFreqs[k] = (double)SampleRate / 2 * k / (Freqs - 1);
        var melF = new double[Bins + 2];
        double melMin = HzToMel(FMin), melMax = HzToMel(FMax);
        for (int i = 0; i < Bins + 2; i++) melF[i] = MelToHz(melMin + (melMax - melMin) * i / (Bins + 1));
        for (int m = 0; m < Bins; m++)
        {
            double lo = melF[m], mid = melF[m + 1], hi = melF[m + 2];
            double enorm = 2.0 / (hi - lo);
            for (int k = 0; k < Freqs; k++)
            {
                double lower = (fftFreqs[k] - lo) / (mid - lo);
                double upper = (hi - fftFreqs[k]) / (hi - mid);
                double w = Math.Max(0, Math.Min(lower, upper));
                basis[m, k] = w * enorm;
            }
        }
        return basis;
    }

    internal static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);
    internal static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);
}
