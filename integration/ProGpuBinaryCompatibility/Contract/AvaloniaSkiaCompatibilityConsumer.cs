using Avalonia.Skia;

namespace OfficialBinaryCompatibilityConsumer;

public static class AvaloniaSkiaCompatibilityConsumer
{
    public static string Probe()
    {
        var owner = typeof(ISkiaSharpApiLeaseFeature).Assembly.GetName();
        var leaseMethod = typeof(ISkiaSharpApiLeaseFeature).GetMethod(
            nameof(ISkiaSharpApiLeaseFeature.Lease));

        return string.Join(
            '|',
            owner.Name,
            leaseMethod?.ReturnType == typeof(ISkiaSharpApiLease));
    }
}
