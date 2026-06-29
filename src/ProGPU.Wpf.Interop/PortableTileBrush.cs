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
    {
        Kind = kind;
        Content = content ?? throw new ArgumentNullException(nameof(content));
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

    public object Content { get; }

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
