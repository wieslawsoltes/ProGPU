using SkiaSharp;

namespace OfficialBinaryCompatibilityConsumer;

public static class SkiaSharpCompatibilityConsumer
{
    public static string Probe()
    {
        using var paint = new SKPaint
        {
            Color = SKColors.CornflowerBlue
        };

        var identity = typeof(SKCanvas).Assembly.GetName();
        return string.Join(
            '|',
            identity.Name,
            Convert.ToHexString(
                identity.GetPublicKeyToken() ?? []).ToLowerInvariant(),
            paint.Color == SKColors.CornflowerBlue);
    }
}
