using ProGPU.Backend.Native;

internal static class VisualSourceBoundsValidation
{
    internal static void Run()
    {
        foreach (NativeMilBackend backend in new[] { NativeMilBackend.WgpuNative, NativeMilBackend.Dawn })
        {
            using var channel = new NativeMilChannel(backend);
            var batch = new NativeMilBatchBuilder();
            batch.CreateResource(1, NativeMilResourceType.Visual);
            batch.CreateResource(2, NativeMilResourceType.SolidColorBrush);
            channel.Apply(batch.WrittenSpan);
            RejectHandle(channel, 0);
            RejectHandle(channel, 1); // declared, not initialized
            RejectHandle(channel, 2); // wrong resource family
            RejectHandle(channel, 99);
            batch.Clear();
            batch.CreateVisual(1);
            channel.Apply(batch.WrittenSpan);
            ulong generation = channel.GetResourceGeneration(1);
            channel.SetVisualSourceEmptyBounds(1);
            Check(channel.GetResourceGeneration(1) != generation, "empty witness did not advance its actual owner");
            generation = channel.GetResourceGeneration(1);
            bool zeroRejected = false;
            try { channel.SetVisualCacheBounds(1, new NativeMilRect(0, 0, 0, 0)); }
            catch (ArgumentOutOfRangeException) { zeroRejected = true; }
            Check(zeroRejected && channel.GetResourceGeneration(1) == generation, "old positive bounds contract changed");
            channel.SetVisualCacheBounds(1, new NativeMilRect(10, 20, 8, 6));
            Check(channel.GetResourceGeneration(1) != generation, "positive refill did not replace empty witness");
            generation = channel.GetResourceGeneration(1);
            channel.SetVisualSourceEmptyBounds(1);
            Check(channel.GetResourceGeneration(1) != generation, "empty update retained stale positive bounds");
            channel.Dispose();
            bool disposedRejected = false;
            try { channel.SetVisualSourceEmptyBounds(1); }
            catch (ObjectDisposedException) { disposedRejected = true; }
            Check(disposedRejected, "disposed source owner was admitted");
        }
        Console.WriteLine("package-consumer: explicit empty Visual source bounds passed on both MIL providers");
    }

    private static void RejectHandle(NativeMilChannel channel, uint handle)
    {
        bool rejected = false;
        try { channel.SetVisualSourceEmptyBounds(handle); }
        catch (NativeMilException error) when (error.Status == NativeMilStatus.InvalidHandle) { rejected = true; }
        Check(rejected, "missing/uninitialized/wrong source owner was admitted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
