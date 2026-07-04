using System;
using System.Numerics;

namespace ProGPU.Wpf.Interop;

public interface IPortableGlyphRunSource
{
    bool TryGetPortableGlyphRun(out PortableGlyphRun glyphRun);
}

public interface IPortableNativeGlyphRunSource
{
    bool TryGetPortableNativeGlyphRun(out PortableNativeGlyphRun glyphRun);
}

public sealed class PortableGlyphRun
{
    public ushort[] GlyphIndices { get; set; } = Array.Empty<ushort>();

    public PortablePoint[] GlyphPositions { get; set; } = Array.Empty<PortablePoint>();

    public double[] AdvanceWidths { get; set; } = Array.Empty<double>();

    public PortablePoint[] GlyphOffsets { get; set; } = Array.Empty<PortablePoint>();

    public PortablePoint BaselineOrigin { get; set; }

    public double FontRenderingEmSize { get; set; }

    public object? NativeFont { get; set; }

    public string? FontUri { get; set; }

    public string[] FontFamilyNames { get; set; } = Array.Empty<string>();

    public bool IsBold { get; set; }

    public bool IsItalic { get; set; }

    public bool HasTransform { get; set; }

    public PortableMatrix3x2 Transform { get; set; } = PortableMatrix3x2.Identity;
}

public sealed class PortableNativeGlyphRun
{
    public ushort[] GlyphIndices { get; set; } = Array.Empty<ushort>();

    public Vector2[] GlyphPositions { get; set; } = Array.Empty<Vector2>();

    public Vector2 BaselineOrigin { get; set; }

    public double FontRenderingEmSize { get; set; }

    public object? NativeFont { get; set; }

    public string? FontUri { get; set; }

    public string[] FontFamilyNames { get; set; } = Array.Empty<string>();

    public bool IsBold { get; set; }

    public bool IsItalic { get; set; }

    public bool HasTransform { get; set; }

    public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;
}
