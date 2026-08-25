using System;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Avalonia.ProGpu;

/// <summary>
/// Resolves Avalonia image-brush sources without reflection. Exact-source
/// integration borrows the typed platform bitmap; package-only integration
/// keeps one weakly owned snapshot per immutable public Bitmap.
/// </summary>
internal static class ProGpuImageBrushSource
{
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
    public static IBitmapImpl? GetBitmap(IImageBrushSource? source) =>
        source?.Bitmap?.Item;
#else
    private static readonly ConditionalWeakTable<
        IImageBrushSource,
        SnapshotOwner> Snapshots = new();

    public static IBitmapImpl? GetBitmap(IImageBrushSource? source)
    {
        if (source is not Bitmap bitmap)
            return null;
        return Snapshots
            .GetValue(source, static _ => new SnapshotOwner())
            .Resolve(bitmap);
    }

    private sealed class SnapshotOwner
    {
        private readonly object _gate = new();
        private IProGpuBitmapSource? _snapshot;

        public IProGpuBitmapSource? Resolve(Bitmap bitmap)
        {
            lock (_gate)
            {
                if (_snapshot is null || bitmap is WriteableBitmap)
                {
                    IProGpuBitmapSource next = Capture(bitmap);
                    _snapshot?.Dispose();
                    _snapshot = next;
                }

                return _snapshot;
            }
        }

        ~SnapshotOwner()
        {
            _snapshot?.Dispose();
        }

        private static IProGpuBitmapSource Capture(Bitmap bitmap)
        {
#if !AVALONIA11
            if (bitmap is WriteableBitmap writable)
            {
                using ILockedFramebuffer framebuffer = writable.Lock();
                return new ImmutableBitmap(
                    framebuffer.Size,
                    framebuffer.Dpi,
                    framebuffer.RowBytes,
                    framebuffer.Format,
                    framebuffer.AlphaFormat,
                    framebuffer.Address);
            }
#endif
            using var encoded = new MemoryStream();
#if AVALONIA11
            bitmap.Save(encoded);
#else
            bitmap.Save(encoded, PngBitmapEncoderOptions.Default);
#endif
            encoded.Position = 0;
            return new ImmutableBitmap(encoded);
        }
    }
#endif
}
