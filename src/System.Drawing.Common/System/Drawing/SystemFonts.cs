namespace System.Drawing;

public static class SystemFonts
{
    public static Font DefaultFont { get; } = new(FontFamily.GenericSansSerif, 8.25f);

    public static Font DialogFont => DefaultFont;

    public static Font MenuFont => DefaultFont;

    public static Font MessageBoxFont => DefaultFont;

    public static Font StatusFont => DefaultFont;
}
