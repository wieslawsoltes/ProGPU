using System.Numerics;

namespace System.Windows.Media;

public abstract class Geometry
{
    public Transform? Transform { get; set; }

    public abstract void Draw(ProGPU.Scene.DrawingContext context, ProGPU.Vector.Brush? fill, ProGPU.Vector.Pen? pen);
    public abstract Rect Bounds { get; }
    internal abstract bool TryGetPathGeometry(out ProGPU.Vector.PathGeometry path, out Matrix4x4 transform);
}
