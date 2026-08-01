namespace SkiaSharp;

/// <summary>Controls process-wide compatibility cache budgets exposed by SkiaSharp.</summary>
public static class SKGraphics
{
    private const int DefaultFontCacheCountLimit = 2_048;
    private const long DefaultFontCacheByteLimit = 2L * 1024 * 1024;
    private const long DefaultResourceCacheTotalByteLimit = 256L * 1024 * 1024;

    private static int s_fontCacheCountLimit = DefaultFontCacheCountLimit;
    private static long s_fontCacheByteLimit = DefaultFontCacheByteLimit;
    private static long s_resourceCacheSingleAllocationByteLimit;
    private static long s_resourceCacheTotalByteLimit = DefaultResourceCacheTotalByteLimit;

    public static void Init()
    {
    }

    public static int GetFontCacheCountLimit() =>
        Volatile.Read(ref s_fontCacheCountLimit);

    public static int GetFontCacheCountUsed() => 0;

    public static int SetFontCacheCountLimit(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return Interlocked.Exchange(ref s_fontCacheCountLimit, count);
    }

    public static long GetFontCacheLimit() =>
        Volatile.Read(ref s_fontCacheByteLimit);

    public static long GetFontCacheUsed() => 0;

    public static long SetFontCacheLimit(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        return Interlocked.Exchange(ref s_fontCacheByteLimit, bytes);
    }

    public static long GetResourceCacheSingleAllocationByteLimit() =>
        Volatile.Read(ref s_resourceCacheSingleAllocationByteLimit);

    public static long SetResourceCacheSingleAllocationByteLimit(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        return Interlocked.Exchange(ref s_resourceCacheSingleAllocationByteLimit, bytes);
    }

    public static long GetResourceCacheTotalByteLimit() =>
        Volatile.Read(ref s_resourceCacheTotalByteLimit);

    public static long GetResourceCacheTotalBytesUsed() => 0;

    public static long SetResourceCacheTotalByteLimit(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        return Interlocked.Exchange(ref s_resourceCacheTotalByteLimit, bytes);
    }

    public static void PurgeFontCache()
    {
    }

    public static void PurgeResourceCache()
    {
    }

    public static void PurgeAllCaches()
    {
        PurgeFontCache();
        PurgeResourceCache();
    }

    public static void DumpMemoryStatistics(SKTraceMemoryDump dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        dump.OnDumpNumericValue("skia/font_cache", "count_limit", "objects", (ulong)GetFontCacheCountLimit());
        dump.OnDumpNumericValue("skia/font_cache", "byte_limit", "bytes", (ulong)GetFontCacheLimit());
        dump.OnDumpNumericValue("skia/resource_cache", "single_allocation_limit", "bytes", (ulong)GetResourceCacheSingleAllocationByteLimit());
        dump.OnDumpNumericValue("skia/resource_cache", "total_limit", "bytes", (ulong)GetResourceCacheTotalByteLimit());
        dump.OnDumpStringValue("skia/backend", "implementation", "ProGPU WebGPU");
    }
}

public class SKTraceMemoryDump : SKObject
{
    protected SKTraceMemoryDump(bool detailedDump, bool dumpWrappedObjects)
        : base(SKObjectHandle.Create(), owns: true)
    {
        DetailedDump = detailedDump;
        DumpWrappedObjects = dumpWrappedObjects;
    }

    internal bool DetailedDump { get; }

    internal bool DumpWrappedObjects { get; }

    protected internal virtual void OnDumpNumericValue(
        string dumpName,
        string valueName,
        string units,
        ulong value)
    {
    }

    protected internal virtual void OnDumpStringValue(
        string dumpName,
        string valueName,
        string value)
    {
    }

    protected override void DisposeNative()
    {
    }
}
