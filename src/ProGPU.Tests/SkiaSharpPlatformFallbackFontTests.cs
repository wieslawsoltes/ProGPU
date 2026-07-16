using ProGPU.Browser;
using ProGPU.Text;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkiaSharpPlatformFallbackFontTests
{
    [Fact]
    public void DefaultTypefaceUsesPlatformFontWhenSystemFontsAreUnavailable()
    {
        using var stream = typeof(BrowserWindowHost).Assembly.GetManifestResourceStream(
            "ProGPU.Browser.Fonts.Roboto-Regular.ttf");
        Assert.NotNull(stream);
        using var memory = new MemoryStream(checked((int)stream.Length));
        stream.CopyTo(memory);
        var fallback = new TtfFont(memory.ToArray());

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
