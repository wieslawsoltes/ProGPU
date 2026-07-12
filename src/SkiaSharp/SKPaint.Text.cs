using System;

namespace SkiaSharp;

#pragma warning disable CS0619

[Obsolete("Use SKFontHinting instead.", true)]
public enum SKPaintHinting
{
    NoHinting = 0,
    Slight = 1,
    Normal = 2,
    Full = 3,
}

public partial class SKPaint
{
    private readonly SKFont _legacyFont;
    private bool _isAntialias;
    private bool _isDither;
    private bool _lcdRenderText;
    private SKTextAlign _textAlign;
    private SKTextEncoding _textEncoding;
    private int _filterQuality;

    public SKPaint()
        : base(SKObjectHandle.Create(), owns: true)
    {
        _legacyFont = new SKFont();
        ResetLegacyTextState();
    }

    [Obsolete("Use SKFont instead.", true)]
    public SKPaint(SKFont font)
        : base(SKObjectHandle.Create(), owns: true)
    {
        ArgumentNullException.ThrowIfNull(font);
        _legacyFont = CloneLegacyFont(font);
        _isAntialias = false;
        _lcdRenderText = false;
        _textAlign = SKTextAlign.Left;
        _textEncoding = SKTextEncoding.Utf8;
        _filterQuality = 0;
        UpdateLegacyFontEdging();
    }

    public bool IsDither
    {
        get => _isDither;
        set => _isDither = value;
    }

    [Obsolete("Use SKFont.LinearMetrics instead.", true)]
    public bool IsLinearText
    {
        get => _legacyFont.LinearMetrics;
        set => _legacyFont.LinearMetrics = value;
    }

    [Obsolete("Use SKFont.Subpixel instead.", true)]
    public bool SubpixelText
    {
        get => _legacyFont.Subpixel;
        set => _legacyFont.Subpixel = value;
    }

    [Obsolete("Use SKFont.Edging instead.", true)]
    public bool LcdRenderText
    {
        get => _lcdRenderText;
        set
        {
            _lcdRenderText = value;
            UpdateLegacyFontEdging();
        }
    }

    [Obsolete("Use SKFont.EmbeddedBitmaps instead.", true)]
    public bool IsEmbeddedBitmapText
    {
        get => _legacyFont.EmbeddedBitmaps;
        set => _legacyFont.EmbeddedBitmaps = value;
    }

    [Obsolete("Use SKFont.ForceAutoHinting instead.", true)]
    public bool IsAutohinted
    {
        get => _legacyFont.ForceAutoHinting;
        set => _legacyFont.ForceAutoHinting = value;
    }

    [Obsolete("Use SKFont.Hinting instead.", true)]
    public SKPaintHinting HintingLevel
    {
        get => (SKPaintHinting)_legacyFont.Hinting;
        set => _legacyFont.Hinting = (SKFontHinting)value;
    }

    [Obsolete("Use SKFont.Embolden instead.", true)]
    public bool FakeBoldText
    {
        get => _legacyFont.Embolden;
        set => _legacyFont.Embolden = value;
    }

    [Obsolete("Use SKSamplingOptions instead.", true)]
    public SKFilterQuality FilterQuality
    {
        get => (SKFilterQuality)_filterQuality;
        set => _filterQuality = (int)value;
    }

    [Obsolete("Use SKTextAlign method overloads instead.", true)]
    public SKTextAlign TextAlign
    {
        get => _textAlign;
        set => _textAlign = value;
    }

    [Obsolete("Use SKTextEncoding method overloads instead.", true)]
    public SKTextEncoding TextEncoding
    {
        get => _textEncoding;
        set => _textEncoding = value;
    }

    [Obsolete("Use SKFont.ScaleX instead.", true)]
    public float TextScaleX
    {
        get => _legacyFont.ScaleX;
        set => _legacyFont.ScaleX = value;
    }

    [Obsolete("Use SKFont.SkewX instead.", true)]
    public float TextSkewX
    {
        get => _legacyFont.SkewX;
        set => _legacyFont.SkewX = value;
    }

    public SKMaskFilter? MaskFilter { get; set; }

    [Obsolete("Use SKFont.Spacing instead.", true)]
    public float FontSpacing => _legacyFont.Spacing;

    [Obsolete("Use SKFont.Metrics instead.", true)]
    public SKFontMetrics FontMetrics => _legacyFont.Metrics;

    public void SetColor(SKColorF color, SKColorSpace colorspace)
    {
        _ = colorspace;
        ColorF = color;
    }

    [Obsolete("Use SKFont.GetFontMetrics() instead.", true)]
    public float GetFontMetrics(out SKFontMetrics metrics) =>
        _legacyFont.GetFontMetrics(out metrics);

    [Obsolete("Use SKFont instead.", true)]
    public SKFont ToFont() => CloneLegacyFont(_legacyFont);

    internal SKTextAlign GetLegacyTextAlign() => _textAlign;
    internal SKTextEncoding GetLegacyTextEncoding() => _textEncoding;
    internal SKFont GetLegacyFont() => _legacyFont;

    internal SKSamplingOptions GetLegacyFilterQualitySampling() => _filterQuality switch
    {
        0 => new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
        1 => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
        2 => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
        3 => new SKSamplingOptions(SKCubicResampler.Mitchell),
        _ => throw new ArgumentOutOfRangeException(
            nameof(_filterQuality),
            $"Unknown filter quality: '{(int)_filterQuality}'"),
    };

    private void UpdateLegacyFontEdging()
    {
        _legacyFont.Edging = !_isAntialias
            ? SKFontEdging.Alias
            : _lcdRenderText
                ? SKFontEdging.SubpixelAntialias
                : SKFontEdging.Antialias;
    }

    private void ResetLegacyTextState()
    {
        _legacyFont.Typeface = SKTypeface.Default;
        _legacyFont.Size = 12f;
        _legacyFont.ScaleX = 1f;
        _legacyFont.SkewX = 0f;
        _legacyFont.Hinting = SKFontHinting.Normal;
        _legacyFont.Subpixel = false;
        _legacyFont.BaselineSnap = true;
        _legacyFont.ForceAutoHinting = false;
        _legacyFont.LinearMetrics = false;
        _legacyFont.Embolden = false;
        _legacyFont.EmbeddedBitmaps = false;
        _isAntialias = false;
        _isDither = false;
        _lcdRenderText = false;
        _textAlign = SKTextAlign.Left;
        _textEncoding = SKTextEncoding.Utf8;
        _filterQuality = 0;
        UpdateLegacyFontEdging();
    }

    private void CopyLegacyTextStateFrom(SKPaint source)
    {
        CopyLegacyFont(source._legacyFont, _legacyFont);
        _isAntialias = source._isAntialias;
        _isDither = source._isDither;
        _lcdRenderText = source._lcdRenderText;
        _textAlign = source._textAlign;
        _textEncoding = source._textEncoding;
        _filterQuality = source._filterQuality;
        UpdateLegacyFontEdging();
    }

    private static SKFont CloneLegacyFont(SKFont source)
    {
        var clone = new SKFont();
        CopyLegacyFont(source, clone);
        return clone;
    }

    private static void CopyLegacyFont(SKFont source, SKFont destination)
    {
        destination.Typeface = source.Typeface;
        destination.Size = source.Size;
        destination.ScaleX = source.ScaleX;
        destination.SkewX = source.SkewX;
        destination.Hinting = source.Hinting;
        destination.Edging = source.Edging;
        destination.Subpixel = source.Subpixel;
        destination.BaselineSnap = source.BaselineSnap;
        destination.ForceAutoHinting = source.ForceAutoHinting;
        destination.LinearMetrics = source.LinearMetrics;
        destination.Embolden = source.Embolden;
        destination.EmbeddedBitmaps = source.EmbeddedBitmaps;
    }

    [Obsolete("Use SKFont.MeasureText() instead.", true)]
    public float MeasureText(string text) => _legacyFont.MeasureText(text, this);

    [Obsolete("Use SKFont.MeasureText() instead.", true)]
    public float MeasureText(ReadOnlySpan<char> text) => _legacyFont.MeasureText(text, this);

    [Obsolete("Use SKFont.MeasureText() instead.", true)]
    public float MeasureText(byte[] text) => _legacyFont.MeasureText(text, _textEncoding, this);

    [Obsolete("Use SKFont.MeasureText() instead.", true)]
    public float MeasureText(ReadOnlySpan<byte> text) => _legacyFont.MeasureText(text, _textEncoding, this);

    [Obsolete("Use SKFont.MeasureText() instead.", true)]
    public float MeasureText(IntPtr buffer, int length) =>
        _legacyFont.MeasureText(buffer, length, _textEncoding, this);

    [Obsolete("Use SKFont.MeasureText() instead.", true)]
    public float MeasureText(IntPtr buffer, IntPtr length) =>
        _legacyFont.MeasureText(buffer, (int)length, _textEncoding, this);

    [Obsolete("Use SKFont.MeasureText() instead.", true)]
    public float MeasureText(string text, ref SKRect bounds) =>
        _legacyFont.MeasureText(text, out bounds, this);

    [Obsolete("Use SKFont.MeasureText() instead.", true)]
    public float MeasureText(ReadOnlySpan<char> text, ref SKRect bounds) =>
        _legacyFont.MeasureText(text, out bounds, this);

    [Obsolete("Use SKFont.MeasureText() instead.", true)]
    public float MeasureText(byte[] text, ref SKRect bounds) =>
        _legacyFont.MeasureText(text, _textEncoding, out bounds, this);

    [Obsolete("Use SKFont.MeasureText() instead.", true)]
    public float MeasureText(ReadOnlySpan<byte> text, ref SKRect bounds) =>
        _legacyFont.MeasureText(text, _textEncoding, out bounds, this);

    [Obsolete("Use SKFont.MeasureText() instead.", true)]
    public float MeasureText(IntPtr buffer, int length, ref SKRect bounds) =>
        _legacyFont.MeasureText(buffer, length, _textEncoding, out bounds, this);

    [Obsolete("Use SKFont.MeasureText() instead.", true)]
    public float MeasureText(IntPtr buffer, IntPtr length, ref SKRect bounds) =>
        _legacyFont.MeasureText(buffer, (int)length, _textEncoding, out bounds, this);

    [Obsolete("Use SKFont.BreakText() instead.", true)]
    public long BreakText(string text, float maxWidth) =>
        _legacyFont.BreakText(text, maxWidth, this);

    [Obsolete("Use SKFont.BreakText() instead.", true)]
    public long BreakText(string text, float maxWidth, out float measuredWidth) =>
        _legacyFont.BreakText(text, maxWidth, out measuredWidth, this);

    [Obsolete("Use SKFont.BreakText() instead.", true)]
    public long BreakText(
        string text,
        float maxWidth,
        out float measuredWidth,
        out string measuredText)
    {
        ArgumentNullException.ThrowIfNull(text);
        var count = _legacyFont.BreakText(text, maxWidth, out measuredWidth, this);
        measuredText = count switch
        {
            0 => string.Empty,
            _ when count == text.Length => text,
            _ => text[..count],
        };
        return count;
    }

    [Obsolete("Use SKFont.BreakText() instead.", true)]
    public long BreakText(ReadOnlySpan<char> text, float maxWidth) =>
        _legacyFont.BreakText(text, maxWidth, this);

    [Obsolete("Use SKFont.BreakText() instead.", true)]
    public long BreakText(ReadOnlySpan<char> text, float maxWidth, out float measuredWidth) =>
        _legacyFont.BreakText(text, maxWidth, out measuredWidth, this);

    [Obsolete("Use SKFont.BreakText() instead.", true)]
    public long BreakText(byte[] text, float maxWidth) =>
        _legacyFont.BreakText(text, _textEncoding, maxWidth, this);

    [Obsolete("Use SKFont.BreakText() instead.", true)]
    public long BreakText(byte[] text, float maxWidth, out float measuredWidth) =>
        _legacyFont.BreakText(text, _textEncoding, maxWidth, out measuredWidth, this);

    [Obsolete("Use SKFont.BreakText() instead.", true)]
    public long BreakText(ReadOnlySpan<byte> text, float maxWidth) =>
        _legacyFont.BreakText(text, _textEncoding, maxWidth, this);

    [Obsolete("Use SKFont.BreakText() instead.", true)]
    public long BreakText(ReadOnlySpan<byte> text, float maxWidth, out float measuredWidth) =>
        _legacyFont.BreakText(text, _textEncoding, maxWidth, out measuredWidth, this);

    [Obsolete("Use SKFont.BreakText() instead.", true)]
    public long BreakText(IntPtr buffer, int length, float maxWidth) =>
        _legacyFont.BreakText(buffer, length, _textEncoding, maxWidth, this);

    [Obsolete("Use SKFont.BreakText() instead.", true)]
    public long BreakText(IntPtr buffer, int length, float maxWidth, out float measuredWidth) =>
        _legacyFont.BreakText(buffer, length, _textEncoding, maxWidth, out measuredWidth, this);

    [Obsolete("Use SKFont.BreakText() instead.", true)]
    public long BreakText(IntPtr buffer, IntPtr length, float maxWidth) =>
        _legacyFont.BreakText(buffer, (int)length, _textEncoding, maxWidth, this);

    [Obsolete("Use SKFont.BreakText() instead.", true)]
    public long BreakText(
        IntPtr buffer,
        IntPtr length,
        float maxWidth,
        out float measuredWidth) =>
        _legacyFont.BreakText(
            buffer,
            (int)length,
            _textEncoding,
            maxWidth,
            out measuredWidth,
            this);

    [Obsolete("Use SKFont.CountGlyphs() instead.", true)]
    public int CountGlyphs(string text) => _legacyFont.CountGlyphs(text);

    [Obsolete("Use SKFont.CountGlyphs() instead.", true)]
    public int CountGlyphs(ReadOnlySpan<char> text) => _legacyFont.CountGlyphs(text);

    [Obsolete("Use SKFont.CountGlyphs() instead.", true)]
    public int CountGlyphs(byte[] text) => _legacyFont.CountGlyphs(text, _textEncoding);

    [Obsolete("Use SKFont.CountGlyphs() instead.", true)]
    public int CountGlyphs(ReadOnlySpan<byte> text) => _legacyFont.CountGlyphs(text, _textEncoding);

    [Obsolete("Use SKFont.CountGlyphs() instead.", true)]
    public int CountGlyphs(IntPtr text, int length) =>
        _legacyFont.CountGlyphs(text, length, _textEncoding);

    [Obsolete("Use SKFont.CountGlyphs() instead.", true)]
    public int CountGlyphs(IntPtr text, IntPtr length) =>
        _legacyFont.CountGlyphs(text, (int)length, _textEncoding);

    [Obsolete("Use SKFont.GetGlyphs() instead.", true)]
    public ushort[] GetGlyphs(string text) => _legacyFont.GetGlyphs(text);

    [Obsolete("Use SKFont.GetGlyphs() instead.", true)]
    public ushort[] GetGlyphs(ReadOnlySpan<char> text) => _legacyFont.GetGlyphs(text);

    [Obsolete("Use SKFont.GetGlyphs() instead.", true)]
    public ushort[] GetGlyphs(byte[] text) => _legacyFont.GetGlyphs(text, _textEncoding);

    [Obsolete("Use SKFont.GetGlyphs() instead.", true)]
    public ushort[] GetGlyphs(ReadOnlySpan<byte> text) => _legacyFont.GetGlyphs(text, _textEncoding);

    [Obsolete("Use SKFont.GetGlyphs() instead.", true)]
    public ushort[] GetGlyphs(IntPtr text, int length) =>
        _legacyFont.GetGlyphs(text, length, _textEncoding);

    [Obsolete("Use SKFont.GetGlyphs() instead.", true)]
    public ushort[] GetGlyphs(IntPtr text, IntPtr length) =>
        _legacyFont.GetGlyphs(text, (int)length, _textEncoding);

    [Obsolete("Use SKFont.ContainsGlyphs() instead.", true)]
    public bool ContainsGlyphs(string text) => _legacyFont.ContainsGlyphs(text);

    [Obsolete("Use SKFont.ContainsGlyphs() instead.", true)]
    public bool ContainsGlyphs(ReadOnlySpan<char> text) => _legacyFont.ContainsGlyphs(text);

    [Obsolete("Use SKFont.ContainsGlyphs() instead.", true)]
    public bool ContainsGlyphs(byte[] text) => _legacyFont.ContainsGlyphs(text, _textEncoding);

    [Obsolete("Use SKFont.ContainsGlyphs() instead.", true)]
    public bool ContainsGlyphs(ReadOnlySpan<byte> text) =>
        _legacyFont.ContainsGlyphs(text, _textEncoding);

    [Obsolete("Use SKFont.ContainsGlyphs() instead.", true)]
    public bool ContainsGlyphs(IntPtr text, int length) =>
        _legacyFont.ContainsGlyphs(text, length, _textEncoding);

    [Obsolete("Use SKFont.ContainsGlyphs() instead.", true)]
    public bool ContainsGlyphs(IntPtr text, IntPtr length) =>
        _legacyFont.ContainsGlyphs(text, (int)length, _textEncoding);

    [Obsolete("Use SKFont.GetGlyphPositions() instead.", true)]
    public SKPoint[] GetGlyphPositions(string text, SKPoint origin = default) =>
        _legacyFont.GetGlyphPositions(text, origin);

    [Obsolete("Use SKFont.GetGlyphPositions() instead.", true)]
    public SKPoint[] GetGlyphPositions(ReadOnlySpan<char> text, SKPoint origin = default) =>
        _legacyFont.GetGlyphPositions(text, origin);

    [Obsolete("Use SKFont.GetGlyphPositions() instead.", true)]
    public SKPoint[] GetGlyphPositions(ReadOnlySpan<byte> text, SKPoint origin = default) =>
        _legacyFont.GetGlyphPositions(text, _textEncoding, origin);

    [Obsolete("Use SKFont.GetGlyphPositions() instead.", true)]
    public SKPoint[] GetGlyphPositions(IntPtr text, int length, SKPoint origin = default) =>
        _legacyFont.GetGlyphPositions(text, length, _textEncoding, origin);

    [Obsolete("Use SKFont.GetGlyphOffsets() instead.", true)]
    public float[] GetGlyphOffsets(string text, float origin = 0f) =>
        _legacyFont.GetGlyphOffsets(text, origin);

    [Obsolete("Use SKFont.GetGlyphOffsets() instead.", true)]
    public float[] GetGlyphOffsets(ReadOnlySpan<char> text, float origin = 0f) =>
        _legacyFont.GetGlyphOffsets(text, origin);

    [Obsolete("Use SKFont.GetGlyphOffsets() instead.", true)]
    public float[] GetGlyphOffsets(ReadOnlySpan<byte> text, float origin = 0f) =>
        _legacyFont.GetGlyphOffsets(text, _textEncoding, origin);

    [Obsolete("Use SKFont.GetGlyphOffsets() instead.", true)]
    public float[] GetGlyphOffsets(IntPtr text, int length, float origin = 0f) =>
        _legacyFont.GetGlyphOffsets(text, length, _textEncoding, origin);

    [Obsolete("Use SKFont.GetGlyphWidths() instead.", true)]
    public float[] GetGlyphWidths(string text) => _legacyFont.GetGlyphWidths(text, this);

    [Obsolete("Use SKFont.GetGlyphWidths() instead.", true)]
    public float[] GetGlyphWidths(ReadOnlySpan<char> text) =>
        _legacyFont.GetGlyphWidths(text, this);

    [Obsolete("Use SKFont.GetGlyphWidths() instead.", true)]
    public float[] GetGlyphWidths(byte[] text) =>
        _legacyFont.GetGlyphWidths(text, _textEncoding, this);

    [Obsolete("Use SKFont.GetGlyphWidths() instead.", true)]
    public float[] GetGlyphWidths(ReadOnlySpan<byte> text) =>
        _legacyFont.GetGlyphWidths(text, _textEncoding, this);

    [Obsolete("Use SKFont.GetGlyphWidths() instead.", true)]
    public float[] GetGlyphWidths(IntPtr text, int length) =>
        _legacyFont.GetGlyphWidths(text, length, _textEncoding, this);

    [Obsolete("Use SKFont.GetGlyphWidths() instead.", true)]
    public float[] GetGlyphWidths(IntPtr text, IntPtr length) =>
        _legacyFont.GetGlyphWidths(text, (int)length, _textEncoding, this);

    [Obsolete("Use SKFont.GetGlyphWidths() instead.", true)]
    public float[] GetGlyphWidths(string text, out SKRect[] bounds) =>
        _legacyFont.GetGlyphWidths(text, out bounds, this);

    [Obsolete("Use SKFont.GetGlyphWidths() instead.", true)]
    public float[] GetGlyphWidths(ReadOnlySpan<char> text, out SKRect[] bounds) =>
        _legacyFont.GetGlyphWidths(text, out bounds, this);

    [Obsolete("Use SKFont.GetGlyphWidths() instead.", true)]
    public float[] GetGlyphWidths(byte[] text, out SKRect[] bounds) =>
        _legacyFont.GetGlyphWidths(text, _textEncoding, out bounds, this);

    [Obsolete("Use SKFont.GetGlyphWidths() instead.", true)]
    public float[] GetGlyphWidths(ReadOnlySpan<byte> text, out SKRect[] bounds) =>
        _legacyFont.GetGlyphWidths(text, _textEncoding, out bounds, this);

    [Obsolete("Use SKFont.GetGlyphWidths() instead.", true)]
    public float[] GetGlyphWidths(IntPtr text, int length, out SKRect[] bounds) =>
        _legacyFont.GetGlyphWidths(text, length, _textEncoding, out bounds, this);

    [Obsolete("Use SKFont.GetGlyphWidths() instead.", true)]
    public float[] GetGlyphWidths(IntPtr text, IntPtr length, out SKRect[] bounds) =>
        _legacyFont.GetGlyphWidths(text, (int)length, _textEncoding, out bounds, this);

    [Obsolete("Use SKFont.GetTextPath() instead.", true)]
    public SKPath GetTextPath(string text, float x, float y) =>
        _legacyFont.GetTextPath(text, new SKPoint(x, y));

    [Obsolete("Use SKFont.GetTextPath() instead.", true)]
    public SKPath GetTextPath(ReadOnlySpan<char> text, float x, float y) =>
        _legacyFont.GetTextPath(text, new SKPoint(x, y));

    [Obsolete("Use SKFont.GetTextPath() instead.", true)]
    public SKPath GetTextPath(byte[] text, float x, float y) =>
        _legacyFont.GetTextPath(text, _textEncoding, new SKPoint(x, y));

    [Obsolete("Use SKFont.GetTextPath() instead.", true)]
    public SKPath GetTextPath(ReadOnlySpan<byte> text, float x, float y) =>
        _legacyFont.GetTextPath(text, _textEncoding, new SKPoint(x, y));

    [Obsolete("Use SKFont.GetTextPath() instead.", true)]
    public SKPath GetTextPath(IntPtr buffer, int length, float x, float y) =>
        _legacyFont.GetTextPath(buffer, length, _textEncoding, new SKPoint(x, y));

    [Obsolete("Use SKFont.GetTextPath() instead.", true)]
    public SKPath GetTextPath(IntPtr buffer, IntPtr length, float x, float y) =>
        _legacyFont.GetTextPath(buffer, (int)length, _textEncoding, new SKPoint(x, y));

    [Obsolete("Use SKFont.GetTextPath() instead.", true)]
    public SKPath GetTextPath(string text, SKPoint[] points) =>
        _legacyFont.GetTextPath(text, points);

    [Obsolete("Use SKFont.GetTextPath() instead.", true)]
    public SKPath GetTextPath(ReadOnlySpan<char> text, ReadOnlySpan<SKPoint> points) =>
        _legacyFont.GetTextPath(text, points);

    [Obsolete("Use SKFont.GetTextPath() instead.", true)]
    public SKPath GetTextPath(byte[] text, SKPoint[] points) =>
        _legacyFont.GetTextPath(text, _textEncoding, points);

    [Obsolete("Use SKFont.GetTextPath() instead.", true)]
    public SKPath GetTextPath(ReadOnlySpan<byte> text, ReadOnlySpan<SKPoint> points) =>
        _legacyFont.GetTextPath(text, _textEncoding, points);

    [Obsolete("Use SKFont.GetTextPath() instead.", true)]
    public SKPath GetTextPath(IntPtr buffer, int length, SKPoint[] points) =>
        _legacyFont.GetTextPath(buffer, length, _textEncoding, points);

    [Obsolete("Use SKFont.GetTextPath() instead.", true)]
    public SKPath GetTextPath(
        IntPtr buffer,
        int length,
        ReadOnlySpan<SKPoint> points) =>
        _legacyFont.GetTextPath(buffer, length, _textEncoding, points);

    [Obsolete("Use SKFont.GetTextPath() instead.", true)]
    public SKPath GetTextPath(IntPtr buffer, IntPtr length, SKPoint[] points) =>
        _legacyFont.GetTextPath(buffer, (int)length, _textEncoding, points);

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetTextIntercepts(
        string text,
        float x,
        float y,
        float upperBounds,
        float lowerBounds) =>
        GetTextIntercepts(text.AsSpan(), x, y, upperBounds, lowerBounds);

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetTextIntercepts(
        ReadOnlySpan<char> text,
        float x,
        float y,
        float upperBounds,
        float lowerBounds)
    {
        using var blob = CreateTextBlob(text, new SKPoint(x, y));
        return blob.GetIntercepts(upperBounds, lowerBounds, this);
    }

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetTextIntercepts(
        byte[] text,
        float x,
        float y,
        float upperBounds,
        float lowerBounds) =>
        GetTextIntercepts(text.AsSpan(), x, y, upperBounds, lowerBounds);

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetTextIntercepts(
        ReadOnlySpan<byte> text,
        float x,
        float y,
        float upperBounds,
        float lowerBounds)
    {
        using var blob = CreateTextBlob(text, new SKPoint(x, y));
        return blob.GetIntercepts(upperBounds, lowerBounds, this);
    }

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetTextIntercepts(
        IntPtr text,
        int length,
        float x,
        float y,
        float upperBounds,
        float lowerBounds)
    {
        using var blob = CreateTextBlob(text, length, new SKPoint(x, y));
        return blob.GetIntercepts(upperBounds, lowerBounds, this);
    }

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetTextIntercepts(
        IntPtr text,
        IntPtr length,
        float x,
        float y,
        float upperBounds,
        float lowerBounds) =>
        GetTextIntercepts(text, (int)length, x, y, upperBounds, lowerBounds);

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetTextIntercepts(
        SKTextBlob text,
        float upperBounds,
        float lowerBounds)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.GetIntercepts(upperBounds, lowerBounds, this);
    }

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetPositionedTextIntercepts(
        string text,
        SKPoint[] positions,
        float upperBounds,
        float lowerBounds) =>
        GetPositionedTextIntercepts(text.AsSpan(), positions, upperBounds, lowerBounds);

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetPositionedTextIntercepts(
        ReadOnlySpan<char> text,
        ReadOnlySpan<SKPoint> positions,
        float upperBounds,
        float lowerBounds)
    {
        using var blob = CreatePositionedTextBlob(_legacyFont.GetGlyphs(text), positions);
        return blob.GetIntercepts(upperBounds, lowerBounds, this);
    }

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetPositionedTextIntercepts(
        byte[] text,
        SKPoint[] positions,
        float upperBounds,
        float lowerBounds) =>
        GetPositionedTextIntercepts(text.AsSpan(), positions, upperBounds, lowerBounds);

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetPositionedTextIntercepts(
        ReadOnlySpan<byte> text,
        ReadOnlySpan<SKPoint> positions,
        float upperBounds,
        float lowerBounds)
    {
        using var blob = CreatePositionedTextBlob(
            _legacyFont.GetGlyphs(text, _textEncoding),
            positions);
        return blob.GetIntercepts(upperBounds, lowerBounds, this);
    }

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetPositionedTextIntercepts(
        IntPtr text,
        int length,
        SKPoint[] positions,
        float upperBounds,
        float lowerBounds)
    {
        using var blob = CreatePositionedTextBlob(
            _legacyFont.GetGlyphs(text, length, _textEncoding),
            positions);
        return blob.GetIntercepts(upperBounds, lowerBounds, this);
    }

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetPositionedTextIntercepts(
        IntPtr text,
        IntPtr length,
        SKPoint[] positions,
        float upperBounds,
        float lowerBounds) =>
        GetPositionedTextIntercepts(
            text,
            (int)length,
            positions,
            upperBounds,
            lowerBounds);

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetHorizontalTextIntercepts(
        string text,
        float[] xpositions,
        float y,
        float upperBounds,
        float lowerBounds) =>
        GetHorizontalTextIntercepts(text.AsSpan(), xpositions, y, upperBounds, lowerBounds);

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetHorizontalTextIntercepts(
        ReadOnlySpan<char> text,
        ReadOnlySpan<float> xpositions,
        float y,
        float upperBounds,
        float lowerBounds)
    {
        using var blob = CreateHorizontalTextBlob(_legacyFont.GetGlyphs(text), xpositions, y);
        return blob.GetIntercepts(upperBounds, lowerBounds, this);
    }

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetHorizontalTextIntercepts(
        byte[] text,
        float[] xpositions,
        float y,
        float upperBounds,
        float lowerBounds) =>
        GetHorizontalTextIntercepts(text.AsSpan(), xpositions, y, upperBounds, lowerBounds);

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetHorizontalTextIntercepts(
        ReadOnlySpan<byte> text,
        ReadOnlySpan<float> xpositions,
        float y,
        float upperBounds,
        float lowerBounds)
    {
        using var blob = CreateHorizontalTextBlob(
            _legacyFont.GetGlyphs(text, _textEncoding),
            xpositions,
            y);
        return blob.GetIntercepts(upperBounds, lowerBounds, this);
    }

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetHorizontalTextIntercepts(
        IntPtr text,
        int length,
        float[] xpositions,
        float y,
        float upperBounds,
        float lowerBounds)
    {
        using var blob = CreateHorizontalTextBlob(
            _legacyFont.GetGlyphs(text, length, _textEncoding),
            xpositions,
            y);
        return blob.GetIntercepts(upperBounds, lowerBounds, this);
    }

    [Obsolete("Use SKTextBlob.GetIntercepts() instead.", true)]
    public float[] GetHorizontalTextIntercepts(
        IntPtr text,
        IntPtr length,
        float[] xpositions,
        float y,
        float upperBounds,
        float lowerBounds) =>
        GetHorizontalTextIntercepts(
            text,
            (int)length,
            xpositions,
            y,
            upperBounds,
            lowerBounds);

    private SKTextBlob CreateTextBlob(ReadOnlySpan<char> text, SKPoint origin)
    {
        var glyphs = _legacyFont.GetGlyphs(text);
        return new SKTextBlob(_legacyFont, glyphs, _legacyFont.GetGlyphPositions(glyphs, origin));
    }

    private SKTextBlob CreateTextBlob(ReadOnlySpan<byte> text, SKPoint origin)
    {
        var glyphs = _legacyFont.GetGlyphs(text, _textEncoding);
        return new SKTextBlob(_legacyFont, glyphs, _legacyFont.GetGlyphPositions(glyphs, origin));
    }

    private SKTextBlob CreateTextBlob(IntPtr text, int length, SKPoint origin)
    {
        var glyphs = _legacyFont.GetGlyphs(text, length, _textEncoding);
        return new SKTextBlob(_legacyFont, glyphs, _legacyFont.GetGlyphPositions(glyphs, origin));
    }

    private SKTextBlob CreatePositionedTextBlob(
        ushort[] glyphs,
        ReadOnlySpan<SKPoint> positions)
    {
        if (glyphs.Length != positions.Length)
        {
            throw new ArgumentException(
                "The number of glyphs and positions must be the same.",
                nameof(positions));
        }

        return new SKTextBlob(_legacyFont, glyphs, positions.ToArray());
    }

    private SKTextBlob CreateHorizontalTextBlob(
        ushort[] glyphs,
        ReadOnlySpan<float> xpositions,
        float y)
    {
        if (glyphs.Length != xpositions.Length)
        {
            throw new ArgumentException(
                "The number of glyphs and positions must be the same.",
                nameof(xpositions));
        }

        var positions = new SKPoint[xpositions.Length];
        for (var index = 0; index < positions.Length; index++)
        {
            positions[index] = new SKPoint(xpositions[index], y);
        }

        return new SKTextBlob(_legacyFont, glyphs, positions);
    }
}

#pragma warning restore CS0619
