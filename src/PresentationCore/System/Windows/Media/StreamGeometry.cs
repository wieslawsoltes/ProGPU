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

    internal override bool TryGetPathGeometry(out ProGPU.Vector.PathGeometry path, out System.Numerics.Matrix4x4 transform)
    {
        path = _pathGeometry.ToProGpuPathGeometry();
        transform = Transform != null ? Transform.Value : System.Numerics.Matrix4x4.Identity;
        return true;
    }

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
