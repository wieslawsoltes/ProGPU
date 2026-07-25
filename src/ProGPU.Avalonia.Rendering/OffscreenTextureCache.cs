using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;

namespace Avalonia.ProGpu
{
    internal class OffscreenTextureCache : IDisposable
    {
        private const int MaximumSolidStyleCacheEntries = 256;
        public GpuTexture? CachedTexture;
        public GpuTextureReadbackBuffer? CachedReadbackBuffer;
        public uint CachedWidth;
        public uint CachedHeight;
        public bool IsTextureFresh = true;
        private readonly object _recordingContextLock = new();
        private readonly object _solidStyleCacheLock = new();
        private readonly Dictionary<SolidBrushKey, SolidColorBrush> _solidBrushes = new();
        private readonly Dictionary<SolidPenKey, Pen> _solidPens = new();
        private DrawingContext? _recordingContext;
        private bool _disposed;

        private readonly record struct SolidBrushKey(uint Color, float Opacity);

        private readonly record struct SolidPenKey(
            SolidBrushKey Brush,
            float Thickness,
            PenLineJoin LineJoin,
            float MiterLimit,
            PenLineCap LineCap);

        public OffscreenTextureCache()
        {
            WgpuContext.Disposing += OnContextDisposing;
        }

        private void OnContextDisposing(WgpuContext context)
        {
            if (CachedTexture?.Context == context)
            {
                Invalidate(context);
            }
        }

        public DrawingContext RentRecordingContext()
        {
            lock (_recordingContextLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var context = _recordingContext;
                _recordingContext = null;
                return context ?? new DrawingContext();
            }
        }

        public void ReturnRecordingContext(DrawingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.Clear();

            lock (_recordingContextLock)
            {
                if (_disposed || _recordingContext != null)
                {
                    return;
                }

                _recordingContext = context;
            }
        }

        public SolidColorBrush GetSolidBrush(byte red, byte green, byte blue, byte alpha, float opacity)
        {
            var key = new SolidBrushKey(PackColor(red, green, blue, alpha), opacity);
            lock (_solidStyleCacheLock)
            {
                if (_solidBrushes.TryGetValue(key, out var brush))
                {
                    return brush;
                }

                if (_solidBrushes.Count >= MaximumSolidStyleCacheEntries)
                {
                    _solidBrushes.Clear();
                }

                brush = new SolidColorBrush(new Vector4(
                    red / 255.0f,
                    green / 255.0f,
                    blue / 255.0f,
                    alpha / 255.0f))
                {
                    Opacity = opacity
                };
                _solidBrushes.Add(key, brush);
                return brush;
            }
        }

        public Pen GetSolidPen(
            byte red,
            byte green,
            byte blue,
            byte alpha,
            float opacity,
            float thickness,
            PenLineJoin lineJoin,
            float miterLimit,
            PenLineCap lineCap)
        {
            var brushKey = new SolidBrushKey(PackColor(red, green, blue, alpha), opacity);
            var key = new SolidPenKey(brushKey, thickness, lineJoin, miterLimit, lineCap);
            lock (_solidStyleCacheLock)
            {
                if (_solidPens.TryGetValue(key, out var pen))
                {
                    return pen;
                }

                if (_solidPens.Count >= MaximumSolidStyleCacheEntries)
                {
                    _solidPens.Clear();
                }

                var brush = GetSolidBrush(red, green, blue, alpha, opacity);
                pen = new Pen(
                    brush,
                    thickness,
                    lineJoin,
                    miterLimit,
                    lineCap,
                    lineCap,
                    lineCap);
                _solidPens.Add(key, pen);
                return pen;
            }
        }

        private static uint PackColor(byte red, byte green, byte blue, byte alpha) =>
            ((uint)red << 24) | ((uint)green << 16) | ((uint)blue << 8) | alpha;

        public void Invalidate(WgpuContext? context)
        {
            if (CachedTexture != null)
            {
                CachedTexture.Dispose();
                CachedTexture = null;
            }
            CachedReadbackBuffer?.Dispose();
            CachedReadbackBuffer = null;
            CachedWidth = 0;
            CachedHeight = 0;
            IsTextureFresh = true;
        }

        public void Dispose()
        {
            WgpuContext.Disposing -= OnContextDisposing;
            var context = CachedTexture?.Context ?? WgpuContext.Current;
            Invalidate(context);

            lock (_recordingContextLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _recordingContext?.Clear();
                _recordingContext = null;
            }

            lock (_solidStyleCacheLock)
            {
                _solidBrushes.Clear();
                _solidPens.Clear();
            }
        }
    }
}
