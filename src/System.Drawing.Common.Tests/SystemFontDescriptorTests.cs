using System.Drawing.Interop;
using System.Runtime.InteropServices;
using Xunit;

namespace System.Drawing.Tests;

public sealed class SystemFontDescriptorTests
{
    [Theory]
    [InlineData(FontStyle.Regular, 1, false)]
    [InlineData(FontStyle.Bold | FontStyle.Italic, 128, true)]
    [InlineData(FontStyle.Underline | FontStyle.Strikeout, 2, false)]
    public void OwnedSystemFontRetainsCompleteDescriptor(FontStyle style, byte charSet, bool vertical)
    {
        using FontFamily family = FontFamily.GenericSansSerif;
        using Font font = Font.CreateSystemFont(family, 11.25f, style, charSet, vertical, nameof(SystemFonts.MenuFont));
        family.Dispose();
        Assert.Equal(11.25f, font.Size);
        Assert.Equal(GraphicsUnit.Point, font.Unit);
        Assert.Equal(style, font.Style);
        Assert.Equal(charSet, font.GdiCharSet);
        Assert.Equal(vertical, font.GdiVerticalFont);
        Assert.Equal(nameof(SystemFonts.MenuFont), font.SystemFontName);
        Assert.True(font.IsSystemFont);
        using Font clone = Assert.IsType<Font>(font.Clone());
        Assert.Equal(font, clone);
        Assert.False(clone.IsSystemFont);
        font.Dispose();
        Assert.True(clone.GetHeight() > 0);
    }

    [Fact]
    public void NativeSettingsLayoutsMatchWindowsAbi()
    {
        Assert.Equal(92, Marshal.SizeOf<LOGFONT>());
        Assert.Equal(504, Marshal.SizeOf<WindowsSystemFontDescriptor.NonClientMetrics>());
        Assert.Equal((nint)24, Marshal.OffsetOf<WindowsSystemFontDescriptor.NonClientMetrics>("CaptionFont"));
        Assert.Equal((nint)124, Marshal.OffsetOf<WindowsSystemFontDescriptor.NonClientMetrics>("SmallCaptionFont"));
        Assert.Equal((nint)224, Marshal.OffsetOf<WindowsSystemFontDescriptor.NonClientMetrics>("MenuFont"));
        Assert.Equal((nint)316, Marshal.OffsetOf<WindowsSystemFontDescriptor.NonClientMetrics>("StatusFont"));
        Assert.Equal((nint)408, Marshal.OffsetOf<WindowsSystemFontDescriptor.NonClientMetrics>("MessageFont"));
        Assert.Equal((nint)500, Marshal.OffsetOf<WindowsSystemFontDescriptor.NonClientMetrics>("PaddedBorderWidth"));
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Marshal.SizeOf<WindowsSystemFontDescriptor.StartupInput>());
    }
}
