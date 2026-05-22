using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Silk.NET.Windowing;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using ProGPU.Compute;
using ProGPU.Virtualization;

namespace ProGPU.Samples;

public static unsafe class Program
{
    private static IWindow? _window;
    private static WgpuContext? _wgpuContext;
    private static Compositor? _screenCompositor;
    private static Compositor? _offscreenCompositor;
    private static ComputeAccelerator? _compute;

    private static TtfFont? _font;
    private static GridPanel? _rootGrid;
    private static TextVisual? _statsText;
    private static VirtualizingScrollPanel? _virtualScrollPanel;
    private static GearCanvasVisual? _gearCanvasVisual;

    private static readonly List<SliderControl> _sliders = new();
    private static ToggleButton? _animToggle;

    private static float _blurRadius = 8f;
    private static float _shadowRadius = 8f;
    private static Vector2 _shadowOffset = new Vector2(4f, 4f);
    private static bool _animateGear = true;
    private static float _gearRotation = 0f;

    private static readonly Stopwatch _frameStopwatch = new();
    private static double _fpsAccumulator = 0;
    private static int _frameCount = 0;
    private static double _currentFps = 60;
    private static double _cpuFrameTimeMs = 0;

    private static GpuTexture? _canvasSourceTexture;
    private static GpuTexture? _canvasTempTexture;
    private static GpuTexture? _canvasBlurTexture;
    private static GpuTexture? _canvasShadowTexture;

    public static float GetBlurRadius() => _blurRadius;
    public static float GetShadowRadius() => _shadowRadius;
    public static TtfFont? GetFont() => _font;

    public static void Main()
    {
        // 1. Create native desktop window option using GLFW
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(1280, 800);
        options.Title = "ProGPU Substrate - 1,000,000 Record Glassmorphic Dashboard";
        options.API = GraphicsAPI.None; // Zero OpenGL, standard WebGPU context bypass

        _window = Window.Create(options);

        _window.Load += OnWindowLoad;
        _window.Update += OnWindowUpdate;
        _window.Render += OnWindowRender;
        _window.Resize += OnWindowResize;

        Console.WriteLine("[ProGPU.Samples] Starting GPU-first UI Infrastructure Dashboard...");
        _window.Run();
        
        // Cleanup after closing
        Cleanup();
    }

    private static void OnWindowLoad()
    {
        if (_window == null) return;

        // 2. Initialize WebGPU Context
        _wgpuContext = new WgpuContext();
        _wgpuContext.Initialize(_window);

        // 3. Instantiate Dual-Compositor pipelines (screen Bgra8Unorm, offscreen Rgba8Unorm)
        _screenCompositor = new Compositor(_wgpuContext, _wgpuContext.SwapChainFormat);
        _offscreenCompositor = new Compositor(_wgpuContext, TextureFormat.Rgba8Unorm);
        _compute = new ComputeAccelerator(_wgpuContext);

        // 4. Load system Arial TrueType font outline parser
        string fontPath = "/System/Library/Fonts/Supplemental/Arial.ttf";
        if (!File.Exists(fontPath))
        {
            // Fallback for Windows or Linux if they run this later
            fontPath = "Arial.ttf";
        }

        if (File.Exists(fontPath))
        {
            Console.WriteLine($"[ProGPU.Samples] Loading TrueType Font: {fontPath}");
            _font = new TtfFont(fontPath);
        }
        else
        {
            throw new FileNotFoundException("Arial.ttf is required to execute typography. Ensure a standard Arial TrueType font path is available.");
        }

        // 5. Build dynamic Offscreen effect textures
        _canvasSourceTexture = new GpuTexture(_wgpuContext, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.StorageBinding | TextureUsage.CopySrc);
        _canvasTempTexture = new GpuTexture(_wgpuContext, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.TextureBinding | TextureUsage.StorageBinding);
        _canvasBlurTexture = new GpuTexture(_wgpuContext, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.TextureBinding | TextureUsage.StorageBinding);
        _canvasShadowTexture = new GpuTexture(_wgpuContext, 600, 600, TextureFormat.Rgba8Unorm, 
            TextureUsage.TextureBinding | TextureUsage.StorageBinding);

        // 6. Build retaining scene graph layout
        BuildSceneGraph();

        // 7. Setup Mouse and Input Handlers
        SetupInput();
    }

    private static void BuildSceneGraph()
    {
        if (_wgpuContext == null || _font == null) return;

        // Root grid containing: Top Header + Body Content
        _rootGrid = new GridPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _rootGrid.RowDefinitions.Add(new GridLength(70, GridUnitType.Absolute)); // Row 0
        _rootGrid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));     // Row 1

        // HEADER
        var headerBar = new BorderPanel
        {
            Background = new SolidColorBrush(0x0C0C12FF),
            Border = new Pen(new SolidColorBrush(0x222230FF), 1.5f),
            Padding = new Thickness(20, 10, 20, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var headerGrid = new GridPanel();
        headerGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        headerGrid.ColumnDefinitions.Add(new GridLength(300f, GridUnitType.Absolute));

        var titleText = new TextVisual
        {
            Text = "ProGPU Substrate Dashboard",
            FontSize = 20f,
            Brush = new SolidColorBrush(0xFFFFFFFF),
            Font = _font,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerGrid.SetColumn(titleText, 0);
        headerGrid.AddChild(titleText);

        var subtitleText = new TextVisual
        {
            Text = ".NET 10 cross-platform high-performance engine",
            FontSize = 11f,
            Brush = new SolidColorBrush(0x8888A0FF),
            Font = _font,
            VerticalAlignment = VerticalAlignment.Bottom,
            Alignment = TextAlignment.Right
        };
        headerGrid.SetColumn(subtitleText, 1);
        headerGrid.AddChild(subtitleText);

        headerBar.AddChild(headerGrid);
        _rootGrid.SetRow(headerBar, 0);
        _rootGrid.AddChild(headerBar);

        // BODY Workspace
        var bodyGrid = new GridPanel();
        bodyGrid.ColumnDefinitions.Add(new GridLength(320, GridUnitType.Absolute)); // Col 0: Controls
        bodyGrid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));     // Col 1: Vector canvas
        bodyGrid.ColumnDefinitions.Add(new GridLength(380, GridUnitType.Absolute)); // Col 2: Virtual list log

        // Sidebar Card
        var sidebarCard = new BorderPanel
        {
            CornerRadius = 8f,
            Background = new SolidColorBrush(0x13131AFF),
            Border = new Pen(new SolidColorBrush(0x2A2A38FF), 1f),
            Padding = new Thickness(15),
            Margin = new Thickness(10)
        };

        var leftStack = new StackPanel { Orientation = Orientation.Vertical };

        var panelTitle = new TextVisual
        {
            FontSize = 15f,
            Text = "PRO-GPU CONTROLS",
            Brush = new SolidColorBrush(0x00E5FFFF),
            Font = _font,
            Margin = new Thickness(0, 0, 0, 15)
        };
        leftStack.AddChild(panelTitle);

        var diagTitle = new TextVisual
        {
            FontSize = 11f,
            Text = "DIAGNOSTICS & METRICS",
            Brush = new SolidColorBrush(0x888899FF),
            Font = _font,
            Margin = new Thickness(0, 10, 0, 5)
        };
        leftStack.AddChild(diagTitle);

        _statsText = new TextVisual
        {
            FontSize = 11f,
            Text = "FPS: -- | CPU: -- ms",
            Brush = new SolidColorBrush(0xBBBBC5FF),
            Font = _font,
            Margin = new Thickness(0, 0, 0, 15)
        };
        leftStack.AddChild(_statsText);

        var slidersTitle = new TextVisual
        {
            FontSize = 11f,
            Text = "WGSL COMPUTE EFFECTS",
            Brush = new SolidColorBrush(0x888899FF),
            Font = _font,
            Margin = new Thickness(0, 10, 0, 5)
        };
        leftStack.AddChild(slidersTitle);

        var blurSlider = new SliderControl("Backdrop Blur", 0f, 20f, _blurRadius, "px");
        blurSlider.ValueChanged += (s, val) => _blurRadius = val;
        leftStack.AddChild(blurSlider);
        _sliders.Add(blurSlider);

        var shadowSlider = new SliderControl("Shadow Blur", 0f, 20f, _shadowRadius, "px");
        shadowSlider.ValueChanged += (s, val) => _shadowRadius = val;
        leftStack.AddChild(shadowSlider);
        _sliders.Add(shadowSlider);

        var offsetXSlider = new SliderControl("Shadow Offset X", -20f, 20f, _shadowOffset.X, "px");
        offsetXSlider.ValueChanged += (s, val) => _shadowOffset.X = val;
        leftStack.AddChild(offsetXSlider);
        _sliders.Add(offsetXSlider);

        var offsetYSlider = new SliderControl("Shadow Offset Y", -20f, 20f, _shadowOffset.Y, "px");
        offsetYSlider.ValueChanged += (s, val) => _shadowOffset.Y = val;
        leftStack.AddChild(offsetYSlider);
        _sliders.Add(offsetYSlider);

        var btnTitle = new TextVisual
        {
            FontSize = 11f,
            Text = "VECTOR COG ANIMATION",
            Brush = new SolidColorBrush(0x888899FF),
            Font = _font,
            Margin = new Thickness(0, 10, 0, 5)
        };
        leftStack.AddChild(btnTitle);

        _animToggle = new ToggleButton("Rotate Gears", _animateGear);
        _animToggle.CheckedChanged += (s, val) => _animateGear = val;
        leftStack.AddChild(_animToggle);

        sidebarCard.AddChild(leftStack);
        bodyGrid.SetColumn(sidebarCard, 0);
        bodyGrid.AddChild(sidebarCard);

        // Center Panel Canvas
        _gearCanvasVisual = new GearCanvasVisual(_font)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        
        var canvasContainer = new BorderPanel
        {
            CornerRadius = 8f,
            Background = new SolidColorBrush(0x0C0C12FF),
            Border = new Pen(new SolidColorBrush(0x222230FF), 1f),
            Margin = new Thickness(0, 10, 0, 10)
        };

        var displayCanvas = new GpuTextureCanvas(_canvasSourceTexture!, _canvasShadowTexture!, _canvasBlurTexture!);
        canvasContainer.AddChild(displayCanvas);
        
        bodyGrid.SetColumn(canvasContainer, 1);
        bodyGrid.AddChild(canvasContainer);

        // Right Panel List
        var rightPanelCard = new BorderPanel
        {
            CornerRadius = 8f,
            Background = new SolidColorBrush(0x13131AFF),
            Border = new Pen(new SolidColorBrush(0x2A2A38FF), 1f),
            Padding = new Thickness(10),
            Margin = new Thickness(10)
        };

        var rightStack = new StackPanel { Orientation = Orientation.Vertical };
        var listTitle = new TextVisual
        {
            FontSize = 13f,
            Text = "SYSTEM ACTIVITY LOGS",
            Brush = new SolidColorBrush(0x00FF88FF),
            Font = _font,
            Margin = new Thickness(5, 5, 5, 10)
        };
        rightStack.AddChild(listTitle);

        _virtualScrollPanel = new VirtualizingScrollPanel
        {
            ItemsCount = 1000000,
            ItemHeight = 55f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _virtualScrollPanel.CreateVisualFactory = () =>
        {
            var rowCard = new BorderPanel
            {
                CornerRadius = 6f,
                Background = new SolidColorBrush(0x1E1E26FF),
                Border = new Pen(new SolidColorBrush(0x333344FF), 1f),
                Margin = new Thickness(5, 4, 5, 4),
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var grid = new GridPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            grid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
            grid.ColumnDefinitions.Add(new GridLength(100f, GridUnitType.Absolute));

            var logText = new TextVisual
            {
                FontSize = 11f,
                Brush = new SolidColorBrush(0xE0E0E0FF),
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.SetColumn(logText, 0);
            grid.AddChild(logText);

            var latencyText = new TextVisual
            {
                FontSize = 11f,
                Brush = new SolidColorBrush(0x00FF88FF),
                VerticalAlignment = VerticalAlignment.Center,
                Alignment = TextAlignment.Right
            };
            grid.SetColumn(latencyText, 1);
            grid.AddChild(latencyText);

            rowCard.AddChild(grid);
            return rowCard;
        };

        _virtualScrollPanel.BindVisualCallback = (visual, index) =>
        {
            var rowCard = (BorderPanel)visual;
            var grid = (GridPanel)rowCard.Children[0];
            var logText = (TextVisual)grid.Children[0];
            var latencyText = (TextVisual)grid.Children[1];

            logText.Text = $"System.Dispatch #{(index + 1):N0} [OK]";
            logText.Font = _font;

            double latency = Math.Abs(Math.Sin(index * 0.05) * 45.0 + Math.Cos(index * 0.2) * 5.0 + 10.0);
            latencyText.Text = $"{latency:F1} ms";
            latencyText.Font = _font;

            if (latency > 35.0)
            {
                latencyText.Brush = new SolidColorBrush(0xFF5555FF);
                rowCard.Border = new Pen(new SolidColorBrush(0xFF555540), 1f);
            }
            else if (latency > 20.0)
            {
                latencyText.Brush = new SolidColorBrush(0xFFB800FF);
                rowCard.Border = new Pen(new SolidColorBrush(0xFFB80040), 1f);
            }
            else
            {
                latencyText.Brush = new SolidColorBrush(0x00FF88FF);
                rowCard.Border = new Pen(new SolidColorBrush(0x00FF8840), 1f);
            }
        };

        rightStack.AddChild(_virtualScrollPanel);
        rightPanelCard.AddChild(rightStack);

        bodyGrid.SetColumn(rightPanelCard, 2);
        bodyGrid.AddChild(rightPanelCard);

        _rootGrid.SetRow(bodyGrid, 1);
        _rootGrid.AddChild(bodyGrid);
    }

    private static void SetupInput()
    {
        if (_window == null) return;

        var input = _window.CreateInput();
        foreach (var mouse in input.Mice)
        {
            mouse.Scroll += (m, wheel) =>
            {
                if (_virtualScrollPanel != null)
                {
                    // Dynamic viewport scroll
                    _virtualScrollPanel.ScrollOffset -= wheel.Y * 20f;
                    _rootGrid?.Invalidate();
                }
            };

            bool isMouseDown = false;
            mouse.MouseDown += (m, button) =>
            {
                if (button == MouseButton.Left)
                {
                    isMouseDown = true;
                    var pos = mouse.Position;
                    Vector2 mousePos = new Vector2(pos.X, pos.Y);
                    
                    // Hit test Sidebar controls (starts at X=10, Y=80)
                    if (mousePos.X >= 10 && mousePos.X <= 310 && mousePos.Y >= 80)
                    {
                        foreach (var slider in _sliders)
                        {
                            Vector2 localPos = mousePos - slider.Offset;
                            if (slider.HandleMouseDown(localPos))
                            {
                                _rootGrid?.Invalidate();
                                break;
                            }
                        }

                        if (_animToggle != null)
                        {
                            Vector2 localPos = mousePos - _animToggle.Offset;
                            if (_animToggle.HandleMouseDown(localPos))
                            {
                                _rootGrid?.Invalidate();
                            }
                        }
                    }
                }
            };

            mouse.MouseUp += (m, button) =>
            {
                if (button == MouseButton.Left)
                {
                    isMouseDown = false;
                }
            };

            mouse.MouseMove += (m, pos) =>
            {
                if (isMouseDown)
                {
                    Vector2 mousePos = new Vector2(pos.X, pos.Y);
                    if (mousePos.X >= 10 && mousePos.X <= 310 && mousePos.Y >= 80)
                    {
                        foreach (var slider in _sliders)
                        {
                            Vector2 localPos = mousePos - slider.Offset;
                            if (slider.HandleMouseDown(localPos))
                            {
                                _rootGrid?.Invalidate();
                                break;
                            }
                        }
                    }
                }
            };
        }
    }

    private static void OnWindowUpdate(double delta)
    {
        if (_animateGear)
        {
            _gearRotation += (float)delta * 1.2f;
            if (_gearRotation > Math.PI * 2) _gearRotation -= (float)(Math.PI * 2);
            _rootGrid?.Invalidate();
        }
    }

    private static void OnWindowRender(double delta)
    {
        if (_rootGrid == null || _wgpuContext == null || _window == null) return;
        if (_screenCompositor == null || _offscreenCompositor == null || _compute == null) return;

        _frameStopwatch.Restart();

        // 1. Perform Measure and Arrange negotiate sizing layouts
        _rootGrid.Measure(new Vector2(_window.Size.X, _window.Size.Y));
        _rootGrid.Arrange(new Rect(0, 0, _window.Size.X, _window.Size.Y));

        // Update animated cogs matrix transformations
        if (_gearCanvasVisual != null)
        {
            _gearCanvasVisual.UpdateRotation(_gearRotation);

            // 2. Offscreen Canvas render (Gears subtree)
            uint canvasW = (uint)Math.Max(1f, _gearCanvasVisual.Size.X);
            uint canvasH = (uint)Math.Max(1f, _gearCanvasVisual.Size.Y);

            if (_canvasSourceTexture != null && _canvasTempTexture != null && _canvasBlurTexture != null && _canvasShadowTexture != null)
            {
                _canvasSourceTexture.Resize(canvasW, canvasH);
                _canvasTempTexture.Resize(canvasW, canvasH);
                _canvasBlurTexture.Resize(canvasW, canvasH);
                _canvasShadowTexture.Resize(canvasW, canvasH);

                // Render vector gears directly to our Rgba8Unorm canvas source texture
                _offscreenCompositor.RenderScene(_gearCanvasVisual, canvasW, canvasH, _canvasSourceTexture.ViewPtr);

                // 3. Execute real-time dynamic WGSL compute shaders
                if (_shadowRadius > 0)
                {
                    var shadowColor = new Vector4(0f, 0f, 0f, 0.65f); // elegant shadow alpha
                    _compute.ApplyDropShadow(_canvasSourceTexture, _canvasShadowTexture, _shadowOffset, shadowColor, _shadowRadius);
                }

                if (_blurRadius > 0)
                {
                    _compute.ApplyGaussianBlur(_canvasSourceTexture, _canvasTempTexture, _canvasBlurTexture);
                }
            }
        }

        // 4. Update dynamic overlay diagnostics labels
        _cpuFrameTimeMs = _frameStopwatch.Elapsed.TotalMilliseconds;
        
        _frameCount++;
        _fpsAccumulator += delta;
        if (_fpsAccumulator >= 0.5)
        {
            _currentFps = _frameCount / _fpsAccumulator;
            _frameCount = 0;
            _fpsAccumulator = 0;
        }

        if (_statsText != null)
        {
            _statsText.Text = $"FPS: {_currentFps:F0} | CPU: {_cpuFrameTimeMs:F2} ms | Virtual Recycled: 1,000,000 cogs";
        }

        // 5. Get current swapchain view and render primary dashboard layout
        TextureView* targetView = null;
        if (_wgpuContext.Surface != null)
        {
            var surfaceTexture = new SurfaceTexture();
            _wgpuContext.Wgpu.SurfaceGetCurrentTexture(_wgpuContext.Surface, &surfaceTexture);
            
            if (surfaceTexture.Status == SurfaceGetCurrentTextureStatus.Success)
            {
                var viewDesc = new TextureViewDescriptor
                {
                    Format = _wgpuContext.SwapChainFormat,
                    Dimension = TextureViewDimension.Dimension2D,
                    BaseMipLevel = 0,
                    MipLevelCount = 1,
                    BaseArrayLayer = 0,
                    ArrayLayerCount = 1,
                    Aspect = TextureAspect.All
                };
                targetView = _wgpuContext.Wgpu.TextureCreateView(surfaceTexture.Texture, &viewDesc);
            }
        }

        if (targetView != null)
        {
            _screenCompositor.RenderScene(_rootGrid, (uint)_window.Size.X, (uint)_window.Size.Y, targetView);
            
            // Present screen surface
            _wgpuContext.Wgpu.SurfacePresent(_wgpuContext.Surface);
            _wgpuContext.Wgpu.TextureViewRelease(targetView);
        }
    }

    private static void OnWindowResize(Vector2D<int> newSize)
    {
        if (_wgpuContext == null) return;
        _wgpuContext.ConfigureSwapChain((uint)newSize.X, (uint)newSize.Y);
        _rootGrid?.Invalidate();
    }

    private static void Cleanup()
    {
        _canvasSourceTexture?.Dispose();
        _canvasTempTexture?.Dispose();
        _canvasBlurTexture?.Dispose();
        _canvasShadowTexture?.Dispose();

        _compute?.Dispose();
        _offscreenCompositor?.Dispose();
        _screenCompositor?.Dispose();
        _wgpuContext?.Dispose();
    }

    // Mathematical Hollow Gear geometry path builder
    public static PathGeometry CreateGearPath(Vector2 center, float innerRadius, float outerRadius, int teethCount, float toothDepth)
    {
        var path = new PathGeometry();
        var fig = new PathFigure { IsClosed = true, IsFilled = true };

        float angleStep = (float)(Math.PI * 2.0 / teethCount);
        
        for (int i = 0; i < teethCount; i++)
        {
            float angle = i * angleStep;
            
            float a0 = angle;
            float a1 = angle + angleStep * 0.25f;
            float a2 = angle + angleStep * 0.55f;
            float a3 = angle + angleStep * 0.8f;

            Vector2 pt0 = center + new Vector2((float)Math.Cos(a0), (float)Math.Sin(a0)) * innerRadius;
            Vector2 pt1 = center + new Vector2((float)Math.Cos(a1), (float)Math.Sin(a1)) * outerRadius;
            Vector2 pt2 = center + new Vector2((float)Math.Cos(a2), (float)Math.Sin(a2)) * outerRadius;
            Vector2 pt3 = center + new Vector2((float)Math.Cos(a3), (float)Math.Sin(a3)) * innerRadius;

            if (i == 0)
            {
                fig.StartPoint = pt0;
            }
            else
            {
                fig.Segments.Add(new LineSegment(pt0));
            }
            
            fig.Segments.Add(new LineSegment(pt1));
            fig.Segments.Add(new LineSegment(pt2));
            fig.Segments.Add(new LineSegment(pt3));
        }
        
        path.Figures.Add(fig);

        // Circular cutout center hole
        var cutoutFig = new PathFigure { IsClosed = true, IsFilled = true };
        float cutRadius = innerRadius * 0.6f;
        int circleSegments = 32;
        for (int i = 0; i < circleSegments; i++)
        {
            float a = -(float)(i * Math.PI * 2.0 / circleSegments);
            Vector2 pt = center + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * cutRadius;
            
            if (i == 0)
                cutoutFig.StartPoint = pt;
            else
                cutoutFig.Segments.Add(new LineSegment(pt));
        }
        path.Figures.Add(cutoutFig);

        return path;
    }
}

// ==========================================
// Custom Sibling Layout & Rendering Visual Nodes
// ==========================================

public class BorderPanel : LayoutNode
{
    public Brush? Background { get; set; }
    public Pen? Border { get; set; }
    public float CornerRadius { get; set; }

    public override void OnRender(DrawingContext context)
    {
        if (Background != null || Border != null)
        {
            if (CornerRadius <= 0f)
            {
                context.DrawRectangle(Background, Border, new Rect(Vector2.Zero, Size));
            }
            else
            {
                var roundedPath = CreateRoundedRectPath(new Rect(Vector2.Zero, Size), CornerRadius);
                context.DrawPath(Background, Border, roundedPath);
            }
        }
        base.OnRender(context);
    }

    private static PathGeometry CreateRoundedRectPath(Rect rect, float r)
    {
        var geo = new PathGeometry();
        var fig = new PathFigure(new Vector2(rect.X + r, rect.Y), isClosed: true);
        
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + rect.Width - r, rect.Y)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X + rect.Width, rect.Y), new Vector2(rect.X + rect.Width, rect.Y + r)));
        
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + rect.Width, rect.Y + rect.Height - r)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X + rect.Width, rect.Y + rect.Height), new Vector2(rect.X + rect.Width - r, rect.Y + rect.Height)));
        
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + r, rect.Y + rect.Height)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X, rect.Y + rect.Height), new Vector2(rect.X, rect.Y + rect.Height - r)));
        
        fig.Segments.Add(new LineSegment(new Vector2(rect.X, rect.Y + r)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X, rect.Y), new Vector2(rect.X + r, rect.Y)));
        
        geo.Figures.Add(fig);
        return geo;
    }
}

public class SliderControl : LayoutNode
{
    private float _value;
    public float Min { get; set; }
    public float Max { get; set; }
    public string Label { get; set; }
    public string Unit { get; set; }

    public float Value
    {
        get => _value;
        set
        {
            float clamped = Math.Clamp(value, Min, Max);
            if (_value != clamped)
            {
                _value = clamped;
                UpdateLabel();
                Invalidate();
                ValueChanged?.Invoke(this, _value);
            }
        }
    }

    public event EventHandler<float>? ValueChanged;

    private readonly TextVisual _labelText;
    private readonly BorderPanel _track;
    private readonly BorderPanel _thumb;

    public SliderControl(string label, float min, float max, float initialValue, string unit = "")
    {
        Label = label;
        Min = min;
        Max = max;
        Unit = unit;
        HeightConstraint = 42f;
        Margin = new Thickness(0, 4, 0, 4);

        _labelText = new TextVisual
        {
            FontSize = 11f,
            Brush = new SolidColorBrush(0x888899FF),
            Size = new Vector2(280f, 15f),
            Font = Program.GetFont()
        };

        _track = new BorderPanel
        {
            Background = new SolidColorBrush(0x242430FF),
            CornerRadius = 3f,
            HeightConstraint = 6f,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 3)
        };

        _thumb = new BorderPanel
        {
            Background = new SolidColorBrush(0x00E5FFFF),
            CornerRadius = 6f,
            WidthConstraint = 12f,
            HeightConstraint = 12f,
            VerticalAlignment = VerticalAlignment.Center
        };

        var mainStack = new StackPanel { Orientation = Orientation.Vertical };
        mainStack.AddChild(_labelText);
        
        var trackContainer = new GridPanel();
        trackContainer.AddChild(_track);
        trackContainer.AddChild(_thumb);
        mainStack.AddChild(trackContainer);
        
        AddChild(mainStack);

        Value = initialValue;
        UpdateLabel();
    }

    public void UpdateLabel()
    {
        _labelText.Text = $"{Label}: {Value:F1} {Unit}";
        _labelText.Font = Program.GetFont();
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        base.ArrangeOverride(arrangeRect);

        float percent = (Value - Min) / (Max - Min);
        float trackWidth = Size.X;
        float thumbWidth = 12f;
        float thumbX = percent * (trackWidth - thumbWidth);

        _thumb.Offset = new Vector2(thumbX, _thumb.Offset.Y);
    }

    public bool HandleMouseDown(Vector2 localPos)
    {
        if (localPos.Y >= 15f && localPos.Y <= 42f)
        {
            float percent = Math.Clamp(localPos.X / Size.X, 0f, 1f);
            Value = Min + percent * (Max - Min);
            return true;
        }
        return false;
    }
}

public class ToggleButton : LayoutNode
{
    private bool _checked;
    public string Label { get; set; }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked != value)
            {
                _checked = value;
                UpdateState();
                Invalidate();
                CheckedChanged?.Invoke(this, _checked);
            }
        }
    }

    public event EventHandler<bool>? CheckedChanged;

    private readonly BorderPanel _card;
    private readonly TextVisual _text;

    public ToggleButton(string label, bool initialValue)
    {
        Label = label;
        _checked = initialValue;
        HeightConstraint = 32f;
        Margin = new Thickness(0, 6, 0, 6);

        _card = new BorderPanel
        {
            CornerRadius = 5f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _text = new TextVisual
        {
            FontSize = 11f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Font = Program.GetFont()
        };

        _card.AddChild(_text);
        AddChild(_card);

        UpdateState();
    }

    private void UpdateState()
    {
        _text.Font = Program.GetFont();
        if (_checked)
        {
            _card.Background = new SolidColorBrush(0x8B00FFFF); // bright purple
            _card.Border = new Pen(new SolidColorBrush(0xD080FFFF), 1f);
            _text.Text = $"{Label}: ON";
            _text.Brush = new SolidColorBrush(0xFFFFFFFF);
        }
        else
        {
            _card.Background = new SolidColorBrush(0x1F1F28FF);
            _card.Border = new Pen(new SolidColorBrush(0x383848FF), 1f);
            _text.Text = $"{Label}: OFF";
            _text.Brush = new SolidColorBrush(0x777788FF);
        }
    }

    public bool HandleMouseDown(Vector2 localPos)
    {
        if (localPos.X >= 0 && localPos.X <= Size.X && localPos.Y >= 0 && localPos.Y <= Size.Y)
        {
            Checked = !Checked;
            return true;
        }
        return false;
    }
}

public class GearCanvasVisual : LayoutNode
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
            var p1 = Program.CreateGearPath(Vector2.Zero, 85f, 115f, 16, 20f);
            _gear1.Context.DrawPath(new SolidColorBrush(0x00E5FFFF), new Pen(new SolidColorBrush(0xFFFFFFFF), 1.5f), p1);

            var p2 = Program.CreateGearPath(Vector2.Zero, 52f, 78f, 12, 18f);
            _gear2.Context.DrawPath(new SolidColorBrush(0xA100FFFF), new Pen(new SolidColorBrush(0xFFFFFFFF), 1.5f), p2);

            var p3 = Program.CreateGearPath(Vector2.Zero, 35f, 55f, 8, 15f);
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

public class GpuTextureCanvas : LayoutNode
{
    private readonly GpuTexture _source;
    private readonly GpuTexture _shadow;
    private readonly GpuTexture _blur;

    public GpuTextureCanvas(GpuTexture source, GpuTexture shadow, GpuTexture blur)
    {
        _source = source;
        _shadow = shadow;
        _blur = blur;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    public override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(new SolidColorBrush(0x0C0C12FF), null, new Rect(Vector2.Zero, Size));

        Rect r = new Rect(Vector2.Zero, Size);

        if (Program.GetShadowRadius() > 0)
        {
            context.DrawTexture(_shadow, r);
        }

        context.DrawTexture(_source, r);

        if (Program.GetBlurRadius() > 0)
        {
            float cardW = Math.Min(310f, Size.X * 0.8f);
            float cardH = Math.Min(180f, Size.Y * 0.6f);
            float cardX = (Size.X - cardW) / 2f;
            float cardY = (Size.Y - cardH) / 2f;
            Rect cardRect = new Rect(cardX, cardY, cardW, cardH);

            context.PushClip(cardRect);
            context.DrawTexture(_blur, r);

            var glassBg = new SolidColorBrush(0xFFFFFF15);
            var glassBorder = new Pen(new SolidColorBrush(0xFFFFFF35), 1.2f);
            context.DrawRectangle(glassBg, glassBorder, cardRect);

            context.PopClip();

            var font = Program.GetFont();
            if (font != null)
            {
                context.DrawText("FROSTED ACROSS GLASS", font, 13f, new SolidColorBrush(0x00E5FFFF), new Vector2(cardX + 20f, cardY + 30f));
                context.DrawText("Dual-pass horizontal + vertical", font, 11f, new SolidColorBrush(0xE0E0E0FF), new Vector2(cardX + 20f, cardY + 60f));
                context.DrawText("Backdrop compute blur filter dispatches", font, 10f, new SolidColorBrush(0x888899FF), new Vector2(cardX + 20f, cardY + 85f));
                
                context.DrawText($"Blur Radius: {Program.GetBlurRadius():F1} px", font, 10f, new SolidColorBrush(0x00FF88FF), new Vector2(cardX + 20f, cardY + 115f));
                context.DrawText($"Shadow Radius: {Program.GetShadowRadius():F1} px", font, 10f, new SolidColorBrush(0xFF5588FF), new Vector2(cardX + 160f, cardY + 115f));
            }
        }
    }
}
