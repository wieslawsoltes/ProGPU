using System;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;

namespace ProGPU.Samples;

public class GearVisual : FrameworkElement
{
    private readonly SolidColorBrush _backgroundBrush = new(0x00000000);
    private readonly SolidColorBrush _fillBrush = new(0x0078D4FF);
    private readonly Pen _outlinePen = new(new SolidColorBrush(0xFFFFFFFF), 1.5f);
    private readonly PathGeometry _gearPath;
    private readonly RenderCommandGeometryCache _gearGeometryCache;
    private float _gearRotation;

    public float GearRotation
    {
        get => _gearRotation;
        set
        {
            if (_gearRotation == value) return;
            _gearRotation = value;
            Invalidate();
        }
    }

    public GearVisual()
    {
        Width = 100f;
        Height = 100f;
        _gearPath = GeometryHelpers.CreateGearPathWithRotation(
            Vector2.Zero,
            25f,
            40f,
            10,
            8f,
            0f);
        _gearGeometryCache = RenderCommandGeometryCache.ForPath(_gearPath);
    }

    public override void OnRender(DrawingContext context)
    {
        Vector2 center = Size / 2f;
        if (center.X <= 0 || center.Y <= 0) return;

        context.DrawRectangle(_backgroundBrush, null, new Rect(Vector2.Zero, Size));

        Matrix4x4 transform = Matrix4x4.CreateRotationZ(_gearRotation) *
            Matrix4x4.CreateTranslation(center.X, center.Y, 0f);
        context.DrawPath(_fillBrush, _outlinePen, _gearPath, transform, _gearGeometryCache);
    }
}
