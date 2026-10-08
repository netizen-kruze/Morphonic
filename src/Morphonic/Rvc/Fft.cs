using System;

namespace Morphonic.Rvc;

// In-place iterative radix-2 complex FFT for power-of-two sizes. Enough
// for the 1024-point analysis the pitch model's mel front end needs;
// twiddles are computed once per size.
public sealed class Fft
{
    public int Size { get; }
    private readonly float[] _cos, _sin;
    private readonly int[] _rev;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0) throw new ArgumentException("size must be a power of two", nameof(size));
        Size = size;
        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            _cos[i] = (float)Math.Cos(2 * Math.PI * i / size);
            _sin[i] = (float)Math.Sin(2 * Math.PI * i / size);
        }
        _rev = new int[size];
        int bits = (int)Math.Log2(size);
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            _rev[i] = r;
        }
    }

    // Forward transform of (re, im) in place.
    public void Transform(Span<float> re, Span<float> im)
    {
        int n = Size;
        for (int i = 0; i < n; i++)
        {
            int j = _rev[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1;
            int step = n / len;
            for (int i = 0; i < n; i += len)
            {
                for (int k = 0; k < half; k++)
                {
                    float wr = _cos[k * step], wi = -_sin[k * step];
                    int a = i + k, b = a + half;
                    float tr = re[b] * wr - im[b] * wi;
                    float ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
            }
        }
    }
}
