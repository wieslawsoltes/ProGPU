using System;
using System.IO;
using Avalonia.Controls;

namespace ProGPU.Avalonia.HeadlessPixelTests;

public sealed class RenderingPixelContractTests
{
    [AvaloniaFact]
    public void StableLayoutAndTextMutationProduceValidDistinctFrames()
    {
        var view = new PixelContractView();
        var window = new Window
        {
            Width = 640,
            Height = 360,
            CanResize = false,
            Content = view
        };

        try
        {
            window.Show();
            var first = ScreenshotCapture.SavePng(window, "avalonia-reference-frame-1");

            view.AdvanceFrame();
            var second = ScreenshotCapture.SavePng(window, "avalonia-reference-frame-2");

            Assert.Equal(640, first.Width);
            Assert.Equal(360, first.Height);
            Assert.Equal(first.Width, second.Width);
            Assert.Equal(first.Height, second.Height);
            Assert.True(first.ByteCount > 1_000);
            Assert.True(second.ByteCount > 1_000);
            Assert.True(File.Exists(first.Path));
            Assert.True(File.Exists(second.Path));
            Assert.False(File.ReadAllBytes(first.Path).AsSpan().SequenceEqual(File.ReadAllBytes(second.Path)));
        }
        finally
        {
            window.Close();
        }
    }
}
