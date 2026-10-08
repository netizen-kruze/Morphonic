using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Morphonic.Rvc;

public enum TorchDtype { Float32, Float16, BFloat16, Float64, Int64, Int32, Int16, Int8, UInt8, Bool }

// One tensor of a checkpoint: where its numbers sit in the zip and how
// they are laid out. Nothing is read until ReadFloat32.
public sealed class TorchTensor
{
    public string StorageKey { get; }
    public TorchDtype Dtype { get; }
    public long StorageOffset { get; }
    public long[] Shape { get; }
    public long[] Stride { get; }

    internal TorchTensor(string storageKey, TorchDtype dtype, long storageOffset, long[] shape, long[] stride)
    {
        StorageKey = storageKey; Dtype = dtype; StorageOffset = storageOffset; Shape = shape; Stride = stride;
    }

    public long Count => Shape.Aggregate(1L, (a, b) => a * b);

    public bool IsContiguous
    {
        get
        {
            long expected = 1;
            for (int i = Shape.Length - 1; i >= 0; i--)
            {
                if (Shape[i] != 1 && Stride[i] != expected) return false;
                expected *= Shape[i];
            }
            return true;
        }
    }

    public override string ToString() => $"{Dtype}[{string.Join(",", Shape)}]";
}

// Reads PyTorch checkpoints (torch.save, zip format) without PyTorch: the
// zip holds a pickle naming every tensor and one raw file per storage.
// The pickle is walked by a tiny interpreter that knows the few opcodes a
// state dict uses and only three "globals" (OrderedDict, the tensor
// rebuild helper, the storage classes). A pickle asking for anything else
// is refused: no code in a checkpoint ever runs here, which is also what
// makes it safe to open a file somebody else trained.
public sealed class TorchCheckpoint : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly string _prefix;   // folder inside the zip ("archive/")

    // Plain .NET values: Dictionary<object, object>, List<object>,
    // object[] (tuple), string, long, double, bool, null, TorchTensor.
    public object? Root { get; }

    private TorchCheckpoint(ZipArchive zip, string prefix, object? root)
    {
        _zip = zip; _prefix = prefix; Root = root;
    }

    public static TorchCheckpoint Open(string path)
    {
        var zip = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read);
        try
        {
            var pkl = zip.Entries.FirstOrDefault(e => e.FullName == "data.pkl" || e.FullName.EndsWith("/data.pkl", StringComparison.Ordinal))
                      ?? throw new InvalidDataException("not a PyTorch checkpoint (no data.pkl in the archive)");
            var prefix = pkl.FullName.Substring(0, pkl.FullName.Length - "data.pkl".Length);
            byte[] data;
            using (var s = pkl.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                data = ms.ToArray();
            }
            var root = new Unpickler(data).Load();
            return new TorchCheckpoint(zip, prefix, root);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    // The tensors under `key` ("model", "weight"), or the top-level ones
    // when key is empty. Only string-keyed tensor entries are returned.
    public Dictionary<string, TorchTensor> StateDict(string key)
    {
        object? node = Root;
        if (key.Length > 0)
        {
            if (Root is not Dictionary<object, object> d || !d.TryGetValue(key, out node))
                throw new InvalidDataException($"the checkpoint has no '{key}' entry");
        }
        if (node is not Dictionary<object, object> dict)
            throw new InvalidDataException("the checkpoint's state is not a dictionary");
        var result = new Dictionary<string, TorchTensor>(StringComparer.Ordinal);
        foreach (var (k, v) in dict)
            if (k is string name && v is TorchTensor t) result[name] = t;
        return result;
    }

    // A top-level value that is not a tensor (an RVC voice's "config",
    // "sr", "f0", "version").
    public object? Value(string key) =>
        Root is Dictionary<object, object> d && d.TryGetValue(key, out var v) ? v : null;

    public float[] ReadFloat32(TorchTensor t)
    {
        long count = t.Count;
        if (count > int.MaxValue / 4) throw new InvalidDataException("tensor too large");
        int size = ElementSize(t.Dtype);
        var entry = _zip.GetEntry(_prefix + "data/" + t.StorageKey)
                    ?? throw new InvalidDataException($"storage {t.StorageKey} is missing from the checkpoint");
        if (t.IsContiguous)
        {
            var raw = ReadRange(entry, t.StorageOffset * size, count * size);
            return Convert(raw, t.Dtype, (int)count);
        }
        // Strided (a transposed or sliced tensor saved as a view): read
        // the storage span the view touches and gather element by element.
        long span = 1;
        for (int i = 0; i < t.Shape.Length; i++) span += (t.Shape[i] - 1) * t.Stride[i];
        var all = Convert(ReadRange(entry, t.StorageOffset * size, span * size), t.Dtype, (int)span);
        var result = new float[count];
        var idx = new long[t.Shape.Length];
        for (long n = 0; n < count; n++)
        {
            long off = 0;
            for (int i = 0; i < idx.Length; i++) off += idx[i] * t.Stride[i];
            result[n] = all[off];
            for (int i = idx.Length - 1; i >= 0; i--)
            {
                if (++idx[i] < t.Shape[i]) break;
                idx[i] = 0;
            }
        }
        return result;
    }

    private static byte[] ReadRange(ZipArchiveEntry entry, long offset, long length)
    {
        if (offset + length > entry.Length)
            throw new InvalidDataException($"storage {entry.Name} is shorter than the tensor that uses it");
        var buf = new byte[length];
        using var s = entry.Open();
        // Entry streams are forward-only; skip to the offset in chunks.
        if (offset > 0)
        {
            var skip = new byte[Math.Min(offset, 1 << 20)];
            long left = offset;
            while (left > 0)
            {
                int n = s.Read(skip, 0, (int)Math.Min(left, skip.Length));
                if (n <= 0) throw new EndOfStreamException();
                left -= n;
            }
        }
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) throw new EndOfStreamException();
            got += n;
        }
        return buf;
    }

    public static int ElementSize(TorchDtype d) => d switch
    {
        TorchDtype.Float64 or TorchDtype.Int64 => 8,
        TorchDtype.Float32 or TorchDtype.Int32 => 4,
        TorchDtype.Float16 or TorchDtype.BFloat16 or TorchDtype.Int16 => 2,
        _ => 1,
    };

    // Little-endian storage bytes to float32. Half to single is exact.
    internal static float[] Convert(byte[] raw, TorchDtype dtype, int count)
    {
        var f = new float[count];
        var span = raw.AsSpan();
        switch (dtype)
        {
            case TorchDtype.Float32:
                if (BitConverter.IsLittleEndian) MemoryMarshal.Cast<byte, float>(span).Slice(0, count).CopyTo(f);
                else for (int i = 0; i < count; i++) f[i] = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(i * 4));
                break;
            case TorchDtype.Float16:
                for (int i = 0; i < count; i++) f[i] = (float)BinaryPrimitives.ReadHalfLittleEndian(span.Slice(i * 2));
                break;
            case TorchDtype.BFloat16:
                for (int i = 0; i < count; i++) f[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(i * 2)) << 16);
                break;
            case TorchDtype.Float64:
                for (int i = 0; i < count; i++) f[i] = (float)BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(i * 8));
                break;
            case TorchDtype.Int64:
                for (int i = 0; i < count; i++) f[i] = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(i * 8));
                break;
            case TorchDtype.Int32:
                for (int i = 0; i < count; i++) f[i] = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(i * 4));
                break;
            case TorchDtype.Int16:
                for (int i = 0; i < count; i++) f[i] = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(i * 2));
                break;
            case TorchDtype.Int8:
                for (int i = 0; i < count; i++) f[i] = (sbyte)raw[i];
                break;
            default:
                for (int i = 0; i < count; i++) f[i] = raw[i];
                break;
        }
        return f;
    }

    public void Dispose() => _zip.Dispose();

    // ── the pickle interpreter ──────────────────────────────────────

    private sealed record Global(string Module, string Name);
    private sealed record StorageRef(TorchDtype Dtype, string Key, long Count);

    private sealed class Unpickler
    {
        private readonly byte[] _d;
        private int _p;
        private readonly List<object?> _stack = new();
        private readonly Stack<int> _marks = new();
        private readonly Dictionary<long, object?> _memo = new();

        public Unpickler(byte[] data) { _d = data; }

        public object? Load()
        {
            while (_p < _d.Length)
            {
                byte op = _d[_p++];
                switch (op)
                {
                    case 0x80: _p++; break;                                  // PROTO
                    case (byte)'.': return _stack.Count > 0 ? _stack[^1] : null;   // STOP
                    case 0x95: _p += 8; break;                               // FRAME
                    case (byte)'(': _marks.Push(_stack.Count); break;        // MARK
                    case (byte)'N': Push(null); break;                       // NONE
                    case 0x88: Push(true); break;                            // NEWTRUE
                    case 0x89: Push(false); break;                           // NEWFALSE
                    case (byte)'J': Push((long)BinaryPrimitives.ReadInt32LittleEndian(Take(4))); break;   // BININT
                    case (byte)'K': Push((long)_d[_p++]); break;             // BININT1
                    case (byte)'M': Push((long)BinaryPrimitives.ReadUInt16LittleEndian(Take(2))); break;  // BININT2
                    case 0x8a: { int n = _d[_p++]; Push(ReadLong(n)); break; }                           // LONG1
                    case 0x8b: { int n = BinaryPrimitives.ReadInt32LittleEndian(Take(4)); Push(ReadLong(n)); break; }  // LONG4
                    case (byte)'G': Push(BinaryPrimitives.ReadDoubleBigEndian(Take(8))); break;           // BINFLOAT
                    case (byte)'X': Push(Utf8(BinaryPrimitives.ReadInt32LittleEndian(Take(4)))); break;   // BINUNICODE
                    case 0x8c: Push(Utf8(_d[_p++])); break;                                               // SHORT_BINUNICODE
                    case 0x8d: Push(Utf8(checked((int)BinaryPrimitives.ReadInt64LittleEndian(Take(8))))); break;  // BINUNICODE8
                    case (byte)'T': Push(Latin1(BinaryPrimitives.ReadInt32LittleEndian(Take(4)))); break; // BINSTRING
                    case (byte)'U': Push(Latin1(_d[_p++])); break;                                        // SHORT_BINSTRING
                    case (byte)'B': Push(Take(BinaryPrimitives.ReadInt32LittleEndian(Take(4))).ToArray()); break;  // BINBYTES
                    case (byte)'C': Push(Take(_d[_p++]).ToArray()); break;                                // SHORT_BINBYTES
                    case 0x8e: Push(Take(checked((int)BinaryPrimitives.ReadInt64LittleEndian(Take(8)))).ToArray()); break;  // BINBYTES8
                    case (byte)'}': Push(new Dictionary<object, object?>()); break;                      // EMPTY_DICT
                    case (byte)']': Push(new List<object?>()); break;                                     // EMPTY_LIST
                    case (byte)')': Push(Array.Empty<object?>()); break;                                  // EMPTY_TUPLE
                    case (byte)'t': { var items = PopMark(); Push(items.ToArray()); break; }              // TUPLE
                    case 0x85: { var a = Pop(); Push(new[] { a }); break; }                               // TUPLE1
                    case 0x86: { var b = Pop(); var a = Pop(); Push(new[] { a, b }); break; }             // TUPLE2
                    case 0x87: { var c = Pop(); var b = Pop(); var a = Pop(); Push(new[] { a, b, c }); break; }  // TUPLE3
                    case (byte)'l': { var items = PopMark(); Push(new List<object?>(items)); break; }     // LIST
                    case (byte)'d': { var items = PopMark(); var dict = new Dictionary<object, object?>(); for (int i = 0; i + 1 < items.Count; i += 2) dict[items[i]!] = items[i + 1]; Push(dict); break; }  // DICT
                    case (byte)'a': { var v = Pop(); AsList().Add(v); break; }                            // APPEND
                    case (byte)'e': { var items = PopMark(); AsList().AddRange(items); break; }           // APPENDS
                    case (byte)'s': { var v = Pop(); var k = Pop(); AsDict()[k!] = v; break; }            // SETITEM
                    case (byte)'u': { var items = PopMark(); var dict = AsDict(); for (int i = 0; i + 1 < items.Count; i += 2) dict[items[i]!] = items[i + 1]; break; }  // SETITEMS
                    case (byte)'q': _memo[_d[_p++]] = Top(); break;                                       // BINPUT
                    case (byte)'r': _memo[BinaryPrimitives.ReadUInt32LittleEndian(Take(4))] = Top(); break;   // LONG_BINPUT
                    case 0x94: _memo[_memo.Count] = Top(); break;                                         // MEMOIZE
                    case (byte)'h': Push(_memo[_d[_p++]]); break;                                         // BINGET
                    case (byte)'j': Push(_memo[BinaryPrimitives.ReadUInt32LittleEndian(Take(4))]); break; // LONG_BINGET
                    case (byte)'c': { var module = Line(); var name = Line(); Push(Resolve(module, name)); break; }  // GLOBAL
                    case 0x93: { var name = (string)Pop()!; var module = (string)Pop()!; Push(Resolve(module, name)); break; }  // STACK_GLOBAL
                    case (byte)'R': { var args = (object?[])Pop()!; var callable = Pop(); Push(Call(callable, args)); break; }   // REDUCE
                    case 0x81: { var args = (object?[])Pop()!; var cls = Pop(); Push(Call(cls, args)); break; }                  // NEWOBJ
                    case (byte)'b': { var state = Pop(); Build(Top(), state); break; }                    // BUILD
                    case (byte)'Q': { var pid = Pop(); Push(Persistent(pid)); break; }                    // BINPERSID
                    case (byte)'0': Pop(); break;                                                         // POP
                    case (byte)'2': Push(Top()); break;                                                   // DUP
                    default:
                        throw new NotSupportedException($"pickle opcode 0x{op:x2} at {_p - 1} is not part of a tensor checkpoint");
                }
            }
            throw new InvalidDataException("pickle ended without STOP");
        }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || _p + n > _d.Length) throw new InvalidDataException("truncated pickle");
            var s = _d.AsSpan(_p, n);
            _p += n;
            return s;
        }

        private string Utf8(int n) => Encoding.UTF8.GetString(Take(n));
        private string Latin1(int n) => Encoding.Latin1.GetString(Take(n));

        private string Line()
        {
            int start = _p;
            while (_p < _d.Length && _d[_p] != (byte)'\n') _p++;
            var s = Encoding.ASCII.GetString(_d, start, _p - start);
            _p++;
            return s;
        }

        private long ReadLong(int n)
        {
            if (n == 0) return 0;
            if (n > 8) throw new NotSupportedException("integer too large");
            var b = Take(n);
            long v = 0;
            for (int i = n - 1; i >= 0; i--) v = (v << 8) | b[i];
            int shift = 64 - 8 * n;
            return (v << shift) >> shift;   // sign-extend
        }

        private void Push(object? v) => _stack.Add(v);
        private object? Pop() { var v = _stack[^1]; _stack.RemoveAt(_stack.Count - 1); return v; }
        private object? Top() => _stack[^1];
        private List<object?> PopMark()
        {
            int m = _marks.Pop();
            var items = _stack.GetRange(m, _stack.Count - m);
            _stack.RemoveRange(m, _stack.Count - m);
            return items;
        }
        private List<object?> AsList() => Top() as List<object?> ?? throw new InvalidDataException("pickle: not a list");
        private Dictionary<object, object?> AsDict() => Top() as Dictionary<object, object?> ?? throw new InvalidDataException("pickle: not a dict");

        private static Global Resolve(string module, string name)
        {
            bool ok = (module, name) switch
            {
                ("collections", "OrderedDict") => true,
                ("torch._utils", "_rebuild_tensor_v2") => true,
                ("torch._utils", "_rebuild_parameter") => true,
                ("torch", _) when name.EndsWith("Storage", StringComparison.Ordinal) => true,
                _ => false,
            };
            if (!ok) throw new NotSupportedException($"the checkpoint needs {module}.{name} to load — only plain tensor checkpoints are supported");
            return new Global(module, name);
        }

        private static object? Call(object? callable, object?[] args)
        {
            if (callable is not Global g) throw new InvalidDataException("pickle: call of a non-global");
            switch (g.Name)
            {
                case "OrderedDict":
                {
                    var dict = new Dictionary<object, object?>();
                    if (args.Length > 0 && args[0] is List<object?> pairs)
                        foreach (var p in pairs) if (p is object?[] kv && kv.Length == 2) dict[kv[0]!] = kv[1];
                    return dict;
                }
                case "_rebuild_tensor_v2":
                {
                    if (args.Length < 4 || args[0] is not StorageRef st) throw new InvalidDataException("pickle: malformed tensor");
                    var shape = ((object?[])args[2]!).Select(x => (long)x!).ToArray();
                    var stride = ((object?[])args[3]!).Select(x => (long)x!).ToArray();
                    return new TorchTensor(st.Key, st.Dtype, (long)args[1]!, shape, stride);
                }
                case "_rebuild_parameter":
                    return args.Length > 0 ? args[0] : null;
                default:
                    throw new NotSupportedException($"pickle: cannot call {g.Module}.{g.Name}");
            }
        }

        private static void Build(object? target, object? state)
        {
            // OrderedDict pickles carry a (usually empty) state; merging a
            // dict state keeps any entries it brings.
            if (target is Dictionary<object, object?> d && state is Dictionary<object, object?> s)
                foreach (var (k, v) in s) d[k] = v;
        }

        private static StorageRef Persistent(object? pid)
        {
            if (pid is object?[] t && t.Length >= 5 && t[0] is "storage" && t[1] is Global g && t[2] is string key && t[4] is long count)
                return new StorageRef(DtypeOf(g.Name), key, count);
            throw new NotSupportedException("pickle: unknown persistent id");
        }

        private static TorchDtype DtypeOf(string storageClass) => storageClass switch
        {
            "FloatStorage" => TorchDtype.Float32,
            "HalfStorage" => TorchDtype.Float16,
            "BFloat16Storage" => TorchDtype.BFloat16,
            "DoubleStorage" => TorchDtype.Float64,
            "LongStorage" => TorchDtype.Int64,
            "IntStorage" => TorchDtype.Int32,
            "ShortStorage" => TorchDtype.Int16,
            "CharStorage" => TorchDtype.Int8,
            "ByteStorage" => TorchDtype.UInt8,
            "BoolStorage" => TorchDtype.Bool,
            _ => throw new NotSupportedException($"unsupported tensor storage {storageClass}"),
        };
    }
}
