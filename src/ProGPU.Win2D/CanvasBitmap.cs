using System.Numerics;
using ProGPU.Backend;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;

namespace Microsoft.Graphics.Canvas;

/// <summary>GPU-resident Win2D-shaped bitmap resource.</summary>
public abstract class CanvasBitmap :
    ICanvasImage,
    ICanvasResourceCreatorWithDpi,
    IProGpuTextureLeaseSource,
    IDisposable
{
    private readonly object _lifetimeLock = new();
    private bool _isDisposed;
    private bool _textureDisposed;
    private int _leaseCount;

    internal CanvasBitmap(
        CanvasDevice device,
        GpuTexture texture,
        float width,
        float height,
        float dpi,
        DirectXPixelFormat format,
        CanvasAlphaMode alphaMode)
    {
        Device = device;
        Texture = texture;
        Size = new Size(width, height);
        Dpi = dpi;
        Format = format;
        AlphaMode = alphaMode;
    }

    public CanvasDevice Device { get; }

    public float Dpi { get; }

    public Size Size { get; }

    public Rect Bounds => new(0d, 0d, Size.Width, Size.Height);

    public BitmapSize SizeInPixels => new()
    {
        Width = Texture.Width,
        Height = Texture.Height
    };

    public DirectXPixelFormat Format { get; }

    public CanvasAlphaMode AlphaMode { get; }

    internal GpuTexture Texture { get; }

    public bool IsDisposed => _isDisposed;

    public float ConvertPixelsToDips(int pixels)
    {
        ThrowIfDisposed();
        return pixels * CanvasContract.DefaultDpi / Dpi;
    }

    public int ConvertDipsToPixels(
        float dips,
        CanvasDpiRounding dpiRounding)
    {
        ThrowIfDisposed();
        return CanvasContract.DipsToPixels(dips, Dpi, dpiRounding);
    }

    public byte[] GetPixelBytes()
    {
        ThrowIfDisposed();
        Device.Context.WaitIdle();
        return Texture.ReadPixels();
    }

    public Rect GetBounds(ICanvasResourceCreator resourceCreator) =>
        GetBounds(resourceCreator, Matrix3x2.Identity);

    public Rect GetBounds(
        ICanvasResourceCreator resourceCreator,
        Matrix3x2 transform)
    {
        ThrowIfDisposed();
        CanvasContract.ValidateImageResourceCreator(
            resourceCreator,
            Device);
        return CanvasContract.TransformBounds(Bounds, transform);
    }

    public bool TryGetGpuTexture(out GpuTexture texture)
    {
        lock (_lifetimeLock)
        {
            if (!_isDisposed && !Texture.IsDisposed)
            {
                texture = Texture;
                return true;
            }
        }

        texture = null!;
        return false;
    }

    public bool TryAcquireGpuTextureLease(out IProGpuTextureLease lease)
    {
        lock (_lifetimeLock)
        {
            if (_isDisposed || Texture.IsDisposed)
            {
                lease = null!;
                return false;
            }

            _leaseCount = checked(_leaseCount + 1);
            lease = new CanvasBitmapTextureLease(this, Texture);
            return true;
        }
    }

    protected void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }

    protected virtual void DisposeCore()
    {
        Texture.Dispose();
    }

    protected virtual void ValidateCanDispose()
    {
    }

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_isDisposed)
            {
                return;
            }

            ValidateCanDispose();
            _isDisposed = true;
            if (_leaseCount == 0)
            {
                DisposeTexture();
            }
        }
        GC.SuppressFinalize(this);
    }

    private void ReleaseTextureLease()
    {
        lock (_lifetimeLock)
        {
            if (_leaseCount <= 0)
            {
                throw new InvalidOperationException(
                    "Canvas bitmap texture lease accounting underflowed.");
            }

            _leaseCount--;
            if (_leaseCount == 0 && _isDisposed)
            {
                DisposeTexture();
            }
        }
    }

    private void DisposeTexture()
    {
        if (_textureDisposed)
        {
            return;
        }

        DisposeCore();
        _textureDisposed = true;
    }

    private sealed class CanvasBitmapTextureLease : IProGpuTextureLease
    {
        private CanvasBitmap? _owner;

        public CanvasBitmapTextureLease(
            CanvasBitmap owner,
            GpuTexture texture)
        {
            _owner = owner;
            Texture = texture;
        }

        public GpuTexture Texture { get; }

        public void Dispose()
        {
            CanvasBitmap? owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseTextureLease();
        }
    }
}
