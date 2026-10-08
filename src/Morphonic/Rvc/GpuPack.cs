using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Morphonic.Rvc;

// Optional GPU acceleration, downloaded on demand so the release stays
// small and CPU-only: a different build of the same ONNX Runtime version
// the app is compiled against, placed in the data folder's runtimes/gpu/
// and loaded instead of the bundled CPU build (OnnxHost).
//
//  Windows — DirectML: works on any DirectX 12 card (NVIDIA, AMD, Intel).
//    Two parts from nuget.org (a nupkg is a zip): the DirectML build of
//    onnxruntime.dll, and Microsoft's DirectML.dll it links against.
//  Linux — CUDA (NVIDIA only): the CUDA build from nuget.org plus the
//    CUDA 12 runtime it links against (cudart, cuBLAS, cuFFT, cuRAND,
//    cuDNN 9, nvJitLink) from NVIDIA's own wheels on PyPI (a wheel is a
//    zip). A system-wide CUDA 12 runtime is used instead when present.
//    Only the driver's libcuda.so.1 cannot be downloaded — it must match
//    the kernel module (on Fedora: xorg-x11-drv-nvidia-cuda-libs).
//
// Every archive and every extracted file is verified against the hashes
// below. Takes effect on the next app start (natives load once per process).
public static class GpuPack
{
    public const string Id = "gpu-pack";
    public static string DisplayName => OperatingSystem.IsWindows()
        ? "GPU acceleration (DirectML)"
        : "GPU acceleration (CUDA, NVIDIA)";
    public static string License => OperatingSystem.IsWindows()
        ? "MIT (ONNX Runtime) + Microsoft DirectML license"
        : "MIT (ONNX Runtime) + NVIDIA EULA (CUDA runtime, cuDNN)";
    public static string Attribution => OperatingSystem.IsWindows()
        ? "ONNX Runtime DirectML build by Microsoft; DirectML by Microsoft"
        : "ONNX Runtime CUDA build by Microsoft; CUDA runtime, cuBLAS, cuFFT, cuRAND, cuDNN and nvJitLink by NVIDIA";
    public static string Description => OperatingSystem.IsWindows()
        ? "Runs the models on your graphics card instead of the CPU — much lower delay. Any DirectX 12 card. Takes effect after a restart."
        : "Runs the models on your NVIDIA card instead of the CPU — much lower delay. Takes effect after a restart.";

    public sealed record PackFile(string Entry, string Name, long Size, string Sha256);

    // One downloadable archive. Runtime parts are skipped when the system
    // already provides the CUDA 12 runtime (Linux only).
    public sealed record PackPart(string Id, string Label, string Url, long Size, string Sha256, PackFile[] Files, bool Runtime)
    {
        public long ExtractedBytes => Files.Sum(f => f.Size);
    }

    private const string OrtVersion = "1.24.4";

    // ── Windows: DirectML ──────────────────────────────────────────

    public static readonly PackPart OrtDirectML = new(
        "ort-directml", "ONNX Runtime (DirectML build)",
        $"https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.directml/{OrtVersion}/microsoft.ml.onnxruntime.directml.{OrtVersion}.nupkg",
        12_458_649, "57e9f11b73437bef7a309496135d4c1f96b1a8e9ddba60013fa27bfc1d788681",
        new[]
        {
            new PackFile("runtimes/win-x64/native/onnxruntime.dll", "onnxruntime.dll", 17_328_152, "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40"),
            new PackFile("runtimes/win-x64/native/onnxruntime_providers_shared.dll", "onnxruntime_providers_shared.dll", 22_040, "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6"),
            new PackFile("LICENSE", "ONNXRUNTIME-LICENSE.txt", 1_094, "c250d6278f0b47a6439fb7592b08b58a55eb9f535aa49a1db63211c3f982b674"),
        },
        Runtime: false);

    public static readonly PackPart DirectML = new(
        "directml", "DirectML",
        "https://api.nuget.org/v3-flatcontainer/microsoft.ai.directml/1.15.4/microsoft.ai.directml.1.15.4.nupkg",
        202_292_617, "4e7cb7ddce8cf837a7a75dc029209b520ca0101470fcdf275c1f49736a3615b9",
        new[]
        {
            new PackFile("bin/x64-win/DirectML.dll", "DirectML.dll", 18_527_776, "9c9e6d822561c6c41b90e6994b3e8857cf1d66dbfb1e0c4c799c7c89b4e92da1"),
            new PackFile("LICENSE.txt", "DIRECTML-LICENSE.txt", 10_439, "a05138e3a085ff60a44881eedfa58dccb03ecc1d7b1f6ae888418e8c2fec4b8d"),
        },
        Runtime: false);

    // ── Linux: CUDA ────────────────────────────────────────────────

    private static PackFile Lib(string prefix, string name, long size, string sha) => new(prefix + name, name, size, sha);

    public static readonly PackPart OrtCuda = new(
        "ort-cuda", "ONNX Runtime (CUDA build)",
        $"https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.gpu.linux/{OrtVersion}/microsoft.ml.onnxruntime.gpu.linux.{OrtVersion}.nupkg",
        205_529_484, "06540847b4f83cc5fd92562263122659452cf62783e9a1477b6000b2d947b542",
        new[]
        {
            Lib("runtimes/linux-x64/native/", "libonnxruntime.so", 25_493_408, "1aacefdf0b4afa145d410b2381bbc3db3d978c485fb182c42a2b0b09f91f5310"),
            Lib("runtimes/linux-x64/native/", "libonnxruntime_providers_cuda.so", 315_724_552, "1defa2f82f2195a0667f2003e14c6715107af7d2716364cfdfa1a8c5e708ddaa"),
            Lib("runtimes/linux-x64/native/", "libonnxruntime_providers_shared.so", 14_632, "c6a12593396095f5670160e284c35d1700b7708cf3037b7042e2a5200ccae772"),
            new PackFile("LICENSE", "ONNXRUNTIME-LICENSE.txt", 1_094, "c250d6278f0b47a6439fb7592b08b58a55eb9f535aa49a1db63211c3f982b674"),
        },
        Runtime: false);

    public const string NvidiaLicenseFileName = "NVIDIA-CUDA-LICENSE.txt";
    public const string CudnnLicenseFileName = "NVIDIA-CUDNN-LICENSE.txt";

    public static readonly PackPart CudaRuntime = new(
        "cuda-runtime", "CUDA 12 runtime",
        "https://files.pythonhosted.org/packages/bc/46/a92db19b8309581092a3add7e6fceb4c301a3fd233969856a8cbf042cd3c/nvidia_cuda_runtime_cu12-12.9.79-py3-none-manylinux2014_x86_64.manylinux_2_17_x86_64.whl",
        3_493_179, "25bba2dfb01d48a9b59ca474a1ac43c6ebf7011f1b0b8cc44f54eb6ac48a96c3",
        new[]
        {
            Lib("nvidia/cuda_runtime/lib/", "libcudart.so.12", 741_088, "256e6409e4f06f618e1fb53d4844a6b81cdded1013afa8ade40c22f99eb133b7"),
            new PackFile("nvidia_cuda_runtime_cu12-12.9.79.dist-info/licenses/License.txt", NvidiaLicenseFileName, 59_262, "ad6f5853fba0ca0d159d0f58d49ae49830c2f8c93f7a92648b9ce90adb4c6ccd"),
        },
        Runtime: true);

    public static readonly PackPart NvJitLink = new(
        "nvjitlink", "nvJitLink 12",
        "https://files.pythonhosted.org/packages/46/0c/c75bbfb967457a0b7670b8ad267bfc4fffdf341c074e0a80db06c24ccfd4/nvidia_nvjitlink_cu12-12.9.86-py3-none-manylinux2010_x86_64.manylinux_2_12_x86_64.whl",
        39_748_338, "e3f1171dbdc83c5932a45f0f4c99180a70de9bd2718c1ab77d14104f6d7147f9",
        new[] { Lib("nvidia/nvjitlink/lib/", "libnvJitLink.so.12", 95_935_344, "02d3acb5fe598dd20f0fca3cc03734ad164037a22747a01900561a42d0b8448f") },
        Runtime: true);

    public static readonly PackPart CuBlas = new(
        "cublas", "cuBLAS 12",
        "https://files.pythonhosted.org/packages/cb/c0/0a517bfe63ccd3b92eb254d264e28fca3c7cab75d07daea315250fb1bf73/nvidia_cublas_cu12-12.9.2.10-py3-none-manylinux_2_27_x86_64.whl",
        581_240_110, "e4f53a8ca8c5d6e8c492d0d0a3d565ecb59a751b19cfdaa4f6da0ab2104c1702",
        new[]
        {
            Lib("nvidia/cublas/lib/", "libcublasLt.so.12", 749_210_000, "2c9006a75c74b3bea2dc7ae2ec38ab038b0e45ea02cb4b717a915e8a5796acb1"),
            Lib("nvidia/cublas/lib/", "libcublas.so.12", 105_140_976, "5757ab5839fb4f203ca47ecb336110d10f4a5606b1e097f195fbca89774569e2"),
        },
        Runtime: true);

    public static readonly PackPart CuFft = new(
        "cufft", "cuFFT 11",
        "https://files.pythonhosted.org/packages/95/f4/61e6996dd20481ee834f57a8e9dca28b1869366a135e0d42e2aa8493bdd4/nvidia_cufft_cu12-11.4.1.4-py3-none-manylinux2014_x86_64.manylinux_2_17_x86_64.whl",
        200_877_592, "c67884f2a7d276b4b80eb56a79322a95df592ae5e765cf1243693365ccab4e28",
        new[] { Lib("nvidia/cufft/lib/", "libcufft.so.11", 291_507_928, "e1d65ebd08895f9d9883f848f3974f89e0130416252477b18835ba7f15d159bc") },
        Runtime: true);

    public static readonly PackPart CuRand = new(
        "curand", "cuRAND 10",
        "https://files.pythonhosted.org/packages/fb/aa/6584b56dc84ebe9cf93226a5cde4d99080c8e90ab40f0c27bda7a0f29aa1/nvidia_curand_cu12-10.3.9.90-py3-none-manylinux_2_27_x86_64.whl",
        63_619_976, "b32331d4f4df5d6eefa0554c565b626c7216f87a06a4f56fab27c3b68a830ec9",
        new[] { Lib("nvidia/curand/lib/", "libcurand.so.10", 136_749_240, "f9bea038a2703b721571fd45a299a898141fd8cb264a5912635c95116f5960fe") },
        Runtime: true);

    // The RNN library (libcudnn_adv, 275 MB) is needed: ONNX Runtime runs
    // the pitch model's GRU layers through cuDNN's RNN API. Only the
    // extension library (libcudnn_ext) is left out.
    public static readonly PackPart CuDnn = new(
        "cudnn", "cuDNN 9",
        "https://files.pythonhosted.org/packages/65/e4/c5a205d48ff00ed8b27882bb45d338d8138e976c76385f328a326bbfaeda/nvidia_cudnn_cu12-9.27.0.42-py3-none-manylinux_2_27_x86_64.whl",
        766_178_381, "0a4aa3a7d2264256506c6857fc41fc0c499982f78d70195b1cfcc9055c1957cd",
        new[]
        {
            Lib("nvidia/cudnn/lib/", "libcudnn.so.9", 133_336, "4c3ccb552ee0c43aacca7498e12914fbda31be4fcb52cbdc1aaa87e78fb96e19"),
            Lib("nvidia/cudnn/lib/", "libcudnn_graph.so.9", 115_562_616, "28aaa0dc993c91d7c537846d3409a45dd71b94097c4794aedf9aac8a98a0bac8"),
            Lib("nvidia/cudnn/lib/", "libcudnn_ops.so.9", 106_964_664, "e6c3242e8ea81fdc9b33d6a20144cab7ba246d2a4bb6b9ed31f0894be55921e4"),
            Lib("nvidia/cudnn/lib/", "libcudnn_cnn.so.9", 4_203_880, "0bc150ee4d6ed549287a5e2c04f3284155b208a22084543b6af2ec6521e02561"),
            Lib("nvidia/cudnn/lib/", "libcudnn_engines_precompiled.so.9", 566_255_968, "106171d50adb53c263c9ce8e83d3b257fe231731372dbb72d31520fa58fae7d1"),
            Lib("nvidia/cudnn/lib/", "libcudnn_engines_runtime_compiled.so.9", 48_269_728, "3f480e78a61ab16d54263c0397f62f57915af67213752b8e7235e74f4b027a36"),
            Lib("nvidia/cudnn/lib/", "libcudnn_engines_tensor_ir.so.9", 2_099_128, "805ec06f754944c107ce4e2c234a3a190a8e42d5b4bfae82a9128460fd5959bd"),
            Lib("nvidia/cudnn/lib/", "libcudnn_heuristic.so.9", 96_473_880, "0f8bebd3f041bdce10e11c7981f3cd397e90e1f953215b6afa1c37b1d65db15a"),
            Lib("nvidia/cudnn/lib/", "libcudnn_adv.so.9", 274_846_904, "6fb2f7455e690d77df3351483be3abd23c7b26c85cb2b70fe62abd27c0a14fa2"),
            new PackFile("nvidia_cudnn_cu12-9.27.0.42.dist-info/licenses/License.txt", CudnnLicenseFileName, 18_174, "49cf79bdb35734b52fe6203013b3bd759f81e998cd32aa2c65c51db9a88c61d2"),
        },
        Runtime: true);

    public static PackPart[] Parts => OperatingSystem.IsWindows()
        ? new[] { OrtDirectML, DirectML }
        : new[] { OrtCuda, CudaRuntime, NvJitLink, CuBlas, CuFft, CuRand, CuDnn };

    // The CUDA runtime libraries in dependency order: each must be in the
    // process (by soname) before the next one asks for it.
    public static readonly string[] CudaLoadOrder =
    {
        "libcudart.so.12", "libnvJitLink.so.12", "libcublasLt.so.12", "libcublas.so.12",
        "libcufft.so.11", "libcurand.so.10", "libcudnn.so.9",
    };

    public static string InstallDir => AppPaths.GpuDir;

    // The runtime library the app loads instead of the bundled CPU build.
    public static string MainLibraryPath =>
        Path.Combine(InstallDir, OperatingSystem.IsWindows() ? "onnxruntime.dll" : "libonnxruntime.so");

    // ── state ──────────────────────────────────────────────────────

    private static bool FilePresent(PackFile f) =>
        new FileInfo(Path.Combine(InstallDir, f.Name)) is { Exists: true } fi && fi.Length == f.Size;

    public static bool PartInstalled(PackPart part) => part.Files.All(FilePresent);

    // The ONNX Runtime GPU build itself is there.
    public static bool RuntimeBuildInstalled() => Parts.Where(p => !p.Runtime).All(PartInstalled);

    public static bool BundledCudaInstalled() => Parts.Where(p => p.Runtime).All(PartInstalled);

    // The system provides a CUDA 12 runtime (never answered by loading
    // anything). Always true on Windows, where there is no runtime part.
    public static bool SystemCudaPresent() =>
        !OperatingSystem.IsLinux() || CudaLoadOrder.All(LinuxHost.LibraryPresent);

    public static bool CudaRuntimePresent() => BundledCudaInstalled() || SystemCudaPresent();

    // "Installed" means it can actually work: the build plus a runtime.
    public static bool IsInstalled() => RuntimeBuildInstalled() && CudaRuntimePresent();

    internal static List<PackPart> Plan(IEnumerable<PackPart> parts, Func<PackPart, bool> partInstalled, bool systemRuntime)
    {
        var plan = new List<PackPart>();
        foreach (var part in parts)
        {
            if (partInstalled(part)) continue;
            if (part.Runtime && systemRuntime) continue;
            plan.Add(part);
        }
        return plan;
    }

    private static List<PackPart> Plan() => Plan(Parts, PartInstalled, SystemCudaPresent());

    // The download size shown in the UI: what a download would fetch now,
    // or the footprint of what is installed.
    public static long SizeBytes
    {
        get
        {
            var plan = Plan();
            return plan.Count > 0 ? plan.Sum(p => p.Size) : Parts.Where(PartInstalled).Sum(p => p.Size);
        }
    }

    // ── loading ────────────────────────────────────────────────────

    private static int _preloaded;
    private static readonly List<IntPtr> Handles = new(); // kept for the life of the process
    public static string RuntimeStatus { get; private set; } = "not checked";

    // Loads the libraries the GPU build links against by full path before
    // OnnxHost loads the build itself, so its bare-name imports resolve to
    // these copies (a module already loaded under a name satisfies later
    // requests for that name on both operating systems). Once per process;
    // never unloads. Returns false when something could not be loaded.
    public static bool PreloadRuntime()
    {
        if (Interlocked.Exchange(ref _preloaded, 1) != 0) return RuntimeStatus.StartsWith("loaded", StringComparison.Ordinal);
        if (!RuntimeBuildInstalled()) { RuntimeStatus = "GPU pack not installed"; return false; }
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var dml = Path.Combine(InstallDir, "DirectML.dll");
                if (!NativeLibrary.TryLoad(dml, out var h))
                {
                    RuntimeStatus = "DirectML.dll failed to load from " + dml;
                    return false;
                }
                Handles.Add(h);
                RuntimeStatus = "loaded DirectML";
                return true;
            }
            bool bundled = BundledCudaInstalled();
            if (!bundled && !SystemCudaPresent())
            {
                RuntimeStatus = "CUDA 12 runtime missing — download GPU acceleration again to fetch it";
                return false;
            }
            var driver = LinuxHost.LibraryPath("libcuda.so.1");
            if (driver != null && NativeLibrary.TryLoad(driver, out var driverHandle)) Handles.Add(driverHandle);
            foreach (var name in CudaLoadOrder)
            {
                var path = bundled ? Path.Combine(InstallDir, name) : LinuxHost.LibraryPath(name);
                if (path != null && NativeLibrary.TryLoad(path, out var handle)) { Handles.Add(handle); continue; }
                RuntimeStatus = $"{(bundled ? "bundled" : "system")} CUDA 12 runtime failed to load: {name}" +
                                (path == null ? " (path unknown)" : $" from {path}");
                ErrorLog.WriteNote("GpuPack.PreloadRuntime", RuntimeStatus);
                return false;
            }
            RuntimeStatus = bundled ? "loaded the bundled CUDA 12 runtime" : "loaded the system CUDA 12 runtime";
            return true;
        }
        catch (Exception ex)
        {
            RuntimeStatus = "runtime load failed: " + ex.Message;
            ErrorLog.WriteEntry("GpuPack.PreloadRuntime", ex);
            return false;
        }
    }

    // ── download ───────────────────────────────────────────────────

    public static async Task<(bool Ok, string? Error)> DownloadAsync(Action<long, long> onProgress, CancellationToken ct = default)
    {
        var plan = Plan();
        if (plan.Count == 0) return (true, null);
        long total = plan.Sum(p => p.Size);
        long done = 0;
        try
        {
            Directory.CreateDirectory(InstallDir);
            Download.EnsureFreeSpace(InstallDir, plan.Max(p => p.Size) + plan.Sum(p => p.ExtractedBytes) + 64_000_000);
        }
        catch (Exception ex) { return (false, $"GPU pack: {ex.Message}"); }
        foreach (var part in plan)
        {
            long offset = done;
            var (ok, error) = await FetchPartAsync(part, received => onProgress(offset + received, total), ct);
            if (!ok) return (false, error);
            done += part.Size;
        }
        return (true, null);
    }

    // The archive is staged in the install folder itself (on Fedora /tmp
    // is RAM). A network failure keeps the staged file so the next attempt
    // resumes it; a bad hash or a cancel drops it.
    private static async Task<(bool Ok, string? Error)> FetchPartAsync(PackPart part, Action<long> onProgress, CancellationToken ct)
    {
        var temp = Path.Combine(InstallDir, part.Id + ".download");
        bool keepTemp = false;
        try
        {
            Directory.CreateDirectory(InstallDir);
            using (var sha = SHA256.Create())
            {
                long received = await Download.ResumableDownloadAsync(part.Url, temp, part.Size, sha, onProgress, ct);
                var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (received != part.Size || hash != part.Sha256)
                    return (false, $"GPU pack ({part.Label}): package SHA-256 mismatch — download corrupt or source changed");
            }
            using (var zip = ZipFile.OpenRead(temp))
            {
                foreach (var file in part.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.GetEntry(file.Entry);
                    if (entry == null) return (false, $"GPU pack ({part.Label}): {file.Name} missing from the package");
                    var partial = Path.Combine(InstallDir, file.Name + ".partial");
                    entry.ExtractToFile(partial, overwrite: true);
                    if (new FileInfo(partial).Length != file.Size || Download.Sha256Of(partial) != file.Sha256)
                    {
                        File.Delete(partial);
                        return (false, $"GPU pack ({part.Label}): {file.Name} failed verification");
                    }
                    File.Move(partial, Path.Combine(InstallDir, file.Name), overwrite: true);
                }
            }
            return (true, null);
        }
        catch (OperationCanceledException) { return (false, "download cancelled"); }
        catch (Exception ex)
        {
            keepTemp = true;
            return (false, $"GPU pack ({part.Label}): download failed — {ex.Message} (a retry continues where it stopped)");
        }
        finally
        {
            try { if (!keepTemp && File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    // Re-hashes every installed pack file. A part that is absent altogether
    // is not the user's problem (a system runtime stands in for it); a part
    // with any file present must be whole.
    public static (int Ok, List<string> Bad) VerifyFiles()
    {
        int ok = 0;
        var bad = new List<string>();
        foreach (var part in Parts)
        {
            if (!part.Files.Any(f => File.Exists(Path.Combine(InstallDir, f.Name)))) continue;
            foreach (var f in part.Files)
            {
                var path = Path.Combine(InstallDir, f.Name);
                if (File.Exists(path) && new FileInfo(path).Length == f.Size && Download.Sha256Of(path) == f.Sha256) ok++;
                else bad.Add($"GPU acceleration ({f.Name})");
            }
        }
        return (ok, bad);
    }

    public static bool Delete()
    {
        try
        {
            if (!Directory.Exists(InstallDir)) return true;
            foreach (var f in Parts.SelectMany(p => p.Files))
            {
                var path = Path.Combine(InstallDir, f.Name);
                if (File.Exists(path)) File.Delete(path);
            }
            foreach (var pattern in new[] { "*.partial", "*.download" })
                foreach (var stray in Directory.EnumerateFiles(InstallDir, pattern)) File.Delete(stray);
            if (!Directory.EnumerateFileSystemEntries(InstallDir).Any()) Directory.Delete(InstallDir);
            return true;
        }
        catch { return false; }
    }
}
