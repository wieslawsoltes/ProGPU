namespace System.Windows.Media;

public sealed class StreamGeometry : Geometry
{
    private readonly PathGeometry _pathGeometry = new();

    public override void Draw(ProGPU.Scene.DrawingContext context, ProGPU.Vector.Brush? fill, ProGPU.Vector.Pen? pen)
    {
        _pathGeometry.Transform = Transform;
        _pathGeometry.Draw(context, fill, pen);
    }

    public override Rect Bounds => _pathGeometry.Bounds;

    public StreamGeometryContext Open()
    {
        _pathGeometry.Figures.Clear();
        return new StreamGeometryContextImpl(_pathGeometry);
    }

    public void Clear()
    {
        _pathGeometry.Figures.Clear();
    }
}
