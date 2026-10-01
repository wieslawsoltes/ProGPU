using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeHintedPaintFailureReceiptTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("progpu-receipt-control-").FullName;
    private readonly NativeGlyphOutline[] _outlines = [new(0, 1, new(-2.125f, -12.375f), new(9.25f, .5f), 1)];
    private readonly NativePathSegment[] _segments = [new(NativePathSegmentKind.Quadratic, new(1, 2), new(3, 4), new(5, 6))];
    private readonly NativePositionedGlyph[] _glyphs = [new(0, new(4.0625f, 13.1875f), Vector2.UnitX, Vector2.UnitY, new(.25f, .75f, .5f, .46875f), .5f)];
    private readonly byte[] _writer = [5, 7, 9], _scene = [1, 3, 5, 7], _texel = [64, 192, 128, 255];
    private readonly byte[] _actual = new byte[96 * 96 * 4], _expected = new byte[96 * 96 * 4];
    private readonly NativeSceneGlyphPaint _paint = new(NativeSceneGlyphPaint.Texture, 0,
        NativeSceneGlyphPaint.BoundedTexture, new(0, 0, .46875f, 0), new(0, 0, 1, 1),
        new(0, 0, 48, 0), new(48, 48, 0, 48), new(0, .5f, 0, 0));

    private string Write(string? root = null, float dpi = 2, byte[]? actual = null, byte[]? texel = null) =>
        NativeHintedPaintFailureReceipt.Write(root ?? _root, "original exact pixel failure", new string('A', 64),
            dpi, 0, _outlines, _segments, _glyphs, _glyphs, _writer, _scene, _paint,
            texel ?? _texel, actual ?? _actual, _expected, "synthetic CPU control", "no device", "none", "none");

    [Fact]
    public void RawInputAndOutputBytesRoundTripWithMatchingManifestHashes()
    {
        _actual[2] = 47;
        string path = Write();
        Assert.Equal(MemoryMarshal.AsBytes(_outlines.AsSpan()).ToArray(), File.ReadAllBytes(Path.Combine(path, "outlines.bin")));
        Assert.Equal(MemoryMarshal.AsBytes(_segments.AsSpan()).ToArray(), File.ReadAllBytes(Path.Combine(path, "segments.bin")));
        Assert.Equal(MemoryMarshal.AsBytes(_glyphs.AsSpan()).ToArray(), File.ReadAllBytes(Path.Combine(path, "glyphs.bin")));
        Assert.Equal(_writer, File.ReadAllBytes(Path.Combine(path, "original-writer.bin")));
        Assert.Equal(_scene, File.ReadAllBytes(Path.Combine(path, "scene.bin")));
        Assert.Equal(_actual, File.ReadAllBytes(Path.Combine(path, "actual.rgba8")));
        Assert.Equal(_expected, File.ReadAllBytes(Path.Combine(path, "expected.rgba8")));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "receipt.json")));
        foreach (var file in json.RootElement.GetProperty("Files").EnumerateArray())
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(path, file.GetProperty("Name").GetString()!));
            Assert.Equal(bytes.Length, file.GetProperty("Bytes").GetInt32());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), file.GetProperty("Sha256").GetString());
        }
        Assert.Equal(12, json.RootElement.GetProperty("Files").GetArrayLength());
    }

    [Fact]
    public void ManifestPreservesBoundsPositionsAndExactPaintRecord()
    {
        string path = Write();
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "receipt.json")));
        var root = json.RootElement;
        Assert.Equal(-12.375f, root.GetProperty("Outlines")[0].GetProperty("Minimum")[1].GetSingle());
        Assert.Equal(13.1875f, root.GetProperty("Glyphs")[0].GetProperty("Position")[1].GetSingle());
        Assert.Equal(2f, root.GetProperty("Dpi").GetSingle());
        Assert.Equal(96, root.GetProperty("PaintRecordBytes").GetInt32());
        Assert.Equal("original exact pixel failure", root.GetProperty("Failure").GetString());
        Assert.False(root.GetProperty("RuntimeIdentityVerified").GetBoolean());
        Assert.Equal(0, root.GetProperty("LoadedLibraries").GetArrayLength());
        Assert.Equal(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in _paint, 1)).ToArray(),
            File.ReadAllBytes(Path.Combine(path, "paint.bin")));
    }

    [Fact]
    public void RepeatedCaptureUsesSeparateDirectoriesWithoutOverwriting()
    {
        string first = Write();
        byte[] original = File.ReadAllBytes(Path.Combine(first, "receipt.json"));
        string second = Write();
        Assert.NotEqual(first, second);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(first, "receipt.json")));
        Assert.True(File.Exists(Path.Combine(second, "receipt.json")));
    }

    [Fact]
    public void RelativeRootIsRejectedBeforeCreatingAReceipt() =>
        Assert.Throws<ArgumentException>(() => Write(root: "relative-receipt"));

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void WrongOutputLengthIsRejectedBeforePublication(int length)
    {
        Assert.Throws<ArgumentException>(() => Write(actual: new byte[length]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void WrongTexelLengthIsRejectedBeforePublication()
    {
        Assert.Throws<ArgumentException>(() => Write(texel: new byte[8]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void FailedJsonSerializationDoesNotPublishACompleteManifest()
    {
        Assert.Throws<ArgumentException>(() => Write(dpi: float.NaN));
        string path = Assert.Single(Directory.GetDirectories(_root));
        Assert.False(File.Exists(Path.Combine(path, "receipt.json")));
    }

    [Fact]
    public void CaptureFailureCannotEscapeTheOriginalAssertionHandler()
    {
        string? previous = Environment.GetEnvironmentVariable(NativeHintedPaintFailureReceipt.OutputVariable);
        try
        {
            Environment.SetEnvironmentVariable(NativeHintedPaintFailureReceipt.OutputVariable, _root);
            var original = new InvalidOperationException("original failure");
            // A null diagnostic context fails inside capture; no device is created.
            NativeHintedPaintFailureReceipt.TryCapture(null!, original, "not-a-font", 2, 0,
                _outlines, _segments, _glyphs, _glyphs, _writer, _scene, _paint, _texel, _actual, _expected);
            Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        }
        finally { Environment.SetEnvironmentVariable(NativeHintedPaintFailureReceipt.OutputVariable, previous); }
    }

    [Fact]
    public void ExactNativePathAndHashAreAccepted()
    {
        string path = Path.Combine(_root, "progpu_native.dll");
        NativeHintedPaintFailureReceipt.VerifyLibraryIdentity(path, new string('A', 64),
            path, new string('a', 64), requirePath: true);
    }

    [Fact]
    public void MatchingBytesFromWrongNativePathAreRejected() =>
        Assert.Throws<InvalidOperationException>(() => NativeHintedPaintFailureReceipt.VerifyLibraryIdentity(
            Path.Combine(_root, "unexpected", "progpu_native.dll"), new string('A', 64),
            Path.Combine(_root, "fresh", "progpu_native.dll"), new string('A', 64), requirePath: true));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/progpu_native.dll")]
    public void MissingOrRelativeExpectedNativePathIsRejected(string? expectedPath) =>
        Assert.Throws<InvalidOperationException>(() => NativeHintedPaintFailureReceipt.VerifyLibraryIdentity(
            Path.Combine(_root, "progpu_native.dll"), new string('A', 64),
            expectedPath, new string('A', 64), requirePath: true));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AAAA")]
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    public void MissingMalformedOrDifferentHashIsRejected(string? expectedHash) =>
        Assert.Throws<InvalidOperationException>(() => NativeHintedPaintFailureReceipt.VerifyLibraryIdentity(
            Path.Combine(_root, "wgpu_native.dll"), new string('A', 64),
            null, expectedHash, requirePath: false));

    [Fact]
    public void PinnedWebGpuBytesMayComeFromConsumerCopy() =>
        NativeHintedPaintFailureReceipt.VerifyLibraryIdentity(
            Path.Combine(_root, "consumer", "wgpu_native.dll"), new string('A', 64),
            null, new string('A', 64), requirePath: false);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
