using System;
using System.Collections.Generic;
using System.Numerics;
using netDxf;
using netDxf.Entities;
using ProGPU.Scene;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace ProGPU.Dxf;

public static class DxfDocumentRenderer
{
    static DxfDocumentRenderer()
    {
        // Register the code pages encoding provider required to parse DXF files 
        // using Windows/ANSI encodings in modern cross-platform .NET.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    private static readonly Dictionary<Type, IDxfEntityRenderer> _renderers = new()
    {
        { typeof(Line), new DxfLineRenderer() },
        { typeof(Circle), new DxfArcCircleRenderer() },
        { typeof(Arc), new DxfArcCircleRenderer() },
        { typeof(Ellipse), new DxfEllipseRenderer() },
        { typeof(LwPolyline), new DxfPolylineRenderer() },
        { typeof(Polyline), new DxfPolylineRenderer() },
        { typeof(Spline), new DxfSplineRenderer() },
        { typeof(netDxf.Entities.Text), new DxfTextRenderer() },
        { typeof(MText), new DxfTextRenderer() },
        { typeof(Insert), new DxfInsertRenderer() },
        { typeof(netDxf.Entities.Solid), new DxfSolidRenderer() },
        
        // Dimensions support
        { typeof(LinearDimension), new DxfDimensionRenderer() },
        { typeof(AlignedDimension), new DxfDimensionRenderer() },
        { typeof(RadialDimension), new DxfDimensionRenderer() },
        { typeof(DiametricDimension), new DxfDimensionRenderer() },
        { typeof(Angular3PointDimension), new DxfDimensionRenderer() },
        { typeof(Angular2LineDimension), new DxfDimensionRenderer() },
        { typeof(OrdinateDimension), new DxfDimensionRenderer() },
        
        // Leaders and Hatches support
        { typeof(Leader), new DxfLeaderRenderer() },
        { typeof(Hatch), new DxfHatchRenderer() },
        { typeof(Image), new DxfImageRenderer() },
        
        // Viewport and Attribute support
        { typeof(netDxf.Entities.Viewport), new DxfViewportRenderer() }
        // { typeof(netDxf.Entities.Attribute), new DxfTextRenderer() }
    };

    public static void Render(DxfDocument doc, DxfRenderContext context)
    {
        context.Reset();
        context.Document = doc;
        
        bool renderedFromLayout = false;
        if (doc.Layouts != null && !string.IsNullOrEmpty(doc.ActiveLayout) && doc.Layouts.Contains(doc.ActiveLayout))
        {
            var layout = doc.Layouts[doc.ActiveLayout];
            if (layout.AssociatedBlock != null && layout.AssociatedBlock.Entities != null && layout.AssociatedBlock.Entities.Count > 0)
            {
                foreach (var entity in layout.AssociatedBlock.Entities)
                {
                    if (context.ActiveLayers.Contains(entity.Layer.Name))
                    {
                        RenderEntity(entity, context, Matrix4x4.Identity);
                    }
                }
                renderedFromLayout = true;
            }
        }

        if (!renderedFromLayout)
        {
            RenderFlatCollections(doc, context);
        }
    }

    private static void RenderFlatCollections(DxfDocument doc, DxfRenderContext context)
    {
        foreach (var line in doc.Lines)
        {
            if (context.ActiveLayers.Contains(line.Layer.Name))
                RenderEntity(line, context, Matrix4x4.Identity);
        }
        foreach (var circle in doc.Circles)
        {
            if (context.ActiveLayers.Contains(circle.Layer.Name))
                RenderEntity(circle, context, Matrix4x4.Identity);
        }
        foreach (var arc in doc.Arcs)
        {
            if (context.ActiveLayers.Contains(arc.Layer.Name))
                RenderEntity(arc, context, Matrix4x4.Identity);
        }
        foreach (var ellipse in doc.Ellipses)
        {
            if (context.ActiveLayers.Contains(ellipse.Layer.Name))
                RenderEntity(ellipse, context, Matrix4x4.Identity);
        }
        foreach (var lwPoly in doc.LwPolylines)
        {
            if (context.ActiveLayers.Contains(lwPoly.Layer.Name))
                RenderEntity(lwPoly, context, Matrix4x4.Identity);
        }
        foreach (var poly in doc.Polylines)
        {
            if (context.ActiveLayers.Contains(poly.Layer.Name))
                RenderEntity(poly, context, Matrix4x4.Identity);
        }
        foreach (var spline in doc.Splines)
        {
            if (context.ActiveLayers.Contains(spline.Layer.Name))
                RenderEntity(spline, context, Matrix4x4.Identity);
        }
        foreach (var text in doc.Texts)
        {
            if (context.ActiveLayers.Contains(text.Layer.Name))
                RenderEntity(text, context, Matrix4x4.Identity);
        }
        foreach (var mtext in doc.MTexts)
        {
            if (context.ActiveLayers.Contains(mtext.Layer.Name))
                RenderEntity(mtext, context, Matrix4x4.Identity);
        }
        foreach (var insert in doc.Inserts)
        {
            if (context.ActiveLayers.Contains(insert.Layer.Name))
                RenderEntity(insert, context, Matrix4x4.Identity);
        }
        foreach (var solid in doc.Solids)
        {
            if (context.ActiveLayers.Contains(solid.Layer.Name))
                RenderEntity(solid, context, Matrix4x4.Identity);
        }
        foreach (var dim in doc.Dimensions)
        {
            if (context.ActiveLayers.Contains(dim.Layer.Name))
                RenderEntity(dim, context, Matrix4x4.Identity);
        }
        foreach (var leader in doc.Leaders)
        {
            if (context.ActiveLayers.Contains(leader.Layer.Name))
                RenderEntity(leader, context, Matrix4x4.Identity);
        }
        foreach (var hatch in doc.Hatches)
        {
            if (context.ActiveLayers.Contains(hatch.Layer.Name))
                RenderEntity(hatch, context, Matrix4x4.Identity);
        }
        foreach (var image in doc.Images)
        {
            if (context.ActiveLayers.Contains(image.Layer.Name))
                RenderEntity(image, context, Matrix4x4.Identity);
        }
    }

    public static void RenderEntity(EntityObject entity, DxfRenderContext context, Matrix4x4 transform)
    {
        if (_renderers.TryGetValue(entity.GetType(), out var renderer))
        {
            renderer.Render(entity, context, transform);
        }
    }

    /// <summary>
    /// Computes the exact 2D Cartesian bounding box of all active visible entities in the document.
    /// Supports nested block inserts.
    /// </summary>
    public static (Vector2 Min, Vector2 Max) CalculateBounds(DxfDocument doc)
    {
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        bool hasData = false;

        bool calculatedFromLayout = false;
        if (doc.Layouts != null && !string.IsNullOrEmpty(doc.ActiveLayout) && doc.Layouts.Contains(doc.ActiveLayout))
        {
            var layout = doc.Layouts[doc.ActiveLayout];
            if (layout.AssociatedBlock != null && layout.AssociatedBlock.Entities != null && layout.AssociatedBlock.Entities.Count > 0)
            {
                foreach (var entity in layout.AssociatedBlock.Entities)
                {
                    AccumulateEntityBounds(entity, Matrix4x4.Identity, ref min, ref max, ref hasData);
                }
                calculatedFromLayout = true;
            }
        }

        if (!calculatedFromLayout)
        {
            AccumulateFlatCollectionsBounds(doc, ref min, ref max, ref hasData);
        }

        if (!hasData)
        {
            return (new Vector2(-100f, -100f), new Vector2(100f, 100f));
        }

        return (min, max);
    }

    private static void AccumulateFlatCollectionsBounds(DxfDocument doc, ref Vector2 min, ref Vector2 max, ref bool hasData)
    {
        foreach (var line in doc.Lines)
            AccumulateEntityBounds(line, Matrix4x4.Identity, ref min, ref max, ref hasData);
        foreach (var circle in doc.Circles)
            AccumulateEntityBounds(circle, Matrix4x4.Identity, ref min, ref max, ref hasData);
        foreach (var arc in doc.Arcs)
            AccumulateEntityBounds(arc, Matrix4x4.Identity, ref min, ref max, ref hasData);
        foreach (var ellipse in doc.Ellipses)
            AccumulateEntityBounds(ellipse, Matrix4x4.Identity, ref min, ref max, ref hasData);
        foreach (var lwPoly in doc.LwPolylines)
            AccumulateEntityBounds(lwPoly, Matrix4x4.Identity, ref min, ref max, ref hasData);
        foreach (var poly in doc.Polylines)
            AccumulateEntityBounds(poly, Matrix4x4.Identity, ref min, ref max, ref hasData);
        foreach (var spline in doc.Splines)
            AccumulateEntityBounds(spline, Matrix4x4.Identity, ref min, ref max, ref hasData);
        foreach (var text in doc.Texts)
            AccumulateEntityBounds(text, Matrix4x4.Identity, ref min, ref max, ref hasData);
        foreach (var mtext in doc.MTexts)
            AccumulateEntityBounds(mtext, Matrix4x4.Identity, ref min, ref max, ref hasData);
        foreach (var insert in doc.Inserts)
            AccumulateEntityBounds(insert, Matrix4x4.Identity, ref min, ref max, ref hasData);
        foreach (var solid in doc.Solids)
            AccumulateEntityBounds(solid, Matrix4x4.Identity, ref min, ref max, ref hasData);
        foreach (var dim in doc.Dimensions)
            AccumulateEntityBounds(dim, Matrix4x4.Identity, ref min, ref max, ref hasData);
        foreach (var leader in doc.Leaders)
            AccumulateEntityBounds(leader, Matrix4x4.Identity, ref min, ref max, ref hasData);
        foreach (var hatch in doc.Hatches)
            AccumulateEntityBounds(hatch, Matrix4x4.Identity, ref min, ref max, ref hasData);
        foreach (var image in doc.Images)
            AccumulateEntityBounds(image, Matrix4x4.Identity, ref min, ref max, ref hasData);
    }

    public static void AccumulateAttributeBounds(netDxf.Entities.Attribute attr, Matrix4x4 transform, ref Vector2 min, ref Vector2 max, ref bool hasData)
    {
        var v3 = new Vector3((float)attr.Position.X, (float)attr.Position.Y, 0f);
        var v3Transformed = Vector3.Transform(v3, transform);
        
        min.X = Math.Min(min.X, v3Transformed.X);
        min.Y = Math.Min(min.Y, v3Transformed.Y);
        
        max.X = Math.Max(max.X, v3Transformed.X);
        max.Y = Math.Max(max.Y, v3Transformed.Y);
        
        hasData = true;
    }

    private static void AccumulateEntityBounds(EntityObject entity, Matrix4x4 transform, ref Vector2 min, ref Vector2 max, ref bool hasData)
    {
        if (entity is Line line)
        {
            UpdateBounds(new Vector2((float)line.StartPoint.X, (float)line.StartPoint.Y), transform, ref min, ref max, ref hasData);
            UpdateBounds(new Vector2((float)line.EndPoint.X, (float)line.EndPoint.Y), transform, ref min, ref max, ref hasData);
        }
        else if (entity is Circle circle)
        {
            var center = new Vector2((float)circle.Center.X, (float)circle.Center.Y);
            float r = (float)circle.Radius;
            UpdateBounds(center + new Vector2(-r, -r), transform, ref min, ref max, ref hasData);
            UpdateBounds(center + new Vector2(r, r), transform, ref min, ref max, ref hasData);
        }
        else if (entity is Arc arc)
        {
            var center = new Vector2((float)arc.Center.X, (float)arc.Center.Y);
            float r = (float)arc.Radius;
            UpdateBounds(center + new Vector2(-r, -r), transform, ref min, ref max, ref hasData);
            UpdateBounds(center + new Vector2(r, r), transform, ref min, ref max, ref hasData);
        }
        else if (entity is Ellipse ellipse)
        {
            var center = new Vector2((float)ellipse.Center.X, (float)ellipse.Center.Y);
            float rx = (float)ellipse.MajorAxis;
            float ry = (float)ellipse.MinorAxis;
            float r = Math.Max(rx, ry);
            UpdateBounds(center + new Vector2(-r, -r), transform, ref min, ref max, ref hasData);
            UpdateBounds(center + new Vector2(r, r), transform, ref min, ref max, ref hasData);
        }
        else if (entity is LwPolyline lwPolyline)
        {
            foreach (var v in lwPolyline.Vertexes)
            {
                UpdateBounds(new Vector2((float)v.Position.X, (float)v.Position.Y), transform, ref min, ref max, ref hasData);
            }
        }
        else if (entity is Polyline polyline)
        {
            foreach (var v in polyline.Vertexes)
            {
                UpdateBounds(new Vector2((float)v.Position.X, (float)v.Position.Y), transform, ref min, ref max, ref hasData);
            }
        }
        else if (entity is Spline spline)
        {
            foreach (var v in spline.ControlPoints)
            {
                UpdateBounds(new Vector2((float)v.Position.X, (float)v.Position.Y), transform, ref min, ref max, ref hasData);
            }
        }
        else if (entity is netDxf.Entities.Text text)
        {
            UpdateBounds(new Vector2((float)text.Position.X, (float)text.Position.Y), transform, ref min, ref max, ref hasData);
        }
        else if (entity is MText mtext)
        {
            UpdateBounds(new Vector2((float)mtext.Position.X, (float)mtext.Position.Y), transform, ref min, ref max, ref hasData);
        }
        else if (entity is Insert insert)
        {
            var scale = insert.Scale;
            var pos = insert.Position;
            float radAngle = (float)(insert.Rotation * Math.PI / 180.0);
            var origin = insert.Block.Origin;

            var localMat = Matrix4x4.CreateTranslation(-(float)origin.X, -(float)origin.Y, -(float)origin.Z) *
                           Matrix4x4.CreateScale((float)scale.X, (float)scale.Y, (float)scale.Z) *
                           Matrix4x4.CreateRotationZ(radAngle) *
                           GetOcsMatrix(insert.Normal) *
                           Matrix4x4.CreateTranslation((float)pos.X, (float)pos.Y, (float)pos.Z);

            var combinedMat = localMat * transform;

            foreach (var childEntity in insert.Block.Entities)
            {
                AccumulateEntityBounds(childEntity, combinedMat, ref min, ref max, ref hasData);
            }
            foreach (var attr in insert.Attributes)
            {
                AccumulateAttributeBounds(attr, combinedMat, ref min, ref max, ref hasData);
            }
        }
        else if (entity is netDxf.Entities.Solid solid)
        {
            UpdateBounds(new Vector2((float)solid.FirstVertex.X, (float)solid.FirstVertex.Y), transform, ref min, ref max, ref hasData);
            UpdateBounds(new Vector2((float)solid.SecondVertex.X, (float)solid.SecondVertex.Y), transform, ref min, ref max, ref hasData);
            UpdateBounds(new Vector2((float)solid.ThirdVertex.X, (float)solid.ThirdVertex.Y), transform, ref min, ref max, ref hasData);
            UpdateBounds(new Vector2((float)solid.FourthVertex.X, (float)solid.FourthVertex.Y), transform, ref min, ref max, ref hasData);
        }
        else if (entity is Dimension dimension)
        {
            if (dimension.Block != null)
            {
                foreach (var childEntity in dimension.Block.Entities)
                {
                    AccumulateEntityBounds(childEntity, transform, ref min, ref max, ref hasData);
                }
            }
        }
        else if (entity is Leader leader)
        {
            foreach (var v in leader.Vertexes)
            {
                UpdateBounds(new Vector2((float)v.X, (float)v.Y), transform, ref min, ref max, ref hasData);
            }
        }
        else if (entity is Hatch hatch)
        {
            if (hatch.BoundaryPaths != null)
            {
                foreach (var bp in hatch.BoundaryPaths)
                {
                    if (bp.Entities == null) continue;
                    foreach (var childEntity in bp.Entities)
                    {
                        AccumulateEntityBounds(childEntity, transform, ref min, ref max, ref hasData);
                    }
                }
            }
        }
        else if (entity is Image image)
        {
            UpdateBounds(new Vector2((float)image.Position.X, (float)image.Position.Y), transform, ref min, ref max, ref hasData);
            UpdateBounds(new Vector2((float)(image.Position.X + image.Width), (float)(image.Position.Y + image.Height)), transform, ref min, ref max, ref hasData);
        }
        else if (entity is netDxf.Entities.Viewport vp)
        {
            if (vp.Width > 0 && vp.Height > 0)
            {
                float halfW = (float)(vp.Width * 0.5);
                float halfH = (float)(vp.Height * 0.5);
                UpdateBounds(new Vector2((float)(vp.Center.X - halfW), (float)(vp.Center.Y - halfH)), transform, ref min, ref max, ref hasData);
                UpdateBounds(new Vector2((float)(vp.Center.X + halfW), (float)(vp.Center.Y + halfH)), transform, ref min, ref max, ref hasData);
            }
        }
    }

    private static void UpdateBounds(Vector2 pt, Matrix4x4 mat, ref Vector2 min, ref Vector2 max, ref bool hasData)
    {
        var v3 = new Vector3(pt.X, pt.Y, 0f);
        var v3Transformed = Vector3.Transform(v3, mat);
        
        min.X = Math.Min(min.X, v3Transformed.X);
        min.Y = Math.Min(min.Y, v3Transformed.Y);
        
        max.X = Math.Max(max.X, v3Transformed.X);
        max.Y = Math.Max(max.Y, v3Transformed.Y);
        
        hasData = true;
    }

    public static Matrix4x4 GetOcsMatrix(netDxf.Vector3 normal)
    {
        var N = Vector3.Normalize(new Vector3((float)normal.X, (float)normal.Y, (float)normal.Z));
        
        Vector3 Wx;
        Vector3 Wy;
        
        const float limit = 1.0f / 64.0f;
        if (Math.Abs(N.X) < limit && Math.Abs(N.Y) < limit)
        {
            Wx = Vector3.Cross(new Vector3(0f, 1f, 0f), N);
        }
        else
        {
            Wx = Vector3.Cross(new Vector3(0f, 0f, 1f), N);
        }
        
        Wx = Vector3.Normalize(Wx);
        Wy = Vector3.Normalize(Vector3.Cross(N, Wx));
        
        return new Matrix4x4(
            Wx.X, Wx.Y, Wx.Z, 0f,
            Wy.X, Wy.Y, Wy.Z, 0f,
            N.X,  N.Y,  N.Z,  0f,
            0f,   0f,   0f,   1f
        );
    }
}
