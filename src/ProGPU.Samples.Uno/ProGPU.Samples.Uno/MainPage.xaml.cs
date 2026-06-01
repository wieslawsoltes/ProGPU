extern alias ProGpu;

using System;
using System.IO;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProGpu::ProGPU.Samples;

namespace ProGPU.Samples.Uno;

public sealed partial class MainPage : Page
{
    private bool _isPlaying = true;
    private readonly Stopwatch _stopwatch = new();
    private double _lastTickTime = 0;
    private int _frameCount = 0;
    private double _fpsTimer = 0;
    private double _currentFps = 0;

    public MainPage()
    {
        this.InitializeComponent();

        ProGpuHost.Loaded += OnHostLoaded;
    }

    private void OnHostLoaded(object sender, RoutedEventArgs e)
    {
        // 1. Initialize ProGPU text rendering default system font
        string fontPath = "/System/Library/Fonts/Supplemental/Arial.ttf";
        if (!File.Exists(fontPath)) fontPath = "Arial.ttf";
        if (File.Exists(fontPath))
        {
            ProGpu::Microsoft.UI.Xaml.Controls.PopupService.DefaultFont = new ProGPU.Text.TtfFont(fontPath);
        }

        if (ProGpuHost.WgpuContext != null && ProGpuHost.Compositor != null)
        {
            // 2. Start the embedded offscreen context and compositor
            MainWindowController.StartEmbedded(ProGpuHost.WgpuContext, ProGpuHost.Compositor);

            // 3. Setup event listeners for the host controls
            SidebarList.SelectionChanged += OnSidebarSelectionChanged;
            ThemeCombo.SelectionChanged += OnThemeSelectionChanged;
            PlayPauseBtn.Click += OnPlayPauseClicked;

            // Set default selections
            SidebarList.SelectedIndex = 0;
            ThemeCombo.SelectedIndex = 0;

            // 4. Hook up to CompositionTarget.Rendering to drive animations (cogs, layouts) and telemetry stats
            _stopwatch.Start();
            CompositionTarget.Rendering += OnCompositionTargetRendering;
        }
    }

    private void OnCompositionTargetRendering(object? sender, object e)
    {
        double currentTime = _stopwatch.Elapsed.TotalSeconds;
        double delta = currentTime - _lastTickTime;
        _lastTickTime = currentTime;

        // Cap delta
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

        // Repaint ProGPU host control
        ProGpuHost.QueueInvalidate();
    }

    private void OnSidebarSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SidebarList.SelectedItem is ListViewItem item && item.Tag is string pageKey)
        {
            // Instantiates individual high-performance pages natively on demand
            ProGpuHost.WinuiRoot = pageKey switch
            {
                "Charting" => ProGpu::ProGPU.Samples.ChartShowcasePage.Create(),
                "Dxf" => ProGpu::ProGPU.Samples.DxfViewerPage.Create(),
                "Drawing" => ProGpu::ProGPU.Samples.SamplePagePresenter.CreateDrawingContextShowcaseView(),
                "MotionMark" => ProGpu::ProGPU.Samples.SamplePagePresenter.CreateMotionMarkShowcaseView(),
                "Markdown" => ProGpu::ProGPU.Samples.MarkdownPage.Create(),
                "Glyphs" => ProGpu::ProGPU.Samples.FontGlyphBrowserPage.Create(),
                "DataGrid" => ProGpu::ProGPU.Samples.DataVirtualizationPage.Create(),
                "Designer" => ProGpu::ProGPU.Samples.VisualDesignerPage.Create(),
                _ => ProGpu::ProGPU.Samples.ChartShowcasePage.Create()
            };

            // Invalidate the host element layout to force sizing negotiation (Measure/Arrange)
            ProGpuHost.QueueInvalidate();
        }
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo == null || ProGpuHost == null) return;

        bool isDark = ThemeCombo.SelectedIndex == 0;

        // 1. Synchronize the Host (Uno Platform) page theme
        this.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;

        // 2. Synchronize the Embedded Control (ProGPU) theme
        ProGpu::Microsoft.UI.Xaml.ThemeManager.CurrentTheme = isDark ? ProGpu::Microsoft.UI.Xaml.ElementTheme.Dark : ProGpu::Microsoft.UI.Xaml.ElementTheme.Light;

        // 3. Trigger immediate repaint of cached and themed brushes
        ProGpuHost.WinuiRoot?.NotifyThemeChanged();
        ProGpuHost.QueueInvalidate();
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        _isPlaying = !_isPlaying;
        PlayPauseBtn.Content = _isPlaying ? "⏸ Pause" : "▶ Play";
    }
}
