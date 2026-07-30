using System.Collections.ObjectModel;

namespace ProGPU.Media.Playback;

/// <summary>
/// Provider-neutral timed-text font style. Values intentionally match the
/// WinUI TimedTextFontStyle projection.
/// </summary>
public enum MediaPlaybackTimedTextFontStyle
{
    Normal = 0,
    Oblique = 1,
    Italic = 2
}

/// <summary>
/// Provider-neutral timed-text weight. Values intentionally match the WinUI
/// TimedTextWeight projection.
/// </summary>
public enum MediaPlaybackTimedTextWeight
{
    Normal = 400,
    Bold = 700
}

/// <summary>
/// Provider-neutral timed-text alignment. Left and Right remain distinct
/// because WebVTT defines them independently of Start and End.
/// </summary>
public enum MediaPlaybackTimedTextAlignment
{
    Start = 0,
    End = 1,
    Center = 2,
    Left = 3,
    Right = 4
}

/// <summary>
/// Provider-neutral writing direction. Values intentionally match the WinUI
/// TimedTextWritingMode projection.
/// </summary>
public enum MediaPlaybackTimedTextWritingMode
{
    LeftRightTopBottom = 0,
    RightLeftTopBottom = 1,
    TopBottomRightLeft = 2,
    TopBottomLeftRight = 3,
    LeftRight = 4,
    RightLeft = 5,
    TopBottom = 6
}

/// <summary>
/// Unit used by an explicit cue line position.
/// </summary>
public enum MediaPlaybackTimedTextLinePositionUnit
{
    Lines = 0,
    Percentage = 1
}

/// <summary>
/// Immutable sparse style state. Null properties inherit the enclosing cue
/// or platform style. Construction and reads are allocation-free O(1).
/// </summary>
public readonly record struct MediaPlaybackTimedTextStyle(
    MediaPlaybackTimedTextFontStyle? FontStyle = null,
    MediaPlaybackTimedTextWeight? FontWeight = null,
    bool? IsUnderlineEnabled = null);

/// <summary>
/// Immutable formatting span over UTF-16 indices in one timed-text line.
/// </summary>
public readonly record struct
    MediaPlaybackTimedTextSubformatDescriptor(
        int StartIndex,
        int Length,
        MediaPlaybackTimedTextStyle Style);

/// <summary>
/// Immutable line and formatting-span snapshot. Construction performs O(S)
/// bounded copying for S subformats; reads are O(1).
/// </summary>
public sealed class MediaPlaybackTimedTextLineDescriptor
{
    private static readonly ReadOnlyCollection<
        MediaPlaybackTimedTextSubformatDescriptor>
        s_emptySubformats =
            Array.AsReadOnly(
                Array.Empty<
                    MediaPlaybackTimedTextSubformatDescriptor>());

    private readonly ReadOnlyCollection<
        MediaPlaybackTimedTextSubformatDescriptor>
        _subformats;

    public MediaPlaybackTimedTextLineDescriptor(
        string? text,
        IReadOnlyList<
            MediaPlaybackTimedTextSubformatDescriptor>?
            subformats = null)
    {
        Text = text ?? string.Empty;
        if (subformats is null || subformats.Count == 0)
        {
            _subformats = s_emptySubformats;
            return;
        }

        var copy =
            new MediaPlaybackTimedTextSubformatDescriptor[
                subformats.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            MediaPlaybackTimedTextSubformatDescriptor
                subformat = subformats[index];
            if (subformat.StartIndex < 0 ||
                subformat.Length < 0 ||
                subformat.StartIndex >
                    Text.Length - subformat.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(subformats),
                    "Timed-text subformats must be contained within their line.");
            }
            copy[index] = subformat;
        }
        _subformats = Array.AsReadOnly(copy);
    }

    public string Text { get; }

    public IReadOnlyList<
        MediaPlaybackTimedTextSubformatDescriptor>
        Subformats => _subformats;
}

/// <summary>
/// Immutable provider-neutral cue-box layout. WebVTT line numbers remain in
/// line units rather than being incorrectly converted to device pixels.
/// </summary>
public readonly record struct
    MediaPlaybackTimedTextCueLayout(
        string? RegionName = null,
        double? LinePosition = null,
        MediaPlaybackTimedTextLinePositionUnit
            LinePositionUnit =
                MediaPlaybackTimedTextLinePositionUnit.Lines,
        MediaPlaybackTimedTextAlignment? LineAlignment = null,
        double? TextPositionPercentage = null,
        MediaPlaybackTimedTextAlignment?
            PositionAlignment = null,
        double? SizePercentage = null,
        MediaPlaybackTimedTextAlignment? TextAlignment = null,
        MediaPlaybackTimedTextWritingMode? WritingMode = null);

/// <summary>
/// Immutable provider-neutral presentation snapshot for one timed-text cue.
/// Construction performs O(L) bounded copying for L immutable lines; reads are
/// O(1). Parsing and allocation happen only when providers publish cue state,
/// never in audio or video frame processing.
/// </summary>
public sealed class MediaPlaybackTimedTextCuePresentation
{
    private static readonly ReadOnlyCollection<
        MediaPlaybackTimedTextLineDescriptor> s_emptyLines =
            Array.AsReadOnly(
                Array.Empty<
                    MediaPlaybackTimedTextLineDescriptor>());

    private readonly ReadOnlyCollection<
        MediaPlaybackTimedTextLineDescriptor> _lines;

    public MediaPlaybackTimedTextCuePresentation(
        IReadOnlyList<
            MediaPlaybackTimedTextLineDescriptor>? lines,
        MediaPlaybackTimedTextStyle style = default,
        MediaPlaybackTimedTextCueLayout layout = default)
    {
        ValidateLayout(in layout);
        Style = style;
        Layout = layout with
        {
            RegionName = layout.RegionName ?? string.Empty
        };
        if (lines is null || lines.Count == 0)
        {
            _lines = s_emptyLines;
            return;
        }

        var copy =
            new MediaPlaybackTimedTextLineDescriptor[
                lines.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = lines[index] ??
                throw new ArgumentException(
                    "Timed-text presentation lines cannot contain null.",
                    nameof(lines));
        }
        _lines = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<
        MediaPlaybackTimedTextLineDescriptor> Lines =>
        _lines;

    public MediaPlaybackTimedTextStyle Style { get; }

    public MediaPlaybackTimedTextCueLayout Layout { get; }

    private static void ValidateLayout(
        in MediaPlaybackTimedTextCueLayout layout)
    {
        ValidateFinite(
            layout.LinePosition,
            nameof(layout.LinePosition));
        ValidatePercentage(
            layout.TextPositionPercentage,
            nameof(layout.TextPositionPercentage));
        ValidatePercentage(
            layout.SizePercentage,
            nameof(layout.SizePercentage));
        if (layout.LinePositionUnit ==
                MediaPlaybackTimedTextLinePositionUnit
                    .Percentage &&
            layout.LinePosition is double linePercentage &&
            (linePercentage < 0d ||
             linePercentage > 100d))
        {
            throw new ArgumentOutOfRangeException(
                nameof(layout),
                "A percentage line position must be between 0 and 100.");
        }
    }

    private static void ValidatePercentage(
        double? value,
        string propertyName)
    {
        ValidateFinite(value, propertyName);
        if (value is double percentage &&
            (percentage < 0d || percentage > 100d))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"{propertyName} must be between 0 and 100.");
        }
    }

    private static void ValidateFinite(
        double? value,
        string propertyName)
    {
        if (value is double scalar &&
            !double.IsFinite(scalar))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"{propertyName} must be finite.");
        }
    }
}
