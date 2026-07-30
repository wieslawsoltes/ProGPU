using ProGPU.Media.Playback;
using Windows.UI;

namespace Windows.Media.Core;

public enum TimedTextUnit
{
    Pixels = 0,
    Percentage = 1
}

public enum TimedTextDisplayAlignment
{
    Before = 0,
    After = 1,
    Center = 2
}

public enum TimedTextLineAlignment
{
    Start = 0,
    End = 1,
    Center = 2
}

public enum TimedTextWritingMode
{
    LeftRightTopBottom = 0,
    RightLeftTopBottom = 1,
    TopBottomRightLeft = 2,
    TopBottomLeftRight = 3,
    LeftRight = 4,
    RightLeft = 5,
    TopBottom = 6
}

public enum TimedTextWrapping
{
    NoWrap = 0,
    Wrap = 1
}

public enum TimedTextScrollMode
{
    Popon = 0,
    Rollup = 1
}

public enum TimedTextFlowDirection
{
    LeftToRight = 0,
    RightToLeft = 1
}

public enum TimedTextFontStyle
{
    Normal = 0,
    Oblique = 1,
    Italic = 2
}

public enum TimedTextWeight
{
    Normal = 400,
    Bold = 700
}

public enum TimedTextBoutenPosition
{
    Before = 0,
    After = 1,
    Outside = 2
}

public enum TimedTextBoutenType
{
    None = 0,
    Auto = 1,
    FilledCircle = 2,
    OpenCircle = 3,
    FilledDot = 4,
    OpenDot = 5,
    FilledSesame = 6,
    OpenSesame = 7
}

public enum TimedTextRubyPosition
{
    Before = 0,
    After = 1,
    Outside = 2
}

public enum TimedTextRubyReserve
{
    None = 0,
    Before = 1,
    After = 2,
    Both = 3,
    Outside = 4
}

public enum TimedTextRubyAlign
{
    Center = 0,
    Start = 1,
    End = 2,
    SpaceAround = 3,
    SpaceBetween = 4,
    WithBase = 5
}

public struct TimedTextPoint
{
    public double X;
    public double Y;
    public TimedTextUnit Unit;
}

public struct TimedTextSize
{
    public double Height;
    public double Width;
    public TimedTextUnit Unit;
}

public struct TimedTextDouble
{
    public double Value;
    public TimedTextUnit Unit;
}

public struct TimedTextPadding
{
    public double Before;
    public double After;
    public double Start;
    public double End;
    public TimedTextUnit Unit;
}

public sealed class TimedTextBouten
{
    internal TimedTextBouten()
    {
    }

    public TimedTextBoutenType Type { get; set; }

    public TimedTextBoutenPosition Position { get; set; }

    public Color Color { get; set; }
}

public sealed class TimedTextRuby
{
    private string _text = string.Empty;

    internal TimedTextRuby()
    {
    }

    public string Text
    {
        get => _text;
        set => _text = value ?? string.Empty;
    }

    public TimedTextRubyReserve Reserve { get; set; }

    public TimedTextRubyPosition Position { get; set; }

    public TimedTextRubyAlign Align { get; set; }
}

/// <summary>
/// WinUI-aligned timed-text style. Provider projection mutates retained style
/// objects rather than replacing them for every cue snapshot.
/// </summary>
public sealed class TimedTextStyle
{
    private string _fontFamily = string.Empty;
    private string _name = string.Empty;

    public TimedTextStyle()
    {
        Bouten = new TimedTextBouten();
        Ruby = new TimedTextRuby();
        FontWeight = TimedTextWeight.Normal;
    }

    public Color Background { get; set; }

    public TimedTextBouten Bouten { get; }

    public TimedTextFlowDirection FlowDirection { get; set; }

    public double FontAngleInDegrees { get; set; }

    public string FontFamily
    {
        get => _fontFamily;
        set => _fontFamily = value ?? string.Empty;
    }

    public TimedTextDouble FontSize { get; set; }

    public TimedTextFontStyle FontStyle { get; set; }

    public TimedTextWeight FontWeight { get; set; }

    public Color Foreground { get; set; }

    public bool IsBackgroundAlwaysShown { get; set; }

    public bool IsLineThroughEnabled { get; set; }

    public bool IsOverlineEnabled { get; set; }

    public bool IsTextCombined { get; set; }

    public bool IsUnderlineEnabled { get; set; }

    public TimedTextLineAlignment LineAlignment { get; set; }

    public string Name
    {
        get => _name;
        set => _name = value ?? string.Empty;
    }

    public Color OutlineColor { get; set; }

    public TimedTextDouble OutlineRadius { get; set; }

    public TimedTextDouble OutlineThickness { get; set; }

    public TimedTextRuby Ruby { get; }

    internal bool ApplyProviderStyle(
        in MediaPlaybackTimedTextStyle style,
        MediaPlaybackTimedTextAlignment?
            alignment = null)
    {
        TimedTextFontStyle fontStyle =
            (TimedTextFontStyle)(
                style.FontStyle ??
                MediaPlaybackTimedTextFontStyle.Normal);
        TimedTextWeight fontWeight =
            (TimedTextWeight)(
                style.FontWeight ??
                MediaPlaybackTimedTextWeight.Normal);
        bool underline =
            style.IsUnderlineEnabled ?? false;
        TimedTextLineAlignment lineAlignment =
            ToLineAlignment(alignment);
        bool changed =
            FontStyle != fontStyle ||
            FontWeight != fontWeight ||
            IsUnderlineEnabled != underline ||
            LineAlignment != lineAlignment;
        FontStyle = fontStyle;
        FontWeight = fontWeight;
        IsUnderlineEnabled = underline;
        LineAlignment = lineAlignment;
        return changed;
    }

    internal static TimedTextLineAlignment ToLineAlignment(
        MediaPlaybackTimedTextAlignment? alignment) =>
        alignment switch
        {
            MediaPlaybackTimedTextAlignment.End or
            MediaPlaybackTimedTextAlignment.Right =>
                TimedTextLineAlignment.End,
            MediaPlaybackTimedTextAlignment.Center =>
                TimedTextLineAlignment.Center,
            _ => TimedTextLineAlignment.Start
        };
}

/// <summary>
/// WinUI-aligned timed-text region.
/// </summary>
public sealed class TimedTextRegion
{
    private string _name = string.Empty;

    public Color Background { get; set; }

    public TimedTextDisplayAlignment DisplayAlignment
    {
        get;
        set;
    }

    public TimedTextSize Extent { get; set; }

    public bool IsOverflowClipped { get; set; }

    public TimedTextDouble LineHeight { get; set; }

    public string Name
    {
        get => _name;
        set => _name = value ?? string.Empty;
    }

    public TimedTextPadding Padding { get; set; }

    public TimedTextPoint Position { get; set; }

    public TimedTextScrollMode ScrollMode { get; set; }

    public TimedTextWrapping TextWrapping { get; set; }

    public TimedTextWritingMode WritingMode { get; set; }

    public int ZIndex { get; set; }

    internal bool ApplyProviderLayout(
        in MediaPlaybackTimedTextCueLayout layout)
    {
        TimedTextWritingMode writingMode =
            (TimedTextWritingMode)(
                layout.WritingMode ??
                MediaPlaybackTimedTextWritingMode
                    .LeftRightTopBottom);
        TimedTextDisplayAlignment displayAlignment =
            layout.LineAlignment switch
            {
                MediaPlaybackTimedTextAlignment.End or
                MediaPlaybackTimedTextAlignment.Right =>
                    TimedTextDisplayAlignment.After,
                MediaPlaybackTimedTextAlignment.Center =>
                    TimedTextDisplayAlignment.Center,
                _ => TimedTextDisplayAlignment.Before
            };
        double size =
            layout.SizePercentage ?? 100d;
        double textPosition =
            layout.TextPositionPercentage ?? 50d;
        double adjustedTextPosition =
            GetBoxStart(
                textPosition,
                size,
                layout.PositionAlignment ??
                layout.TextAlignment);
        double linePosition =
            layout.LinePositionUnit ==
                    MediaPlaybackTimedTextLinePositionUnit
                        .Percentage
                ? layout.LinePosition ?? 100d
                : 100d;
        bool vertical =
            writingMode is
                TimedTextWritingMode.TopBottomRightLeft or
                TimedTextWritingMode.TopBottomLeftRight or
                TimedTextWritingMode.TopBottom;
        TimedTextPoint position = vertical
            ? new TimedTextPoint
            {
                X = linePosition,
                Y = adjustedTextPosition,
                Unit = TimedTextUnit.Percentage
            }
            : new TimedTextPoint
            {
                X = adjustedTextPosition,
                Y = linePosition,
                Unit = TimedTextUnit.Percentage
            };
        TimedTextSize extent = vertical
            ? new TimedTextSize
            {
                Width = 0d,
                Height = size,
                Unit = TimedTextUnit.Percentage
            }
            : new TimedTextSize
            {
                Width = size,
                Height = 0d,
                Unit = TimedTextUnit.Percentage
            };
        string name = layout.RegionName ?? string.Empty;
        bool changed =
            !StringComparer.Ordinal.Equals(Name, name) ||
            WritingMode != writingMode ||
            DisplayAlignment != displayAlignment ||
            !Position.Equals(position) ||
            !Extent.Equals(extent);
        Name = name;
        WritingMode = writingMode;
        DisplayAlignment = displayAlignment;
        Position = position;
        Extent = extent;
        return changed;
    }

    private static double GetBoxStart(
        double position,
        double size,
        MediaPlaybackTimedTextAlignment? alignment)
    {
        double start = alignment switch
        {
            MediaPlaybackTimedTextAlignment.Center =>
                position - size * 0.5d,
            MediaPlaybackTimedTextAlignment.End or
            MediaPlaybackTimedTextAlignment.Right =>
                position - size,
            _ => position
        };
        return Math.Clamp(
            start,
            0d,
            Math.Max(0d, 100d - size));
    }
}

/// <summary>
/// WinUI-aligned formatting span over one timed-text line.
/// </summary>
public sealed class TimedTextSubformat
{
    private TimedTextStyle _subformatStyle = new();

    public int Length { get; set; }

    public int StartIndex { get; set; }

    public TimedTextStyle SubformatStyle
    {
        get => _subformatStyle;
        set => _subformatStyle =
            value ?? new TimedTextStyle();
    }

    internal bool ApplyProviderState(
        in MediaPlaybackTimedTextSubformatDescriptor
            descriptor)
    {
        bool changed =
            StartIndex != descriptor.StartIndex ||
            Length != descriptor.Length;
        StartIndex = descriptor.StartIndex;
        Length = descriptor.Length;
        MediaPlaybackTimedTextStyle style =
            descriptor.Style;
        return _subformatStyle.ApplyProviderStyle(
                   in style) ||
               changed;
    }
}
