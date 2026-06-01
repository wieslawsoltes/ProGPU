using System;
using System.IO;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using ProGPU.Samples;
using ProGPU.Avalonia;
using ProGPU.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProGPU.Samples.Avalonia;

public partial class MainWindow : global::Avalonia.Controls.Window
{
    private bool _isPlaying = true;
    private readonly Stopwatch _stopwatch = new();
    private double _lastTickTime = 0;
    private int _frameCount = 0;
    private double _fpsTimer = 0;
    private double _currentFps = 0;
    private global::Avalonia.Controls.Window? _devToolsWindow;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 1. Initialize ProGPU text rendering default system font
        string fontPath = "/System/Library/Fonts/Supplemental/Arial.ttf";
        if (!File.Exists(fontPath)) fontPath = "Arial.ttf";
        if (File.Exists(fontPath))
        {
            Microsoft.UI.Xaml.Controls.PopupService.DefaultFont = new TtfFont(fontPath);
        }

        // 2. Start the embedded offscreen context and compositor
        MainWindowController.StartEmbedded(ProGpuHost.WgpuContext!, ProGpuHost.Compositor!);

        // 3. Setup event listeners for the host controls
        SidebarList.SelectionChanged += OnSidebarSelectionChanged;
        ThemeCombo.SelectionChanged += OnThemeSelectionChanged;
        PlayPauseBtn.Click += OnPlayPauseClicked;

        // Set default selections
        SidebarList.SelectedIndex = 0;
        ThemeCombo.SelectedIndex = 0;

        // 4. Hook up the native VSync-locked animation loop using RequestAnimationFrame
        _stopwatch.Start();
        _lastTickTime = _stopwatch.Elapsed.TotalSeconds;
        RequestAnimationFrame(OnAnimationTick);

        // 5. Hook up DevTools state changes to open/close native Avalonia DevTools Window
        global::Microsoft.UI.Xaml.Controls.DevToolsService.StateChanged += OnDevToolsStateChanged;
    }

    private void OnAnimationTick(TimeSpan time)
    {
        double currentTime = _stopwatch.Elapsed.TotalSeconds;
        double delta = currentTime - _lastTickTime;
        _lastTickTime = currentTime;

        // Cap delta to prevent huge jumps
        if (delta > 0.1) delta = 0.016;

        // Calculate real-time FPS
        _frameCount++;
        _fpsTimer += delta;
        if (_fpsTimer >= 1.0)
        {
            _currentFps = _frameCount / _fpsTimer;
            _frameCount = 0;
            _fpsTimer = 0;

            StatsLabel.Text = $"FPS: {_currentFps:F1} | Frame: {(delta * 1000.0):F1} ms";
        }

        // Tick ProGPU visual animations
        if (_isPlaying)
        {
            MainWindowController.OnWindowRender(delta);
        }

        // Force Avalonia control repaint
        ProGpuHost.InvalidateVisual();

        // Queue next frame
        RequestAnimationFrame(OnAnimationTick);
    }

    private void OnSidebarSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SidebarList.SelectedItem is global::Avalonia.Controls.ListBoxItem item && item.Tag is string pageKey)
        {
            // Instantiates individual high-performance pages natively on demand
            ProGpuHost.WinuiRoot = pageKey switch
            {
                "Charting" => ChartShowcasePage.Create(),
                "Dxf" => DxfViewerPage.Create(),
                "Drawing" => SamplePagePresenter.CreateDrawingContextShowcaseView(),
                "MotionMark" => SamplePagePresenter.CreateMotionMarkShowcaseView(),
                "Markdown" => MarkdownPage.Create(),
                "Glyphs" => FontGlyphBrowserPage.Create(),
                "DataGrid" => DataVirtualizationPage.Create(),
                "Designer" => VisualDesignerPage.Create(),
                _ => ChartShowcasePage.Create()
            };

            // Invalidate the host element layout to force sizing negotiation (Measure/Arrange)
            ProGpuHost.InvalidateMeasure();
            ProGpuHost.InvalidateVisual();
        }
    }

    private void OnThemeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo == null || ProGpuHost == null) return;

        bool isDark = ThemeCombo.SelectedIndex == 0;

        // 1. Synchronize the Host (Avalonia) theme
        global::Avalonia.Application.Current!.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        // 2. Synchronize the Embedded Control (ProGPU) theme
        ThemeManager.CurrentTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
        
        // 3. Trigger immediate repaint of cached and themed brushes
        ProGpuHost.WinuiRoot?.NotifyThemeChanged();
        ProGpuHost.InvalidateVisual();
    }

    private void OnPlayPauseClicked(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        _isPlaying = !_isPlaying;
        PlayPauseBtn.Content = _isPlaying ? "⏸ Pause" : "▶ Play";
    }

    private void OnDevToolsStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (global::Microsoft.UI.Xaml.Controls.DevToolsService.IsDevToolsActive)
            {
                if (_devToolsWindow != null) return;

                var hostControl = new ProGpuHostControl
                {
                    WinuiRoot = AppState._devToolsPanel
                };

                _devToolsWindow = new global::Avalonia.Controls.Window
                {
                    Title = "ProGPU Developer Tools (Avalonia)",
                    Width = 850,
                    Height = 600,
                    Content = hostControl,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                _devToolsWindow.Closed += (s, ev) =>
                {
                    _devToolsWindow = null;
                    global::Microsoft.UI.Xaml.Controls.DevToolsService.IsDevToolsActive = false;
                };

                _devToolsWindow.Show(this);
            }
            else
            {
                if (_devToolsWindow != null)
                {
                    _devToolsWindow.Close();
                    _devToolsWindow = null;
                }
            }
        });
    }
}