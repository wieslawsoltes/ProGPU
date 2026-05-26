using System.Numerics;
using netDxf.Entities;

namespace ProGPU.Dxf;

public class DxfHatchRenderer : IDxfEntityRenderer
{
    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        if (entity is not Hatch hatch) return;
        if (hatch.BoundaryPaths == null) return;

        // Render each boundary loop using high-performance culling/LOD primitives
        foreach (var bp in hatch.BoundaryPaths)
        {
            if (bp.Entities == null) continue;
            foreach (var childEntity in bp.Entities)
            {
                if (childEntity.Layer == null)
                {
                    childEntity.Layer = hatch.Layer;
                }
                DxfDocumentRenderer.RenderEntity(childEntity, context, transform);
            }
        }
    }
}
