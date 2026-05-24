using System;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.WinUI;

namespace ProGPU.Samples;

public class GearCanvasVisual : FrameworkElement
{
    private readonly DrawingVisual _gear1;
    private readonly DrawingVisual _gear2;
    private readonly DrawingVisual _gear3;

    public GearCanvasVisual(TtfFont font)
    {
        _gear1 = new DrawingVisual();
        _gear2 = new DrawingVisual();
        _gear3 = new DrawingVisual();

        AddChild(_gear1);
        AddChild(_gear2);
        AddChild(_gear3);
    }

    public void UpdateRotation(float baseRotation)
    {
        Vector2 center = Size / 2f;
        if (center.X <= 0 || center.Y <= 0) return;

        if (_gear1.Context.Commands.Count == 0)
        {
            var p1 = GeometryHelpers.CreateGearPath(Vector2.Zero, 85f, 115f, 16, 20f);
            _gear1.Context.DrawPath(new SolidColorBrush(0x00E5FFFF), new Pen(new SolidColorBrush(0xFFFFFFFF), 1.5f), p1);

            var p2 = GeometryHelpers.CreateGearPath(Vector2.Zero, 52f, 78f, 12, 18f);
            _gear2.Context.DrawPath(new SolidColorBrush(0xA100FFFF), new Pen(new SolidColorBrush(0xFFFFFFFF), 1.5f), p2);

            var p3 = GeometryHelpers.CreateGearPath(Vector2.Zero, 35f, 55f, 8, 15f);
            _gear3.Context.DrawPath(new SolidColorBrush(0xFF007FFF), new Pen(new SolidColorBrush(0xFFFFFFFF), 1.5f), p3);
        }

        _gear1.Transform = Matrix4x4.CreateRotationZ(baseRotation) * Matrix4x4.CreateTranslation(center.X - 35f, center.Y, 0f);

        Vector2 g2Center = center + new Vector2(152f, 0f);
        float g2Rotation = -baseRotation * (16f / 12f) + (float)(Math.PI / 12.0);
        _gear2.Transform = Matrix4x4.CreateRotationZ(g2Rotation) * Matrix4x4.CreateTranslation(g2Center.X - 35f, g2Center.Y, 0f);

        float angleBL = (float)(Math.PI * 5.0 / 4.0);
        Vector2 g3Center = center + new Vector2((float)Math.Cos(angleBL), (float)Math.Sin(angleBL)) * 133f;
        float g3Rotation = -baseRotation * (16f / 8f) + (float)(Math.PI / 8.0);
        _gear3.Transform = Matrix4x4.CreateRotationZ(g3Rotation) * Matrix4x4.CreateTranslation(g3Center.X - 35f, g3Center.Y, 0f);
    }
}
