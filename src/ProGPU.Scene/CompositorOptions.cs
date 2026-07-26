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

    public uint PathAtlasSize { get; init; } = 4096;

    public long PathAtlasCpuCacheBudgetBytes { get; init; } =
        PathAtlas.DefaultCompiledPathCacheBudgetBytes;

    public uint InitialVertexCount { get; init; } = 16384;

    public uint InitialIndexCount { get; init; } = 24576;

    public uint InitialBrushCount { get; init; } = 64;

    public uint InitialGradientStopCount { get; init; } = 512;

    public bool EnableGpuHitTesting { get; init; } = true;

    public bool EnableCompiledSceneCache { get; init; } = true;

    /// <summary>
    /// Reuses immutable local command compilation pages when another visual in
    /// the retained tree changes. Unsupported composition scopes fail closed to
    /// ordinary compilation.
    /// </summary>
    public bool EnableIncrementalScenePages { get; init; } = true;

    /// <summary>
    /// Bounds CPU-resident incremental compilation pages per compositor.
    /// </summary>
    public int MaximumIncrementalScenePages { get; init; } = 512;

    /// <summary>
    /// Bounds cached placement variants for one retained visual. A visual that
    /// exceeds this limit is treated as composition-volatile for a bounded
    /// cooldown instead of allocating a new page for every animation sample.
    /// </summary>
    public int MaximumIncrementalScenePageVariantsPerVisual { get; init; } = 2;

    /// <summary>
    /// Number of compositor frames before a composition-volatile visual may
    /// attempt to cache a stable placement again.
    /// </summary>
    public int IncrementalScenePageVolatilityCooldownFrames { get; init; } = 600;

    /// <summary>
    /// Bounds inactive R8 mask textures retained for reuse. Active masks are
    /// never dropped; surplus returned textures are released after submission.
    /// </summary>
    public int MaximumPooledMaskTextures { get; init; } = 128;

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
        if (MaximumIncrementalScenePages <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumIncrementalScenePages));
        }
        if (MaximumIncrementalScenePageVariantsPerVisual <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumIncrementalScenePageVariantsPerVisual));
        }
        if (IncrementalScenePageVolatilityCooldownFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(IncrementalScenePageVolatilityCooldownFrames));
        }
        if (MaximumPooledMaskTextures <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumPooledMaskTextures));
        }
    }
}
