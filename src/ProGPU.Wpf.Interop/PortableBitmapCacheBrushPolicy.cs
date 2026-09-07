using System;

namespace ProGPU.Wpf.Interop;

/// <summary>Shared typed explicit/target/default cache selection for brush capture.</summary>
public static class PortableBitmapCacheBrushPolicy
{
    public static bool TryResolve(PortableBitmapCacheBrush brush, out PortableBitmapCache policy)
    {
        policy = new PortableBitmapCache(1, false, false);
        object? cache = brush.BitmapCache;
        if (cache == null && brush.InternalTarget is object target)
        {
            if (target is not IPortableVisualStateSource source ||
                !source.TryGetPortableVisualState(out var state))
                return false;
            if (state.HasCacheMode)
            {
                cache = state.CacheMode;
                if (cache == null) return false;
            }
        }
        if (cache == null) return true;
        if (cache is not IPortableBitmapCacheSource cacheSource ||
            !cacheSource.TryGetPortableBitmapCache(out var descriptor) ||
            !double.IsFinite(descriptor.RenderAtScale))
            return false;
        policy = descriptor with
        {
            RenderAtScale = Math.Max(0, descriptor.RenderAtScale),
            SnapsToDevicePixels = false
        };
        return true;
    }
}
