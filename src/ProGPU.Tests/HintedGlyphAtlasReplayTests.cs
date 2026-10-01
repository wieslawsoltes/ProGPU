using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Text;
using Xunit;

namespace ProGPU.Tests;

// Authored synthetic physical records, not a native producer/font qualification.
// CPU controls need no device; atlas fixtures deliberately require a real GPU
// context even when its captured coverage execution policy is explicitly CPU.
public sealed class HintedGlyphAtlasReplayTests
{
    [Fact]
    public void OriginalSelectedSegmentsIgnorePoisonAndRemainByteExact()
    {
        using var geometry = CreateGeometry();
        byte[][] before = Snapshot(geometry);
        GpuGlyphRecord record = geometry.RenderOutlines[1];
        Assert.True(record.StartSegment > 0);
        Assert.True(record.StartSegment + record.SegmentCount < geometry.SegmentCount);
        var bounds = GlyphAtlas.GetHintedRasterBounds(in record);
        byte[] scalar = Coverage(geometry, record, bounds, useSimd: false);
        byte[] simd = Coverage(geometry, record, bounds, useSimd: true);
        GpuSegment[] selected = geometry.RenderSegments.Slice(
            checked((int)record.StartSegment), checked((int)record.SegmentCount)).ToArray();
        record.StartSegment = 0;
        byte[] independent = GlyphAtlas.RasterizeGlyphCoverageCpu(selected, record,
            bounds.XStart, bounds.YStart, 1f, 0f, bounds.Width, bounds.Height, useSimd: false);
        Assert.Equal(independent, scalar);
        Assert.Equal(scalar, simd);
        Assert.Contains(scalar, value => value == 0);
        Assert.Contains(scalar, value => value == 255);
        Assert.Contains(scalar, value => value is > 0 and < 255);
        AssertSnapshot(before, geometry);
    }

    [Fact]
    public void PhysicalBoundsUseExactFourPixelPaddingAndYInversion()
    {
        var record = new GpuGlyphRecord
        {
            StartSegment = 3, SegmentCount = 4,
            MinX = -1.25f, MinY = -4.5f, MaxX = 7.75f, MaxY = 9.25f
        };
        GlyphAtlas.ValidateHintedRasterRecord(in record, 7);
        Assert.Equal(new GlyphAtlas.HintedRasterBounds(-6, -14, 18, 23),
            GlyphAtlas.GetHintedRasterBounds(in record));
        record.MinX = float.MinValue;
        Assert.Throws<OverflowException>(() => GlyphAtlas.GetHintedRasterBounds(in record));
        record.MinX = 0f;
        record.MaxX = float.MaxValue;
        Assert.Throws<OverflowException>(() => GlyphAtlas.GetHintedRasterBounds(in record));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void InvalidOriginalRecordIsRejectedBeforeRasterization(int invalid)
    {
        var record = new GpuGlyphRecord { SegmentCount = 4, MaxX = 8, MaxY = 8 };
        int count = 4;
        switch (invalid)
        {
            case 0: count = -1; break;
            case 1: record.SegmentCount = 0; break;
            case 2: record.StartSegment = uint.MaxValue; break;
            case 3: record.SegmentCount = 5; break;
            case 4: record.MinX = float.NaN; break;
            case 5: record.MinY = 9; break;
        }
        Assert.Throws<InvalidOperationException>(() => GlyphAtlas.ValidateHintedRasterRecord(in record, count));
    }

    [Fact]
    public void AtlasIdentityKeepsExactGeometryReferenceAndFullUintSlot()
    {
        using var first = CreateGeometry();
        using var second = CreateGeometry();
        var key = new GlyphAtlas.GlyphKey(first, 0);
        Assert.Equal(key, new GlyphAtlas.GlyphKey(first, 0));
        Assert.Equal(key.GetHashCode(), new GlyphAtlas.GlyphKey(first, 0).GetHashCode());
        Assert.NotEqual(key, new GlyphAtlas.GlyphKey(second, 0));
        Assert.NotEqual(key, new GlyphAtlas.GlyphKey(first, 1));
        Assert.NotEqual(new GlyphAtlas.GlyphKey(first, ushort.MaxValue),
            new GlyphAtlas.GlyphKey(first, uint.MaxValue));
        Assert.Equal(24, Unsafe.SizeOf<GlyphAtlas.GlyphKey>());
    }

    [Fact]
    public void ReplayTargetAdmitsOnlyExactDpiAndUnchangedFiniteTranslation()
    {
        var translation = Matrix4x4.CreateTranslation(0.375f, -7.625f, 0f);
        Compositor.ValidateHintedReplayTarget(2f, 2f, translation, 1f);
        Assert.Throws<NotSupportedException>(() => Compositor.ValidateHintedReplayTarget(2f, float.BitIncrement(2f), translation, 1f));
        Assert.Throws<NotSupportedException>(() => Compositor.ValidateHintedReplayTarget(0f, 0f, translation, 1f));
        Assert.Throws<NotSupportedException>(() => Compositor.ValidateHintedReplayTarget(float.NaN, float.NaN, translation, 1f));
        Assert.Throws<NotSupportedException>(() => Compositor.ValidateHintedReplayTarget(2f, 2f, translation, float.BitIncrement(1f)));
        foreach (Matrix4x4 denied in new[]
        {
            Matrix4x4.CreateScale(float.BitIncrement(1f)), Matrix4x4.CreateRotationZ(0.01f),
            Matrix4x4.CreateTranslation(0, 0, 1), Matrix4x4.CreateTranslation(float.NaN, 0, 0),
            Matrix4x4.CreateTranslation(0, float.PositiveInfinity, 0),
            new Matrix4x4(1, 0.25f, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)
        })
            Assert.Throws<NotSupportedException>(() => Compositor.ValidateHintedReplayTarget(2f, 2f, denied, 1f));
    }

    [Theory]
    [InlineData(GpuComputeExecutionPreference.NativeCompute, 768u)]
    [InlineData(GpuComputeExecutionPreference.NativeCompute, 11008u)]
    [InlineData(GpuComputeExecutionPreference.NativeCompute, 65536u)]
    [InlineData(GpuComputeExecutionPreference.RasterShader, 768u)]
    [InlineData(GpuComputeExecutionPreference.RasterShader, 11008u)]
    [InlineData(GpuComputeExecutionPreference.RasterShader, 65536u)]
    [InlineData(GpuComputeExecutionPreference.IntrinsicSimdCpu, 768u)]
    [InlineData(GpuComputeExecutionPreference.IntrinsicSimdCpu, 11008u)]
    [InlineData(GpuComputeExecutionPreference.IntrinsicSimdCpu, 65536u)]
    [InlineData(GpuComputeExecutionPreference.ScalarCpu, 768u)]
    [InlineData(GpuComputeExecutionPreference.ScalarCpu, 11008u)]
    [InlineData(GpuComputeExecutionPreference.ScalarCpu, 65536u)]
    public void AtlasImmediateBatchRolloverWarmAndClearMatchEveryScalarByte(
        GpuComputeExecutionPreference preference, uint coverageCapacity)
    {
        using var context = CreateContext(preference);
        using var geometry = CreateGeometry();
        using var retained = geometry.RetainForRecording();
        byte[][] original = Snapshot(geometry);
        geometry.Dispose(); // The explicit recorded owner, not the caller, admits replay.
        using var immediate = CreateAtlas(context, 256, coverageCapacity);
        using var batch = CreateAtlas(context, 256, coverageCapacity);
        Assert.Equal(0, context.CachedDeviceShaderModuleCount);
        Assert.Equal(0, context.CachedDeviceComputePipelineCount);
        Assert.Equal(0, context.CachedDeviceRenderPipelineCount);
        context.ComputeExecutionPreference = GpuComputeExecutionPreference.ScalarCpu;
        // Each atlas must retain the original explicitly selected policy.
        GlyphInfo[] expectedInfos = Rasterize(immediate, geometry, batched: false);
        GlyphInfo[] actualInfos = Rasterize(batch, geometry, batched: true);
        Assert.Equal(expectedInfos, actualInfos);
        Assert.All(actualInfos, info => Assert.Equal(43u, info.Height));
        byte[] expected = ExpectedAtlas(geometry, actualInfos, 256);
        Assert.Contains(expected, value => value != 0);
        Assert.Equal(expected, immediate.AtlasTexture.ReadPixels());
        Assert.Equal(expected, batch.AtlasTexture.ReadPixels());
        bool gpu = preference is GpuComputeExecutionPreference.NativeCompute or GpuComputeExecutionPreference.RasterShader;
        Assert.Equal(gpu ? geometry.OutlineCount : 0, batch.CompiledGpuGlyphCount);
        Assert.Equal(gpu ? 1 : 0, context.CachedDeviceShaderModuleCount);
        Assert.Equal(preference == GpuComputeExecutionPreference.NativeCompute ? 1 : 0, context.CachedDeviceComputePipelineCount);
        Assert.Equal(preference == GpuComputeExecutionPreference.RasterShader ? 1 : 0, context.CachedDeviceRenderPipelineCount);
        if (gpu && (preference == GpuComputeExecutionPreference.RasterShader || coverageCapacity >= 11008))
            Assert.True(batch.RasterBatchSubmissionCount > 1); // Two-slice uniform ring really rolled over.

        var before = Counters(batch);
        ulong generation = batch.Generation;
        GlyphAtlas.ResidencySet residency;
        batch.BeginBatch();
        try
        {
            for (uint index = 0; index < actualInfos.Length; index++)
                Assert.Equal(actualInfos[index], batch.GetOrCreateHintedGlyph(geometry, index));
            residency = batch.CaptureBatchResidency(0);
            Assert.Equal(geometry.OutlineCount, residency.Count);
            Assert.True(batch.TryMarkRetainedGlyphReplay(residency));
        }
        finally { batch.EndBatch(); }
        Assert.Equal(before, Counters(batch));
        Assert.Equal(generation, batch.Generation);
        Assert.Equal(expected, batch.AtlasTexture.ReadPixels());
        batch.Clear();
        Assert.Equal(generation + 1, batch.Generation);
        Assert.False(batch.TryMarkRetainedGlyphReplay(residency));
        Assert.Equal(0, batch.CachedGlyphCount);
        Assert.All(batch.AtlasTexture.ReadPixels(), value => Assert.Equal((byte)0, value));
        GlyphInfo[] restored = Rasterize(batch, geometry, batched: true);
        Assert.Equal(actualInfos, restored);
        Assert.Equal(expected, batch.AtlasTexture.ReadPixels());
        AssertSnapshot(original, geometry);
    }

    [Theory]
    [InlineData(GpuComputeExecutionPreference.NativeCompute)]
    [InlineData(GpuComputeExecutionPreference.RasterShader)]
    [InlineData(GpuComputeExecutionPreference.IntrinsicSimdCpu)]
    [InlineData(GpuComputeExecutionPreference.ScalarCpu)]
    public void LiveFrameRegionsSurviveCapacityFailureAndOldResidencyRejectsEviction(
        GpuComputeExecutionPreference preference)
    {
        using var context = CreateContext(preference);
        using var geometry = CreateGeometry(count: 2, width: 31, height: 21);
        using var retained = geometry.RetainForRecording();
        using var atlas = CreateAtlas(context, 64, 65536);
        GlyphInfo first;
        GlyphAtlas.ResidencySet residency;
        atlas.BeginBatch();
        try
        {
            first = atlas.GetOrCreateHintedGlyph(geometry, 0);
            residency = atlas.CaptureBatchResidency(0);
            ulong generation = atlas.Generation;
            Assert.Throws<InvalidOperationException>(() => atlas.GetOrCreateHintedGlyph(geometry, 1));
            Assert.Equal(generation, atlas.Generation);
            Assert.True(atlas.TryMarkRetainedGlyphReplay(residency));
            Assert.Equal(first, atlas.GetOrCreateHintedGlyph(geometry, 0));
        }
        finally { atlas.EndBatch(); }
        Assert.Equal(ExpectedAtlas(geometry, [first], 64), atlas.AtlasTexture.ReadPixels());
        ulong priorGeneration = atlas.Generation;
        atlas.BeginBatch();
        GlyphInfo second;
        try
        {
            second = atlas.GetOrCreateHintedGlyph(geometry, 1);
            Assert.Equal((first.X, first.Y), (second.X, second.Y));
            Assert.False(atlas.TryMarkRetainedGlyphReplay(residency));
        }
        finally { atlas.EndBatch(); }
        Assert.Equal(priorGeneration + 1, atlas.Generation);
        Assert.Equal(1UL, atlas.EvictionCount);
        Assert.Equal(1, atlas.CachedGlyphCount);
        Assert.False(atlas.CapacityExceeded);
        Assert.Equal(ExpectedAtlas(geometry, [second], 64, firstOutline: 1), atlas.AtlasTexture.ReadPixels());
        var before = Counters(atlas);
        Assert.Equal(second, atlas.GetOrCreateHintedGlyph(geometry, 1));
        Assert.Equal(before, Counters(atlas));
    }

    private static WgpuContext CreateContext(GpuComputeExecutionPreference preference)
    {
        var context = new WgpuContext { ComputeExecutionPreference = preference };
        context.Initialize(null);
        var expected = preference switch
        {
            GpuComputeExecutionPreference.NativeCompute => GpuComputeExecutionPath.NativeCompute,
            GpuComputeExecutionPreference.RasterShader => GpuComputeExecutionPath.RasterShader,
            GpuComputeExecutionPreference.IntrinsicSimdCpu => GpuComputeExecutionPath.IntrinsicSimdCpu,
            GpuComputeExecutionPreference.ScalarCpu => GpuComputeExecutionPath.ScalarCpu,
            _ => throw new ArgumentOutOfRangeException(nameof(preference))
        };
        Assert.Equal(expected, context.GlyphRasterizationPath);
        Console.WriteLine($"Hinted atlas fixture: provider={context.BackendKind}, backend={context.AdapterBackendType}, adapter={context.AdapterName}, path={context.GlyphRasterizationPath}");
        return context;
    }

    private static GlyphAtlas CreateAtlas(WgpuContext context, uint size, uint coverageCapacity) =>
        new(context, atlasSize: size, initialAtlasSize: size, colorAtlasSize: 64,
            initialColorAtlasSize: 64, uniformRingBufferSize: 512, coverageRingBufferSize: coverageCapacity);

    private static GlyphInfo[] Rasterize(GlyphAtlas atlas, HintedGlyphGeometry geometry, bool batched)
    {
        var infos = new GlyphInfo[geometry.OutlineCount];
        if (batched) atlas.BeginBatch();
        try
        {
            for (uint index = 0; index < infos.Length; index++)
                infos[index] = atlas.GetOrCreateHintedGlyph(geometry, index);
        }
        finally { if (batched) atlas.EndBatch(); }
        return infos;
    }

    private static (int, int, int, ulong, ulong, ulong, ulong) Counters(GlyphAtlas atlas) =>
        (atlas.CachedGlyphCount, atlas.CompiledGpuGlyphCount, atlas.CompiledGpuSegmentCount,
            atlas.OutlineUploadWriteCount, atlas.UniformUploadWriteCount,
            atlas.RasterBatchSubmissionCount, atlas.RasterBindGroupCreationCount);

    private static byte[] ExpectedAtlas(HintedGlyphGeometry geometry, GlyphInfo[] infos,
        uint atlasSize, int firstOutline = 0)
    {
        byte[] expected = new byte[checked((int)(atlasSize * atlasSize))];
        for (int index = 0; index < infos.Length; index++)
        {
            GpuGlyphRecord record = geometry.RenderOutlines[firstOutline + index];
            var bounds = GlyphAtlas.GetHintedRasterBounds(in record);
            byte[] coverage = Coverage(geometry, record, bounds, useSimd: false);
            GlyphInfo info = infos[index];
            Assert.Equal((bounds.Width, bounds.Height), (info.Width, info.Height));
            Assert.Equal((float)bounds.XStart, info.BearX);
            Assert.Equal((float)bounds.YStart, info.BearY);
            Assert.Equal(1f, info.RasterScale);
            for (uint row = 0; row < info.Height; row++)
                coverage.AsSpan(checked((int)(row * info.Width)), checked((int)info.Width)).CopyTo(
                    expected.AsSpan(checked((int)((info.Y + row) * atlasSize + info.X))));
        }
        return expected;
    }

    private static byte[] Coverage(HintedGlyphGeometry geometry, GpuGlyphRecord record,
        GlyphAtlas.HintedRasterBounds bounds, bool useSimd) =>
        GlyphAtlas.RasterizeGlyphCoverageCpu(geometry.RenderSegments, record,
            bounds.XStart, bounds.YStart, 1f, 0f, bounds.Width, bounds.Height, useSimd);

    private static byte[][] Snapshot(HintedGlyphGeometry geometry) =>
    [
        MemoryMarshal.AsBytes(geometry.RenderOutlines).ToArray(),
        MemoryMarshal.AsBytes(geometry.RenderSegments).ToArray(),
        MemoryMarshal.AsBytes(geometry.RenderOccurrences).ToArray()
    ];

    private static void AssertSnapshot(byte[][] expected, HintedGlyphGeometry geometry)
    {
        byte[][] actual = Snapshot(geometry);
        for (int index = 0; index < expected.Length; index++) Assert.Equal(expected[index], actual[index]);
    }

    private static HintedGlyphGeometry CreateGeometry(int count = 6, float width = 9, float height = 35)
    {
        var segments = new List<GpuSegment>();
        var outlines = new GpuGlyphRecord[count];
        var occurrences = new HintedGlyphOccurrence[count];
        AddRectangle(new(-100), new(100)); // Poison outside every selected range.
        for (int index = 0; index < count; index++)
        {
            Vector2 low = new(0.125f + index * 0.125f, 0.125f);
            Vector2 high = new(width - 0.125f, height - 0.125f);
            outlines[index] = new GpuGlyphRecord
            {
                StartSegment = checked((uint)segments.Count), SegmentCount = 4,
                MinX = low.X, MinY = low.Y, MaxX = high.X, MaxY = high.Y
            };
            AddRectangle(low, high);
            AddRectangle(new(-100), new(100));
            occurrences[index] = new HintedGlyphOccurrence((uint)index, (uint)index,
                0x1_0000u + (uint)index, 0, 0, (uint)index, (uint)index, (uint)index,
                (uint)index, index + 1, 0, new(0.375f + index, 0.625f), new(3.125f, 0));
        }
        return new HintedGlyphGeometry(1f, outlines, segments.ToArray(), occurrences, new SyntheticOwner());

        void AddRectangle(Vector2 low, Vector2 high)
        {
            segments.Add(Line(low, new(high.X, low.Y)));
            segments.Add(Line(new(high.X, low.Y), high));
            segments.Add(Line(high, new(low.X, high.Y)));
            segments.Add(Line(new(low.X, high.Y), low));
        }
    }

    private static GpuSegment Line(Vector2 from, Vector2 to) => new() { P0 = from, P1 = to, SegmentType = 0 };
    private sealed class SyntheticOwner : IDisposable { public void Dispose() { } }
}
