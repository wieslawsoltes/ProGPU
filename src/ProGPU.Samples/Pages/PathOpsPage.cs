using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;

namespace ProGPU.Samples;

public class PathOpsVisual : FrameworkElement
{
    private int _operation = 2; // Default Union
    private string _shapeA = "Circle";
    private string _shapeB = "Rectangle";
    private float _overlapOffset = 50f; // Slider overlap offset
    private PathGeometry? _pathA;
    private PathGeometry? _pathB;
    private PathGeometry? _result;
    private string? _error;
    private Vector2 _requestedSize;
    private int _requestVersion;
    private bool _isComputing;

    public int Operation
    {
        get => _operation;
        set
        {
            if (_operation == value) return;
            _operation = value;
            RequestComputation();
        }
    }

    public string ShapeA
    {
        get => _shapeA;
        set
        {
            if (_shapeA == value) return;
            _shapeA = value;
            RequestComputation();
        }
    }

    public string ShapeB
    {
        get => _shapeB;
        set
        {
            if (_shapeB == value) return;
            _shapeB = value;
            RequestComputation();
        }
    }

    public float OverlapOffset
    {
        get => _overlapOffset;
        set
        {
            if (MathF.Abs(_overlapOffset - value) <= 0.001f) return;
            _overlapOffset = value;
            RequestComputation();
        }
    }

    public PathOpsVisual()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HeightConstraint = 420f;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        base.ArrangeOverride(arrangeRect);
        var arrangedSize = new Vector2(arrangeRect.Width, arrangeRect.Height);
        if (Vector2.DistanceSquared(_requestedSize, arrangedSize) > 0.01f)
        {
            _requestedSize = arrangedSize;
            RequestComputation();
        }
    }

    private void RequestComputation()
    {
        _requestVersion++;
        TryStartComputation();
    }

    private void TryStartComputation()
    {
        if (_isComputing || _requestedSize.X <= 0f || _requestedSize.Y <= 0f) return;

        _isComputing = true;
        var version = _requestVersion;
        var center = _requestedSize / 2f;
        var pathA = CreateShapeA(center - new Vector2(_overlapOffset / 2f, 0f));
        var pathB = CreateShapeB(center + new Vector2(_overlapOffset / 2f, 0f));
        _ = CompleteComputationAsync(version, pathA, pathB, _operation);
    }

    private async Task CompleteComputationAsync(int version, PathGeometry pathA, PathGeometry pathB, int operation)
    {
        PathGeometry? result = null;
        string? error = null;
        try
        {
            result = await PathOpGeometrySolver.CombineAsync(pathA, pathB, operation);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        _isComputing = false;
        if (version == _requestVersion)
        {
            // Publish the operands and result as one visual snapshot. Keeping the
            // previous completed snapshot visible while a newer request is in flight
            // prevents slider input from alternating between result and source-only
            // frames.
            _pathA = pathA;
            _pathB = pathB;
            _result = result;
            _error = error;
            Invalidate();
        }
        else
        {
            TryStartComputation();
        }
    }

    private PathGeometry CreateShapeA(Vector2 center)
    {
        var geom = new PathGeometry();
        if (_shapeA == "Circle")
        {
            var fig = new PathFigure(new Vector2(center.X - 60f, center.Y));
            fig.Segments.Add(new ArcSegment(new Vector2(center.X + 60f, center.Y), new Vector2(60f, 60f), 0, true, SweepDirection.Clockwise));
            fig.Segments.Add(new ArcSegment(new Vector2(center.X - 60f, center.Y), new Vector2(60f, 60f), 0, true, SweepDirection.Clockwise));
            fig.IsClosed = true;
            geom.Figures.Add(fig);
        }
        else if (_shapeA == "Rectangle")
        {
            var fig = new PathFigure(new Vector2(center.X - 60f, center.Y - 60f));
            fig.Segments.Add(new LineSegment(new Vector2(center.X + 60f, center.Y - 60f)));
            fig.Segments.Add(new LineSegment(new Vector2(center.X + 60f, center.Y + 60f)));
            fig.Segments.Add(new LineSegment(new Vector2(center.X - 60f, center.Y + 60f)));
            fig.IsClosed = true;
            geom.Figures.Add(fig);
        }
        else // Star
        {
            float rOuter = 70f;
            float rInner = 28f;
            var fig = new PathFigure(new Vector2(center.X, center.Y - rOuter));
            for (int i = 1; i < 10; i++)
            {
                float angle = i * MathF.PI / 5f;
                float r = (i % 2 == 0) ? rOuter : rInner;
                fig.Segments.Add(new LineSegment(new Vector2(center.X + MathF.Sin(angle) * r, center.Y - MathF.Cos(angle) * r)));
            }
            fig.IsClosed = true;
            geom.Figures.Add(fig);
        }
        return geom;
    }

    private PathGeometry CreateShapeB(Vector2 center)
    {
        var geom = new PathGeometry();
        if (_shapeB == "Circle")
        {
            var fig = new PathFigure(new Vector2(center.X - 60f, center.Y));
            fig.Segments.Add(new ArcSegment(new Vector2(center.X + 60f, center.Y), new Vector2(60f, 60f), 0, true, SweepDirection.Clockwise));
            fig.Segments.Add(new ArcSegment(new Vector2(center.X - 60f, center.Y), new Vector2(60f, 60f), 0, true, SweepDirection.Clockwise));
            fig.IsClosed = true;
            geom.Figures.Add(fig);
        }
        else if (_shapeB == "Rectangle")
        {
            var fig = new PathFigure(new Vector2(center.X - 60f, center.Y - 60f));
            fig.Segments.Add(new LineSegment(new Vector2(center.X + 60f, center.Y - 60f)));
            fig.Segments.Add(new LineSegment(new Vector2(center.X + 60f, center.Y + 60f)));
            fig.Segments.Add(new LineSegment(new Vector2(center.X - 60f, center.Y + 60f)));
            fig.IsClosed = true;
            geom.Figures.Add(fig);
        }
        else // Star
        {
            float rOuter = 70f;
            float rInner = 28f;
            var fig = new PathFigure(new Vector2(center.X, center.Y - rOuter));
            for (int i = 1; i < 10; i++)
            {
                float angle = i * MathF.PI / 5f;
                float r = (i % 2 == 0) ? rOuter : rInner;
                fig.Segments.Add(new LineSegment(new Vector2(center.X + MathF.Sin(angle) * r, center.Y - MathF.Cos(angle) * r)));
            }
            fig.IsClosed = true;
            geom.Figures.Add(fig);
        }
        return geom;
    }

    public override void OnRender(DrawingContext context)
    {
        // Card background outline
        context.DrawRectangle(ThemeManager.GetBrush("CardBackground"), new Pen(ThemeManager.GetBrush("ControlBorder"), 1f), new Rect(0, 0, Size.X, Size.Y));

        var pathA = _pathA;
        var pathB = _pathB;
        if (pathA == null || pathB == null)
        {
            return;
        }

        // Draw original outlines as light guidance lines
        var guidancePenA = new Pen(new SolidColorBrush(new Vector4(0.85f, 0.15f, 0.15f, 0.25f)), 1.5f);
        var guidancePenB = new Pen(new SolidColorBrush(new Vector4(0.15f, 0.15f, 0.85f, 0.25f)), 1.5f);
        context.DrawPath(null, guidancePenA, pathA);
        context.DrawPath(null, guidancePenB, pathB);

        if (_result is { } result)
        {
            // Draw resulting filled shape
            var fillBrush = new SolidColorBrush(new Vector4(0f, 0.47f, 0.83f, 0.15f)); // Light blue accent fill
            var strokePen = new Pen(ThemeManager.GetBrush("SystemAccentColor"), 3.5f);

            context.DrawPath(fillBrush, strokePen, result);

            // Draw vector vertices for premium diagnostic look
            var vertBrush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f));
            var vertPen = new Pen(ThemeManager.GetBrush("SystemAccentColor"), 1.2f);
            foreach (var figure in result.Figures)
            {
                context.DrawCircle(vertBrush, vertPen, figure.StartPoint, 4f);
                foreach (var seg in figure.Segments)
                {
                    if (seg is LineSegment line)
                    {
                        context.DrawCircle(vertBrush, vertPen, line.Point, 3.5f);
                    }
                    else if (seg is QuadraticBezierSegment quad)
                    {
                        context.DrawCircle(vertBrush, vertPen, quad.Point, 3.5f);
                    }
                    else if (seg is CubicBezierSegment cubic)
                    {
                        context.DrawCircle(vertBrush, vertPen, cubic.Point, 3.5f);
                    }
                    else if (seg is ArcSegment arc)
                    {
                        context.DrawCircle(vertBrush, vertPen, arc.Point, 3.5f);
                    }
                }
            }
        }
        else if (!string.IsNullOrEmpty(_error))
        {
            // Draw error message on WebGPU context errors
            context.DrawText($"Error compiling: {_error}", AppState.GetFont()!, 12f, new SolidColorBrush(new Vector4(1f, 0.2f, 0.2f, 1f)), new Vector2(20f, 20f));
        }
        else
        {
            context.DrawText("Computing path operation on WebGPU...", AppState.GetFont()!, 12f, ThemeManager.GetBrush("TextSecondary"), new Vector2(20f, 20f));
        }
    }
}

public static class PathOpsPage
{
    public static FrameworkElement Create()
    {
        var scrollViewer = new ScrollViewer
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var mainStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        scrollViewer.Content = mainStack;

        // Header Title
        var title = new RichTextBlock { Font = AppState._font, FontSize = 22f, Margin = new Thickness(0, 0, 0, 6) };
        title.Inlines.Add(new Bold(new Run("GPU Path Geometry Operations")));
        mainStack.AddChild(title);

        var desc = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 24) };
        desc.Inlines.Add(new Run("This page showcases live, GPU-accelerated analytical Path Boolean operations (Union, Intersect, Difference, XOR, Reverse Difference). The boolean combination is calculated analytically on the GPU using WebGPU compute shaders, reconstructed into standard vector geometry segments, and rendered in real-time."));
        mainStack.AddChild(desc);

        // Split Grid layout
        var splitGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        splitGrid.ColumnDefinitions.Add(new GridLength(1.1f, GridUnitType.Star));     // Left: Controls Card
        splitGrid.ColumnDefinitions.Add(new GridLength(20f, GridUnitType.Absolute));  // Gap
        splitGrid.ColumnDefinitions.Add(new GridLength(2f, GridUnitType.Star));       // Right: Visual Preview Card

        var visualPreview = new PathOpsVisual();

        // Column 0: Controls Card
        var controlsCard = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };

        var controlsStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };

        // Shape A Selector
        var labelA = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 6) };
        labelA.Inlines.Add(new Bold(new Run("Shape A:")));
        controlsStack.AddChild(labelA);

        var comboA = new ComboBox { Font = AppState._font, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 16) };
        comboA.Items.Add(new ComboBoxItem("Circle"));
        comboA.Items.Add(new ComboBoxItem("Rectangle"));
        comboA.Items.Add(new ComboBoxItem("Star"));
        comboA.SelectedItem = comboA.Items[0];
        comboA.SelectionChanged += (s, e) =>
        {
            if (comboA.SelectedItem != null)
            {
                visualPreview.ShapeA = comboA.SelectedItem.Text;
            }
        };
        controlsStack.AddChild(comboA);

        // Shape B Selector
        var labelB = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 6) };
        labelB.Inlines.Add(new Bold(new Run("Shape B:")));
        controlsStack.AddChild(labelB);

        var comboB = new ComboBox { Font = AppState._font, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 16) };
        comboB.Items.Add(new ComboBoxItem("Circle"));
        comboB.Items.Add(new ComboBoxItem("Rectangle"));
        comboB.Items.Add(new ComboBoxItem("Star"));
        comboB.SelectedItem = comboB.Items[1];
        comboB.SelectionChanged += (s, e) =>
        {
            if (comboB.SelectedItem != null)
            {
                visualPreview.ShapeB = comboB.SelectedItem.Text;
            }
        };
        controlsStack.AddChild(comboB);

        // Operation Selector
        var labelOp = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 6) };
        labelOp.Inlines.Add(new Bold(new Run("Boolean Operation:")));
        controlsStack.AddChild(labelOp);

        var comboOp = new ComboBox { Font = AppState._font, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 16) };
        comboOp.Items.Add(new ComboBoxItem("Difference"));
        comboOp.Items.Add(new ComboBoxItem("Intersect"));
        comboOp.Items.Add(new ComboBoxItem("Union"));
        comboOp.Items.Add(new ComboBoxItem("XOR"));
        comboOp.Items.Add(new ComboBoxItem("Reverse Difference"));
        comboOp.SelectedItem = comboOp.Items[2]; // Default Union
        comboOp.SelectionChanged += (s, e) =>
        {
            if (comboOp.SelectedItem != null)
            {
                visualPreview.Operation = comboOp.SelectedItem.Text switch
                {
                    "Difference" => 0,
                    "Intersect" => 1,
                    "Union" => 2,
                    "XOR" => 3,
                    "Reverse Difference" => 4,
                    _ => 2
                };
            }
        };
        controlsStack.AddChild(comboOp);

        // Overlap Slider
        var labelSlider = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 6) };
        labelSlider.Inlines.Add(new Bold(new Run("Overlap Separation:")));
        controlsStack.AddChild(labelSlider);

        var slider = new Slider
        {
            Minimum = 10f,
            Maximum = 180f,
            Value = 50f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 10)
        };
        slider.ValueChanged += (s, e) =>
        {
            visualPreview.OverlapOffset = slider.Value;
        };
        controlsStack.AddChild(slider);

        controlsCard.Child = controlsStack;
        splitGrid.AddChild(controlsCard);
        Grid.SetColumn(controlsCard, 0);

        // Column 2: Visual Preview Card
        var previewCard = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(16f),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        previewCard.Child = visualPreview;
        splitGrid.AddChild(previewCard);
        Grid.SetColumn(previewCard, 2);

        mainStack.AddChild(splitGrid);

        return scrollViewer;
    }
}
