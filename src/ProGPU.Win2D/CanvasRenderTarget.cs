using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using Silk.NET.WebGPU;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace Microsoft.Graphics.Canvas;

/// <summary>
/// GPU-resident Canvas render target. Drawing sessions are compiled to the
/// retained native C++ scene stream when they close or flush.
/// </summary>
public sealed class CanvasRenderTarget :
    CanvasBitmap,
    ICanvasDrawingSessionTarget
{
    private readonly object _sessionLock = new();
    private readonly ulong _sceneId;
    private ulong _generation;
    private bool _hasActiveSession;

    public CanvasRenderTarget(
        ICanvasResourceCreatorWithDpi resourceCreator,
        Size size)
        : this(resourceCreator, (float)size.Width, (float)size.Height)
    {
    }

    public CanvasRenderTarget(
        ICanvasResourceCreatorWithDpi resourceCreator,
        float width,
        float height)
        : this(
            resourceCreator,
            width,
            height,
            resourceCreator?.Dpi ?? throw new ArgumentNullException(
                nameof(resourceCreator)))
    {
    }

    public CanvasRenderTarget(
        ICanvasResourceCreator resourceCreator,
        float width,
        float height,
        float dpi)
        : this(
            resourceCreator,
            width,
            height,
            dpi,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied)
    {
    }

    public CanvasRenderTarget(
        ICanvasResourceCreator resourceCreator,
        float width,
        float height,
        float dpi,
        DirectXPixelFormat format,
        CanvasAlphaMode alphaMode)
        : base(
            GetDevice(resourceCreator),
            CreateTexture(
                GetDevice(resourceCreator),
                width,
                height,
                dpi,
                format,
                alphaMode),
            width,
            height,
            dpi,
            CanvasContract.ValidateFormat(format),
            CanvasContract.ValidateAlphaMode(alphaMode))
    {
        _sceneId = Device.AllocateSceneId();
    }

    public ProGpuCanvasRenderMetrics LastRenderMetrics { get; private set; }

    Windows.Foundation.Rect ICanvasDrawingSessionTarget.DrawingBounds =>
        Bounds;

    public CanvasDrawingSession CreateDrawingSession()
    {
        ThrowIfDisposed();
        lock (_sessionLock)
        {
            if (_hasActiveSession)
            {
                throw new InvalidOperationException(
                    "Only one CanvasDrawingSession may target a CanvasRenderTarget at a time.");
            }

            _hasActiveSession = true;
            return new CanvasDrawingSession(this);
        }
    }

    void ICanvasDrawingSessionTarget.ValidateClear()
    {
    }

    void ICanvasDrawingSessionTarget.Commit(
        GpuPicture sessionPicture,
        bool hasClear,
        Vector4 clearColor)
    {
        ArgumentNullException.ThrowIfNull(sessionPicture);
        ThrowIfDisposed();
        try
        {
            ulong generation = checked(++_generation);
            LastRenderMetrics = Device.Render(
                sessionPicture,
                Texture,
                Dpi,
                _sceneId,
                generation,
                clearColor,
                preserveTarget: !hasClear,
                logicalWidth: (float)Size.Width,
                logicalHeight: (float)Size.Height);
        }
        finally
        {
            sessionPicture.Dispose();
        }
    }

    void ICanvasDrawingSessionTarget.EndSession()
    {
        lock (_sessionLock)
        {
            _hasActiveSession = false;
        }
    }

    protected override void ValidateCanDispose()
    {
        lock (_sessionLock)
        {
            if (_hasActiveSession)
            {
                throw new InvalidOperationException(
                    "Close the active CanvasDrawingSession before disposing its render target.");
            }
        }
    }

    private static CanvasDevice GetDevice(
        ICanvasResourceCreator resourceCreator)
    {
        ArgumentNullException.ThrowIfNull(resourceCreator);
        CanvasDevice device = resourceCreator.Device ??
            throw new ArgumentException(
                "The resource creator did not provide a CanvasDevice.",
                nameof(resourceCreator));
        if (device.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(resourceCreator));
        }

        return device;
    }

    private static GpuTexture CreateTexture(
        CanvasDevice device,
        float width,
        float height,
        float dpi,
        DirectXPixelFormat format,
        CanvasAlphaMode alphaMode)
    {
        CanvasContract.ValidateDpi(dpi);
        CanvasContract.ValidateFormat(format);
        CanvasContract.ValidateAlphaMode(alphaMode);
        uint pixelWidth = CanvasContract.SizeDipsToPixels(width, dpi);
        uint pixelHeight = CanvasContract.SizeDipsToPixels(height, dpi);
        var texture = new GpuTexture(
            device.Context,
            pixelWidth,
            pixelHeight,
            TextureFormat.Bgra8Unorm,
            TextureUsage.RenderAttachment |
            TextureUsage.TextureBinding |
            TextureUsage.CopySrc |
            TextureUsage.CopyDst,
            "ProGPU Win2D CanvasRenderTarget",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        GpuTextureClearer.Clear(texture, default);
        return texture;
    }
}
