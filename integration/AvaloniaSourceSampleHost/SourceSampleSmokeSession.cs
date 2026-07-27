using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.ProGpu;
using Avalonia.Threading;
using ProGPU.Scene;

namespace ProGpuAvaloniaSamples;

internal sealed class SourceSampleSmokeSession : IDisposable
{
    private readonly int _targetFrames;
    private readonly string? _outputPath;
    private int _frameCount;
    private bool _completionScheduled;
    private CompositorMetrics _lastMetrics;

    private SourceSampleSmokeSession(
        int targetFrames,
        string? outputPath)
    {
        _targetFrames = targetFrames;
        _outputPath = outputPath;
    }

    internal static SourceSampleSmokeSession? TryCreate(
        string[] args)
    {
        int optionIndex = Array.IndexOf(args, "--smoke-frames");
        if (optionIndex < 0)
            return null;

        if (optionIndex + 1 >= args.Length ||
            !int.TryParse(
                args[optionIndex + 1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int targetFrames) ||
            targetFrames <= 0)
        {
            throw new ArgumentException(
                "--smoke-frames requires a positive integer.");
        }

        int outputIndex = Array.IndexOf(args, "--smoke-output");
        string? outputPath =
            outputIndex >= 0 && outputIndex + 1 < args.Length
                ? Path.GetFullPath(args[outputIndex + 1])
                : null;
        return new SourceSampleSmokeSession(
            targetFrames,
            outputPath);
    }

    internal void Start()
    {
        ProGpuRenderingDiagnostics.FrameRendered += OnFrameRendered;
        Dispatcher.UIThread.Post(
            InvalidateMainWindow,
            DispatcherPriority.Background);
    }

    public void Dispose()
    {
        ProGpuRenderingDiagnostics.FrameRendered -= OnFrameRendered;
    }

    private static void InvalidateMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is
                IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow?.InvalidateVisual();
        }
    }

    private void OnFrameRendered(CompositorMetrics metrics)
    {
        _lastMetrics = metrics;
        _frameCount++;
        if (_frameCount < _targetFrames)
        {
            Dispatcher.UIThread.Post(
                InvalidateMainWindow,
                DispatcherPriority.Background);
            return;
        }
        if (_completionScheduled)
            return;

        _completionScheduled = true;
        Dispatcher.UIThread.Post(
            Complete,
            DispatcherPriority.Background);
    }

    private void Complete()
    {
        bool passed =
            _lastMetrics.DrawCallsCount > 0 &&
            !string.IsNullOrWhiteSpace(
                _lastMetrics.PresentationPath);

        if (_outputPath is not null)
        {
            string? directory = Path.GetDirectoryName(_outputPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using FileStream output = File.Create(_outputPath);
            using var writer = new Utf8JsonWriter(
                output,
                new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteBoolean("Passed", passed);
            writer.WriteNumber("Frames", _frameCount);
            writer.WriteString(
                "PresentationPath",
                _lastMetrics.PresentationPath ?? "Unavailable");
            writer.WriteNumber(
                "DrawCalls",
                _lastMetrics.DrawCallsCount);
            writer.WriteNumber(
                "RetainedCompositionScenes",
                _lastMetrics.RetainedCompositionSceneCount);
            writer.WriteNumber(
                "RetainedCompositionFallbackNodes",
                _lastMetrics.RetainedCompositionFallbackNodeCount);
            writer.WriteEndObject();
            writer.Flush();
        }

        Environment.Exit(passed ? 0 : 5);
    }
}
