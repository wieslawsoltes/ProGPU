using ProGPU.Backend;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;

namespace Microsoft.Graphics.Canvas;

/// <summary>GPU-resident Win2D-shaped bitmap resource.</summary>
public abstract class CanvasBitmap :
    ICanvasResourceCreatorWithDpi,
    IProGpuTextureSource,
    IDisposable
{
    private bool _isDisposed;

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

    public bool TryGetGpuTexture(out GpuTexture texture)
    {
        if (!_isDisposed && !Texture.IsDisposed)
        {
            texture = Texture;
            return true;
        }

        texture = null!;
        return false;
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

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        DisposeCore();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
