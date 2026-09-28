using ProGPU.Backend;
using ProGPU.Fonts.Inter;
using ProGPU.Text;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class GpuCoverageAtlasAlignmentTests
{
    private static WgpuContext CreateContext()
    {
        var context = new WgpuContext { ComputeExecutionPreference = GpuComputeExecutionPreference.NativeCompute };
        context.Initialize(null);
        Assert.Equal(GpuComputeExecutionPath.NativeCompute, context.GlyphRasterizationPath);
        return context;
    }

    [Fact]
    public void OddHeightPathBatchMatchesIndependentRastersExactly()
    {
        using var context = CreateContext();
        using var batch = new PathAtlas(context, atlasSize: 128);
        using var reference = new PathAtlas(context, atlasSize: 128);
        var paths = Enumerable.Range(0, 4)
            .Select(i => PrimitivePathGeometry.CreateRectangle(i * 32f, 0, 9, 35)).ToArray();
        foreach (var path in paths)
        {
            var actual = batch.GetOrCreatePath(path, 1);
            var expected = reference.GetOrCreatePath(path, 1);
            Assert.Equal(43u, actual.Height); // 256 * 43 leaves a 256-mod-512 next offset.
            Assert.Equal((expected.X, expected.Y, expected.Width, expected.Height),
                         (actual.X, actual.Y, actual.Width, actual.Height));
            reference.RasterizePendingPaths();
        }
        batch.RasterizePendingPaths();
        Assert.Equal(44800u, batch.LastRasterStagingBytes); // 4 * 11008 + three 256-byte gaps.
        byte[] pixels = batch.AtlasTexture.ReadPixels();
        Assert.Contains(pixels, value => value != 0);
        Assert.Equal(reference.AtlasTexture.ReadPixels(), pixels);
    }

    [Theory]
    [InlineData(768u)] // Oversized individual glyphs.
    [InlineData(11008u)] // Ring capacity is legal but is not a multiple of 512.
    [InlineData(65536u)] // Multiple odd-height slices share one batch.
    public void GlyphBatchAndRingRolloverMatchImmediateRasters(uint capacity)
    {
        using var context = CreateContext();
        using var batch = new GlyphAtlas(context, atlasSize: 512, colorAtlasSize: 64,
            initialColorAtlasSize: 64, uniformRingBufferSize: 4096, coverageRingBufferSize: capacity);
        using var reference = new GlyphAtlas(context, atlasSize: 512);
        bool oddHeight = false;
        batch.BeginBatch();
        try
        {
            foreach (char character in "AbgipQxyz0123")
            {
                var actual = batch.GetOrCreateGlyph(InterFontFamily.Regular, character, 31);
                var expected = reference.GetOrCreateGlyph(InterFontFamily.Regular, character, 31);
                oddHeight |= actual.Height % 2 != 0;
                Assert.Equal((expected.X, expected.Y, expected.Width, expected.Height),
                             (actual.X, actual.Y, actual.Width, actual.Height));
            }
        }
        finally { batch.EndBatch(); }
        Assert.True(oddHeight);
        byte[] pixels = batch.AtlasTexture.ReadPixels();
        Assert.Contains(pixels, value => value != 0);
        Assert.Equal(reference.AtlasTexture.ReadPixels(), pixels);
    }

    [Fact]
    public unsafe void MisalignedCoverageCopyIsRejectedBeforeEncoding()
    {
        using var context = CreateContext();
        using var source = new GpuBuffer(context, 1024, BufferUsage.CopySrc);
        using var target = new GpuTexture(context, 8, 8, TextureFormat.R8Unorm, TextureUsage.CopyDst);
        var descriptor = new CommandEncoderDescriptor();
        var encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &descriptor);
        Assert.NotEqual(0, (nint)encoder);
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                GpuCoverageUpload.RecordCopy(context, encoder, source, 256, 256, target, 0, 0, 8, 1));
            Assert.Equal("sourceOffset", error.ParamName);
            Assert.False(context.IsDeviceLost);
        }
        finally { context.Api.CommandEncoderRelease(encoder); }
    }
}
