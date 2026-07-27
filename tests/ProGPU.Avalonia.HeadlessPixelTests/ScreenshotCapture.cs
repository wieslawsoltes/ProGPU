using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless;

namespace ProGPU.Avalonia.HeadlessPixelTests;

internal readonly record struct ScreenshotArtifact(
    string Path,
    int Width,
    int Height,
    long ByteCount);

internal static class ScreenshotCapture
{
    public static ScreenshotArtifact SavePng(TopLevel topLevel, string fileName)
    {
        using var frame = topLevel.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No rendered frame was available.");

        var outputRoot = Environment.GetEnvironmentVariable("AVALONIA_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            outputRoot = Path.Combine(AppContext.BaseDirectory, "headless-screenshots");
        }

        Directory.CreateDirectory(outputRoot);
        var path = Path.GetFullPath(Path.Combine(outputRoot, fileName + ".png"));
        frame.Save(path);

        return new ScreenshotArtifact(
            path,
            frame.PixelSize.Width,
            frame.PixelSize.Height,
            new FileInfo(path).Length);
    }
}
