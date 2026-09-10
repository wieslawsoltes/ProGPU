// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace System.Drawing.Imaging.Effects;

/// <summary>Specifies which color channels are affected by a color curve.</summary>
public enum CurveChannel
{
    All = 0,
    Red = 1,
    Green = 2,
    Blue = 3
}

/// <summary>Base class for all effects.</summary>
public abstract class Effect : IDisposable
{
    private bool _disposed;

    private protected Effect()
    {
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing) => _disposed = true;

    internal virtual void Apply(Span<byte> pixels, int width, Rectangle area, bool premultiplied)
    {
        ThrowIfDisposed();

        for (int y = area.Top; y < area.Bottom; y++)
        {
            int offset = ((y * width) + area.Left) * 4;
            for (int x = area.Left; x < area.Right; x++, offset += 4)
            {
                byte alpha = pixels[offset + 3];
                byte red = pixels[offset];
                byte green = pixels[offset + 1];
                byte blue = pixels[offset + 2];
                if (premultiplied)
                {
                    red = Unpremultiply(red, alpha);
                    green = Unpremultiply(green, alpha);
                    blue = Unpremultiply(blue, alpha);
                }

                uint transformed = Transform((uint)(alpha << 24 | red << 16 | green << 8 | blue));
                alpha = (byte)(transformed >> 24);
                red = (byte)(transformed >> 16);
                green = (byte)(transformed >> 8);
                blue = (byte)transformed;
                if (premultiplied)
                {
                    red = Premultiply(red, alpha);
                    green = Premultiply(green, alpha);
                    blue = Premultiply(blue, alpha);
                }

                pixels[offset] = red;
                pixels[offset + 1] = green;
                pixels[offset + 2] = blue;
                pixels[offset + 3] = alpha;
            }
        }
    }

    internal virtual uint Transform(uint argb) => argb;

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static byte Premultiply(byte value, byte alpha) =>
        (byte)((value * alpha + 127) / 255);

    private static byte Unpremultiply(byte value, byte alpha) =>
        alpha == 0 ? (byte)0 : (byte)Math.Min(255, (value * 255 + alpha / 2) / alpha);

    internal static byte Clamp(float value)
    {
        if (!(value > 0f))
        {
            return 0;
        }

        return value >= 255f ? (byte)255 : (byte)MathF.Round(value);
    }

    internal static void ValidateRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentException($"Value must be in the range {minimum} through {maximum}.", name);
        }
    }

    internal static void ValidateRange(float value, float minimum, float maximum, string name)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentException($"Value must be finite and in the range {minimum} through {maximum}.", name);
        }
    }
}

internal enum CurveAdjustment
{
    BlackSaturation,
    WhiteSaturation,
    Contrast,
    Density,
    Exposure,
    Highlight,
    Midtone,
    Shadow
}

/// <summary>Base class for effects that adjust one or more color channels.</summary>
public abstract class ColorCurveEffect : Effect
{
    private readonly CurveAdjustment _adjustment;

    private protected ColorCurveEffect(CurveAdjustment adjustment, CurveChannel channel, int adjustValue)
    {
        if ((uint)channel > (uint)CurveChannel.Blue)
        {
            throw new ArgumentException("The curve channel is invalid.", nameof(channel));
        }

        _adjustment = adjustment;
        Channel = channel;
        AdjustValue = adjustValue;
    }

    public CurveChannel Channel { get; }

    private protected int AdjustValue { get; }

    internal override uint Transform(uint argb)
    {
        byte alpha = (byte)(argb >> 24);
        byte red = (byte)(argb >> 16);
        byte green = (byte)(argb >> 8);
        byte blue = (byte)argb;

        if (Channel is CurveChannel.All or CurveChannel.Red) red = Adjust(red);
        if (Channel is CurveChannel.All or CurveChannel.Green) green = Adjust(green);
        if (Channel is CurveChannel.All or CurveChannel.Blue) blue = Adjust(blue);
        return (uint)(alpha << 24 | red << 16 | green << 8 | blue);
    }

    private byte Adjust(byte value)
    {
        float normalized = value / 255f;
        float adjusted = _adjustment switch
        {
            CurveAdjustment.BlackSaturation => MathF.Max(0f, value - AdjustValue) * 255f / (255f - AdjustValue),
            CurveAdjustment.WhiteSaturation => MathF.Min(255f, value * 255f / AdjustValue),
            CurveAdjustment.Contrast => (normalized - 0.5f) * ContrastFactor(AdjustValue) * 255f + 127.5f,
            CurveAdjustment.Density => value + AdjustValue,
            CurveAdjustment.Exposure => value * MathF.Pow(2f, AdjustValue / 256f),
            CurveAdjustment.Highlight => value + (AdjustValue / 100f) * 255f * normalized * normalized,
            CurveAdjustment.Midtone => MathF.Pow(normalized, MathF.Pow(2f, -AdjustValue / 100f)) * 255f,
            CurveAdjustment.Shadow => value + (AdjustValue / 100f) * 255f * (1f - normalized) * (1f - normalized),
            _ => value
        };
        return Clamp(adjusted);
    }

    private static float ContrastFactor(int contrast) =>
        contrast >= 100 ? 255f : (100f + contrast) / (100f - contrast);
}

public class BlackSaturationCurveEffect : ColorCurveEffect
{
    public BlackSaturationCurveEffect(CurveChannel channel, int blackSaturation)
        : base(CurveAdjustment.BlackSaturation, channel, Validate(blackSaturation)) { }
    public int BlackSaturation => AdjustValue;
    private static int Validate(int value) { ValidateRange(value, 0, 254, nameof(value)); return value; }
}

public class WhiteSaturationCurveEffect : ColorCurveEffect
{
    public WhiteSaturationCurveEffect(CurveChannel channel, int whiteSaturation)
        : base(CurveAdjustment.WhiteSaturation, channel, Validate(whiteSaturation)) { }
    public int WhiteSaturation => AdjustValue;
    private static int Validate(int value) { ValidateRange(value, 1, 255, nameof(value)); return value; }
}

public class ContrastCurveEffect : ColorCurveEffect
{
    public ContrastCurveEffect(CurveChannel channel, int contrast)
        : base(CurveAdjustment.Contrast, channel, Validate(contrast)) { }
    public int Contrast => AdjustValue;
    private static int Validate(int value) { ValidateRange(value, -100, 100, nameof(value)); return value; }
}

public class DensityCurveEffect : ColorCurveEffect
{
    public DensityCurveEffect(CurveChannel channel, int density)
        : base(CurveAdjustment.Density, channel, Validate(density)) { }
    public int Density => AdjustValue;
    private static int Validate(int value) { ValidateRange(value, -256, 256, nameof(value)); return value; }
}

public class ExposureCurveEffect : ColorCurveEffect
{
    public ExposureCurveEffect(CurveChannel channel, int exposure)
        : base(CurveAdjustment.Exposure, channel, Validate(exposure)) { }
    public int Exposure => AdjustValue;
    private static int Validate(int value) { ValidateRange(value, -256, 256, nameof(value)); return value; }
}

public class HighlightCurveEffect : ColorCurveEffect
{
    public HighlightCurveEffect(CurveChannel channel, int highlight)
        : base(CurveAdjustment.Highlight, channel, Validate(highlight)) { }
    public int Highlight => AdjustValue;
    private static int Validate(int value) { ValidateRange(value, -100, 100, nameof(value)); return value; }
}

public class MidtoneCurveEffect : ColorCurveEffect
{
    public MidtoneCurveEffect(CurveChannel channel, int midtone)
        : base(CurveAdjustment.Midtone, channel, Validate(midtone)) { }
    public int Midtone => AdjustValue;
    private static int Validate(int value) { ValidateRange(value, -100, 100, nameof(value)); return value; }
}

public class ShadowCurveEffect : ColorCurveEffect
{
    public ShadowCurveEffect(CurveChannel channel, int shadow)
        : base(CurveAdjustment.Shadow, channel, Validate(shadow)) { }
    public int Shadow => AdjustValue;
    private static int Validate(int value) { ValidateRange(value, -100, 100, nameof(value)); return value; }
}

public class ColorMatrixEffect : Effect
{
    private readonly float[] _values = new float[25];

    public ColorMatrixEffect(ColorMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        Matrix = matrix;
        for (int row = 0; row < 5; row++)
        {
            for (int column = 0; column < 5; column++)
            {
                _values[row * 5 + column] = matrix[row, column];
            }
        }
    }

    public ColorMatrix Matrix { get; }

    internal override uint Transform(uint argb)
    {
        float a = (byte)(argb >> 24) / 255f;
        float r = (byte)(argb >> 16) / 255f;
        float g = (byte)(argb >> 8) / 255f;
        float b = (byte)argb / 255f;
        byte red = Clamp((r * _values[0] + g * _values[5] + b * _values[10] + a * _values[15] + _values[20]) * 255f);
        byte green = Clamp((r * _values[1] + g * _values[6] + b * _values[11] + a * _values[16] + _values[21]) * 255f);
        byte blue = Clamp((r * _values[2] + g * _values[7] + b * _values[12] + a * _values[17] + _values[22]) * 255f);
        byte alpha = Clamp((r * _values[3] + g * _values[8] + b * _values[13] + a * _values[18] + _values[23]) * 255f);
        return (uint)(alpha << 24 | red << 16 | green << 8 | blue);
    }
}

public sealed class GrayScaleEffect : ColorMatrixEffect
{
    public GrayScaleEffect() : base(new([
        0.299f, 0.299f, 0.299f, 0, 0, 0.587f, 0.587f, 0.587f, 0, 0,
        0.114f, 0.114f, 0.114f, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1])) { }
}

public sealed class SepiaEffect : ColorMatrixEffect
{
    public SepiaEffect() : base(new([
        0.393f, 0.349f, 0.272f, 0, 0, 0.769f, 0.686f, 0.534f, 0, 0,
        0.189f, 0.168f, 0.131f, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1])) { }
}

public sealed class InvertEffect : ColorMatrixEffect
{
    public InvertEffect() : base(new([
        -1f, 0, 0, 0, 0, 0, -1f, 0, 0, 0, 0, 0, -1f, 0, 0,
        0, 0, 0, 1, 0, 1, 1, 1, 1, 1])) { }
}

public sealed class VividEffect : ColorMatrixEffect
{
    public VividEffect() : base(new([
        1.2f, -0.1f, -0.1f, 0, 0, -0.1f, 1.2f, -0.1f, 0, 0,
        -0.1f, -0.1f, 1.2f, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1])) { }
}

public class ColorLookupTableEffect : Effect
{
    private readonly byte[] _bytes = new byte[1024];

    public ColorLookupTableEffect(byte[] redLookupTable, byte[] greenLookupTable, byte[] blueLookupTable, byte[] alphaLookupTable)
        : this(redLookupTable.AsSpan(), greenLookupTable, blueLookupTable, alphaLookupTable) { }

    public ColorLookupTableEffect(ReadOnlySpan<byte> redLookupTable, ReadOnlySpan<byte> greenLookupTable, ReadOnlySpan<byte> blueLookupTable, ReadOnlySpan<byte> alphaLookupTable)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(redLookupTable.Length, 256, nameof(redLookupTable));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(greenLookupTable.Length, 256, nameof(greenLookupTable));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blueLookupTable.Length, 256, nameof(blueLookupTable));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(alphaLookupTable.Length, 256, nameof(alphaLookupTable));
        blueLookupTable.CopyTo(_bytes);
        greenLookupTable.CopyTo(_bytes.AsSpan(256));
        redLookupTable.CopyTo(_bytes.AsSpan(512));
        alphaLookupTable.CopyTo(_bytes.AsSpan(768));
    }

    public ReadOnlyMemory<byte> BlueLookupTable => new(_bytes, 0, 256);
    public ReadOnlyMemory<byte> GreenLookupTable => new(_bytes, 256, 256);
    public ReadOnlyMemory<byte> RedLookupTable => new(_bytes, 512, 256);
    public ReadOnlyMemory<byte> AlphaLookupTable => new(_bytes, 768, 256);

    internal override uint Transform(uint argb) => (uint)(
        _bytes[768 + (byte)(argb >> 24)] << 24 |
        _bytes[512 + (byte)(argb >> 16)] << 16 |
        _bytes[256 + (byte)(argb >> 8)] << 8 |
        _bytes[(byte)argb]);
}

public class BrightnessContrastEffect : Effect
{
    public BrightnessContrastEffect(int brightnessLevel, int contrastLevel)
    {
        ValidateRange(brightnessLevel, -255, 255, nameof(brightnessLevel));
        ValidateRange(contrastLevel, -100, 100, nameof(contrastLevel));
        BrightnessLevel = brightnessLevel;
        ContrastLevel = contrastLevel;
    }
    public int BrightnessLevel { get; }
    public int ContrastLevel { get; }
    internal override uint Transform(uint argb)
    {
        float factor = ContrastLevel >= 100 ? 255f : (100f + ContrastLevel) / (100f - ContrastLevel);
        byte Adjust(byte value) => Clamp((value - 127.5f) * factor + 127.5f + BrightnessLevel);
        return (argb & 0xff000000u) | (uint)(Adjust((byte)(argb >> 16)) << 16 | Adjust((byte)(argb >> 8)) << 8 | Adjust((byte)argb));
    }
}

public class ColorBalanceEffect : Effect
{
    public ColorBalanceEffect(int cyanRed, int magentaGreen, int yellowBlue)
    {
        ValidateRange(cyanRed, -100, 100, nameof(cyanRed));
        ValidateRange(magentaGreen, -100, 100, nameof(magentaGreen));
        ValidateRange(yellowBlue, -100, 100, nameof(yellowBlue));
        CyanRed = cyanRed; MagentaGreen = magentaGreen; YellowBlue = yellowBlue;
    }
    public int CyanRed { get; }
    public int MagentaGreen { get; }
    public int YellowBlue { get; }
    internal override uint Transform(uint argb) => (argb & 0xff000000u) | (uint)(
        Clamp((byte)(argb >> 16) + CyanRed * 2.55f) << 16 |
        Clamp((byte)(argb >> 8) + MagentaGreen * 2.55f) << 8 |
        Clamp((byte)argb + YellowBlue * 2.55f));
}

public class LevelsEffect : Effect
{
    public LevelsEffect(int highlight, int midtone, int shadow)
    {
        ValidateRange(highlight, 0, 100, nameof(highlight));
        ValidateRange(midtone, -100, 100, nameof(midtone));
        ValidateRange(shadow, 0, 100, nameof(shadow));
        Highlight = highlight; Midtone = midtone; Shadow = shadow;
    }
    public int Highlight { get; }
    public int Midtone { get; }
    public int Shadow { get; }
    internal override uint Transform(uint argb)
    {
        byte Adjust(byte value)
        {
            float low = Shadow * 2.55f;
            float high = Highlight * 2.55f;
            float normalized = high <= low ? (value >= high ? 1f : 0f) : Math.Clamp((value - low) / (high - low), 0f, 1f);
            return Clamp(MathF.Pow(normalized, MathF.Pow(2f, -Midtone / 100f)) * 255f);
        }
        return (argb & 0xff000000u) | (uint)(Adjust((byte)(argb >> 16)) << 16 | Adjust((byte)(argb >> 8)) << 8 | Adjust((byte)argb));
    }
}

public class TintEffect : Effect
{
    public TintEffect(Color color, int amount) : this((int)color.GetHue(), color.IsEmpty || color == Color.White ? 0 : amount) { }
    public TintEffect(int hue, int amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hue, 0, nameof(hue));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hue, 360, nameof(hue));
        ValidateRange(amount, -100, 100, nameof(amount));
        Hue = hue;
        Amount = amount;
    }
    public int Hue { get; }
    public int Amount { get; }
    internal override uint Transform(uint argb)
    {
        float c = 1f;
        float x = 1f - MathF.Abs((Hue / 60f) % 2f - 1f);
        (float tr, float tg, float tb) = Hue switch
        {
            < 60 => (c, x, 0f), < 120 => (x, c, 0f), < 180 => (0f, c, x),
            < 240 => (0f, x, c), < 300 => (x, 0f, c), _ => (c, 0f, x)
        };
        float blend = Amount / 100f;
        if (blend < 0f) { tr = 1f - tr; tg = 1f - tg; tb = 1f - tb; blend = -blend; }
        byte Mix(byte value, float target) => Clamp(value + (target * 255f - value) * blend);
        return (argb & 0xff000000u) | (uint)(Mix((byte)(argb >> 16), tr) << 16 | Mix((byte)(argb >> 8), tg) << 8 | Mix((byte)argb, tb));
    }
}

public class BlurEffect : Effect
{
    public BlurEffect(float radius, bool expandEdge)
    {
        ValidateRange(radius, 0, 256, nameof(radius));
        Radius = radius; ExpandEdge = expandEdge;
    }
    public float Radius { get; }
    public bool ExpandEdge { get; }
    internal override void Apply(Span<byte> pixels, int width, Rectangle area, bool premultiplied)
    {
        ThrowIfDisposed();
        Convolution.Apply(pixels, width, area, Radius, ExpandEdge, sharpenAmount: -1f);
    }
}

public class SharpenEffect : Effect
{
    public SharpenEffect(float radius, float amount)
    {
        ValidateRange(radius, 0, 256, nameof(radius));
        ValidateRange(amount, 0, 100, nameof(amount));
        Radius = radius; Amount = amount;
    }
    public float Radius { get; }
    public float Amount { get; }
    internal override void Apply(Span<byte> pixels, int width, Rectangle area, bool premultiplied)
    {
        ThrowIfDisposed();
        Convolution.Apply(pixels, width, area, Radius, expandEdge: true, sharpenAmount: Amount / 100f);
    }
}

internal static class Convolution
{
    internal static void Apply(Span<byte> pixels, int bitmapWidth, Rectangle area, float radius, bool expandEdge, float sharpenAmount)
    {
        int kernelRadius = Math.Min(256, (int)MathF.Ceiling(radius));
        if (kernelRadius == 0 || sharpenAmount == 0f) return;
        int width = area.Width;
        int height = area.Height;
        int length = checked(width * height * 4);
        byte[] sourceArray = ArrayPool<byte>.Shared.Rent(length);
        byte[] horizontalArray = ArrayPool<byte>.Shared.Rent(length);
        byte[] blurredArray = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Span<byte> source = sourceArray.AsSpan(0, length);
            Span<byte> horizontal = horizontalArray.AsSpan(0, length);
            Span<byte> blurred = blurredArray.AsSpan(0, length);
            for (int y = 0; y < height; y++)
                pixels.Slice(((area.Top + y) * bitmapWidth + area.Left) * 4, width * 4).CopyTo(source.Slice(y * width * 4, width * 4));

            BoxPass(source, horizontal, width, height, kernelRadius, horizontalPass: true, expandEdge);
            BoxPass(horizontal, blurred, width, height, kernelRadius, horizontalPass: false, expandEdge);

            for (int y = 0; y < height; y++)
            {
                Span<byte> destinationRow = pixels.Slice(((area.Top + y) * bitmapWidth + area.Left) * 4, width * 4);
                int rowOffset = y * width * 4;
                for (int i = 0; i < width * 4; i++)
                {
                    float value = sharpenAmount < 0f
                        ? blurred[rowOffset + i]
                        : source[rowOffset + i] + (source[rowOffset + i] - blurred[rowOffset + i]) * sharpenAmount;
                    destinationRow[i] = Effect.Clamp(value);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceArray);
            ArrayPool<byte>.Shared.Return(horizontalArray);
            ArrayPool<byte>.Shared.Return(blurredArray);
        }
    }

    private static void BoxPass(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height, int radius, bool horizontalPass, bool expandEdge)
    {
        int major = horizontalPass ? height : width;
        int minor = horizontalPass ? width : height;
        int divisor = radius * 2 + 1;
        for (int line = 0; line < major; line++)
        {
            Span<int> sums = stackalloc int[4];
            for (int sample = -radius; sample <= radius; sample++)
            {
                AddSample(source, sums, width, minor, line, sample, horizontalPass, expandEdge, 1);
            }
            for (int position = 0; position < minor; position++)
            {
                int destinationPixel = horizontalPass ? line * width + position : position * width + line;
                int offset = destinationPixel * 4;
                destination[offset] = (byte)((sums[0] + divisor / 2) / divisor);
                destination[offset + 1] = (byte)((sums[1] + divisor / 2) / divisor);
                destination[offset + 2] = (byte)((sums[2] + divisor / 2) / divisor);
                destination[offset + 3] = (byte)((sums[3] + divisor / 2) / divisor);
                AddSample(source, sums, width, minor, line, position - radius, horizontalPass, expandEdge, -1);
                AddSample(source, sums, width, minor, line, position + radius + 1, horizontalPass, expandEdge, 1);
            }
        }
    }

    private static void AddSample(
        ReadOnlySpan<byte> source,
        Span<int> sums,
        int width,
        int minor,
        int line,
        int position,
        bool horizontalPass,
        bool expandEdge,
        int direction)
    {
        if (expandEdge) position = Math.Clamp(position, 0, minor - 1);
        else if ((uint)position >= (uint)minor) return;
        int pixel = horizontalPass ? line * width + position : position * width + line;
        int offset = pixel * 4;
        sums[0] += source[offset] * direction;
        sums[1] += source[offset + 1] * direction;
        sums[2] += source[offset + 2] * direction;
        sums[3] += source[offset + 3] * direction;
    }
}
