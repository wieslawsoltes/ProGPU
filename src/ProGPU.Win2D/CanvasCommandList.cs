using System.Numerics;
using ProGPU.Scene;

namespace Microsoft.Graphics.Canvas;

/// <summary>
/// Win2D-shaped retained command list backed by immutable ProGPU pictures.
/// Drawing it nests those pictures directly into the native scene compiler;
/// no intermediate bitmap or readback is created.
/// </summary>
public sealed class CanvasCommandList :
    ICanvasImage,
    ICanvasResourceCreator,
    ICanvasDrawingSessionTarget,
    IDisposable
{
    private readonly object _lifetimeLock = new();
    private readonly List<GpuPicture> _pictures = new();
    private bool _hasCreatedSession;
    private bool _hasActiveSession;
    private bool _isDisposed;

    public CanvasCommandList(ICanvasResourceCreator resourceCreator)
    {
        ArgumentNullException.ThrowIfNull(resourceCreator);
        Device = resourceCreator.Device ?? throw new ArgumentException(
            "The resource creator did not provide a CanvasDevice.",
            nameof(resourceCreator));
        if (Device.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(resourceCreator));
        }
    }

    public CanvasDevice Device { get; }

    float ICanvasResourceCreatorWithDpi.Dpi => CanvasContract.DefaultDpi;

    Windows.Foundation.Rect ICanvasDrawingSessionTarget.DrawingBounds => new(
        0d,
        0d,
        CanvasContract.MaximumBitmapSizeInPixels,
        CanvasContract.MaximumBitmapSizeInPixels);

    public bool IsDisposed => _isDisposed;

    public Windows.Foundation.Rect GetBounds(
        ICanvasResourceCreator resourceCreator) =>
        GetBounds(resourceCreator, Matrix3x2.Identity);

    public Windows.Foundation.Rect GetBounds(
        ICanvasResourceCreator resourceCreator,
        Matrix3x2 transform)
    {
        CanvasContract.ValidateImageResourceCreator(
            resourceCreator,
            Device);
        if (!CanvasContract.IsFinite(transform))
        {
            throw new ArgumentOutOfRangeException(nameof(transform));
        }

        lock (_lifetimeLock)
        {
            ThrowIfDisposed();
            if (_hasActiveSession)
            {
                throw new InvalidOperationException(
                    "Close the CanvasCommandList drawing session before querying its bounds.");
            }

            bool hasBounds = false;
            Vector2 minimum = default;
            Vector2 maximum = default;
            Matrix4x4 boundsTransform = ToMatrix4x4(transform);
            for (int index = 0; index < _pictures.Count; index++)
            {
                if (!GpuPictureBounds.TryGetBounds(
                        _pictures[index],
                        boundsTransform,
                        out Rect pictureBounds))
                {
                    throw new NotSupportedException(
                        "The command list contains a retained command whose two-dimensional bounds cannot be resolved.");
                }
                if (pictureBounds.IsEmpty)
                {
                    continue;
                }

                Vector2 pictureMinimum = new(
                    pictureBounds.X,
                    pictureBounds.Y);
                Vector2 pictureMaximum = new(
                    pictureBounds.X + pictureBounds.Width,
                    pictureBounds.Y + pictureBounds.Height);
                if (hasBounds)
                {
                    minimum = Vector2.Min(minimum, pictureMinimum);
                    maximum = Vector2.Max(maximum, pictureMaximum);
                }
                else
                {
                    minimum = pictureMinimum;
                    maximum = pictureMaximum;
                    hasBounds = true;
                }
            }

            return hasBounds
                ? new Windows.Foundation.Rect(
                    minimum.X,
                    minimum.Y,
                    maximum.X - minimum.X,
                    maximum.Y - minimum.Y)
                : default;
        }
    }

    public CanvasDrawingSession CreateDrawingSession()
    {
        lock (_lifetimeLock)
        {
            ThrowIfDisposed();
            if (_hasCreatedSession)
            {
                throw new InvalidOperationException(
                    "A CanvasCommandList can be recorded only once.");
            }

            _hasCreatedSession = true;
            _hasActiveSession = true;
            return new CanvasDrawingSession(this);
        }
    }

    float ICanvasResourceCreatorWithDpi.ConvertPixelsToDips(int pixels) =>
        pixels;

    int ICanvasResourceCreatorWithDpi.ConvertDipsToPixels(
        float dips,
        CanvasDpiRounding dpiRounding) =>
        CanvasContract.DipsToPixels(
            dips,
            CanvasContract.DefaultDpi,
            dpiRounding);

    void ICanvasDrawingSessionTarget.ValidateClear() =>
        throw new NotSupportedException(
            "Portable CanvasCommandList recording does not yet support Clear; compose a filled rectangle when bounded clear semantics are required.");

    void ICanvasDrawingSessionTarget.Commit(
        GpuPicture sessionPicture,
        bool hasClear,
        Vector4 clearColor)
    {
        ArgumentNullException.ThrowIfNull(sessionPicture);
        lock (_lifetimeLock)
        {
            if (_isDisposed)
            {
                sessionPicture.Dispose();
                throw new ObjectDisposedException(nameof(CanvasCommandList));
            }
            if (hasClear)
            {
                sessionPicture.Dispose();
                throw new NotSupportedException(
                    "Portable CanvasCommandList clear commands are not supported.");
            }

            _pictures.Add(sessionPicture);
        }
    }

    void ICanvasDrawingSessionTarget.EndSession()
    {
        lock (_lifetimeLock)
        {
            _hasActiveSession = false;
        }
    }

    internal GpuPicture[] ClonePicturesForDrawing()
    {
        lock (_lifetimeLock)
        {
            ThrowIfDisposed();
            if (_hasActiveSession)
            {
                throw new InvalidOperationException(
                    "Close the CanvasCommandList drawing session before using the command list as an image.");
            }
            if (!_hasCreatedSession)
            {
                throw new InvalidOperationException(
                    "Record the CanvasCommandList before using it as an image.");
            }

            var clones = new GpuPicture[_pictures.Count];
            try
            {
                for (int index = 0; index < clones.Length; index++)
                {
                    clones[index] = _pictures[index].Clone();
                }
                return clones;
            }
            catch
            {
                for (int index = 0; index < clones.Length; index++)
                {
                    clones[index]?.Dispose();
                }
                throw;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(CanvasCommandList));
        }
    }

    private static Matrix4x4 ToMatrix4x4(in Matrix3x2 value) => new(
        value.M11, value.M12, 0f, 0f,
        value.M21, value.M22, 0f, 0f,
        0f, 0f, 1f, 0f,
        value.M31, value.M32, 0f, 1f);

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_isDisposed)
            {
                return;
            }
            if (_hasActiveSession)
            {
                throw new InvalidOperationException(
                    "Close the active CanvasDrawingSession before disposing its command list.");
            }

            _isDisposed = true;
            for (int index = 0; index < _pictures.Count; index++)
            {
                _pictures[index].Dispose();
            }
            _pictures.Clear();
        }
        GC.SuppressFinalize(this);
    }
}
