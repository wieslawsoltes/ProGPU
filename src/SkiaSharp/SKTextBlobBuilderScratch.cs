namespace SkiaSharp;

internal struct SKTextBlobBuilderScratch
{
    public SKTextBlobBuilderRun? PositionedRun;
    public SKTextBlobRun[]? SingleRunSnapshots;
    public SKPositionedRunBuffer? PositionedBuffer;
    public SKTextBlobBuilderRun? DetachedPositionedBuffer;

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void ReturnPositionedRun(
        SKTextBlobBuilder owner,
        SKTextBlobBuilderRun run,
        SKTextBlobRun[] snapshots)
    {
        snapshots[0] = default;
        if (owner.IsDisposedForScratch)
        {
            return;
        }

        owner.Scratch.SingleRunSnapshots ??= snapshots;
        if (owner.Scratch.PositionedRun is null &&
            run.CanCacheAsPositionedScratch(
                SKTextBlobBuilder.MaximumReusablePositionedGlyphs))
        {
            owner.Scratch.PositionedRun = run;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void DetachPositionedBuffer(SKTextBlobBuilderRun leasedRun)
    {
        if (PositionedBuffer is null)
        {
            return;
        }

        var detached = DetachedPositionedBuffer;
        if (detached is null ||
            !detached.TryPrepareDetachedPositioned(leasedRun.Font, leasedRun.Glyphs.Length))
        {
            detached = new SKTextBlobBuilderRun(
                leasedRun.Font,
                leasedRun.Glyphs.Length,
                SKTextBlobRunPlacement.Positioned,
                0f,
                0f,
                textByteCount: 0)
            {
                PositionedPositions = SKTextBlobBuilderRun.AllocatePinned<SKPoint>(leasedRun.Glyphs.Length),
            };
            DetachedPositionedBuffer = detached;
        }

        PositionedBuffer.ResetRun(detached);
    }
}
