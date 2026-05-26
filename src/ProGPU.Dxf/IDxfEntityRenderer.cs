using System.Numerics;
using netDxf.Entities;

namespace ProGPU.Dxf;

public interface IDxfEntityRenderer
{
    void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform);
}
