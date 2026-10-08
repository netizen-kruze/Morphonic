using System;
using System.Linq;
using Microsoft.ML.OnnxRuntime;

namespace Morphonic.Rvc;

// RMVPE pitch estimation through the RVC project's ONNX export: the model
// takes a log-mel spectrogram [1, 128, T] (T padded to a multiple of 32)
// and returns a salience map [1, T, 360] that PitchMath decodes to one f0
// per 10 ms frame. The mel front end is computed here (MelSpectrogram).
public sealed class RmvpePitch : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _input, _output;
    private readonly RunOptions _run = new();
    private readonly MelSpectrogram _mel = new();

    public RmvpePitch(string path)
    {
        _session = new InferenceSession(path, OnnxHost.CreateSessionOptions());
        _input = _session.InputMetadata.Keys.First();
        _output = _session.OutputMetadata.Keys.First();
    }

    public static int FrameCount(int samples) => MelSpectrogram.FrameCount(samples);

    // f0 in Hz per frame (0 = unvoiced); samples/160 + 1 frames.
    public float[] Infer(ReadOnlySpan<float> audio16k, double threshold = 0.03)
    {
        int frames = MelSpectrogram.FrameCount(audio16k.Length);
        var mel = _mel.Compute(audio16k);                       // [128 * frames]
        int padded = 32 * ((frames - 1) / 32 + 1);
        float[] tensor;
        if (padded == frames) tensor = mel;
        else
        {
            // Zero padding at the end of the time axis, as RVC's F.pad does.
            tensor = new float[MelSpectrogram.Bins * padded];
            for (int b = 0; b < MelSpectrogram.Bins; b++)
                Array.Copy(mel, b * frames, tensor, b * padded, frames);
        }
        using var input = OrtValue.CreateTensorValueFromMemory(tensor, new long[] { 1, MelSpectrogram.Bins, padded });
        using var results = _session.Run(_run, new[] { _input }, new[] { input }, new[] { _output });
        var salience = results[0].GetTensorDataAsSpan<float>();
        return PitchMath.DecodeSalience(salience, frames, threshold);
    }

    public void Dispose()
    {
        _run.Dispose();
        _session.Dispose();
    }
}
