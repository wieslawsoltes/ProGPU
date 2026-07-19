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
    public float GearRotation { get; set; }

    public GearVisual()
    {
        Width = 100f;
        Height = 100f;
    }

    public override void OnRender(DrawingContext context)
    {
        Vector2 center = Size / 2f;
        if (center.X <= 0 || center.Y <= 0) return;

        context.DrawRectangle(new SolidColorBrush(0x00000000), null, new Rect(Vector2.Zero, Size));

        var p = GeometryHelpers.CreateGearPathWithRotation(center, 25f, 40f, 10, 8f, GearRotation);
        context.DrawPath(new SolidColorBrush(0x0078D4FF), new Pen(new SolidColorBrush(0xFFFFFFFF), 1.5f), p);
    }
}
