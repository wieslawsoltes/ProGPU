using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.Dxf;

public class DxfRenderContext
{
    public DrawingContext DrawingContext { get; }
    
    // Viewport and projection parameters
    public float Zoom { get; set; } = 1.0f;
    public Vector2 Pan { get; set; } = Vector2.Zero;
    public Vector2 Center { get; set; } = Vector2.Zero;
    public Vector2 ScreenCenter { get; set; } = Vector2.Zero;
    
    // Active document reference for layout and space rendering
    public netDxf.DxfDocument? Document { get; set; }
    
    // Level of Detail rendering optimization flag
    public bool EnableLod { get; set; } = false;
    
    // Font and Styling fallback
    public TtfFont Font { get; set; }
    public Brush FallbackBrush { get; set; } = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f));
    public Brush BackgroundBrush { get; set; } = new SolidColorBrush(new Vector4(0.12f, 0.12f, 0.14f, 1f));
    
    // Theme and visibility settings
    public HashSet<string> ActiveLayers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Vector4> LayerColors { get; } = new(StringComparer.OrdinalIgnoreCase);
    
    // Matrix transform stack for nested inserts/blocks
    private readonly Stack<Matrix4x4> _transformStack = new();
    
    public Matrix4x4 CurrentTransform { get; private set; } = Matrix4x4.Identity;

    public DxfRenderContext(DrawingContext drawingContext, TtfFont defaultFont)
    {
        DrawingContext = drawingContext;
        Font = defaultFont;
    }

    /// <summary>
    /// Transforms a DXF world coordinate (Y-up) to screen coordinate (Y-down) 
    /// considering Center, Zoom, ScreenCenter, and Pan.
    /// </summary>
    public Vector2 TransformToScreen(Vector2 worldPoint)
    {
        // 1. Center the world coordinate (relative to the DXF model's center)
        float localX = worldPoint.X - Center.X;
        float localY = worldPoint.Y - Center.Y;
        
        // 2. Scale and project with Y inverted (CAD is Y-up, screen is Y-down)
        float screenX = localX * Zoom + ScreenCenter.X + Pan.X;
        float screenY = -localY * Zoom + ScreenCenter.Y + Pan.Y;
        
        return new Vector2(screenX, screenY);
    }

    /// <summary>
    /// Transforms a point first by the active block matrix stack, and then projects to screen.
    /// </summary>
    public Vector2 Transform(Vector2 localPoint, Matrix4x4 modelMatrix)
    {
        var v3 = new Vector3(localPoint.X, localPoint.Y, 0f);
        var v3Transformed = Vector3.Transform(v3, modelMatrix);
        return TransformToScreen(new Vector2(v3Transformed.X, v3Transformed.Y));
    }

    public void PushTransform(Matrix4x4 transform)
    {
        _transformStack.Push(CurrentTransform);
        CurrentTransform = transform * CurrentTransform;
    }

    public void PopTransform()
    {
        if (_transformStack.Count > 0)
        {
            CurrentTransform = _transformStack.Pop();
        }
        else
        {
            CurrentTransform = Matrix4x4.Identity;
        }
    }

    /// <summary>
    /// Checks if the given screen-space bounding box is completely off-screen.
    /// Uses a small safety padding to prevent abrupt clipping artifacts.
    /// </summary>
    public bool IsOffScreen(Vector2 minScreen, Vector2 maxScreen)
    {
        float w = ScreenCenter.X * 2f;
        float h = ScreenCenter.Y * 2f;
        if (w <= 0f || h <= 0f) return false; // Viewport not yet sized, do not cull
        
        const float padding = 50f;
        return maxScreen.X < -padding || minScreen.X > w + padding || 
               maxScreen.Y < -padding || minScreen.Y > h + padding;
    }

    // Dxf-specific brush and pen caches to prevent high-frequency GC allocations
    private readonly Dictionary<(string Layer, float R, float G, float B, float A), Brush> _brushCache = new();
    private readonly Dictionary<(string Layer, float R, float G, float B, float A, float Thickness), Pen> _penCache = new();

    public Brush GetCachedBrush(netDxf.Entities.EntityObject entity)
    {
        var color = new Vector4(1f, 1f, 1f, 1f); // Default white/fallback
        
        if (entity.Color.IsByLayer)
        {
            if (LayerColors.TryGetValue(entity.Layer.Name, out var lColor))
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

        var key = (entity.Layer.Name, color.X, color.Y, color.Z, color.W);
        if (!_brushCache.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(color);
            _brushCache[key] = brush;
        }
        return brush;
    }

    public Pen GetCachedPen(netDxf.Entities.EntityObject entity, float thickness)
    {
        var brush = GetCachedBrush(entity);
        var color = (brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);
        
        var key = (entity.Layer.Name, color.X, color.Y, color.Z, color.W, thickness);
        if (!_penCache.TryGetValue(key, out var pen))
        {
            pen = new Pen(brush, thickness);
            _penCache[key] = pen;
        }
        return pen;
    }

    public void Reset()
    {
        _transformStack.Clear();
        CurrentTransform = Matrix4x4.Identity;
    }
}
