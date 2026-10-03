using ProGPU.Backend.Native;

internal static class CacheBrushEmptySourceValidation
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
            Reject(channel, 1, 2, NativeMilStatus.InvalidHandle);
            batch.Clear();
            batch.SetBitmapCacheBrush(1, new NativeMilBitmapCacheBrush(0));
            batch.CreateVisual(2);
            channel.Apply(batch.WrittenSpan);
            Reject(channel, 1, 2, NativeMilStatus.InvalidArgument);
            channel.SetVisualCacheBounds(2, new NativeMilRect(1, 2, 3, 4));
            Reject(channel, 1, 2, NativeMilStatus.InvalidArgument);
            channel.SetVisualSourceEmptyBounds(2);
            foreach ((uint brush, uint visual) in new[] { (0U, 2U), (2U, 2U), (1U, 0U), (1U, 99U) })
                Reject(channel, brush, visual, NativeMilStatus.InvalidHandle);

            ulong generation = channel.GetResourceGeneration(1);
            channel.SetBitmapCacheBrushEmptySource(1, 2);
            Check(channel.GetResourceGeneration(1) != generation, "ownership witness did not advance brush revision");
            generation = channel.GetResourceGeneration(1);
            batch.Clear();
            batch.DeleteResource(2, NativeMilResourceType.Visual);
            bool retained = false;
            try { channel.Apply(batch.WrittenSpan); }
            catch (NativeMilException error) when (error.Status == NativeMilStatus.InvalidGraph) { retained = true; }
            Check(retained && channel.GetResourceGeneration(1) == generation, "owned empty source was deleted");

            batch.Clear();
            batch.SetBitmapCacheBrush(1, new NativeMilBitmapCacheBrush(2));
            channel.Apply(batch.WrittenSpan);
            Reject(channel, 1, 2, NativeMilStatus.InvalidArgument);
            batch.Clear();
            batch.SetBitmapCacheBrush(1, new NativeMilBitmapCacheBrush(0));
            batch.DeleteResource(2, NativeMilResourceType.Visual);
            channel.Apply(batch.WrittenSpan);
            Reject(channel, 1, 2, NativeMilStatus.InvalidHandle);
            channel.Dispose();
            bool disposed = false;
            try { channel.SetBitmapCacheBrushEmptySource(1, 2); }
            catch (ObjectDisposedException) { disposed = true; }
            Check(disposed, "disposed witness owner admitted");
        }

        Console.WriteLine("package-consumer: retained empty cache source ownership passed on both MIL providers");
    }

    private static void Reject(NativeMilChannel channel, uint brush, uint visual, NativeMilStatus expected)
    {
        bool rejected = false;
        try { channel.SetBitmapCacheBrushEmptySource(brush, visual); }
        catch (NativeMilException error) when (error.Status == expected) { rejected = true; }
        Check(rejected, "invalid empty cache source relationship admitted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
