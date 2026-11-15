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
        // nothing a file system refuses — Windows' set on every platform, so
        // a library made on Linux still opens after a copy to Windows
        Assert.Equal("a b c.pth", VoiceLibrary.LibraryName("x/a:b*c", "model.pth"));
        Assert.Equal("q a.onnx", VoiceLibrary.LibraryName("x/y", "q?<a|>\".onnx"));
        // no origin at all: the file's own name, generic as it is
        Assert.Equal("model.pth", VoiceLibrary.LibraryName("", "model.pth"));
        Assert.True(VoiceLibrary.IsGenericName("model.pth"));
        Assert.True(VoiceLibrary.IsGenericName("G_10000.pth"));
        Assert.True(VoiceLibrary.IsGenericName("D_10000.pth"));
        Assert.False(VoiceLibrary.IsGenericName("Gollum.pth"));
    }

    [Fact]
    public void APickedFileIsNamedAfterItsFolderUnlessThatFolderSaysNothing()
    {
        var dir = TempData();
        try
        {
            // ~/Downloads/model.pth stays "model.pth" — never a voice called "Downloads"
            var downloads = Path.Combine(dir, "Downloads");
            Directory.CreateDirectory(downloads);
            File.WriteAllBytes(Path.Combine(downloads, "model.pth"), new byte[] { 1 });
            var (ok, message) = VoiceLibrary.Import(Path.Combine(downloads, "model.pth"), out var added);
            Assert.True(ok, message);
            Assert.Equal("model.pth", added);
            // …/SpongeBob/model.pth becomes "SpongeBob.pth"
            var folder = Path.Combine(dir, "SpongeBob");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "model.pth"), new byte[] { 2 });
            (ok, message) = VoiceLibrary.Import(Path.Combine(folder, "model.pth"), out added);
            Assert.True(ok, message);
            Assert.Equal("SpongeBob.pth", added);
            // a descriptive name is kept wherever it came from
            File.WriteAllBytes(Path.Combine(downloads, "Gollum.onnx"), new byte[] { 3 });
            (ok, _) = VoiceLibrary.Import(Path.Combine(downloads, "Gollum.onnx"), out added);
            Assert.True(ok);
            Assert.Equal("Gollum.onnx", added);
            Assert.True(VoiceLibrary.IsUserFolder(downloads));
            Assert.True(VoiceLibrary.IsUserFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            Assert.True(VoiceLibrary.IsUserFolder(VoiceLibrary.Dir));
            Assert.True(VoiceLibrary.IsUserFolder(Path.GetPathRoot(dir)!));
            Assert.False(VoiceLibrary.IsUserFolder(folder));
            // the desktop's own (localized) folders say nothing either; the
            // paths are each platform's own, since they are compared in full
            if (OperatingSystem.IsWindows())
            {
                var known = new[] { @"C:\Users\u\Téléchargements", @"C:\Users\u\Descargas" };
                Assert.True(VoiceLibrary.IsUserFolder(@"C:\Users\u\Téléchargements\", known));
                Assert.True(VoiceLibrary.IsUserFolder(@"c:\users\u\descargas", known));
                Assert.False(VoiceLibrary.IsUserFolder(@"C:\Users\u\Descargas\SpongeBob", known));
                Assert.True(VoiceLibrary.IsUserFolder(@"D:\", known));   // a drive's root (a USB stick)
                Assert.False(VoiceLibrary.IsUserFolder(@"D:\Voices\SpongeBob", known));
            }
            else
            {
                var known = new[] { "/home/u/Téléchargements", "/home/u/Descargas" };
                Assert.True(VoiceLibrary.IsUserFolder("/home/u/Téléchargements/", known));
                Assert.True(VoiceLibrary.IsUserFolder("/home/u/Descargas", known));
                Assert.False(VoiceLibrary.IsUserFolder("/home/u/Descargas/SpongeBob", known));
                // mount points
                Assert.True(VoiceLibrary.IsUserFolder("/media/u/KINGSTON", known));
                Assert.True(VoiceLibrary.IsUserFolder("/run/media/u/KINGSTON", known));
                Assert.True(VoiceLibrary.IsUserFolder("/mnt/usb", known));
                Assert.True(VoiceLibrary.IsUserFolder("/media/usb", known));
                Assert.False(VoiceLibrary.IsUserFolder("/mnt/usb/Voices/SpongeBob", known));
                // ~/.config/user-dirs.dirs
                var dirs = VoiceLibrary.ParseXdgUserDirs(new[]
                {
                    "# This file is written by xdg-user-dirs-update",
                    "XDG_DESKTOP_DIR=\"$HOME/Bureau\"",
                    "XDG_DOWNLOAD_DIR=\"$HOME/Téléchargements\"",
                    "XDG_MUSIC_DIR=\"$HOME\"",
                    "XDG_VIDEOS_DIR=\"/srv/videos/\"",
                    "NOT_A_DIR=\"$HOME/x\"",
                }, "/home/u").ToArray();
                Assert.Equal(new[] { "/home/u/Bureau", "/home/u/Téléchargements", "/home/u", "/srv/videos" }, dirs);
            }
            // a file already in the library is reported as added, not copied onto itself
            (ok, message) = VoiceLibrary.Import(Path.Combine(VoiceLibrary.Dir, "Gollum.onnx"), out added);
            Assert.True(ok);
            Assert.Contains("already", message);
            Assert.Equal("Gollum.onnx", added);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task AnUnusualCheckpointIsHandedToThePythonToolWithAPlainAnswer()
    {
        // The tiny fixture is a v2, pitch-guided checkpoint with a layout the
        // embedded graphs do not cover, so conversion goes to the Python tool.
        // Without PyTorch (or without Python) the answer must say what to
        // install, not show a traceback; nothing half-made may stay behind.
        var dir = TempData();
        try
        {
            Directory.CreateDirectory(VoiceLibrary.Dir);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny_voice.pth"), Path.Combine(VoiceLibrary.Dir, "Tiny.pth"));
            var notes = new List<string>();
            var (ok, message) = await VoiceLibrary.ConvertAsync("Tiny.pth", notes.Add);
            Assert.False(ok);
            Assert.StartsWith("conversion failed", message);
            Assert.True(message.Contains("Install fallback converter", StringComparison.Ordinal) ||
                        message.Contains("Python 3", StringComparison.Ordinal) ||
                        message.Contains("needs Python", StringComparison.Ordinal),
                        "the answer names the missing piece: " + message);
            Assert.DoesNotContain("Traceback", message);
            Assert.False(File.Exists(Path.Combine(VoiceLibrary.Dir, "Tiny.onnx")));
            Assert.Empty(Directory.GetFiles(VoiceLibrary.Dir, "*.converting*"));
            Assert.True(File.Exists(VoiceLibrary.ExportScript));   // the tool was unpacked for the attempt
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ANewVoiceNeverReplacesOneWithTheSameName()
    {
        // voice libraries call nearly every checkpoint model.pth: a second,
        // different one must land beside the first, never over it or its
        // conversion; the very same file again is the voice already there
        var dir = TempData();
        try
        {
            var downloads = Path.Combine(dir, "Downloads");
            Directory.CreateDirectory(downloads);
            Directory.CreateDirectory(VoiceLibrary.Dir);
            File.WriteAllBytes(Path.Combine(VoiceLibrary.Dir, "model.onnx"), new byte[] { 9 });   // an earlier voice, converted
            File.WriteAllBytes(Path.Combine(VoiceLibrary.Dir, "model.pth"), new byte[] { 1, 1 });
            File.WriteAllBytes(Path.Combine(downloads, "model.pth"), new byte[] { 2, 2 });
            var (ok, message) = VoiceLibrary.Import(Path.Combine(downloads, "model.pth"), out var added);
            Assert.True(ok, message);
            Assert.Equal("model 2.pth", added);
            Assert.Contains("converting", message);
            Assert.Equal(new byte[] { 1, 1 }, File.ReadAllBytes(Path.Combine(VoiceLibrary.Dir, "model.pth")));
            // the same file again: no third copy, it is the voice already there
            (ok, message) = VoiceLibrary.Import(Path.Combine(downloads, "model.pth"), out added);
            Assert.True(ok, message);
            Assert.Equal("model 2.pth", added);
            Assert.Contains("already in your library", message);
            Assert.False(File.Exists(Path.Combine(VoiceLibrary.Dir, "model 3.pth")));
            // a dropped file (streamed) and an export: the conversion's name counts as taken too
            Assert.True(VoiceLibrary.BeginImport("model.pth", 1).Ok);
            Assert.True(VoiceLibrary.AppendImport("model.pth", new byte[] { 3 }).Ok);
            Assert.True(VoiceLibrary.EndImport("model.pth", out added).Ok);
            Assert.Equal("model 3.pth", added);
            File.WriteAllBytes(Path.Combine(VoiceLibrary.Dir, "Nova.pth"), new byte[] { 4 });   // not converted yet
            Assert.True(VoiceLibrary.BeginImport("Nova.onnx", 1).Ok);
            Assert.True(VoiceLibrary.AppendImport("Nova.onnx", new byte[] { 5 }).Ok);
            Assert.True(VoiceLibrary.EndImport("Nova.onnx", out added).Ok);
            Assert.Equal("Nova 2.onnx", added);
            // two different checkpoints of the same name in one zip
            var zip = Path.Combine(dir, "Pack.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var s = z.CreateEntry("a/model.pth").Open()) s.Write(new byte[] { 6 });
                using (var s = z.CreateEntry("b/model.pth").Open()) s.Write(new byte[] { 7 });
            }
            Assert.True(VoiceLibrary.ImportZip(zip, out var fromZip).Ok);
            Assert.Equal(2, fromZip.Distinct().Count());
            Assert.Empty(Directory.GetFiles(VoiceLibrary.Dir, "*.importing"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheLinuxImportDialogGetsOnePatternPerFileType()
    {
        // Photino's GTK dialog applies only the first pattern of a filter as given:
        // a "*.pth;*.onnx;*.zip" filter listed no file at all on Linux
        var linux = Program.VoiceFileFilters(linux: true);
        Assert.All(linux, f => Assert.Single(f.Extensions));
        Assert.Equal("*", linux[0].Extensions[0]);   // first: every file, nothing hidden
        Assert.Equal(new[] { "*.pth", "*.onnx", "*.zip", "*.index" }, linux.Skip(1).Select(f => f.Extensions[0]));
        Assert.Equal(new[] { "*.pth", "*.onnx", "*.zip", "*.index" }, Program.VoiceFileFilters(linux: false).Single().Extensions);
    }

    [Fact]
    public void WhatLandedIsReportedSoItCanBeAdopted()
    {
        var dir = TempData();
        try
        {
            // a zip: the voice inside it, an export before a checkpoint
            var zip = Path.Combine(dir, "Pack.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var s = z.CreateEntry("Alice/model.pth").Open()) s.Write(new byte[] { 1 });
                using (var s = z.CreateEntry("Bob/model.onnx").Open()) s.Write(new byte[] { 2 });
            }
            var (ok, _) = VoiceLibrary.ImportZip(zip, out var added);
            Assert.True(ok);
            Assert.Equal(new[] { "Pack Alice.pth", "Pack Bob.onnx" }, added);
            Assert.Equal("Pack Bob.onnx", VoiceLibrary.PickAdopted(added));
            Assert.Equal("Only.pth", VoiceLibrary.PickAdopted(new[] { "Only.pth" }));
            Assert.Null(VoiceLibrary.PickAdopted(Array.Empty<string>()));

            // the streamed route (drop zone, Hub) says the same: the zip's content, never the zip
            var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                using (var s = z.CreateEntry("model.pth").Open()) s.Write(new byte[] { 7 });
            var bytes = ms.ToArray();
            Assert.True(VoiceLibrary.BeginImport("Cat Voice.zip", bytes.Length).Ok);
            Assert.True(VoiceLibrary.AppendImport("Cat Voice.zip", bytes).Ok);
            var (ok2, message2) = VoiceLibrary.EndImport("Cat Voice.zip", out var landed);
            Assert.True(ok2, message2);
            Assert.Equal("Cat Voice.pth", landed);
            Assert.True(VoiceLibrary.BeginImport("Nova.onnx", 1).Ok);
            Assert.True(VoiceLibrary.AppendImport("Nova.onnx", new byte[] { 1 }).Ok);
            Assert.True(VoiceLibrary.EndImport("Nova.onnx", out landed).Ok);
            Assert.Equal("Nova.onnx", landed);
            Assert.False(VoiceLibrary.EndImport("never.pth", out landed).Ok);
            Assert.Null(landed);
        }
        finally { Directory.Delete(dir, true); }
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

    private static bool SameBytes(string a, string b) => File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    [Fact]
    public void APickedVoiceBringsTheIndexBesideIt()
    {
        var dir = TempData();
        try
        {
            var src = Path.Combine(dir, "picked", "SpongeBob");
            Directory.CreateDirectory(src);
            File.WriteAllBytes(Path.Combine(src, "model.pth"), new byte[] { 1, 2, 3 });
            var added = Path.Combine(src, "added_IVF280_Flat_nprobe_1_SpongeBob_v2.index");
            FeatureIndexTests.WriteSmallIndex(added, 1f);
            FeatureIndexTests.WriteSmallIndex(Path.Combine(src, "trained_IVF280_Flat_nprobe_1_SpongeBob_v2.index"), 2f);
            var (ok, message) = VoiceLibrary.Import(Path.Combine(src, "model.pth"), out var id);
            Assert.True(ok, message);
            Assert.Equal("SpongeBob.pth", id);
            Assert.Contains("with its index", message);
            Assert.True(VoiceLibrary.HasIndex("SpongeBob.onnx"));                 // the conversion shares it
            Assert.True(SameBytes(added, VoiceLibrary.IndexPathFor(id!)));      // the "added" one, not "trained"

            // a voice picked out of Downloads takes no index that does not name it
            var downloads = Path.Combine(dir, "Downloads");
            Directory.CreateDirectory(downloads);
            File.WriteAllBytes(Path.Combine(downloads, "Gollum.pth"), new byte[] { 4 });
            FeatureIndexTests.WriteSmallIndex(Path.Combine(downloads, "added_Someone_Else_v2.index"), 3f);
            Assert.True(VoiceLibrary.Import(Path.Combine(downloads, "Gollum.pth"), out var g).Ok);
            Assert.False(VoiceLibrary.HasIndex(g!));
            FeatureIndexTests.WriteSmallIndex(Path.Combine(downloads, "added_IVF_Gollum_v2.index"), 4f);
            File.WriteAllBytes(Path.Combine(downloads, "Gollum.pth"), new byte[] { 4 });
            var (ok2, again) = VoiceLibrary.Import(Path.Combine(downloads, "Gollum.pth"), out _);
            Assert.True(ok2);
            Assert.Contains("already in your library with its index", again);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AZipPairsEachIndexWithTheVoiceInItsFolder()
    {
        var dir = TempData();
        try
        {
            var ia = Path.Combine(dir, "a.index"); var ib = Path.Combine(dir, "b.index");
            FeatureIndexTests.WriteSmallIndex(ia, 1f);
            FeatureIndexTests.WriteSmallIndex(ib, 2f);
            var zip = Path.Combine(dir, "Two voices.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var s = z.CreateEntry("Alpha/Alpha.pth").Open()) s.Write(new byte[] { 1 });
                z.CreateEntryFromFile(ia, "Alpha/added_IVF1_Flat_Alpha_v2.index");
                using (var s = z.CreateEntry("Beta/Beta.pth").Open()) s.Write(new byte[] { 2 });
                z.CreateEntryFromFile(ib, "Beta/added_IVF1_Flat_Beta_v2.index");
            }
            var (ok, message) = VoiceLibrary.Import(zip);
            Assert.True(ok, message);
            Assert.True(SameBytes(ia, VoiceLibrary.IndexPathFor("Alpha.pth")));
            Assert.True(SameBytes(ib, VoiceLibrary.IndexPathFor("Beta.pth")));
            Assert.DoesNotContain(Directory.GetFiles(VoiceLibrary.Dir), f => f.EndsWith(".importing"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AnIndexGoesWithItsVoiceAndIsNeverHandedToANewcomer()
    {
        var dir = TempData();
        try
        {
            Directory.CreateDirectory(VoiceLibrary.Dir);
            File.WriteAllBytes(Path.Combine(VoiceLibrary.Dir, "Nova.pth"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(VoiceLibrary.Dir, "Nova.onnx"), new byte[] { 2 });
            var idx = Path.Combine(dir, "x.index");
            FeatureIndexTests.WriteSmallIndex(idx, 1f);
            Assert.True(VoiceLibrary.AttachIndex(idx, "Nova.onnx").Ok);
            Assert.True(VoiceLibrary.Delete("Nova.onnx"));
            Assert.True(VoiceLibrary.HasIndex("Nova.pth"));     // the checkpoint is still there
            Assert.True(VoiceLibrary.Delete("Nova.pth"));
            Assert.False(File.Exists(VoiceLibrary.IndexPathFor("Nova.pth")));

            // an index left behind (its voice deleted by hand) keeps its name from a newcomer
            FeatureIndexTests.WriteSmallIndex(Path.Combine(VoiceLibrary.Dir, "Echo.index"), 1f);
            Assert.Equal("Echo 2.pth", VoiceLibrary.FreeName("Echo.pth", Path.Combine(dir, "nothing")));

            // not an index: refused, nothing attached
            File.WriteAllText(Path.Combine(dir, "junk.index"), "junk");
            File.WriteAllBytes(Path.Combine(VoiceLibrary.Dir, "Mira.onnx"), new byte[] { 3 });
            Assert.False(VoiceLibrary.AttachIndex(Path.Combine(dir, "junk.index"), "Mira.onnx").Ok);
            Assert.False(VoiceLibrary.HasIndex("Mira.onnx"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AStreamedIndexWaitsToBeAttached()
    {
        var dir = TempData();
        try
        {
            var idx = Path.Combine(dir, "src.index");
            FeatureIndexTests.WriteSmallIndex(idx, 1f);
            var bytes = File.ReadAllBytes(idx);
            Assert.True(VoiceLibrary.BeginImport("added_x.index", bytes.Length).Ok);
            Assert.True(VoiceLibrary.AppendImport("added_x.index", bytes).Ok);
            var (ok, _) = VoiceLibrary.EndImport("added_x.index", out var added);
            Assert.True(ok);
            Assert.Equal("added_x.index", added);
            Assert.True(File.Exists(VoiceLibrary.ImportingPath("added_x.index")));   // for the controller to attach
            Assert.Empty(VoiceLibrary.Scan());                                          // never listed as a voice

            Assert.True(VoiceLibrary.BeginImport("bad.index", 4).Ok);
            Assert.True(VoiceLibrary.AppendImport("bad.index", new byte[] { 1, 2, 3, 4 }).Ok);
            Assert.False(VoiceLibrary.EndImport("bad.index", out _).Ok);
            Assert.False(File.Exists(VoiceLibrary.ImportingPath("bad.index")));
        }
        finally { Directory.Delete(dir, true); }
    }

    // An ONNX file that holds only metadata_props: enough for the reader.
    private static void WriteMetadataOnly(string path, string json)
    {
        static byte[] Field(int number, byte[] payload)
        {
            var bytes = new List<byte> { (byte)(number << 3 | 2) };
            int n = payload.Length;
            while (n >= 0x80) { bytes.Add((byte)(n | 0x80)); n >>= 7; }
            bytes.Add((byte)n);
            bytes.AddRange(payload);
            return bytes.ToArray();
        }
        var entry = Field(1, System.Text.Encoding.UTF8.GetBytes("morphonic")).Concat(Field(2, System.Text.Encoding.UTF8.GetBytes(json))).ToArray();
        File.WriteAllBytes(path, Field(14, entry));
    }

    [Fact]
    public void VoicesConvertedBeforeTheTemplateFixAreFound()
    {
        var dir = TempData();
        try
        {
            Directory.CreateDirectory(VoiceLibrary.Dir);
            string P(string n) => Path.Combine(VoiceLibrary.Dir, n);
            WriteMetadataOnly(P("Old.onnx"), "{\"format\":\"morphonic-rvc-voice\",\"exporter\":\"in-app\",\"sr\":40000}");
            File.WriteAllBytes(P("Old.pth"), new byte[] { 1 });
            WriteMetadataOnly(P("Lost.onnx"), "{\"format\":\"morphonic-rvc-voice\",\"exporter\":\"in-app\",\"sr\":40000}");
            WriteMetadataOnly(P("New.onnx"), "{\"format\":\"morphonic-rvc-voice\",\"exporter\":\"in-app\",\"assembly\":2}");
            File.WriteAllBytes(P("New.pth"), new byte[] { 1 });
            WriteMetadataOnly(P("Python.onnx"), "{\"format\":\"morphonic-rvc-voice\",\"exporter\":\"morphonic_export.py\"}");
            File.WriteAllBytes(P("Plain.onnx"), new byte[] { 8, 1 });
            var (rebuild, orphaned) = VoiceLibrary.StaleConversions();
            Assert.Equal(new[] { "Old.onnx" }, rebuild);
            Assert.Equal(new[] { "Lost.onnx" }, orphaned);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void FindVoicesFetchesTheIndexThatGoesWithTheVoice()
    {
        HubFile F(string p, long size = 10) => new(p, size, null);
        // one voice, its index in the root: taken; "added" over "trained"
        Assert.Equal("added_IVF_x_v2.index", VoiceHub.PickIndex(new[] { F("model.pth"), F("trained_IVF_x_v2.index", 99), F("added_IVF_x_v2.index") }, "model.pth")!.Path);
        // several voices: only the index in the voice's own folder
        var many = new[] { F("a/model.pth"), F("a/a.index"), F("b/model.pth"), F("b/b.index") };
        Assert.Equal("b/b.index", VoiceHub.PickIndex(many, "b/model.pth")!.Path);
        Assert.Null(VoiceHub.PickIndex(new[] { F("a/model.pth"), F("b/model.pth"), F("b/b.index") }, "a/model.pth"));
        Assert.Null(VoiceHub.PickIndex(new[] { F("model.pth") }, "model.pth"));
    }
}
