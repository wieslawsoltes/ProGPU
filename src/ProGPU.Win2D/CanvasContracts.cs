using System.Numerics;
using Windows.Graphics.DirectX;

namespace Microsoft.Graphics.Canvas;

public enum CanvasAlphaMode
{
    Premultiplied = 0,
    Straight = 1,
    Ignore = 2
}

public enum CanvasBufferPrecision
{
    Precision8UIntNormalized = 0,
    Precision8UIntNormalizedSrgb = 1,
    Precision16UIntNormalized = 2,
    Precision16Float = 3,
    Precision32Float = 4
}

public enum CanvasColorSpace
{
    Custom = 0,
    Srgb = 1,
    ScRgb = 2
}

public enum CanvasEdgeBehavior
{
    Clamp = 0,
    Wrap = 1,
    Mirror = 2
}

public enum CanvasDpiRounding
{
    Floor = 0,
    Round = 1,
    Ceiling = 2
}

public interface ICanvasResourceCreator
{
    CanvasDevice Device { get; }
}

public interface ICanvasResourceCreatorWithDpi : ICanvasResourceCreator
{
    float Dpi { get; }

    float ConvertPixelsToDips(int pixels);

    int ConvertDipsToPixels(float dips, CanvasDpiRounding dpiRounding);
}

public interface ICanvasImage
{
    Windows.Foundation.Rect GetBounds(
        ICanvasResourceCreator resourceCreator);

    Windows.Foundation.Rect GetBounds(
        ICanvasResourceCreator resourceCreator,
        Matrix3x2 transform);
}

public enum CanvasImageInterpolation
{
    NearestNeighbor = 0,
    Linear = 1,
    Cubic = 2,
    MultiSampleLinear = 3,
    Anisotropic = 4,
    HighQualityCubic = 5
}

public enum ProGpuCanvasExecutionPath
{
    NativeCppWebGpu = 0
}

public enum ProGpuCanvasCpuConversionMode
{
    Automatic = 0,
    IntrinsicSimd = 1,
    ScalarReference = 2
}

public enum ProGpuCanvasCpuConversionPath
{
    None = 0,
    Vector256 = 1,
    Vector128 = 2,
    ScalarReference = 3
}

public readonly record struct ProGpuCanvasRenderMetrics(
    ProGpuCanvasExecutionPath ExecutionPath,
    int SourceCommandCount,
    int NativeCommandCount,
    int NativeDrawCount,
    ulong SubmissionCount,
    uint DrawCallCount,
    ulong PayloadHash);

internal static class CanvasContract
{
    public const float DefaultDpi = 96f;
    public const int MaximumBitmapSizeInPixels = 16_384;

    public static void ValidateImageResourceCreator(
        ICanvasResourceCreator resourceCreator,
        CanvasDevice requiredDevice)
    {
        ArgumentNullException.ThrowIfNull(resourceCreator);
        CanvasDevice device = resourceCreator.Device ??
            throw new ArgumentException(
                "The resource creator did not provide a CanvasDevice.",
                nameof(resourceCreator));
        if (device.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(resourceCreator));
        }
        if (!ReferenceEquals(device, requiredDevice))
        {
            throw new ArgumentException(
                "Canvas image bounds must be queried with their creation device.",
                nameof(resourceCreator));
        }
    }

    public static Windows.Foundation.Rect TransformBounds(
        Windows.Foundation.Rect bounds,
        Matrix3x2 transform)
    {
        if (!IsFinite(transform))
        {
            throw new ArgumentOutOfRangeException(nameof(transform));
        }

        Vector2 topLeft = Vector2.Transform(
            new Vector2((float)bounds.X, (float)bounds.Y),
            transform);
        Vector2 topRight = Vector2.Transform(
            new Vector2(
                (float)(bounds.X + bounds.Width),
                (float)bounds.Y),
            transform);
        Vector2 bottomLeft = Vector2.Transform(
            new Vector2(
                (float)bounds.X,
                (float)(bounds.Y + bounds.Height)),
            transform);
        Vector2 bottomRight = Vector2.Transform(
            new Vector2(
                (float)(bounds.X + bounds.Width),
                (float)(bounds.Y + bounds.Height)),
            transform);
        Vector2 minimum = Vector2.Min(
            Vector2.Min(topLeft, topRight),
            Vector2.Min(bottomLeft, bottomRight));
        Vector2 maximum = Vector2.Max(
            Vector2.Max(topLeft, topRight),
            Vector2.Max(bottomLeft, bottomRight));
        return new Windows.Foundation.Rect(
            minimum.X,
            minimum.Y,
            maximum.X - minimum.X,
            maximum.Y - minimum.Y);
    }

    public static bool IsFinite(in Matrix3x2 value) =>
        float.IsFinite(value.M11) &&
        float.IsFinite(value.M12) &&
        float.IsFinite(value.M21) &&
        float.IsFinite(value.M22) &&
        float.IsFinite(value.M31) &&
        float.IsFinite(value.M32);

    public static void ValidateDpi(float dpi)
    {
        if (!float.IsFinite(dpi) || dpi <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi));
        }
    }

    public static int DipsToPixels(
        float dips,
        float dpi,
        CanvasDpiRounding rounding)
    {
        ValidateDpi(dpi);
        if (!float.IsFinite(dips))
        {
            throw new ArgumentOutOfRangeException(nameof(dips));
        }

        float scaled = dips * dpi / DefaultDpi;
        float rounded = rounding switch
        {
            CanvasDpiRounding.Floor => MathF.Floor(scaled),
            CanvasDpiRounding.Round => MathF.Round(scaled,
                MidpointRounding.AwayFromZero),
            CanvasDpiRounding.Ceiling => MathF.Ceiling(scaled),
            _ => throw new ArgumentOutOfRangeException(nameof(rounding))
        };
        if (rounded < int.MinValue || rounded > int.MaxValue)
        {
            throw new OverflowException(
                "The DIP value cannot be represented as a pixel count.");
        }

        return (int)rounded;
    }

    public static uint SizeDipsToPixels(float dips, float dpi)
    {
        if (!float.IsFinite(dips) || dips <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(dips));
        }

        int pixels = DipsToPixels(dips, dpi, CanvasDpiRounding.Round);
        pixels = pixels == 0 ? 1 : pixels;
        if (pixels < 0 || pixels > MaximumBitmapSizeInPixels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dips),
                $"Canvas dimensions cannot exceed {MaximumBitmapSizeInPixels} pixels.");
        }

        return (uint)pixels;
    }

    public static DirectXPixelFormat ValidateFormat(
        DirectXPixelFormat format)
    {
        if (format != DirectXPixelFormat.B8G8R8A8UIntNormalized)
        {
            throw new NotSupportedException(
                "The first portable Canvas render-target lane supports only B8G8R8A8UIntNormalized.");
        }

        return format;
    }

    public static CanvasAlphaMode ValidateAlphaMode(CanvasAlphaMode alphaMode)
    {
        if (alphaMode != CanvasAlphaMode.Premultiplied)
        {
            throw new NotSupportedException(
                "The first portable Canvas render-target lane supports only premultiplied alpha.");
        }

        return alphaMode;
    }
}
