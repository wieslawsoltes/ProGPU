using System;
using System.Numerics;
using netDxf.Entities;
using Vector2 = System.Numerics.Vector2;

namespace ProGPU.Dxf;

public class DxfLeaderRenderer : IDxfEntityRenderer
{
    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        if (entity is not Leader leader) return;
        if (leader.Vertexes.Count < 2) return;

        var pen = context.GetCachedPen(leader, 1f);

        // 1. Draw Leader Arrowhead at the first vertex (pointing towards the second vertex)
        var v0_3 = leader.Vertexes[0];
        var v1_3 = leader.Vertexes[1];
        var v0 = new Vector2((float)v0_3.X, (float)v0_3.Y);
        var v1 = new Vector2((float)v1_3.X, (float)v1_3.Y);

        Vector2 dir = v1 - v0;
        float len = dir.Length();
        Vector2 startPoint = v0;

        if (len > 0.001f)
        {
            dir = Vector2.Normalize(dir);
            Vector2 normal = new Vector2(-dir.Y, dir.X);

            float arrowLength = 2.5f; // Standard CAD arrow size in world space
            float arrowWidth = arrowLength * 0.4f;

            // Define arrow points in world space
            Vector2 worldTip = v0;
            Vector2 worldLeft = v0 + dir * arrowLength + normal * arrowWidth;
            Vector2 worldRight = v0 + dir * arrowLength - normal * arrowWidth;

            // Transform to screen space
            Vector2 screenTip = context.Transform(worldTip, transform);
            Vector2 screenLeft = context.Transform(worldLeft, transform);
            Vector2 screenRight = context.Transform(worldRight, transform);

            // Draw arrowhead outline
            context.DrawingContext.DrawLine(pen, screenTip, screenLeft);
            context.DrawingContext.DrawLine(pen, screenTip, screenRight);
            context.DrawingContext.DrawLine(pen, screenLeft, screenRight);

            // Start the actual leader line from the back of the arrowhead
            startPoint = v0 + dir * arrowLength;
        }

        // 2. Draw leader segments
        var pPrev = context.Transform(startPoint, transform);
        for (int i = 1; i < leader.Vertexes.Count; i++)
        {
            var vi = leader.Vertexes[i];
            var pNext = context.Transform(new Vector2((float)vi.X, (float)vi.Y), transform);
            
            // Viewport culling for each line segment
            var min = Vector2.Min(pPrev, pNext);
            var max = Vector2.Max(pPrev, pNext);
            if (!context.IsOffScreen(min, max))
            {
                context.DrawingContext.DrawLine(pen, pPrev, pNext);
            }
            pPrev = pNext;
        }
    }
}
