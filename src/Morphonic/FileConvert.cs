using System;
using System.IO;
using System.Text;
using Morphonic.Audio;
using Morphonic.Rvc;

namespace Morphonic;

// "--convert in.wav out.wav": a recording through the exact live pipeline
// (the same blocks, context and crossfade the microphone path uses), from
// a terminal. For trying a voice on a clip, and for the tests: the output
// can be measured without anyone listening.
public static class FileConvert
{
    public static int Run(string[] args)
    {
        int i = Array.IndexOf(args, "--convert");
        if (i < 0 || i + 2 >= args.Length) { Console.WriteLine("usage: Morphonic --convert <in.wav> <out.wav> [--pitch <semitones>] [--data-dir <dir>]"); return 2; }
        string inPath = args[i + 1], outPath = args[i + 2];
        int pitchArg = int.TryParse(Arg(args, "--pitch"), out var p) ? p : int.MinValue;
        try
        {
            var settings = AppSettings.Load();
            if (!ModelManager.ComponentsReady()) { Console.WriteLine("the content encoder and pitch model are not installed in " + AppPaths.ModelDir); return 1; }
            if (settings.VoiceId.Length == 0 || !VoiceLibrary.Exists(settings.VoiceId)) { Console.WriteLine("no voice chosen (settings.json VoiceId) in " + AppPaths.DataDir); return 1; }
            OnnxHost.Configure(settings.Acceleration);
            Console.WriteLine("acceleration: " + OnnxHost.Status);

            var (audio, rate) = ReadWav(inPath);
            Console.WriteLine($"input: {audio.Length} samples at {rate} Hz ({audio.Length / (double)rate:0.0} s)");
            float[] in16 = audio;
            if (rate != 16000)
            {
                var rs = new Resampler(rate, 16000);
                var tmp = new float[rs.MaxOutput(audio.Length)];
                int n = rs.Process(audio, tmp);
                in16 = tmp.AsSpan(0, n).ToArray();
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var converter = new VoiceConverter(
                new ContentEncoder(ModelManager.PathFor(ModelCatalog.Find(ModelCatalog.EncoderId)!)),
                new RmvpePitch(ModelManager.PathFor(ModelCatalog.Find(ModelCatalog.PitchId)!)),
                new VoiceModel(VoiceLibrary.PathFor(settings.VoiceId)));
            converter.PitchSemitones = pitchArg == int.MinValue ? settings.PitchSemitones : pitchArg;
            converter.SpeakerId = settings.SpeakerId;
            Console.WriteLine($"voice: {settings.VoiceId} ({converter.SampleRate} Hz) loaded in {sw.ElapsedMilliseconds} ms; pitch {converter.PitchSemitones:+0;-0;0} st");
            using var engine = new RealtimeEngine(converter, new EngineConfig
            {
                BlockMs = settings.BlockMs, ExtraMs = settings.ExtraMs, CrossfadeMs = settings.CrossfadeMs,
                NoiseGateDb = settings.NoiseGateDb, RmsMixRate = settings.RmsMixRate, OutputGainDb = settings.OutputGainDb,
            });
            // Whole blocks only: the tail is padded with silence.
            int blocks = (in16.Length + engine.Block16 - 1) / engine.Block16;
            var padded = new float[blocks * engine.Block16];
            in16.CopyTo(padded, 0);
            var output = new float[blocks * engine.Block];
            sw.Restart();
            for (int b = 0; b < blocks; b++)
            {
                var block = engine.ProcessBlock(padded.AsSpan(b * engine.Block16, engine.Block16));
                block.CopyTo(output, b * engine.Block);
            }
            // The pipeline delays the output by the crossfade region: trim it.
            int trim = Math.Min(output.Length, (engine.SolaBuffer + engine.SolaSearch));
            WriteWav(outPath, output.AsSpan(trim), engine.SampleRate);
            Console.WriteLine($"converted {blocks} blocks of {engine.BlockFrames * 10} ms in {sw.ElapsedMilliseconds} ms " +
                              $"({sw.ElapsedMilliseconds / (double)blocks:0} ms per block); wrote {outPath} at {engine.SampleRate} Hz");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("conversion failed: " + ex.Message);
            ErrorLog.WriteEntry("FileConvert", ex);
            return 1;
        }
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // RIFF/WAVE: PCM 16/24/32-bit or IEEE float, any channel count (mixed
    // to mono), any rate.
    public static (float[] Samples, int Rate) ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidDataException("not a WAV file");
        int pos = 12, channels = 0, rate = 0, bits = 0, format = 0;
        byte[]? data = null;
        while (pos + 8 <= bytes.Length)
        {
            string id = Encoding.ASCII.GetString(bytes, pos, 4);
            int size = BitConverter.ToInt32(bytes, pos + 4);
            int body = pos + 8;
            if (id == "fmt ")
            {
                format = BitConverter.ToInt16(bytes, body);
                channels = BitConverter.ToInt16(bytes, body + 2);
                rate = BitConverter.ToInt32(bytes, body + 4);
                bits = BitConverter.ToInt16(bytes, body + 14);
                if (format == 0xFFFE && size >= 40) format = BitConverter.ToInt16(bytes, body + 24); // extensible: subformat's first word
            }
            else if (id == "data")
            {
                int len = Math.Max(0, Math.Min(size, bytes.Length - body));
                data = new byte[len];
                Buffer.BlockCopy(bytes, body, data, 0, len);
            }
            pos = body + size + (size & 1);
        }
        if (data == null || channels == 0 || rate == 0) throw new InvalidDataException("WAV file has no fmt/data chunks");
        int bytesPer = bits / 8;
        int frames = data.Length / (bytesPer * channels);
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float acc = 0;
            for (int c = 0; c < channels; c++)
            {
                int at = (f * channels + c) * bytesPer;
                acc += format == 3 && bits == 32 ? BitConverter.ToSingle(data, at)
                    : bits == 16 ? BitConverter.ToInt16(data, at) / 32768f
                    : bits == 24 ? ((data[at] << 8 | data[at + 1] << 16 | data[at + 2] << 24) >> 8) / 8388608f
                    : bits == 32 ? BitConverter.ToInt32(data, at) / 2147483648f
                    : throw new NotSupportedException($"{bits}-bit WAV is not supported");
            }
            mono[f] = acc / channels;
        }
        return (mono, rate);
    }

    public static void WriteWav(string path, ReadOnlySpan<float> samples, int rate)
    {
        using var f = File.Create(path);
        using var w = new BinaryWriter(f);
        int dataBytes = samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataBytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataBytes);
        foreach (var s in samples) w.Write((short)Math.Clamp((int)MathF.Round(s * 32767f), -32768, 32767));
    }
}
