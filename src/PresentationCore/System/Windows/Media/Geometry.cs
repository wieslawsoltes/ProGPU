using System.Numerics;

namespace System.Windows.Media;

public abstract class Geometry :
    ProGPU.Scene.INativePathGeometrySource,
    ProGPU.Wpf.Interop.IPortablePrimitiveGeometrySource
{
    public Transform? Transform { get; set; }

    public static Geometry Parse(string source)
    {
        return PathGeometry.Parse(source);
    }

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

    protected virtual bool TryGetPortablePrimitiveGeometryCore(
        out ProGPU.Wpf.Interop.PortablePrimitiveGeometry geometry)
    {
        geometry = default;
        return false;
    }

    bool ProGPU.Wpf.Interop.IPortablePrimitiveGeometrySource.TryGetPortablePrimitiveGeometry(
        out ProGPU.Wpf.Interop.PortablePrimitiveGeometry geometry) =>
        TryGetPortablePrimitiveGeometryCore(out geometry);

    protected ProGPU.Wpf.Interop.PortableMatrix3x2 GetPortableTransform()
    {
        Matrix4x4 matrix = Transform?.Value ?? Matrix4x4.Identity;
        return new ProGPU.Wpf.Interop.PortableMatrix3x2(
            matrix.M11,
            matrix.M12,
            matrix.M21,
            matrix.M22,
            matrix.M41,
            matrix.M42);
    }
}
