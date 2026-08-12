using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using ProGPU.Scene;
using ProGPU.Samples;
using ProGPU.Vector;
using Silk.NET.WebGPU;

namespace ProGPU.Samples.Desktop;

internal static class NativeRendererSamplePage
{
    public static FrameworkElement Create()
    {
        if (AppState._wgpuContext is not { } context)
        {
            return CreateMessage(
                "Native C++ renderer unavailable",
                "The gallery WebGPU context has not been initialized.");
        }

        if (context.BackendKind != WgpuBackendKind.SilkNative)
        {
            return CreateMessage(
                "Native C++ renderer requires the exact wgpu-native ABI",
                "Restart ProGPU.Samples.Desktop with --native-renderer. " +
                "The ordinary desktop launch uses Dawn for media interop; " +
                "Dawn handles are intentionally never reinterpreted as " +
                "wgpu-native handles.");
        }

        try
        {
            var session = new NativeRendererSampleSession(context);
            FrameworkElement page = session.CreatePage();
            page.Unloaded += (_, _) => session.Dispose();
            return page;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            NativeRendererException)
        {
            return CreateMessage(
                "Native C++ renderer could not be loaded",
                exception.Message +
                " Run eng/build-progpu-native.sh once, then restart with " +
                "--native-renderer.");
        }
    }

    private static FrameworkElement CreateMessage(
        string title,
        string detail)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(20)
        };
        content.AddChild(CreateText(title, 22f, bold: true));
        content.AddChild(CreateText(detail, 13f));
        return content;
    }

    private static RichTextBlock CreateText(
        string text,
        float fontSize,
        bool bold = false)
    {
        var block = new RichTextBlock
        {
            Font = AppState._font,
            FontSize = fontSize,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Inline inline = new Run(text);
        block.Inlines.Add(bold ? new Bold(inline) : inline);
        return block;
    }

    private sealed class NativeRendererSampleSession : IDisposable
    {
        private const uint TargetWidth = 960;
        private const uint TargetHeight = 540;
        private const int MaximumRectangles = 4096;

        private readonly NativeCompositor _compositor;
        private readonly GpuTexture _target;
        private readonly NativeSolidRectangle[] _rectangles =
            new NativeSolidRectangle[MaximumRectangles];
        private readonly NativeRendererInfo _info;
        private NativeTexturePreview? _preview;
        private Run? _countRun;
        private Run? _metricsRun;
        private int _rectangleCount = 384;
        private int _palette;
        private int _disposeState;

        public NativeRendererSampleSession(WgpuContext context)
        {
            _info = NativeCompositor.GetInfo();
            _target = new GpuTexture(
                context,
                TargetWidth,
                TargetHeight,
                TextureFormat.Rgba8Unorm,
                TextureUsage.RenderAttachment |
                TextureUsage.TextureBinding |
                TextureUsage.CopySrc,
                "Native C++ gallery render target",
                alphaMode: GpuTextureAlphaMode.Premultiplied);
            _compositor = new NativeCompositor(
                context,
                TextureFormat.Rgba8Unorm);
            RenderFrame();
        }

        public FrameworkElement CreatePage()
        {
            var root = new Grid
            {
                Margin = new Thickness(14)
            };
            root.RowDefinitions.Add(GridLength.Auto);
            root.RowDefinitions.Add(GridLength.Auto);
            root.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));

            var heading = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 0, 0, 10)
            };
            heading.AddChild(CreateText(
                "Pure C++ WebGPU renderer",
                22f,
                bold: true));
            heading.AddChild(CreateText(
                $"{_info.Name}. One stable C ABI call records one GPU " +
                "submission using the same embedded Vector.wgsl module as " +
                "the managed renderer.",
                12f));
            root.AddChild(heading);
            Grid.SetRow(heading, 0);

            var controls = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalSpacing = 10f,
                VerticalSpacing = 8f,
                Margin = new Thickness(0, 0, 0, 10)
            };

            _countRun = new Run();
            var countLabel = new RichTextBlock
            {
                Font = AppState._font,
                FontSize = 12f,
                Width = 132f,
                Margin = new Thickness(0, 8, 0, 0)
            };
            countLabel.Inlines.Add(_countRun);
            UpdateCountText();
            controls.AddChild(countLabel);

            var countSlider = new Slider
            {
                Minimum = 1,
                Maximum = MaximumRectangles,
                Value = _rectangleCount,
                Width = 280f,
                SmallChange = 1,
                LargeChange = 128
            };
            countSlider.ValueChanged += (_, _) =>
            {
                _rectangleCount = Math.Clamp(
                    (int)Math.Round(countSlider.Value),
                    1,
                    MaximumRectangles);
                UpdateCountText();
            };
            controls.AddChild(countSlider);

            var renderButton = CreateButton("Render native batch", 170f);
            renderButton.Click += (_, _) => RenderFrame();
            controls.AddChild(renderButton);

            var paletteButton = CreateButton("Cycle palette", 132f);
            paletteButton.Click += (_, _) =>
            {
                _palette = (_palette + 1) % 3;
                RenderFrame();
            };
            controls.AddChild(paletteButton);

            _metricsRun = new Run();
            var metrics = new RichTextBlock
            {
                Font = AppState._font,
                FontSize = 11f,
                Margin = new Thickness(0, 7, 0, 0)
            };
            metrics.Inlines.Add(_metricsRun);
            controls.AddChild(metrics);
            root.AddChild(controls);
            Grid.SetRow(controls, 1);

            _preview = new NativeTexturePreview(_target);
            var previewBorder = new Border
            {
                Background = new ThemeResourceBrush("ControlBackground"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = 8f,
                Padding = new Thickness(8),
                Child = _preview
            };
            root.AddChild(previewBorder);
            Grid.SetRow(previewBorder, 2);

            // Publish the constructor's initial frame now that text runs exist.
            RenderFrame();
            return root;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }
            _compositor.Dispose();
            _target.Dispose();
        }

        private void RenderFrame()
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            FillRectangles(_rectangleCount, _palette);
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            long timestamp = Stopwatch.GetTimestamp();
            NativeFrameMetrics metrics = _compositor.Render(
                _target,
                dpiScale: 1f,
                _rectangles.AsSpan(0, _rectangleCount),
                new Vector4(0.015f, 0.02f, 0.035f, 1f));
            double elapsedMilliseconds =
                Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
            long managedBytes =
                GC.GetAllocatedBytesForCurrentThread() - allocationStart;

            if (_metricsRun is not null)
            {
                _metricsRun.Text =
                    $"C ABI + submit {elapsedMilliseconds:F3} ms · " +
                    $"managed alloc {managedBytes} B · " +
                    $"draws {metrics.DrawCallCount} · " +
                    $"vertices {metrics.VertexCount:N0} · " +
                    $"upload {metrics.VertexUploadBytes:N0} B";
            }
            _preview?.Invalidate();
        }

        private void FillRectangles(int count, int palette)
        {
            const float inset = 18f;
            const float gap = 3f;
            float usableWidth = TargetWidth - inset * 2f;
            float usableHeight = TargetHeight - inset * 2f;
            int columns = Math.Max(
                1,
                (int)MathF.Ceiling(MathF.Sqrt(
                    count * usableWidth / usableHeight)));
            int rows = (count + columns - 1) / columns;
            float cellWidth = usableWidth / columns;
            float cellHeight = usableHeight / rows;

            for (int index = 0; index < count; index++)
            {
                int column = index % columns;
                int row = index / columns;
                float phase = (index * 0.61803398875f + palette * 0.23f) % 1f;
                Vector4 color = Palette(phase, palette);
                _rectangles[index] = new NativeSolidRectangle(
                    inset + column * cellWidth + gap * 0.5f,
                    inset + row * cellHeight + gap * 0.5f,
                    Math.Max(1f, cellWidth - gap),
                    Math.Max(1f, cellHeight - gap),
                    color);
            }
        }

        private static Vector4 Palette(float phase, int palette)
        {
            float wave0 = 0.5f + 0.5f * MathF.Sin(phase * MathF.Tau);
            float wave1 = 0.5f + 0.5f * MathF.Sin((phase + 0.333f) * MathF.Tau);
            float wave2 = 0.5f + 0.5f * MathF.Sin((phase + 0.666f) * MathF.Tau);
            return palette switch
            {
                1 => new Vector4(0.25f + 0.7f * wave2, 0.12f + 0.45f * wave0, 0.35f + 0.6f * wave1, 1f),
                2 => new Vector4(0.08f + 0.38f * wave1, 0.38f + 0.58f * wave2, 0.42f + 0.5f * wave0, 1f),
                _ => new Vector4(0.12f + 0.45f * wave0, 0.3f + 0.62f * wave1, 0.45f + 0.5f * wave2, 1f)
            };
        }

        private void UpdateCountText()
        {
            if (_countRun is not null)
            {
                _countRun.Text = $"Rectangles: {_rectangleCount:N0}";
            }
        }

        private static Button CreateButton(string text, float width)
        {
            var label = CreateText(text, 12f);
            label.Margin = new Thickness(0);
            return new Button
            {
                Width = width,
                Height = 36f,
                CornerRadius = 6f,
                Content = label
            };
        }
    }

    private sealed class NativeTexturePreview : FrameworkElement
    {
        private readonly GpuTexture _texture;

        public NativeTexturePreview(GpuTexture texture)
        {
            _texture = texture;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawTexture(
                _texture,
                new Rect(Vector2.Zero, Size));
        }
    }
}
