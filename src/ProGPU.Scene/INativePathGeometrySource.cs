using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.Scene;

public interface INativePathGeometrySource
{
    bool TryGetPathGeometry(out PathGeometry path, out Matrix4x4 transform);
}
