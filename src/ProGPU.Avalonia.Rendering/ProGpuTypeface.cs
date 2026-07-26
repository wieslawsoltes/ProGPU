using System;
using System.IO;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using ProGPU.Text;

namespace Avalonia.ProGpu
{
    internal class ProGpuTypeface :
#if AVALONIA11
        IGlyphTypeface
#else
        IPlatformTypeface
#endif
    {
        private sealed class FontDataStream : Stream
        {
            private readonly ReadOnlyMemory<byte> _data;
            private int _position;
            private bool _disposed;

            public FontDataStream(ReadOnlyMemory<byte> data)
            {
                _data = data;
            }

            public override bool CanRead => !_disposed;
            public override bool CanSeek => !_disposed;
            public override bool CanWrite => false;
            public override long Length
            {
                get
                {
                    ThrowIfDisposed();
                    return _data.Length;
                }
            }

            public override long Position
            {
                get
                {
                    ThrowIfDisposed();
                    return _position;
                }
                set
                {
                    ThrowIfDisposed();
                    if (value < 0 || value > _data.Length)
                    {
                        throw new ArgumentOutOfRangeException(nameof(value));
                    }

                    _position = checked((int)value);
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                ArgumentNullException.ThrowIfNull(buffer);
                return Read(buffer.AsSpan(offset, count));
            }

            public override int Read(Span<byte> buffer)
            {
                ThrowIfDisposed();
                int count = Math.Min(buffer.Length, _data.Length - _position);
                _data.Span.Slice(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                ThrowIfDisposed();
                long position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End => _data.Length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                };
                Position = position;
                return position;
            }

            public override void Flush()
            {
            }

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                _disposed = true;
                base.Dispose(disposing);
            }

            private void ThrowIfDisposed() =>
                ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public TtfFont Font { get; }
        private readonly ReadOnlyMemory<byte> _fontData;
        internal TtfShapingFontFace ShapingFace { get; }
        public FontSimulations FontSimulations { get; }
        public string FamilyName { get; }
        public FontWeight Weight { get; }
        public FontStyle Style { get; }
        public FontStretch Stretch { get; }
#if AVALONIA11
        public int GlyphCount => Font.NumGlyphs;
        public FontMetrics Metrics { get; }
#endif

        public ProGpuTypeface(TtfFont font, ReadOnlyMemory<byte> fontData, string familyName, FontWeight weight, FontStyle style, FontStretch stretch, FontSimulations fontSimulations = FontSimulations.None)
        {
            Font = font ?? throw new ArgumentNullException(nameof(font));
            ShapingFace = new TtfShapingFontFace(Font);
            _fontData = fontData;
            FamilyName = familyName;
            Weight = weight;
            Style = style;
            Stretch = stretch;
            FontSimulations = fontSimulations;
#if AVALONIA11
            Metrics = new FontMetrics
            {
                DesignEmHeight = (short)font.UnitsPerEm,
                Ascent = -font.Ascender,
                Descent = -font.Descender,
                LineGap = font.LineGap,
                UnderlinePosition = -(font.UnderlinePosition ?? 0),
                UnderlineThickness = font.UnderlineThickness ?? 0,
                StrikethroughPosition = -(font.StrikeoutPosition ?? 0),
                StrikethroughThickness = font.StrikeoutThickness ?? 0,
                IsFixedPitch = font.IsFixedPitch
            };
#endif
        }

#if AVALONIA11
        public bool TryGetGlyphMetrics(ushort glyph, out GlyphMetrics metrics)
        {
            metrics = default;
            if (!Font.TryGetGlyphBounds(glyph, out var xMin, out var yMin, out var xMax, out var yMax))
            {
                return false;
            }

            metrics = new GlyphMetrics
            {
                XBearing = xMin,
                YBearing = yMax,
                Width = xMax - xMin,
                Height = yMax - yMin
            };
            return true;
        }

        public ushort GetGlyph(uint codepoint) => Font.GetGlyphIndex(codepoint);

        public bool TryGetGlyph(uint codepoint, out ushort glyph)
        {
            glyph = GetGlyph(codepoint);
            return glyph != 0;
        }

        public ushort[] GetGlyphs(ReadOnlySpan<uint> codepoints)
        {
            var glyphs = new ushort[codepoints.Length];
            for (var i = 0; i < codepoints.Length; i++)
            {
                glyphs[i] = GetGlyph(codepoints[i]);
            }

            return glyphs;
        }

        public int GetGlyphAdvance(ushort glyph) =>
            (int)Math.Round(Font.GetAdvanceWidth(glyph, Font.UnitsPerEm));

        public int[] GetGlyphAdvances(ReadOnlySpan<ushort> glyphs)
        {
            var advances = new int[glyphs.Length];
            for (var i = 0; i < glyphs.Length; i++)
            {
                advances[i] = GetGlyphAdvance(glyphs[i]);
            }

            return advances;
        }

        public bool TryGetTable(uint tag, out byte[] table)
#else
        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
#endif
        {
#if !AVALONIA11
            var value = (uint)tag;
#else
            var value = tag;
#endif
            var tableTag = new string(new[]
            {
                (char)((value >> 24) & 0xFF),
                (char)((value >> 16) & 0xFF),
                (char)((value >> 8) & 0xFF),
                (char)(value & 0xFF)
            });
            if (Font.TryGetTable(tableTag, out var memory))
            {
#if AVALONIA11
                table = memory.ToArray();
#else
                table = memory;
#endif
                return true;
            }

            var reversedTag = new string(new[]
            {
                tableTag[3],
                tableTag[2],
                tableTag[1],
                tableTag[0]
            });
            if (Font.TryGetTable(reversedTag, out memory))
            {
#if AVALONIA11
                table = memory.ToArray();
#else
                table = memory;
#endif
                return true;
            }

#if AVALONIA11
            table = Array.Empty<byte>();
#else
            table = default;
#endif
            return false;
        }

        public bool TryGetStream([NotNullWhen(true)] out Stream? stream)
        {
            try
            {
                stream = new FontDataStream(_fontData);
                return true;
            }
            catch
            {
                stream = null;
                return false;
            }
        }

        public void Dispose()
        {
            // Parsed font data is immutable managed state and is owned by this
            // platform typeface; no native shaping objects need disposal.
        }
    }
}
