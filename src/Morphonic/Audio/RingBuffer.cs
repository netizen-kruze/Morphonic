using System;

namespace Morphonic.Audio;

// A fixed-capacity float FIFO shared between the audio callback threads
// and the conversion worker. Writes past the capacity drop the oldest
// samples (the reader is too slow — the honest failure mode for live
// audio); reads return what is there and leave the rest to the caller
// (silence on an underrun). One lock; the critical sections are copies.
public sealed class RingBuffer
{
    private readonly float[] _data;
    private readonly object _gate = new();
    private int _head;      // next write position
    private int _count;
    public long Dropped { get; private set; }

    public RingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _data = new float[capacity];
    }

    public int Capacity => _data.Length;
    public int Count { get { lock (_gate) return _count; } }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (samples.Length >= _data.Length)
            {
                Dropped += _count + samples.Length - _data.Length;
                samples.Slice(samples.Length - _data.Length).CopyTo(_data);
                _head = 0;
                _count = _data.Length;
                return;
            }
            int overflow = _count + samples.Length - _data.Length;
            if (overflow > 0) { _count -= overflow; Dropped += overflow; }
            int first = Math.Min(samples.Length, _data.Length - _head);
            samples.Slice(0, first).CopyTo(_data.AsSpan(_head));
            samples.Slice(first).CopyTo(_data);
            _head = (_head + samples.Length) % _data.Length;
            _count += samples.Length;
        }
    }

    // Reads up to dst.Length samples; returns how many were available.
    public int Read(Span<float> dst)
    {
        lock (_gate)
        {
            int n = Math.Min(dst.Length, _count);
            int tail = (_head - _count + _data.Length) % _data.Length;
            int first = Math.Min(n, _data.Length - tail);
            _data.AsSpan(tail, first).CopyTo(dst);
            _data.AsSpan(0, n - first).CopyTo(dst.Slice(first));
            _count -= n;
            return n;
        }
    }

    // Drops the oldest `n` samples (catching up after a stall).
    public int Skip(int n)
    {
        lock (_gate)
        {
            n = Math.Min(n, _count);
            _count -= n;
            return n;
        }
    }

    public void Clear()
    {
        lock (_gate) { _count = 0; _head = 0; }
    }
}
