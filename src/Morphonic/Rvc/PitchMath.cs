using System;

namespace Morphonic.Rvc;

// The arithmetic around pitch that RVC does in numpy: turning the pitch
// model's salience map into a frequency per frame, transposing, and the
// coarse 1..255 bins the synthesizer's pitch embedding takes.
public static class PitchMath
{
    public const double F0Min = 50, F0Max = 1100;
    private static readonly double MelMin = 1127 * Math.Log(1 + F0Min / 700);
    private static readonly double MelMax = 1127 * Math.Log(1 + F0Max / 700);

    // RMVPE: 360 salience bins, 20 cents apart from 1997.379… cents
    // (a ≈ 31.7 Hz reference), decoded as the salience-weighted average
    // over the nine bins around the peak. Peaks under `threshold` are
    // unvoiced (f0 = 0).
    public const int SalienceBins = 360;
    private const double CentsBase = 1997.3794084376191;

    public static float[] DecodeSalience(ReadOnlySpan<float> salience, int frames, double threshold = 0.03)
    {
        var f0 = new float[frames];
        for (int t = 0; t < frames; t++)
        {
            var row = salience.Slice(t * SalienceBins, SalienceBins);
            int center = 0;
            float max = row[0];
            for (int b = 1; b < SalienceBins; b++) if (row[b] > max) { max = row[b]; center = b; }
            if (max <= threshold) { f0[t] = 0f; continue; }
            double num = 0, den = 0;
            for (int b = center - 4; b <= center + 4; b++)
            {
                // Beyond the ends the padded salience is zero.
                if (b < 0 || b >= SalienceBins) continue;
                double s = row[b];
                num += s * (20.0 * b + CentsBase);
                den += s;
            }
            double cents = den > 0 ? num / den : 0;
            double hz = 10 * Math.Pow(2, cents / 1200);
            f0[t] = hz == 10 ? 0f : (float)hz;
        }
        return f0;
    }

    public static float Transpose(float f0, int semitones) => f0 * (float)Math.Pow(2, semitones / 12.0);

    // f0 (Hz) -> the 1..255 embedding index: mel-scaled between F0Min and
    // F0Max, 1 for unvoiced, rounded half-to-even like numpy's rint.
    public static long Coarse(float f0)
    {
        if (f0 <= 0) return 1;
        double mel = 1127 * Math.Log(1 + f0 / 700.0);
        mel = (mel - MelMin) * 254 / (MelMax - MelMin) + 1;
        if (mel <= 1) return 1;
        if (mel > 255) return 255;
        return (long)Math.Round(mel, MidpointRounding.ToEven);
    }
}
