using System;
using System.Collections.Generic;
using System.Numerics;
using netDxf.Entities;
using ProGPU.Scene;
using ProGPU.Vector;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace ProGPU.Dxf;

public static class DxfColorHelper
{
    public static Brush ResolveBrush(EntityObject entity, DxfRenderContext context)
    {
        var color = new Vector4(1f, 1f, 1f, 1f); // Default white/fallback
        
        if (entity.Color.IsByLayer)
        {
            if (context.LayerColors.TryGetValue(entity.Layer.Name, out var lColor))
            {
                color = lColor;
            }
            else
            {
                var aci = entity.Layer.Color;
                color = new Vector4(aci.R / 255f, aci.G / 255f, aci.B / 255f, 1f);
            }
        }
        else
        {
            var aci = entity.Color;
            color = new Vector4(aci.R / 255f, aci.G / 255f, aci.B / 255f, 1f);
        }

        return new SolidColorBrush(color);
    }
}


public class DxfLineRenderer : IDxfEntityRenderer
{
    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        if (entity is not Line line) return;

        var p1 = context.Transform(new Vector2((float)line.StartPoint.X, (float)line.StartPoint.Y), transform);
        var p2 = context.Transform(new Vector2((float)line.EndPoint.X, (float)line.EndPoint.Y), transform);

        // Viewport culling
        var min = Vector2.Min(p1, p2);
        var max = Vector2.Max(p1, p2);
        if (context.IsOffScreen(min, max)) return;

        // screen-space length LOD culling
        float dx = p2.X - p1.X;
        float dy = p2.Y - p1.Y;
        float lenSq = dx * dx + dy * dy;
        if (context.EnableGpuTransforms) lenSq *= context.Zoom * context.Zoom;
        if (!context.IsCompilingStatic && (context.EnableLod ? (lenSq < 1.5f) : (lenSq < 0.01f))) return;

        var pen = context.GetCachedPen(line, 1f);

        context.DrawingContext.DrawLine(pen, p1, p2);
    }
}

public class DxfArcCircleRenderer : IDxfEntityRenderer
{
    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        var pen = context.GetCachedPen(entity, 1.2f);

        if (entity is Circle circle)
        {
            var combined = DxfDocumentRenderer.GetOcsMatrix(circle.Normal) * transform;
            float cx = (float)circle.Center.X;
            float cy = (float)circle.Center.Y;
            float r = (float)circle.Radius;

            // Calculate screen-space center and radius
            var screenCenter = context.Transform(new Vector2(cx, cy), combined);
            var screenPoint = context.Transform(new Vector2(cx + r, cy), combined);
            float screenR = Vector2.Distance(screenCenter, screenPoint);

            float physicalR = screenR;
            if (context.EnableGpuTransforms)
            {
                physicalR *= context.Zoom;
            }

            // Viewport culling using circle bounding box
            if (context.IsOffScreen(screenCenter - new Vector2(screenR), screenCenter + new Vector2(screenR))) return;

            if (physicalR < 1f) return; // Too small to render

            // Detect uniform scaling in the transformation matrix to use native DrawCircle
            var col1 = new Vector3(combined.M11, combined.M12, combined.M13);
            var col2 = new Vector3(combined.M21, combined.M22, combined.M23);
            float scaleX = col1.Length();
            float scaleY = col2.Length();
            bool isUniform = Math.Abs(scaleX - scaleY) < 1e-4f;

            if (isUniform)
            {
                // Native high-performance GPU Circle rendering (1 draw call, perfect SDF anti-aliasing)
                context.DrawingContext.DrawCircle(null, pen, screenCenter, screenR);
            }
            else
            {
                // Non-uniform fallback using dynamic LOD segment interpolation
                int numSegments = 64;
                if (context.EnableLod)
                {
                    if (physicalR < 5f) numSegments = 8;
                    else if (physicalR < 15f) numSegments = 12;
                    else if (physicalR < 50f) numSegments = 24;
                    else if (physicalR < 150f) numSegments = 36;
                    else numSegments = 64;
                }

                Span<Vector2> points = numSegments <= 64 ? stackalloc Vector2[numSegments + 1] : new Vector2[numSegments + 1];

                for (int i = 0; i <= numSegments; i++)
                {
                    float angle = i * 2f * MathF.PI / numSegments;
                    points[i] = context.Transform(new Vector2(cx + MathF.Cos(angle) * r, cy + MathF.Sin(angle) * r), combined);
                }

                for (int i = 0; i < numSegments; i++)
                {
                    context.DrawingContext.DrawLine(pen, points[i], points[i + 1]);
                }
            }
        }
        else if (entity is Arc arc)
        {
            var combined = DxfDocumentRenderer.GetOcsMatrix(arc.Normal) * transform;
            float cx = (float)arc.Center.X;
            float cy = (float)arc.Center.Y;
            float r = (float)arc.Radius;

            // Calculate screen-space center and radius
            var screenCenter = context.Transform(new Vector2(cx, cy), combined);
            var screenPoint = context.Transform(new Vector2(cx + r, cy), combined);
            float screenR = Vector2.Distance(screenCenter, screenPoint);

            float physicalR = screenR;
            if (context.EnableGpuTransforms)
            {
                physicalR *= context.Zoom;
            }

            // Viewport culling
            if (context.IsOffScreen(screenCenter - new Vector2(screenR), screenCenter + new Vector2(screenR))) return;

            if (physicalR < 1f) return; // Too small to render

            // Dynamic LOD for Arc segment count
            int numSegments = 64;
            if (context.EnableLod)
            {
                if (physicalR < 5f) numSegments = 6;
                else if (physicalR < 15f) numSegments = 12;
                else if (physicalR < 50f) numSegments = 18;
                else if (physicalR < 150f) numSegments = 24;
                else numSegments = 32;
            }

            float startRad = (float)(arc.StartAngle * Math.PI / 180.0);
            float endRad = (float)(arc.EndAngle * Math.PI / 180.0);
            if (endRad < startRad) endRad += 2f * MathF.PI;

            Span<Vector2> points = numSegments <= 64 ? stackalloc Vector2[numSegments + 1] : new Vector2[numSegments + 1];

            for (int i = 0; i <= numSegments; i++)
            {
                float t = (float)i / numSegments;
                float angle = startRad + t * (endRad - startRad);
                points[i] = context.Transform(new Vector2(cx + MathF.Cos(angle) * r, cy + MathF.Sin(angle) * r), combined);
            }

            for (int i = 0; i < numSegments; i++)
            {
                context.DrawingContext.DrawLine(pen, points[i], points[i + 1]);
            }
        }
    }
}

public class DxfEllipseRenderer : IDxfEntityRenderer
{
    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        if (entity is not Ellipse ellipse) return;

        var combined = DxfDocumentRenderer.GetOcsMatrix(ellipse.Normal) * transform;

        var center = new Vector2((float)ellipse.Center.X, (float)ellipse.Center.Y);
        float rx = (float)ellipse.MajorAxis;
        float ry = (float)ellipse.MinorAxis;

        // Calculate screen-space parameters using combined OCS matrix
        var screenCenter = context.Transform(center, combined);
        var screenPointX = context.Transform(center + new Vector2(rx, 0f), combined);
        var screenPointY = context.Transform(center + new Vector2(0f, ry), combined);
        float screenRx = Vector2.Distance(screenCenter, screenPointX);
        float screenRy = Vector2.Distance(screenCenter, screenPointY);
        float maxScreenR = Math.Max(screenRx, screenRy);

        float physicalMaxR = maxScreenR;
        if (context.EnableGpuTransforms) physicalMaxR *= context.Zoom;

        // Viewport culling using maximum screen radius bounding box
        if (context.IsOffScreen(screenCenter - new Vector2(maxScreenR), screenCenter + new Vector2(maxScreenR))) return;

        if (physicalMaxR < 1f) return; // Too small to see

        // Dynamic LOD for ellipse segment count
        int numSegments = 64;
        if (context.EnableLod)
        {
            if (physicalMaxR < 5f) numSegments = 8;
            else if (physicalMaxR < 15f) numSegments = 16;
            else if (physicalMaxR < 50f) numSegments = 24;
            else if (physicalMaxR < 150f) numSegments = 36;
            else numSegments = 64;
        }

        float rotationRad = (float)(ellipse.Rotation * Math.PI / 180.0);

        float startRad = (float)(ellipse.StartAngle * Math.PI / 180.0);
        float endRad = (float)(ellipse.EndAngle * Math.PI / 180.0);
        if (endRad < startRad) endRad += 2f * MathF.PI;

        Span<Vector2> points = numSegments <= 64 ? stackalloc Vector2[numSegments + 1] : new Vector2[numSegments + 1];

        for (int i = 0; i <= numSegments; i++)
        {
            float t = startRad + ((float)i / numSegments) * (endRad - startRad);
            
            float x = rx * MathF.Cos(t);
            float y = ry * MathF.Sin(t);

            // Apply ellipse rotation
            float rotX = x * MathF.Cos(rotationRad) - y * MathF.Sin(rotationRad);
            float rotY = x * MathF.Sin(rotationRad) + y * MathF.Cos(rotationRad);

            var pt = center + new Vector2(rotX, rotY);
            points[i] = context.Transform(pt, combined);
        }

        var pen = context.GetCachedPen(ellipse, 1.2f);

        for (int i = 0; i < numSegments; i++)
        {
            context.DrawingContext.DrawLine(pen, points[i], points[i + 1]);
        }
    }
}

public class DxfPolylineRenderer : IDxfEntityRenderer
{
    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        Matrix4x4 combined = transform;
        if (entity is LwPolyline lw)
        {
            combined = DxfDocumentRenderer.GetOcsMatrix(lw.Normal) * transform;
        }
        else if (entity is Polyline poly)
        {
            combined = DxfDocumentRenderer.GetOcsMatrix(poly.Normal) * transform;
        }

        // Viewport culling at the entire polyline level using combined matrix
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        bool hasVertices = false;

        if (entity is LwPolyline lwPolyline)
        {
            foreach (var v in lwPolyline.Vertexes)
            {
                var sp = context.Transform(new Vector2((float)v.Position.X, (float)v.Position.Y), combined);
                minX = Math.Min(minX, sp.X);
                minY = Math.Min(minY, sp.Y);
                maxX = Math.Max(maxX, sp.X);
                maxY = Math.Max(maxY, sp.Y);
                hasVertices = true;
            }
        }
        else if (entity is Polyline polyline)
        {
            foreach (var v in polyline.Vertexes)
            {
                var sp = context.Transform(new Vector2((float)v.Position.X, (float)v.Position.Y), combined);
                minX = Math.Min(minX, sp.X);
                minY = Math.Min(minY, sp.Y);
                maxX = Math.Max(maxX, sp.X);
                maxY = Math.Max(maxY, sp.Y);
                hasVertices = true;
            }
        }

        if (hasVertices)
        {
            if (context.IsOffScreen(new Vector2(minX, minY), new Vector2(maxX, maxY))) return;

            float width = maxX - minX;
            float height = maxY - minY;
            if (context.EnableGpuTransforms)
            {
                width *= context.Zoom;
                height *= context.Zoom;
            }
            if (!context.IsCompilingStatic && (context.EnableLod ? (width < 2f && height < 2f) : (width < 0.2f && height < 0.2f))) return;
        }

        var pen = context.GetCachedPen(entity, 1.2f);

        if (entity is LwPolyline lwPoly)
        {
            RenderLwPolyline(lwPoly, context, combined, pen);
        }
        else if (entity is Polyline polyObj)
        {
            RenderPolyline(polyObj, context, combined, pen);
        }
    }

    private void RenderLwPolyline(LwPolyline poly, DxfRenderContext context, Matrix4x4 transform, Pen pen)
    {
        int count = poly.Vertexes.Count;
        if (count < 2) return;

        // Check if there are any bulges (arcs) in the polyline
        bool hasBulge = false;
        foreach (var v in poly.Vertexes)
        {
            if (Math.Abs(v.Bulge) > 1e-5)
            {
                hasBulge = true;
                break;
            }
        }

        if (!hasBulge)
        {
            // Pure straight polyline - batch entirely into ONE native DrawPolyline command
            var points = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                var v = poly.Vertexes[i];
                points[i] = context.Transform(new Vector2((float)v.Position.X, (float)v.Position.Y), transform);
            }
            context.DrawingContext.DrawPolyline(pen, points, poly.IsClosed);
        }
        else
        {
            // Fallback for curves/bulges using segmented pre-transformed rendering
            Span<Vector2> screenPoints = count <= 512 ? stackalloc Vector2[count] : new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                var v = poly.Vertexes[i];
                screenPoints[i] = context.Transform(new Vector2((float)v.Position.X, (float)v.Position.Y), transform);
            }

            for (int i = 0; i < count - 1; i++)
            {
                var v1 = poly.Vertexes[i];
                var v2 = poly.Vertexes[i + 1];
                RenderSegment(v1.Position, v1.Bulge, v2.Position, screenPoints[i], screenPoints[i + 1], context, transform, pen);
            }

            if (poly.IsClosed)
            {
                var v1 = poly.Vertexes[count - 1];
                var v2 = poly.Vertexes[0];
                RenderSegment(v1.Position, v1.Bulge, v2.Position, screenPoints[count - 1], screenPoints[0], context, transform, pen);
            }
        }
    }

    private void RenderPolyline(Polyline poly, DxfRenderContext context, Matrix4x4 transform, Pen pen)
    {
        int count = poly.Vertexes.Count;
        if (count < 2) return;

        // Polyline vertexes do not support bulges in netDxf version - batch entirely
        var points = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            var v = poly.Vertexes[i];
            points[i] = context.Transform(new Vector2((float)v.Position.X, (float)v.Position.Y), transform);
        }

        context.DrawingContext.DrawPolyline(pen, points, poly.IsClosed);
    }

    private void RenderSegment(netDxf.Vector2 start, double bulge, netDxf.Vector2 end, Vector2 sp1, Vector2 sp2, DxfRenderContext context, Matrix4x4 transform, Pen pen)
    {
        if (Math.Abs(bulge) < 1e-5)
        {
            // Viewport culling for straight line segment
            var min = Vector2.Min(sp1, sp2);
            var max = Vector2.Max(sp1, sp2);
            if (context.IsOffScreen(min, max)) return;

            context.DrawingContext.DrawLine(pen, sp1, sp2);
        }
        else
        {
            var p1 = new Vector2((float)start.X, (float)start.Y);
            var p2 = new Vector2((float)end.X, (float)end.Y);

            float b = (float)bulge;
            float alpha = 2f * MathF.Atan(b);
            var mid = (p1 + p2) * 0.5f;
            var dVec = p2 - p1;
            float d = dVec.Length();

            if (d > 1e-5f)
            {
                float r = d * (1f + b * b) / (4f * b);
                var perp = new Vector2(-dVec.Y, dVec.X) / d;
                float h = d * (1f - b * b) / (4f * b);
                var center = mid + perp * h;

                // Calculate screen-space center and radius of the bulge arc
                float rAbs = MathF.Abs(r);
                var screenCenter = context.Transform(center, transform);
                var screenPoint = context.Transform(center + new Vector2(rAbs, 0f), transform);
                float screenR = Vector2.Distance(screenCenter, screenPoint);
                if (context.EnableGpuTransforms) screenR *= context.Zoom;

                // Viewport culling for bulge arc
                if (context.IsOffScreen(screenCenter - new Vector2(screenR), screenCenter + new Vector2(screenR))) return;

                if (screenR < 1f) return; // Too small to see

                // Dynamic LOD for bulge arc segment count
                int numSegments = 16;
                if (context.EnableLod)
                {
                    if (screenR < 5f) numSegments = 4;
                    else if (screenR < 15f) numSegments = 8;
                    else if (screenR < 50f) numSegments = 12;
                    else numSegments = 16;
                }

                float angle1 = MathF.Atan2(p1.Y - center.Y, p1.X - center.X);
                float angle2 = MathF.Atan2(p2.Y - center.Y, p2.X - center.X);

                if (b > 0 && angle2 < angle1) angle2 += 2f * MathF.PI;
                if (b < 0 && angle2 > angle1) angle2 -= 2f * MathF.PI;

                Span<Vector2> arcPoints = numSegments <= 64 ? stackalloc Vector2[numSegments + 1] : new Vector2[numSegments + 1];

                for (int j = 0; j <= numSegments; j++)
                {
                    float t = (float)j / numSegments;
                    float angle = angle1 + t * (angle2 - angle1);
                    arcPoints[j] = context.Transform(center + new Vector2(MathF.Cos(angle) * rAbs, MathF.Sin(angle) * rAbs), transform);
                }

                for (int j = 0; j < numSegments; j++)
                {
                    context.DrawingContext.DrawLine(pen, arcPoints[j], arcPoints[j + 1]);
                }
            }
        }
    }
}

public class DxfSplineRenderer : IDxfEntityRenderer
{
    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        if (entity is not Spline spline) return;

        if (spline.ControlPoints.Count < 2) return;

        // Viewport culling based on control points screen bounding box
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        var controlPoints = new Vector2[spline.ControlPoints.Count];
        for (int i = 0; i < spline.ControlPoints.Count; i++)
        {
            var cp = spline.ControlPoints[i];
            var sp = context.Transform(new Vector2((float)cp.Position.X, (float)cp.Position.Y), transform);
            controlPoints[i] = sp;
            minX = Math.Min(minX, sp.X);
            minY = Math.Min(minY, sp.Y);
            maxX = Math.Max(maxX, sp.X);
            maxY = Math.Max(maxY, sp.Y);
        }

        var minPt = new Vector2(minX, minY);
        var maxPt = new Vector2(maxX, maxY);
        if (context.IsOffScreen(minPt, maxPt)) return;

        var pen = context.GetCachedPen(spline, 1.2f);
        
        var knots = new double[spline.Knots.Count];
        for (int i = 0; i < spline.Knots.Count; i++)
        {
            knots[i] = spline.Knots[i];
        }

        double[]? weights = null;
        bool hasWeights = false;
        for (int i = 0; i < spline.ControlPoints.Count; i++)
        {
            if (Math.Abs(spline.ControlPoints[i].Weight - 1.0) > 1e-5)
            {
                hasWeights = true;
                break;
            }
        }

        if (hasWeights)
        {
            weights = new double[spline.ControlPoints.Count];
            for (int i = 0; i < spline.ControlPoints.Count; i++)
            {
                weights[i] = spline.ControlPoints[i].Weight;
            }
        }

        // Submit exactly ONE batch spline command (extremely fast, zero heap allocations for NURBS)
        context.DrawingContext.DrawSpline(pen, controlPoints, knots, weights, spline.Degree, spline.IsClosed);
    }
}

public class DxfTextRenderer : IDxfEntityRenderer
{
    public static void RenderAttribute(netDxf.Entities.Attribute attr, DxfRenderContext context, Matrix4x4 transform, float scaleY)
    {
        if (attr.Flags.HasFlag(netDxf.Entities.AttributeFlags.Hidden)) return;

        string valStr = attr.Value?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(valStr)) return;

        float rot = (float)(attr.Rotation * Math.PI / 180.0);
        var origin = new Vector2((float)attr.Position.X, (float)attr.Position.Y);
        var screenPos = context.Transform(origin, transform);

        // Project coordinate baseline to find exact screen scale and rotation angle
        var originPlusBaseline = origin + new Vector2(MathF.Cos(rot), MathF.Sin(rot));
        var screenBaselinePt = context.Transform(originPlusBaseline, transform);
        var baselineVec = screenBaselinePt - screenPos;
        float screenScale = baselineVec.Length();
        if (screenScale < 1e-4f) return;

        var u = baselineVec / screenScale;
        var v = new Vector2(-u.Y, u.X);
        if (context.EnableGpuTransforms)
        {
            v = -v;
        }

        float screenFontSize = (float)attr.Height * screenScale;
        float physicalFontSize = screenFontSize;
        if (context.EnableGpuTransforms) physicalFontSize *= context.Zoom;
        if (!context.IsCompilingStatic && (context.EnableLod ? (physicalFontSize < 4f) : (physicalFontSize < 0.1f))) return;

        float horizontalShiftMultiplier = 0f;
        float verticalShiftMultiplier = 0f;

        switch (attr.Alignment)
        {
            case netDxf.Entities.TextAlignment.TopLeft:
                horizontalShiftMultiplier = 0f;
                verticalShiftMultiplier = 1.0f;
                break;
            case netDxf.Entities.TextAlignment.TopCenter:
                horizontalShiftMultiplier = 0.5f;
                verticalShiftMultiplier = 1.0f;
                break;
            case netDxf.Entities.TextAlignment.TopRight:
                horizontalShiftMultiplier = 1.0f;
                verticalShiftMultiplier = 1.0f;
                break;
            case netDxf.Entities.TextAlignment.MiddleLeft:
                horizontalShiftMultiplier = 0f;
                verticalShiftMultiplier = 0.5f;
                break;
            case netDxf.Entities.TextAlignment.MiddleCenter:
            case netDxf.Entities.TextAlignment.Middle:
                horizontalShiftMultiplier = 0.5f;
                verticalShiftMultiplier = 0.5f;
                break;
            case netDxf.Entities.TextAlignment.MiddleRight:
                horizontalShiftMultiplier = 1.0f;
                verticalShiftMultiplier = 0.5f;
                break;
            case netDxf.Entities.TextAlignment.BottomLeft:
            case netDxf.Entities.TextAlignment.BaselineLeft:
                horizontalShiftMultiplier = 0f;
                verticalShiftMultiplier = 0f;
                break;
            case netDxf.Entities.TextAlignment.BottomCenter:
            case netDxf.Entities.TextAlignment.BaselineCenter:
            case netDxf.Entities.TextAlignment.Aligned:
            case netDxf.Entities.TextAlignment.Fit:
                horizontalShiftMultiplier = 0.5f;
                verticalShiftMultiplier = 0f;
                break;
            case netDxf.Entities.TextAlignment.BottomRight:
            case netDxf.Entities.TextAlignment.BaselineRight:
                horizontalShiftMultiplier = 1.0f;
                verticalShiftMultiplier = 0f;
                break;
        }

        // Measure text width using exact glyph metrics
        float screenWidth = MeasureLineWidthStatic(valStr, context.Font, screenFontSize);
        float shiftX = -screenWidth * horizontalShiftMultiplier;
        float shiftY = screenFontSize * verticalShiftMultiplier;

        float maxDim = Math.Max(screenWidth, screenFontSize) * 1.5f;
        var minPt = new Vector2(screenPos.X - maxDim, screenPos.Y - maxDim);
        var maxPt = new Vector2(screenPos.X + maxDim, screenPos.Y + maxDim);
        if (context.IsOffScreen(minPt, maxPt)) return;

        // Resolve Brush
        var color = new Vector4(1f, 1f, 1f, 1f); // Default white/fallback
        if (attr.Color.IsByLayer)
        {
            if (context.LayerColors.TryGetValue(attr.Layer.Name, out var lColor))
            {
                color = lColor;
            }
            else
            {
                var aci = attr.Layer.Color;
                color = new Vector4(aci.R / 255f, aci.G / 255f, aci.B / 255f, 1f);
            }
        }
        else
        {
            var aci = attr.Color;
            color = new Vector4(aci.R / 255f, aci.G / 255f, aci.B / 255f, 1f);
        }
        var brush = new SolidColorBrush(color);

        var drawPos = screenPos + u * shiftX + v * shiftY;
        float rotationRad = MathF.Atan2(baselineVec.Y, baselineVec.X);
        DrawCameraAwareText(context, valStr, screenFontSize, brush, drawPos, rotationRad);
    }

    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        var brush = context.GetCachedBrush(entity);

        if (entity is netDxf.Entities.Text text)
        {
            if (string.IsNullOrWhiteSpace(text.Value)) return;

            var combined = DxfDocumentRenderer.GetOcsMatrix(text.Normal) * transform;
            float rot = (float)(text.Rotation * Math.PI / 180.0);
            var origin = new Vector2((float)text.Position.X, (float)text.Position.Y);
            var screenPos = context.Transform(origin, combined);

            // Project coordinate baseline to find exact screen scale and rotation angle
            var originPlusBaseline = origin + new Vector2(MathF.Cos(rot), MathF.Sin(rot));
            var screenBaselinePt = context.Transform(originPlusBaseline, combined);
            var baselineVec = screenBaselinePt - screenPos;
            float screenScale = baselineVec.Length();
            if (screenScale < 1e-4f) return;

            var u = baselineVec / screenScale;
            var v = new Vector2(-u.Y, u.X);
            if (context.EnableGpuTransforms)
            {
                v = -v;
            }

            float screenFontSize = (float)text.Height * screenScale;
            float physicalFontSize = screenFontSize;
            if (context.EnableGpuTransforms) physicalFontSize *= context.Zoom;
            if (!context.IsCompilingStatic && (context.EnableLod ? (physicalFontSize < 4f) : (physicalFontSize < 0.1f))) return;

            float horizontalShiftMultiplier = 0f;
            float verticalShiftMultiplier = 0f;

            switch (text.Alignment)
            {
                case netDxf.Entities.TextAlignment.TopLeft:
                    horizontalShiftMultiplier = 0f;
                    verticalShiftMultiplier = 1.0f;
                    break;
                case netDxf.Entities.TextAlignment.TopCenter:
                    horizontalShiftMultiplier = 0.5f;
                    verticalShiftMultiplier = 1.0f;
                    break;
                case netDxf.Entities.TextAlignment.TopRight:
                    horizontalShiftMultiplier = 1.0f;
                    verticalShiftMultiplier = 1.0f;
                    break;
                case netDxf.Entities.TextAlignment.MiddleLeft:
                    horizontalShiftMultiplier = 0f;
                    verticalShiftMultiplier = 0.5f;
                    break;
                case netDxf.Entities.TextAlignment.MiddleCenter:
                case netDxf.Entities.TextAlignment.Middle:
                    horizontalShiftMultiplier = 0.5f;
                    verticalShiftMultiplier = 0.5f;
                    break;
                case netDxf.Entities.TextAlignment.MiddleRight:
                    horizontalShiftMultiplier = 1.0f;
                    verticalShiftMultiplier = 0.5f;
                    break;
                case netDxf.Entities.TextAlignment.BottomLeft:
                case netDxf.Entities.TextAlignment.BaselineLeft:
                    horizontalShiftMultiplier = 0f;
                    verticalShiftMultiplier = 0f;
                    break;
                case netDxf.Entities.TextAlignment.BottomCenter:
                case netDxf.Entities.TextAlignment.BaselineCenter:
                case netDxf.Entities.TextAlignment.Aligned:
                case netDxf.Entities.TextAlignment.Fit:
                    horizontalShiftMultiplier = 0.5f;
                    verticalShiftMultiplier = 0f;
                    break;
                case netDxf.Entities.TextAlignment.BottomRight:
                case netDxf.Entities.TextAlignment.BaselineRight:
                    horizontalShiftMultiplier = 1.0f;
                    verticalShiftMultiplier = 0f;
                    break;
            }

            float screenWidth = MeasureLineWidthStatic(text.Value, context.Font, screenFontSize);
            float shiftX = -screenWidth * horizontalShiftMultiplier;
            float shiftY = screenFontSize * verticalShiftMultiplier;

            // Viewport culling (estimate rotated text bounds with generous padding)
            float maxDim = Math.Max(screenWidth, screenFontSize) * 1.5f;
            var minPt = new Vector2(screenPos.X - maxDim, screenPos.Y - maxDim);
            var maxPt = new Vector2(screenPos.X + maxDim, screenPos.Y + maxDim);
            if (context.IsOffScreen(minPt, maxPt)) return;

            var drawPos = screenPos + u * shiftX + v * shiftY;
            float rotationRad = MathF.Atan2(baselineVec.Y, baselineVec.X);
            DrawCameraAwareText(context, text.Value, screenFontSize, brush, drawPos, rotationRad);
        }
        else if (entity is MText mtext)
        {
            if (string.IsNullOrWhiteSpace(mtext.Value)) return;

            string cleanedValue = CleanMText(mtext.Value);
            var lines = cleanedValue.Split('\n');

            var combined = DxfDocumentRenderer.GetOcsMatrix(mtext.Normal) * transform;
            float rot = (float)(mtext.Rotation * Math.PI / 180.0);
            var origin = new Vector2((float)mtext.Position.X, (float)mtext.Position.Y);
            var screenPos = context.Transform(origin, combined);

            // Project coordinate baseline to find exact screen scale and rotation angle
            var originPlusBaseline = origin + new Vector2(MathF.Cos(rot), MathF.Sin(rot));
            var screenBaselinePt = context.Transform(originPlusBaseline, combined);
            var baselineVec = screenBaselinePt - screenPos;
            float screenScale = baselineVec.Length();
            if (screenScale < 1e-4f) return;

            var u = baselineVec / screenScale;
            var v = new Vector2(-u.Y, u.X);
            if (context.EnableGpuTransforms)
            {
                v = -v;
            }

            float screenFontSize = (float)mtext.Height * screenScale;
            float physicalFontSize = screenFontSize;
            if (context.EnableGpuTransforms) physicalFontSize *= context.Zoom;
            if (!context.IsCompilingStatic && (context.EnableLod ? (physicalFontSize < 4f) : (physicalFontSize < 0.1f))) return;

            float screenLineOffset = screenFontSize * 1.25f;
            float totalHeight = lines.Length * screenLineOffset;

            // Resolve vertical shift and horizontal shift multiplier based on CAD attachment point
            float verticalShift = 0f;
            float horizontalShiftMultiplier = 0f;

            switch (mtext.AttachmentPoint)
            {
                case MTextAttachmentPoint.TopLeft:
                    verticalShift = 0f;
                    horizontalShiftMultiplier = 0f;
                    break;
                case MTextAttachmentPoint.TopCenter:
                    verticalShift = 0f;
                    horizontalShiftMultiplier = 0.5f;
                    break;
                case MTextAttachmentPoint.TopRight:
                    verticalShift = 0f;
                    horizontalShiftMultiplier = 1f;
                    break;
                case MTextAttachmentPoint.MiddleLeft:
                    verticalShift = -totalHeight / 2f;
                    horizontalShiftMultiplier = 0f;
                    break;
                case MTextAttachmentPoint.MiddleCenter:
                    verticalShift = -totalHeight / 2f;
                    horizontalShiftMultiplier = 0.5f;
                    break;
                case MTextAttachmentPoint.MiddleRight:
                    verticalShift = -totalHeight / 2f;
                    horizontalShiftMultiplier = 1f;
                    break;
                case MTextAttachmentPoint.BottomLeft:
                    verticalShift = -totalHeight;
                    horizontalShiftMultiplier = 0f;
                    break;
                case MTextAttachmentPoint.BottomCenter:
                    verticalShift = -totalHeight;
                    horizontalShiftMultiplier = 0.5f;
                    break;
                case MTextAttachmentPoint.BottomRight:
                    verticalShift = -totalHeight;
                    horizontalShiftMultiplier = 1f;
                    break;
            }

            // Viewport culling (estimate mtext block bounding box using exact character metrics)
            float maxLineWidth = 0f;
            var lineWidths = new float[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                lineWidths[i] = MeasureLineWidthStatic(lines[i], context.Font, screenFontSize);
                maxLineWidth = Math.Max(maxLineWidth, lineWidths[i]);
            }

            // Estimate diagonal culling radius
            float maxDim = Math.Max(maxLineWidth, totalHeight) * 1.5f;
            if (context.IsOffScreen(screenPos - new Vector2(maxDim), screenPos + new Vector2(maxDim))) return;

            float rotationRad = MathF.Atan2(baselineVec.Y, baselineVec.X);

            for (int i = 0; i < lines.Length; i++)
            {
                // Shift along u and v directions matching text baseline rotation and scale
                var lineShift = u * (-lineWidths[i] * horizontalShiftMultiplier) + 
                                v * (verticalShift + i * screenLineOffset);
                var pos = screenPos + lineShift;
                DrawCameraAwareText(context, lines[i], screenFontSize, brush, pos, rotationRad);
            }
        }
    }

    internal static void DrawCameraAwareText(
        DxfRenderContext context,
        string text,
        float fontSize,
        Brush brush,
        Vector2 position,
        float rotation = 0f)
    {
        if (!context.EnableGpuTransforms)
        {
            context.DrawingContext.DrawText(
                text,
                context.Font,
                fontSize,
                brush,
                position,
                rotation: rotation);
            return;
        }

        // The dynamic CAD view maps the drawing's Y-up world coordinates to
        // the canvas' Y-down coordinates on the GPU. Reflect each retained
        // glyph run locally so the later camera reflection keeps the glyphs
        // upright without CPU-projecting or rebuilding them while zooming.
        var textTransform = Matrix4x4.CreateTranslation(-position.X, -position.Y, 0f) *
            Matrix4x4.CreateScale(1f, -1f, 1f) *
            Matrix4x4.CreateTranslation(position.X, position.Y, 0f);
        context.DrawingContext.DrawText(
            text,
            context.Font,
            fontSize,
            brush,
            position,
            textTransform,
            rotation: rotation);
    }

    private static float MeasureLineWidthStatic(string line, ProGPU.Text.TtfFont font, float fontSize)
    {
        float totalWidth = 0f;
        for (int i = 0; i < line.Length; i++)
        {
            ushort idx = font.GetGlyphIndex(line[i]);
            totalWidth += font.GetAdvanceWidth(idx, fontSize);
        }
        return totalWidth;
    }

    private float MeasureLineWidth(string line, ProGPU.Text.TtfFont font, float fontSize)
    {
        return MeasureLineWidthStatic(line, font, fontSize);
    }

    public static string CleanMText(string mtext)
    {
        if (string.IsNullOrEmpty(mtext)) return string.Empty;

        // 1. Unescape standard paragraphs
        var result = mtext.Replace("\\P", "\n").Replace("\\p", "\n");

        // 2. Remove braces { } which are used for nesting styles
        result = result.Replace("{", "").Replace("}", "");

        // 3. Remove single character style tags like \\L, \\l, \\O, \\o, \\K, \\k
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\[L|l|O|o|K|k]", "");

        // 4. Remove parametric tags that end with a semicolon
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\\[A-Za-z0-9_|\.\s\-\^/#]+;", "");

        // 5. Translate AutoCAD special characters
        result = result.Replace("%%d", "°", StringComparison.OrdinalIgnoreCase)
                       .Replace("%%c", "Ø", StringComparison.OrdinalIgnoreCase)
                       .Replace("%%p", "±", StringComparison.OrdinalIgnoreCase)
                       .Replace("%%D", "°")
                       .Replace("%%C", "Ø")
                       .Replace("%%P", "±");

        return result;
    }

    public static void RenderAttributeDefinition(AttributeDefinition attdef, DxfRenderContext context, Matrix4x4 transform)
    {
        if (attdef.Flags.HasFlag(netDxf.Entities.AttributeFlags.Hidden)) return;

        string valStr = attdef.Tag ?? attdef.Value?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(valStr)) return;

        var combined = DxfDocumentRenderer.GetOcsMatrix(attdef.Normal) * transform;
        float rot = (float)(attdef.Rotation * Math.PI / 180.0);
        var origin = new Vector2((float)attdef.Position.X, (float)attdef.Position.Y);
        var screenPos = context.Transform(origin, combined);

        // Project coordinate baseline to find exact screen scale and rotation angle
        var originPlusBaseline = origin + new Vector2(MathF.Cos(rot), MathF.Sin(rot));
        var screenBaselinePt = context.Transform(originPlusBaseline, combined);
        var baselineVec = screenBaselinePt - screenPos;
        float screenScale = baselineVec.Length();
        if (screenScale < 1e-4f) return;

        var u = baselineVec / screenScale;
        var v = new Vector2(-u.Y, u.X);
        if (context.EnableGpuTransforms)
        {
            v = -v;
        }

        float screenFontSize = (float)attdef.Height * screenScale;
        float physicalFontSize = screenFontSize;
        if (context.EnableGpuTransforms) physicalFontSize *= context.Zoom;
        if (!context.IsCompilingStatic && (context.EnableLod ? (physicalFontSize < 4f) : (physicalFontSize < 0.1f))) return;

        float horizontalShiftMultiplier = 0f;
        float verticalShiftMultiplier = 0f;

        switch (attdef.Alignment)
        {
            case netDxf.Entities.TextAlignment.TopLeft:
                horizontalShiftMultiplier = 0f;
                verticalShiftMultiplier = 1.0f;
                break;
            case netDxf.Entities.TextAlignment.TopCenter:
                horizontalShiftMultiplier = 0.5f;
                verticalShiftMultiplier = 1.0f;
                break;
            case netDxf.Entities.TextAlignment.TopRight:
                horizontalShiftMultiplier = 1.0f;
                verticalShiftMultiplier = 1.0f;
                break;
            case netDxf.Entities.TextAlignment.MiddleLeft:
                horizontalShiftMultiplier = 0f;
                verticalShiftMultiplier = 0.5f;
                break;
            case netDxf.Entities.TextAlignment.MiddleCenter:
            case netDxf.Entities.TextAlignment.Middle:
                horizontalShiftMultiplier = 0.5f;
                verticalShiftMultiplier = 0.5f;
                break;
            case netDxf.Entities.TextAlignment.MiddleRight:
                horizontalShiftMultiplier = 1.0f;
                verticalShiftMultiplier = 0.5f;
                break;
            case netDxf.Entities.TextAlignment.BottomLeft:
            case netDxf.Entities.TextAlignment.BaselineLeft:
                horizontalShiftMultiplier = 0f;
                verticalShiftMultiplier = 0f;
                break;
            case netDxf.Entities.TextAlignment.BottomCenter:
            case netDxf.Entities.TextAlignment.BaselineCenter:
            case netDxf.Entities.TextAlignment.Aligned:
            case netDxf.Entities.TextAlignment.Fit:
                horizontalShiftMultiplier = 0.5f;
                verticalShiftMultiplier = 0f;
                break;
            case netDxf.Entities.TextAlignment.BottomRight:
            case netDxf.Entities.TextAlignment.BaselineRight:
                horizontalShiftMultiplier = 1.0f;
                verticalShiftMultiplier = 0f;
                break;
        }

        // Measure text width using exact glyph metrics
        float screenWidth = MeasureLineWidthStatic(valStr, context.Font, screenFontSize);
        float shiftX = -screenWidth * horizontalShiftMultiplier;
        float shiftY = screenFontSize * verticalShiftMultiplier;

        float maxDim = Math.Max(screenWidth, screenFontSize) * 1.5f;
        var minPt = new Vector2(screenPos.X - maxDim, screenPos.Y - maxDim);
        var maxPt = new Vector2(screenPos.X + maxDim, screenPos.Y + maxDim);
        if (context.IsOffScreen(minPt, maxPt)) return;

        // Resolve Brush
        var color = new Vector4(1f, 1f, 1f, 1f); // Default white/fallback
        if (attdef.Color.IsByLayer)
        {
            if (context.LayerColors.TryGetValue(attdef.Layer.Name, out var lColor))
            {
                color = lColor;
            }
            else
            {
                var aci = attdef.Layer.Color;
                color = new Vector4(aci.R / 255f, aci.G / 255f, aci.B / 255f, 1f);
            }
        }
        else
        {
            var aci = attdef.Color;
            color = new Vector4(aci.R / 255f, aci.G / 255f, aci.B / 255f, 1f);
        }
        var brush = new SolidColorBrush(color);

        var drawPos = screenPos + u * shiftX + v * shiftY;
        float rotationRad = MathF.Atan2(baselineVec.Y, baselineVec.X);
        DrawCameraAwareText(context, valStr, screenFontSize, brush, drawPos, rotationRad);
    }
}

public class DxfInsertRenderer : IDxfEntityRenderer
{
    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        if (entity is not Insert insert) return;

        var scale = insert.Scale;
        var pos = insert.Position;
        float radAngle = (float)(insert.Rotation * Math.PI / 180.0);
        var origin = insert.Block.Origin;

        // Correct AutoCAD standard local block transform mapping:
        // Translate by pos (insertion point) in WCS/parent space *after* applying the extrusion normal OCS-to-WCS rotation!
        var localMat = Matrix4x4.CreateTranslation(-(float)origin.X, -(float)origin.Y, -(float)origin.Z) *
                       Matrix4x4.CreateScale((float)scale.X, (float)scale.Y, (float)scale.Z) *
                       Matrix4x4.CreateRotationZ(radAngle) *
                       DxfDocumentRenderer.GetOcsMatrix(insert.Normal) *
                       Matrix4x4.CreateTranslation((float)pos.X, (float)pos.Y, (float)pos.Z);

        // Pre-calculate block bounds once and perform block-level viewport and LOD culling
        var blockBounds = context.GetOrCalculateBlockBounds(insert.Block);
        var combinedMat = localMat * transform;
        var sMin = context.Transform(blockBounds.Min, combinedMat);
        var sMax = context.Transform(blockBounds.Max, combinedMat);

        float minX = Math.Min(sMin.X, sMax.X);
        float minY = Math.Min(sMin.Y, sMax.Y);
        float maxX = Math.Max(sMin.X, sMax.X);
        float maxY = Math.Max(sMin.Y, sMax.Y);

        if (context.IsOffScreen(new Vector2(minX, minY), new Vector2(maxX, maxY)))
        {
            return; // Completely out of bounds, cull early!
        }

        float width = maxX - minX;
        float height = maxY - minY;
        if (context.EnableGpuTransforms)
        {
            width *= context.Zoom;
            height *= context.Zoom;
        }
        if (!context.IsCompilingStatic && (context.EnableLod ? (width < 3.5f && height < 3.5f) : (width < 0.2f && height < 0.2f)))
        {
            return; // Too tiny on the screen, skip traversing child entities!
        }

        context.PushTransform(localMat);

        foreach (var childEntity in insert.Block.Entities)
        {
            DxfDocumentRenderer.RenderEntity(childEntity, context, combinedMat);
        }

        // Render block insert attributes (tags, labels, etc.) using the insert's parent transform but scaling the height by insert Y scale
        foreach (var attr in insert.Attributes)
        {
            DxfTextRenderer.RenderAttribute(attr, context, transform, (float)insert.Scale.Y);
        }

        context.PopTransform();
    }
}

public class DxfViewportRenderer : IDxfEntityRenderer
{
    private Vector2 ProjectToScreenBypassingGpu(Vector2 worldPoint, DxfRenderContext context)
    {
        float localX = worldPoint.X - context.Center.X;
        float localY = worldPoint.Y - context.Center.Y;
        float screenX = localX * context.Zoom + context.ScreenCenter.X + context.Pan.X;
        float screenY = -localY * context.Zoom + context.ScreenCenter.Y + context.Pan.Y;
        return new Vector2(screenX, screenY);
    }

    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        if (entity is not netDxf.Entities.Viewport vp) return;

        // Skip layout viewports that don't project model space
        if (vp.ViewHeight <= 1e-4 || vp.Height <= 1e-4) return;
        if (vp.Status.HasFlag(netDxf.Entities.ViewportStatusFlags.ViewportOff)) return;

        // 1. Draw Viewport Outline Box on Paper
        float halfW = (float)(vp.Width * 0.5);
        float halfH = (float)(vp.Height * 0.5);

        var pMinPaper = new Vector2((float)(vp.Center.X - halfW), (float)(vp.Center.Y - halfH));
        var pMaxPaper = new Vector2((float)(vp.Center.X + halfW), (float)(vp.Center.Y + halfH));

        var screenMin = ProjectToScreenBypassingGpu(pMinPaper, context);
        var screenMax = ProjectToScreenBypassingGpu(pMaxPaper, context);

        float clipX = Math.Min(screenMin.X, screenMax.X);
        float clipY = Math.Min(screenMin.Y, screenMax.Y);
        float clipW = Math.Abs(screenMax.X - screenMin.X);
        float clipH = Math.Abs(screenMax.Y - screenMin.Y);

        var clipRect = new Rect(clipX, clipY, clipW, clipH);

        // Draw boundary border if layer is visible
        var borderPen = context.GetCachedPen(vp, 1f);
        context.DrawingContext.DrawRectangle(null, borderPen, clipRect);

        // 2. Set Up Clipping Rect in Screen Space
        context.DrawingContext.PushClip(clipRect);

        // 3. Construct Model Space to Paper Space Matrix
        float scale = (float)(vp.Height / vp.ViewHeight);
        var viewportMatrix = Matrix4x4.CreateTranslation(-(float)vp.ViewCenter.X, -(float)vp.ViewCenter.Y, 0f) *
                             Matrix4x4.CreateScale(scale, scale, 1f) *
                             Matrix4x4.CreateTranslation((float)vp.Center.X, (float)vp.Center.Y, 0f);

        var combinedTransform = viewportMatrix * transform;

        // 4. Render All Model Space Entities inside this viewport
        var doc = context.Document;
        if (doc != null && doc.Layouts != null && doc.Layouts.Contains("Model"))
        {
            var modelLayout = doc.Layouts["Model"];
            if (modelLayout.AssociatedBlock != null && modelLayout.AssociatedBlock.Entities != null)
            {
                foreach (var modelEntity in modelLayout.AssociatedBlock.Entities)
                {
                    if (modelEntity is netDxf.Entities.Viewport) continue;
                    if (context.ActiveLayers.Contains(modelEntity.Layer.Name))
                    {
                        DxfDocumentRenderer.RenderEntity(modelEntity, context, combinedTransform);
                    }
                }
            }

            // Render cached 3D ACIS solids through this paper-space viewport
            if (context.Cached3dSolids.Count > 0)
            {
                foreach (var solid in context.Cached3dSolids)
                {
                    if (!context.ActiveLayers.Contains(solid.Layer)) continue;

                    var color = new Vector4(1f, 1f, 1f, 1f);
                    if (context.LayerColors.TryGetValue(solid.Layer, out var lColor))
                    {
                        color = lColor;
                    }
                    var brush = new SolidColorBrush(color);
                    var pen = new ProGPU.Vector.Pen(brush, 1.0f);

                    foreach (var edge in solid.Edges)
                    {
                        var screenP1 = context.TransformToScreen3D(edge.StartPoint, combinedTransform);
                        var screenP2 = context.TransformToScreen3D(edge.EndPoint, combinedTransform);
                        
                        float sMinX = Math.Min(screenP1.X, screenP2.X);
                        float sMaxX = Math.Max(screenP1.X, screenP2.X);
                        float sMinY = Math.Min(screenP1.Y, screenP2.Y);
                        float sMaxY = Math.Max(screenP1.Y, screenP2.Y);

                        if (context.IsOffScreen(new Vector2(sMinX, sMinY), new Vector2(sMaxX, sMaxY))) continue;

                        context.DrawingContext.DrawLine(pen, new Vector2(screenP1.X, screenP1.Y), new Vector2(screenP2.X, screenP2.Y));
                    }
                }
            }

            // Render cached MULTILEADERs through this paper-space viewport
            if (context.CachedMLeaders.Count > 0)
            {
                foreach (var mleader in context.CachedMLeaders)
                {
                    if (!context.ActiveLayers.Contains(mleader.Layer)) continue;

                    var color = new Vector4(1f, 1f, 1f, 1f);
                    if (context.LayerColors.TryGetValue(mleader.Layer, out var lColor))
                    {
                        color = lColor;
                    }
                    var brush = new SolidColorBrush(color);
                    var pen = new ProGPU.Vector.Pen(brush, 1.5f);

                    // 1. Draw leader line segments
                    foreach (var line in mleader.LeaderLines)
                    {
                        if (line.Count < 2) continue;

                        var screenPoints = new List<Vector2>();
                        float minX = float.MaxValue;
                        float minY = float.MaxValue;
                        float maxX = float.MinValue;
                        float maxY = float.MinValue;

                        foreach (var pt in line)
                        {
                            var sPt = context.Transform(pt, combinedTransform);
                            screenPoints.Add(sPt);
                            minX = Math.Min(minX, sPt.X);
                            minY = Math.Min(minY, sPt.Y);
                            maxX = Math.Max(maxX, sPt.X);
                            maxY = Math.Max(maxY, sPt.Y);
                        }

                        if (context.IsOffScreen(new Vector2(minX, minY), new Vector2(maxX, maxY))) continue;

                        for (int i = 0; i < screenPoints.Count - 1; i++)
                        {
                            context.DrawingContext.DrawLine(pen, screenPoints[i], screenPoints[i + 1]);
                        }

                        DxfDocumentRenderer.DrawArrowhead(context, screenPoints[0], screenPoints[1], brush, pen, mleader.TextHeight);
                    }

                    // 2. Draw the text label
                    if (!string.IsNullOrEmpty(mleader.TextValue))
                    {
                        string cleanText = DxfTextRenderer.CleanMText(mleader.TextValue);
                        var pos = context.Transform(mleader.TextInsertionPoint, combinedTransform);
                        float fontSize = mleader.TextHeight * context.Zoom;
                        if (fontSize > 0.1f)
                        {
                            DxfTextRenderer.DrawCameraAwareText(
                                context,
                                cleanText,
                                fontSize,
                                brush,
                                pos);
                        }
                    }
                }
            }
        }

        // 5. Pop Clip Rect
        context.DrawingContext.PopClip();
    }
}

public class DxfSolidRenderer : IDxfEntityRenderer
{
    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        if (entity is not netDxf.Entities.Solid solid) return;

        var combined = DxfDocumentRenderer.GetOcsMatrix(solid.Normal) * transform;

        var p1 = context.Transform(new Vector2((float)solid.FirstVertex.X, (float)solid.FirstVertex.Y), combined);
        var p2 = context.Transform(new Vector2((float)solid.SecondVertex.X, (float)solid.SecondVertex.Y), combined);
        var p3 = context.Transform(new Vector2((float)solid.ThirdVertex.X, (float)solid.ThirdVertex.Y), combined);
        var p4 = context.Transform(new Vector2((float)solid.FourthVertex.X, (float)solid.FourthVertex.Y), combined);

        // Viewport culling (bounding box of all 4 points)
        float minX = Math.Min(p1.X, Math.Min(p2.X, Math.Min(p3.X, p4.X)));
        float minY = Math.Min(p1.Y, Math.Min(p2.Y, Math.Min(p3.Y, p4.Y)));
        float maxX = Math.Max(p1.X, Math.Max(p2.X, Math.Max(p3.X, p4.X)));
        float maxY = Math.Max(p1.Y, Math.Max(p2.Y, Math.Max(p3.Y, p4.Y)));
        if (context.IsOffScreen(new Vector2(minX, minY), new Vector2(maxX, maxY))) return;

        var brush = context.GetCachedBrush(solid);
        bool isTriangle = solid.FourthVertex == solid.ThirdVertex || 
            (Math.Abs(p4.X - p3.X) < 1e-5f && Math.Abs(p4.Y - p3.Y) < 1e-5f);

        if (isTriangle)
        {
            context.DrawingContext.FillTriangle(brush, p1, p2, p3);
        }
        else
        {
            context.DrawingContext.FillQuad(brush, p1, p2, p4, p3);
        }
    }
}

public class DxfImageRenderer : IDxfEntityRenderer
{
    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        if (entity is not Image dxfImage) return;

        var combined = DxfDocumentRenderer.GetOcsMatrix(dxfImage.Normal) * transform;

        // Bottom-left in model space
        var bl = new Vector2((float)dxfImage.Position.X, (float)dxfImage.Position.Y);
        // Top-left in model space (Y grows upwards in CAD)
        var tl = new Vector2((float)dxfImage.Position.X, (float)(dxfImage.Position.Y + dxfImage.Height));
        // Bottom-right in model space
        var br = new Vector2((float)(dxfImage.Position.X + dxfImage.Width), (float)dxfImage.Position.Y);

        // Projected to screen coordinates
        var screenTl = context.Transform(tl, combined);
        var screenBr = context.Transform(br, combined);

        float screenW = screenBr.X - screenTl.X;
        float screenH = screenBr.Y - screenTl.Y;

        var rect = new Rect(screenTl, new Vector2(screenW, screenH));

        if (context.IsOffScreen(rect.Position, rect.Position + rect.Size)) return;

        string? filename = null;
        if (dxfImage.Definition != null && !string.IsNullOrEmpty(dxfImage.Definition.File))
        {
            filename = Path.GetFileName(dxfImage.Definition.File);
        }

        // Draw missing image placeholder
        DrawMissingImagePlaceholder(context.DrawingContext, rect, filename ?? "Unknown Image Reference", context.Font);
    }

    private void DrawMissingImagePlaceholder(DrawingContext dc, Rect rect, string filename, ProGPU.Text.TtfFont font)
    {
        // 1. Draw solid background (sleek dark panel background)
        var bgBrush = new SolidColorBrush(new Vector4(0.12f, 0.12f, 0.14f, 0.85f));
        dc.DrawRectangle(bgBrush, null, rect);

        // 2. Draw border
        var borderPen = new Pen(new SolidColorBrush(new Vector4(0.4f, 0.4f, 0.45f, 0.5f)), 1f);
        dc.DrawRectangle(null, borderPen, rect);

        // 3. Draw a centered diagonal cross/hatch to look like standard architectural "missing image" box
        dc.DrawLine(borderPen, rect.Position, rect.Position + rect.Size);
        dc.DrawLine(borderPen, new Vector2(rect.X + rect.Width, rect.Y), new Vector2(rect.X, rect.Y + rect.Height));

        // 4. Draw missing text label centered
        float titleFontSize = Math.Max(8f, Math.Min(12f, rect.Height * 0.12f));
        float subFontSize = Math.Max(7f, Math.Min(9f, rect.Height * 0.08f));

        var textBrush = new SolidColorBrush(new Vector4(0.85f, 0.85f, 0.9f, 0.9f));
        var subBrush = new SolidColorBrush(new Vector4(0.55f, 0.55f, 0.6f, 0.7f));

        string mainText = "🖼️ [Image: " + filename + "]";
        string subText = "Reference Location Missing";

        // Measure text width using 0.55f as font size ratio
        float mainTextW = mainText.Length * titleFontSize * 0.55f;
        float subTextW = subText.Length * subFontSize * 0.55f;

        var mainPos = new Vector2(rect.X + (rect.Width - mainTextW) / 2f, rect.Y + rect.Height / 2f - titleFontSize);
        var subPos = new Vector2(rect.X + (rect.Width - subTextW) / 2f, rect.Y + rect.Height / 2f + subFontSize * 0.5f);

        // Only draw text if the placeholder is large enough
        if (rect.Width > mainTextW + 10f && rect.Height > titleFontSize * 4f)
        {
            dc.DrawText(mainText, font, titleFontSize, textBrush, mainPos);
            dc.DrawText(subText, font, subFontSize, subBrush, subPos);
        }
    }
}

public class DxfFace3dRenderer : IDxfEntityRenderer
{
    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        if (entity is not Face3d face) return;

        var combined = DxfDocumentRenderer.GetOcsMatrix(face.Normal) * transform;

        var p1 = context.Transform(new Vector2((float)face.FirstVertex.X, (float)face.FirstVertex.Y), combined);
        var p2 = context.Transform(new Vector2((float)face.SecondVertex.X, (float)face.SecondVertex.Y), combined);
        var p3 = context.Transform(new Vector2((float)face.ThirdVertex.X, (float)face.ThirdVertex.Y), combined);
        var p4 = context.Transform(new Vector2((float)face.FourthVertex.X, (float)face.FourthVertex.Y), combined);

        // Viewport culling (bounding box of all 4 points)
        float minX = Math.Min(p1.X, Math.Min(p2.X, Math.Min(p3.X, p4.X)));
        float minY = Math.Min(p1.Y, Math.Min(p2.Y, Math.Min(p3.Y, p4.Y)));
        float maxX = Math.Max(p1.X, Math.Max(p2.X, Math.Max(p3.X, p4.X)));
        float maxY = Math.Max(p1.Y, Math.Max(p2.Y, Math.Max(p3.Y, p4.Y)));
        if (context.IsOffScreen(new Vector2(minX, minY), new Vector2(maxX, maxY))) return;

        var brush = context.GetCachedBrush(face);
        bool isTriangle = face.ThirdVertex == face.FourthVertex || 
            (Math.Abs(p4.X - p3.X) < 1e-5f && Math.Abs(p4.Y - p3.Y) < 1e-5f);

        if (isTriangle)
        {
            context.DrawingContext.FillTriangle(brush, p1, p2, p3);
        }
        else
        {
            context.DrawingContext.FillQuad(brush, p1, p2, p3, p4);
        }
    }
}

public class DxfPointRenderer : IDxfEntityRenderer
{
    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        if (entity is not netDxf.Entities.Point point) return;

        var combined = DxfDocumentRenderer.GetOcsMatrix(point.Normal) * transform;
        var p = context.Transform(new Vector2((float)point.Position.X, (float)point.Position.Y), combined);

        // Viewport culling
        if (context.IsOffScreen(p - new Vector2(3f), p + new Vector2(3f))) return;

        var pen = context.GetCachedPen(point, 1f);

        // Draw a small vector cross-hair representing the point in world space
        float size = 1.5f; 
        context.DrawingContext.DrawLine(pen, p - new Vector2(size, 0f), p + new Vector2(size, 0f));
        context.DrawingContext.DrawLine(pen, p - new Vector2(0f, size), p + new Vector2(0f, size));
    }
}

public class DxfWipeoutRenderer : IDxfEntityRenderer
{
    public void Render(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        if (entity is not Wipeout wipeout) return;
        if (wipeout.ClippingBoundary == null || wipeout.ClippingBoundary.Vertexes == null || wipeout.ClippingBoundary.Vertexes.Count < 3) return;

        var combined = DxfDocumentRenderer.GetOcsMatrix(wipeout.Normal) * transform;

        // Transform vertices to screen coordinates and collect bounds for culling
        var points = new Vector2[wipeout.ClippingBoundary.Vertexes.Count];
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        for (int i = 0; i < points.Length; i++)
        {
            var v = wipeout.ClippingBoundary.Vertexes[i];
            points[i] = context.Transform(new Vector2((float)v.X, (float)v.Y), combined);
            minX = Math.Min(minX, points[i].X);
            minY = Math.Min(minY, points[i].Y);
            maxX = Math.Max(maxX, points[i].X);
            maxY = Math.Max(maxY, points[i].Y);
        }

        // Viewport culling
        if (context.IsOffScreen(new Vector2(minX, minY), new Vector2(maxX, maxY))) return;

        // Render as CPU triangle fan with direct batched FillTriangle calls
        var p0 = points[0];
        for (int i = 1; i < points.Length - 1; i++)
        {
            context.DrawingContext.FillTriangle(context.BackgroundBrush, p0, points[i], points[i + 1]);
        }
    }
}
