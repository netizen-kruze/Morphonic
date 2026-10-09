using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Morphonic.Rvc;

// A voice as the Voices screen lists it: an .onnx ready to run, or a .pth
// waiting to be converted with the bundled tool.
public sealed record VoiceEntry(string Id, string Name, string Kind, long SizeBytes, int SampleRate, int Speakers, bool SkipHead, string Source);

// The voices folder: what is in it, importing files into it, deleting,
// and converting RVC .pth checkpoints to ONNX by running
// tools/morphonic_export.py with the user's Python (the one thing in Morphonic
// that needs Python — the app itself never does).
public static class VoiceLibrary
{
    public static string Dir => AppPaths.VoiceDir;

    public static List<VoiceEntry> Scan()
    {
        var list = new List<VoiceEntry>();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            foreach (var file in Directory.EnumerateFiles(Dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var id = Path.GetFileName(file);
                var stem = Path.GetFileNameWithoutExtension(file);
                long size = new FileInfo(file).Length;
                if (ext == ".onnx")
                {
                    var m = VoiceModel.ReadMetadata(file);
                    list.Add(new VoiceEntry(id, m.Name.Length > 0 ? m.Name : stem, "onnx", size, m.SampleRate, m.Speakers, m.SkipHead, m.Source));
                }
                else if (ext == ".pth")
                {
                    bool converted = File.Exists(Path.Combine(Dir, stem + ".onnx"));
                    if (!converted) list.Add(new VoiceEntry(id, stem, "pth", size, 0, 0, false, ""));
                }
            }
        }
        catch (Exception ex) { ErrorLog.WriteEntry("VoiceLibrary.Scan", ex); }
        return list;
    }

    public static string PathFor(string id) => Path.Combine(Dir, Path.GetFileName(id));

    public static bool Exists(string id) => id.Length > 0 && File.Exists(PathFor(id));

    // Voice libraries name nearly every checkpoint "model.pth" or
    // "G_2333.pth"; a voice named "model" tells the user nothing and the
    // next download would replace it. A generic file name is replaced by
    // the name of where it came from (the Hugging Face repository, or the
    // zip it was inside), plus the sub-folder when the origin holds several.
    private static readonly System.Text.RegularExpressions.Regex GenericStem =
        new(@"^(model|pytorch_model|weights?|checkpoint|final|best|voice|rvc|output|generator|net_g|[gd](_?\d+)?|g_v\d+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsGenericName(string fileName) =>
        GenericStem.IsMatch(Path.GetFileNameWithoutExtension(fileName).Trim());

    // origin: "owner/Repo_Name" (Hub) or "Bundle Name" (a zip's stem);
    // path: the file's path inside it. Returns the library file name.
    public static string LibraryName(string origin, string path)
    {
        var fileName = Path.GetFileName(path.Replace('\\', '/'));
        var ext = Path.GetExtension(fileName);
        var own = Sanitize(Path.GetFileNameWithoutExtension(fileName));
        if (!IsGenericName(fileName)) return own + ext;
        var from = origin.Contains('/') ? origin[(origin.LastIndexOf('/') + 1)..] : origin;
        var folder = path.Replace('\\', '/').Contains('/') ? Path.GetFileName(Path.GetDirectoryName(path.Replace('\\', '/'))!) : "";
        var stem = Sanitize(from);
        if (folder.Length > 0 && !IsGenericName(folder + ".x")) stem = Sanitize(from + " " + folder);
        // No usable origin (a file picked straight out of Downloads): the
        // file's own name, generic as it is, beats inventing one.
        if (stem.Length == 0) stem = own.Length > 0 ? own : "voice";
        return stem + ext;
    }

    // The folders a picked file's parent says nothing about: the home
    // folder itself, the usual landing places (by their English names and
    // by the desktop's own configuration — localized "Téléchargements",
    // "Descargas"), the voices folder, and mount points (a USB stick's
    // root). A generic checkpoint from ~/Downloads must not become a voice
    // called "Downloads".
    private static readonly string[] UserFolderNames =
        { "downloads", "download", "desktop", "documents", "music", "videos", "pictures", "public", "tmp", "temp", "voices" };

    internal static bool IsUserFolder(string dir) => IsUserFolder(dir, KnownUserFolders.Value);

    internal static bool IsUserFolder(string dir, IReadOnlyCollection<string> knownFolders)
    {
        if (string.IsNullOrEmpty(dir)) return true;
        try
        {
            var full = Normalize(dir);
            var name = Path.GetFileName(full);
            if (name.Length == 0) return true;   // a drive or filesystem root
            if (UserFolderNames.Contains(name.ToLowerInvariant())) return true;
            if (knownFolders.Any(k => string.Equals(full, k, StringComparison.OrdinalIgnoreCase))) return true;
            if (string.Equals(full, Normalize(Dir), StringComparison.OrdinalIgnoreCase)) return true;
            // Mount points: /media/<x>, /media/<user>/<x>, /run/media/<user>/<x>, /mnt/<x>.
            var parent = Path.GetDirectoryName(full) ?? "";
            var grand = Path.GetDirectoryName(parent) ?? "";
            if (parent is "/media" or "/mnt" || grand is "/media" or "/run/media") return true;
        }
        catch { }
        return false;
    }

    private static string Normalize(string p) =>
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // The user's own folders as the platform knows them: the profile, the
    // special folders, and on Linux the XDG user-dirs file (localized names).
    private static readonly Lazy<string[]> KnownUserFolders = new(() => ReadKnownUserFolders().ToArray());

    private static IEnumerable<string> ReadKnownUserFolders()
    {
        var list = new List<string>();
        void Add(string? p) { if (!string.IsNullOrEmpty(p)) { try { list.Add(Normalize(p)); } catch { } } }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(home);
        foreach (var f in new[] { Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
                                  Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos })
        {
            try { Add(Environment.GetFolderPath(f)); } catch { }
        }
        if (home.Length > 0) Add(Path.Combine(home, "Downloads"));
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var file = Path.Combine(LinuxHost.ConfigHome, "user-dirs.dirs");
                if (File.Exists(file))
                    foreach (var p in ParseXdgUserDirs(File.ReadAllLines(file), home)) Add(p);
            }
            catch { }
        }
        return list;
    }

    // XDG_DOWNLOAD_DIR="$HOME/Téléchargements" -> /home/u/Téléchargements
    internal static IEnumerable<string> ParseXdgUserDirs(IEnumerable<string> lines, string home)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0 || !line.StartsWith("XDG_", StringComparison.Ordinal) || !line[..eq].EndsWith("_DIR", StringComparison.Ordinal)) continue;
            var value = line[(eq + 1)..].Trim().Trim('"');
            if (value.StartsWith("$HOME/", StringComparison.Ordinal)) value = Path.Combine(home, value[6..]);
            else if (value == "$HOME") value = home;
            if (value.Length > 0 && value.StartsWith('/')) yield return value.TrimEnd('/');
        }
    }

    // The characters no file system of either desktop takes (Windows' set,
    // which covers Linux's), always — the voices folder may be synced or
    // copied to the other platform, so a name must work on both.
    private static readonly char[] UnsafeNameChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    private static string Sanitize(string s)
    {
        var chars = s.Select(c => c < ' ' || UnsafeNameChars.Contains(c) ? ' ' : c == '_' ? ' ' : c).ToArray();
        var collapsed = System.Text.RegularExpressions.Regex.Replace(new string(chars), @"\s+", " ").Trim(' ', '.');
        return collapsed.Length > 80 ? collapsed[..80].TrimEnd() : collapsed;
    }

    // Copies a file the user picked into the library. The .index files
    // that come with RVC voices are not used (feature retrieval is not
    // part of this pipeline), so they are not copied.
    public static (bool Ok, string Message) Import(string sourcePath) => Import(sourcePath, out _);

    // `added`: the library id of the voice that landed (for a zip, the
    // checkpoint or export it held — an .onnx first), null when none did.
    public static (bool Ok, string Message) Import(string sourcePath, out string? added)
    {
        added = null;
        try
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext == ".zip")
            {
                var r = ImportZip(sourcePath, out var fromZip);
                added = PickAdopted(fromZip);
                return r;
            }
            if (ext is not (".onnx" or ".pth"))
                return (false, "pick an RVC voice: a .pth checkpoint (converted here), an .onnx export, or the .zip a voice library gave you");
            Directory.CreateDirectory(Dir);
            // "…/SpongeBob/model.pth" picked from disk becomes "SpongeBob.pth";
            // "~/Downloads/model.pth" keeps its own name.
            var parentDir = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? "";
            var parent = IsUserFolder(parentDir) ? "" : Path.GetFileName(parentDir);
            var dest = Path.Combine(Dir, LibraryName(parent, Path.GetFileName(sourcePath)));
            if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            {
                // Picked out of the library itself: an export is simply the
                // voice to use; a checkpoint whose conversion exists means
                // that conversion; a bare checkpoint is nothing new.
                var converted = Path.Combine(Dir, Path.GetFileNameWithoutExtension(dest) + ".onnx");
                added = ext == ".onnx" ? Path.GetFileName(dest) : File.Exists(converted) ? Path.GetFileName(converted) : null;
                return (true, "that file is already in the voices folder");
            }
            var tmp = dest + ".importing";
            File.Copy(sourcePath, tmp, overwrite: true);
            File.Move(tmp, dest, overwrite: true);
            added = Path.GetFileName(dest);
            return (true, ext == ".pth"
                ? $"{Path.GetFileName(dest)} added — converting to an ONNX voice now"
                : $"{Path.GetFileName(dest)} added");
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("VoiceLibrary.Import", ex);
            return (false, "import failed: " + ex.Message);
        }
    }

    // The voice to make active out of what a zip held: an export runs as
    // it is, a checkpoint after conversion; null for nothing.
    internal static string? PickAdopted(IReadOnlyList<string> added) =>
        added.FirstOrDefault(n => n.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) ?? added.FirstOrDefault();

    public static (bool Ok, string Message) ImportZip(string zipPath) => ImportZip(zipPath, out _);

    // Voice libraries hand out zips holding the .pth (and an .index the
    // app does not use, plus sometimes a readme). Only the voice files
    // are taken, by their own names: no folders from the archive.
    // `added` lists the library ids that landed.
    public static (bool Ok, string Message) ImportZip(string zipPath, out List<string> added)
    {
        added = new List<string>();
        try
        {
            Directory.CreateDirectory(Dir);
            // "Voice.zip" holding "model.pth" gives a voice called "Voice".
            var bundle = Path.GetFileName(zipPath);
            if (bundle.EndsWith(".importing", StringComparison.OrdinalIgnoreCase)) bundle = bundle[..^".importing".Length];
            bundle = Path.GetFileNameWithoutExtension(bundle);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (entry.Name.Length == 0 || ext is not (".pth" or ".onnx")) continue;
                    if (entry.Length > 4L << 30) return (false, $"{entry.Name} in the zip is too large");
                    var name = LibraryName(bundle, entry.FullName);
                    var dest = Path.Combine(Dir, name);
                    var tmp = dest + ".importing";
                    entry.ExtractToFile(tmp, overwrite: true);
                    File.Move(tmp, dest, overwrite: true);
                    added.Add(name);
                }
            }
            if (added.Count == 0)
                return (false, $"{Path.GetFileName(zipPath)} holds no .pth or .onnx voice (an .index file alone is not a voice)");
            bool needsConvert = added.Any(n => n.EndsWith(".pth", StringComparison.OrdinalIgnoreCase));
            return (true, string.Join(", ", added) + (needsConvert ? " added — converting to an ONNX voice now" : " added"));
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("VoiceLibrary.ImportZip", ex);
            return (false, "could not read the zip: " + ex.Message);
        }
    }

    // ── streamed import (drag and drop, Find voices) ─────────────────
    // The page cannot hand the app a path for a dropped file, so it sends
    // the bytes in pieces; the Hub download writes the same way. The file
    // grows as <name>.importing and is taken into the library by EndImport.

    public static string ImportingPath(string name) => Path.Combine(Dir, Path.GetFileName(name) + ".importing");

    public static (bool Ok, string? Error) BeginImport(string name, long size)
    {
        name = Path.GetFileName(name);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (name.Length == 0 || ext is not (".pth" or ".onnx" or ".zip"))
            return (false, $"{name}: not a voice file (.pth, .onnx or .zip)");
        if (size < 0 || size > 4L << 30) return (false, $"{name}: unexpected size");
        try
        {
            Directory.CreateDirectory(Dir);
            if (size > 0) Download.EnsureFreeSpace(Dir, size);
            // A Hub download resumes its .importing; a dropped file starts over.
            if (!File.Exists(ImportingPath(name))) File.WriteAllBytes(ImportingPath(name), Array.Empty<byte>());
            return (true, null);
        }
        catch (Exception ex) { return (false, $"{name}: {ex.Message}"); }
    }

    public static (bool Ok, string? Error) AppendImport(string name, byte[] bytes)
    {
        try
        {
            using var f = new FileStream(ImportingPath(name), FileMode.Append, FileAccess.Write, FileShare.None);
            f.Write(bytes, 0, bytes.Length);
            return (true, null);
        }
        catch (Exception ex) { return (false, $"{Path.GetFileName(name)}: write failed — {ex.Message}"); }
    }

    public static long ImportedBytes(string name)
    {
        try { return new FileInfo(ImportingPath(name)).Length; } catch { return 0; }
    }

    public static void AbortImport(string name)
    {
        try { File.Delete(ImportingPath(name)); } catch { }
    }

    public static (bool Ok, string Message) EndImport(string name) => EndImport(name, out _);

    // `added`: the library id of the voice that landed (a zip's content,
    // not the zip), null when the import failed.
    public static (bool Ok, string Message) EndImport(string name, out string? added)
    {
        added = null;
        name = Path.GetFileName(name);
        var tmp = ImportingPath(name);
        if (!File.Exists(tmp)) return (false, $"{name}: nothing was received");
        try
        {
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var r = ImportZip(tmp, out var fromZip);
                File.Delete(tmp);
                if (r.Ok) added = PickAdopted(fromZip);
                return r;
            }
            var dest = Path.Combine(Dir, name);
            File.Move(tmp, dest, overwrite: true);
            added = name;
            return (true, name.EndsWith(".pth", StringComparison.OrdinalIgnoreCase)
                ? $"{name} added — converting to an ONNX voice now"
                : $"{name} added");
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("VoiceLibrary.EndImport", ex);
            AbortImport(name);
            return (false, $"{name}: import failed — {ex.Message}");
        }
    }

    public static bool Delete(string id)
    {
        try
        {
            var path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) { ErrorLog.WriteEntry("VoiceLibrary.Delete", ex); return false; }
    }

    // ── conversion (.pth -> .onnx) ──────────────────────────────────

    // The converter and its vendored network code ship inside the binary
    // and are unpacked next to the data (tools/), so the app never depends
    // on a source checkout.
    public static string ToolsDir => Path.Combine(AppPaths.DataDir, "tools");
    public static string ExportScript => Path.Combine(ToolsDir, "morphonic_export.py");

    public static void UnpackTools()
    {
        try
        {
            var asm = typeof(VoiceLibrary).Assembly;
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith("tools/", StringComparison.Ordinal)) continue;
                var dest = Path.Combine(AppPaths.DataDir, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var src = asm.GetManifestResourceStream(name)!;
                using var dst = File.Create(dest);
                src.CopyTo(dst);
            }
        }
        catch (Exception ex) { ErrorLog.WriteEntry("VoiceLibrary.UnpackTools", ex); }
    }

    // MORPHONIC_PYTHON, then the usual names; "py -3" on Windows. Probing runs
    // a process per candidate, so the answer is kept for the whole run.
    private static readonly Lazy<(string File, string[] Prefix)?> PythonFound = new(ProbePython);
    public static (string File, string[] Prefix)? FindPython() => PythonFound.Value;

    private static (string File, string[] Prefix)? ProbePython()
    {
        var env = Environment.GetEnvironmentVariable("MORPHONIC_PYTHON");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return (env, Array.Empty<string>());
        var candidates = OperatingSystem.IsWindows()
            ? new (string, string[])[] { ("py", new[] { "-3" }), ("python", Array.Empty<string>()), ("python3", Array.Empty<string>()) }
            : new (string, string[])[] { ("python3", Array.Empty<string>()), ("python", Array.Empty<string>()) };
        foreach (var (file, prefix) in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var p in prefix) psi.ArgumentList.Add(p);
                psi.ArgumentList.Add("--version");
                using var proc = Process.Start(psi);
                if (proc == null) continue;
                var line = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                proc.WaitForExit(5000);
                if (proc.ExitCode == 0 && line.Contains("Python 3", StringComparison.Ordinal)) return (file, prefix);
            }
            catch { }
        }
        return null;
    }

    // ── in-app conversion ────────────────────────────────────────────
    // A standard RVC v2 voice becomes ONNX right here, in seconds: its
    // tensors are placed into the embedded graph for its sample rate
    // (Assets/rvc-v2-*.template, the mechanism that builds the sample
    // voice). Only an unusual checkpoint falls back to the Python tool.
    private static readonly Dictionary<int, (string Rates, string Kernels)> StandardV2 = new()
    {
        [32000] = ("[10,8,2,2]", "[20,16,4,4]"),
        [40000] = ("[10,10,2,2]", "[16,16,4,4]"),
        [48000] = ("[12,10,2,2]", "[24,20,4,4]"),
    };

    internal static int SampleRateOf(object? v) => v switch
    {
        long l when l is 32000 or 40000 or 48000 => (int)l,
        string s when s.EndsWith("k", StringComparison.OrdinalIgnoreCase) && int.TryParse(s[..^1], out var k) && StandardV2.ContainsKey(k * 1000) => k * 1000,
        _ => 0,
    };

    // The graph depends on these entries of RVC's config list (the rest
    // only matter for training); a voice trained with other values gets
    // the Python converter, which builds any graph.
    internal static bool IsStandardV2(List<object?> config, int sr)
    {
        if (config.Count < 18 || !StandardV2.TryGetValue(sr, out var std)) return false;
        string J(int i) => JsonConvert.SerializeObject(config[i]);
        return J(2) == "192" && J(3) == "192" && J(4) == "768" && J(5) == "2" && J(6) == "6" && J(7) == "3"
            && J(9) == "\"1\"" && J(10) == "[3,7,11]" && J(11) == "[[1,3,5],[1,3,5],[1,3,5]]"
            && J(12) == std.Rates && J(13) == "512" && J(14) == std.Kernels && J(16) == "256";
    }

    // Handled = the checkpoint was understood (converted, or refused for a
    // reason the user should see); not handled = let the Python tool try.
    internal static (bool Ok, string Message, bool Handled) ConvertNative(string source, string dest, string name)
    {
        TorchCheckpoint ck;
        try { ck = TorchCheckpoint.Open(source); }
        catch (Exception ex) { ErrorLog.WriteNote("VoiceLibrary.ConvertNative", $"{Path.GetFileName(source)}: {ex.Message}"); return (false, "", false); }
        using (ck)
        {
            if (ck.Value("config") is not List<object?> config || ck.Value("weight") is not Dictionary<object, object?>)
            {
                // RVC's training checkpoints (G_2333.pth, f0G40k.pth) hold the
                // generator under "model" with the optimizer state — not an
                // exported voice. Say so instead of handing it to Python.
                if (ck.Value("model") is Dictionary<object, object?> && (ck.Value("iteration") != null || ck.Value("optimizer") != null || ck.Value("learning_rate") != null))
                    return (false, $"conversion failed: {Path.GetFileName(source)} is a raw training generator (the G_*.pth / f0G*.pth that training writes), not an exported voice — " +
                                   "in RVC WebUI or Applio use \"Export\" / \"Extract small model\" to get the voice checkpoint, then import that", true);
                return (false, "", false);   // another layout: the Python tool may know it
            }
            var version = ck.Value("version") as string ?? "v1";
            if (version != "v2")
                return (false, $"conversion failed: only RVC v2 voices (768-dim ContentVec) are supported; this is {version}", true);
            if (ck.Value("f0") is long f0 && f0 != 1)
                return (false, "conversion failed: only pitch-guided voices (f0 = 1) are supported; this one was trained without pitch", true);
            int sr = SampleRateOf(config[^1]);
            if (sr == 0 || !IsStandardV2(config, sr)) return (false, "", false);
            var state = ck.StateDict("weight");
            long speakers = state.TryGetValue("emb_g.weight", out var emb) ? emb.Shape[0] : 1;
            var meta = JsonConvert.SerializeObject(new
            {
                format = "morphonic-rvc-voice", formatVersion = 1, rvcVersion = "v2", sr, hop = sr / 100, f0 = true,
                embChannels = 768, speakers, skipHead = true, name, source = Path.GetFileName(source), exporter = "in-app",
            });
            var tmp = dest + ".converting";
            try
            {
                var (size, _) = ModelAssembler.Assemble($"rvc-v2-{sr / 1000}k", source, tmp, metadataJson: meta);
                File.Move(tmp, dest, overwrite: true);
                BootLog.Append($"converted {Path.GetFileName(source)} in-app ({sr} Hz, {speakers} speaker(s), {size / 1_000_000} MB)");
                return (true, $"{Path.GetFileNameWithoutExtension(dest)}.onnx is ready ({size / 1_000_000} MB) — press Use to make it the active voice", true);
            }
            catch (Exception ex)
            {
                ErrorLog.WriteEntry("VoiceLibrary.ConvertNative", ex);
                try { File.Delete(tmp); } catch { }
                return (false, "", false);   // the Python tool gets a turn
            }
        }
    }

    public static Task<(bool Ok, string Message)> ConvertAsync(string pthId, Action<string>? progress) => Task.Run(() =>
    {
        var source = PathFor(pthId);
        if (!File.Exists(source)) return (false, "that file is gone");
        var stem = Path.GetFileNameWithoutExtension(source);
        var dest = Path.Combine(Dir, stem + ".onnx");
        var native = ConvertNative(source, dest, stem);
        if (native.Handled) return (native.Ok, native.Message);

        if (!File.Exists(ExportScript)) UnpackTools();
        var python = FindPython();
        if (python == null)
            return (false, "this checkpoint is not a standard RVC v2 voice, and the fallback converter needs Python 3 with PyTorch. Install it (python.org, or `sudo dnf install python3`), " +
                           "then `pip install -r " + Path.Combine(ToolsDir, "requirements-export.txt") + "` and try again");
        var tmp = Path.Combine(Dir, stem + ".converting.onnx");
        try
        {
            var psi = new ProcessStartInfo(python.Value.File)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                WorkingDirectory = ToolsDir,
            };
            foreach (var p in python.Value.Prefix) psi.ArgumentList.Add(p);
            // Not "-I": isolated mode also drops the user site-packages, the
            // very place "pip install --user" (Install fallback converter)
            // puts PyTorch. The script and its working folder are the app's
            // own tools folder; PYTHON* variables are ignored (-E).
            psi.ArgumentList.Add("-E");
            psi.ArgumentList.Add(ExportScript);
            psi.ArgumentList.Add("voice");
            psi.ArgumentList.Add(source);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("--name");
            psi.ArgumentList.Add(stem);
            LinuxHost.StripSteamPreload(psi);
            progress?.Invoke("converting " + stem + " (this takes a minute)…");
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("python did not start");
            var stderr = new StringBuilder();
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            proc.BeginErrorReadLine();
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(15 * 60 * 1000)) { try { proc.Kill(entireProcessTree: true); } catch { } return (false, "conversion timed out after 15 minutes"); }
            if (proc.ExitCode != 0 || !File.Exists(tmp))
            {
                string err;
                lock (stderr) err = stderr.ToString();
                ErrorLog.WriteNote("VoiceLibrary.Convert", $"exit {proc.ExitCode}\n{stdout}\n{err}");
                var tail = LastLines(err.Length > 0 ? err : stdout, 3);
                if (tail.Contains("No module named", StringComparison.Ordinal))
                    return (false, "conversion failed: this checkpoint is not a standard RVC v2 voice, and the fallback converter's packages (PyTorch) are not installed — " +
                                   "Settings → Conversion → Install fallback converter, then press Convert again");
                return (false, "conversion failed: " + tail);
            }
            File.Move(tmp, dest, overwrite: true);
            BootLog.Append($"converted {Path.GetFileName(source)}: {LastLines(stdout, 1)}");
            return (true, $"{stem}.onnx is ready ({new FileInfo(dest).Length / 1_000_000} MB) — press Use to make it the active voice");
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("VoiceLibrary.Convert", ex);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return (false, "conversion failed: " + ex.Message);
        }
    });

    // "pip install -r requirements-export.txt" with the Python the app
    // found, for the Settings button. Output goes to error.log only when
    // it fails.
    public static Task<(bool Ok, string Message)> InstallConverterAsync() => Task.Run(() =>
    {
        if (!File.Exists(ExportScript)) UnpackTools();
        var python = FindPython();
        if (python == null) return (false, "Python 3 was not found — install it first (python.org, or `sudo dnf install python3`)");
        var req = Path.Combine(ToolsDir, "requirements-export.txt");
        try
        {
            var psi = new ProcessStartInfo(python.Value.File) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = ToolsDir };
            foreach (var p in python.Value.Prefix) psi.ArgumentList.Add(p);
            foreach (var a in new[] { "-m", "pip", "install", "--user", "-r", req }) psi.ArgumentList.Add(a);
            LinuxHost.StripSteamPreload(psi);
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("python did not start");
            var stderr = new StringBuilder();
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            proc.BeginErrorReadLine();
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(60 * 60 * 1000)) { try { proc.Kill(entireProcessTree: true); } catch { } return (false, "pip did not finish within an hour"); }
            string err;
            lock (stderr) err = stderr.ToString();
            if (proc.ExitCode != 0)
            {
                ErrorLog.WriteNote("VoiceLibrary.InstallConverter", $"exit {proc.ExitCode}\n{stdout}\n{err}");
                return (false, "pip failed: " + LastLines(err.Length > 0 ? err : stdout, 2));
            }
            BootLog.Append("fallback converter installed with pip");
            return (true, "Fallback converter installed — unusual .pth checkpoints can be converted now");
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("VoiceLibrary.InstallConverter", ex);
            return (false, "install failed: " + ex.Message);
        }
    });

    private static string LastLines(string text, int n)
    {
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" / ", lines.Skip(Math.Max(0, lines.Length - n)).Select(l => l.Trim()));
    }
}
