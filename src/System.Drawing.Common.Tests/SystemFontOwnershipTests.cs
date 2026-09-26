using Xunit;

namespace System.Drawing.Tests;

public sealed class SystemFontOwnershipTests
{
    [Theory]
    [InlineData(nameof(SystemFonts.DefaultFont))]
    [InlineData(nameof(SystemFonts.DialogFont))]
    [InlineData(nameof(SystemFonts.MenuFont))]
    [InlineData(nameof(SystemFonts.MessageBoxFont))]
    [InlineData(nameof(SystemFonts.StatusFont))]
    [InlineData(nameof(SystemFonts.CaptionFont))]
    [InlineData(nameof(SystemFonts.IconTitleFont))]
    [InlineData(nameof(SystemFonts.SmallCaptionFont))]
    public void NamedFontsHaveIndependentLifetimeAndCorrectIdentity(string name)
    {
        using Font first = GetProperty(name);
        using Font second = GetProperty(name);
        Assert.NotSame(first, second);
        Assert.Equal(first, second);
        Assert.True(first.IsSystemFont);
        Assert.Equal(name, first.SystemFontName);
        Assert.True(second.IsSystemFont);
        Assert.Equal(name, second.SystemFontName);
        Assert.Equal(8.25f, first.Size);
        Assert.Equal(GraphicsUnit.Point, first.Unit);

        float height = first.GetHeight();
        first.Dispose();
        Assert.Throws<ArgumentException>(() => first.Clone());
        using Font survivingCopy = Assert.IsType<Font>(second.Clone());
        Assert.Equal(second, survivingCopy);
        Assert.Equal(height, second.GetHeight());
        using Font following = GetProperty(name);
        Assert.Equal(height, following.GetHeight());
        Assert.NotSame(second, following);
        using Font byName = Assert.IsType<Font>(SystemFonts.GetFontByName(name));
        Assert.NotSame(following, byName);
        Assert.Equal(following, byName);
        Assert.Equal(name, byName.SystemFontName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("captionfont")]
    [InlineData("UnrecognizedFont")]
    public void UnknownNamesReturnNull(string? name)
        => Assert.Null(SystemFonts.GetFontByName(name!));

    [Fact]
    public void DifferentRolesAndOrdinaryCopiesDoNotShareSystemIdentity()
    {
        using Font caption = SystemFonts.CaptionFont;
        using Font menu = SystemFonts.MenuFont;
        using Font copy = Assert.IsType<Font>(caption.Clone());
        using Font prototype = new(caption, caption.Style);
        using Font ordinary = new(caption.FontFamily, caption.Size, caption.Style, caption.Unit);

        Assert.NotSame(caption, menu);
        Assert.Equal(nameof(SystemFonts.CaptionFont), caption.SystemFontName);
        Assert.Equal(nameof(SystemFonts.MenuFont), menu.SystemFontName);
        foreach (Font item in new[] { copy, prototype, ordinary })
        {
            Assert.False(item.IsSystemFont);
            Assert.Equal(string.Empty, item.SystemFontName);
            Assert.Equal(caption, item);
        }
    }

    private static Font GetProperty(string name) => name switch
    {
        nameof(SystemFonts.DefaultFont) => SystemFonts.DefaultFont,
        nameof(SystemFonts.DialogFont) => SystemFonts.DialogFont,
        nameof(SystemFonts.MenuFont) => SystemFonts.MenuFont,
        nameof(SystemFonts.MessageBoxFont) => SystemFonts.MessageBoxFont,
        nameof(SystemFonts.StatusFont) => SystemFonts.StatusFont,
        nameof(SystemFonts.CaptionFont) => SystemFonts.CaptionFont,
        nameof(SystemFonts.IconTitleFont) => SystemFonts.IconTitleFont,
        nameof(SystemFonts.SmallCaptionFont) => SystemFonts.SmallCaptionFont,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
}
