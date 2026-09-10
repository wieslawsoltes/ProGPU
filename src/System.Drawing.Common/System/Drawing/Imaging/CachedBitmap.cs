// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using ProGPU.Backend;

namespace System.Drawing.Imaging;

/// <summary>
/// A device-dependent immutable copy of a <see cref="Bitmap"/> prepared for a
/// specific <see cref="Graphics"/> target.
/// </summary>
public sealed class CachedBitmap : IDisposable
{
    private Bitmap? _snapshot;
    private readonly WgpuContext _deviceContext;

    /// <summary>
    /// Creates a device-dependent copy of <paramref name="bitmap"/> for the
    /// device used by <paramref name="graphics"/>.
    /// </summary>
    public CachedBitmap(Bitmap bitmap, Graphics graphics)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(graphics);

        _deviceContext = graphics.GetTargetContextForCachedBitmap();
        var snapshot = new Bitmap(bitmap);
        try
        {
            if (!snapshot.TryGetGpuTexture(_deviceContext, out _))
            {
                throw new InvalidOperationException(
                    "The Graphics device is not available for cached bitmap creation.");
            }

            _snapshot = snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    internal Bitmap GetSnapshotForDraw(WgpuContext deviceContext)
    {
        if (!ReferenceEquals(deviceContext, _deviceContext))
        {
            throw new InvalidOperationException(
                "The CachedBitmap is not compatible with this Graphics device.");
        }

        return Volatile.Read(ref _snapshot)
            ?? throw new ArgumentException("Parameter is not valid.");
    }

    private void Dispose(bool disposing)
    {
        Bitmap? snapshot = Interlocked.Exchange(ref _snapshot, null);
        snapshot?.Dispose();
    }

    ~CachedBitmap() => Dispose(disposing: false);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
