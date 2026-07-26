using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Platform;
using ProGPU.Text;
using SixLabors.ImageSharp;

namespace Avalonia.ProGpu
{
    internal readonly struct BitmapGlyphMetrics
    {
        public BitmapGlyphMetrics(
            ushort pixelsPerEm,
            ushort pixelsPerInch,
            short originOffsetX,
            short originOffsetY,
            int pixelWidth,
            int pixelHeight)
        {
            PixelsPerEm = pixelsPerEm;
            PixelsPerInch = pixelsPerInch;
            OriginOffsetX = originOffsetX;
            OriginOffsetY = originOffsetY;
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
        }

        public ushort PixelsPerEm { get; }
        public ushort PixelsPerInch { get; }
        public short OriginOffsetX { get; }
        public short OriginOffsetY { get; }
        public int PixelWidth { get; }
        public int PixelHeight { get; }

        public Rect GetBounds(Point baselineOrigin, double emSize)
        {
            var scale = PixelsPerEm > 0 ? emSize / PixelsPerEm : 1.0;
            return new Rect(
                baselineOrigin.X - OriginOffsetX * scale,
                baselineOrigin.Y - (PixelHeight - OriginOffsetY) * scale,
                PixelWidth * scale,
                PixelHeight * scale);
        }
    }

    /// <summary>
    /// Retains only bitmap-glyph dimensions needed by Avalonia's CPU bounds
    /// contract. GPU pixels are demand-decoded by ProGPU's bounded color glyph
    /// atlas during scene compilation, so the Avalonia adapter does not own a
    /// second set of RGBA atlas pages or decoded pixel arrays.
    /// </summary>
    internal static class BitmapGlyphCache
    {
        internal const int MaximumCachedMetricCount = 2048;
        internal const int MaximumFailedGlyphCount = 256;

        private readonly record struct GlyphKey(
            TtfFont Font,
            ushort GlyphIndex,
            ushort PixelsPerEm);

        private sealed class MetricEntry
        {
            public MetricEntry(
                BitmapGlyphMetrics metrics,
                LinkedListNode<GlyphKey> lruNode)
            {
                Metrics = metrics;
                LruNode = lruNode;
            }

            public BitmapGlyphMetrics Metrics { get; }
            public LinkedListNode<GlyphKey> LruNode { get; }
        }

        private sealed class ReadOnlyMemoryStream : Stream
        {
            private readonly ReadOnlyMemory<byte> _data;
            private int _position;

            public ReadOnlyMemoryStream(ReadOnlyMemory<byte> data)
            {
                _data = data;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _data.Length;
            public override long Position
            {
                get => _position;
                set
                {
                    if (value < 0 || value > _data.Length)
                        throw new ArgumentOutOfRangeException(nameof(value));
                    _position = checked((int)value);
                }
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                int count = Math.Min(buffer.Length, _data.Length - _position);
                _data.Span.Slice(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                long target = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End => _data.Length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                };
                Position = target;
                return target;
            }

            public override void Flush()
            {
            }

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
        }

        private static readonly object s_sync = new();
        private static readonly Dictionary<GlyphKey, MetricEntry> s_metrics = new();
        private static readonly LinkedList<GlyphKey> s_metricLru = new();
        private static readonly Dictionary<GlyphKey, LinkedListNode<GlyphKey>>
            s_failedGlyphs = new();
        private static readonly LinkedList<GlyphKey> s_failedLru = new();

        internal static int CachedMetricCount
        {
            get
            {
                lock (s_sync)
                    return s_metrics.Count;
            }
        }

        internal static int FailedGlyphCount
        {
            get
            {
                lock (s_sync)
                    return s_failedGlyphs.Count;
            }
        }

        internal static long CachedDecodedPixelBytes => 0;

        internal static ulong MetricEvictionCount { get; private set; }

        public static bool TryGetMetrics(
            TtfFont font,
            ushort glyphIndex,
            double emSize,
            out BitmapGlyphMetrics metrics)
        {
            if (!font.TryGetBitmapGlyph(glyphIndex, (float)emSize, out var bitmap))
            {
                metrics = default;
                return false;
            }

            var key = new GlyphKey(font, glyphIndex, bitmap.PixelsPerEm);
            lock (s_sync)
            {
                if (s_metrics.TryGetValue(key, out var cached))
                {
                    Touch(s_metricLru, cached.LruNode);
                    metrics = cached.Metrics;
                    return true;
                }

                if (s_failedGlyphs.TryGetValue(key, out var failedNode))
                {
                    Touch(s_failedLru, failedNode);
                    metrics = default;
                    return false;
                }
            }

            int imageWidth;
            int imageHeight;
            try
            {
                using var stream = new ReadOnlyMemoryStream(bitmap.Data);
                var imageInfo = Image.Identify(stream);
                if (imageInfo == null)
                {
                    RememberFailure(key);
                    metrics = default;
                    return false;
                }

                imageWidth = imageInfo.Width;
                imageHeight = imageInfo.Height;
            }
            catch (Exception ex) when (
                ex is InvalidImageContentException or
                    NotSupportedException or
                    ArgumentException)
            {
                RememberFailure(key);
                metrics = default;
                return false;
            }

            if (imageWidth <= 0 || imageHeight <= 0)
            {
                RememberFailure(key);
                metrics = default;
                return false;
            }

            metrics = new BitmapGlyphMetrics(
                bitmap.PixelsPerEm,
                bitmap.PixelsPerInch,
                bitmap.OriginOffsetX,
                bitmap.OriginOffsetY,
                imageWidth,
                imageHeight);
            lock (s_sync)
            {
                if (s_metrics.TryGetValue(key, out var raced))
                {
                    Touch(s_metricLru, raced.LruNode);
                    metrics = raced.Metrics;
                    return true;
                }

                while (s_metrics.Count >= MaximumCachedMetricCount)
                {
                    LinkedListNode<GlyphKey>? oldest = s_metricLru.Last;
                    if (oldest == null)
                        break;
                    s_metricLru.RemoveLast();
                    if (s_metrics.Remove(oldest.Value))
                        MetricEvictionCount++;
                }

                var node = s_metricLru.AddFirst(key);
                s_metrics.Add(key, new MetricEntry(metrics, node));
            }

            return true;
        }

        private static void RememberFailure(GlyphKey key)
        {
            lock (s_sync)
            {
                if (s_failedGlyphs.TryGetValue(key, out var existing))
                {
                    Touch(s_failedLru, existing);
                    return;
                }

                while (s_failedGlyphs.Count >= MaximumFailedGlyphCount)
                {
                    LinkedListNode<GlyphKey>? oldest = s_failedLru.Last;
                    if (oldest == null)
                        break;
                    s_failedLru.RemoveLast();
                    s_failedGlyphs.Remove(oldest.Value);
                }

                var node = s_failedLru.AddFirst(key);
                s_failedGlyphs.Add(key, node);
            }
        }

        private static void Touch(
            LinkedList<GlyphKey> lru,
            LinkedListNode<GlyphKey> node)
        {
            if (ReferenceEquals(lru.First, node))
                return;
            lru.Remove(node);
            lru.AddFirst(node);
        }
    }
}
