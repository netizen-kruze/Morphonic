using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace Morphonic.Rvc;

public enum Accelerator { Cpu, DirectML, Cuda }

// Chooses which ONNX Runtime native build the process runs — the bundled
// CPU build, or the GPU pack's — before the first session is created, and
// hands out session options with the matching execution provider. The
// managed binding asks the loader for "onnxruntime"; a DllImport resolver
// answers with the pack's library when the pack is active, so the choice
// is made once per process and never needs a different managed assembly.
public static class OnnxHost
{
    public static Accelerator Active { get; private set; } = Accelerator.Cpu;
    public static string Status { get; private set; } = "not configured";
    public static bool Configured { get; private set; }
    // The GPU pack was complete when this process started: a CPU session
    // now is a loader verdict, not something a restart fixes.
    public static bool PackArmedAtStartup { get; private set; }

    private static string? _packLibrary;
    private static IntPtr _packHandle;
    private static int _intraThreads;

    public static string Label => Active switch
    {
        Accelerator.DirectML => "DirectML",
        Accelerator.Cuda => "CUDA",
        _ => "CPU",
    };

    // preference: "auto" (GPU pack when installed and the hardware verdict
    // allows it), "gpu" (the pack whenever installed), "cpu".
    public static void Configure(string preference)
    {
        if (Configured) return;
        Configured = true;
        _intraThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        bool wantGpu = preference != "cpu";
        if (!wantGpu) { Status = "CPU (chosen in Settings)"; return; }
        if (!GpuPack.IsInstalled()) { Status = "CPU (GPU pack not installed)"; return; }
        if (preference == "auto" && HardwareTier.Detect() != Tier.Gpu)
        {
            Status = "CPU (GPU pack installed, but: " + HardwareTier.GpuVerdict + ")";
            return;
        }
        if (!GpuPack.PreloadRuntime())
        {
            Status = "CPU (GPU pack present but its runtime did not load: " + GpuPack.RuntimeStatus + ")";
            return;
        }
        var lib = GpuPack.MainLibraryPath;
        if (!File.Exists(lib)) { Status = "CPU (GPU pack library missing: " + lib + ")"; return; }
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, Resolve);
            _packLibrary = lib;
            Active = OperatingSystem.IsWindows() ? Accelerator.DirectML : Accelerator.Cuda;
            PackArmedAtStartup = true;
            Status = $"{Label} ({GpuPack.RuntimeStatus}; {Path.GetFileName(lib)} from the GPU pack)";
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("OnnxHost.Configure", ex);
            Status = "CPU (could not redirect the runtime: " + ex.Message + ")";
        }
    }

    // Any request for the runtime library by the managed binding is answered
    // with the pack's copy; everything else keeps the default probing.
    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (_packLibrary == null) return IntPtr.Zero;
        if (!libraryName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        if (_packHandle == IntPtr.Zero)
        {
            if (!NativeLibrary.TryLoad(_packLibrary, out _packHandle))
            {
                ErrorLog.WriteNote("OnnxHost", "the GPU pack's runtime failed to load from " + _packLibrary + "; falling back to the CPU build");
                _packLibrary = null;
                Active = Accelerator.Cpu;
                Status = "CPU (GPU pack runtime failed to load)";
                return IntPtr.Zero;
            }
        }
        return _packHandle;
    }

    // Session options for the active accelerator. The first session on a
    // GPU provider is where a broken pack surfaces; callers catch and
    // report, then the engine retries on the CPU (DemoteToCpu).
    public static SessionOptions CreateSessionOptions()
    {
        var so = new SessionOptions
        {
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        switch (Active)
        {
            case Accelerator.DirectML:
                // DirectML needs a sequential, memory-pattern-free session.
                so.EnableMemoryPattern = false;
                so.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                so.AppendExecutionProvider_DML(0);
                break;
            case Accelerator.Cuda:
                so.AppendExecutionProvider_CUDA(0);
                break;
            default:
                so.IntraOpNumThreads = _intraThreads;
                so.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                break;
        }
        return so;
    }

    // A provider that cannot create sessions is given up on for the rest
    // of the process: the native library stays (it also runs the CPU
    // provider), only the provider choice changes.
    public static void DemoteToCpu(string why)
    {
        if (Active == Accelerator.Cpu) return;
        ErrorLog.WriteNote("OnnxHost", $"{Label} sessions failed ({why}); using the CPU for the rest of this run");
        BootLog.Append($"acceleration: {Label} failed — {why}; continuing on the CPU");
        Active = Accelerator.Cpu;
        Status = "CPU (the GPU provider failed: " + why + ")";
    }
}
