using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public class ArcStrokeShaderTests
{
    [Fact]
    public void StrokedPathArc_UsesFixedQuadGpuArcSdfShapeType()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(240, 160);
        window.Content = new ArcStrokeVisual();

        try
        {
            window.Render();

            Assert.Equal(4, window.Compositor.VectorVertices.Count(vertex => DecodeShapeType(vertex.ShapeType) == 12));
            Assert.DoesNotContain(window.Compositor.VectorVertices, vertex => DecodeShapeType(vertex.ShapeType) == 11);
            Assert.DoesNotContain(window.Compositor.VectorVertices, vertex => DecodeShapeType(vertex.ShapeType) == 3);

            byte[] pixels = window.ReadPixels();
            int strokePixels = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i] < 80 && pixels[i + 1] > 90 && pixels[i + 2] > 150)
                {
                    strokePixels++;
                }
            }

            Assert.True(strokePixels > 100, $"Expected visible arc stroke pixels, found {strokePixels}.");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void TransformedStrokedPathArc_UsesFixedQuadGpuArcSdfShapeType()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(260, 180);
        window.Content = new TransformedArcStrokeVisual();

        try
        {
            window.Render();

            Assert.Equal(4, window.Compositor.VectorVertices.Count(vertex => DecodeShapeType(vertex.ShapeType) == 12));
            Assert.DoesNotContain(window.Compositor.VectorVertices, vertex => DecodeShapeType(vertex.ShapeType) == 11);
            Assert.DoesNotContain(window.Compositor.VectorVertices, vertex => DecodeShapeType(vertex.ShapeType) == 3);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void NonStrokedPathSegment_DoesNotEmitStrokeVertices()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(180, 80);
        window.Content = new NonStrokedLineVisual();

        try
        {
            window.Render();

            Assert.DoesNotContain(window.Compositor.VectorVertices, vertex => DecodeShapeType(vertex.ShapeType) == 3);
            Assert.Empty(window.Compositor.VectorIndices);
        }
        finally
        {
            window.Content = null;
        }
    }

    private static int DecodeShapeType(float shapeType)
    {
        if (shapeType >= 1000f)
        {
            shapeType -= 1000f;
        }

        if (shapeType >= 195f)
        {
            shapeType -= 200f;
        }
        else if (shapeType >= 95f)
        {
            shapeType -= 100f;
        }

        return (int)MathF.Round(shapeType);
    }

    private sealed class ArcStrokeVisual : FrameworkElement
    {
        public ArcStrokeVisual()
        {
            Width = 240f;
            Height = 160f;
        }

        public override void OnRender(ProGPU.Scene.DrawingContext context)
        {
            var path = new PathGeometry();
            var figure = new PathFigure(new Vector2(30f, 100f));
            figure.Segments.Add(new ArcSegment(
                new Vector2(210f, 100f),
                new Vector2(110f, 70f),
                20f,
                isLargeArc: false,
                SweepDirection.Clockwise));
            path.Figures.Add(figure);

            context.DrawPath(
                brush: null,
                pen: new Pen(new SolidColorBrush(new Vector4(0.1f, 0.6f, 1f, 1f)), 8f),
                path);
        }
    }

    private sealed class TransformedArcStrokeVisual : FrameworkElement
    {
        public TransformedArcStrokeVisual()
        {
            Width = 260f;
            Height = 180f;
        }

        public override void OnRender(ProGPU.Scene.DrawingContext context)
        {
            var path = new PathGeometry();
            var figure = new PathFigure(new Vector2(25f, 105f));
            figure.Segments.Add(new ArcSegment(
                new Vector2(190f, 105f),
                new Vector2(95f, 55f),
                30f,
                isLargeArc: false,
                SweepDirection.Clockwise));
            path.Figures.Add(figure);

            var transform = new Matrix4x4(
                1.05f, 0.18f, 0f, 0f,
                0.32f, 0.92f, 0f, 0f,
                0f, 0f, 1f, 0f,
                8f, -4f, 0f, 1f);

            context.DrawPath(
                brush: null,
                pen: new Pen(new SolidColorBrush(new Vector4(0.9f, 0.25f, 0.1f, 1f)), 7f),
                path,
                transform);
        }
    }

    private sealed class NonStrokedLineVisual : FrameworkElement
    {
        public NonStrokedLineVisual()
        {
            Width = 180f;
            Height = 80f;
        }

        public override void OnRender(ProGPU.Scene.DrawingContext context)
        {
            var path = new PathGeometry();
            var figure = new PathFigure(new Vector2(20f, 40f))
            {
                IsFilled = false
            };
            figure.Segments.Add(new LineSegment(new Vector2(160f, 40f), isStroked: false));
            path.Figures.Add(figure);

            context.DrawPath(
                brush: null,
                pen: new Pen(new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)), 6f),
                path);
        }
    }
}
