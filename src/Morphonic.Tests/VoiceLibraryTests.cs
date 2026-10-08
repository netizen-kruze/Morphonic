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
            Assert.Contains("converting", message);
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
    public void GenericFileNamesTakeTheNameOfWhereTheyCameFrom()
    {
        // Hugging Face: owner/Repo_Name with the usual "model.pth" inside
        Assert.Equal("SpongeBob SquarePants RVC v2.pth", VoiceLibrary.LibraryName("binant/SpongeBob_SquarePants__RVC_v2_", "model.pth"));
        Assert.Equal("Some Voice.pth", VoiceLibrary.LibraryName("x/Some_Voice", "G_2333.pth"));
        Assert.Equal("Some Voice.onnx", VoiceLibrary.LibraryName("x/Some_Voice", "weights/model.onnx"));
        // several generic files in named sub-folders stay apart
        Assert.Equal("Pack Alice.pth", VoiceLibrary.LibraryName("x/Pack", "Alice/model.pth"));
        Assert.Equal("Pack Bob.pth", VoiceLibrary.LibraryName("x/Pack", "Bob/model.pth"));
        // a descriptive file name is kept as it is
        Assert.Equal("Obama.pth", VoiceLibrary.LibraryName("x/whatever", "voices/Obama.pth"));
        Assert.Equal("MyVoice.pth", VoiceLibrary.LibraryName("MyVoice", "MyVoice/MyVoice.pth"));
        // nothing a file system refuses
        Assert.Equal("a b c.pth", VoiceLibrary.LibraryName("x/a:b*c", "model.pth"));
        Assert.True(VoiceLibrary.IsGenericName("model.pth"));
        Assert.True(VoiceLibrary.IsGenericName("G_10000.pth"));
        Assert.True(VoiceLibrary.IsGenericName("D_10000.pth"));
        Assert.False(VoiceLibrary.IsGenericName("Gollum.pth"));
    }

    [Fact]
    public void ZipWithAGenericCheckpointIsNamedAfterTheZip()
    {
        var dir = TempData();
        try
        {
            var zip = Path.Combine(dir, "Cartoon_Cat.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var s = z.CreateEntry("model.pth").Open()) s.Write(new byte[] { 1 });
                using (var s = z.CreateEntry("model.index").Open()) s.Write(new byte[] { 2 });
            }
            var (ok, message) = VoiceLibrary.Import(zip);
            Assert.True(ok, message);
            Assert.Contains("Cartoon Cat.pth", message);
            Assert.Contains("converting", message);
            Assert.Equal(new[] { "Cartoon Cat.pth" }, Directory.GetFiles(VoiceLibrary.Dir).Select(Path.GetFileName).ToArray());
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
