using System.Numerics;
using netDxf.Entities;

namespace ProGPU.Dxf;

public class DxfDimensionRenderer : IDxfEntityRenderer
{
    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        if (entity is not Dimension dimension) return;
        if (dimension.Block == null) return;

        // Render the pre-compiled visual primitives inside the dimension's block
        foreach (var childEntity in dimension.Block.Entities)
        {
            if (context.ActiveLayers.Contains(childEntity.Layer.Name))
            {
                DxfDocumentRenderer.RenderEntity(childEntity, context, transform);
            }
        }
    }
}
