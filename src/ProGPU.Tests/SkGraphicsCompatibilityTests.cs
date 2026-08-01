using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkGraphicsCompatibilityTests
{
    [Fact]
    public void GlBackendStateValuesMatchOfficialBitContract()
    {
        Assert.Equal(0u, (uint)GRGlBackendState.None);
        Assert.Equal(1u, (uint)GRGlBackendState.RenderTarget);
        Assert.Equal(2u, (uint)GRGlBackendState.TextureBinding);
        Assert.Equal(4u, (uint)GRGlBackendState.View);
        Assert.Equal(8u, (uint)GRGlBackendState.Blend);
        Assert.Equal(0x100u, (uint)GRGlBackendState.Program);
        Assert.Equal(0x800u, (uint)GRGlBackendState.PathRendering);
        Assert.Equal(0xffffu, (uint)GRGlBackendState.All);
    }

    [Fact]
    public void CacheBudgetsAreAtomicAndReturnPreviousValues()
    {
        var oldCount = SKGraphics.GetFontCacheCountLimit();
        var oldFontBytes = SKGraphics.GetFontCacheLimit();
        var oldSingleBytes = SKGraphics.GetResourceCacheSingleAllocationByteLimit();
        var oldTotalBytes = SKGraphics.GetResourceCacheTotalByteLimit();
        try
        {
            Assert.Equal(oldCount, SKGraphics.SetFontCacheCountLimit(321));
            Assert.Equal(321, SKGraphics.GetFontCacheCountLimit());
            Assert.Equal(oldFontBytes, SKGraphics.SetFontCacheLimit(4_096));
            Assert.Equal(4_096, SKGraphics.GetFontCacheLimit());
            Assert.Equal(oldSingleBytes, SKGraphics.SetResourceCacheSingleAllocationByteLimit(8_192));
            Assert.Equal(8_192, SKGraphics.GetResourceCacheSingleAllocationByteLimit());
            Assert.Equal(oldTotalBytes, SKGraphics.SetResourceCacheTotalByteLimit(16_384));
            Assert.Equal(16_384, SKGraphics.GetResourceCacheTotalByteLimit());
            Assert.Equal(0, SKGraphics.GetFontCacheCountUsed());
            Assert.Equal(0, SKGraphics.GetFontCacheUsed());
            Assert.Equal(0, SKGraphics.GetResourceCacheTotalBytesUsed());
            Assert.Throws<ArgumentOutOfRangeException>(() => SKGraphics.SetFontCacheCountLimit(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => SKGraphics.SetFontCacheLimit(-1));
        }
        finally
        {
            SKGraphics.SetFontCacheCountLimit(oldCount);
            SKGraphics.SetFontCacheLimit(oldFontBytes);
            SKGraphics.SetResourceCacheSingleAllocationByteLimit(oldSingleBytes);
            SKGraphics.SetResourceCacheTotalByteLimit(oldTotalBytes);
        }
    }

    [Fact]
    public void MemoryDumpReportsBoundedCacheAndBackendState()
    {
        using var dump = new RecordingDump();
        SKGraphics.DumpMemoryStatistics(dump);

        Assert.Equal(4, dump.NumericCount);
        Assert.Equal(1, dump.StringCount);
        Assert.Equal("ProGPU WebGPU", dump.LastStringValue);
        Assert.NotEqual(IntPtr.Zero, dump.Handle);
    }

    private sealed class RecordingDump : SKTraceMemoryDump
    {
        public RecordingDump()
            : base(detailedDump: true, dumpWrappedObjects: false)
        {
        }

        public int NumericCount { get; private set; }

        public int StringCount { get; private set; }

        public string? LastStringValue { get; private set; }

        protected internal override void OnDumpNumericValue(
            string dumpName,
            string valueName,
            string units,
            ulong value) => NumericCount++;

        protected internal override void OnDumpStringValue(
            string dumpName,
            string valueName,
            string value)
        {
            StringCount++;
            LastStringValue = value;
        }
    }
}
