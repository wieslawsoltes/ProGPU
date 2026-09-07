namespace ProGPU.Wpf.Interop;

/// <summary>
/// Immutable capture identity shared by cache brushes. Consumer opacity and
/// transforms are deliberately absent. Null cache retains the target/default
/// policy selection, while an explicit cache remains a typed live dependency.
/// </summary>
public sealed class PortableBitmapCacheBrushCaptureSource(object? target, object? bitmapCache)
    : IPortableBitmapCacheBrushSource
{
    public object? Target { get; } = target;
    public object? BitmapCache { get; } = bitmapCache;

    public bool TryGetPortableBitmapCacheBrush(out PortableBitmapCacheBrush brush)
    {
        brush = new PortableBitmapCacheBrush(Target, BitmapCache);
        return true;
    }
}
