using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Morphonic.Rvc;

// What lands in the models (or voices) folder.
public sealed record ModelFile(string FileName, long SizeBytes, string Sha256);

// What is fetched from the network to get it.
public sealed record ModelDownload(string Url, long SizeBytes, string Sha256);

public enum ModelKind { Component, Voice }

public enum ModelStage { Download, Assemble }

// A model the app knows by hash. The pitch model is downloaded as the
// ONNX file the RVC project publishes. The content encoder and the sample
// voice have no permissively licensed ONNX publication anywhere, so the
// app downloads their ORIGINAL checkpoints from Hugging Face (MIT) and
// assembles the ONNX files itself from the embedded templates
// (ModelAssembler, tools/make_templates.py): no Python, no third-party
// host, and the result is deterministic, so its hash is pinned too.
public sealed record ModelInfo(
    string Id,
    string DisplayName,
    ModelKind Kind,
    ModelFile File,
    ModelDownload Download,
    string? Recipe,
    string License,
    string Attribution,
    string Description)
{
    public bool Assembled => Recipe != null;
    public long SizeBytes => File.SizeBytes;
    public long DownloadBytes => Download.SizeBytes;
}

// The known-file catalog. Download sizes and hashes come from the Hugging
// Face LFS records of the pinned commits; assembled sizes and hashes from
// tools/make_templates.py assemble. A file that does not match is
// discarded.
public static class ModelCatalog
{
    public const string EncoderId = "contentvec";
    public const string PitchId = "rmvpe";
    public const string SampleVoiceId = "sample-voice";

    // Pinned to commits so a hash can never drift from its source.
    private const string RvcHubBase = "https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/e6d0c1a17da07c33557852f9dfa2bd44cc75737d/";
    private const string ContentVecBase = "https://huggingface.co/lengyue233/content-vec-best/resolve/ab04aa7067b99ee05cc82499bc64916b980a1967/";

    public static readonly ModelInfo[] Models =
    {
        new(EncoderId, "Content encoder (ContentVec, 768-d)", ModelKind.Component,
            new ModelFile("contentvec-768-layer12.onnx", 377_677_485, "7d22f5f5f435bd2df4a9437304b5f72d2509c0433ad121e907fc8219d316319a"),
            new ModelDownload(ContentVecBase + "pytorch_model.bin", 378_342_945, "d8dd400e054ddf4e6be75dab5a2549db748cc99e756a097c496c099f65a4854e"),
            "contentvec-768-layer12",
            "MIT", "ContentVec by auspicious3000 (MIT); Transformers checkpoint by lengyue233 (content-vec-best); assembled in-app",
            "Turns your speech into the phonetic features every RVC v2 voice is driven by. Required."),
        new(PitchId, "Pitch model (RMVPE)", ModelKind.Component,
            new ModelFile("rmvpe.onnx", 361_688_443, "5370e71ac80af8b4b7c793d27efd51fd8bf962de3a7ede0766dac0befa3660fd"),
            new ModelDownload(RvcHubBase + "rmvpe.onnx", 361_688_443, "5370e71ac80af8b4b7c793d27efd51fd8bf962de3a7ede0766dac0befa3660fd"),
            null,
            "MIT", "RMVPE by yxlllc (MIT); ONNX export published by the RVC project (lj1995/VoiceConversionWebUI)",
            "Tracks the pitch of your voice so the converted voice follows your melody. Required."),
        new(SampleVoiceId, "Sample voice (RVC base, 40 kHz)", ModelKind.Voice,
            new ModelFile("sample-voice-40k.onnx", 110_674_999, "ef01e598fafae188afab90204aed181b4459d69ef599b946a908d9e75b192b18"),
            new ModelDownload(RvcHubBase + "pretrained_v2/f0G40k.pth", 73_106_273, "3b2c44035e782c4b14ddc0bede9e2f4a724d025cd073f736d4f43708453adfcb"),
            "sample-voice-40k",
            "MIT", "RVC v2 pretrained generator (f0G40k) by the RVC project (lj1995/VoiceConversionWebUI); assembled in-app",
            "A generic voice from the RVC v2 pretrained generator — try the app before you import a trained voice."),
    };

    public static ModelInfo? Find(string id) => Models.FirstOrDefault(m => m.Id == id);
}

// Runtime download with SHA-256 verification, in-app assembly for the
// models that need it, and a license manifest. Each download streams to a
// .partial and is verified; an assembled model is then built next to it
// and verified again; files move into place atomically; the manifest is
// rewritten after changes.
public sealed class ModelManager
{
    // (modelId, doneBytes, totalBytes, stage) — raised on the download task.
    public event Action<string, long, long, ModelStage>? OnProgress;

    public static string DirFor(ModelInfo model) => model.Kind == ModelKind.Voice ? AppPaths.VoiceDir : AppPaths.ModelDir;
    public static string PathFor(ModelInfo model) => Path.Combine(DirFor(model), model.File.FileName);

    // The verified checkpoint an assembled model is built from; kept only
    // between a finished download and a finished assembly.
    private static string SourcePathFor(ModelInfo model) => PathFor(model) + ".source";

    // Installed = present with the exact catalog size (full hash checks are
    // done at download time and by Verify).
    public static bool IsInstalled(ModelInfo model) =>
        new FileInfo(PathFor(model)) is { Exists: true } fi && fi.Length == model.File.SizeBytes;

    public static bool ComponentsReady() =>
        IsInstalled(ModelCatalog.Find(ModelCatalog.EncoderId)!) && IsInstalled(ModelCatalog.Find(ModelCatalog.PitchId)!);

    public async Task<(bool Ok, string? Error)> DownloadAsync(string id, CancellationToken ct = default)
    {
        var model = ModelCatalog.Find(id);
        if (model == null) return (false, $"unknown model '{id}'");
        var dir = DirFor(model);
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { return (false, $"{model.DisplayName}: cannot create {dir} — {ex.Message}"); }
        if (IsInstalled(model)) return (true, null);

        var finalPath = PathFor(model);
        if (OfflinePayload.Has(model.Id))
            return await Task.Run(() => ExtractIncluded(model, finalPath, ct), ct);
        var sourcePath = SourcePathFor(model);
        // The download lands in .partial (resumable). For a direct model it
        // becomes the file; for an assembled one it becomes .source, the
        // checkpoint the assembly reads.
        var partialPath = (model.Assembled ? sourcePath : finalPath) + ".partial";
        bool keepPartial = false;
        try
        {
            Download.EnsureFreeSpace(dir, model.DownloadBytes + (model.Assembled ? model.SizeBytes : 0));
            bool haveSource = model.Assembled && File.Exists(sourcePath) && new FileInfo(sourcePath).Length == model.DownloadBytes
                              && Download.Sha256Of(sourcePath) == model.Download.Sha256;
            if (!haveSource)
            {
                using var sha = SHA256.Create();
                long received = await Download.ResumableDownloadAsync(model.Download.Url, partialPath, model.DownloadBytes, sha,
                    got => OnProgress?.Invoke(model.Id, got, model.DownloadBytes, ModelStage.Download), ct);
                if (received != model.DownloadBytes)
                    return (false, $"{model.DisplayName}: size mismatch ({received} vs {model.DownloadBytes} bytes)");
                var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (hash != model.Download.Sha256)
                    return (false, $"{model.DisplayName}: SHA-256 mismatch — download corrupt or source changed");
                File.Move(partialPath, model.Assembled ? sourcePath : finalPath, overwrite: true);
            }
            if (model.Assembled)
            {
                var error = await Task.Run(() => AssembleVerified(model, sourcePath, finalPath, ct), ct);
                if (error != null) return (false, error);
            }
            WriteManifest();
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "download cancelled");
        }
        catch (Exception ex)
        {
            keepPartial = true;   // a network failure: the next attempt resumes the .partial
            return (false, $"{model.DisplayName}: download failed — {ex.Message} (a retry continues where it stopped)");
        }
        finally
        {
            try { if (!keepPartial && File.Exists(partialPath)) File.Delete(partialPath); } catch { }
        }
    }

    // The offline build carries the file inside the binary: copied out and
    // checked against the same pinned hash a download would be.
    private (bool Ok, string? Error) ExtractIncluded(ModelInfo model, string finalPath, CancellationToken ct)
    {
        var partial = finalPath + ".partial";
        try
        {
            Download.EnsureFreeSpace(Path.GetDirectoryName(finalPath)!, model.SizeBytes);
            var (size, hash) = OfflinePayload.ExtractTo(model.Id, partial,
                done => OnProgress?.Invoke(model.Id, done, model.SizeBytes, ModelStage.Download), ct);
            if (size != model.SizeBytes || hash != model.File.Sha256)
                return (false, $"{model.DisplayName}: the copy inside this build does not match its pinned hash — the binary is damaged; download it instead");
            File.Move(partial, finalPath, overwrite: true);
            WriteManifest();
            return (true, null);
        }
        catch (OperationCanceledException) { return (false, "cancelled"); }
        catch (Exception ex) { return (false, $"{model.DisplayName}: could not unpack the included file — {ex.Message}"); }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
        }
    }

    // Builds finalPath from the verified checkpoint, checks it against the
    // pinned hash, and removes the checkpoint. A mismatch is a bug, not a
    // network problem: the checkpoint is kept so a fixed build need not
    // download it again.
    private string? AssembleVerified(ModelInfo model, string sourcePath, string finalPath, CancellationToken ct)
    {
        var building = finalPath + ".assembling";
        try
        {
            var (size, hash) = ModelAssembler.Assemble(model.Recipe!, sourcePath, building,
                done => OnProgress?.Invoke(model.Id, done, model.SizeBytes, ModelStage.Assemble), ct);
            if (size != model.SizeBytes || hash != model.File.Sha256)
            {
                ErrorLog.WriteNote("ModelManager.Assemble", $"{model.Id}: assembled {size} bytes, SHA-256 {hash}; expected {model.SizeBytes}, {model.File.Sha256}");
                return $"{model.DisplayName}: the assembled file did not match its pinned hash — please report this (error.log has the details)";
            }
            File.Move(building, finalPath, overwrite: true);
            try { File.Delete(sourcePath); } catch { }
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("ModelManager.Assemble", ex);
            return $"{model.DisplayName}: could not assemble the model — {ex.Message}";
        }
        finally
        {
            try { if (File.Exists(building)) File.Delete(building); } catch { }
        }
    }

    public bool Delete(string id)
    {
        var model = ModelCatalog.Find(id);
        if (model == null) return false;
        try
        {
            var path = PathFor(model);
            foreach (var p in new[] { path, path + ".partial", path + ".assembling", SourcePathFor(model), SourcePathFor(model) + ".partial" })
                if (File.Exists(p)) File.Delete(p);
            WriteManifest();
            return true;
        }
        catch { return false; }
    }

    // Re-hashes every installed catalog file against its pinned checksum.
    public static (int Ok, List<string> Bad) VerifyFiles()
    {
        int ok = 0;
        var bad = new List<string>();
        foreach (var m in ModelCatalog.Models)
        {
            var path = PathFor(m);
            if (!File.Exists(path)) continue;   // a truncated file is present, and wrong
            if (new FileInfo(path).Length == m.File.SizeBytes && Download.Sha256Of(path) == m.File.Sha256) ok++;
            else bad.Add(m.DisplayName);
        }
        return (ok, bad);
    }

    // models_manifest.json + LICENSES.txt in the models folder: what is
    // installed, where it came from, and under which license.
    public static void WriteManifest()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ModelDir);
            var installed = ModelCatalog.Models.Where(IsInstalled).ToArray();
            var manifest = new
            {
                generated = DateTime.UtcNow.ToString("o"),
                models = installed.Select(m => new
                {
                    id = m.Id,
                    kind = m.Kind.ToString().ToLowerInvariant(),
                    file = m.File.FileName,
                    folder = DirFor(m),
                    sizeBytes = m.File.SizeBytes,
                    sha256 = m.File.Sha256,
                    source = m.Download.Url,
                    sourceSha256 = m.Download.Sha256,
                    assembledInApp = m.Assembled,
                    license = m.License,
                    attribution = m.Attribution,
                }),
            };
            File.WriteAllText(Path.Combine(AppPaths.ModelDir, "models_manifest.json"),
                JsonConvert.SerializeObject(manifest, Formatting.Indented));
            var lines = new List<string> { "Model licenses (Morphonic)", "" };
            foreach (var m in installed) lines.Add($"{m.DisplayName}: {m.License} — {m.Attribution}");
            File.WriteAllLines(Path.Combine(AppPaths.ModelDir, "LICENSES.txt"), lines);
        }
        catch { }
    }
}
