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
}

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
    ReadOnlyMemory<ushort> GlyphIndices { get; }
    ReadOnlyMemory<Vector2> GlyphPositions { get; }
    PortableRect InkBounds { get; }
    PortableRect BaselineRelativeInkBounds { get; }
    IPortableHintedGlyphRunBinding Retain();
    IPortableHintedTextGlyphRun AcquireGlyphRun();
}
