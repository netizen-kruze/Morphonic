using System.IO.Compression;
using Morphonic.Rvc;
using Xunit;

namespace Morphonic.Tests;

// The voices folder: zip bundles from voice libraries, and the streamed
// import the drop zone and the Hub download feed.
public class VoiceLibraryTests
{
    private static string TempData()
    {
        var dir = Path.Combine(Path.GetTempPath(), "morphonic-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        AppPaths.DataDir = dir;
        return dir;
    }

    [Fact]
    public void ZipImportTakesOnlyTheVoiceFilesByTheirOwnNames()
    {
        var dir = TempData();
        try
        {
            var zip = Path.Combine(dir, "MyVoice.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var s = z.CreateEntry("MyVoice/MyVoice.pth").Open()) s.Write(new byte[] { 1, 2, 3 });
                using (var s = z.CreateEntry("MyVoice/added_IVF256_Flat_nprobe_1.index").Open()) s.Write(new byte[] { 9 });
                using (var s = z.CreateEntry("../evil.onnx").Open()) s.Write(new byte[] { 4 });
                using (var s = z.CreateEntry("readme.txt").Open()) s.Write(new byte[] { 5 });
            }
            var (ok, message) = VoiceLibrary.Import(zip);
            Assert.True(ok, message);
            Assert.Contains("MyVoice.pth", message);
            Assert.Contains("Convert", message);
            var files = Directory.GetFiles(VoiceLibrary.Dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "MyVoice.pth", "evil.onnx" }, files);   // the entry's own name, never its folder
            Assert.False(File.Exists(Path.Combine(dir, "evil.onnx")));

            using (var z = ZipFile.Open(Path.Combine(dir, "nothing.zip"), ZipArchiveMode.Create))
                using (var s = z.CreateEntry("only.index").Open()) s.Write(new byte[] { 9 });
            var (ok2, message2) = VoiceLibrary.Import(Path.Combine(dir, "nothing.zip"));
            Assert.False(ok2);
            Assert.Contains(".index", message2);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void StreamedImportAssemblesPiecesAndUnpacksZips()
    {
        var dir = TempData();
        try
        {
            Assert.False(VoiceLibrary.BeginImport("notes.txt", 10).Ok);
            Assert.True(VoiceLibrary.BeginImport("Dropped.onnx", 6).Ok);
            Assert.True(VoiceLibrary.AppendImport("Dropped.onnx", new byte[] { 1, 2, 3 }).Ok);
            Assert.True(VoiceLibrary.AppendImport("Dropped.onnx", new byte[] { 4, 5, 6 }).Ok);
            Assert.Equal(6, VoiceLibrary.ImportedBytes("Dropped.onnx"));
            var (ok, message) = VoiceLibrary.EndImport("Dropped.onnx");
            Assert.True(ok, message);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, File.ReadAllBytes(Path.Combine(VoiceLibrary.Dir, "Dropped.onnx")));
            Assert.False(File.Exists(VoiceLibrary.ImportingPath("Dropped.onnx")));

            // a zip arriving in pieces is unpacked at the end
            var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                using (var s = z.CreateEntry("Voice.pth").Open()) s.Write(new byte[] { 7, 7 });
            var bytes = ms.ToArray();
            Assert.True(VoiceLibrary.BeginImport("Voice.zip", bytes.Length).Ok);
            Assert.True(VoiceLibrary.AppendImport("Voice.zip", bytes).Ok);
            var (ok2, message2) = VoiceLibrary.EndImport("Voice.zip");
            Assert.True(ok2, message2);
            Assert.True(File.Exists(Path.Combine(VoiceLibrary.Dir, "Voice.pth")));
            Assert.False(File.Exists(Path.Combine(VoiceLibrary.Dir, "Voice.zip")));

            VoiceLibrary.AbortImport("gone.pth");   // never started: harmless
            Assert.False(VoiceLibrary.EndImport("gone.pth").Ok);
        }
        finally { Directory.Delete(dir, true); }
    }
}
