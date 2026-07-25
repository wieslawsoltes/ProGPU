using ProGPU.Vector;
using ProGPU.Text;

namespace ProGPU.Scene;

public sealed record CompositorOptions
{
    public static CompositorOptions Default { get; } = new();

    public uint GlyphAtlasSize { get; init; } = 2048;

    public uint InitialGlyphAtlasSize { get; init; } = GlyphAtlas.DefaultInitialAtlasSize;

    public uint ColorGlyphAtlasSize { get; init; } = GlyphAtlas.DefaultColorAtlasSize;

    public uint InitialColorGlyphAtlasSize { get; init; } =
        GlyphAtlas.DefaultInitialColorAtlasSize;

    public uint GlyphUniformStagingBytes { get; init; } =
        GlyphAtlas.DefaultUniformRingBufferSize;

    public uint GlyphCoverageStagingBytes { get; init; } = GlyphAtlas.DefaultCoverageRingBufferSize;

    public uint PathAtlasSize { get; init; } = 2048;

    public long PathAtlasCpuCacheBudgetBytes { get; init; } =
        PathAtlas.DefaultCompiledPathCacheBudgetBytes;

    public uint InitialVertexCount { get; init; } = 16384;

    public uint InitialIndexCount { get; init; } = 24576;

    public uint InitialBrushCount { get; init; } = 64;

    public uint InitialGradientStopCount { get; init; } = 512;

    public bool EnableGpuHitTesting { get; init; } = true;

    public bool EnableCompiledSceneCache { get; init; } = true;

    public bool PrecompileBasePipelines { get; init; }

    public uint PrimarySampleCount { get; init; } = 4;

    internal void Validate()
    {
        if (GlyphAtlasSize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(GlyphAtlasSize));
        }
        if (InitialGlyphAtlasSize <= 4)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialGlyphAtlasSize));
        }
        if (ColorGlyphAtlasSize <= 4)
        {
            throw new ArgumentOutOfRangeException(nameof(ColorGlyphAtlasSize));
        }
        if (InitialColorGlyphAtlasSize <= 4)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialColorGlyphAtlasSize));
        }
        if (GlyphUniformStagingBytes < 256 || GlyphUniformStagingBytes % 256 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(GlyphUniformStagingBytes));
        }
        if (GlyphCoverageStagingBytes < 256 || GlyphCoverageStagingBytes % 256 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(GlyphCoverageStagingBytes));
        }
        if (PathAtlasSize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PathAtlasSize));
        }
        if (PathAtlasCpuCacheBudgetBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PathAtlasCpuCacheBudgetBytes));
        }
        if (InitialVertexCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialVertexCount));
        }
        if (InitialIndexCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialIndexCount));
        }
        if (InitialBrushCount == 0 || InitialBrushCount > Compositor.MaxBrushes)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialBrushCount));
        }
        if (InitialGradientStopCount == 0 ||
            InitialGradientStopCount > Compositor.MaxGradientStops)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialGradientStopCount));
        }
        if (PrimarySampleCount is not (1 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(PrimarySampleCount));
        }
    }
}
