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
    public void StaticTypefaceStyleCacheStaysBoundedAcrossOpticalSizes()
    {
        var manager = new FontManager();
        TtfFont regular = InterFontFamily.Regular;
        manager.RegisterFont(regular);

        for (var index = 0; index < 1_000; index++)
        {
            Assert.Same(
                regular,
                manager.MatchTypeface(
                    regular,
                    new FontStyleRequest(700, 5, FontSlant.Upright)
                    {
                        OpticalSize = 8f + index * 0.125f
                    }));
        }

        FieldInfo? cacheField = typeof(FontManager).GetField(
            "_styleMatches",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(cacheField);
        var cache = Assert.IsType<
            System.Collections.Concurrent.ConcurrentDictionary<
                (TtfFont Font, FontStyleRequest Style), TtfFont>>(
                    cacheField.GetValue(manager));
        Assert.InRange(cache.Count, 1, 256);
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
    public void RepeatedCharacterFallbackUsesBoundedAllocationFreeMatchCache()
    {
        var manager = new FontManager();
        manager.RegisterFont(
            "ProGPU Cached Fallback",
            new Lazy<TtfFont>(() => InterFontFamily.Regular),
            FontStyleRequest.Normal,
            isFallback: true);

        Assert.True(manager.TryMatchCharacter(
            "ProGPU Cached Fallback",
            FontStyleRequest.Normal,
            null,
            'A',
            out TtfFont? initial,
            out ushort initialGlyph));
        Assert.Same(InterFontFamily.Regular, initial);
        Assert.NotEqual((ushort)0, initialGlyph);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allMatched = true;
        for (int index = 0; index < 1_000; index++)
        {
            bool matched = manager.TryMatchCharacter(
                "ProGPU Cached Fallback",
                FontStyleRequest.Normal,
                null,
                'A',
                out TtfFont? repeated,
                out ushort repeatedGlyph);
            allMatched &= matched && ReferenceEquals(initial, repeated) && initialGlyph == repeatedGlyph;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allMatched);
        Assert.InRange(allocated, 0, 4_096);
    }

    [Fact]
    public void RegisteringFontInvalidatesCachedCharacterMatch()
    {
        var manager = new FontManager();
        const string family = "ProGPU Dynamic Character Match";

        Assert.True(manager.TryMatchCharacter(
            family,
            FontStyleRequest.Normal,
            null,
            'A',
            out TtfFont? original,
            out _));
        Assert.NotNull(original);

        manager.RegisterFont(
            family,
            new Lazy<TtfFont>(() => InterFontFamily.Regular),
            FontStyleRequest.Normal);

        Assert.True(manager.TryMatchCharacter(
            family,
            FontStyleRequest.Normal,
            null,
            'A',
            out TtfFont? updated,
            out ushort glyph));
        Assert.Same(InterFontFamily.Regular, updated);
        Assert.NotEqual((ushort)0, glyph);
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
