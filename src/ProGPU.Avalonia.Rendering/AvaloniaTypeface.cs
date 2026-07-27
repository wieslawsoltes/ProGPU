using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using ProGPU.Text;

namespace Avalonia.ProGpu;

/// <summary>
/// Couples Avalonia's typeface contract to one immutable parsed ProGPU font.
/// </summary>
internal sealed class ProGpuTypeface :
#if AVALONIA11
    IGlyphTypeface
#else
    IPlatformTypeface
#endif
{
    private readonly ReadOnlyMemory<byte> _fontBytes;

    public ProGpuTypeface(
        TtfFont font,
        ReadOnlyMemory<byte> fontData,
        string familyName,
        FontWeight weight,
        FontStyle style,
        FontStretch stretch,
        FontSimulations fontSimulations = FontSimulations.None)
    {
        Font = font ?? throw new ArgumentNullException(nameof(font));
        _fontBytes = fontData;
        FamilyName = familyName ??
            throw new ArgumentNullException(nameof(familyName));
        Weight = weight;
        Style = style;
        Stretch = stretch;
        FontSimulations = fontSimulations;
        ShapingFace = new TtfShapingFontFace(font);

#if AVALONIA11
        Metrics = new FontMetrics
        {
            DesignEmHeight = checked((short)font.UnitsPerEm),
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

    public TtfFont Font { get; }

    internal TtfShapingFontFace ShapingFace { get; }

    public FontSimulations FontSimulations { get; }

    public string FamilyName { get; }

    public FontWeight Weight { get; }

    public FontStyle Style { get; }

    public FontStretch Stretch { get; }

#if AVALONIA11
    public int GlyphCount => Font.NumGlyphs;

    public FontMetrics Metrics { get; }

    public ushort GetGlyph(uint codepoint) => Font.GetGlyphIndex(codepoint);

    public bool TryGetGlyph(uint codepoint, out ushort glyph)
    {
        glyph = Font.GetGlyphIndex(codepoint);
        return glyph != 0;
    }

    public ushort[] GetGlyphs(ReadOnlySpan<uint> codepoints)
    {
        var result = new ushort[codepoints.Length];
        for (int index = 0; index < codepoints.Length; index++)
            result[index] = Font.GetGlyphIndex(codepoints[index]);
        return result;
    }

    public int GetGlyphAdvance(ushort glyph) =>
        checked((int)Math.Round(
            Font.GetAdvanceWidth(glyph, Font.UnitsPerEm)));

    public int[] GetGlyphAdvances(ReadOnlySpan<ushort> glyphs)
    {
        var result = new int[glyphs.Length];
        for (int index = 0; index < glyphs.Length; index++)
            result[index] = GetGlyphAdvance(glyphs[index]);
        return result;
    }

    public bool TryGetGlyphMetrics(
        ushort glyph,
        out GlyphMetrics metrics)
    {
        if (Font.TryGetGlyphBounds(
            glyph,
            out short xMin,
            out short yMin,
            out short xMax,
            out short yMax))
        {
            metrics = new GlyphMetrics
            {
                XBearing = xMin,
                YBearing = yMax,
                Width = xMax - xMin,
                Height = yMax - yMin
            };
            return true;
        }

        metrics = default;
        return false;
    }

    public bool TryGetTable(uint tag, out byte[] table)
    {
        if (TryGetTableCore(tag, out ReadOnlyMemory<byte> memory))
        {
            table = memory.ToArray();
            return true;
        }

        table = Array.Empty<byte>();
        return false;
    }
#else
    public bool TryGetTable(
        OpenTypeTag tag,
        out ReadOnlyMemory<byte> table) =>
        TryGetTableCore((uint)tag, out table);
#endif

    public bool TryGetStream([NotNullWhen(true)] out Stream? stream)
    {
        stream = new ReadOnlyFontStream(_fontBytes);
        return true;
    }

    public void Dispose()
    {
    }

    private bool TryGetTableCore(
        uint packedTag,
        out ReadOnlyMemory<byte> table)
    {
        Span<char> tag = stackalloc char[4];
        tag[0] = (char)((packedTag >> 24) & 0xff);
        tag[1] = (char)((packedTag >> 16) & 0xff);
        tag[2] = (char)((packedTag >> 8) & 0xff);
        tag[3] = (char)(packedTag & 0xff);
        if (Font.TryGetTable(new string(tag), out table))
            return true;

        tag.Reverse();
        return Font.TryGetTable(new string(tag), out table);
    }

    private sealed class ReadOnlyFontStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _bytes;
        private int _cursor;
        private bool _isClosed;

        public ReadOnlyFontStream(ReadOnlyMemory<byte> bytes)
        {
            _bytes = bytes;
        }

        public override bool CanRead => !_isClosed;

        public override bool CanSeek => !_isClosed;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                ThrowIfClosed();
                return _bytes.Length;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfClosed();
                return _cursor;
            }
            set
            {
                ThrowIfClosed();
                if ((ulong)value > (ulong)_bytes.Length)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _cursor = (int)value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowIfClosed();
            int length = Math.Min(buffer.Length, _bytes.Length - _cursor);
            _bytes.Span.Slice(_cursor, length).CopyTo(buffer);
            _cursor += length;
            return length;
        }

        public override int ReadByte()
        {
            ThrowIfClosed();
            if (_cursor == _bytes.Length)
                return -1;
            return _bytes.Span[_cursor++];
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfClosed();
            long next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_cursor + offset),
                SeekOrigin.End => checked(_bytes.Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = next;
            return next;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _isClosed = true;
            base.Dispose(disposing);
        }

        private void ThrowIfClosed() =>
            ObjectDisposedException.ThrowIf(_isClosed, this);
    }
}
