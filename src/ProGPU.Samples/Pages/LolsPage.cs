using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;

namespace ProGPU.Samples;

public static class LolsPage
{
    private const int VSyncPendingElements = 100;
    private const int UncappedPendingElements = 200;

    private static int _count = 0;
    private static int _max = 500;
    private static bool _isRunning = false;
    private static readonly System.Diagnostics.Stopwatch _stopwatch = new();
    private static readonly System.Timers.Timer _timer = new(500);
    private static readonly System.Timers.Timer _browserPumpTimer = new(16);

    private static RichTextBlock? _scoreLabel;
    private static RichTextBlock? _progressLabel;
    private static Microsoft.UI.Xaml.Controls.Canvas? _canvas;
    private static Button? _startStopBtn;
    private static Run? _startStopRun;
    private static int _pendingElementCount;
    private static int _drainScheduled;

    internal static int ActiveElementCount => _canvas?.Children.Count ?? 0;
    internal static int MaximumElementCount => _max;
    internal static int TotalRenderedCount => Volatile.Read(ref _count);
    internal static bool IsReady => _canvas != null;

    public static FrameworkElement Create()
    {
        // Setup timer once
        _timer.Elapsed -= OnTimer;
        _timer.Elapsed += OnTimer;
        _browserPumpTimer.AutoReset = true;
        _browserPumpTimer.Elapsed -= OnBrowserPumpTimer;
        _browserPumpTimer.Elapsed += OnBrowserPumpTimer;

        var mainGrid = new Microsoft.UI.Xaml.Controls.Grid { Margin = new Thickness(12) };
        mainGrid.RowDefinitions.Add(new GridLength(50, GridUnitType.Absolute));   // Header description
        mainGrid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));       // Main Workspace
        mainGrid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));

        // 1. Description Header
        var headerText = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 10) };
        headerText.Inlines.Add(new Run("This page implements a native text layout and rendering performance benchmark based on the LOL/s suite. Renders hundreds of rotating, color-changing text controls on a canvas with a thread-pool dispatcher using standard poolable Border-wrapped RichTextBlocks."));
        mainGrid.AddChild(headerText);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(headerText, 0);

        // 2. Main Content Grid
        var contentSplit = new ResponsiveSplitView { OpenPaneLength = 300f };

        // --- Column 0: Settings Panel ---
        var settingsCard = new Border
        {
            CornerRadius = 8f,
            BorderThickness = new Thickness(1f),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            Background = new ThemeResourceBrush("ControlBackground"),
            Padding = new Thickness(14f),
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var settingsStack = new StackPanel { Orientation = Orientation.Vertical };
        settingsCard.Child = settingsStack;

        // Title text in settings
        var settingsTitle = new RichTextBlock { Font = AppState._font, FontSize = 15f, Margin = new Thickness(0, 0, 0, 12) };
        settingsTitle.Inlines.Add(new Bold(new Run("Benchmark Controls")));
        settingsStack.AddChild(settingsTitle);

        // Scoreboard Box
        var scoreBorder = new Border
        {
            Background = new ThemeResourceBrush("SelectionHighlight"),
            BorderBrush = new ThemeResourceBrush("SystemAccentColor"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 6f,
            Padding = new Thickness(12f),
            Margin = new Thickness(0, 0, 0, 12)
        };

        _scoreLabel = new RichTextBlock { Font = AppState._font, FontSize = 22f, TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center };
        _scoreLabel.Inlines.Add(new Bold(new Run("LOL/s: 0.00")) { Foreground = new ThemeResourceBrush("SystemAccentColor") });
        scoreBorder.Child = _scoreLabel;
        settingsStack.AddChild(scoreBorder);

        // Progress Text
        _progressLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 16) };
        _progressLabel.Inlines.Add(new Run("Total Rendered: "));
        _progressLabel.Inlines.Add(new Bold(new Run("0")));
        settingsStack.AddChild(_progressLabel);

        // Max Elements Slider
        var maxLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
        maxLabel.Inlines.Add(new Bold(new Run($"Max Elements on Screen: {_max}")));
        settingsStack.AddChild(maxLabel);

        var maxSlider = new Slider { Minimum = 100f, Maximum = 2000f, Value = 500f, Margin = new Thickness(0, 0, 0, 16) };
        maxSlider.ValueChanged += (s, e) =>
        {
            int val = (int)(Math.Round(maxSlider.Value / 50f) * 50f);
            if (val < 100) val = 100;
            if (Math.Abs(maxSlider.Value - val) > 0.01f)
            {
                maxSlider.Value = val;
                return;
            }
            _max = val;
            maxLabel.Inlines.Clear();
            maxLabel.Inlines.Add(new Bold(new Run($"Max Elements on Screen: {_max}")));
            maxLabel.Invalidate();
        };
        settingsStack.AddChild(maxSlider);

        // Start / Stop Button
        _startStopBtn = new Button
        {
            Height = 36f,
            CornerRadius = 6f,
            Margin = new Thickness(0, 0, 0, 8),
            Background = new ThemeResourceBrush("SystemAccentColor")
        };
        _startStopRun = new Run("Start Benchmark") { Foreground = new SolidColorBrush(0xFFFFFFFF) };
        var startStopText = new RichTextBlock { Font = AppState._font, FontSize = 13f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        startStopText.Inlines.Add(new Bold(_startStopRun));
        _startStopBtn.Content = startStopText;

        _startStopBtn.Click += (s, e) =>
        {
            if (_isRunning)
            {
                Stop();
            }
            else
            {
                Start();
            }
        };
        settingsStack.AddChild(_startStopBtn);

        // Reset Button
        var resetBtn = new Button
        {
            Height = 36f,
            CornerRadius = 6f,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var resetText = new RichTextBlock { Font = AppState._font, FontSize = 13f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        resetText.Inlines.Add(new Bold(new Run("Reset Canvas")));
        resetBtn.Content = resetText;

        resetBtn.Click += (s, e) =>
        {
            ResetAndStop();
        };
        settingsStack.AddChild(resetBtn);

        contentSplit.PaneContent = settingsCard;

        // --- Column 1: Visual Canvas Card ---
        var canvasCard = new Border
        {
            CornerRadius = 8f,
            BorderThickness = new Thickness(1f),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            Background = new ThemeResourceBrush("ControlBackground"),
            Padding = new Thickness(8f),
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _canvas = new Microsoft.UI.Xaml.Controls.Canvas
        {
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        canvasCard.Child = _canvas;

        contentSplit.MainContent = canvasCard;

        mainGrid.AddChild(contentSplit);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(contentSplit, 1);

        return mainGrid;
    }

    public static void Start()
    {
        if (_isRunning) return;
        Interlocked.Exchange(ref _pendingElementCount, 0);
        _isRunning = true;
        _stopwatch.Restart();
        _count = 0;
        _timer.Start();

        if (_startStopRun != null) _startStopRun.Text = "Stop Benchmark";
        if (_startStopBtn != null) _startStopBtn.Invalidate();

        if (OperatingSystem.IsBrowser())
        {
            _browserPumpTimer.Start();
        }
        else
        {
            _ = Task.Factory.StartNew(RunBenchmarkLoop, TaskCreationOptions.LongRunning);
        }
    }

    public static void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        Interlocked.Exchange(ref _pendingElementCount, 0);
        _timer.Stop();
        _browserPumpTimer.Stop();
        _stopwatch.Stop();

        if (_startStopRun != null) _startStopRun.Text = "Start Benchmark";
        if (_startStopBtn != null) _startStopBtn.Invalidate();
    }

    public static void ResetAndStop()
    {
        Stop();
        UIThread.Post(() =>
        {
            if (_canvas != null)
            {
                foreach (var child in _canvas.Children)
                {
                    if (child is Border border)
                    {
                        TextDisplayFactory.Return(border);
                    }
                }
                _canvas.ClearChildren();
                _canvas.Invalidate();
            }
            _count = 0;
            if (_scoreLabel != null)
            {
                _scoreLabel.Inlines.Clear();
                _scoreLabel.Inlines.Add(new Bold(new Run("LOL/s: 0.00") { Foreground = new ThemeResourceBrush("SystemAccentColor") }));
                _scoreLabel.Invalidate();
            }
            if (_progressLabel != null)
            {
                _progressLabel.Inlines.Clear();
                _progressLabel.Inlines.Add(new Run("Total Rendered: "));
                _progressLabel.Inlines.Add(new Bold(new Run("0")));
                _progressLabel.Invalidate();
            }
        });
    }

    private static void OnTimer(object? sender, System.Timers.ElapsedEventArgs e)
    {
        double elapsed = _stopwatch.Elapsed.TotalSeconds;
        if (elapsed <= 0) elapsed = 0.001;
        double avg = _count / elapsed;

        UIThread.Post(() =>
        {
            if (_scoreLabel != null)
            {
                _scoreLabel.Inlines.Clear();
                _scoreLabel.Inlines.Add(new Bold(new Run($"LOL/s: {avg:0.00}") { Foreground = new ThemeResourceBrush("SystemAccentColor") }));
                _scoreLabel.Invalidate();
            }

            if (_progressLabel != null)
            {
                _progressLabel.Inlines.Clear();
                _progressLabel.Inlines.Add(new Run("Total Rendered: "));
                _progressLabel.Inlines.Add(new Bold(new Run($"{_count:N0}")));
                _progressLabel.Invalidate();
            }
        });
    }

    private static void RunBenchmarkLoop()
    {
        while (_isRunning)
        {
            int maxPendingElements = AppState._wgpuContext?.VSync == true
                ? VSyncPendingElements
                : UncappedPendingElements;
            int pending = Volatile.Read(ref _pendingElementCount);
            if (pending >= maxPendingElements)
            {
                Thread.Sleep(1);
                continue;
            }

            if (Interlocked.CompareExchange(ref _pendingElementCount, pending + 1, pending) != pending)
            {
                continue;
            }

            SchedulePendingDrain();
        }
    }

    private static void OnBrowserPumpTimer(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!_isRunning)
        {
            return;
        }

        int maxPendingElements = AppState._wgpuContext?.VSync == true
            ? VSyncPendingElements
            : UncappedPendingElements;

        while (true)
        {
            int pending = Volatile.Read(ref _pendingElementCount);
            if (pending >= maxPendingElements)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref _pendingElementCount,
                    maxPendingElements,
                    pending) == pending)
            {
                SchedulePendingDrain();
                return;
            }
        }
    }

    private static void SchedulePendingDrain()
    {
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) == 0)
        {
            UIThread.Post(ProcessPendingElements);
        }
    }

    private static void ProcessPendingElements()
    {
        int count = Interlocked.Exchange(ref _pendingElementCount, 0);
        try
        {
            if (!_isRunning || _canvas == null)
            {
                return;
            }

            float width = _canvas.Size.X > 0f ? _canvas.Size.X : 800f;
            float height = _canvas.Size.Y > 0f ? _canvas.Size.Y : 600f;
            var random = Random.Shared;

            for (int i = 0; i < count; i++)
            {
                int red = random.Next(256);
                int green = random.Next(256);
                int blue = random.Next(256);
                var foreground = new SolidColorBrush(new Vector4(
                    red / 255f,
                    green / 255f,
                    blue / 255f,
                    1f));
                float rotation = (float)(random.NextDouble() * Math.PI * 2d);

                var textControl = TextDisplayFactory.Rent();
                TextDisplayFactory.SetText(textControl, "lol?");
                TextDisplayFactory.SetForeground(textControl, foreground);

                textControl.Width = 80f;
                textControl.Height = 40f;
                textControl.CenterPoint = new Vector3(40f, 20f, 0f);
                textControl.Rotation = rotation;

                float left = (float)(random.NextDouble() * (width - 80f));
                float top = (float)(random.NextDouble() * (height - 40f));
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(textControl, left);
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(textControl, top);

                if (_canvas.Children.Count >= _max)
                {
                    var oldest = _canvas.Children[0] as Border;
                    if (oldest != null)
                    {
                        _canvas.RemoveChild(oldest);
                        TextDisplayFactory.Return(oldest);
                    }
                }

                _canvas.AddChild(textControl);
                _count++;
            }
        }
        finally
        {
            Volatile.Write(ref _drainScheduled, 0);
            if (_isRunning && Volatile.Read(ref _pendingElementCount) > 0)
            {
                SchedulePendingDrain();
            }
        }
    }
}
