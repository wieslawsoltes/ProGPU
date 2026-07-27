using Avalonia.Media;

namespace Avalonia.ProGpu;

/// <summary>
/// Immutable mapping from an Avalonia tile-brush source rectangle into one
/// destination tile.
/// </summary>
internal readonly struct AvaloniaTileBrushMapping
{
    public AvaloniaTileBrushMapping(
        ITileBrush brush,
        Size contentSize,
        Size targetSize)
    {
        SourceRect = brush.SourceRect.ToPixels(contentSize);
        DestinationRect = brush.DestinationRect.ToPixels(targetSize);

        Vector scale = brush.Stretch.CalculateScaling(
            DestinationRect.Size,
            SourceRect.Size);
        Vector alignmentOffset = GetAlignmentOffset(
            brush.AlignmentX,
            brush.AlignmentY,
            SourceRect.Size * scale,
            DestinationRect.Size);
        Matrix mapping =
            Matrix.CreateTranslation(-(Vector)SourceRect.Position) *
            Matrix.CreateScale(scale) *
            Matrix.CreateTranslation(alignmentOffset);

        if (brush.TileMode == TileMode.None)
        {
            IntermediateClip = DestinationRect;
            IntermediateTransform =
                mapping *
                Matrix.CreateTranslation((Vector)DestinationRect.Position);
        }
        else
        {
            IntermediateClip = new Rect(DestinationRect.Size);
            IntermediateTransform = mapping;
        }
    }

    public Rect DestinationRect { get; }
    public Rect IntermediateClip { get; }
    public Matrix IntermediateTransform { get; }
    public Rect SourceRect { get; }

    private static Vector GetAlignmentOffset(
        AlignmentX horizontal,
        AlignmentY vertical,
        Size scaledSource,
        Size destination)
    {
        double x = horizontal switch
        {
            AlignmentX.Center =>
                (destination.Width - scaledSource.Width) * 0.5,
            AlignmentX.Right =>
                destination.Width - scaledSource.Width,
            _ => 0d
        };
        double y = vertical switch
        {
            AlignmentY.Center =>
                (destination.Height - scaledSource.Height) * 0.5,
            AlignmentY.Bottom =>
                destination.Height - scaledSource.Height,
            _ => 0d
        };
        return new Vector(x, y);
    }
}
