using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Text;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class CompositorReviewRegressionTests
{
    [Fact]
    public void DrawGlyphRunFlushesActualTextCountBeforeColorLayerPaths()
    {
        var font = new TtfFont(BuildColorLayerFont());
        var window = HeadlessWindow.Shared;
        window.Resize(96, 48);
        window.Content = new MixedColorGlyphRunVisual(font);

        try
        {
            window.Render();

            AssertMixedColorGlyphDrawCalls(window.Compositor);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawTextFlushesActualTextCountBeforeColorLayerPaths()
    {
        var font = new TtfFont(BuildColorLayerFont());
        var window = HeadlessWindow.Shared;
        window.Resize(96, 48);
        window.Content = new MixedColorTextVisual(font);

        try
        {
            window.Render();

            AssertMixedColorGlyphDrawCalls(window.Compositor);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void TransformedEllipticalRoundedRectanglePathFallbackAppliesTransformOnce()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(80, 40);
        window.Content = new TransformedEllipticalRoundedRectangleVisual();

        try
        {
            window.Render();

            byte[] pixels = window.ReadPixels();
            RgbaPixel expected = ReadPixel(pixels, window.Width, x: 32, y: 17);
            RgbaPixel doubleTransformed = ReadPixel(pixels, window.Width, x: 52, y: 22);

            Assert.True(expected.R >= 220, $"Expected once-transformed rounded rectangle center to be red, found {expected}.");
            Assert.True(expected.G <= 35, $"Expected once-transformed rounded rectangle center to keep green low, found {expected}.");
            Assert.True(expected.B <= 35, $"Expected once-transformed rounded rectangle center to keep blue low, found {expected}.");
            Assert.Equal(255, expected.A);

            Assert.True(
                doubleTransformed.R < 80 || doubleTransformed.A < 220,
                $"Expected double-transformed location to remain outside the rounded rectangle fill, found {doubleTransformed}.");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void RoundedRectangleWithExplicitZeroRadiusYRendersAsRectangle()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 24);
        window.Content = new ExplicitZeroRadiusYRoundedRectangleVisual();

        try
        {
            window.Render();

            byte[] pixels = window.ReadPixels();
            RgbaPixel corner = ReadPixel(pixels, window.Width, x: 5, y: 5);

            Assert.True(corner.R >= 220, $"Expected explicit zero RadiusY to keep the rectangle corner red, found {corner}.");
            Assert.True(corner.G <= 35, $"Expected explicit zero RadiusY to keep green low, found {corner}.");
            Assert.True(corner.B <= 35, $"Expected explicit zero RadiusY to keep blue low, found {corner}.");
            Assert.Equal(255, corner.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void RenderOffscreenKeepsPathAtlasBuffersAliveAfterSubmit()
    {
        using var window = new HeadlessWindow(32, 32);
        using var target = new GpuTexture(
            window.Context,
            32,
            32,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "RenderOffscreen PathAtlas Buffer Lifetime Test");
        var visual = new DrawingVisual
        {
            Size = new Vector2(32f, 32f)
        };
        visual.Context.DrawPath(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            pen: null,
            PrimitivePathGeometry.CreateRoundedRectangle(4f, 4f, 20f, 16f, 4f, 4f));

        window.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 32,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1f);

        Assert.NotEmpty(GetPathAtlasTempBuffers(window.Compositor));
    }

    [Fact]
    public void RenderOffscreenRunsExtensionFrameScopeForTopLevelPass()
    {
        using var window = new HeadlessWindow(32, 32);
        using var target = new GpuTexture(
            window.Context,
            32,
            32,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "RenderOffscreen Extension Frame Scope Test");
        var extension = new CountingExtension();
        window.Compositor.RegisterExtension(9001, extension);

        var visual = new DrawingVisual
        {
            Size = new Vector2(32f, 32f)
        };
        visual.Context.DrawRectangle(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            pen: null,
            new Rect(0f, 0f, 16f, 16f));

        window.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 32,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1f);

        Assert.Equal(1, extension.BeginFrameCount);
        Assert.Equal(1, extension.EndFrameCount);
    }

    [Fact]
    public void CachedTextureBindGroupsAreQueuedWhenSourceTextureIsDisposed()
    {
        using var window = new HeadlessWindow(16, 16);
        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Texture BindGroup Disposal Queue Test");
        texture.WritePixels<byte>(new byte[] { 255, 0, 0, 255 });
        window.Content = new TextureCacheVisual(texture);

        window.Render();

        var textureId = texture.Id;
        var textureBindGroups = GetPersistentTextureBindGroups(window.Compositor);
        Assert.Contains(textureBindGroups.Keys, key => key.TextureId == textureId);
        Assert.Empty(window.Context.PendingBindGroups);

        texture.Dispose();
        window.Content = null;

        Assert.DoesNotContain(textureBindGroups.Keys, key => key.TextureId == textureId);
        lock (window.Context.DisposalLock)
        {
            Assert.Contains(window.Context.PendingBindGroups, ptr => ptr != IntPtr.Zero);
        }

        window.Context.CleanupPendingResources();
    }

    private static void AssertMixedColorGlyphDrawCalls(Compositor compositor)
    {
        Compositor.CompositorDrawCall[] drawCalls = GetDrawCalls(compositor);
        Assert.Contains(drawCalls, drawCall => drawCall.Type == Compositor.DrawCallType.Vector);

        Compositor.CompositorDrawCall textDraw = Assert.Single(
            drawCalls,
            drawCall => drawCall.Type == Compositor.DrawCallType.Text && drawCall.IndexCount > 0);
        Assert.Equal(1u, textDraw.IndexCount);
    }

    private static Compositor.CompositorDrawCall[] GetDrawCalls(Compositor compositor)
    {
        var field = typeof(Compositor).GetField("_drawCalls", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var drawCalls = Assert.IsAssignableFrom<IEnumerable<Compositor.CompositorDrawCall>>(field.GetValue(compositor));
        return drawCalls.ToArray();
    }

    private static IList GetPathAtlasTempBuffers(Compositor compositor)
    {
        var pathAtlasField = typeof(Compositor).GetField("_pathAtlas", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pathAtlasField);
        var pathAtlas = pathAtlasField.GetValue(compositor);
        Assert.NotNull(pathAtlas);

        var tempBuffersField = pathAtlas.GetType().GetField("_tempBuffers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(tempBuffersField);
        return Assert.IsAssignableFrom<IList>(tempBuffersField.GetValue(pathAtlas));
    }

    private static Dictionary<Compositor.TextureCacheKey, Compositor.CachedBindGroup> GetPersistentTextureBindGroups(Compositor compositor)
    {
        var field = typeof(Compositor).GetField("_persistentTextureBindGroups", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<Dictionary<Compositor.TextureCacheKey, Compositor.CachedBindGroup>>(field.GetValue(compositor));
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

    private static byte[] BuildColorLayerFont()
    {
        byte[][] glyphs =
        {
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            BuildRectangleGlyph(0, 0, 500, 500),
            BuildRectangleGlyph(120, 120, 620, 620),
        };

        byte[] glyf = BuildGlyfTable(glyphs, out uint[] glyphOffsets);
        return BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable(glyphs.Length)),
            ("maxp", BuildMaxpTable(glyphs.Length)),
            ("hmtx", BuildHmtxTable(glyphs.Length)),
            ("cmap", BuildCmapFormat12Table()),
            ("loca", BuildLongLoca(glyphOffsets)),
            ("glyf", glyf),
            ("COLR", BuildColrTable()),
            ("CPAL", BuildCpalTable()));
    }

    private sealed class TextureCacheVisual : FrameworkElement
    {
        private readonly GpuTexture _texture;

        public TextureCacheVisual(GpuTexture texture)
        {
            _texture = texture;
            Width = 16f;
            Height = 16f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawTexture(_texture, new Rect(0f, 0f, 16f, 16f));
        }
    }

    private static byte[] BuildRectangleGlyph(short xMin, short yMin, short xMax, short yMax)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteShort(writer, 1);
        WriteShort(writer, xMin);
        WriteShort(writer, yMin);
        WriteShort(writer, xMax);
        WriteShort(writer, yMax);
        WriteUShort(writer, 3);
        WriteUShort(writer, 0);
        writer.Write(new byte[] { 1, 1, 1, 1 });
        WriteShort(writer, xMin);
        WriteShort(writer, (short)(xMax - xMin));
        WriteShort(writer, 0);
        WriteShort(writer, (short)(xMin - xMax));
        WriteShort(writer, yMin);
        WriteShort(writer, 0);
        WriteShort(writer, (short)(yMax - yMin));
        WriteShort(writer, 0);
        return stream.ToArray();
    }

    private static byte[] BuildGlyfTable(byte[][] glyphs, out uint[] glyphOffsets)
    {
        glyphOffsets = new uint[glyphs.Length + 1];
        using var stream = new MemoryStream();

        for (int i = 0; i < glyphs.Length; i++)
        {
            glyphOffsets[i] = checked((uint)stream.Position);
            stream.Write(glyphs[i]);
            WritePadding(stream);
        }

        glyphOffsets[^1] = checked((uint)stream.Position);
        return stream.ToArray();
    }

    private static byte[] BuildHeadTable()
    {
        byte[] table = new byte[54];
        using var stream = new MemoryStream(table);
        using var writer = new BinaryWriter(stream);

        WriteUInt(writer, 0x00010000);
        WriteUInt(writer, 0x00010000);
        stream.Position = 18;
        WriteUShort(writer, 1000);
        stream.Position = 50;
        WriteShort(writer, 1);
        return table;
    }

    private static byte[] BuildHheaTable(int glyphCount)
    {
        byte[] table = new byte[36];
        using var stream = new MemoryStream(table);
        using var writer = new BinaryWriter(stream);

        stream.Position = 4;
        WriteShort(writer, 800);
        WriteShort(writer, -200);
        WriteShort(writer, 0);
        stream.Position = 34;
        WriteUShort(writer, checked((ushort)glyphCount));
        return table;
    }

    private static byte[] BuildMaxpTable(int glyphCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUInt(writer, 0x00010000);
        WriteUShort(writer, checked((ushort)glyphCount));
        return stream.ToArray();
    }

    private static byte[] BuildHmtxTable(int glyphCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        for (int i = 0; i < glyphCount; i++)
        {
            WriteUShort(writer, 600);
            WriteShort(writer, 0);
        }

        return stream.ToArray();
    }

    private static byte[] BuildCmapFormat12Table()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 1);
        WriteUShort(writer, 3);
        WriteUShort(writer, 10);
        WriteUInt(writer, 12);
        WriteUShort(writer, 12);
        WriteUShort(writer, 0);
        WriteUInt(writer, 28);
        WriteUInt(writer, 0);
        WriteUInt(writer, 1);
        WriteUInt(writer, (uint)'A');
        WriteUInt(writer, (uint)'B');
        WriteUInt(writer, 1);
        return stream.ToArray();
    }

    private static byte[] BuildLongLoca(uint[] glyphOffsets)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        foreach (uint offset in glyphOffsets)
        {
            WriteUInt(writer, offset);
        }

        return stream.ToArray();
    }

    private static byte[] BuildColrTable()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 1);
        WriteUInt(writer, 14);
        WriteUInt(writer, 20);
        WriteUShort(writer, 2);
        WriteUShort(writer, 1);
        WriteUShort(writer, 0);
        WriteUShort(writer, 2);
        WriteUShort(writer, 2);
        WriteUShort(writer, 0);
        WriteUShort(writer, 3);
        WriteUShort(writer, 1);
        return stream.ToArray();
    }

    private static byte[] BuildCpalTable()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 2);
        WriteUShort(writer, 1);
        WriteUShort(writer, 2);
        WriteUInt(writer, 14);
        WriteUShort(writer, 0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)255);
        writer.Write((byte)255);
        writer.Write((byte)255);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)255);
        return stream.ToArray();
    }

    private static byte[] BuildSfntWithTables(params (string Tag, byte[] Data)[] tables)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUInt(writer, 0x00010000);
        WriteUShort(writer, checked((ushort)tables.Length));
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);

        uint tableOffset = (uint)(12 + tables.Length * 16);
        foreach ((string tag, byte[] data) in tables)
        {
            WriteTag(writer, tag);
            WriteUInt(writer, 0);
            WriteUInt(writer, tableOffset);
            WriteUInt(writer, (uint)data.Length);
            tableOffset += (uint)data.Length;
        }

        foreach ((_, byte[] data) in tables)
        {
            writer.Write(data);
        }

        return stream.ToArray();
    }

    private static void WritePadding(Stream stream)
    {
        while ((stream.Position & 3) != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static void WriteTag(BinaryWriter writer, string tag)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(tag);
        Assert.Equal(4, bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteShort(BinaryWriter writer, short value)
    {
        WriteUShort(writer, unchecked((ushort)value));
    }

    private static void WriteUShort(BinaryWriter writer, ushort value)
    {
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static void WriteUInt(BinaryWriter writer, uint value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private readonly record struct RgbaPixel(byte R, byte G, byte B, byte A);

    private sealed class CountingExtension : ICompositorExtension
    {
        public int BeginFrameCount { get; private set; }

        public int EndFrameCount { get; private set; }

        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
        }

        public void BeginFrame(Compositor compositor)
        {
            BeginFrameCount++;
        }

        public void EndFrame(Compositor compositor)
        {
            EndFrameCount++;
        }
    }

    private sealed class MixedColorGlyphRunVisual : FrameworkElement
    {
        private readonly TtfFont _font;

        public MixedColorGlyphRunVisual(TtfFont font)
        {
            _font = font;
            Width = 96f;
            Height = 48f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawGlyphRun(
                new ushort[] { 1, 2 },
                new[] { new Vector2(6f, 30f), new Vector2(36f, 30f) },
                _font,
                24f,
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                Vector2.Zero);
        }
    }

    private sealed class MixedColorTextVisual : FrameworkElement
    {
        private readonly TtfFont _font;

        public MixedColorTextVisual(TtfFont font)
        {
            _font = font;
            Width = 96f;
            Height = 48f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawText(
                "AB",
                _font,
                24f,
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                new Vector2(6f, 30f));
        }
    }

    private sealed class TransformedEllipticalRoundedRectangleVisual : FrameworkElement
    {
        public TransformedEllipticalRoundedRectangleVisual()
        {
            Width = 80f;
            Height = 40f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawRoundedRect,
                Brush = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                Rect = new Rect(0f, 0f, 24f, 24f),
                RadiusX = 4f,
                RadiusY = 8f,
                Transform = Matrix4x4.CreateTranslation(20f, 5f, 0f)
            });
        }
    }

    private sealed class ExplicitZeroRadiusYRoundedRectangleVisual : FrameworkElement
    {
        public ExplicitZeroRadiusYRoundedRectangleVisual()
        {
            Width = 32f;
            Height = 24f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawRoundedRect,
                Brush = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                Rect = new Rect(4f, 4f, 20f, 12f),
                RadiusX = 8f,
                RadiusY = 0f
            });
        }
    }
}
