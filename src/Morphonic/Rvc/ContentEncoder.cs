using System;
using System.Linq;
using Microsoft.ML.OnnxRuntime;

namespace Morphonic.Rvc;

// The ContentVec encoder (HuBERT-style, 768-d, layer 12): 16 kHz audio in,
// one feature vector per 20 ms out. The ONNX file is the one
// tools/morphonic_export.py produces ("source" -> "features"); a file with
// other names still loads, as its first input and first output.
public sealed class ContentEncoder : IDisposable
{
    public const int SampleRate = 16000, Dim = 768, Hop = 320, Receptive = 400;

    private readonly InferenceSession _session;
    private readonly string _input, _output;
    private readonly RunOptions _run = new();

    public ContentEncoder(string path)
    {
        _session = new InferenceSession(path, OnnxHost.CreateSessionOptions());
        _input = _session.InputMetadata.Keys.First();
        _output = _session.OutputMetadata.ContainsKey("features") ? "features" : _session.OutputMetadata.Keys.First();
        if (_session.InputMetadata[_input].ElementDataType != Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Float)
            throw new InvalidOperationException("the content encoder must take float32 audio (half-precision exports are not supported)");
    }

    // Frames the encoder yields for n samples (its convolutional front end:
    // stride 320, receptive field 400).
    public static int FrameCount(int samples) => samples < Receptive ? 0 : (samples - Receptive) / Hop + 1;

    // Returns [frames * Dim], frame-major.
    public float[] Extract(ReadOnlySpan<float> audio16k, out int frames)
    {
        using var input = OrtValue.CreateTensorValueFromMemory(audio16k.ToArray(), new long[] { 1, audio16k.Length });
        using var results = _session.Run(_run, new[] { _input }, new[] { input }, new[] { _output });
        var value = results[0];
        var shape = value.GetTensorTypeAndShape().Shape;
        frames = (int)shape[1];
        if (shape[2] != Dim) throw new InvalidOperationException($"the content encoder yields {shape[2]}-d features; RVC v2 needs {Dim}");
        return value.GetTensorDataAsSpan<float>().ToArray();
    }

    public void Dispose()
    {
        _run.Dispose();
        _session.Dispose();
    }
}
