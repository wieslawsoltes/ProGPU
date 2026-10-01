using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Text;
using Xunit;

namespace ProGPU.Tests;

// Synthetic immutable records exercise only key/selection ownership. No font,
// native library, GPU device, atlas allocation or pixel qualification is implied.
public sealed class HintedGlyphRasterIdentityTests
{
    private sealed class Owner : IDisposable
    {
        internal int Calls;
        internal int Failures;
        public void Dispose()
        {
            Calls++;
            if (Failures-- > 0) throw new InvalidOperationException("original identity owner fault");
        }
    }

    private static HintedGlyphGeometry Create(Owner owner, float dpi = 2f) => new(dpi,
        [new GpuGlyphRecord { SegmentCount = 1, MinX = -2, MinY = -4, MaxX = 6, MaxY = 8 },
         new GpuGlyphRecord { StartSegment = 1, SegmentCount = 1, MinX = 0, MinY = 0, MaxX = 3, MaxY = 5 }],
        [new GpuSegment { P0 = new(-2, -4), P1 = new(6, 8), SegmentType = 1 },
         new GpuSegment { P0 = Vector2.Zero, P1 = new(3, 5), SegmentType = 1 }],
        [new(0, 5, 11, 0, 2, 1, 0, 0, 8, 9, 1, new(10, 20), new(4, 0)),
         new(1, 4, 32, 0, 2, 0, 1, uint.MaxValue, 7, 8, 1, new(14, 20), new(2, 0)),
         new(2, 3, 11, 0, 1, 0, 0, 0, 6, 7, 1, new(16, 20), new(4, 0)),
         new(3, 2, 12, 0, 1, 1, 1, 1, 5, 6, 1, new(20, 20), new(3, 0))], owner);

    [Fact]
    public void ReorderedRepeatedAndSiblingViewsReuseOriginalOutlineKeys()
    {
        using var original = Create(new Owner());
        using var selected = original.SelectOccurrences([3, 2, 0, 2, 1]);
        using var sibling = original.SelectOccurrences([0, 3]);
        Assert.NotSame(original, selected);
        var resident = new Dictionary<GlyphAtlas.GlyphKey, uint>();
        foreach (var occurrence in original.Occurrences)
            if (occurrence.OutlineIndex != uint.MaxValue)
                resident.TryAdd(new(original, occurrence.OutlineIndex), occurrence.OutlineIndex);
        Assert.Equal(2, resident.Count);
        foreach (var view in new[] { selected, sibling })
        {
            Assert.Same(original.RasterGenerationIdentity, view.RasterGenerationIdentity);
            foreach (var occurrence in view.Occurrences)
            {
                if (occurrence.OutlineIndex == uint.MaxValue) continue;
                var key = new GlyphAtlas.GlyphKey(view, occurrence.OutlineIndex);
                Assert.Equal(occurrence.OutlineIndex, resident[key]);
                Assert.Equal(new GlyphAtlas.GlyphKey(original, occurrence.OutlineIndex).GetHashCode(), key.GetHashCode());
            }
        }
        Assert.NotEqual(new GlyphAtlas.GlyphKey(selected, 0), new GlyphAtlas.GlyphKey(selected, 1));
        Assert.Equal(24, Unsafe.SizeOf<GlyphAtlas.GlyphKey>());
    }

    [Fact]
    public void NestedViewsKeepIdentityAfterParentDisposalAndRetirement()
    {
        var owner = new Owner();
        using var original = Create(owner);
        using var selected = original.SelectOccurrences([3, 0, 1, 2]);
        using var nested = selected.SelectOccurrences([3, 0, 3]);
        var key = new GlyphAtlas.GlyphKey(original, 0);
        var resident = new Dictionary<GlyphAtlas.GlyphKey, int> { [key] = 117 };
        original.Dispose();
        selected.Dispose();
        Assert.True(original.HasRenderStorage);
        Assert.True(selected.HasRenderStorage);
        Assert.Equal(0, owner.Calls);
        Assert.Same(original.RasterGenerationIdentity, nested.RasterGenerationIdentity);
        Assert.Equal(original.DpiScale, nested.DpiScale);
        Assert.Equal(new uint[] { 2, 3, 2 }, nested.Occurrences.ToArray().Select(value => value.PositionedIndex));
        Assert.Equal(117, resident[new(nested, 0)]);
        Assert.Equal(key, new GlyphAtlas.GlyphKey(nested, 0));

        nested.Dispose();
        Assert.Equal(1, owner.Calls);
        Assert.False(original.HasRenderStorage);
        Assert.False(selected.HasRenderStorage);
        Assert.False(nested.HasRenderStorage);
        Assert.Throws<ObjectDisposedException>(() => { _ = nested.RenderOutlines.Length; });
        // Key equality/hash and dictionary lookup use the opaque identity only;
        // they remain safe after every source use and storage admission retire.
        Assert.Equal(117, resident[new(nested, 0)]);
        Assert.Equal(key.GetHashCode(), new GlyphAtlas.GlyphKey(nested, 0).GetHashCode());
        Assert.Equal(1, owner.Calls);
    }

    [Fact]
    public void NoInkAndEmptyViewsDoNotManufactureRasterKeys()
    {
        var owner = new Owner();
        using var original = Create(owner);
        using var noInk = original.SelectOccurrences([1, 1]);
        using var empty = noInk.SelectOccurrences([]);
        var keys = new HashSet<GlyphAtlas.GlyphKey>();
        foreach (var view in new[] { noInk, empty })
        {
            Assert.Same(original.RasterGenerationIdentity, view.RasterGenerationIdentity);
            foreach (var occurrence in view.Occurrences)
                if (occurrence.OutlineIndex != uint.MaxValue)
                    keys.Add(new(view, occurrence.OutlineIndex));
        }
        Assert.Empty(keys);
        original.Dispose();
        noInk.Dispose();
        Assert.Equal(0, owner.Calls);
        empty.Dispose();
        Assert.Equal(1, owner.Calls);
    }

    [Fact]
    public void IndependentGenerationsStayDistinctEvenWithIdenticalPhysicalArrays()
    {
        using var original = Create(new Owner());
        using var selected = original.SelectOccurrences([0]);
        GpuGlyphRecord[] outlines = original.Outlines.ToArray();
        GpuSegment[] segments = original.Segments.ToArray();
        HintedGlyphOccurrence[] occurrences = original.Occurrences.ToArray();
        using var independent = new HintedGlyphGeometry(original.DpiScale, outlines, segments, occurrences, new Owner());
        using var sameArrays = new HintedGlyphGeometry(original.DpiScale, outlines, segments, occurrences, new Owner());
        Assert.NotSame(original.RasterGenerationIdentity, independent.RasterGenerationIdentity);
        Assert.NotEqual(new GlyphAtlas.GlyphKey(selected, 0), new GlyphAtlas.GlyphKey(independent, 0));
        Assert.NotSame(independent.RasterGenerationIdentity, sameArrays.RasterGenerationIdentity);
        Assert.NotEqual(new GlyphAtlas.GlyphKey(independent, 0), new GlyphAtlas.GlyphKey(sameArrays, 0));
        using var differentDpi = Create(new Owner(), 1f);
        Assert.NotEqual(new GlyphAtlas.GlyphKey(original, 0), new GlyphAtlas.GlyphKey(differentDpi, 0));
        Assert.NotEqual(new GlyphAtlas.GlyphKey(selected, ushort.MaxValue), new GlyphAtlas.GlyphKey(selected, uint.MaxValue));
    }

    [Fact]
    public void FailedRetirementDoesNotTurnKeyComparisonIntoSourceAccessOrTeardownRetry()
    {
        var owner = new Owner { Failures = 1 };
        using var original = Create(owner);
        using var selected = original.SelectOccurrences([0]);
        var key = new GlyphAtlas.GlyphKey(original, 0);
        var resident = new Dictionary<GlyphAtlas.GlyphKey, int> { [key] = 117 };
        original.Dispose();
        Assert.Equal("original identity owner fault", Assert.Throws<InvalidOperationException>(selected.Dispose).Message);
        Assert.False(original.HasRenderStorage);
        Assert.False(selected.HasRenderStorage);
        Assert.Equal(1, owner.Calls);
        Assert.Equal(117, resident[new(selected, 0)]);
        Assert.Equal(key.GetHashCode(), new GlyphAtlas.GlyphKey(selected, 0).GetHashCode());
        Assert.Equal(1, owner.Calls);
        selected.Dispose();
        Assert.Equal(2, owner.Calls);
        Assert.Equal(key, new GlyphAtlas.GlyphKey(selected, 0));
    }
}
