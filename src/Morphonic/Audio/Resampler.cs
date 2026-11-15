using System;

namespace Morphonic.Audio;

// Streaming sample-rate converter: a windowed-sinc (Kaiser) interpolation
// filter read from a polyphase table, so each output sample costs Taps
// multiply-adds and no transcendental functions. Handles any rate pair
// (48000 -> 16000, 40000 -> 48000, 44100 -> 16000 …) with the same code;
// state is kept between calls so block boundaries are seamless. Latency
// is Taps/2 input samples (1.5 ms at 48 kHz -> 16 kHz).
//
// The filter spans ZeroCrossings zero crossings of the lower rate on each
// side: 144 taps for 48 kHz -> 16 kHz, 48 for 40 kHz -> 48 kHz. Until
// 1.0.5 it was 32 taps whatever the ratio, which for the microphone
// (48 -> 16 kHz) meant a transition band 6.5 kHz wide: speech above 4 kHz
// reached the content encoder dulled (-4 dB at 7 kHz) and hiss at 8-10 kHz
// folded back into the 6-8 kHz band (9 kHz only 20 dB down).
public sealed class Resampler
{
    public const int ZeroCrossings = 24;   // per side, at the lower of the two rates
    private const int Phases = 512;        // fractional positions tabulated
    private const double Beta = 8.6;       // Kaiser: ~ -86 dB stopband

    public int Taps { get; }               // filter length (both sides together)
    private readonly int _half;

    public int InputRate { get; }
    public int OutputRate { get; }

    private readonly double _step;         // input samples per output sample
    private readonly float[] _table;       // [Phases + 1][Taps], last row = phase 1.0
    private float[] _history;              // the previous call's tail, oldest first
    private int _historyLen;
    private double _pos;                   // next output's position, in input samples from history[0]

    public Resampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        InputRate = inputRate;
        OutputRate = outputRate;
        _step = (double)inputRate / outputRate;
        Taps = 2 * ZeroCrossings * (int)Math.Ceiling(Math.Max(1.0, _step));
        _half = Taps / 2;
        _history = new float[Taps];
        // Cutoff a little under the lower Nyquist, relative to the input rate.
        double cutoff = Math.Min(1.0, (double)outputRate / inputRate) * 0.95;
        _table = BuildTable(cutoff, Taps);
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_history);
        _historyLen = Taps;    // Taps zeros of history: the filter starts centered on real data at once
        _pos = _half;
    }

    // Upper bound on the output a call with `inputCount` samples can produce.
    public int MaxOutput(int inputCount) => (int)Math.Ceiling(inputCount / _step) + 2;

    // Converts `input`, writing to `output`; returns the samples written.
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        // Working buffer: history followed by the new samples.
        int total = _historyLen + input.Length;
        var buf = total <= 4096 ? stackalloc float[total] : new float[total];
        _history.AsSpan(0, _historyLen).CopyTo(buf);
        input.CopyTo(buf.Slice(_historyLen));

        int written = 0;
        // An output sample at position p reads taps up to floor(p) + half.
        int taps = Taps, half = _half;
        while (written < output.Length)
        {
            int center = (int)Math.Floor(_pos);
            if (center + half > total - 1) break;
            double frac = _pos - center;
            int phase = (int)(frac * Phases);
            float blend = (float)(frac * Phases - phase);
            int rowA = phase * taps, rowB = (phase + 1) * taps;
            int start = center - half + 1;
            float acc = 0f;
            for (int k = 0; k < taps; k++)
            {
                float coef = _table[rowA + k] + (_table[rowB + k] - _table[rowA + k]) * blend;
                acc += buf[start + k] * coef;
            }
            output[written++] = acc;
            _pos += _step;
        }

        // Keep the last Taps samples as history for the next call and
        // rebase the position onto them.
        int keep = Math.Min(Taps, total);
        if (_history.Length < keep) _history = new float[keep];
        buf.Slice(total - keep, keep).CopyTo(_history);
        _historyLen = keep;
        _pos -= total - keep;
        return written;
    }

    // Table row r holds the Taps coefficients for an output sample whose
    // position lies r/Phases past input sample `center`: tap k multiplies
    // input[center - half + 1 + k], at distance (center + frac) - that.
    private static float[] BuildTable(double cutoff, int taps)
    {
        int half = taps / 2;
        var table = new float[(Phases + 1) * taps];
        const double beta = Beta;
        double i0beta = BesselI0(beta);
        for (int r = 0; r <= Phases; r++)
        {
            double frac = (double)r / Phases;
            double sum = 0;
            for (int k = 0; k < taps; k++)
            {
                double t = frac - (k - half + 1);      // distance from the output position to this tap
                double x = t / half;                    // window argument in [-1, 1]
                double w = Math.Abs(x) >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - x * x)) / i0beta;
                double s = t == 0 ? cutoff : Math.Sin(Math.PI * cutoff * t) / (Math.PI * t);
                table[r * taps + k] = (float)(s * w);
                sum += s * w;
            }
            // Unity DC gain per phase.
            if (sum > 0) for (int k = 0; k < taps; k++) table[r * taps + k] = (float)(table[r * taps + k] / sum);
        }
        return table;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, y = x * x / 4;
        for (int k = 1; k < 50; k++)
        {
            term *= y / (k * k);
            sum += term;
            if (term < sum * 1e-12) break;
        }
        return sum;
    }
}
