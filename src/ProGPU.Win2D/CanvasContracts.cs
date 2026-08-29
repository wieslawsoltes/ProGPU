using Windows.Graphics.DirectX;

namespace Microsoft.Graphics.Canvas;

public enum CanvasAlphaMode
{
    Premultiplied = 0,
    Straight = 1,
    Ignore = 2
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
