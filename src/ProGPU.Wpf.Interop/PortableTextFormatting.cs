namespace ProGPU.Wpf.Interop;

/// <summary>Immutable font snapshot, cached by the source face. No WPF type identity crosses this seam.</summary>
public sealed class PortableTextFont(ReadOnlyMemory<byte> data, uint faceIndex, ushort unitsPerEm)
{
    public ReadOnlyMemory<byte> Data { get; } = data;
    public uint FaceIndex { get; } = faceIndex;
    public ushort UnitsPerEm { get; } = unitsPerEm;
}

public enum PortableTextAlignment { Left, Center, Right, Justify }
public readonly record struct PortableTextFeature(uint Tag, uint Value);

/// <summary>
/// Horizontal paragraph. Optional styles partition Text at UTF-16 scalar boundaries;
/// an empty style list preserves the uniform font/feature domain.
/// </summary>
public readonly record struct PortableTextParagraphRequest(
    ReadOnlyMemory<char> Text, PortableTextFont Font, float FontSize,
    float LineHeight, float MaximumWidth, bool RightToLeft, PortableTextAlignment Alignment,
    ReadOnlyMemory<PortableTextFeature> Features = default,
    ReadOnlyMemory<PortableTextStyle> Styles = default,
    float IncrementalTab = 0, float TabOrigin = 0);

public readonly record struct PortableTextStyle(int Start, int Length, PortableTextFont Font,
    float FontSize, ReadOnlyMemory<PortableTextFeature> Features = default, uint Language = 0);

public readonly record struct PortableTextGlyph(uint GlyphId, int Cluster, int ClusterEnd,
    float X, float Y, float Advance, sbyte BidiLevel, uint FontIndex = 0, bool IsTab = false);
public readonly record struct PortableTextLineInfo(int GlyphStart, int GlyphCount,
    int InputStart, int InputEnd, float Width, float Y, float Height);
public readonly record struct PortableTextHit(int Position, bool Trailing);

/// <summary>Immutable owned layout output; no borrowed native handles survive formatting.</summary>
public interface IPortableTextParagraph
{
    /// <summary>Opaque render-font annotation, as on PortableNativeGlyphRun; never inspected by source WPF.</summary>
    object? NativeFont => null;
    /// <summary>Exact context face annotation for each positioned glyph, not a family-name lookup.</summary>
    object? GetNativeFont(uint fontIndex) => fontIndex == 0 ? NativeFont :
        throw new ArgumentOutOfRangeException(nameof(fontIndex));
    ReadOnlyMemory<PortableTextGlyph> Glyphs { get; }
    ReadOnlyMemory<PortableTextLineInfo> Lines { get; }
    PortableTextHit HitTest(int lineIndex, float distance);
    float GetCaretDistance(int lineIndex, PortableTextHit hit);
    int GetNextLogicalCaret(int lineIndex, int position, bool previous);
    int GetSelection(int lineIndex, int start, int end, Span<PortableRect> rectangles);
}

public interface IPortableTextFormatting
{
    IPortableTextParagraph Format(in PortableTextParagraphRequest request);
}

public static partial class PortableWpfServiceRegistry
{
    private sealed class TextFormattingRegistration(IPortableTextFormatting service) : IDisposable
    {
        internal IPortableTextFormatting Service { get; } = service;
        public void Dispose() => Interlocked.CompareExchange(ref s_textFormatting, null, this);
    }
    private static TextFormattingRegistration? s_textFormatting;

    public static bool TryGetTextFormatting(out IPortableTextFormatting service)
    {
        service = Volatile.Read(ref s_textFormatting)?.Service!;
        return service != null;
    }

    public static IDisposable RegisterTextFormatting(IPortableTextFormatting service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var registration = new TextFormattingRegistration(service);
        Interlocked.Exchange(ref s_textFormatting, registration);
        return registration;
    }

    public static void EnsureTextFormatting(IPortableTextFormatting service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (Volatile.Read(ref s_textFormatting) == null)
            Interlocked.CompareExchange(ref s_textFormatting, new TextFormattingRegistration(service), null);
    }
}
