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
        if (OperatingSystem.IsWindows())
        {
            // Only scalar metadata crosses the OS boundary. Font ownership,
            // discovery, shaping and rendering remain portable ProGPU objects.
            WindowsSystemFontDescriptor descriptor = WindowsSystemFontDescriptor.Read(role);
            using FontFamily nativeFamily = new(descriptor.FamilyName);
            return Font.CreateSystemFont(nativeFamily, descriptor.SizeInPoints, descriptor.Style,
                descriptor.CharSet, descriptor.Vertical, role);
        }

        // Preserve the existing non-Windows policy until its platform settings
        // contract is implemented independently.
        using FontFamily family = FontFamily.GenericSansSerif;
        return Font.CreateSystemFont(family, 8.25f, role);
    }
}
