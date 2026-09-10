namespace System.Drawing;

public static class SystemFonts
{
    public static Font DefaultFont { get; } = new(FontFamily.GenericSansSerif, 8.25f);

    public static Font DialogFont => DefaultFont;

    public static Font MenuFont => DefaultFont;

    public static Font MessageBoxFont => DefaultFont;

    public static Font StatusFont => DefaultFont;

    public static Font CaptionFont => DefaultFont;

    public static Font IconTitleFont => DefaultFont;

    public static Font SmallCaptionFont => DefaultFont;

    public static Font GetFontByName(string systemFontName)
    {
        ArgumentNullException.ThrowIfNull(systemFontName);
        return systemFontName switch
        {
            nameof(CaptionFont) => CaptionFont,
            nameof(DefaultFont) => DefaultFont,
            nameof(DialogFont) => DialogFont,
            nameof(IconTitleFont) => IconTitleFont,
            nameof(MenuFont) => MenuFont,
            nameof(MessageBoxFont) => MessageBoxFont,
            nameof(SmallCaptionFont) => SmallCaptionFont,
            nameof(StatusFont) => StatusFont,
            _ => DefaultFont
        };
    }
}
