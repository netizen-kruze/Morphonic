using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace Morphonic.Rvc;

// The offline build: the catalog's model files appended to the single
// binary, behind a small index and a trailer. Nothing is read until a
// model is asked for, so the ordinary build and the offline build start
// the same way; the Models screen then takes a model out of the binary
// instead of downloading it, verifying the pinned hash either way.
//
//   [ordinary single-file binary][file 1][file 2]...[index JSON][index length: 8 bytes LE][magic]
public static class OfflinePayload
{
    public const string Magic = "MORPHONIC-OFFLINE-1";
    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes(Magic);

    public sealed class Entry
    {
        public string Id { get; set; } = "";
        public string FileName { get; set; } = "";
        public long Offset { get; set; }
        public long Length { get; set; }
        public string Sha256 { get; set; } = "";
    }

    private static readonly Lazy<Dictionary<string, Entry>?> Own = new(() => Read(Environment.ProcessPath ?? ""));

    public static bool Has(string modelId) => Own.Value?.ContainsKey(modelId) == true;
    public static IReadOnlyCollection<string> Ids => Own.Value?.Keys.ToArray() ?? Array.Empty<string>();

    // The index of the payload carried by `path`, or null when it carries none.
    public static Dictionary<string, Entry>? Read(string path)
    {
        try
        {
            if (path.Length == 0 || !File.Exists(path)) return null;
            using var f = File.OpenRead(path);
            int tail = MagicBytes.Length + 8;
            if (f.Length < tail + 2) return null;
            var buf = new byte[tail];
            f.Seek(-tail, SeekOrigin.End);
            f.ReadExactly(buf);
            if (!buf.AsSpan(8).SequenceEqual(MagicBytes)) return null;
            long indexLen = BitConverter.ToInt64(buf, 0);
            if (indexLen <= 0 || indexLen > 1 << 20 || indexLen + tail > f.Length) return null;
            var json = new byte[indexLen];
            f.Seek(-(tail + indexLen), SeekOrigin.End);
            f.ReadExactly(json);
            var entries = JsonConvert.DeserializeObject<List<Entry>>(Encoding.UTF8.GetString(json)) ?? new List<Entry>();
            var dict = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var e in entries)
                if (e.Offset >= 0 && e.Length > 0 && e.Offset + e.Length <= f.Length - tail - indexLen) dict[e.Id] = e;
            return dict;
        }
        catch { return null; }
    }

    // Where the payload begins in `path` (the length of the plain binary), or the file length without one.
    public static long BaseLength(string path)
    {
        var index = Read(path);
        if (index == null || index.Count == 0) return new FileInfo(path).Length;
        return index.Values.Min(e => e.Offset);
    }

    // Copies one model out of this binary to `dest`; returns its size and SHA-256.
    public static (long Size, string Sha256) ExtractTo(string modelId, string dest, Action<long>? progress = null, CancellationToken ct = default)
    {
        var index = Own.Value ?? throw new InvalidOperationException("this build carries no models");
        if (!index.TryGetValue(modelId, out var e)) throw new InvalidOperationException($"this build does not carry {modelId}");
        using var src = File.OpenRead(Environment.ProcessPath!);
        src.Seek(e.Offset, SeekOrigin.Begin);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var buf = new byte[1 << 20];
        long left = e.Length, done = 0;
        while (left > 0)
        {
            ct.ThrowIfCancellationRequested();
            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
            if (n <= 0) throw new EndOfStreamException("the binary is shorter than its index says");
            dst.Write(buf, 0, n);
            sha.AppendData(buf, 0, n);
            left -= n;
            done += n;
            progress?.Invoke(done);
        }
        return (done, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }

    // Writes `outPath` = the plain binary at `basePath` (any payload it
    // already carries is dropped) followed by the given files.
    public static void Pack(string basePath, IEnumerable<(string Id, string FileName, string SourcePath, string Sha256)> files, string outPath)
    {
        long baseLen = BaseLength(basePath);
        var entries = new List<Entry>();
        using (var outFile = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            using (var b = File.OpenRead(basePath)) CopyLength(b, outFile, baseLen);
            foreach (var (id, fileName, sourcePath, sha256) in files)
            {
                long offset = outFile.Position;
                using var s = File.OpenRead(sourcePath);
                s.CopyTo(outFile);
                entries.Add(new Entry { Id = id, FileName = fileName, Offset = offset, Length = outFile.Position - offset, Sha256 = sha256 });
            }
            var json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(entries));
            outFile.Write(json);
            outFile.Write(BitConverter.GetBytes((long)json.Length));
            outFile.Write(MagicBytes);
        }
    }

    // The catalog's models from `modelsDir` (components) and its voices
    // subfolder or the same folder (the sample voice), each checked
    // against its pinned size and hash before it is packed.
    public static string PackCatalog(string basePath, string modelsDir, string outPath)
    {
        var files = new List<(string, string, string, string)>();
        var report = new StringBuilder();
        foreach (var m in ModelCatalog.Models)
        {
            var candidates = new[] { Path.Combine(modelsDir, m.File.FileName), Path.Combine(modelsDir, "..", "voices", m.File.FileName), Path.Combine(modelsDir, "voices", m.File.FileName) };
            var path = candidates.FirstOrDefault(File.Exists)
                       ?? throw new FileNotFoundException($"{m.File.FileName} not found in {modelsDir} (run `--fetch-models --data-dir <folder>` first and point at its models folder)");
            if (new FileInfo(path).Length != m.File.SizeBytes || Download.Sha256Of(path) != m.File.Sha256)
                throw new InvalidDataException($"{path} does not match the catalog's pinned size and SHA-256; it must be exactly the file the app would download");
            files.Add((m.Id, m.File.FileName, path, m.File.Sha256));
            report.AppendLine($"  {m.File.FileName}: {m.File.SizeBytes / 1_000_000} MB, verified");
        }
        Pack(basePath, files, outPath);
        return report.ToString().TrimEnd();
    }

    private static void CopyLength(Stream src, Stream dst, long length)
    {
        var buf = new byte[1 << 20];
        while (length > 0)
        {
            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, length));
            if (n <= 0) throw new EndOfStreamException();
            dst.Write(buf, 0, n);
            length -= n;
        }
    }
}
