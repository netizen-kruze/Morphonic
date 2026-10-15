using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Morphonic;

// Settings > Updates > "Update now": fetches this platform's file from the
// release the check found, checks it against that release's SHA256SUMS.txt,
// and puts it in place of the running binary. The caller then restarts.
//
// The running binary is never opened for writing: on Windows it is renamed
// aside (Windows allows renaming a running exe) and the new one moved into
// its name; on Linux the new file is renamed over it. Settings, voices and
// models live in the data folder and are not touched.
public static class Updater
{
    public sealed record Installed(string ExePath, string From, string To);

    // Left beside the binary by a Windows update; removed at the next start.
    public static string OldPath(string exe) => exe + ".old";

    public static async Task<Installed> InstallAsync(UpdateCheck.Result r, Action<long, long> progress, CancellationToken ct)
    {
        if (!r.Available) throw new InvalidOperationException("this is already the latest version");
        if (r.AssetUrl.Length == 0 || r.AssetName.Length == 0)
            throw new InvalidOperationException("the release has no file for this platform");
        if (r.SumsUrl.Length == 0) throw new InvalidOperationException("the release has no SHA256SUMS.txt to check the file against");

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) throw new InvalidOperationException("cannot find the running program file");
        exe = Path.GetFullPath(exe);
        var dir = Path.GetDirectoryName(exe)!;
        var part = Path.Combine(dir, "." + r.AssetName + ".part");
        var fresh = Path.Combine(dir, "." + Path.GetFileName(exe) + ".new");
        try { using (File.Create(part + ".probe")) { } File.Delete(part + ".probe"); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new IOException($"Morphonic cannot write to its own folder ({dir}): {ex.Message}");
        }

        // The expected hash comes from the same release, over HTTPS.
        string sums;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            sums = await Rvc.Download.Http.GetStringAsync(r.SumsUrl, timeout.Token).ConfigureAwait(false);
        }
        var expected = ExpectedHash(sums, r.AssetName)
            ?? throw new InvalidDataException($"SHA256SUMS.txt does not list {r.AssetName}");

        Rvc.Download.EnsureFreeSpace(dir, Math.Max(r.AssetSize, 1) * 3);
        long got;
        using (var sha = SHA256.Create())
        {
            got = await Rvc.Download.ResumableDownloadAsync(r.AssetUrl, part, r.AssetSize > 0 ? r.AssetSize : long.MaxValue, sha,
                n => progress(n, r.AssetSize), ct).ConfigureAwait(false);
            var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (hash != expected)
            {
                TryDelete(part);
                throw new InvalidDataException($"the downloaded file does not match the release's checksum (got {hash[..12]}…, expected {expected[..12]}…); nothing was changed");
            }
        }
        if (r.AssetSize > 0 && got != r.AssetSize) { TryDelete(part); throw new InvalidDataException("the download is incomplete; nothing was changed"); }

        try
        {
            TryDelete(fresh);
            if (r.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(part);
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("Morphonic.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"{r.AssetName} holds no Morphonic.exe");
                entry.ExtractToFile(fresh, overwrite: true);
            }
            else File.Move(part, fresh, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(fresh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Swap(exe, fresh);
        }
        finally
        {
            TryDelete(part);
            TryDelete(fresh);
        }
        return new Installed(exe, r.Current, r.Latest);
    }

    // Puts `fresh` at `exe`. Either the new file is in place, or the old one
    // still is (and the error says why).
    public static void Swap(string exe, string fresh)
    {
        if (OperatingSystem.IsWindows())
        {
            var old = OldPath(exe);
            TryDelete(old);
            File.Move(exe, old);
            try { File.Move(fresh, exe); }
            catch
            {
                try { File.Move(old, exe); } catch { }
                throw;
            }
        }
        else File.Move(fresh, exe, overwrite: true);
    }

    // "<hash>  <name>" or "<hash> *<name>" per line, as sha256sum writes.
    public static string? ExpectedHash(string sums, string name)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            int sp = line.IndexOf(' ');
            if (sp != 64) continue;
            var file = line[sp..].Trim().TrimStart('*');
            if (!file.Equals(name, StringComparison.Ordinal)) continue;
            var hash = line[..64].ToLowerInvariant();
            return hash.All(Uri.IsHexDigit) ? hash : null;
        }
        return null;
    }

    // At start-up: the Windows leftover of the previous version, and an
    // interrupted download's pieces.
    public static void CleanUp()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            TryDelete(OldPath(exe));
            TryDelete(Path.Combine(Path.GetDirectoryName(exe)!, "." + Path.GetFileName(exe) + ".new"));
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
