using System;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Avalonia.ProGpu
{
    internal static class ProGpuImageBrushSource
    {
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
        public static IBitmapImpl? GetBitmap(IImageBrushSource? source) =>
            source?.Bitmap?.Item;
#else
        private static readonly ConditionalWeakTable<IImageBrushSource, BitmapSnapshot> s_snapshots = new();

        public static IBitmapImpl? GetBitmap(IImageBrushSource? source)
        {
            if (source is not Bitmap bitmap)
            {
                return null;
            }

            var snapshot = s_snapshots.GetValue(source, static _ => new BitmapSnapshot());
            return snapshot.Get(bitmap);
        }

        private sealed class BitmapSnapshot
        {
            private readonly object _gate = new();
            private IDrawableBitmapImpl? _bitmap;

            public IDrawableBitmapImpl? Get(Bitmap source)
            {
                lock (_gate)
                {
                    // WriteableBitmap doesn't publish its backend Version through the
                    // public Avalonia contract. Refresh only when its retained command is
                    // rebuilt; immutable Bitmap instances keep one cached GPU snapshot.
                    if (_bitmap == null || source is WriteableBitmap)
                    {
                        var next = CreateSnapshot(source);
                        _bitmap?.Dispose();
                        _bitmap = next;
                    }

                    return _bitmap;
                }
            }

            ~BitmapSnapshot()
            {
                _bitmap?.Dispose();
            }

            private static IDrawableBitmapImpl? CreateSnapshot(Bitmap source)
            {
#if !AVALONIA11
                if (source is WriteableBitmap writeable)
                {
                    using var locked = writeable.Lock();
                    return new ImmutableBitmap(
                        locked.Size,
                        locked.Dpi,
                        locked.RowBytes,
                        locked.Format,
                        locked.AlphaFormat,
                        locked.Address);
                }
#endif

                // The public package contract deliberately hides IBitmapImpl. A cached
                // PNG snapshot is the reflection-free compatibility path. Avalonia 11
                // does not expose a locked framebuffer's alpha format, so its mutable
                // bitmap path also uses this representation instead of guessing. Exact-
                // source integration takes the direct branch above and performs no readback.
                using var encoded = new MemoryStream();
                source.Save(encoded);
                encoded.Position = 0;
                return new ImmutableBitmap(encoded);
            }
        }
#endif
    }
}
