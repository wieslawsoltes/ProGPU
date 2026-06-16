using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Text;
using ProGPU.Vector;
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
}
