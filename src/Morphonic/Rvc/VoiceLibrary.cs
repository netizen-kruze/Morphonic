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

    // Copies a file the user picked into the library. The .index files
    // that come with RVC voices are not used (feature retrieval is not
    // part of this pipeline), so they are not copied.
    public static (bool Ok, string Message) Import(string sourcePath)
    {
        try
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext == ".zip") return ImportZip(sourcePath);
            if (ext is not (".onnx" or ".pth"))
                return (false, "pick an RVC voice: a .pth checkpoint (converted here), an .onnx export, or the .zip a voice library gave you");
            Directory.CreateDirectory(Dir);
            var dest = Path.Combine(Dir, Path.GetFileName(sourcePath));
            if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
                return (true, "that file is already in the voices folder");
            var tmp = dest + ".importing";
            File.Copy(sourcePath, tmp, overwrite: true);
            File.Move(tmp, dest, overwrite: true);
            return (true, ext == ".pth"
                ? $"{Path.GetFileName(dest)} added — press Convert to build its ONNX voice"
                : $"{Path.GetFileName(dest)} added");
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("VoiceLibrary.Import", ex);
            return (false, "import failed: " + ex.Message);
        }
    }

    // Voice libraries hand out zips holding the .pth (and an .index the
    // app does not use, plus sometimes a readme). Only the voice files
    // are taken, by their own names: no folders from the archive.
    public static (bool Ok, string Message) ImportZip(string zipPath)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var added = new List<string>();
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (entry.Name.Length == 0 || ext is not (".pth" or ".onnx")) continue;
                    if (entry.Length > 4L << 30) return (false, $"{entry.Name} in the zip is too large");
                    var dest = Path.Combine(Dir, entry.Name);
                    var tmp = dest + ".importing";
                    entry.ExtractToFile(tmp, overwrite: true);
                    File.Move(tmp, dest, overwrite: true);
                    added.Add(entry.Name);
                }
            }
            if (added.Count == 0)
                return (false, $"{Path.GetFileName(zipPath)} holds no .pth or .onnx voice (an .index file alone is not a voice)");
            bool needsConvert = added.Any(n => n.EndsWith(".pth", StringComparison.OrdinalIgnoreCase));
            return (true, string.Join(", ", added) + (needsConvert ? " added — press Convert to build the ONNX voice" : " added"));
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

    public static (bool Ok, string Message) EndImport(string name)
    {
        name = Path.GetFileName(name);
        var tmp = ImportingPath(name);
        if (!File.Exists(tmp)) return (false, $"{name}: nothing was received");
        try
        {
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var r = ImportZip(tmp);
                File.Delete(tmp);
                return r;
            }
            var dest = Path.Combine(Dir, name);
            File.Move(tmp, dest, overwrite: true);
            return (true, name.EndsWith(".pth", StringComparison.OrdinalIgnoreCase)
                ? $"{name} added — press Convert to build its ONNX voice"
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
                return (false, "", false);   // a raw training generator, or another layout
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
            psi.ArgumentList.Add("-I");
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
                    tail += " — install the converter's packages: pip install -r " + Path.Combine(ToolsDir, "requirements-export.txt");
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
