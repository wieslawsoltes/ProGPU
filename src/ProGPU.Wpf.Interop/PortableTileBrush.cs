namespace ProGPU.Wpf.Interop;

public interface IPortableTileBrushSource
{
    bool TryGetPortableTileBrush(out PortableTileBrush brush);
}

public enum PortableTileBrushKind
{
    Image = 0,
    Drawing = 1,
    Visual = 2
}

public enum PortableTileMode
{
    None = 0,
    Tile = 1,
    FlipX = 2,
    FlipY = 3,
    FlipXY = 4
}

public enum PortableStretch
{
    None = 0,
    Fill = 1,
    Uniform = 2,
    UniformToFill = 3
}

public enum PortableAlignmentX
{
    Left = 0,
    Center = 1,
    Right = 2
}

public enum PortableAlignmentY
{
    Top = 0,
    Center = 1,
    Bottom = 2
}

public sealed class PortableTileBrush
{
    public PortableTileBrush(
        PortableTileBrushKind kind,
        object content,
        double opacity,
        PortableRect viewport,
        PortableRect viewbox,
        PortableBrushMappingMode viewportUnits,
        PortableBrushMappingMode viewboxUnits,
        PortableTileMode tileMode,
        PortableStretch stretch,
        PortableAlignmentX alignmentX,
        PortableAlignmentY alignmentY,
        bool hasTransform,
        PortableMatrix3x2 transform,
        bool hasRelativeTransform,
        PortableMatrix3x2 relativeTransform)
        : this(kind, content, opacity, viewport, viewbox, viewportUnits, viewboxUnits,
            tileMode, stretch, alignmentX, alignmentY, hasTransform, transform,
            hasRelativeTransform, relativeTransform, allowNullVisual: false)
    {
    }

    /// <summary>
    /// Captures a VisualBrush, including an explicitly unassigned Visual.
    /// A null <paramref name="visual"/> means transparent source content; it
    /// does not replace unavailable source bounds or a failed visual capture.
    /// Mapping and transform values retain the ordinary snapshot policy.
    /// </summary>
    public static PortableTileBrush Visual(
        object? visual,
        double opacity,
        PortableRect viewport,
        PortableRect viewbox,
        PortableBrushMappingMode viewportUnits,
        PortableBrushMappingMode viewboxUnits,
        PortableTileMode tileMode,
        PortableStretch stretch,
        PortableAlignmentX alignmentX,
        PortableAlignmentY alignmentY,
        bool hasTransform,
        PortableMatrix3x2 transform,
        bool hasRelativeTransform,
        PortableMatrix3x2 relativeTransform)
        => new(PortableTileBrushKind.Visual, visual, opacity, viewport, viewbox,
            viewportUnits, viewboxUnits, tileMode, stretch, alignmentX, alignmentY,
            hasTransform, transform, hasRelativeTransform, relativeTransform, allowNullVisual: true);

    private PortableTileBrush(
        PortableTileBrushKind kind,
        object? content,
        double opacity,
        PortableRect viewport,
        PortableRect viewbox,
        PortableBrushMappingMode viewportUnits,
        PortableBrushMappingMode viewboxUnits,
        PortableTileMode tileMode,
        PortableStretch stretch,
        PortableAlignmentX alignmentX,
        PortableAlignmentY alignmentY,
        bool hasTransform,
        PortableMatrix3x2 transform,
        bool hasRelativeTransform,
        PortableMatrix3x2 relativeTransform,
        bool allowNullVisual)
    {
        Kind = kind;
        if (content is null && !(allowNullVisual && kind == PortableTileBrushKind.Visual))
            throw new ArgumentNullException(nameof(content));
        Content = content;
        Opacity = double.IsFinite(opacity) ? opacity : 1.0;
        Viewport = viewport;
        Viewbox = viewbox;
        ViewportUnits = viewportUnits;
        ViewboxUnits = viewboxUnits;
        TileMode = tileMode;
        Stretch = stretch;
        AlignmentX = alignmentX;
        AlignmentY = alignmentY;
        HasTransform = hasTransform;
        Transform = hasTransform ? transform : PortableMatrix3x2.Identity;
        HasRelativeTransform = hasRelativeTransform;
        RelativeTransform = hasRelativeTransform ? relativeTransform : PortableMatrix3x2.Identity;
    }

    public PortableTileBrushKind Kind { get; }

    /// <summary>
    /// Original source identity. Only the explicit <see cref="Visual"/>
    /// factory can publish null, for a genuinely unassigned VisualBrush source.
    /// </summary>
    public object? Content { get; }

    public double Opacity { get; }

    public PortableRect Viewport { get; }

    public PortableRect Viewbox { get; }

    public PortableBrushMappingMode ViewportUnits { get; }

    public PortableBrushMappingMode ViewboxUnits { get; }

    public PortableTileMode TileMode { get; }

    public PortableStretch Stretch { get; }

    public PortableAlignmentX AlignmentX { get; }

    public PortableAlignmentY AlignmentY { get; }

    public bool HasTransform { get; }

    public PortableMatrix3x2 Transform { get; }

    public bool HasRelativeTransform { get; }

    public PortableMatrix3x2 RelativeTransform { get; }
}
