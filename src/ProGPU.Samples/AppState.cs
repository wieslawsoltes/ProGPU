using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Silk.NET.Windowing;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Compute;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;

namespace ProGPU.Samples;

public static class AppState
{
    public static IWindow? _window;
    public static WgpuContext? _wgpuContext;
    public static Compositor? _screenCompositor;
    public static Compositor? _offscreenCompositor;
    public static ComputeAccelerator? _compute;

    public static IWindow? _devToolsWindow;
    public static WgpuContext? _devToolsWgpuContext;
    public static Compositor? _devToolsCompositor;

    public static TtfFont? _font;
    public static TtfFont? _fontTimes;
    public static TtfFont? _fontCourier;
    public static TtfFont? _fontGeorgia;
    public static TtfFont? _fontComic;
    public static Microsoft.UI.Xaml.Controls.Grid? _rootGrid;
    public static Microsoft.UI.Xaml.Controls.Grid? _topLevelGrid;
    public static Microsoft.UI.Xaml.Controls.DevTools? _devToolsPanel;
    public static bool _needsCloseDevTools;

    // Active diagnostic metric stats
    public static RichTextBlock? _statsText;
    public static Vector2 _mousePos;
    public static string _activeFocusedName = "None";

    // Category pages and sidebar selections
    public static string _activeCategory = "Basic Input";
    public static NavigationView? _navigationView;

    // Framework Effects Page Variables
    public static float _fxBlurRadius = 8f;
    public static float _fxShadowRadius = 12f;
    public static Vector2 _fxShadowOffset = new Vector2(5f, 5f);
    public static Vector4 _fxShadowColor = new Vector4(0f, 0f, 0f, 0.6f);
    public static Vector4 _fxNeonColor = new Vector4(0.85f, 0.08f, 0.52f, 0.8f);

    // Compute FX variables
    public static float _blurRadius = 8f;
    public static float _shadowRadius = 8f;
    public static Vector2 _shadowOffset = new Vector2(4f, 4f);
    public static bool _animateGear = true;
    public static float _gearRotation = 0f;

    // Diagnostic timing
    public static readonly Stopwatch _frameStopwatch = new();
    public static double _fpsAccumulator = 0;
    public static int _frameCount = 0;
    public static double _currentFps = 60;
    public static double _cpuFrameTimeMs = 0;

    // Compute effect textures
    public static GpuTexture? _canvasSourceTexture;
    public static GpuTexture? _canvasTempTexture;
    public static GpuTexture? _canvasBlurTexture;
    public static GpuTexture? _canvasShadowTexture;

    // DXF viewer optimization controls
    public static bool EnableGpuTransforms { get; set; } = false;
    public static bool EnableStaticGpuBuffers { get; set; } = false;
    public static bool EnableCommandCaching { get; set; } = false;
    public static Compositor.VectorRenderingEngine VectorEngine { get; set; } = Compositor.VectorRenderingEngine.Atlas;

    // Basic Input Page Interactive State
    public static int _clickCount = 0;
    public static string _checkboxStatus = "Unchecked";
    public static float _sliderValue = 50f;

    public static readonly List<object> _logItems = new();

    // Image/Button Repeat
    public static int _repeatCount = 0;

    public static GearCanvasVisual? _gearCanvasVisual;
    public static float GetBlurRadius() => _blurRadius;
    public static float GetShadowRadius() => _shadowRadius;
    public static TtfFont? GetFont() => _font;
    public static TtfFont? GetFontTimes() => _fontTimes;
    public static TtfFont? GetFontCourier() => _fontCourier;
    public static TtfFont? GetFontGeorgia() => _fontGeorgia;
    public static TtfFont? GetFontComic() => _fontComic;

    public static void GenerateLogItems()
    {
        _logItems.Clear();
        for (int i = 0; i < 10000; i++)
        {
            _logItems.Add(new LogItem
            {
                Id = i + 1,
                Name = $"Dispatcher.QueueEvent #{i + 1:N0}",
                Status = (i % 3 == 0) ? "OK" : ((i % 3 == 1) ? "PENDING" : "WARNING"),
                Latency = Math.Abs(Math.Sin(i * 0.05) * 45.0 + Math.Cos(i * 0.2) * 5.0 + 10.0)
            });
        }
    }

    public static void EnsureLogItemsGenerated()
    {
        if (_logItems.Count == 0)
        {
            GenerateLogItems();
        }
    }
}
