using ProGPU.Text;
using System.Drawing.Text;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
        (long allocated, int total) = MeasureIsolatedPrivateMetrics(allocateControl: false);

        Assert.True(total > 0);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void PrivateMetricAllocationMeasurementDetectsEscapingObjects()
    {
        (long allocated, int total) = MeasureIsolatedPrivateMetrics(allocateControl: true);

        Assert.True(total > 0);
        Assert.True(allocated >= 1000 * IntPtr.Size,
            "The measurement must detect every deliberately escaping allocation.");
    }

    private static object? s_metricAllocationControl;

    private static (long Allocated, int Total) MeasureIsolatedPrivateMetrics(bool allocateControl)
    {
        using var collection = new PrivateFontCollection();
        collection.AddFontFile(FontPath);
        using FontFamily family = Assert.Single(collection.Families);

        (long Allocated, int Total) result = (-1, 0);
        ExceptionDispatchInfo? failure = null;
        // Keep font loading, test-runner setup and assertions off the measured
        // thread. The original warmup and all 1,000 iterations remain intact;
        // GC stays enabled and the measurement is never retried or discounted.
        var worker = new Thread(() =>
        {
            try { result = MeasurePrivateMetrics(family, allocateControl); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "The private font metric measurement did not finish.");
        failure?.Throw();
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Allocated, int Total) MeasurePrivateMetrics(FontFamily family, bool allocateControl)
    {
        _ = family.GetEmHeight(FontStyle.Regular);
        _ = family.GetCellAscent(FontStyle.Regular);
        _ = family.GetCellDescent(FontStyle.Regular);
        _ = family.GetLineSpacing(FontStyle.Regular);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int total = 0;
        for (int index = 0; index < 1000; index++)
        {
            if (allocateControl)
                Volatile.Write(ref s_metricAllocationControl, new object());
            total += family.GetEmHeight(FontStyle.Regular);
            total += family.GetCellAscent(FontStyle.Regular);
            total += family.GetCellDescent(FontStyle.Regular);
            total += family.GetLineSpacing(FontStyle.Regular);
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before, total);
    }
}
