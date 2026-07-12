using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkPaintTextCompatibilityTests
{
    private static readonly Type PaintType = typeof(SKPaint);

    [Fact]
    public void DefaultsAndFontEdgingMatchLegacyPaint()
    {
        using var paint = new SKPaint();

        Assert.False(paint.IsAntialias);
        Assert.False(paint.IsDither);
        Assert.False(paint.IsStroke);
        Assert.Equal(SKPaintStyle.Fill, paint.Style);
        Assert.False(Get<bool>(paint, "IsLinearText"));
        Assert.False(Get<bool>(paint, "SubpixelText"));
        Assert.False(Get<bool>(paint, "LcdRenderText"));
        Assert.False(Get<bool>(paint, "IsEmbeddedBitmapText"));
        Assert.False(Get<bool>(paint, "IsAutohinted"));
        Assert.False(Get<bool>(paint, "FakeBoldText"));
        Assert.Equal(12f, Get<float>(paint, "TextSize"));
        Assert.Equal(SKTextAlign.Left, Get<SKTextAlign>(paint, "TextAlign"));
        Assert.Equal(SKTextEncoding.Utf8, Get<SKTextEncoding>(paint, "TextEncoding"));
        Assert.Equal(1f, Get<float>(paint, "TextScaleX"));
        Assert.Equal(0f, Get<float>(paint, "TextSkewX"));
        Assert.Equal(0, Convert.ToInt32(Get<object>(paint, "FilterQuality")));
        Assert.Equal(2, Convert.ToInt32(Get<object>(paint, "HintingLevel")));

        using var initial = ToFont(paint);
        Assert.Equal(SKFontEdging.Alias, initial.Edging);
        paint.IsAntialias = true;
        using var antialiased = ToFont(paint);
        Assert.Equal(SKFontEdging.Antialias, antialiased.Edging);
        Set(paint, "LcdRenderText", true);
        using var subpixel = ToFont(paint);
        Assert.Equal(SKFontEdging.SubpixelAntialias, subpixel.Edging);
        paint.IsAntialias = false;
        using var aliased = ToFont(paint);
        Assert.Equal(SKFontEdging.Alias, aliased.Edging);

        paint.Style = SKPaintStyle.StrokeAndFill;
        Assert.True(paint.IsStroke);
    }

    [Fact]
    public void FontConstructorCloneAndResetOwnIndependentSnapshots()
    {
        using var source = new SKFont(SKTypeface.Default, 31f, 1.5f, 0.2f)
        {
            Embolden = true,
            LinearMetrics = true,
            Subpixel = true,
            EmbeddedBitmaps = true,
            ForceAutoHinting = true,
            Hinting = SKFontHinting.Full,
        };
        using var paint = (SKPaint)Activator.CreateInstance(PaintType, source)!;
        source.Size = 9f;

        Assert.Equal(31f, Get<float>(paint, "TextSize"));
        Assert.Equal(1.5f, Get<float>(paint, "TextScaleX"));
        Assert.Equal(0.2f, Get<float>(paint, "TextSkewX"));
        Assert.True(Get<bool>(paint, "FakeBoldText"));
        Assert.True(Get<bool>(paint, "IsLinearText"));
        Assert.True(Get<bool>(paint, "SubpixelText"));
        Assert.True(Get<bool>(paint, "IsEmbeddedBitmapText"));
        Assert.True(Get<bool>(paint, "IsAutohinted"));
        Assert.Equal(3, Convert.ToInt32(Get<object>(paint, "HintingLevel")));
        Assert.False(paint.IsAntialias);

        Set(paint, "TextSize", 17f);
        Set(paint, "TextAlign", SKTextAlign.Center);
        Set(paint, "TextEncoding", SKTextEncoding.GlyphId);
        paint.IsAntialias = true;
        Set(paint, "LcdRenderText", true);
        paint.IsDither = true;
        using var clone = paint.Clone();
        Set(paint, "TextSize", 8f);

        Assert.Equal(17f, Get<float>(clone, "TextSize"));
        Assert.Equal(SKTextAlign.Center, Get<SKTextAlign>(clone, "TextAlign"));
        Assert.Equal(SKTextEncoding.GlyphId, Get<SKTextEncoding>(clone, "TextEncoding"));
        Assert.True(clone.IsAntialias);
        Assert.True(clone.IsDither);
        using var cloneFont = ToFont(clone);
        Assert.Equal(SKFontEdging.SubpixelAntialias, cloneFont.Edging);

        clone.Reset();
        Assert.Equal(12f, Get<float>(clone, "TextSize"));
        Assert.Equal(SKTextAlign.Left, Get<SKTextAlign>(clone, "TextAlign"));
        Assert.Equal(SKTextEncoding.Utf8, Get<SKTextEncoding>(clone, "TextEncoding"));
        Assert.False(clone.IsAntialias);
        Assert.False(clone.IsDither);
        using var resetFont = ToFont(clone);
        Assert.Equal(SKFontEdging.Alias, resetFont.Edging);
    }

    [Fact]
    public void StringTextMethodsDelegateToTheFontEngine()
    {
        using var paint = new SKPaint();
        Set(paint, "TextSize", 24f);
        Set(paint, "TextScaleX", 1.25f);
        Set(paint, "TextSkewX", 0.15f);
        using var font = ToFont(paint);
        const string text = "A\U0001f600V";

        var measured = Invoke<float>(paint, "MeasureText", [typeof(string)], [text]);
        Assert.Equal(font.MeasureText(text, paint), measured, 4);
        var bounds = SKRect.Empty;
        var boundsArguments = new object?[] { text, bounds };
        var measuredWithBounds = (float)GetMethod(
            "MeasureText",
            typeof(string),
            typeof(SKRect).MakeByRefType()).Invoke(paint, boundsArguments)!;
        Assert.Equal(measured, measuredWithBounds, 4);
        font.MeasureText(text, out var expectedBounds, paint);
        Assert.Equal(expectedBounds, (SKRect)boundsArguments[1]!);

        Assert.Equal(font.CountGlyphs(text), Invoke<int>(paint, "CountGlyphs", [typeof(string)], [text]));
        Assert.Equal(font.ContainsGlyphs(text), Invoke<bool>(paint, "ContainsGlyphs", [typeof(string)], [text]));
        Assert.Equal(font.GetGlyphs(text), Invoke<ushort[]>(paint, "GetGlyphs", [typeof(string)], [text]));
        Assert.Equal(
            font.GetGlyphPositions(text, new SKPoint(3f, 4f)),
            Invoke<SKPoint[]>(
                paint,
                "GetGlyphPositions",
                [typeof(string), typeof(SKPoint)],
                [text, new SKPoint(3f, 4f)]));
        Assert.Equal(
            font.GetGlyphOffsets(text, 3f),
            Invoke<float[]>(paint, "GetGlyphOffsets", [typeof(string), typeof(float)], [text, 3f]));
        Assert.Equal(
            font.GetGlyphWidths(text, paint),
            Invoke<float[]>(paint, "GetGlyphWidths", [typeof(string)], [text]));

        using var path = Invoke<SKPath>(
            paint,
            "GetTextPath",
            [typeof(string), typeof(float), typeof(float)],
            [text, 7f, 11f]);
        using var expectedPath = font.GetTextPath(text, new SKPoint(7f, 11f));
        Assert.Equal(expectedPath.TightBounds, path.TightBounds);
    }

    [Fact]
    public void BreakTextAndEncodedPointerMethodsPreserveEncodingUnits()
    {
        using var paint = new SKPaint();
        Set(paint, "TextSize", 20f);
        using var font = ToFont(paint);
        const string text = "A\U0001f600B";
        var width = font.MeasureText("A\U0001f600", paint) + 0.01f;
        var breakArguments = new object?[] { text, width, 0f, null };
        var consumed = (long)GetMethod(
            "BreakText",
            typeof(string),
            typeof(float),
            typeof(float).MakeByRefType(),
            typeof(string).MakeByRefType()).Invoke(paint, breakArguments)!;
        Assert.Equal(3, consumed);
        Assert.Equal("A\U0001f600", breakArguments[3]);

        var utf8 = Encoding.UTF8.GetBytes(text);
        Set(paint, "TextEncoding", SKTextEncoding.Utf8);
        Assert.Equal(
            font.GetGlyphs(utf8, SKTextEncoding.Utf8),
            Invoke<ushort[]>(paint, "GetGlyphs", [typeof(byte[])], [utf8]));
        Assert.Equal(
            font.MeasureText(utf8, SKTextEncoding.Utf8, paint),
            Invoke<float>(paint, "MeasureText", [typeof(byte[])], [utf8]),
            4);

        var address = Marshal.AllocHGlobal(utf8.Length);
        try
        {
            Marshal.Copy(utf8, 0, address, utf8.Length);
            Assert.Equal(
                font.GetGlyphs(address, utf8.Length, SKTextEncoding.Utf8),
                Invoke<ushort[]>(
                    paint,
                    "GetGlyphs",
                    [typeof(IntPtr), typeof(int)],
                    [address, utf8.Length]));
            Assert.Equal(
                font.MeasureText(address, utf8.Length, SKTextEncoding.Utf8, paint),
                Invoke<float>(
                    paint,
                    "MeasureText",
                    [typeof(IntPtr), typeof(int)],
                    [address, utf8.Length]),
                4);
        }
        finally
        {
            Marshal.FreeHGlobal(address);
        }

        var glyphs = font.GetGlyphs(text);
        var glyphBytes = MemoryMarshal.AsBytes(glyphs.AsSpan()).ToArray();
        Set(paint, "TextEncoding", SKTextEncoding.GlyphId);
        Assert.Equal(glyphs, Invoke<ushort[]>(paint, "GetGlyphs", [typeof(byte[])], [glyphBytes]));
    }

    [Fact]
    public void TextInterceptAdaptersMatchEquivalentTextBlobs()
    {
        using var paint = new SKPaint { IsAntialias = true };
        Set(paint, "TextSize", 28f);
        using var font = ToFont(paint);
        const string text = "AV";
        const float lower = -24f;
        const float upper = 2f;

        var textIntercepts = Invoke<float[]>(
            paint,
            "GetTextIntercepts",
            [typeof(string), typeof(float), typeof(float), typeof(float), typeof(float)],
            [text, 5f, 7f, lower, upper]);
        var glyphs = font.GetGlyphs(text);
        using var textBlob = new SKTextBlob(
            font,
            glyphs,
            font.GetGlyphPositions(glyphs, new SKPoint(5f, 7f)));
        Assert.Equal(textBlob.GetIntercepts(lower, upper, paint), textIntercepts);

        var positions = new[] { new SKPoint(10f, 6f), new SKPoint(42f, 6f) };
        var positioned = Invoke<float[]>(
            paint,
            "GetPositionedTextIntercepts",
            [typeof(string), typeof(SKPoint[]), typeof(float), typeof(float)],
            [text, positions, lower, upper]);
        using var positionedBlob = new SKTextBlob(font, glyphs, positions);
        Assert.Equal(positionedBlob.GetIntercepts(lower, upper, paint), positioned);

        var xpositions = new[] { 10f, 42f };
        var horizontal = Invoke<float[]>(
            paint,
            "GetHorizontalTextIntercepts",
            [typeof(string), typeof(float[]), typeof(float), typeof(float), typeof(float)],
            [text, xpositions, 6f, lower, upper]);
        Assert.Equal(positioned, horizontal);
    }

    [Fact]
    public void LegacySurfaceRetainsNativeObsoleteMetadataAndInheritance()
    {
        Assert.Equal(typeof(SKObject), PaintType.BaseType);
        Assert.Null(PaintType.GetMethod(
            nameof(IDisposable.Dispose),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Null(PaintType.GetProperty(
            nameof(SKPaint.Handle),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));

        var fontConstructor = PaintType.GetConstructor([typeof(SKFont)]);
        Assert.NotNull(fontConstructor);
        AssertObsoleteError(fontConstructor!, "Use SKFont instead.");
        AssertObsoleteError(PaintType.GetProperty("TextSize")!, "Use SKFont.Size instead.");
        AssertObsoleteError(PaintType.GetMethod("MeasureText", [typeof(string)])!, "Use SKFont.MeasureText() instead.");

        var hintingType = PaintType.Assembly.GetType("SkiaSharp.SKPaintHinting", throwOnError: true)!;
        Assert.Equal(new[] { "NoHinting", "Slight", "Normal", "Full" }, Enum.GetNames(hintingType));
        Assert.Equal(new[] { 0, 1, 2, 3 }, Enum.GetValues(hintingType).Cast<object>().Select(Convert.ToInt32));
        AssertObsoleteError(hintingType, "Use SKFontHinting instead.");
    }

    private static SKFont ToFont(SKPaint paint) =>
        (SKFont)PaintType.GetMethod("ToFont")!.Invoke(paint, null)!;

    private static T Get<T>(SKPaint paint, string property) =>
        (T)PaintType.GetProperty(property)!.GetValue(paint)!;

    private static void Set(SKPaint paint, string property, object value) =>
        PaintType.GetProperty(property)!.SetValue(paint, value);

    private static T Invoke<T>(
        SKPaint paint,
        string name,
        Type[] parameterTypes,
        object?[] arguments) =>
        (T)GetMethod(name, parameterTypes).Invoke(paint, arguments)!;

    private static MethodInfo GetMethod(string name, params Type[] parameterTypes) =>
        PaintType.GetMethod(name, parameterTypes)
        ?? throw new MissingMethodException(PaintType.FullName, name);

    private static void AssertObsoleteError(MemberInfo member, string message)
    {
        var obsolete = member.GetCustomAttribute<ObsoleteAttribute>();
        Assert.NotNull(obsolete);
        Assert.True(obsolete.IsError);
        Assert.Equal(message, obsolete.Message);
    }
}
