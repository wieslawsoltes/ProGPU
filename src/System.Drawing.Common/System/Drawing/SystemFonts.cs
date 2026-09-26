namespace System.Drawing;

public static class SystemFonts
{
    public static Font DefaultFont => Create(nameof(DefaultFont));

    public static Font DialogFont => Create(nameof(DialogFont));

    public static Font MenuFont => Create(nameof(MenuFont));

    public static Font MessageBoxFont => Create(nameof(MessageBoxFont));

    public static Font StatusFont => Create(nameof(StatusFont));

    public static Font CaptionFont => Create(nameof(CaptionFont));

    public static Font IconTitleFont => Create(nameof(IconTitleFont));

    public static Font SmallCaptionFont => Create(nameof(SmallCaptionFont));

    public static Font? GetFontByName(string? systemFontName)
    {
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
            _ => null
        };
    }

    private static Font Create(string role)
    {
        // Keep the existing portable font-selection and size policy. Only the
        // caller-owned wrapper and its role identity are new for each request.
        using FontFamily family = FontFamily.GenericSansSerif;
        return Font.CreateSystemFont(family, 8.25f, role);
    }
}
