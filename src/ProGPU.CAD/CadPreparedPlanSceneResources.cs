using ProGPU.Backend;
using ProGPU.Scene;

namespace ProGPU.CAD;

/// <summary>
/// One-shot host-thread GPU resource preparation for background plan-scene
/// recording.
/// </summary>
/// <remarks>
/// Preparation may decode and upload bounded raster IMAGE resources in the
/// consuming device domain. Transfer to a drawing context is O(R) for R
/// snapshot resources, performs no GPU operation, and can succeed once.
/// </remarks>
public sealed class CadPreparedPlanSceneResources : IDisposable
{
    private readonly IProGpuTextureLease?[] _rasterImageLeases;
    private int _transferred;
    private bool _disposed;

    public ulong ContentGeneration { get; }

    public int RasterImageResourceCount => _rasterImageLeases.Length;

    public int AvailableRasterImageCount { get; }

    internal CadPreparedPlanSceneResources(
        ulong contentGeneration,
        IProGpuTextureLease?[] rasterImageLeases,
        int availableRasterImageCount)
    {
        ContentGeneration = contentGeneration;
        _rasterImageLeases = rasterImageLeases;
        AvailableRasterImageCount = availableRasterImageCount;
    }

    internal GpuTexture?[] TransferTo(
        DrawingContext context,
        CadDocumentSnapshot snapshot,
        WgpuContext? requiredContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(snapshot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (snapshot.ContentGeneration != ContentGeneration)
        {
            throw new InvalidOperationException(
                "Prepared plan resources do not match the snapshot generation.");
        }
        if (snapshot.RasterImageResources.Length != _rasterImageLeases.Length)
        {
            throw new InvalidOperationException(
                "Prepared plan resources do not match the snapshot resource table.");
        }
        if (Interlocked.Exchange(ref _transferred, 1) != 0)
        {
            throw new InvalidOperationException(
                "Prepared plan resources can be transferred only once.");
        }

        var textures = new GpuTexture?[_rasterImageLeases.Length];
        for (int index = 0; index < _rasterImageLeases.Length; index++)
        {
            IProGpuTextureLease? lease = _rasterImageLeases[index];
            if (lease is null)
            {
                continue;
            }

            GpuTexture texture = lease.Texture;
            if (texture is null || texture.IsDisposed)
            {
                throw new InvalidOperationException(
                    $"Prepared raster IMAGE resource {index} is no longer available.");
            }
            if (requiredContext is not null &&
                !texture.Context.SharesDeviceWith(requiredContext))
            {
                throw new InvalidOperationException(
                    $"Prepared raster IMAGE resource {index} belongs to a different WebGPU device domain.");
            }

            context.RetainResource(lease);
            _rasterImageLeases[index] = null;
            textures[index] = texture;
        }
        return textures;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        for (int index = 0; index < _rasterImageLeases.Length; index++)
        {
            _rasterImageLeases[index]?.Dispose();
            _rasterImageLeases[index] = null;
        }
    }
}
