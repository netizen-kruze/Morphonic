using Morphonic.Rvc;
using Xunit;

namespace Morphonic.Tests;

// The offline build's trailer: files appended to a binary, found again by
// the index, and dropped when a packed binary is packed again.
public class OfflinePayloadTests
{
    [Fact]
    public void PacksFilesBehindTheBinaryAndReadsThemBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "morphonic-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var basePath = Path.Combine(dir, "app.exe");
            var baseBytes = new byte[3000];
            new Random(1).NextBytes(baseBytes);
            File.WriteAllBytes(basePath, baseBytes);
            var a = Path.Combine(dir, "a.onnx");
            var b = Path.Combine(dir, "b.onnx");
            File.WriteAllBytes(a, Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
            File.WriteAllBytes(b, new byte[] { 9, 8, 7 });

            Assert.Null(OfflinePayload.Read(basePath));
            Assert.Equal(3000, OfflinePayload.BaseLength(basePath));

            var packed = Path.Combine(dir, "packed.exe");
            OfflinePayload.Pack(basePath, new[] { ("a", "a.onnx", a, "sha-a"), ("b", "b.onnx", b, "sha-b") }, packed);
            var index = OfflinePayload.Read(packed);
            Assert.NotNull(index);
            Assert.Equal(2, index!.Count);
            Assert.Equal(3000, index["a"].Offset);
            Assert.Equal(5000, index["a"].Length);
            Assert.Equal(8000, index["b"].Offset);
            Assert.Equal(3, index["b"].Length);
            Assert.Equal("sha-b", index["b"].Sha256);
            Assert.Equal(3000, OfflinePayload.BaseLength(packed));
            // the binary itself is untouched in front of the payload
            Assert.Equal(baseBytes, File.ReadAllBytes(packed).Take(3000).ToArray());

            // packing a packed binary replaces the payload instead of nesting it
            var repacked = Path.Combine(dir, "repacked.exe");
            OfflinePayload.Pack(packed, new[] { ("b", "b.onnx", b, "sha-b") }, repacked);
            var index2 = OfflinePayload.Read(repacked);
            Assert.Single(index2!);
            Assert.Equal(3000, index2!["b"].Offset);
            Assert.Equal(baseBytes, File.ReadAllBytes(repacked).Take(3000).ToArray());

            // a plain file with a stray trailing string is not a payload
            File.WriteAllText(Path.Combine(dir, "junk.bin"), "nothing here " + OfflinePayload.Magic);
            Assert.Null(OfflinePayload.Read(Path.Combine(dir, "junk.bin")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
