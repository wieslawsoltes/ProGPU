using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Text;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class StaticDxfRenderTests
{
    [Fact]
    public void AffineHatchPatternBrushRendersInPatternSpace()
    {
        using var window = new HeadlessWindow(64, 32);
        window.Content = new PatternHatchVisual();

        window.Render();

        byte[] pixels = window.ReadPixels();
        foreach (int x in new[] { 16, 48 })
        {
            RgbaPixel line = ReadPixel(pixels, window.Width, x, y: 3);
            RgbaPixel gap = ReadPixel(pixels, window.Width, x, y: 7);
            Assert.True(line.R >= 180, $"Expected transformed red hatch line at x={x}, found {line}.");
            Assert.True(line.B <= 80, $"Expected low blue on transformed hatch line at x={x}, found {line}.");
            Assert.True(gap.R <= 80, $"Expected low red between hatch lines at x={x}, found {gap}.");
            Assert.True(gap.B >= 180, $"Expected blue between hatch lines at x={x}, found {gap}.");
        }
    }

    [Fact]
    public void MultiFamilyDashGapDotPatternRendersAcrossBothHatchPipelines()
    {
        using var window = new HeadlessWindow(64, 32);
        window.Content = new PatternSetHatchVisual();

        window.Render();

        byte[] pixels = window.ReadPixels();
        foreach (int offset in new[] { 0, 32 })
        {
            RgbaPixel dash = ReadPixel(pixels, window.Width, offset + 2, y: 3);
            RgbaPixel gap = ReadPixel(pixels, window.Width, offset + 6, y: 3);
            RgbaPixel shiftedDash = ReadPixel(pixels, window.Width, offset + 4, y: 11);
            RgbaPixel shiftedGap = ReadPixel(pixels, window.Width, offset + 1, y: 11);
            RgbaPixel dot = ReadPixel(pixels, window.Width, offset + 7, y: 7);
            RgbaPixel dotGap = ReadPixel(pixels, window.Width, offset + 7, y: 9);
            Assert.True(dash.R >= 160 && dash.B <= 100, $"Expected dash coverage at offset {offset}, found {dash}.");
            Assert.True(gap.R <= 100 && gap.B >= 160, $"Expected authored gap at offset {offset}, found {gap}.");
            Assert.True(shiftedDash.R >= 160 && shiftedDash.B <= 100, $"Expected tangent-shifted dash at offset {offset}, found {shiftedDash}.");
            Assert.True(shiftedGap.R <= 100 && shiftedGap.B >= 160, $"Expected tangent-shifted gap at offset {offset}, found {shiftedGap}.");
            Assert.True(dot.R >= 120, $"Expected retained zero-length dot at offset {offset}, found {dot}.");
            Assert.True(dotGap.R <= 100 && dotGap.B >= 160, $"Expected dot-family gap at offset {offset}, found {dotGap}.");
        }
    }

    [Fact]
    public void DrawStaticDxfHonorsActiveOpacityMask()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(190, 90);

        using var visibleBuffer = CreateStaticRect(window.Compositor, new Rect(20, 25, 40, 40));
        using var extensionMaskedBuffer = CreateStaticRect(window.Compositor, new Rect(75, 25, 40, 40));
        using var commandMaskedBuffer = CreateStaticRect(window.Compositor, new Rect(130, 25, 40, 40));

        window.Content = new MaskedStaticDxfVisual(visibleBuffer, extensionMaskedBuffer, commandMaskedBuffer);

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var background = ReadPixel(pixels, window.Width, x: 10, y: 10);
            var visible = ReadPixel(pixels, window.Width, x: 40, y: 45);
            var extensionMasked = ReadPixel(pixels, window.Width, x: 95, y: 45);
            var commandMasked = ReadPixel(pixels, window.Width, x: 150, y: 45);

            Assert.True(visible.R >= 220, $"Expected unmasked static DXF draw to render red, found {visible}.");
            Assert.True(visible.G <= 35, $"Expected unmasked static DXF draw to keep green low, found {visible}.");
            Assert.True(visible.B <= 35, $"Expected unmasked static DXF draw to keep blue low, found {visible}.");
            Assert.Equal(255, visible.A);

            AssertColorNear(background, extensionMasked, tolerance: 12);
            AssertColorNear(background, commandMasked, tolerance: 12);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawStaticDxfSkipsCollapsedNestedClip()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);

        using var buffer = CreateStaticRect(window.Compositor, new Rect(0, 0, 32, 32));
        window.Content = new CollapsedNestedClipStaticDxfVisual(buffer);

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var clippedEdge = ReadPixel(pixels, window.Width, x: 16, y: 8);

            Assert.True(clippedEdge.R <= 35, $"Expected collapsed clip edge to keep background red low, found {clippedEdge}.");
            Assert.True(clippedEdge.G <= 35, $"Expected collapsed clip edge to keep background green low, found {clippedEdge}.");
            Assert.True(clippedEdge.B >= 220, $"Expected collapsed clip edge to remain blue background, found {clippedEdge}.");
            Assert.Equal(255, clippedEdge.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawStaticDxfHonorsActiveBlendMode()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(72, 32);

        using var extensionBuffer = CreateStaticRect(window.Compositor, new Rect(0, 0, 32, 32));
        using var commandBuffer = CreateStaticRect(window.Compositor, new Rect(40, 0, 32, 32));

        window.Content = new ClearBlendStaticDxfVisual(extensionBuffer, commandBuffer);

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var extensionCleared = ReadPixel(pixels, window.Width, x: 16, y: 16);
            var commandCleared = ReadPixel(pixels, window.Width, x: 56, y: 16);

            AssertTransparent(extensionCleared);
            AssertTransparent(commandCleared);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawStaticDxfHonorsNestedVisualPlacement()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(96, 64);

        using var buffer = CreateStaticRect(window.Compositor, new Rect(0, 0, 24, 24));
        window.Content = new OffsetStaticDxfHost(new SingleStaticDxfVisual(buffer));

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var outside = ReadPixel(pixels, window.Width, x: 12, y: 12);
            var placed = ReadPixel(pixels, window.Width, x: 56, y: 28);

            Assert.True(outside.R <= 35, $"Expected the unplaced origin to remain background, found {outside}.");
            Assert.True(placed.R >= 220, $"Expected the nested static DXF draw at its visual offset, found {placed}.");
            Assert.True(placed.G <= 35 && placed.B <= 35, $"Expected the placed static DXF draw to remain red, found {placed}.");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void AppendedStaticDxfTranslationIsAppliedExactlyOnce()
    {
        using var window = new HeadlessWindow(72, 40);
        using var buffer = CreateStaticRect(window.Compositor, new Rect(0, 8, 16, 24));
        window.Content = new AppendedStaticDxfVisual(buffer, new Vector2(20f, 0f));

        window.Render();

        var pixels = window.ReadPixels();
        var translated = ReadPixel(pixels, window.Width, x: 28, y: 20);
        var doubleTranslated = ReadPixel(pixels, window.Width, x: 48, y: 20);
        Assert.True(translated.R >= 220, $"Expected the appended DXF at one translation, found {translated}.");
        Assert.True(translated.G <= 35 && translated.B <= 35, $"Expected red appended DXF content, found {translated}.");
        Assert.True(doubleTranslated.R <= 35, $"Expected no double-translated DXF content, found {doubleTranslated}.");
    }

    [Fact]
    public void CompileStaticDxfAppliesAppendedLineAndHatchTransforms()
    {
        var compositor = HeadlessWindow.Shared.Compositor;
        var translation = new Vector2(20f, 30f);
        var pen = new Pen(new SolidColorBrush(Vector4.One), 2f);

        var sourceLine = new DrawingContext();
        sourceLine.DrawLine3D(
            pen,
            new Vector3(0f, 0f, 0f),
            new Vector3(10f, 0f, 0f));
        var appendedLine = new DrawingContext();
        appendedLine.Append(sourceLine, translation);
        using var lineBuffer = compositor.CompileStaticDxf(appendedLine);

        Assert.NotEmpty(lineBuffer.VectorVertices);
        Assert.Equal(20f, lineBuffer.VectorVertices.Min(vertex => vertex.Position.X));
        Assert.Equal(30f, lineBuffer.VectorVertices.Max(vertex => vertex.Position.X));
        Assert.All(lineBuffer.VectorVertices, vertex => Assert.Equal(30f, vertex.Position.Y));

        var sourceHatch = new DrawingContext();
        sourceHatch.DrawHatch(
            new SolidColorBrush(Vector4.One),
            PrimitivePathGeometry.CreateRectangle(0f, 0f, 10f, 12f));
        var appendedHatch = new DrawingContext();
        appendedHatch.Append(sourceHatch, translation);
        using var hatchBuffer = compositor.CompileStaticDxf(appendedHatch);

        Assert.NotEmpty(hatchBuffer.VectorVertices);
        Assert.Equal(20f, hatchBuffer.VectorVertices.Min(vertex => vertex.Position.X));
        Assert.Equal(30f, hatchBuffer.VectorVertices.Max(vertex => vertex.Position.X));
        Assert.Equal(30f, hatchBuffer.VectorVertices.Min(vertex => vertex.Position.Y));
        Assert.Equal(42f, hatchBuffer.VectorVertices.Max(vertex => vertex.Position.Y));
    }

    [Fact]
    public void CompileStaticDxfBakesExplicitTransformForQuadExtensions()
    {
        var context = new DrawingContext();
        context.DrawExtension(
            CompositorBuiltInExtensions.ShaderToy,
            dataParam: new ProGPU.Scene.Extensions.ShaderToyParams
            {
                Rect = new Rect(2f, 4f, 10f, 12f)
            },
            transform: Matrix4x4.CreateTranslation(20f, 30f, 0f));

        using var buffer = HeadlessWindow.Shared.Compositor.CompileStaticDxf(context);

        Assert.NotEmpty(buffer.VectorVertices);
        Assert.Equal(22f, buffer.VectorVertices.Min(vertex => vertex.Position.X));
        Assert.Equal(32f, buffer.VectorVertices.Max(vertex => vertex.Position.X));
        Assert.Equal(34f, buffer.VectorVertices.Min(vertex => vertex.Position.Y));
        Assert.Equal(46f, buffer.VectorVertices.Max(vertex => vertex.Position.Y));
    }

    [Fact]
    public void DrawStaticDxfSplineHonorsActiveBlendMode()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(48, 48);

        using var buffer = CreateStaticSpline(window.Compositor);

        window.Content = new ClearBlendStaticDxfSplineVisual(buffer);

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var cleared = ReadPixel(pixels, window.Width, x: 24, y: 24);

            AssertTransparent(cleared);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void CompileStaticDxfAcisSolidPreservesModelTransform()
    {
        var window = HeadlessWindow.Shared;
        using var contextBuffer = CreateStaticAcis(window.Compositor, y: 14f);
        using var listBuffer = window.Compositor.CompileStaticDxf(CreateAcisCommands(y: 34f));

        var contextRecords = GetStaticAcisRecords(contextBuffer);
        var listRecords = GetStaticAcisRecords(listBuffer);
        if (contextRecords == null || listRecords == null)
        {
            return;
        }

        var contextRecord = Assert.Single(contextRecords);
        var listRecord = Assert.Single(listRecords);

        Assert.Equal(Matrix4x4.CreateTranslation(18f, 14f, 0f), contextRecord.Transform);
        Assert.Equal(Matrix4x4.CreateTranslation(18f, 34f, 0f), listRecord.Transform);
    }

    [Fact]
    public void CompileStaticDxfIncludesGlyphRunCommands()
    {
        var font = TryLoadTestFont();
        if (font == null)
        {
            return;
        }

        var window = HeadlessWindow.Shared;
        var context = CreateGlyphRunContext(font);

        using var listBuffer = window.Compositor.CompileStaticDxf(context);
        AssertStaticGlyphRunCompiled(listBuffer);

        using var contextBuffer = window.Compositor.CompileStaticDxf(context);
        AssertStaticGlyphRunCompiled(contextBuffer);
    }

    [Fact]
    public void CompileStaticDxfStoresOneOutlineForRepeatedGlyphInstances()
    {
        var font = TryLoadTestFont();
        if (font == null)
        {
            return;
        }

        var glyphIndex = font.GetGlyphIndex('A');
        var context = new DrawingContext();
        context.DrawGlyphRun(
            new[] { glyphIndex, glyphIndex, glyphIndex },
            new[] { new Vector2(20f, 55f), new Vector2(40f, 55f), new Vector2(60f, 55f) },
            font,
            24f,
            new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            Vector2.Zero);

        using var buffer = HeadlessWindow.Shared.Compositor.CompileStaticDxf(context);

        Assert.Equal(1u, buffer.RetainedGlyphRecordCount);
        Assert.True(buffer.RetainedGlyphSegmentCount > 0);
        Assert.Equal(3u, buffer.RetainedGlyphInstanceCount);
        Assert.Empty(buffer.TextVertices);
    }

    [Fact]
    public void DisposedStaticDxfCannotRemainInCompiledScene()
    {
        using var window = new HeadlessWindow(64, 64);

        using var buffer = CreateStaticRect(window.Compositor, new Rect(8, 8, 48, 48));
        var projection = new Matrix4x4(
            2f / 64f, 0f, 0f, 0f,
            0f, -2f / 64f, 0f, 0f,
            0f, 0f, 1f, 0f,
            -1f, 1f, 0f, 1f);
        buffer.UpdateViewport(projection, 1f, Vector2.Zero, Vector2.Zero, Vector2.Zero);
        window.Content = new SingleStaticDxfVisual(buffer);

        try
        {
            window.Render();

            // A retained scene may still hold the external buffer after its owner unloads
            // or replaces a DXF document. Disposing that buffer must invalidate the scene
            // before another frame can bind its released native handle.
            buffer.Dispose();
            window.Render();

            Assert.True(buffer.IsDisposed);
            Assert.False(window.Compositor.Metrics.SceneCacheHit);

            // Recompilation strips the disposed external resource, so the now-safe
            // scene can be reused without repeatedly compiling the visual tree.
            window.Render();
            Assert.True(window.Compositor.Metrics.SceneCacheHit);
        }
        finally
        {
            window.Content = null;
        }
    }

    private static DxfStaticBuffer CreateStaticRect(Compositor compositor, Rect rect)
    {
        var context = new DrawingContext();
        context.DrawRectangle(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            null,
            rect);

        return compositor.CompileStaticDxf(context);
    }

    private static DxfStaticBuffer CreateStaticAcis(Compositor compositor, float y)
    {
        return compositor.CompileStaticDxf(CreateAcisContext(y));
    }

    private static DrawingContext CreateAcisContext(float y)
    {
        var context = new DrawingContext();
        var pen = new Pen(new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)), 8f);
        var edges = new[]
        {
            new Line3D(new Vector3(0f, 0f, 0f), new Vector3(24f, 0f, 0f))
        };

        context.DrawAcisSolid(pen, edges, Matrix4x4.CreateTranslation(18f, y, 0f));
        return context;
    }

    private static List<RenderCommand> CreateAcisCommands(float y)
    {
        return
        [
            new RenderCommand
            {
                Type = RenderCommandType.DrawExtension,
                ExtensionId = CompositorBuiltInExtensions.AcisSolid,
                Pen = new Pen(new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)), 8f),
                Edges3D =
                [
                    new Line3D(new Vector3(0f, 0f, 0f), new Vector3(24f, 0f, 0f))
                ],
                Transform = Matrix4x4.CreateTranslation(18f, y, 0f)
            }
        ];
    }

    private static GpuAcisRecord[]? GetStaticAcisRecords(DxfStaticBuffer buffer)
    {
        var state = buffer.GetExtensionState(CompositorBuiltInExtensions.AcisSolid);
        Assert.NotNull(state);

        return state
            .GetType()
            .GetProperty("RecordsSnapshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(state) as GpuAcisRecord[];
    }

    private static DxfStaticBuffer CreateStaticSpline(Compositor compositor)
    {
        var context = new DrawingContext();
        var pen = new Pen(new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)), 12f);
        var controlPoints = new[]
        {
            new Vector2(6f, 24f),
            new Vector2(24f, 24f),
            new Vector2(42f, 24f)
        };
        var knots = new double[] { 0, 0, 0, 1, 1, 1 };

        context.DrawSpline(pen, controlPoints, knots, degree: 2);

        return compositor.CompileStaticDxf(context);
    }

    private static DrawingContext CreateGlyphRunContext(TtfFont font)
    {
        var glyphIndex = font.GetGlyphIndex('A');
        var context = new DrawingContext();
        context.DrawGlyphRun(
            new[] { glyphIndex },
            new[] { new Vector2(20f, 55f) },
            font,
            24f,
            new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
            Vector2.Zero);

        return context;
    }

    private static void AssertStaticGlyphRunCompiled(DxfStaticBuffer buffer)
    {
        Assert.Equal(1u, buffer.RetainedGlyphRecordCount);
        Assert.True(buffer.RetainedGlyphSegmentCount > 0);
        Assert.Equal(1u, buffer.RetainedGlyphInstanceCount);
        Assert.Empty(buffer.TextVertices);
        Assert.Empty(buffer.TextRecords);
    }

    private static TtfFont? TryLoadTestFont()
    {
        string[] candidates =
        {
            "/System/Library/Fonts/Supplemental/Arial.ttf",
            "/System/Library/Fonts/Supplemental/Helvetica.ttf",
            "/Library/Fonts/Arial.ttf",
            "C:\\Windows\\Fonts\\arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return new TtfFont(candidate);
            }
        }

        var fontInfo = FontApi.GetSystemFonts().FirstOrDefault(font => File.Exists(font.FilePath));
        return fontInfo != null ? new TtfFont(fontInfo.FilePath) : null;
    }

    private static RgbaPixel ReadPixel(byte[] pixels, uint width, int x, int y)
    {
        var index = ((y * (int)width) + x) * 4;
        return new RgbaPixel(
            pixels[index + 0],
            pixels[index + 1],
            pixels[index + 2],
            pixels[index + 3]);
    }

    private static void AssertColorNear(RgbaPixel expected, RgbaPixel actual, int tolerance)
    {
        Assert.InRange(Math.Abs(expected.R - actual.R), 0, tolerance);
        Assert.InRange(Math.Abs(expected.G - actual.G), 0, tolerance);
        Assert.InRange(Math.Abs(expected.B - actual.B), 0, tolerance);
        Assert.InRange(Math.Abs(expected.A - actual.A), 0, tolerance);
    }

    private static void AssertTransparent(RgbaPixel actual)
    {
        Assert.InRange(actual.R, 0, 8);
        Assert.InRange(actual.G, 0, 8);
        Assert.InRange(actual.B, 0, 8);
        Assert.InRange(actual.A, 0, 8);
    }

    private readonly record struct RgbaPixel(byte R, byte G, byte B, byte A);

    private sealed class PatternHatchVisual : FrameworkElement
    {
        private readonly SolidColorBrush _background =
            new(new Vector4(0f, 0f, 1f, 1f));
        private readonly HatchPatternBrush _pattern = new(
            MathF.PI / 2f,
            spacing: 8f,
            thickness: 0f,
            color: new Vector4(1f, 0f, 0f, 1f))
        {
            CoordinateTransform = Matrix4x4.CreateTranslation(0f, 0.5f, 0f),
        };
        private readonly PathGeometry _extensionPath =
            PrimitivePathGeometry.CreateRectangle(32f, 0f, 32f, 32f);

        public PatternHatchVisual()
        {
            Width = 64f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_background, null, new Rect(0f, 0f, 64f, 32f));
            context.DrawRectangle(_pattern, null, new Rect(0f, 0f, 32f, 32f));
            context.DrawHatch(_pattern, _extensionPath);
        }
    }

    private sealed class PatternSetHatchVisual : FrameworkElement
    {
        private readonly SolidColorBrush _background =
            new(new Vector4(0f, 0f, 1f, 1f));
        private readonly HatchPatternSetBrush _pattern = new(
            [
                new HatchPatternLineFamily(
                    new Vector2(0f, 3.5f),
                    Vector2.UnitX,
                    2f,
                    8f,
                    0,
                    2,
                    8f),
                new HatchPatternLineFamily(
                    new Vector2(7.5f, 7.5f),
                    Vector2.UnitY,
                    0f,
                    16f,
                    2,
                    2,
                    8f),
            ],
            [4f, -4f, 0f, -8f],
            thickness: 0f,
            color: new Vector4(1f, 0f, 0f, 1f));
        private readonly PathGeometry _extensionPath =
            PrimitivePathGeometry.CreateRectangle(32f, 0f, 32f, 32f);

        public PatternSetHatchVisual()
        {
            Width = 64f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_background, null, new Rect(0f, 0f, 64f, 32f));
            context.DrawRectangle(_pattern, null, new Rect(0f, 0f, 32f, 32f));
            context.DrawHatch(_pattern, _extensionPath);
        }
    }

    private sealed class MaskedStaticDxfVisual : FrameworkElement
    {
        private readonly DxfStaticBuffer _visibleBuffer;
        private readonly DxfStaticBuffer _extensionMaskedBuffer;
        private readonly DxfStaticBuffer _commandMaskedBuffer;

        public MaskedStaticDxfVisual(
            DxfStaticBuffer visibleBuffer,
            DxfStaticBuffer extensionMaskedBuffer,
            DxfStaticBuffer commandMaskedBuffer)
        {
            _visibleBuffer = visibleBuffer;
            _extensionMaskedBuffer = extensionMaskedBuffer;
            _commandMaskedBuffer = commandMaskedBuffer;
            Width = 190f;
            Height = 90f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawStaticDxf(_visibleBuffer);

            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 0f)),
                new Rect(75f, 25f, 40f, 40f));
            context.DrawStaticDxf(_extensionMaskedBuffer);
            context.PopOpacityMask();

            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 0f)),
                new Rect(130f, 25f, 40f, 40f));
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawStaticDxf,
                StaticBuffer = _commandMaskedBuffer
            });
            context.PopOpacityMask();
        }
    }

    private sealed class SingleStaticDxfVisual : FrameworkElement
    {
        private readonly DxfStaticBuffer _buffer;

        public SingleStaticDxfVisual(DxfStaticBuffer buffer)
        {
            _buffer = buffer;
            Width = 64f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawStaticDxf(_buffer);
        }
    }

    private sealed class AppendedStaticDxfVisual : FrameworkElement
    {
        private readonly DxfStaticBuffer _buffer;
        private readonly Vector2 _translation;

        public AppendedStaticDxfVisual(DxfStaticBuffer buffer, Vector2 translation)
        {
            _buffer = buffer;
            _translation = translation;
            Width = 72f;
            Height = 40f;
        }

        public override void OnRender(DrawingContext context)
        {
            var source = new DrawingContext();
            source.DrawStaticDxf(_buffer);
            context.Append(source, _translation);
        }
    }

    private sealed class OffsetStaticDxfHost : FrameworkElement
    {
        private readonly FrameworkElement _child;

        public OffsetStaticDxfHost(FrameworkElement child)
        {
            _child = child;
            Width = 96f;
            Height = 64f;
            AddChild(child);
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            _child.Measure(new Vector2(24f, 24f));
            return new Vector2(96f, 64f);
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
            _child.Arrange(new Rect(48f, 20f, 24f, 24f));
        }
    }

    private sealed class ClearBlendStaticDxfVisual : FrameworkElement
    {
        private readonly DxfStaticBuffer _extensionBuffer;
        private readonly DxfStaticBuffer _commandBuffer;

        public ClearBlendStaticDxfVisual(DxfStaticBuffer extensionBuffer, DxfStaticBuffer commandBuffer)
        {
            _extensionBuffer = extensionBuffer;
            _commandBuffer = commandBuffer;
            Width = 72f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
                null,
                new Rect(0f, 0f, 72f, 32f));

            context.PushBlendMode(GpuBlendMode.Clear);
            context.DrawStaticDxf(_extensionBuffer);
            context.PopBlendMode();

            context.PushBlendMode(GpuBlendMode.Clear);
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawStaticDxf,
                StaticBuffer = _commandBuffer
            });
            context.PopBlendMode();
        }
    }

    private sealed class CollapsedNestedClipStaticDxfVisual : FrameworkElement
    {
        private readonly DxfStaticBuffer _buffer;
        private readonly SolidColorBrush _background = new(new Vector4(0f, 0f, 1f, 1f));

        public CollapsedNestedClipStaticDxfVisual(DxfStaticBuffer buffer)
        {
            _buffer = buffer;
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_background, null, new Rect(0f, 0f, 32f, 32f));

            context.PushClip(new Rect(0f, 0f, 16f, 16f));
            context.PushClip(new Rect(16f, 0f, 16f, 16f));
            context.DrawStaticDxf(_buffer);
            context.PopClip();
            context.PopClip();
        }
    }

    private sealed class ClearBlendStaticDxfSplineVisual : FrameworkElement
    {
        private readonly DxfStaticBuffer _buffer;

        public ClearBlendStaticDxfSplineVisual(DxfStaticBuffer buffer)
        {
            _buffer = buffer;
            Width = 48f;
            Height = 48f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
                null,
                new Rect(0f, 0f, 48f, 48f));

            context.PushBlendMode(GpuBlendMode.Clear);
            context.DrawStaticDxf(_buffer);
            context.PopBlendMode();
        }
    }
}
