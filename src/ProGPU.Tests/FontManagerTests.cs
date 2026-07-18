using ProGPU.Fonts.Inter;
using ProGPU.Text;
using System.Reflection;
using Xunit;

namespace ProGPU.Tests;

public sealed class FontManagerTests
{
    [Fact]
    public void RegisteredFacesStayLazyUntilMatchedAndExposeStyleMetadata()
    {
        var manager = new FontManager();
        var loadCount = 0;
        var lazy = new Lazy<TtfFont>(() =>
        {
            loadCount++;
            return InterFontFamily.Regular;
        });

        const string family = "ProGPU Test Inter";
        manager.RegisterFont(family, lazy, new FontStyleRequest(400, 5, FontSlant.Upright));

        Assert.Contains(family, manager.FontFamilies);
        FontFace face = Assert.Single(manager.GetFontStyles(family));
        Assert.Equal(0, loadCount);
        Assert.Equal(400, face.Style.Weight);

        TtfFont matched = Assert.IsType<TtfFont>(manager.MatchFamily(family));
        Assert.Same(InterFontFamily.Regular, matched);
        Assert.Equal(1, loadCount);
        Assert.Same(matched, manager.MatchFamily(family));
        Assert.Equal(1, loadCount);
    }

    [Fact]
    public void TypefaceStyleMatchingFallsBackToOriginalFaceWhenFamilyHasNoCloserStyle()
    {
        var manager = new FontManager();
        TtfFont regular = InterFontFamily.Regular;
        manager.RegisterFont(regular);

        TtfFont matched = manager.MatchTypeface(regular, new FontStyleRequest(700, 5, FontSlant.Italic));

        Assert.Same(regular, matched);
    }

    [Fact]
    public void RegisteringACloserFaceInvalidatesPriorStyleMatch()
    {
        var manager = new FontManager();
        TtfFont regular = InterFontFamily.Regular;
        manager.RegisterFont(regular);
        var boldStyle = new FontStyleRequest(700, 5, FontSlant.Upright);

        Assert.Same(regular, manager.MatchTypeface(regular, boldStyle));

        var bold = new Lazy<TtfFont>(() => InterFontFamily.Bold);
        manager.RegisterFont(InterFontFamily.TextFamilyName, bold, boldStyle);

        Assert.Same(InterFontFamily.Bold, manager.MatchTypeface(regular, boldStyle));
    }

    [Fact]
    public void FailedRegisteredFaceIsNotRetried()
    {
        var manager = new FontManager();
        var loadCount = 0;
        var broken = new Lazy<TtfFont>(
            () =>
            {
                loadCount++;
                throw new InvalidDataException("Invalid optional font.");
            },
            LazyThreadSafetyMode.PublicationOnly);
        manager.RegisterFont("Broken optional font", broken);

        Assert.Null(manager.MatchFamily("Broken optional font"));
        Assert.Null(manager.MatchFamily("Broken optional font"));
        Assert.Equal(1, loadCount);
    }

    [Fact]
    public void LegacyLazyFallbackLoaderSkipsNonMemoryFailures()
    {
        MethodInfo? loader = typeof(FontApi).GetMethod(
            "TryLoadLazyPlatformFallback",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(loader);
        var broken = new Lazy<TtfFont>(
            static () => throw new InvalidDataException("Invalid optional font."));

        object? result = loader.Invoke(null, [broken]);

        Assert.Null(result);
    }
}
