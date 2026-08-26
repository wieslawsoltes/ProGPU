using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;

namespace System.Drawing;

public sealed class TextureBrush : Brush, ICloneable
{
    private Bitmap _image;
    private Matrix _transform;
    private WrapMode _wrapMode;
    private bool _disposed;

    public TextureBrush(Image bitmap)
        : this(bitmap, WrapMode.Tile)
    {
    }

    public TextureBrush(Image image, WrapMode wrapMode)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateWrapMode(wrapMode, nameof(wrapMode));

        _image = SnapshotImage(image);
        _transform = new Matrix();
        _wrapMode = wrapMode;
    }

    public TextureBrush(Image image, WrapMode wrapMode, RectangleF dstRect)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateWrapMode(wrapMode, nameof(wrapMode));

        _image = SnapshotImage(image, dstRect, imageAttributes: null);
        _transform = new Matrix();
        _wrapMode = wrapMode;
    }

    public TextureBrush(Image image, WrapMode wrapMode, Rectangle dstRect)
        : this(image, wrapMode, (RectangleF)dstRect)
    {
    }

    public TextureBrush(Image image, RectangleF dstRect)
        : this(image, dstRect, imageAttr: null)
    {
    }

    public TextureBrush(Image image, RectangleF dstRect, ImageAttributes? imageAttr)
    {
        ArgumentNullException.ThrowIfNull(image);

        using ImageAttributes? attributesSnapshot = imageAttr is null
            ? null
            : (ImageAttributes)imageAttr.Clone();
        _image = SnapshotImage(image, dstRect, attributesSnapshot);
        _transform = new Matrix();
        _wrapMode = attributesSnapshot?.WrapMode ?? WrapMode.Tile;
    }

    public TextureBrush(Image image, Rectangle dstRect)
        : this(image, dstRect, imageAttr: null)
    {
    }

    public TextureBrush(Image image, Rectangle dstRect, ImageAttributes? imageAttr)
        : this(image, (RectangleF)dstRect, imageAttr)
    {
    }

    private TextureBrush(Bitmap image, Matrix transform, WrapMode wrapMode)
    {
        _image = image;
        _transform = transform;
        _wrapMode = wrapMode;
    }

    public Image Image
    {
        get
        {
            ThrowIfDisposed();
            return (Image)_image.Clone();
        }
    }

    public Matrix Transform
    {
        get
        {
            ThrowIfDisposed();
            return _transform.Clone();
        }
        set
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(value);
            Matrix replacement = value.Clone();
            _transform.Dispose();
            _transform = replacement;
        }
    }

    public WrapMode WrapMode
    {
        get
        {
            ThrowIfDisposed();
            return _wrapMode;
        }
        set
        {
            ThrowIfDisposed();
            ValidateWrapMode(value, nameof(value));
            _wrapMode = value;
        }
    }

    internal Bitmap Bitmap
    {
        get
        {
            ThrowIfDisposed();
            return _image;
        }
    }

    internal Matrix3x2 TransformValue
    {
        get
        {
            ThrowIfDisposed();
            return _transform.Value;
        }
    }

    public override object Clone()
    {
        ThrowIfDisposed();
        return new TextureBrush(new Bitmap(_image), _transform.Clone(), _wrapMode);
    }

    public void ResetTransform()
    {
        ThrowIfDisposed();
        _transform.Reset();
    }

    public void MultiplyTransform(Matrix matrix) => MultiplyTransform(matrix, MatrixOrder.Prepend);

    public void MultiplyTransform(Matrix matrix, MatrixOrder order)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(matrix);

        Matrix3x2 value;
        try
        {
            value = matrix.Value;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (value.IsIdentity)
        {
            return;
        }

        if (!Matrix3x2.Invert(value, out _))
        {
            throw new ArgumentException("Parameter is not valid.", nameof(matrix));
        }

        _transform.Multiply(matrix, order);
    }

    public void TranslateTransform(float dx, float dy) =>
        TranslateTransform(dx, dy, MatrixOrder.Prepend);

    public void TranslateTransform(float dx, float dy, MatrixOrder order)
    {
        ThrowIfDisposed();
        _transform.Translate(dx, dy, order);
    }

    public void ScaleTransform(float sx, float sy) =>
        ScaleTransform(sx, sy, MatrixOrder.Prepend);

    public void ScaleTransform(float sx, float sy, MatrixOrder order)
    {
        ThrowIfDisposed();
        _transform.Scale(sx, sy, order);
    }

    public void RotateTransform(float angle) =>
        RotateTransform(angle, MatrixOrder.Prepend);

    public void RotateTransform(float angle, MatrixOrder order)
    {
        ThrowIfDisposed();
        _transform.Rotate(angle, order);
    }

    internal override ProGPU.Vector.Brush ToProGpuBrush()
    {
        ThrowIfDisposed();
        throw new NotSupportedException(
            "TextureBrush is lowered through the typed texture-aware Graphics fill path.");
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transform.Dispose();
        _image.Dispose();
    }

    private static Bitmap SnapshotImage(Image image)
    {
        if (image is Bitmap bitmap)
        {
            return new Bitmap(bitmap);
        }

        return new Bitmap(image);
    }

    private static Bitmap SnapshotImage(
        Image image,
        RectangleF rectangle,
        ImageAttributes? imageAttributes)
    {
        using Bitmap source = SnapshotImage(image);
        Bitmap cropped = source.Clone(rectangle, PixelFormat.Format32bppPArgb);
        if (imageAttributes is null)
        {
            return cropped;
        }

        try
        {
            Bitmap adjusted = cropped.CreateImageAttributesAdjusted(imageAttributes);
            cropped.Dispose();
            return adjusted;
        }
        catch
        {
            cropped.Dispose();
            throw;
        }
    }

    private static void ValidateWrapMode(WrapMode value, string parameterName)
    {
        if (value is < WrapMode.Tile or > WrapMode.Clamp)
        {
            throw new InvalidEnumArgumentException(parameterName, (int)value, typeof(WrapMode));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
