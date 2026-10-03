using ProGPU.Backend.Native;

internal static class CacheRasterPolicyValidation
{
    internal static void Run()
    {
        foreach (NativeMilBackend backend in new[] { NativeMilBackend.WgpuNative, NativeMilBackend.Dawn })
        {
            using var channel = new NativeMilChannel(backend);
            var batch = new NativeMilBatchBuilder();
            batch.CreateResource(1, NativeMilResourceType.BitmapCacheBrush);
            batch.CreateResource(2, NativeMilResourceType.Visual);
            channel.Apply(batch.WrittenSpan);
            var policy = new NativeMilBitmapCacheRasterPolicy(1.5f, 2f, 1024, 2048, 1);
            foreach (uint handle in new uint[] { 0, 1, 2, 99 })
            {
                bool rejected = false;
                try { channel.SetBitmapCacheBrushRasterPolicy(handle, policy); }
                catch (NativeMilException error) when (error.Status == NativeMilStatus.InvalidHandle) { rejected = true; }
                Check(rejected, "cache policy accepted missing/uninitialized/wrong owner");
            }

            batch.Clear();
            batch.SetBitmapCacheBrush(1, new NativeMilBitmapCacheBrush(0));
            channel.Apply(batch.WrittenSpan);
            ulong generation = channel.GetResourceGeneration(1);
            channel.SetBitmapCacheBrushRasterPolicy(1, policy);
            Check(channel.GetResourceGeneration(1) != generation, "cache policy lost source revision");
            generation = channel.GetResourceGeneration(1);
            foreach (NativeMilBitmapCacheRasterPolicy invalid in new[]
            {
                policy with { PrimaryDpiScaleX = 0 }, policy with { PrimaryDpiScaleY = -1 },
                policy with { PrimaryDpiScaleX = float.NaN }, policy with { PrimaryDpiScaleY = float.PositiveInfinity },
                policy with { MaximumTextureWidth = 0 }, policy with { MaximumTextureHeight = 0 },
                policy with { SourceRevision = 0 },
            })
            {
                bool rejected = false;
                try { channel.SetBitmapCacheBrushRasterPolicy(1, invalid); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Check(rejected && channel.GetResourceGeneration(1) == generation, "invalid cache policy mutated owner");
            }

            channel.SetBitmapCacheBrushRasterPolicy(1, policy with { SourceRevision = 2 });
            Check(channel.GetResourceGeneration(1) != generation, "same raster dimensions hid source revision");
            channel.Dispose();
            bool disposed = false;
            try { channel.SetBitmapCacheBrushRasterPolicy(1, policy); }
            catch (ObjectDisposedException) { disposed = true; }
            Check(disposed, "disposed cache policy owner admitted");
        }

        Console.WriteLine("package-consumer: explicit cache raster policy passed on both MIL providers");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
