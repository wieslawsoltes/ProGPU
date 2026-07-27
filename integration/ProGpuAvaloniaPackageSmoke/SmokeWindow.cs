using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.ProGpu;
using Avalonia.SilkNet;
using Avalonia.Threading;
using ProGpuCompositorMetrics = ProGPU.Scene.CompositorMetrics;

namespace ProGpuAvaloniaPackageSmoke;

internal sealed class SmokeWindow : Window
{
    private readonly SmokePulseControl _pulse = new();
    private readonly bool _standalone;
    private readonly int _targetFrames;
    private readonly string? _outputPath;
    private readonly bool _requireRetainedCompositor;
    private int _frameCount;
    private bool _completionScheduled;
    private bool _pulsePhase;
    private ProGpuCompositorMetrics _lastMetrics;

    public SmokeWindow(bool standalone = true)
    {
        _standalone = standalone;
        _requireRetainedCompositor =
            ReadBoolean(
                "PROGPU_PACKAGE_SMOKE_REQUIRE_RETAINED");
        Title =
            "ProGPU Avalonia package smoke — " +
            "Windowing: Silk.NET/GLFW | " +
            "Rendering: WebGPU/Dawn | " +
            "Compositor: " +
            (_requireRetainedCompositor
                ? "ProGPU retained"
                : "Avalonia retained with ProGPU renderer") +
            " | " +
            "Text: ProGPU OpenType";
        Width = 640;
        Height = 360;
        Content = new Grid
        {
            Children =
            {
                new TextBlock
                {
                    Text =
                        "ProGPU Avalonia package smoke",
                    FontSize = 30,
                    HorizontalAlignment =
                        Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment =
                        Avalonia.Layout.VerticalAlignment.Center
                },
                _pulse
            }
        };

        _targetFrames = ReadPositiveInt(
            "PROGPU_PACKAGE_SMOKE_FRAMES");
        _outputPath = ReadOptionalPath(
            "PROGPU_PACKAGE_SMOKE_OUTPUT");
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        SilkNetPlatform.FramePreparing += OnFramePreparing;
        if (_standalone)
        {
            ProGpuRenderingDiagnostics.FrameRendered +=
                OnFrameRendered;
        }
    }

    private void OnFramePreparing()
    {
        _pulsePhase = !_pulsePhase;
        _pulse.SetPhase(_pulsePhase);
    }

    private void OnFrameRendered(ProGpuCompositorMetrics metrics)
    {
        _lastMetrics = metrics;
        _frameCount++;
        if (_targetFrames <= 0 ||
            _frameCount < _targetFrames ||
            _completionScheduled)
        {
            return;
        }

        _completionScheduled = true;
        Dispatcher.UIThread.Post(
            CompleteSmoke,
            DispatcherPriority.Background);
    }

    private void CompleteSmoke()
    {
        bool passed =
            _lastMetrics.DrawCallsCount > 0 &&
            (!_requireRetainedCompositor ||
             (_lastMetrics.RetainedCompositionSceneCount > 0 &&
              _lastMetrics
                  .RetainedCompositionServerBackendRenderCount > 0 &&
              _lastMetrics.RetainedCompositionFallbackNodeCount == 0)) &&
            !string.IsNullOrWhiteSpace(
                _lastMetrics.PresentationPath);

        if (!string.IsNullOrWhiteSpace(_outputPath))
        {
            string? directory =
                Path.GetDirectoryName(_outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using FileStream output = File.Create(_outputPath);
            using var writer = new Utf8JsonWriter(
                output,
                new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteBoolean("Passed", passed);
            writer.WriteBoolean(
                "RetainedCompositorRequired",
                _requireRetainedCompositor);
            writer.WriteNumber("Frames", _frameCount);
            writer.WriteString(
                "PresentationPath",
                _lastMetrics.PresentationPath ??
                "Unavailable");
            writer.WriteNumber(
                "DrawCalls",
                _lastMetrics.DrawCallsCount);
            writer.WriteNumber(
                "RetainedCompositionScenes",
                _lastMetrics.RetainedCompositionSceneCount);
            writer.WriteNumber(
                "RetainedCompositionServerBackendRenders",
                _lastMetrics
                    .RetainedCompositionServerBackendRenderCount);
            writer.WriteNumber(
                "RetainedCompositionFallbackNodes",
                _lastMetrics
                    .RetainedCompositionFallbackNodeCount);
            writer.WriteEndObject();
            writer.Flush();
        }

        Environment.Exit(passed ? 0 : 5);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        SilkNetPlatform.FramePreparing -= OnFramePreparing;
        if (_standalone)
        {
            ProGpuRenderingDiagnostics.FrameRendered -=
                OnFrameRendered;
        }
    }

    private static int ReadPositiveInt(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(
                   value,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out int result) &&
               result > 0
            ? result
            : 0;
    }

    private static string? ReadOptionalPath(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : Path.GetFullPath(value);
    }

    private static bool ReadBoolean(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return value is "1" ||
               string.Equals(
                   value,
                   "true",
                   StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class SmokePulseControl : Control
{
    private readonly IBrush _firstBrush =
        new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
    private readonly IBrush _secondBrush =
        new SolidColorBrush(
            Color.FromArgb(1, 255, 255, 255));
    private bool _phase;

    public SmokePulseControl()
    {
        Width = 1;
        Height = 1;
        IsHitTestVisible = false;
    }

    public void SetPhase(bool phase)
    {
        _phase = phase;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(
            _phase ? _firstBrush : _secondBrush,
            new Rect(Bounds.Size));
    }
}
