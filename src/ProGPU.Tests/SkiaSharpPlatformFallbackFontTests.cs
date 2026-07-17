using ProGPU.Fonts.Inter;
using ProGPU.Text;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkiaSharpPlatformFallbackFontTests
{
    [Fact]
    public void DefaultTypefaceUsesPlatformFontWhenSystemFontsAreUnavailable()
    {
        var fallback = InterFontFamily.Regular;

        var typeface = SKTypeface.ResolveDefaultTypeface([], fallback);

        Assert.Same(fallback, typeface.Font);
        Assert.Equal(fallback.FamilyName, typeface.FamilyName);
        Assert.False(typeface.IsEmpty);
    }

    [Fact]
    public void DefaultTypefaceStillRejectsMissingSystemAndPlatformFonts()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => SKTypeface.ResolveDefaultTypeface([], null));

        Assert.Contains("system or platform fallback fonts", exception.Message, StringComparison.Ordinal);
    }
}
