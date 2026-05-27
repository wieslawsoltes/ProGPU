using System;
using System.Collections.Generic;
using System.Numerics;
using netDxf;
using netDxf.Entities;
using ProGPU.Scene;
using ProGPU.Vector;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

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
        { typeof(netDxf.Entities.Viewport), new DxfViewportRenderer() },
        { typeof(Face3d), new DxfFace3dRenderer() },
        { typeof(netDxf.Entities.Point), new DxfPointRenderer() },
        { typeof(Wipeout), new DxfWipeoutRenderer() }
    };

    public static void Render(DxfDocument doc, DxfRenderContext context)
    {
        context.Reset();
        context.Document = doc;
        
        if (context.EnableFlattening)
        {
            if (context.FlatWcsEntities.Count == 0 || context.CachedActiveLayout != doc.ActiveLayout)
            {
                context.FlattenDxfEntities(doc);
            }

            foreach (var flatEntity in context.FlatWcsEntities)
            {
                // Check parent layers visibility first
                bool isParentVisible = true;
                if (flatEntity.ParentInsertLayers != null)
                {
                    foreach (var pLayer in flatEntity.ParentInsertLayers)
                    {
                        if (!context.ActiveLayers.Contains(pLayer))
                        {
                            isParentVisible = false;
                            break;
                        }
                    }
                }
                if (!isParentVisible) continue;

                if (flatEntity.Entity is netDxf.Entities.Attribute attr)
                {
                    if (context.ActiveLayers.Contains(attr.Layer.Name))
                    {
                        DxfTextRenderer.RenderAttribute(attr, context, flatEntity.Transform, flatEntity.ScaleY);
                    }
                }
                else if (flatEntity.Entity is EntityObject entity)
                {
                    if (context.ActiveLayers.Contains(entity.Layer.Name))
                    {
                        RenderEntity(entity, context, flatEntity.Transform);
                    }
                }
            }
        }
        else
        {
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

        // Render 3D ACIS Solids if cached in context via GPU projection (Model space only)
        if (context.Cached3dSolids.Count > 0 && (string.IsNullOrEmpty(doc.ActiveLayout) || string.Equals(doc.ActiveLayout, "Model", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var solid in context.Cached3dSolids)
            {
                if (!context.ActiveLayers.Contains(solid.Layer)) continue;

                // Resolve layer color
                var color = new Vector4(1f, 1f, 1f, 1f); // Default white
                if (context.LayerColors.TryGetValue(solid.Layer, out var lColor))
                {
                    color = lColor;
                }
                var brush = new SolidColorBrush(color);
                var pen = new ProGPU.Vector.Pen(brush, 1.0f);

                foreach (var edge in solid.Edges)
                {
                    // Project 3D coordinate to screen space using our custom 3D projection, applying active CurrentTransform
                    var screenP1 = context.TransformToScreen3D(edge.StartPoint, context.CurrentTransform);
                    var screenP2 = context.TransformToScreen3D(edge.EndPoint, context.CurrentTransform);
                    
                    // Simple frustum culling on screen coordinates
                    float minX = Math.Min(screenP1.X, screenP2.X);
                    float minY = Math.Min(screenP1.Y, screenP2.Y);
                    float maxX = Math.Max(screenP1.X, screenP2.X);
                    float maxY = Math.Max(screenP1.Y, screenP2.Y);
                    if (context.IsOffScreen(new Vector2(minX, minY), new Vector2(maxX, maxY))) continue;

                    // Draw via high-performance GPU 3D Line Projector (sType == 8)
                    context.DrawingContext.DrawLine3D(pen, screenP1, screenP2);
                }
            }
        }

        // Render MULTILEADER entities if cached in context
        if (context.CachedMLeaders.Count > 0)
        {
            foreach (var mleader in context.CachedMLeaders)
            {
                if (!context.ActiveLayers.Contains(mleader.Layer)) continue;

                // Resolve layer color
                var color = new Vector4(1f, 1f, 1f, 1f); // Default white
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

                    // Gather screen space points, applying active CurrentTransform
                    var screenPoints = new List<Vector2>();
                    foreach (var pt in line)
                    {
                        screenPoints.Add(context.Transform(pt, context.CurrentTransform));
                    }

                    // Draw segments
                    for (int i = 0; i < screenPoints.Count - 1; i++)
                    {
                        context.DrawingContext.DrawLine(pen, screenPoints[i], screenPoints[i + 1]);
                    }

                    // Draw standard CAD arrowhead at V0 pointing towards V1
                    DrawArrowhead(context, screenPoints[0], screenPoints[1], brush, pen, mleader.TextHeight);
                }

                // 2. Draw the text label
                if (!string.IsNullOrEmpty(mleader.TextValue))
                {
                    // Clean MText formatting using DxfTextRenderer.CleanMText
                    string cleanText = DxfTextRenderer.CleanMText(mleader.TextValue);
                    
                    var pos = context.Transform(mleader.TextInsertionPoint, context.CurrentTransform);
                    
                    // Render using default font and scale
                    float fontSize = mleader.TextHeight * context.Zoom;
                    if (fontSize > 0.1f)
                    {
                        context.DrawingContext.DrawText(
                            cleanText, 
                            context.Font, 
                            fontSize, 
                            brush, 
                            pos
                        );
                    }
                }
            }
        }
    }

    private static void DrawArrowhead(DxfRenderContext context, Vector2 v0, Vector2 v1, ProGPU.Vector.Brush brush, ProGPU.Vector.Pen pen, float textHeight)
    {
        var diff = v0 - v1;
        float len = diff.Length();
        if (len < 0.001f) return;
        
        var dir = Vector2.Normalize(diff);
        var perp = new Vector2(-dir.Y, dir.X);
        
        float arrowLength = Math.Clamp(textHeight * context.Zoom * 0.8f, 6f, 30f);
        float arrowWidth = arrowLength * 0.35f;
        
        var backCenter = v0 - dir * arrowLength;
        var corner1 = backCenter + perp * arrowWidth;
        var corner2 = backCenter - perp * arrowWidth;
        
        // Construct SVG path data for the solid arrowhead
        string svg = $"M {v0.X.ToString(System.Globalization.CultureInfo.InvariantCulture)},{v0.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                     $"L {corner1.X.ToString(System.Globalization.CultureInfo.InvariantCulture)},{corner1.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                     $"L {corner2.X.ToString(System.Globalization.CultureInfo.InvariantCulture)},{corner2.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)} Z";
                     
        var path = ProGPU.Vector.PathGeometry.Parse(svg);
        context.DrawingContext.DrawPath(brush, pen, path);
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
        foreach (var point in doc.Points)
        {
            if (context.ActiveLayers.Contains(point.Layer.Name))
                RenderEntity(point, context, Matrix4x4.Identity);
        }
        foreach (var face in doc.Faces3d)
        {
            if (context.ActiveLayers.Contains(face.Layer.Name))
                RenderEntity(face, context, Matrix4x4.Identity);
        }
        foreach (var attdef in doc.AttributeDefinitions)
        {
            if (context.ActiveLayers.Contains(attdef.Layer.Name))
                DxfTextRenderer.RenderAttributeDefinition(attdef, context, Matrix4x4.Identity);
        }
        foreach (var wipeout in doc.Wipeouts)
        {
            if (context.ActiveLayers.Contains(wipeout.Layer.Name))
                RenderEntity(wipeout, context, Matrix4x4.Identity);
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
    public static (Vector2 Min, Vector2 Max) CalculateBounds(DxfDocument doc, HashSet<string>? activeLayers = null)
    {
        return CalculateBounds(doc, null, activeLayers);
    }

    public static (Vector2 Min, Vector2 Max) CalculateBounds(DxfDocument doc, DxfRenderContext? context, HashSet<string>? activeLayers = null)
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
                    if (activeLayers == null || activeLayers.Contains(entity.Layer.Name))
                    {
                        AccumulateEntityBounds(entity, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
                    }
                }
                calculatedFromLayout = true;
            }
        }

        if (!calculatedFromLayout)
        {
            AccumulateFlatCollectionsBounds(doc, ref min, ref max, ref hasData, activeLayers);
        }

        if (context != null)
        {
            // Accumulate bounds from Cached3dSolids in WCS space (using context.CurrentTransform)
            foreach (var solid in context.Cached3dSolids)
            {
                if (activeLayers != null && !activeLayers.Contains(solid.Layer)) continue;
                foreach (var edge in solid.Edges)
                {
                    var p1 = Vector3.Transform(edge.StartPoint, context.CurrentTransform);
                    var p2 = Vector3.Transform(edge.EndPoint, context.CurrentTransform);
                    
                    min.X = Math.Min(min.X, Math.Min(p1.X, p2.X));
                    min.Y = Math.Min(min.Y, Math.Min(p1.Y, p2.Y));
                    
                    max.X = Math.Max(max.X, Math.Max(p1.X, p2.X));
                    max.Y = Math.Max(max.Y, Math.Max(p1.Y, p2.Y));
                    hasData = true;
                }
            }

            // Accumulate bounds from CachedMLeaders in WCS space (using context.CurrentTransform)
            foreach (var mleader in context.CachedMLeaders)
            {
                if (activeLayers != null && !activeLayers.Contains(mleader.Layer)) continue;
                foreach (var line in mleader.LeaderLines)
                {
                    foreach (var pt in line)
                    {
                        var p = Vector3.Transform(pt, context.CurrentTransform);
                        min.X = Math.Min(min.X, p.X);
                        min.Y = Math.Min(min.Y, p.Y);
                        max.X = Math.Max(max.X, p.X);
                        max.Y = Math.Max(max.Y, p.Y);
                        hasData = true;
                    }
                }

                if (!string.IsNullOrEmpty(mleader.TextValue))
                {
                    var p = Vector3.Transform(mleader.TextInsertionPoint, context.CurrentTransform);
                    min.X = Math.Min(min.X, p.X);
                    min.Y = Math.Min(min.Y, p.Y);
                    max.X = Math.Max(max.X, p.X);
                    max.Y = Math.Max(max.Y, p.Y);
                    hasData = true;
                }
            }
        }

        if (!hasData)
        {
            return (new Vector2(-100f, -100f), new Vector2(100f, 100f));
        }

        return (min, max);
    }

    private static void AccumulateFlatCollectionsBounds(DxfDocument doc, ref Vector2 min, ref Vector2 max, ref bool hasData, HashSet<string>? activeLayers = null)
    {
        foreach (var line in doc.Lines)
            AccumulateEntityBounds(line, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var circle in doc.Circles)
            AccumulateEntityBounds(circle, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var arc in doc.Arcs)
            AccumulateEntityBounds(arc, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var ellipse in doc.Ellipses)
            AccumulateEntityBounds(ellipse, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var lwPoly in doc.LwPolylines)
            AccumulateEntityBounds(lwPoly, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var poly in doc.Polylines)
            AccumulateEntityBounds(poly, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var spline in doc.Splines)
            AccumulateEntityBounds(spline, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var text in doc.Texts)
            AccumulateEntityBounds(text, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var mtext in doc.MTexts)
            AccumulateEntityBounds(mtext, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var insert in doc.Inserts)
            AccumulateEntityBounds(insert, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var solid in doc.Solids)
            AccumulateEntityBounds(solid, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var dim in doc.Dimensions)
            AccumulateEntityBounds(dim, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var leader in doc.Leaders)
            AccumulateEntityBounds(leader, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var hatch in doc.Hatches)
            AccumulateEntityBounds(hatch, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var image in doc.Images)
            AccumulateEntityBounds(image, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var point in doc.Points)
            AccumulateEntityBounds(point, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var face in doc.Faces3d)
            AccumulateEntityBounds(face, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var attdef in doc.AttributeDefinitions)
            AccumulateAttributeDefinitionBounds(attdef, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
        foreach (var wipeout in doc.Wipeouts)
            AccumulateEntityBounds(wipeout, Matrix4x4.Identity, ref min, ref max, ref hasData, activeLayers);
    }

    public static void AccumulateAttributeBounds(netDxf.Entities.Attribute attr, Matrix4x4 transform, ref Vector2 min, ref Vector2 max, ref bool hasData, HashSet<string>? activeLayers = null)
    {
        if (activeLayers != null && !activeLayers.Contains(attr.Layer.Name)) return;
        if (attr.Flags.HasFlag(netDxf.Entities.AttributeFlags.Hidden)) return;

        var v3 = new Vector3((float)attr.Position.X, (float)attr.Position.Y, 0f);
        var v3Transformed = Vector3.Transform(v3, transform);
        
        min.X = Math.Min(min.X, v3Transformed.X);
        min.Y = Math.Min(min.Y, v3Transformed.Y);
        
        max.X = Math.Max(max.X, v3Transformed.X);
        max.Y = Math.Max(max.Y, v3Transformed.Y);
        
        hasData = true;
    }

    public static void AccumulateAttributeDefinitionBounds(AttributeDefinition attdef, Matrix4x4 transform, ref Vector2 min, ref Vector2 max, ref bool hasData, HashSet<string>? activeLayers = null)
    {
        if (activeLayers != null && !activeLayers.Contains(attdef.Layer.Name)) return;
        if (attdef.Flags.HasFlag(netDxf.Entities.AttributeFlags.Hidden)) return;

        var combined = GetOcsMatrix(attdef.Normal) * transform;
        var v3 = new Vector3((float)attdef.Position.X, (float)attdef.Position.Y, 0f);
        var v3Transformed = Vector3.Transform(v3, combined);
        
        var pt = new Vector2(v3Transformed.X, v3Transformed.Y);
        if (!hasData)
        {
            min = pt;
            max = pt;
            hasData = true;
        }
        else
        {
            min = Vector2.Min(min, pt);
            max = Vector2.Max(max, pt);
        }
    }

    private static void AccumulateEntityBounds(EntityObject entity, Matrix4x4 transform, ref Vector2 min, ref Vector2 max, ref bool hasData, HashSet<string>? activeLayers = null)
    {
        if (activeLayers != null && !activeLayers.Contains(entity.Layer.Name)) return;
        if (entity is Line line)
        {
            UpdateBounds(new Vector2((float)line.StartPoint.X, (float)line.StartPoint.Y), transform, ref min, ref max, ref hasData);
            UpdateBounds(new Vector2((float)line.EndPoint.X, (float)line.EndPoint.Y), transform, ref min, ref max, ref hasData);
        }
        else if (entity is Circle circle)
        {
            var combined = GetOcsMatrix(circle.Normal) * transform;
            var center = new Vector2((float)circle.Center.X, (float)circle.Center.Y);
            float r = (float)circle.Radius;
            UpdateBounds(center + new Vector2(-r, -r), combined, ref min, ref max, ref hasData);
            UpdateBounds(center + new Vector2(r, r), combined, ref min, ref max, ref hasData);
        }
        else if (entity is Arc arc)
        {
            var combined = GetOcsMatrix(arc.Normal) * transform;
            var center = new Vector2((float)arc.Center.X, (float)arc.Center.Y);
            float r = (float)arc.Radius;
            UpdateBounds(center + new Vector2(-r, -r), combined, ref min, ref max, ref hasData);
            UpdateBounds(center + new Vector2(r, r), combined, ref min, ref max, ref hasData);
        }
        else if (entity is Ellipse ellipse)
        {
            var combined = GetOcsMatrix(ellipse.Normal) * transform;
            var center = new Vector2((float)ellipse.Center.X, (float)ellipse.Center.Y);
            float rx = (float)ellipse.MajorAxis;
            float ry = (float)ellipse.MinorAxis;
            float r = Math.Max(rx, ry);
            UpdateBounds(center + new Vector2(-r, -r), combined, ref min, ref max, ref hasData);
            UpdateBounds(center + new Vector2(r, r), combined, ref min, ref max, ref hasData);
        }
        else if (entity is LwPolyline lwPolyline)
        {
            var combined = GetOcsMatrix(lwPolyline.Normal) * transform;
            foreach (var v in lwPolyline.Vertexes)
            {
                UpdateBounds(new Vector2((float)v.Position.X, (float)v.Position.Y), combined, ref min, ref max, ref hasData);
            }
        }
        else if (entity is Polyline polyline)
        {
            var combined = GetOcsMatrix(polyline.Normal) * transform;
            foreach (var v in polyline.Vertexes)
            {
                UpdateBounds(new Vector2((float)v.Position.X, (float)v.Position.Y), combined, ref min, ref max, ref hasData);
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
            var combined = GetOcsMatrix(text.Normal) * transform;
            UpdateBounds(new Vector2((float)text.Position.X, (float)text.Position.Y), combined, ref min, ref max, ref hasData);
        }
        else if (entity is MText mtext)
        {
            var combined = GetOcsMatrix(mtext.Normal) * transform;
            UpdateBounds(new Vector2((float)mtext.Position.X, (float)mtext.Position.Y), combined, ref min, ref max, ref hasData);
        }
        else if (entity is Insert insert)
        {
            var scale = insert.Scale;
            var pos = insert.Position;
            float radAngle = (float)(insert.Rotation * Math.PI / 180.0);
            var origin = insert.Block.Origin;

            // Correct AutoCAD standard local block transform mapping:
            // Translate by pos (insertion point) in WCS/parent space *after* applying the extrusion normal OCS-to-WCS rotation!
            var localMat = Matrix4x4.CreateTranslation(-(float)origin.X, -(float)origin.Y, -(float)origin.Z) *
                           Matrix4x4.CreateScale((float)scale.X, (float)scale.Y, (float)scale.Z) *
                           Matrix4x4.CreateRotationZ(radAngle) *
                           GetOcsMatrix(insert.Normal) *
                           Matrix4x4.CreateTranslation((float)pos.X, (float)pos.Y, (float)pos.Z);

            var combinedMat = localMat * transform;

            foreach (var childEntity in insert.Block.Entities)
            {
                AccumulateEntityBounds(childEntity, combinedMat, ref min, ref max, ref hasData, activeLayers);
            }
            foreach (var attr in insert.Attributes)
            {
                AccumulateAttributeBounds(attr, transform, ref min, ref max, ref hasData, activeLayers);
            }
        }
        else if (entity is netDxf.Entities.Solid solid)
        {
            var combined = GetOcsMatrix(solid.Normal) * transform;
            UpdateBounds(new Vector2((float)solid.FirstVertex.X, (float)solid.FirstVertex.Y), combined, ref min, ref max, ref hasData);
            UpdateBounds(new Vector2((float)solid.SecondVertex.X, (float)solid.SecondVertex.Y), combined, ref min, ref max, ref hasData);
            UpdateBounds(new Vector2((float)solid.ThirdVertex.X, (float)solid.ThirdVertex.Y), combined, ref min, ref max, ref hasData);
            UpdateBounds(new Vector2((float)solid.FourthVertex.X, (float)solid.FourthVertex.Y), combined, ref min, ref max, ref hasData);
        }
        else if (entity is Dimension dimension)
        {
            if (dimension.Block != null)
            {
                foreach (var childEntity in dimension.Block.Entities)
                {
                    AccumulateEntityBounds(childEntity, transform, ref min, ref max, ref hasData, activeLayers);
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
                        AccumulateEntityBounds(childEntity, transform, ref min, ref max, ref hasData, activeLayers);
                    }
                }
            }
        }
        else if (entity is Image image)
        {
            var combined = GetOcsMatrix(image.Normal) * transform;
            UpdateBounds(new Vector2((float)image.Position.X, (float)image.Position.Y), combined, ref min, ref max, ref hasData);
            UpdateBounds(new Vector2((float)(image.Position.X + image.Width), (float)(image.Position.Y + image.Height)), combined, ref min, ref max, ref hasData);
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
        else if (entity is Face3d face)
        {
            var combined = GetOcsMatrix(face.Normal) * transform;
            UpdateBounds(new Vector2((float)face.FirstVertex.X, (float)face.FirstVertex.Y), combined, ref min, ref max, ref hasData);
            UpdateBounds(new Vector2((float)face.SecondVertex.X, (float)face.SecondVertex.Y), combined, ref min, ref max, ref hasData);
            UpdateBounds(new Vector2((float)face.ThirdVertex.X, (float)face.ThirdVertex.Y), combined, ref min, ref max, ref hasData);
            UpdateBounds(new Vector2((float)face.FourthVertex.X, (float)face.FourthVertex.Y), combined, ref min, ref max, ref hasData);
        }
        else if (entity is netDxf.Entities.Point point)
        {
            var combined = GetOcsMatrix(point.Normal) * transform;
            UpdateBounds(new Vector2((float)point.Position.X, (float)point.Position.Y), combined, ref min, ref max, ref hasData);
        }
        else if (entity is Wipeout wipeout)
        {
            var combined = GetOcsMatrix(wipeout.Normal) * transform;
            if (wipeout.ClippingBoundary != null && wipeout.ClippingBoundary.Vertexes != null)
            {
                foreach (var v in wipeout.ClippingBoundary.Vertexes)
                {
                    UpdateBounds(new Vector2((float)v.X, (float)v.Y), combined, ref min, ref max, ref hasData);
                }
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
