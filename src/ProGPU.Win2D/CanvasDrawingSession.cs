using System.Numerics;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Vector;
using Windows.Foundation;
using Windows.UI;
using Color = Windows.UI.Color;
using Rect = ProGPU.Scene.Rect;

namespace Microsoft.Graphics.Canvas;

/// <summary>
/// Allocation-conscious Canvas command recorder for the portable Win2D core.
/// </summary>
public sealed class CanvasDrawingSession :
    ICanvasResourceCreatorWithDpi,
    IDisposable
{
    private readonly CanvasRenderTarget _target;
    private readonly GpuPictureRecorder _recorder = new();
    private readonly Rect _bounds;
    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();
    private readonly Dictionary<PenKey, Pen> _pens = new();
    private DrawingContext _context;
    private Matrix3x2 _transform = Matrix3x2.Identity;
    private Vector4 _clearColor;
    private bool _hasClear;
    private bool _hasCommands;
    private bool _isDisposed;

    private readonly record struct PenKey(uint Color, int WidthBits);

    internal CanvasDrawingSession(
        CanvasRenderTarget target,
        Windows.Foundation.Rect bounds,
        float dpi)
    {
        _target = target;
        _bounds = new Rect(
            (float)bounds.X,
            (float)bounds.Y,
            (float)bounds.Width,
            (float)bounds.Height);
        Dpi = dpi;
        _context = _recorder.BeginRecording(_bounds);
    }

    public CanvasDevice Device => _target.Device;

    public float Dpi { get; }

    public Matrix3x2 Transform
    {
        get => _transform;
        set
        {
            ThrowIfDisposed();
            if (!IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _transform = value;
        }
    }

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

    public void Clear(Color color)
    {
        ThrowIfDisposed();
        _context.Clear();
        _clearColor = ToPremultipliedVector(color);
        _hasClear = true;
        _hasCommands = false;
    }

    public void DrawImage(ICanvasImage image) =>
        DrawImage(image, Vector2.Zero);

    public void DrawImage(ICanvasImage image, Vector2 offset) =>
        DrawImage(image, offset.X, offset.Y);

    public void DrawImage(ICanvasImage image, float x, float y)
    {
        CanvasBitmap bitmap = GetBitmap(image);
        DrawBitmap(
            bitmap,
            new Rect(
                x,
                y,
                (float)bitmap.Size.Width,
                (float)bitmap.Size.Height),
            new Rect(0f, 0f, bitmap.Texture.Width, bitmap.Texture.Height),
            1f,
            CanvasImageInterpolation.Linear);
    }

    public void DrawImage(
        CanvasBitmap bitmap,
        Windows.Foundation.Rect destinationRectangle) =>
        DrawBitmap(
            GetBitmap(bitmap),
            ValidateRect(
                (float)destinationRectangle.X,
                (float)destinationRectangle.Y,
                (float)destinationRectangle.Width,
                (float)destinationRectangle.Height),
            new Rect(0f, 0f, bitmap.Texture.Width, bitmap.Texture.Height),
            1f,
            CanvasImageInterpolation.Linear);

    public void DrawImage(
        ICanvasImage image,
        Vector2 offset,
        Windows.Foundation.Rect sourceRectangle) =>
        DrawImage(image, offset.X, offset.Y, sourceRectangle);

    public void DrawImage(
        ICanvasImage image,
        float x,
        float y,
        Windows.Foundation.Rect sourceRectangle)
    {
        CanvasBitmap bitmap = GetBitmap(image);
        Rect source = ValidateSourceRect(bitmap, sourceRectangle);
        DrawBitmap(
            bitmap,
            new Rect(
                x,
                y,
                (float)sourceRectangle.Width,
                (float)sourceRectangle.Height),
            source,
            1f,
            CanvasImageInterpolation.Linear);
    }

    public void DrawImage(
        ICanvasImage image,
        Windows.Foundation.Rect destinationRectangle,
        Windows.Foundation.Rect sourceRectangle) =>
        DrawImage(
            image,
            destinationRectangle,
            sourceRectangle,
            1f,
            CanvasImageInterpolation.Linear);

    public void DrawImage(
        ICanvasImage image,
        Vector2 offset,
        Windows.Foundation.Rect sourceRectangle,
        float opacity) =>
        DrawImage(
            image,
            new Windows.Foundation.Rect(
                offset.X,
                offset.Y,
                sourceRectangle.Width,
                sourceRectangle.Height),
            sourceRectangle,
            opacity,
            CanvasImageInterpolation.Linear);

    public void DrawImage(
        ICanvasImage image,
        float x,
        float y,
        Windows.Foundation.Rect sourceRectangle,
        float opacity) =>
        DrawImage(
            image,
            new Windows.Foundation.Rect(
                x,
                y,
                sourceRectangle.Width,
                sourceRectangle.Height),
            sourceRectangle,
            opacity,
            CanvasImageInterpolation.Linear);

    public void DrawImage(
        ICanvasImage image,
        Windows.Foundation.Rect destinationRectangle,
        Windows.Foundation.Rect sourceRectangle,
        float opacity) =>
        DrawImage(
            image,
            destinationRectangle,
            sourceRectangle,
            opacity,
            CanvasImageInterpolation.Linear);

    public void DrawImage(
        ICanvasImage image,
        Vector2 offset,
        Windows.Foundation.Rect sourceRectangle,
        float opacity,
        CanvasImageInterpolation interpolation) =>
        DrawImage(
            image,
            new Windows.Foundation.Rect(
                offset.X,
                offset.Y,
                sourceRectangle.Width,
                sourceRectangle.Height),
            sourceRectangle,
            opacity,
            interpolation);

    public void DrawImage(
        ICanvasImage image,
        float x,
        float y,
        Windows.Foundation.Rect sourceRectangle,
        float opacity,
        CanvasImageInterpolation interpolation) =>
        DrawImage(
            image,
            new Windows.Foundation.Rect(
                x,
                y,
                sourceRectangle.Width,
                sourceRectangle.Height),
            sourceRectangle,
            opacity,
            interpolation);

    public void DrawImage(
        ICanvasImage image,
        Windows.Foundation.Rect destinationRectangle,
        Windows.Foundation.Rect sourceRectangle,
        float opacity,
        CanvasImageInterpolation interpolation)
    {
        CanvasBitmap bitmap = GetBitmap(image);
        DrawBitmap(
            bitmap,
            ValidateRect(
                (float)destinationRectangle.X,
                (float)destinationRectangle.Y,
                (float)destinationRectangle.Width,
                (float)destinationRectangle.Height),
            ValidateSourceRect(bitmap, sourceRectangle),
            opacity,
            interpolation);
    }

    public void DrawLine(
        Vector2 point0,
        Vector2 point1,
        Color color,
        float strokeWidth = 1f) =>
        DrawLine(point0.X, point0.Y, point1.X, point1.Y, color, strokeWidth);

    public void DrawLine(
        float x0,
        float y0,
        float x1,
        float y1,
        Color color,
        float strokeWidth = 1f)
    {
        ValidateFinite(x0, y0, x1, y1);
        Pen pen = GetPen(color, strokeWidth);
        _context.DrawLine(
            pen,
            new Vector2(x0, y0),
            new Vector2(x1, y1),
            ToMatrix4x4(_transform));
        _hasCommands = true;
    }

    public void DrawRectangle(
        Windows.Foundation.Rect rectangle,
        Color color,
        float strokeWidth = 1f) =>
        DrawRectangle(
            (float)rectangle.X,
            (float)rectangle.Y,
            (float)rectangle.Width,
            (float)rectangle.Height,
            color,
            strokeWidth);

    public void DrawRectangle(
        float x,
        float y,
        float width,
        float height,
        Color color,
        float strokeWidth = 1f)
    {
        Rect rect = ValidateRect(x, y, width, height);
        _context.DrawRectangle(
            null,
            GetPen(color, strokeWidth),
            rect,
            ToMatrix4x4(_transform));
        _hasCommands = true;
    }

    public void FillRectangle(
        Windows.Foundation.Rect rectangle,
        Color color) =>
        FillRectangle(
            (float)rectangle.X,
            (float)rectangle.Y,
            (float)rectangle.Width,
            (float)rectangle.Height,
            color);

    public void FillRectangle(
        float x,
        float y,
        float width,
        float height,
        Color color)
    {
        Rect rect = ValidateRect(x, y, width, height);
        _context.DrawRectangle(
            GetBrush(color),
            null,
            rect,
            ToMatrix4x4(_transform));
        _hasCommands = true;
    }

    public void DrawRoundedRectangle(
        Windows.Foundation.Rect rectangle,
        float radiusX,
        float radiusY,
        Color color,
        float strokeWidth = 1f) =>
        DrawRoundedRectangle(
            (float)rectangle.X,
            (float)rectangle.Y,
            (float)rectangle.Width,
            (float)rectangle.Height,
            radiusX,
            radiusY,
            color,
            strokeWidth);

    public void DrawRoundedRectangle(
        float x,
        float y,
        float width,
        float height,
        float radiusX,
        float radiusY,
        Color color,
        float strokeWidth = 1f)
    {
        Rect rect = ValidateRect(x, y, width, height);
        ValidateRadii(radiusX, radiusY);
        _context.DrawRoundedRectangle(
            null,
            GetPen(color, strokeWidth),
            rect,
            radiusX,
            radiusY,
            ToMatrix4x4(_transform));
        _hasCommands = true;
    }

    public void FillRoundedRectangle(
        Windows.Foundation.Rect rectangle,
        float radiusX,
        float radiusY,
        Color color) =>
        FillRoundedRectangle(
            (float)rectangle.X,
            (float)rectangle.Y,
            (float)rectangle.Width,
            (float)rectangle.Height,
            radiusX,
            radiusY,
            color);

    public void FillRoundedRectangle(
        float x,
        float y,
        float width,
        float height,
        float radiusX,
        float radiusY,
        Color color)
    {
        Rect rect = ValidateRect(x, y, width, height);
        ValidateRadii(radiusX, radiusY);
        _context.DrawRoundedRectangle(
            GetBrush(color),
            null,
            rect,
            radiusX,
            radiusY,
            ToMatrix4x4(_transform));
        _hasCommands = true;
    }

    public void DrawEllipse(
        Vector2 centerPoint,
        float radiusX,
        float radiusY,
        Color color,
        float strokeWidth = 1f) =>
        DrawEllipse(
            centerPoint.X,
            centerPoint.Y,
            radiusX,
            radiusY,
            color,
            strokeWidth);

    public void DrawEllipse(
        float x,
        float y,
        float radiusX,
        float radiusY,
        Color color,
        float strokeWidth = 1f)
    {
        ValidateFinite(x, y);
        ValidateRadii(radiusX, radiusY);
        _context.DrawEllipse(
            null,
            GetPen(color, strokeWidth),
            new Vector2(x, y),
            radiusX,
            radiusY,
            ToMatrix4x4(_transform));
        _hasCommands = true;
    }

    public void FillEllipse(
        Vector2 centerPoint,
        float radiusX,
        float radiusY,
        Color color) =>
        FillEllipse(centerPoint.X, centerPoint.Y, radiusX, radiusY, color);

    public void FillEllipse(
        float x,
        float y,
        float radiusX,
        float radiusY,
        Color color)
    {
        ValidateFinite(x, y);
        ValidateRadii(radiusX, radiusY);
        _context.DrawEllipse(
            GetBrush(color),
            null,
            new Vector2(x, y),
            radiusX,
            radiusY,
            ToMatrix4x4(_transform));
        _hasCommands = true;
    }

    public void DrawCircle(
        Vector2 centerPoint,
        float radius,
        Color color,
        float strokeWidth = 1f) =>
        DrawCircle(centerPoint.X, centerPoint.Y, radius, color, strokeWidth);

    public void DrawCircle(
        float x,
        float y,
        float radius,
        Color color,
        float strokeWidth = 1f) =>
        DrawEllipse(x, y, radius, radius, color, strokeWidth);

    public void FillCircle(
        Vector2 centerPoint,
        float radius,
        Color color) =>
        FillCircle(centerPoint.X, centerPoint.Y, radius, color);

    public void FillCircle(
        float x,
        float y,
        float radius,
        Color color) =>
        FillEllipse(x, y, radius, radius, color);

    public void DrawText(
        string text,
        Vector2 point,
        Color color) =>
        DrawText(text, point.X, point.Y, color);

    public void DrawText(
        string text,
        float x,
        float y,
        Color color)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);
        ValidateFinite(x, y);
        if (text.Length == 0)
        {
            return;
        }

        _context.DrawText(
            text,
            InterFontFamily.Regular,
            20f,
            GetBrush(color),
            new Vector2(x, y),
            ToMatrix4x4(_transform));
        _hasCommands = true;
    }

    public void Flush()
    {
        ThrowIfDisposed();
        CommitPending();
    }

    private void CommitPending()
    {
        if (!_hasCommands && !_hasClear)
        {
            return;
        }

        GpuPicture picture = _recorder.EndRecording();
        _target.Commit(picture, _hasClear, _clearColor);
        _context = _recorder.BeginRecording(_bounds);
        _hasClear = false;
        _hasCommands = false;
    }

    private SolidColorBrush GetBrush(Color color)
    {
        ThrowIfDisposed();
        uint key = Pack(color);
        if (!_brushes.TryGetValue(key, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(ToStraightVector(color));
            _brushes.Add(key, brush);
        }

        return brush;
    }

    private Pen GetPen(Color color, float width)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(width) || width <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        var key = new PenKey(Pack(color), BitConverter.SingleToInt32Bits(width));
        if (!_pens.TryGetValue(key, out Pen? pen))
        {
            pen = new Pen(GetBrush(color), width);
            _pens.Add(key, pen);
        }

        return pen;
    }

    private CanvasBitmap GetBitmap(ICanvasImage image)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(image);
        if (image is not CanvasBitmap bitmap)
        {
            throw new NotSupportedException(
                "The first portable DrawImage lane accepts CanvasBitmap resources only.");
        }
        if (bitmap.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(image));
        }
        if (!ReferenceEquals(bitmap.Device, Device))
        {
            throw new ArgumentException(
                "Canvas image resources must belong to the drawing-session device.",
                nameof(image));
        }
        if (ReferenceEquals(bitmap, _target))
        {
            throw new NotSupportedException(
                "Drawing a CanvasRenderTarget into itself would create an unsupported texture feedback loop.");
        }

        return bitmap;
    }

    private void DrawBitmap(
        CanvasBitmap bitmap,
        Rect destination,
        Rect sourcePixels,
        float opacity,
        CanvasImageInterpolation interpolation)
    {
        if (!float.IsFinite(opacity) || opacity < 0f || opacity > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }

        TextureSamplingMode sampling = interpolation switch
        {
            CanvasImageInterpolation.NearestNeighbor =>
                TextureSamplingMode.Nearest,
            CanvasImageInterpolation.Linear or
            CanvasImageInterpolation.MultiSampleLinear =>
                TextureSamplingMode.Linear,
            CanvasImageInterpolation.Cubic =>
                TextureSamplingMode.Cubic,
            CanvasImageInterpolation.Anisotropic or
            CanvasImageInterpolation.HighQualityCubic =>
                throw new NotSupportedException(
                    $"Canvas image interpolation {interpolation} is not qualified by the portable texture lane."),
            _ => throw new ArgumentOutOfRangeException(nameof(interpolation))
        };

        if (!_context.TryRetainTexture(
                bitmap,
                Device.Context,
                out var texture))
        {
            throw new ObjectDisposedException(nameof(bitmap));
        }

        Matrix4x4 transform = ToMatrix4x4(_transform);
        if (opacity == 1f)
        {
            _context.DrawTexture(
                texture,
                destination,
                sourcePixels,
                transform,
                sampling);
        }
        else
        {
            var opacityMatrix = new ImageEffectColorMatrix(
                Vector4.UnitX,
                Vector4.UnitY,
                Vector4.UnitZ,
                new Vector4(0f, 0f, 0f, opacity),
                Vector4.Zero);
            _context.DrawImageWithEffect(
                texture,
                destination,
                sourceRect: sourcePixels,
                samplingMode: sampling,
                colorMatrix: opacityMatrix,
                transform: transform);
        }
        _hasCommands = true;
    }

    private Rect ValidateSourceRect(
        CanvasBitmap bitmap,
        Windows.Foundation.Rect sourceRectangle)
    {
        ThrowIfDisposed();
        float x = (float)sourceRectangle.X;
        float y = (float)sourceRectangle.Y;
        float width = (float)sourceRectangle.Width;
        float height = (float)sourceRectangle.Height;
        ValidateFinite(x, y, width, height);
        if (x < 0f || y < 0f || width <= 0f || height <= 0f ||
            x + width > bitmap.Size.Width ||
            y + height > bitmap.Size.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRectangle));
        }

        float scale = bitmap.Dpi / CanvasContract.DefaultDpi;
        return new Rect(
            x * scale,
            y * scale,
            width * scale,
            height * scale);
    }

    private Rect ValidateRect(
        float x,
        float y,
        float width,
        float height)
    {
        ThrowIfDisposed();
        ValidateFinite(x, y, width, height);
        if (width < 0f || height < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        return new Rect(x, y, width, height);
    }

    private void ValidateRadii(float radiusX, float radiusY)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(radiusX) || !float.IsFinite(radiusY) ||
            radiusX < 0f || radiusY < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusX));
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(CanvasDrawingSession));
        }
    }

    private static uint Pack(Color color) =>
        (uint)color.A << 24 |
        (uint)color.R << 16 |
        (uint)color.G << 8 |
        color.B;

    private static Vector4 ToStraightVector(Color color) =>
        new(
            color.R / 255f,
            color.G / 255f,
            color.B / 255f,
            color.A / 255f);

    private static Vector4 ToPremultipliedVector(Color color)
    {
        float alpha = color.A / 255f;
        return new Vector4(
            color.R / 255f * alpha,
            color.G / 255f * alpha,
            color.B / 255f * alpha,
            alpha);
    }

    private static void ValidateFinite(float first, float second)
    {
        if (!float.IsFinite(first) || !float.IsFinite(second))
        {
            throw new ArgumentOutOfRangeException(nameof(first));
        }
    }

    private static void ValidateFinite(
        float first,
        float second,
        float third,
        float fourth)
    {
        if (!float.IsFinite(first) || !float.IsFinite(second) ||
            !float.IsFinite(third) || !float.IsFinite(fourth))
        {
            throw new ArgumentOutOfRangeException(nameof(first));
        }
    }

    private static bool IsFinite(in Matrix3x2 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32);

    private static Matrix4x4 ToMatrix4x4(in Matrix3x2 value) =>
        new(
            value.M11, value.M12, 0f, 0f,
            value.M21, value.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            value.M31, value.M32, 0f, 1f);

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            CommitPending();
        }
        finally
        {
            _isDisposed = true;
            _target.EndSession();
        }
        GC.SuppressFinalize(this);
    }
}
