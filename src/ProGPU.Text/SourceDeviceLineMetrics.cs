using System.Diagnostics.CodeAnalysis;
using ProGPU.Text.Shaping;

namespace ProGPU.Text;

/// <summary>
/// An explicit horizontal source line frame borrowed from one immutable font's
/// exact original VDMX record. It does not change shaping or ordinary font metrics.
/// </summary>
public sealed class SourceDeviceLineMetrics
{
    private SourceDeviceLineMetrics(TtfFont font, FontVerticalDeviceMetrics metrics)
    {
        Font = font;
        Metrics = metrics;
    }

    public TtfFont Font { get; }
    public FontVerticalDeviceMetrics Metrics { get; }
    public int Height => Metrics.Height;
    public int Ascent => Metrics.MaximumY;

    /// <summary>
    /// Captures an exact static square-pixel source record. Missing metadata or
    /// a nonpositive line height returns false; malformed metadata still throws.
    /// No nearest size, scaling, caller-supplied extrema or line-height rounding.
    /// </summary>
    public static bool TryCreate(TtfFont font, ushort pixelsPerEm,
        [NotNullWhen(true)] out SourceDeviceLineMetrics? result)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentOutOfRangeException.ThrowIfZero(pixelsPerEm);
        result = null;
        if (!font.TryGetVerticalDeviceMetrics(pixelsPerEm, out var metrics) || metrics.Height <= 0)
            return false;
        result = new SourceDeviceLineMetrics(font, metrics);
        return true;
    }

    internal void Validate(TtfFont font, float fontSize, ShapingDirection direction)
    {
        if (!ReferenceEquals(Font, font) || fontSize != Metrics.PixelsPerEm ||
            direction is not (ShapingDirection.Unspecified or ShapingDirection.LeftToRight or ShapingDirection.RightToLeft))
            throw new ArgumentException("Source line metrics require their exact font, pixel em size and horizontal direction.");
    }
}
