using System.Numerics;

namespace System.Windows.Media;

public abstract class Geometry : ProGPU.Scene.INativePathGeometrySource
{
    public Transform? Transform { get; set; }

    public abstract void Draw(ProGPU.Scene.DrawingContext context, ProGPU.Vector.Brush? fill, ProGPU.Vector.Pen? pen);
    public abstract Rect Bounds { get; }
    internal virtual bool TryGetPathGeometry(out ProGPU.Vector.PathGeometry path, out Matrix4x4 transform)
    {
        path = null!;
        transform = Matrix4x4.Identity;
        return false;
    }

    bool ProGPU.Scene.INativePathGeometrySource.TryGetPathGeometry(out ProGPU.Vector.PathGeometry path, out Matrix4x4 transform)
    {
        return TryGetPathGeometry(out path, out transform);
    }
}
