using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static partial class Program
{
    private static List<object> CaptureBitmapCacheReentryControls(string directory, bool unavailable,
        uint systemDpi, Stopwatch timer, List<string> failures)
    {
        var controls = new List<object>();
        var state = new BitmapCacheSamplerState(17, "cache-sampler-refilled-drawing-image");
        foreach (BitmapCacheReentryControl control in Enum.GetValues<BitmapCacheReentryControl>())
        {
            string name = "cache-reentry-" + control;
            var retained = new OriginalBitmapCacheSamplerScene(state, control);
            byte[]? first = null;
            var replays = new List<object>();
            for (int replay = 0; replay < 3; replay++)
            {
                CheckSamplerAnimationDeadline(timer);
                object retainedBefore = retained.Describe(state);
                var current = replay < 2 ? retained : new OriginalBitmapCacheSamplerScene(state, control);
                object before = current.Describe(state);
                var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(current.Root);
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                if (Volatile.Read(ref invalidShaders) != 0 || ObserveCacheSamplerSystemDpi() != systemDpi)
                    throw new InvalidOperationException("Original cache re-entry shader or system DPI changed.");
                object after = current.Describe(state);
                object retainedAfter = retained.Describe(state);
                var pixels = new byte[64 * 64 * 4];
                bitmap.CopyPixels(pixels, 64 * 4, 0);
                SaveSamplerBitmap(directory, name + $".replay-{replay}", bitmap, pixels);
                try { AssertBitmapCacheReentryPixels(control, pixels, unavailable, systemDpi); }
                catch (InvalidOperationException error) { failures.Add($"Replay {replay}: {error.Message}"); }
                if (first != null && !first.AsSpan().SequenceEqual(pixels))
                    failures.Add($"{name}: retained/warm/independent-literal pixels differ.");
                first ??= pixels;
                replays.Add(new
                {
                    Replay = replay, SameSourceObjects = replay < 2, IndependentLiteralInstance = replay == 2,
                    Before = before, After = after, RetainedBefore = retainedBefore, RetainedAfter = retainedAfter,
                    Pixels = pixels, PixelSha256 = Convert.ToHexString(SHA256.HashData(pixels))
                });
            }
            controls.Add(new { Name = control.ToString(), Replays = replays });
        }
        if (controls.Count != 7) throw new InvalidOperationException("Original cache re-entry inventory changed.");
        return controls;
    }

    private static void AssertBitmapCacheReentryPixels(BitmapCacheReentryControl control,
        byte[] pixels, bool unavailable, uint systemDpi)
    {
        // Independent source-walk expectations: one cache retains nested red;
        // re-entering a second cache omits the currently entered visual group.
        // Moving only the nested leaf to a sibling preserves blue/green while
        // that leaf is omitted. Equal cache modes/scales still own two caches.
        bool noInk = control is BitmapCacheReentryControl.Original or
            BitmapCacheReentryControl.SameCacheMode or BitmapCacheReentryControl.EqualScales;
        int firstChannel = control == BitmapCacheReentryControl.NestedSibling ? 0 : 2;
        int scale = control == BitmapCacheReentryControl.TargetOnly ? 2 : 1;
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
        {
            int selected = -1;
            if (!unavailable && !noInk && x >= 8 && x < 40 && y >= 10 && y < 34)
                selected = SoftwareSamplerOracle.CacheSamplerFirstBand(x - 8, systemDpi, scale) ? firstChannel : 1;
            for (int channel = 0; channel < 4; channel++)
            {
                byte expected = channel == 3 || channel == selected ? (byte)255 : (byte)0;
                byte actual = pixels[(y * 64 + x) * 4 + channel];
                if (actual != expected)
                    throw new InvalidOperationException($"cache-reentry-{control}: ({x},{y}) BGRA[{channel}]={actual}, expected {expected}.");
            }
        }
    }
}
