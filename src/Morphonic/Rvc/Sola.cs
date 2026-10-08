using System;

namespace Morphonic.Rvc;

// SOLA (synchronous overlap-add) joins consecutive converted blocks
// without clicks: the new block is slid by up to `search` samples to the
// position where its head best correlates with the tail kept from the
// previous block, then the two are crossfaded with complementary sin²
// windows. The recipe follows RVC's realtime GUI (itself from DDSP-SVC).
public static class Sola
{
    // Argmax over offsets 0..search of the normalised cross-correlation
    // between candidate[offset .. offset+prev.Length) and prev.
    public static int FindOffset(ReadOnlySpan<float> candidate, ReadOnlySpan<float> previous, int search)
    {
        int n = previous.Length;
        if (n == 0 || candidate.Length < n) return 0;
        search = Math.Min(search, candidate.Length - n);
        int best = 0;
        double bestScore = double.NegativeInfinity;
        for (int offset = 0; offset <= search; offset++)
        {
            double dot = 0, energy = 0;
            for (int i = 0; i < n; i++)
            {
                float c = candidate[offset + i];
                dot += c * previous[i];
                energy += c * c;
            }
            double score = dot / Math.Sqrt(energy + 1e-8);
            if (score > bestScore) { bestScore = score; best = offset; }
        }
        return best;
    }

    // sin²-shaped fade-in over `length` samples (the fade-out is 1 - this).
    public static float[] FadeIn(int length)
    {
        var w = new float[length];
        for (int i = 0; i < length; i++)
        {
            double x = length == 1 ? 1 : (double)i / (length - 1);
            double s = Math.Sin(0.5 * Math.PI * x);
            w[i] = (float)(s * s);
        }
        return w;
    }

    // In place: head[i] = head[i] * fadeIn[i] + previous[i] * (1 - fadeIn[i]).
    public static void Crossfade(Span<float> head, ReadOnlySpan<float> previous, ReadOnlySpan<float> fadeIn)
    {
        int n = Math.Min(head.Length, Math.Min(previous.Length, fadeIn.Length));
        for (int i = 0; i < n; i++) head[i] = head[i] * fadeIn[i] + previous[i] * (1f - fadeIn[i]);
    }
}
