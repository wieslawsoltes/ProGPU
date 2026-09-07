using System;
using System.Numerics;

namespace ProGPU.Wpf.Interop;

/// <summary>Shared typed explicit/target/default cache selection for brush capture.</summary>
public static class PortableBitmapCacheBrushPolicy
{
    /// <summary>
    /// Resolves brush mapping over the consumer bounds, without TileBrush
    /// stretching or tiling. Relative mapping precedes the absolute transform.
    /// </summary>
    public static bool TryGetMapping(PortableBitmapCacheBrush brush, PortableRect bounds, out Matrix4x4 mapping)
    {
        mapping = Matrix4x4.Identity;
        if (bounds.IsEmpty || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
            || bounds.Width <= 0 || bounds.Height <= 0) return false;
        double a = 1, b = 0, c = 0, d = 1, x = 0, y = 0;
        if (brush.HasRelativeTransform)
        {
            var r = brush.RelativeTransform;
            a = r.M11;
            b = r.M12 * (bounds.Height / bounds.Width);
            c = r.M21 * (bounds.Width / bounds.Height);
            d = r.M22;
            x = bounds.X + r.OffsetX * bounds.Width - bounds.X * a - bounds.Y * c;
            y = bounds.Y + r.OffsetY * bounds.Height - bounds.X * b - bounds.Y * d;
        }
        if (brush.HasTransform)
        {
            var t = brush.Transform;
            (a, b, c, d, x, y) = (a * t.M11 + b * t.M21, a * t.M12 + b * t.M22,
                c * t.M11 + d * t.M21, c * t.M12 + d * t.M22,
                x * t.M11 + y * t.M21 + t.OffsetX, x * t.M12 + y * t.M22 + t.OffsetY);
        }
        if (!float.IsFinite((float)a) || !float.IsFinite((float)b) || !float.IsFinite((float)c)
            || !float.IsFinite((float)d) || !float.IsFinite((float)x) || !float.IsFinite((float)y)) return false;
        mapping = new Matrix4x4((float)a, (float)b, 0, 0, (float)c, (float)d, 0, 0,
            0, 0, 1, 0, (float)x, (float)y, 0, 1);
        return true;
    }

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
