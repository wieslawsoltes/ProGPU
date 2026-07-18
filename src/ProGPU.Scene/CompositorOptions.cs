namespace ProGPU.Scene;

public sealed record CompositorOptions
{
    public static CompositorOptions Default { get; } = new();

    public uint GlyphAtlasSize { get; init; } = 2048;

    public uint PathAtlasSize { get; init; } = 2048;

    public uint InitialVertexCount { get; init; } = 16384;

    public uint InitialIndexCount { get; init; } = 24576;

    public bool EnableGpuHitTesting { get; init; } = true;

    public bool EnableCompiledSceneCache { get; init; } = true;

    /// <summary>
    /// Enables experimental automatic promotion of text-only visuals into persistent fragment
    /// arenas. Disabled by default because recycled/scrolling text can increase draw fragmentation
    /// and GPU work even when CPU compilation decreases. Explicit scene fragments are unaffected.
    /// </summary>
    public bool EnableAutomaticTextFragments { get; init; }

    public uint PrimarySampleCount { get; init; } = 4;

    internal void Validate()
    {
        if (GlyphAtlasSize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(GlyphAtlasSize));
        }
        if (PathAtlasSize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PathAtlasSize));
        }
        if (InitialVertexCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialVertexCount));
        }
        if (InitialIndexCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialIndexCount));
        }
        if (PrimarySampleCount is not (1 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(PrimarySampleCount));
        }
    }
}
