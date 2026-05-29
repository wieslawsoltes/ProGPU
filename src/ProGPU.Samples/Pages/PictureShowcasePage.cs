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
using Microsoft.UI.Xaml.Input;
using Button = Microsoft.UI.Xaml.Controls.Button;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;

namespace ProGPU.Samples;

public class PictureShowcaseVisual : FrameworkElement, IAnimatedElement
{
    private float _time = 0f;
    private GpuPicture? _cachedPicture;
    
    // Metrics
    private double _lastFrameTimeMs = 0;
    private double _smoothedFrameTimeMs = 0;
    private int _fpsCounter = 0;
    private double _averageFps = 0;
    private float _statsTimeAccumulator = 0f;
    
    private long _lastAllocatedBytes = 0;
    private long _allocationsThisSecond = 0;
    private double _averageAllocationsPerSecond = 0;
    
    private int _lastGen0Collections = 0;
    private int _lastGen1Collections = 0;
    private int _lastGen2Collections = 0;
    private int _gen0Diff = 0;
    private int _gen1Diff = 0;
    private int _gen2Diff = 0;

    // Controls bindings
    private readonly RichTextBlock _statsBlock;
    
    public bool EnableCaching { get; set; } = true;
    public float SpeedFactor { get; set; } = 1.0f;
    public float ZoomFactor { get; set; } = 1.0f;

    public PictureShowcaseVisual(RichTextBlock statsBlock)
    {
        _statsBlock = statsBlock;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Height = 480f;

        // Initialize GC collection baselines
        _lastGen0Collections = GC.CollectionCount(0);
        _lastGen1Collections = GC.CollectionCount(1);
        _lastGen2Collections = GC.CollectionCount(2);

        // Pre-record the vector scene once
        RecordVectorScene();
    }

    private Vector2[] GenerateGearPoints(Vector2 center, float innerRadius, float outerRadius, int teeth)
    {
        int pointsCount = teeth * 4;
        var points = new Vector2[pointsCount];
        float angleStep = MathF.PI * 2f / pointsCount;
        for (int i = 0; i < pointsCount; i++)
        {
            float angle = i * angleStep;
            float r = (i % 4 == 0 || i % 4 == 1) ? outerRadius : innerRadius;
            points[i] = new Vector2(center.X + MathF.Cos(angle) * r, center.Y + MathF.Sin(angle) * r);
        }
        return points;
    }

    private Vector2[] GenerateStarPoints(Vector2 center, float innerRadius, float outerRadius, int pointsCount)
    {
        int totalPoints = pointsCount * 2;
        var points = new Vector2[totalPoints];
        float angleStep = MathF.PI / pointsCount;
        for (int i = 0; i < totalPoints; i++)
        {
            float angle = i * angleStep;
            float r = (i % 2 == 0) ? outerRadius : innerRadius;
            points[i] = new Vector2(center.X + MathF.Cos(angle) * r, center.Y + MathF.Sin(angle) * r);
        }
        return points;
    }

    private void RecordVectorScene()
    {
        var recorder = new GpuPictureRecorder();
        // Record in a 1200x1200px logical canvas
        var context = recorder.BeginRecording(new Rect(0, 0, 1200, 1200));

        DrawBlueprintDrawing(context);

        _cachedPicture = recorder.EndRecording();
    }

    private void DrawBlueprintDrawing(DrawingContext context)
    {
        // Vector grid background lines (simulating blueprints)
        var gridColor = new Vector4(0.12f, 0.28f, 0.48f, 0.2f);
        var gridPen = new Pen(new SolidColorBrush(gridColor), 1f);
        for (float x = 0; x <= 1200; x += 50)
        {
            context.DrawLine(gridPen, new Vector2(x, 0), new Vector2(x, 1200));
        }
        for (float y = 0; y <= 1200; y += 50)
        {
            context.DrawLine(gridPen, new Vector2(0, y), new Vector2(1200, y));
        }

        // Circular tech blueprint guides
        var guideColor = new Vector4(0.2f, 0.4f, 0.7f, 0.35f);
        var guidePen = new Pen(new SolidColorBrush(guideColor), 1.5f);
        context.DrawEllipse(null, guidePen, new Vector2(600, 600), 200f, 200f);
        context.DrawEllipse(null, guidePen, new Vector2(600, 600), 400f, 400f);
        context.DrawEllipse(null, guidePen, new Vector2(600, 600), 500f, 500f);

        // Spline wave across background
        var splinePen = new Pen(new SolidColorBrush(new Vector4(0.0f, 0.9f, 0.7f, 0.5f)), 3f);
        var splinePoints = new Vector2[]
        {
            new Vector2(100, 200), new Vector2(300, 800), new Vector2(600, 100),
            new Vector2(900, 1000), new Vector2(1100, 400)
        };
        var knots = new double[] { 0, 0, 0, 0.33, 0.66, 1, 1, 1 };
        context.DrawSpline(splinePen, splinePoints, knots, 3);

        // Core sun/gear element
        var gearColor1 = new Vector4(1.0f, 0.55f, 0.0f, 0.95f);
        var gearPen1 = new Pen(new SolidColorBrush(gearColor1), 2.5f);
        var centerGear = GenerateGearPoints(new Vector2(600, 600), 100f, 125f, 24);
        context.DrawPolyline(gearPen1, centerGear, true);

        // Planetary outer satellite gears
        var gearColor2 = new Vector4(0.0f, 0.75f, 1.0f, 0.9f);
        var gearPen2 = new Pen(new SolidColorBrush(gearColor2), 2f);
        
        float dist = 300f;
        for (int i = 0; i < 4; i++)
        {
            float angle = i * MathF.PI / 2f;
            var pos = new Vector2(600 + MathF.Cos(angle) * dist, 600 + MathF.Sin(angle) * dist);
            var satelliteGear = GenerateGearPoints(pos, 50f, 65f, 14);
            context.DrawPolyline(gearPen2, satelliteGear, true);
        }

        // Draw multiple nested glowing geometric stars
        var starColor = new Vector4(1f, 1f, 1f, 0.3f);
        var starPen = new Pen(new SolidColorBrush(starColor), 1f);
        for (int i = 1; i <= 6; i++)
        {
            var stars = GenerateStarPoints(new Vector2(600, 600), 15f * i, 35f * i, 10);
            context.DrawPolyline(starPen, stars, true);
        }

        // Linear multi-stop gradient card
        var gradStops = new GradientStop[]
        {
            new GradientStop(new Vector4(1.0f, 0.0f, 0.5f, 0.85f), 0.0f),  // Hot Pink
            new GradientStop(new Vector4(0.2f, 0.1f, 0.9f, 0.7f), 0.5f),   // Electric Blue
            new GradientStop(new Vector4(0.0f, 1.0f, 0.4f, 0.9f), 1.0f)    // Vivid Teal
        };
        var linGrad = new LinearGradientBrush(new Vector2(150, 150), new Vector2(350, 350), gradStops);
        context.DrawEllipse(linGrad, new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.8f)), 1.5f), new Vector2(250, 250), 90f, 90f);

        // Radial multi-stop gradient card
        var radStops = new GradientStop[]
        {
            new GradientStop(new Vector4(1.0f, 0.85f, 0.1f, 1f), 0.0f),    // Sun yellow
            new GradientStop(new Vector4(1.0f, 0.3f, 0.0f, 0.5f), 0.6f),    // Lava orange
            new GradientStop(new Vector4(0f, 0f, 0f, 0f), 1.0f)             // Fade out
        };
        var radGrad = new RadialGradientBrush(new Vector2(950, 950), 140f, radStops);
        context.DrawEllipse(radGrad, null, new Vector2(950, 950), 140f, 140f);

        // Text typography layout
        var font = AppState.GetFont()!;
        context.DrawText("PROGPU Caching Subsystem", font, 24f, new ThemeResourceBrush("TextPrimary"), new Vector2(380, 240));
        context.DrawText("SKPicture Playback Engine", font, 15f, new ThemeResourceBrush("AccentBrush"), new Vector2(460, 280));
        context.DrawText("Zero Heap Allocations on Hot Rendering Paths", font, 11f, new ThemeResourceBrush("TextSecondary"), new Vector2(400, 315));
    }

    public void Update(float delta)
    {
        _time += delta * SpeedFactor;
        
        // Track allocations/sec
        long currentAllocated = GC.GetTotalAllocatedBytes(false);
        if (_lastAllocatedBytes > 0)
        {
            long diff = currentAllocated - _lastAllocatedBytes;
            if (diff > 0)
            {
                _allocationsThisSecond += diff;
            }
        }
        _lastAllocatedBytes = currentAllocated;

        // Statistics accumulation
        _statsTimeAccumulator += delta;
        _fpsCounter++;

        if (_statsTimeAccumulator >= 1.0f)
        {
            _averageFps = _fpsCounter / _statsTimeAccumulator;
            _fpsCounter = 0;
            
            _averageAllocationsPerSecond = _allocationsThisSecond;
            _allocationsThisSecond = 0;

            // Read GC differences
            int currentGen0 = GC.CollectionCount(0);
            int currentGen1 = GC.CollectionCount(1);
            int currentGen2 = GC.CollectionCount(2);

            _gen0Diff = currentGen0 - _lastGen0Collections;
            _gen1Diff = currentGen1 - _lastGen1Collections;
            _gen2Diff = currentGen2 - _lastGen2Collections;

            _lastGen0Collections = currentGen0;
            _lastGen1Collections = currentGen1;
            _lastGen2Collections = currentGen2;

            _statsTimeAccumulator = 0f;
            UpdateStatsUI();
        }

        Invalidate();
    }

    private void UpdateStatsUI()
    {
        if (_statsBlock == null) return;

        _statsBlock.Inlines.Clear();
        _statsBlock.Inlines.Add(new Bold(new Run("Engine Telemetry Metrics\n")));
        
        _statsBlock.Inlines.Add(new Run("Renderer: "));
        _statsBlock.Inlines.Add(new Bold(new Run(EnableCaching ? "Hardware GpuPicture Playback (Cached)\n" : "Manual Redraw Pipeline (Uncached)\n")));
        
        _statsBlock.Inlines.Add(new Run("Performance FPS: "));
        var fpsBrush = _averageFps > 55 ? new ThemeResourceBrush("AccentBrush") : new ThemeResourceBrush("TextPrimary");
        var fpsRun = new Run($"{_averageFps:F1} FPS\n");
        _statsBlock.Inlines.Add(new Bold(fpsRun));

        _statsBlock.Inlines.Add(new Run("CPU Render Time: "));
        _statsBlock.Inlines.Add(new Bold(new Run($"{_smoothedFrameTimeMs:F3} ms\n")));

        _statsBlock.Inlines.Add(new Run("Heap Allocations: "));
        var allocBrush = _averageAllocationsPerSecond == 0 ? new ThemeResourceBrush("AccentBrush") : new ThemeResourceBrush("TextPrimary");
        var allocText = _averageAllocationsPerSecond == 0 ? "0 Bytes (Zero-Alloc)\n" : $"{_averageAllocationsPerSecond / (1024f * 1024f):F2} MB/sec\n";
        var allocRun = new Run(allocText);
        _statsBlock.Inlines.Add(new Bold(allocRun));

        _statsBlock.Inlines.Add(new Run("GC Collections (Gen0/1/2): "));
        _statsBlock.Inlines.Add(new Bold(new Run($"{_gen0Diff} / {_gen1Diff} / {_gen2Diff} per sec")));
    }

    public override void OnRender(DrawingContext context)
    {
        // Dark Tech Charcoal Canvas Background card
        context.DrawRectangle(new ThemeResourceBrush("ControlBackground"), new Pen(new ThemeResourceBrush("ControlBorder"), 1f), new Rect(0, 0, Size.X, Size.Y));

        if (_cachedPicture == null) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Calculate dynamic orbital rotation transform matrix around center
        var center = Size * 0.5f;
        float baseScale = (Math.Min(Size.X, Size.Y) / 1200f) * 0.85f * ZoomFactor;
        float dynamicScale = baseScale * (1.0f + MathF.Sin(_time * 0.4f) * 0.08f);
        float rotationAngle = _time * 0.12f;

        var viewMatrix = Matrix4x4.CreateTranslation(-600f, -600f, 0f) *
                         Matrix4x4.CreateScale(dynamicScale, dynamicScale, 1f) *
                         Matrix4x4.CreateRotationZ(rotationAngle) *
                         Matrix4x4.CreateTranslation(center.X, center.Y, 0f);

        context.PushClip(new Rect(0f, 0f, Size.X, Size.Y));

        if (EnableCaching)
        {
            // Cached Playback Path: Draw pre-compiled picture commands directly
            context.DrawPicture(_cachedPicture, viewMatrix);
        }
        else
        {
            // Uncached / Manual Path: Construct a fresh picture recorder context and re-compile on every single frame!
            // This copies, uploads, and allocates coordinate collections, showing performance costs.
            var recorder = new GpuPictureRecorder();
            var recCtx = recorder.BeginRecording(new Rect(0, 0, 1200, 1200));
            DrawBlueprintDrawing(recCtx);
            var dynamicPicture = recorder.EndRecording();
            context.DrawPicture(dynamicPicture, viewMatrix);
        }

        context.PopClip();

        sw.Stop();
        _lastFrameTimeMs = sw.Elapsed.TotalMilliseconds;
        
        // Rolling smooth average of frame times
        _smoothedFrameTimeMs = _smoothedFrameTimeMs * 0.95 + _lastFrameTimeMs * 0.05;
    }
}

public static class PictureShowcasePage
{
    public static FrameworkElement Create()
    {
        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new GridLength(50, GridUnitType.Absolute));   // Header Panel
        mainGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Columns

        // Header Description Block
        var descText = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(12, 12, 12, 4) };
        descText.Inlines.Add(new Bold(new Run("Zero-Allocation GpuPicture Vector Caching Showcase\n")));
        descText.Inlines.Add(new Run("Compare pre-recorded command caches (Skia-like picture buffers) vs. dynamic rebuilding pipelines. Observe real-time frame telemetry, memory allocations, and GC cycles."));
        mainGrid.AddChild(descText);
        Grid.SetRow(descText, 0);

        // Body Content Columns
        var contentGrid = new Grid { Margin = new Thickness(12) };
        contentGrid.ColumnDefinitions.Add(new GridLength(280, GridUnitType.Absolute)); // Sidebar controller
        contentGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Vector visual canvas

        // Sidebar Panel
        var sidebarBorder = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(16f),
            Margin = new Thickness(0, 0, 12, 0)
        };
        var sidebarStack = new StackPanel { Orientation = Orientation.Vertical };
        
        var ctrlTitle = new RichTextBlock { Font = AppState._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 16) };
        ctrlTitle.Inlines.Add(new Bold(new Run("Controls & Configurations")));
        sidebarStack.AddChild(ctrlTitle);

        // Metric Statistics Card (Acrylic look border)
        var telemetryCard = new Border
        {
            Background = new ThemeResourceBrush("ControlBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 6f,
            Padding = new Thickness(12f),
            Margin = new Thickness(0, 0, 0, 16)
        };
        
        var telemetryText = new RichTextBlock { Font = AppState._font, FontSize = 11.5f };
        telemetryText.Inlines.Add(new Bold(new Run("Engine Telemetry Metrics\n")));
        telemetryText.Inlines.Add(new Run("Initializing diagnostics stream..."));
        telemetryCard.Child = telemetryText;
        sidebarStack.AddChild(telemetryCard);

        // Core visual instanced below and bound to controls
        var pictureVisual = new PictureShowcaseVisual(telemetryText);

        // Toggle Caching Button
        var toggleCacheBtn = new Button
        {
            Width = 248f,
            Height = 36f,
            CornerRadius = 6f,
            Margin = new Thickness(0, 0, 0, 12),
            Background = new ThemeResourceBrush("AccentBrush")
        };
        var toggleBtnText = new RichTextBlock { Font = AppState._font, FontSize = 12f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var toggleRun = new Run("Switch to Manual Redraw");
        var boldToggle = new Bold(toggleRun);
        toggleBtnText.Inlines.Add(boldToggle);
        toggleCacheBtn.Content = toggleBtnText;
        
        toggleCacheBtn.Click += (s, e) =>
        {
            pictureVisual.EnableCaching = !pictureVisual.EnableCaching;
            toggleRun.Text = pictureVisual.EnableCaching ? "Switch to Manual Redraw" : "Enable GpuPicture Caching";
            toggleCacheBtn.Background = pictureVisual.EnableCaching ? new ThemeResourceBrush("AccentBrush") : new ThemeResourceBrush("ControlBackground");
        };
        sidebarStack.AddChild(toggleCacheBtn);

        // Speed adjustment label and buttons
        var speedLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 6) };
        speedLabel.Inlines.Add(new Run("Orbital Rotation Velocity:"));
        sidebarStack.AddChild(speedLabel);

        var speedStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        
        var speedSlowBtn = new Button { Width = 76f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 8, 0) };
        var slowText = new RichTextBlock { Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        slowText.Inlines.Add(new Run("0.25x"));
        speedSlowBtn.Content = slowText;
        speedSlowBtn.Click += (s, e) => pictureVisual.SpeedFactor = 0.25f;
        speedStack.AddChild(speedSlowBtn);

        var speedNormBtn = new Button { Width = 76f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 8, 0) };
        var normText = new RichTextBlock { Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        normText.Inlines.Add(new Run("1.0x"));
        speedNormBtn.Content = normText;
        speedNormBtn.Click += (s, e) => pictureVisual.SpeedFactor = 1.0f;
        speedStack.AddChild(speedNormBtn);

        var speedFastBtn = new Button { Width = 76f, Height = 28f, CornerRadius = 4f };
        var fastText = new RichTextBlock { Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        fastText.Inlines.Add(new Run("2.5x"));
        speedFastBtn.Content = fastText;
        speedFastBtn.Click += (s, e) => pictureVisual.SpeedFactor = 2.5f;
        speedStack.AddChild(speedFastBtn);

        sidebarStack.AddChild(speedStack);

        // Zoom adjustment label and buttons
        var zoomLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 6) };
        zoomLabel.Inlines.Add(new Run("Model Zoom Scale:"));
        sidebarStack.AddChild(zoomLabel);

        var zoomStack = new StackPanel { Orientation = Orientation.Horizontal };
        
        var zoomOutBtn = new Button { Width = 118f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 12, 0) };
        var zoomOutText = new RichTextBlock { Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        zoomOutText.Inlines.Add(new Run("Zoom Out (0.6x)"));
        zoomOutBtn.Content = zoomOutText;
        zoomOutBtn.Click += (s, e) => pictureVisual.ZoomFactor = 0.6f;
        zoomStack.AddChild(zoomOutBtn);

        var zoomInBtn = new Button { Width = 118f, Height = 28f, CornerRadius = 4f };
        var zoomInText = new RichTextBlock { Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        zoomInText.Inlines.Add(new Run("Zoom In (1.3x)"));
        zoomInBtn.Content = zoomInText;
        zoomInBtn.Click += (s, e) => pictureVisual.ZoomFactor = 1.3f;
        zoomStack.AddChild(zoomInBtn);

        sidebarStack.AddChild(zoomStack);

        sidebarBorder.Child = sidebarStack;
        contentGrid.AddChild(sidebarBorder);
        Grid.SetColumn(sidebarBorder, 0);

        // Canvas container border
        var canvasBorder = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Child = pictureVisual
        };
        contentGrid.AddChild(canvasBorder);
        Grid.SetColumn(canvasBorder, 1);

        mainGrid.AddChild(contentGrid);
        Grid.SetRow(contentGrid, 1);

        return mainGrid;
    }
}
