using ProGPU.Text;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Xunit;

namespace System.Drawing.Tests;

public sealed class FontQualityTests
{
    private static string FontPath => Path.Combine(AppContext.BaseDirectory, "Fonts", "Inter-Regular.ttf");

    [Fact]
    public void PrivateCollectionUsesExactTypedFamilyAndRealMetrics()
    {
        using var collection = new PrivateFontCollection();
        collection.AddFontFile(FontPath);
        using FontFamily listed = Assert.Single(collection.Families);
        using var family = new FontFamily(listed.Name.ToLowerInvariant(), collection);
        var expected = new TtfFont(FontPath);

        Assert.Equal(expected.FamilyName, family.Name);
        Assert.True(family.IsStyleAvailable(FontStyle.Regular));
        Assert.False(family.IsStyleAvailable(FontStyle.Bold));
        Assert.Equal(expected.UnitsPerEm, family.GetEmHeight(FontStyle.Regular));
        Assert.Equal(expected.Ascender, family.GetCellAscent(FontStyle.Regular));
        Assert.Equal(-expected.Descender, family.GetCellDescent(FontStyle.Regular));
        Assert.Equal(expected.Ascender - expected.Descender + expected.LineGap, family.GetLineSpacing(FontStyle.Regular));
    }

    [Fact]
    public void PublicFamilyDoesNotKeepARequestedNameForFallbackData()
    {
        Assert.Throws<ArgumentException>(() => new FontFamily("ProGPU definitely missing family"));

        using var fallback = new Font("ProGPU definitely missing family", 12f);
        Assert.NotEqual("ProGPU definitely missing family", fallback.Name);
        Assert.Equal("ProGPU definitely missing family", fallback.OriginalFontName);
    }

    [Fact]
    public void MemoryFontCopiesCallerStorageAndSurvivesCollection()
    {
        byte[] bytes = File.ReadAllBytes(FontPath);
        IntPtr memory = Marshal.AllocCoTaskMem(bytes.Length);
        FontFamily family;
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            using var collection = new PrivateFontCollection();
            collection.AddMemoryFont(memory, bytes.Length);
            using FontFamily listed = Assert.Single(collection.Families);
            family = new FontFamily(listed.Name, collection);
            Marshal.WriteByte(memory, 0, 0);
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }

        using (family)
        using (var font = new Font(family, 13f))
        {
            Assert.Equal(new TtfFont(bytes).FamilyName, font.Name);
            Assert.True(font.GetHeight() > 0);
        }
    }

    [Fact]
    public void ExistingNonFontFileDoesNotInventAFamily()
    {
        using var collection = new PrivateFontCollection();

        collection.AddFontFile(typeof(Font).Assembly.Location);

        Assert.Empty(collection.Families);
    }

    [Fact]
    public void FontSnapshotsFamilyAndCloneHasIndependentLifetime()
    {
        using var collection = new PrivateFontCollection();
        collection.AddFontFile(FontPath);
        using FontFamily listed = Assert.Single(collection.Families);
        var family = new FontFamily(listed.Name, collection);
        var font = new Font(family, 11f, FontStyle.Italic | FontStyle.Underline);
        family.Dispose();
        collection.Dispose();
        var clone = Assert.IsType<Font>(font.Clone());
        font.Dispose();

        Assert.Equal(FontStyle.Italic | FontStyle.Underline, clone.Style);
        Assert.Equal(listed.Name, clone.Name);
        Assert.True(clone.GetHeight(120f) > 0);
        clone.Dispose();
        Assert.Throws<ArgumentException>(() => clone.Clone());
    }

    [Theory]
    [InlineData(0f, GraphicsUnit.Point)]
    [InlineData(-1f, GraphicsUnit.Point)]
    [InlineData(float.NaN, GraphicsUnit.Point)]
    [InlineData(float.PositiveInfinity, GraphicsUnit.Point)]
    [InlineData(12f, GraphicsUnit.Display)]
    [InlineData(12f, (GraphicsUnit)7)]
    public void ConstructorsRejectInvalidSizeAndUnit(float size, GraphicsUnit unit)
    {
        using var collection = new PrivateFontCollection();
        collection.AddFontFile(FontPath);
        using FontFamily family = Assert.Single(collection.Families);

        Assert.Throws<ArgumentException>(() => new Font(family, size, unit));
        Assert.Throws<ArgumentException>(() => new Font(family.Name, size, unit));
    }

    [Fact]
    public void CollectionsHaveTheirDocumentedDisposalBehavior()
    {
        var privateFonts = new PrivateFontCollection();
        privateFonts.AddFontFile(FontPath);
        privateFonts.Dispose();
        Assert.Throws<ArgumentException>(() => privateFonts.Families);
        Assert.Throws<ArgumentException>(() => privateFonts.AddFontFile(FontPath));

        var installedFonts = new InstalledFontCollection();
        Assert.NotEmpty(installedFonts.Families);
        installedFonts.Dispose();
        Assert.NotEmpty(installedFonts.Families);
    }

    [Fact]
    public void WarmedPrivateMetricReadsAreAllocationFree()
    {
        using var collection = new PrivateFontCollection();
        collection.AddFontFile(FontPath);
        using FontFamily family = Assert.Single(collection.Families);
        _ = family.GetLineSpacing(FontStyle.Regular);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int total = 0;
        for (int index = 0; index < 1000; index++)
        {
            total += family.GetEmHeight(FontStyle.Regular);
            total += family.GetCellAscent(FontStyle.Regular);
            total += family.GetCellDescent(FontStyle.Regular);
            total += family.GetLineSpacing(FontStyle.Regular);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(total > 0);
        Assert.Equal(0, allocated);
    }
}
