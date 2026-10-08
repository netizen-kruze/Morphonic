using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Morphonic.Rvc;

public sealed record HubRepo(string Id, int Likes, int Downloads, string Updated);
public sealed record HubFile(string Path, long SizeBytes, string? Sha256);

// The Voices screen's "Find voices": Hugging Face's public model API,
// used only when the user presses Search, lists a repository's files
// (.pth, .onnx, .zip) and downloads one into the voices folder. Every
// LFS file on the Hub carries its SHA-256, so a download is verified
// exactly like a catalog file; a file without one is checked by size.
public static class VoiceHub
{
    private const string Api = "https://huggingface.co/api/models";
    private static readonly Regex RepoId = new(@"^[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+$", RegexOptions.Compiled);
    private static readonly string[] VoiceExt = { ".pth", ".onnx", ".zip" };

    public static async Task<List<HubRepo>> SearchAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length == 0) return new List<HubRepo>();
        var url = Api + "?search=" + Uri.EscapeDataString(query) + "&limit=40&sort=downloads&direction=-1";
        var json = await GetAsync(url, ct);
        var list = new List<HubRepo>();
        foreach (var m in JArray.Parse(json))
        {
            var id = m["id"]?.ToString() ?? m["modelId"]?.ToString() ?? "";
            if (!RepoId.IsMatch(id)) continue;
            list.Add(new HubRepo(id, m["likes"]?.Value<int>() ?? 0, m["downloads"]?.Value<int>() ?? 0,
                (m["lastModified"]?.ToString() ?? "").Split('T')[0]));
        }
        return list;
    }

    public static async Task<List<HubFile>> ListFilesAsync(string repo, CancellationToken ct)
    {
        if (!RepoId.IsMatch(repo)) throw new ArgumentException("not a repository id");
        var json = await GetAsync($"{Api}/{repo}/tree/main?recursive=true", ct);
        var files = new List<HubFile>();
        foreach (var e in JArray.Parse(json))
        {
            if (e["type"]?.ToString() != "file") continue;
            var path = e["path"]?.ToString() ?? "";
            if (!VoiceExt.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant())) continue;
            if (path.Contains("..", StringComparison.Ordinal) || path.StartsWith('/')) continue;
            long size = e["size"]?.Value<long>() ?? 0;
            var sha = e["lfs"]?["oid"]?.ToString();
            files.Add(new HubFile(path, size, sha != null && sha.Length == 64 ? sha : null));
        }
        return files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string DownloadUrl(string repo, string path)
    {
        if (!RepoId.IsMatch(repo)) throw new ArgumentException("not a repository id");
        return $"https://huggingface.co/{repo}/resolve/main/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";
    }

    // Downloads repo/path into the voices folder (a .zip is unpacked),
    // verifying the size and, when known, the SHA-256. Returns the
    // library's import message.
    // The file lands under VoiceLibrary.LibraryName(repo, path): a
    // "model.pth" takes the repository's name. Name is that library name.
    public static async Task<(bool Ok, string Message, string Name)> DownloadAsync(string repo, string path, long size, string? sha256,
        Action<long, long> progress, CancellationToken ct)
    {
        var name = VoiceLibrary.LibraryName(repo, path);
        var (ok, err) = VoiceLibrary.BeginImport(name, size);
        if (!ok) return (false, err ?? "cannot start the download", name);
        var partial = VoiceLibrary.ImportingPath(name);
        try
        {
            using var sha = SHA256.Create();
            long got = await Download.ResumableDownloadAsync(DownloadUrl(repo, path), partial, size, sha, n => progress(n, size), ct);
            if (size > 0 && got != size) { VoiceLibrary.AbortImport(name); return (false, $"{name}: size mismatch ({got} vs {size} bytes)", name); }
            var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (sha256 != null && hash != sha256) { VoiceLibrary.AbortImport(name); return (false, $"{name}: SHA-256 mismatch — the download is corrupt", name); }
            var (done, message) = VoiceLibrary.EndImport(name);
            return (done, message, name);
        }
        catch (OperationCanceledException)
        {
            VoiceLibrary.AbortImport(name);
            return (false, "download cancelled", name);
        }
        catch (Exception ex)
        {
            VoiceLibrary.AbortImport(name);
            return (false, $"{name}: download failed — {ex.Message}", name);
        }
    }

    private static async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await Download.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"huggingface.co answered {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync(timeout.Token);
    }
}
