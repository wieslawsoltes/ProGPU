namespace ProGPU.Wpf.Interop;

/// <summary>A real text range in a source that can also contain non-text positions.</summary>
public readonly record struct PortableTextSourceRange(int SourceStart, int TextStart, int Length);

/// <summary>Measured child occupying an actual non-text source range, relative to this source map.</summary>
public readonly record struct PortableTextSourceFloat(int SourceStart, int SourceLength,
    float Width, float Height, PortableTextFloatAlignment Alignment);

/// <summary>
/// Immutable boundary map between source positions and contiguous shaping text.
/// Source gaps have no text, glyph, width or independent caret. Queries are O(log R)
/// for R ranges and allocate nothing. This is source metadata, not a text composer.
/// </summary>
public sealed class PortableTextSourceMap
{
    private readonly PortableTextSourceRange[] _ranges;
    public int SourceLength { get; }
    public int TextLength { get; }

    public PortableTextSourceMap(int sourceLength, int textLength, ReadOnlySpan<PortableTextSourceRange> ranges)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        ArgumentOutOfRangeException.ThrowIfNegative(textLength);
        int sourceEnd = 0, textEnd = 0;
        foreach (var range in ranges)
        {
            if (range.Length <= 0 || range.SourceStart < sourceEnd || range.TextStart != textEnd ||
                range.SourceStart > sourceLength - range.Length || range.TextStart > textLength - range.Length)
                throw new ArgumentException("Visible ranges must partition text and be ordered within the source.", nameof(ranges));
            sourceEnd = range.SourceStart + range.Length;
            textEnd = range.TextStart + range.Length;
        }
        if (textEnd != textLength) throw new ArgumentException("Visible ranges do not cover text.", nameof(ranges));
        SourceLength = sourceLength; TextLength = textLength;
        // Equal lengths plus the partition invariant prove the identity mapping.
        _ranges = sourceLength == textLength ? [] : ranges.ToArray();
    }

    /// <summary>All positions within a source gap map to its single text boundary.</summary>
    public int ToText(int sourcePosition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourcePosition);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sourcePosition, SourceLength);
        if (SourceLength == TextLength) return sourcePosition;
        int lo = 0, hi = _ranges.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            var range = _ranges[mid];
            if (range.SourceStart + range.Length < sourcePosition) lo = mid + 1;
            else hi = mid;
        }
        if (lo == _ranges.Length) return TextLength;
        var found = _ranges[lo];
        return found.TextStart + Math.Max(0, sourcePosition - found.SourceStart);
    }

    /// <summary>
    /// Maps ordered, disjoint hidden child ranges to shaping boundaries. The result
    /// owns its records; retain the original child list for source identity on return.
    /// Visible text cannot be suppressed by declaring it a floating child.
    /// </summary>
    public PortableTextFloat[] MapFloatingRanges(ReadOnlySpan<PortableTextSourceFloat> children)
    {
        if (children.Length > 1 << 20)
            throw new ArgumentException("Floating source range budget exceeded.", nameof(children));
        var result = children.IsEmpty ? [] : new PortableTextFloat[children.Length];
        int previousEnd = 0;
        for (int i = 0; i < children.Length; ++i)
        {
            var child = children[i];
            if (child.SourceLength <= 0 || child.SourceStart < previousEnd ||
                child.SourceLength > SourceLength || child.SourceStart > SourceLength - child.SourceLength)
                throw new ArgumentException("Floating children require ordered disjoint source ranges.", nameof(children));
            int end = child.SourceStart + child.SourceLength;
            int position = ToText(child.SourceStart);
            if (ToText(end) != position)
                throw new ArgumentException("A floating child must occupy a wholly hidden source range.", nameof(children));
            result[i] = new(position, child.Width, child.Height, child.Alignment);
            previousEnd = end;
        }
        return result;
    }

    /// <summary>
    /// A boundary bordering hidden source content has two source positions.
    /// afterHidden selects the next visible start; false selects the previous end.
    /// At paragraph edges these may be SourceLength and zero respectively.
    /// </summary>
    public int ToSource(int textPosition, bool afterHidden)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(textPosition);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(textPosition, TextLength);
        if (SourceLength == TextLength) return textPosition;
        int lo = 0, hi = _ranges.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            var range = _ranges[mid];
            int end = range.TextStart + range.Length;
            if (end < textPosition || (afterHidden && end == textPosition)) lo = mid + 1;
            else hi = mid;
        }
        if (lo == _ranges.Length) return afterHidden ? SourceLength : 0;
        var found = _ranges[lo];
        if (!afterHidden && textPosition == 0) return 0;
        return found.SourceStart + (textPosition - found.TextStart);
    }
}
