using System.Numerics;

namespace ProGPU.Wpf.Interop;

/// <summary>Optional source transport. A successful acquisition returns an independent owned reference.</summary>
public interface IPortableHintedGlyphRunSource
{
    bool TryAcquirePortableHintedGlyphRun(out IPortableHintedGlyphRunBinding? binding);
}

/// <summary>Explicit publication capability, separate from ordinary design-font glyph runs.</summary>
public interface IPortableHintedGlyphRunBindingFactory
{
    IPortableHintedGlyphRunBinding BindGlyphRun(float sourceEmSize, Vector2 logicalOrigin);

    /// <summary>
    /// Admits the source's exact original face bytes/index/UPM and per-occurrence
    /// measured advances before publication. Inputs are borrowed synchronously;
    /// this does not validate WPF-specific offsets or admit caret/source frames.
    /// </summary>
    IPortableHintedGlyphRunBinding BindGlyphRun(PortableTextFont sourceFont, float sourceEmSize,
        Vector2 logicalOrigin, ReadOnlySpan<double> sourceAdvances)
        => throw new NotSupportedException("The hinted provider does not validate original source font and advance identity.");

    /// <summary>
    /// Validates the existing horizontal nominal-design offset convention and
    /// one original writer line. Source baseline is NOT a paragraph translation.
    /// Inputs are borrowed synchronously; this does not admit Display rounding.
    /// </summary>
    IPortableHintedGlyphRunBinding BindGlyphRun(PortableTextFont sourceFont, float sourceEmSize,
        Vector2 sourceBaselineOrigin, ReadOnlySpan<double> sourceAdvances, ReadOnlySpan<PortablePoint> sourceOffsets)
        => throw new NotSupportedException("The hinted provider does not validate original source offsets and line frames.");
}

/// <summary>Original writer frame retained by the owning binding; not synthesized source interaction.</summary>
public readonly record struct PortableHintedGlyphSourceFrame(int LineIndex, float ParagraphBaselineY, Vector2 BaselineOrigin);

/// <summary>
/// Owns an exact original occurrence selection. Positions are original paragraph-local
/// coordinates; origin is a drawing translation, never reshaping or pixel snapping.
/// No native font alias is exposed. Empty ink is distinct from an empty selection.
/// </summary>
public interface IPortableHintedGlyphRunBinding : IDisposable
{
    bool IsDisposed { get; }
    float FontRenderingEmSize { get; }
    float DpiScale { get; }
    sbyte BidiLevel { get; }
    Vector2 Origin { get; }
    /// <summary>Available only from complete explicit source-frame validation.</summary>
    PortableHintedGlyphSourceFrame SourceFrame
        => throw new NotSupportedException("This binding has no validated original source line frame.");
    ReadOnlyMemory<ushort> GlyphIndices { get; }
    ReadOnlyMemory<Vector2> GlyphPositions { get; }
    PortableRect InkBounds { get; }
    PortableRect BaselineRelativeInkBounds { get; }
    IPortableHintedGlyphRunBinding Retain();
    IPortableHintedTextGlyphRun AcquireGlyphRun();
}
