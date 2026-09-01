using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.SystemDrawing;
using ProGPU.Vector;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Imaging.Effects;
using System.Drawing.Text;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace System.Drawing;

public partial class Graphics :
    MarshalByRefObject,
    IDisposable,
    IDeviceContext,
    IProGpuDrawingContextSource
{
    private static readonly ProGPU.Vector.SolidColorBrush VertexColorMeshBrush =
        new(Vector4.One);

    public delegate bool EnumerateMetafileProc(
        EmfPlusRecordType recordType,
        int flags,
        int dataSize,
        IntPtr data,
        PlayRecordCallback? callbackData);

    private readonly DrawingContext _context;
    private readonly Bitmap? _bitmap;
    private readonly RectangleF? _deviceBounds;
    private readonly WgpuContext? _targetContext;
    private readonly Action? _completed;
    private readonly Action<FlushIntention>? _flushed;
    private readonly PortableMetafileRecordingSession? _metafileRecording;
    // Device/host state is immutable; public Transform APIs mutate only _transform.
    private readonly Matrix3x2 _baseTransform;
    private Matrix3x2 _containerTransform = Matrix3x2.Identity;
    private Matrix _transform = new();
    private readonly List<SavedGraphicsContext> _savedStates = new();
    private int _nextStateId;
    private float _pageScale = 1f;
    private GraphicsUnit _pageUnit = GraphicsUnit.Display;
    private CompositingMode _compositingMode = CompositingMode.SourceOver;
    private CompositingQuality _compositingQuality = CompositingQuality.Default;
    private Point _renderingOrigin;
    private int _textContrast = 4;
    private Region? _clip;
    private Matrix3x2 _clipContextTransform = Matrix3x2.Identity;
    private bool _hasPushedClip;
    private bool _hasPushedCompositingMode;
    private int _disposed;

    public DrawingContext DrawingContext => _context;

    public Region Clip
    {
        get => _clip?.Clone() ?? new Region();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetClip(value, CombineMode.Replace);
        }
    }

    public RectangleF ClipBounds => _clip?.GetBounds(this) ?? VisibleClipBounds;
    public bool IsClipEmpty => _clip?.IsEmpty(this) == true;
    public bool IsVisibleClipEmpty => IsClipEmpty || VisibleClipBounds.IsEmpty;

    public Matrix Transform
    {
        get => _transform.Clone();
        set
        {
            if (value != null)
            {
                _transform = value.Clone();
            }
        }
    }

    public Matrix3x2 TransformElements
    {
        get
        {
            ThrowIfDisposed();
            return _transform.Value;
        }
        set
        {
            ThrowIfDisposed();
            if (!Matrix3x2.Invert(value, out _))
            {
                throw new ArgumentException("Parameter is not valid.");
            }

            _transform.MatrixElements = value;
        }
    }

    public SmoothingMode SmoothingMode { get; set; } = SmoothingMode.AntiAlias;
    public InterpolationMode InterpolationMode { get; set; } = InterpolationMode.Bilinear;
    public TextRenderingHint TextRenderingHint { get; set; } = TextRenderingHint.ClearTypeGridFit;
    public PixelOffsetMode PixelOffsetMode { get; set; } = PixelOffsetMode.Default;

    public CompositingMode CompositingMode
    {
        get
        {
            ThrowIfDisposed();
            return _compositingMode;
        }
        set
        {
            ThrowIfDisposed();
            if (value is < CompositingMode.SourceOver or > CompositingMode.SourceCopy)
            {
                throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(CompositingMode));
            }

            if (_compositingMode == value)
            {
                return;
            }

            PopCurrentCompositingMode();
            _compositingMode = value;
            PushCurrentCompositingMode();
        }
    }

    public Point RenderingOrigin
    {
        get
        {
            ThrowIfDisposed();
            return _renderingOrigin;
        }
        set
        {
            ThrowIfDisposed();
            _renderingOrigin = value;
        }
    }

    public int TextContrast
    {
        get
        {
            ThrowIfDisposed();
            return _textContrast;
        }
        set
        {
            ThrowIfDisposed();
            if ((uint)value > 12u)
            {
                throw new ArgumentException("Parameter is not valid.");
            }

            _textContrast = value;
        }
    }

    public float PageScale
    {
        get => _pageScale;
        set
        {
            if (value <= 0f || value > 1_000_000_032f || float.IsInfinity(value))
            {
                throw new ArgumentException("Page scale is outside the supported GDI+ range.", nameof(value));
            }

            _pageScale = value;
        }
    }

    public GraphicsUnit PageUnit
    {
        get => _pageUnit;
        set
        {
            if (value < GraphicsUnit.World || value > GraphicsUnit.Millimeter)
            {
                throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(GraphicsUnit));
            }

            if (value == GraphicsUnit.World)
            {
                throw new ArgumentException("GraphicsUnit.World is not a valid page unit.", nameof(value));
            }

            _pageUnit = value;
        }
    }

    public CompositingQuality CompositingQuality
    {
        get => _compositingQuality;
        set
        {
            if (value < CompositingQuality.Invalid || value > CompositingQuality.AssumeLinear)
            {
                throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(CompositingQuality));
            }

            _compositingQuality = value;
        }
    }

    public float DpiX => 96f;
    public float DpiY => 96f;

    public RectangleF VisibleClipBounds
    {
        get
        {
            RectangleF deviceBounds;
            if (_deviceBounds is { } explicitDeviceBounds)
            {
                deviceBounds = explicitDeviceBounds;
            }
            else if (_bitmap is not null)
            {
                deviceBounds = new RectangleF(0f, 0f, _bitmap.Width, _bitmap.Height);
            }
            else
            {
                return RectangleF.Empty;
            }

            if (!Matrix3x2.Invert(CombinedTransform, out Matrix3x2 deviceToWorld))
            {
                return RectangleF.Empty;
            }

            Vector2 topLeft = Vector2.Transform(new Vector2(deviceBounds.Left, deviceBounds.Top), deviceToWorld);
            Vector2 topRight = Vector2.Transform(new Vector2(deviceBounds.Right, deviceBounds.Top), deviceToWorld);
            Vector2 bottomLeft = Vector2.Transform(new Vector2(deviceBounds.Left, deviceBounds.Bottom), deviceToWorld);
            Vector2 bottomRight = Vector2.Transform(new Vector2(deviceBounds.Right, deviceBounds.Bottom), deviceToWorld);
            float left = MathF.Min(MathF.Min(topLeft.X, topRight.X), MathF.Min(bottomLeft.X, bottomRight.X));
            float top = MathF.Min(MathF.Min(topLeft.Y, topRight.Y), MathF.Min(bottomLeft.Y, bottomRight.Y));
            float right = MathF.Max(MathF.Max(topLeft.X, topRight.X), MathF.Max(bottomLeft.X, bottomRight.X));
            float bottom = MathF.Max(MathF.Max(topLeft.Y, topRight.Y), MathF.Max(bottomLeft.Y, bottomRight.Y));

            return new RectangleF(left, top, right - left, bottom - top);
        }
    }

    internal Graphics(DrawingContext context, Bitmap? bitmap = null)
        : this(
            context,
            bitmap,
            Matrix3x2.Identity,
            deviceBounds: null,
            targetContext: null,
            completed: null,
            flushed: null,
            metafileRecording: null)
    {
    }

    private Graphics(
        DrawingContext context,
        Bitmap? bitmap,
        Matrix3x2 baseTransform,
        RectangleF? deviceBounds,
        WgpuContext? targetContext,
        Action? completed,
        Action<FlushIntention>? flushed,
        PortableMetafileRecordingSession? metafileRecording = null)
    {
        _context = context;
        _bitmap = bitmap;
        _baseTransform = baseTransform;
        _deviceBounds = deviceBounds;
        _targetContext = targetContext;
        _completed = completed;
        _flushed = flushed;
        _metafileRecording = metafileRecording;
    }

    public static Graphics FromProGpuDrawingContext(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        return new Graphics(drawingContext);
    }

    public static Graphics FromHdc(IntPtr hdc)
        => NativeGraphicsInteropServices.CreateFromDeviceContext(hdc, IntPtr.Zero);

    public static Graphics FromHdc(IntPtr hdc, IntPtr hdevice)
        => NativeGraphicsInteropServices.CreateFromDeviceContext(hdc, hdevice);

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Graphics FromHdcInternal(IntPtr hdc) => FromHdc(hdc);

    public void CopyFromScreen(
        Point upperLeftSource,
        Point upperLeftDestination,
        Size blockRegionSize)
        => CopyFromScreen(
            upperLeftSource.X,
            upperLeftSource.Y,
            upperLeftDestination.X,
            upperLeftDestination.Y,
            blockRegionSize,
            CopyPixelOperation.SourceCopy);

    public void CopyFromScreen(
        Point upperLeftSource,
        Point upperLeftDestination,
        Size blockRegionSize,
        CopyPixelOperation copyPixelOperation)
        => CopyFromScreen(
            upperLeftSource.X,
            upperLeftSource.Y,
            upperLeftDestination.X,
            upperLeftDestination.Y,
            blockRegionSize,
            copyPixelOperation);

    public void CopyFromScreen(
        int sourceX,
        int sourceY,
        int destinationX,
        int destinationY,
        Size blockRegionSize)
        => CopyFromScreen(
            sourceX,
            sourceY,
            destinationX,
            destinationY,
            blockRegionSize,
            CopyPixelOperation.SourceCopy);

    public void CopyFromScreen(
        int sourceX,
        int sourceY,
        int destinationX,
        int destinationY,
        Size blockRegionSize,
        CopyPixelOperation copyPixelOperation)
    {
        ThrowIfDisposed();
        ValidateCopyPixelOperation(copyPixelOperation);
        if (blockRegionSize.Width < 0 || blockRegionSize.Height < 0)
        {
            throw new ArgumentException("The capture size cannot be negative.", nameof(blockRegionSize));
        }

        if (blockRegionSize.IsEmpty)
        {
            return;
        }

        int operation = (int)copyPixelOperation;
        int modifiers = (int)(CopyPixelOperation.CaptureBlt | CopyPixelOperation.NoMirrorBitmap);
        int baseOperation = operation & ~modifiers;
        if (baseOperation != (int)CopyPixelOperation.SourceCopy)
        {
            throw new NotSupportedException(
                "Portable desktop capture currently supports SourceCopy with optional CaptureBlt and NoMirrorBitmap modifiers. Other raster operations require a typed destination and pattern-brush backend contract.");
        }

        var sourceRectangle = new Rectangle(sourceX, sourceY, blockRegionSize.Width, blockRegionSize.Height);
        byte[] pixels = GC.AllocateUninitializedArray<byte>(
            checked(blockRegionSize.Width * blockRegionSize.Height * 4));
        DesktopCaptureServices.Current.Capture(sourceRectangle, pixels);
        using Bitmap snapshot = Bitmap.CreateOwnedRgba(
            blockRegionSize.Width,
            blockRegionSize.Height,
            pixels);
        DrawImageUnscaled(snapshot, destinationX, destinationY);
    }

    public static Graphics FromProGpuDrawingContext(
        DrawingContext drawingContext,
        Matrix4x4 outerTransform)
        => FromProGpuDrawingContextCore(
            drawingContext,
            outerTransform,
            deviceBounds: null);

    /// <summary>
    /// Creates a Graphics recorder for a retained ProGPU context with explicit
    /// device-space surface bounds. Framework hosts use this overload so
    /// VisibleClipBounds and clip queries retain normal System.Drawing behavior
    /// without an HDC or an intermediate bitmap.
    /// </summary>
    public static Graphics FromProGpuDrawingContext(
        DrawingContext drawingContext,
        RectangleF deviceBounds)
        => FromProGpuDrawingContext(
            drawingContext,
            deviceBounds,
            Matrix4x4.Identity);

    /// <summary>
    /// Creates a Graphics recorder for a retained ProGPU context with explicit
    /// device-space surface bounds and a host-provided outer transform.
    /// </summary>
    public static Graphics FromProGpuDrawingContext(
        DrawingContext drawingContext,
        RectangleF deviceBounds,
        Matrix4x4 outerTransform)
    {
        if (!IsFiniteNonNegative(deviceBounds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceBounds),
                "Device bounds must be finite and have non-negative dimensions.");
        }

        return FromProGpuDrawingContextCore(
            drawingContext,
            outerTransform,
            deviceBounds);
    }

    /// <summary>
    /// Creates a host-owned Graphics recorder and invokes
    /// <paramref name="completed"/> exactly once when the recorder is disposed.
    /// This overload uses the normal ambient System.Drawing GPU context.
    /// </summary>
    public static Graphics FromProGpuDrawingContext(
        DrawingContext drawingContext,
        RectangleF deviceBounds,
        Matrix4x4 outerTransform,
        Action completed)
    {
        ArgumentNullException.ThrowIfNull(completed);

        if (!IsFiniteNonNegative(deviceBounds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceBounds),
                "Device bounds must be finite and have non-negative dimensions.");
        }

        return FromProGpuDrawingContextCore(
            drawingContext,
            outerTransform,
            deviceBounds,
            targetContext: null,
            completed);
    }

    /// <summary>
    /// Creates a host-owned graphics recorder with a synchronous
    /// batch-consumption callback and no explicit GPU device. Headless and
    /// retained-only hosts use this overload to implement flush boundaries.
    /// </summary>
    public static Graphics FromProGpuDrawingContext(
        DrawingContext drawingContext,
        RectangleF deviceBounds,
        Matrix4x4 outerTransform,
        Action<FlushIntention> flushed,
        Action completed)
    {
        ArgumentNullException.ThrowIfNull(flushed);
        ArgumentNullException.ThrowIfNull(completed);

        if (!IsFiniteNonNegative(deviceBounds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceBounds),
                "Device bounds must be finite and have non-negative dimensions.");
        }

        return FromProGpuDrawingContextCore(
            drawingContext,
            outerTransform,
            deviceBounds,
            targetContext: null,
            completed,
            flushed);
    }

    /// <summary>
    /// Creates a host-owned Graphics recorder that targets an explicit WebGPU
    /// device and invokes <paramref name="completed"/> exactly once when the
    /// recorder is disposed. Framework hosts use this overload to record on the
    /// calling thread, then commit the retained commands on their presentation
    /// thread without relying on ambient GPU state.
    /// </summary>
    public static Graphics FromProGpuDrawingContext(
        DrawingContext drawingContext,
        RectangleF deviceBounds,
        Matrix4x4 outerTransform,
        WgpuContext targetContext,
        Action completed)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        ArgumentNullException.ThrowIfNull(completed);
        ObjectDisposedException.ThrowIf(targetContext.IsDisposed, targetContext);

        if (!IsFiniteNonNegative(deviceBounds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceBounds),
                "Device bounds must be finite and have non-negative dimensions.");
        }

        return FromProGpuDrawingContextCore(
            drawingContext,
            outerTransform,
            deviceBounds,
            targetContext,
            completed);
    }

    /// <summary>
    /// Creates a host-owned graphics recorder with an explicit device and a
    /// synchronous batch-consumption callback. The callback must consume or
    /// clear the current recording before it returns.
    /// </summary>
    public static Graphics FromProGpuDrawingContext(
        DrawingContext drawingContext,
        RectangleF deviceBounds,
        Matrix4x4 outerTransform,
        WgpuContext targetContext,
        Action<FlushIntention> flushed,
        Action completed)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        ArgumentNullException.ThrowIfNull(flushed);
        ArgumentNullException.ThrowIfNull(completed);
        ObjectDisposedException.ThrowIf(targetContext.IsDisposed, targetContext);

        if (!IsFiniteNonNegative(deviceBounds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceBounds),
                "Device bounds must be finite and have non-negative dimensions.");
        }

        return FromProGpuDrawingContextCore(
            drawingContext,
            outerTransform,
            deviceBounds,
            targetContext,
            completed,
            flushed);
    }

    private static Graphics FromProGpuDrawingContextCore(
        DrawingContext drawingContext,
        Matrix4x4 outerTransform,
        RectangleF? deviceBounds,
        WgpuContext? targetContext = null,
        Action? completed = null,
        Action<FlushIntention>? flushed = null)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (!IsFinite2DAffineTransform(outerTransform))
        {
            throw new ArgumentException(
                "The native drawing-context transform must be a finite 2D affine matrix.",
                nameof(outerTransform));
        }

        return new Graphics(
            drawingContext,
            bitmap: null,
            baseTransform: new Matrix3x2(
                outerTransform.M11,
                outerTransform.M12,
                outerTransform.M21,
                outerTransform.M22,
                outerTransform.M41,
                outerTransform.M42),
            deviceBounds,
            targetContext,
            completed,
            flushed);
    }

    private static bool IsFiniteNonNegative(RectangleF bounds) =>
        float.IsFinite(bounds.X)
        && float.IsFinite(bounds.Y)
        && float.IsFinite(bounds.Width)
        && float.IsFinite(bounds.Height)
        && bounds.Width >= 0f
        && bounds.Height >= 0f;

    private static bool IsFinite2DAffineTransform(Matrix4x4 transform)
    {
        const float epsilon = 0.00001f;
        return float.IsFinite(transform.M11)
            && float.IsFinite(transform.M12)
            && float.IsFinite(transform.M21)
            && float.IsFinite(transform.M22)
            && float.IsFinite(transform.M41)
            && float.IsFinite(transform.M42)
            && MathF.Abs(transform.M13) <= epsilon
            && MathF.Abs(transform.M14) <= epsilon
            && MathF.Abs(transform.M23) <= epsilon
            && MathF.Abs(transform.M24) <= epsilon
            && MathF.Abs(transform.M31) <= epsilon
            && MathF.Abs(transform.M32) <= epsilon
            && MathF.Abs(transform.M34) <= epsilon
            && MathF.Abs(transform.M43) <= epsilon
            && MathF.Abs(transform.M33 - 1f) <= epsilon
            && MathF.Abs(transform.M44 - 1f) <= epsilon;
    }

    public static Graphics FromImage(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image is Bitmap bitmap)
        {
            return new Graphics(bitmap.RecordedContext, bitmap);
        }
        if (image is Metafile metafile)
        {
            PortableMetafileRecordingSession recording = metafile.AcquirePortableRecording();
            var context = new DrawingContext();
            return new Graphics(
                context,
                bitmap: null,
                baseTransform: Matrix3x2.Identity,
                deviceBounds: metafile.GetRecordingBounds(),
                targetContext: null,
                completed: () => metafile.CompletePortableRecording(recording, context),
                flushed: null,
                metafileRecording: recording);
        }
        throw new NotSupportedException("Only Bitmap and portable recording Metafile image types are supported.");
    }

    public static Graphics FromHwnd(IntPtr hwnd)
        => NativeGraphicsInteropServices.CreateFromWindow(hwnd);

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Graphics FromHwndInternal(IntPtr hwnd) => FromHwnd(hwnd);

    public static IntPtr GetHalftonePalette()
        => NativeGraphicsInteropServices.CreateHalftonePalette();

    public void Flush() => Flush(FlushIntention.Flush);

    public void Flush(FlushIntention intention)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ArgumentException("Parameter is not valid.");
        }

        if (_bitmap is not null)
        {
            SuspendRecorderState();
            try
            {
                _bitmap.Flush();
                if (intention == FlushIntention.Sync)
                {
                    _bitmap.GetDrawingContext().PollDevice(wait: true);
                }
            }
            finally
            {
                ResumeRecorderState();
            }

            return;
        }

        if (_flushed is null)
        {
            throw new InvalidOperationException(
                "This Graphics recorder has no host submission target to flush.");
        }

        SuspendRecorderState();
        try
        {
            _flushed(intention);
            if (_context.Commands.Count != 0)
            {
                throw new InvalidOperationException(
                    "The Graphics flush callback must consume or clear the recorded command batch before returning.");
            }

            if (intention == FlushIntention.Sync)
            {
                _targetContext?.PollDevice(wait: true);
            }
        }
        finally
        {
            ResumeRecorderState();
        }
    }

    public void TranslateTransform(float dx, float dy) =>
        TranslateTransform(dx, dy, MatrixOrder.Prepend);

    public void TranslateTransform(float dx, float dy, MatrixOrder order) =>
        _transform.Translate(dx, dy, order);

    public void TranslateClip(int dx, int dy) => TranslateClip((float)dx, dy);

    public void TranslateClip(float dx, float dy)
    {
        if (_clip is null)
        {
            return;
        }

        Region translated = _clip.Clone();
        translated.Translate(dx, dy);
        ReplaceClip(translated);
    }

    public void ScaleTransform(float sx, float sy) =>
        ScaleTransform(sx, sy, MatrixOrder.Prepend);

    public void ScaleTransform(float sx, float sy, MatrixOrder order) =>
        _transform.Scale(sx, sy, order);

    public void RotateTransform(float angle) =>
        RotateTransform(angle, MatrixOrder.Prepend);

    public void RotateTransform(float angle, MatrixOrder order) =>
        _transform.Rotate(angle, order);

    public void MultiplyTransform(Matrix matrix) =>
        MultiplyTransform(matrix, MatrixOrder.Prepend);

    public void MultiplyTransform(Matrix matrix, MatrixOrder order)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        _transform.Multiply(matrix, order);
    }

    public void ResetTransform()
    {
        _transform.Reset();
    }

    public void TransformPoints(
        CoordinateSpace destSpace,
        CoordinateSpace srcSpace,
        params PointF[] pts)
    {
        ArgumentNullException.ThrowIfNull(pts);
        TransformPoints(destSpace, srcSpace, (ReadOnlySpan<PointF>)pts);
    }

    public void TransformPoints(
        CoordinateSpace destSpace,
        CoordinateSpace srcSpace,
        scoped ReadOnlySpan<PointF> pts) =>
        TransformPointsCore(destSpace, srcSpace, AsWritableSpan(pts));

    public void TransformPoints(
        CoordinateSpace destSpace,
        CoordinateSpace srcSpace,
        params Point[] pts)
    {
        ArgumentNullException.ThrowIfNull(pts);
        TransformPoints(destSpace, srcSpace, (ReadOnlySpan<Point>)pts);
    }

    public void TransformPoints(
        CoordinateSpace destSpace,
        CoordinateSpace srcSpace,
        scoped ReadOnlySpan<Point> pts) =>
        TransformPointsCore(destSpace, srcSpace, AsWritableSpan(pts));

    private void TransformPointsCore(
        CoordinateSpace destSpace,
        CoordinateSpace srcSpace,
        Span<PointF> points)
    {
        Matrix3x2 transform = GetCoordinateTransform(destSpace, srcSpace, points.Length);
        if (srcSpace == destSpace)
        {
            return;
        }

        for (int index = 0; index < points.Length; index++)
        {
            Vector2 transformed = Vector2.Transform(
                new Vector2(points[index].X, points[index].Y),
                transform);
            points[index] = new PointF(transformed.X, transformed.Y);
        }
    }

    private void TransformPointsCore(
        CoordinateSpace destSpace,
        CoordinateSpace srcSpace,
        Span<Point> points)
    {
        Matrix3x2 transform = GetCoordinateTransform(destSpace, srcSpace, points.Length);
        if (srcSpace == destSpace)
        {
            return;
        }

        for (int index = 0; index < points.Length; index++)
        {
            Vector2 transformed = Vector2.Transform(
                new Vector2(points[index].X, points[index].Y),
                transform);
            points[index] = Point.Round(new PointF(transformed.X, transformed.Y));
        }
    }

    private Matrix3x2 GetCoordinateTransform(
        CoordinateSpace destSpace,
        CoordinateSpace srcSpace,
        int pointCount)
    {
        ThrowIfDisposed();
        ValidateCoordinateSpace(destSpace);
        ValidateCoordinateSpace(srcSpace);
        if (pointCount == 0)
        {
            throw new ArgumentException("Parameter is not valid.");
        }

        if (srcSpace == destSpace)
        {
            return Matrix3x2.Identity;
        }

        Matrix3x2 sourceToDevice = GetCoordinateToDevice(srcSpace);
        Matrix3x2 destinationToDevice = GetCoordinateToDevice(destSpace);
        if (!Matrix3x2.Invert(destinationToDevice, out Matrix3x2 deviceToDestination))
        {
            throw new ArgumentException("The destination coordinate space is not invertible.");
        }

        return sourceToDevice * deviceToDestination;
    }

    private Matrix3x2 GetCoordinateToDevice(CoordinateSpace space) =>
        space switch
        {
            CoordinateSpace.World => CombinedTransform,
            CoordinateSpace.Page => GetPageTransform() * _containerTransform * _baseTransform,
            CoordinateSpace.Device => Matrix3x2.Identity,
            _ => throw new ArgumentException("Parameter is not valid."),
        };

    private static void ValidateCoordinateSpace(CoordinateSpace space)
    {
        if (space is < CoordinateSpace.World or > CoordinateSpace.Device)
        {
            throw new ArgumentException("Parameter is not valid.");
        }
    }

    private static Span<T> AsWritableSpan<T>(ReadOnlySpan<T> source)
    {
        ref T first = ref Unsafe.AsRef(in MemoryMarshal.GetReference(source));
        return MemoryMarshal.CreateSpan(ref first, source.Length);
    }

    private Matrix3x2 CombinedTransform =>
        _transform.Value * GetPageTransform() * _containerTransform * _baseTransform;

    bool IProGpuDrawingContextSource.TryGetProGpuDrawingContext(
        out ProGpuDrawingContextState state)
    {
        Matrix3x2 transform = CombinedTransform;
        state = new ProGpuDrawingContextState(
            _context,
            new Matrix4x4(
                transform.M11,
                transform.M12,
                0f,
                0f,
                transform.M21,
                transform.M22,
                0f,
                0f,
                0f,
                0f,
                1f,
                0f,
                transform.M31,
                transform.M32,
                0f,
                1f));
        return true;
    }

    private Matrix3x2 GetPageTransform()
    {
        float unitScaleX = UnitToPixelScale(PageUnit, DpiX);
        float unitScaleY = UnitToPixelScale(PageUnit, DpiY);
        return Matrix3x2.CreateScale(unitScaleX * PageScale, unitScaleY * PageScale);
    }

    public GraphicsState Save()
    {
        var state = new GraphicsState(++_nextStateId);
        SaveContext(state, isContainer: false, hasInheritedClip: false);
        return state;
    }

    public void Restore(GraphicsState gstate)
    {
        ArgumentNullException.ThrowIfNull(gstate);
        int stateIndex = FindSavedContext(gstate, isContainer: false);
        if (stateIndex < 0)
        {
            throw new ArgumentException("The graphics state does not belong to this Graphics instance or has already been restored.", nameof(gstate));
        }

        RestoreContext(stateIndex);
    }

    public GraphicsContainer BeginContainer()
    {
        ThrowIfDisposed();
        Matrix3x2 parentTransform =
            _transform.Value * GetPageTransform() * _containerTransform;
        return BeginContainerCore(parentTransform);
    }

    public GraphicsContainer BeginContainer(
        RectangleF dstrect,
        RectangleF srcrect,
        GraphicsUnit unit)
    {
        ThrowIfDisposed();
        ValidateContainerUnit(unit);
        if (!IsFinite(dstrect) || !IsFinite(srcrect)
            || srcrect.Width == 0f || srcrect.Height == 0f)
        {
            throw new ArgumentException("Parameter is not valid.");
        }

        float unitScaleX = UnitToPixelScale(unit, DpiX);
        float unitScaleY = UnitToPixelScale(unit, DpiY);
        float sourceX = srcrect.X * unitScaleX;
        float sourceY = srcrect.Y * unitScaleY;
        float sourceWidth = srcrect.Width * unitScaleX;
        float sourceHeight = srcrect.Height * unitScaleY;
        float scaleX = dstrect.Width / sourceWidth;
        float scaleY = dstrect.Height / sourceHeight;
        Matrix3x2 mapping = new(
            scaleX,
            0f,
            0f,
            scaleY,
            dstrect.X - (sourceX * scaleX),
            dstrect.Y - (sourceY * scaleY));
        Matrix3x2 parentTransform =
            _transform.Value * GetPageTransform() * _containerTransform;
        return BeginContainerCore(mapping * parentTransform);
    }

    public GraphicsContainer BeginContainer(
        Rectangle dstrect,
        Rectangle srcrect,
        GraphicsUnit unit) =>
        BeginContainer((RectangleF)dstrect, (RectangleF)srcrect, unit);

    public void EndContainer(GraphicsContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        int stateIndex = FindSavedContext(container, isContainer: true);
        if (stateIndex < 0)
        {
            throw new ArgumentException("The graphics container does not belong to this Graphics instance or has already been ended.", nameof(container));
        }

        RestoreContext(stateIndex);
    }

    private GraphicsContainer BeginContainerCore(Matrix3x2 containerTransform)
    {
        var container = new GraphicsContainer(++_nextStateId);
        bool hasInheritedClip = _hasPushedClip;
        SaveContext(container, isContainer: true, hasInheritedClip);

        // Keep the parent's effective clip as an enclosing recorder clip while
        // exposing a fresh, infinite public clip inside the new container.
        _hasPushedClip = false;
        _clip?.Dispose();
        _clip = null;
        _clipContextTransform = Matrix3x2.Identity;

        _transform.Dispose();
        _transform = new Matrix();
        _containerTransform = containerTransform;
        SmoothingMode = SmoothingMode.None;
        InterpolationMode = InterpolationMode.Bilinear;
        TextRenderingHint = TextRenderingHint.SystemDefault;
        PixelOffsetMode = PixelOffsetMode.Default;
        _pageScale = 1f;
        _pageUnit = GraphicsUnit.Display;
        CompositingMode = CompositingMode.SourceOver;
        _compositingQuality = CompositingQuality.Default;
        _textContrast = 4;
        return container;
    }

    private void SaveContext(object state, bool isContainer, bool hasInheritedClip)
    {
        _savedStates.Add(new SavedGraphicsContext(
            state,
            isContainer,
            hasInheritedClip,
            _transform.Value,
            _containerTransform,
            SmoothingMode,
            InterpolationMode,
            TextRenderingHint,
            PixelOffsetMode,
            PageScale,
            PageUnit,
            CompositingMode,
            CompositingQuality,
            RenderingOrigin,
            TextContrast,
            _clip?.Clone(),
            _clipContextTransform));
    }

    private int FindSavedContext(object state, bool isContainer)
    {
        for (int i = _savedStates.Count - 1; i >= 0; i--)
        {
            if (_savedStates[i].IsContainer == isContainer
                && ReferenceEquals(_savedStates[i].State, state))
            {
                return i;
            }
        }

        return -1;
    }

    private void RestoreContext(int stateIndex)
    {
        if (_hasPushedClip)
        {
            _context.PopGeometryClip();
            _hasPushedClip = false;
        }

        for (int index = _savedStates.Count - 1; index >= stateIndex; index--)
        {
            if (_savedStates[index].HasInheritedClip)
            {
                _context.PopGeometryClip();
            }
        }

        SavedGraphicsContext saved = _savedStates[stateIndex];
        _transform.Dispose();
        _transform = new Matrix(saved.Transform);
        _containerTransform = saved.ContainerTransform;
        SmoothingMode = saved.SmoothingMode;
        InterpolationMode = saved.InterpolationMode;
        TextRenderingHint = saved.TextRenderingHint;
        PixelOffsetMode = saved.PixelOffsetMode;
        _pageScale = saved.PageScale;
        _pageUnit = saved.PageUnit;
        CompositingMode = saved.CompositingMode;
        _compositingQuality = saved.CompositingQuality;
        _renderingOrigin = saved.RenderingOrigin;
        _textContrast = saved.TextContrast;
        ReplaceClip(saved.Clip?.Clone(), saved.ClipContextTransform);

        for (int index = stateIndex; index < _savedStates.Count; index++)
        {
            _savedStates[index].Clip?.Dispose();
        }
        _savedStates.RemoveRange(stateIndex, _savedStates.Count - stateIndex);
    }

    private static void ValidateContainerUnit(GraphicsUnit unit)
    {
        if (unit is < GraphicsUnit.Pixel or > GraphicsUnit.Millimeter)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(unit));
        }
    }

    private static bool IsFinite(RectangleF rectangle) =>
        float.IsFinite(rectangle.X)
        && float.IsFinite(rectangle.Y)
        && float.IsFinite(rectangle.Width)
        && float.IsFinite(rectangle.Height);

    private readonly record struct SavedGraphicsContext(
        object State,
        bool IsContainer,
        bool HasInheritedClip,
        Matrix3x2 Transform,
        Matrix3x2 ContainerTransform,
        SmoothingMode SmoothingMode,
        InterpolationMode InterpolationMode,
        TextRenderingHint TextRenderingHint,
        PixelOffsetMode PixelOffsetMode,
        float PageScale,
        GraphicsUnit PageUnit,
        CompositingMode CompositingMode,
        CompositingQuality CompositingQuality,
        Point RenderingOrigin,
        int TextContrast,
        Region? Clip,
        Matrix3x2 ClipContextTransform);

    public void SetClip(Graphics g) => SetClip(g, CombineMode.Replace);

    public void SetClip(Graphics g, CombineMode combineMode)
    {
        ArgumentNullException.ThrowIfNull(g);
        using Region region = g.Clip;
        SetClip(region, combineMode);
    }

    public void SetClip(Rectangle rect) => SetClip(rect, CombineMode.Replace);
    public void SetClip(Rectangle rect, CombineMode combineMode) =>
        SetClip(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), combineMode);
    public void SetClip(RectangleF rect) => SetClip(rect, CombineMode.Replace);

    public void SetClip(RectangleF rect, CombineMode combineMode)
    {
        using var region = new Region(rect);
        SetClip(region, combineMode);
    }

    public void SetClip(GraphicsPath path) => SetClip(path, CombineMode.Replace);

    public void SetClip(GraphicsPath path, CombineMode combineMode)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var region = new Region(path);
        SetClip(region, combineMode);
    }

    public void SetClip(Region region, CombineMode combineMode)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (combineMode < CombineMode.Replace || combineMode > CombineMode.Complement)
        {
            throw new InvalidEnumArgumentException(nameof(combineMode), (int)combineMode, typeof(CombineMode));
        }

        Region next = combineMode == CombineMode.Replace
            ? region.Clone()
            : _clip?.Clone() ?? new Region();
        if (combineMode != CombineMode.Replace)
        {
            switch (combineMode)
            {
                case CombineMode.Intersect:
                    next.Intersect(region);
                    break;
                case CombineMode.Union:
                    next.Union(region);
                    break;
                case CombineMode.Xor:
                    next.Xor(region);
                    break;
                case CombineMode.Exclude:
                    next.Exclude(region);
                    break;
                case CombineMode.Complement:
                    next.Complement(region);
                    break;
            }
        }

        ReplaceClip(next);
    }

    public void IntersectClip(Rectangle rect) => SetClip(rect, CombineMode.Intersect);
    public void IntersectClip(RectangleF rect) => SetClip(rect, CombineMode.Intersect);
    public void IntersectClip(Region region) => SetClip(region, CombineMode.Intersect);
    public void ExcludeClip(Rectangle rect) => SetClip(rect, CombineMode.Exclude);
    public void ExcludeClip(Region region) => SetClip(region, CombineMode.Exclude);

    public void ResetClip() => ReplaceClip(null);

    private void ReplaceClip(Region? clip, Matrix3x2? contextTransform = null)
    {
        if (_hasPushedClip)
        {
            _context.PopGeometryClip();
            _hasPushedClip = false;
        }

        _clip?.Dispose();
        _clip = clip;
        _clipContextTransform = clip is null
            ? Matrix3x2.Identity
            : contextTransform ?? GetCumulativeContextTransform();
        PushCurrentClip();
    }

    private void PushCurrentClip()
    {
        if (_clip == null || _clip.IsInfinite(this))
        {
            return;
        }

        _context.PushGeometryClip(
            _clip.CreatePathGeometry(GetFiniteDrawingUniverse()),
            CurrentTransform4x4());
        _hasPushedClip = true;
    }

    private void SuspendRecorderState()
    {
        if (_hasPushedClip)
        {
            _context.PopGeometryClip();
            _hasPushedClip = false;
        }

        PopCurrentCompositingMode();
    }

    private void ResumeRecorderState()
    {
        PushCurrentCompositingMode();
        PushCurrentClip();
    }

    private void PushCurrentCompositingMode()
    {
        if (_compositingMode != CompositingMode.SourceCopy)
        {
            return;
        }

        _context.PushBlendMode(GpuBlendMode.Src);
        _hasPushedCompositingMode = true;
    }

    private void PopCurrentCompositingMode()
    {
        if (!_hasPushedCompositingMode)
        {
            return;
        }

        _context.PopBlendMode();
        _hasPushedCompositingMode = false;
    }

    private RectangleF GetFiniteDrawingUniverse()
    {
        RectangleF visible = VisibleClipBounds;
        return visible.Width > 0f && visible.Height > 0f
            ? visible
            : new RectangleF(-1_000_000f, -1_000_000f, 2_000_000f, 2_000_000f);
    }

    private Vector2 Tx(float x, float y)
    {
        return Vector2.Transform(new Vector2(x, y), CombinedTransform);
    }

    private Vector2 Tx(PointF pt)
    {
        return Vector2.Transform(new Vector2(pt.X, pt.Y), CombinedTransform);
    }

    private Vector2 Tx(Vector2 pt)
    {
        return Vector2.Transform(pt, CombinedTransform);
    }

    private bool HasRotationOrShear =>
        Math.Abs(CombinedTransform.M12) > 1e-5f
        || Math.Abs(CombinedTransform.M21) > 1e-5f;

    private bool RequiresTransformedStrokePath
    {
        get
        {
            if (HasRotationOrShear)
            {
                return true;
            }

            var xAxisLengthSquared =
                (CombinedTransform.M11 * CombinedTransform.M11) +
                (CombinedTransform.M12 * CombinedTransform.M12);
            var yAxisLengthSquared =
                (CombinedTransform.M21 * CombinedTransform.M21) +
                (CombinedTransform.M22 * CombinedTransform.M22);
            if (!float.IsFinite(xAxisLengthSquared) ||
                !float.IsFinite(yAxisLengthSquared) ||
                xAxisLengthSquared <= 1e-10f ||
                yAxisLengthSquared <= 1e-10f)
            {
                return true;
            }

            var comparisonScale = MathF.Max(
                1f,
                MathF.Max(xAxisLengthSquared, yAxisLengthSquared));
            return MathF.Abs(xAxisLengthSquared - yAxisLengthSquared) >
                (comparisonScale * 1e-5f);
        }
    }

    private Rect TxRect(RectangleF rect)
    {
        var p1 = Tx(rect.X, rect.Y);
        var p2 = Tx(rect.Right, rect.Bottom);
        var x1 = MathF.Min(p1.X, p2.X);
        var y1 = MathF.Min(p1.Y, p2.Y);
        var x2 = MathF.Max(p1.X, p2.X);
        var y2 = MathF.Max(p1.Y, p2.Y);
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    private Matrix4x4 CurrentTransform4x4()
    {
        return ToMatrix4x4(CombinedTransform);
    }

    private static Matrix4x4 ToMatrix4x4(Matrix3x2 m32)
    {
        return new Matrix4x4(
            m32.M11, m32.M12, 0f, 0f,
            m32.M21, m32.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            m32.M31, m32.M32, 0f, 1f);
    }

    private ProGPU.Vector.Pen TransformPen(Pen pen)
    {
        float widthScale = GetFallbackStrokeWidthScale();
        return pen.ToProGpuPen(pen.Width * widthScale, _renderingOrigin);
    }

    private ProGPU.Vector.Brush TransformBrush(Brush brush)
        => TransformBrush(brush, _renderingOrigin);

    internal static ProGPU.Vector.Brush TransformBrush(Brush brush, Point renderingOrigin) =>
        brush is HatchBrush hatchBrush
            ? hatchBrush.ToProGpuBrush(renderingOrigin)
            : brush.ToProGpuBrush();

    private float GetFallbackStrokeWidthScale()
    {
        float xAxis = Vector2.TransformNormal(Vector2.UnitX, CombinedTransform).Length();
        float yAxis = Vector2.TransformNormal(Vector2.UnitY, CombinedTransform).Length();
        float fallbackScale = (xAxis + yAxis) * 0.5f;
        return float.IsFinite(fallbackScale) && fallbackScale > 1e-5f
            ? fallbackScale
            : 1f;
    }

    public void Clear(Color color)
    {
        float w = _bitmap != null ? _bitmap.Width : 100000f;
        float h = _bitmap != null ? _bitmap.Height : 100000f;
        var brush = new SolidBrush(color);
        _context.PushBlendMode(GpuBlendMode.Src);
        _context.DrawRectangle(TransformBrush(brush), null, new Rect(0, 0, w, h));
        _context.PopBlendMode();
    }

    public Color GetNearestColor(Color color) => color;

    internal void SetTransformedPixel(Color color, Point point)
    {
        ThrowIfDisposed();
        Vector2 transformed = Vector2.Transform(new Vector2(point.X, point.Y), CombinedTransform);
        if (!float.IsFinite(transformed.X) || !float.IsFinite(transformed.Y))
        {
            throw new ArgumentException("Parameter is not valid.");
        }

        Point devicePixel = Point.Round(new PointF(transformed.X, transformed.Y));
        var brush = new ProGPU.Vector.SolidColorBrush(new Vector4(
            color.R / 255f,
            color.G / 255f,
            color.B / 255f,
            color.A / 255f));
        _context.DrawRectangle(
            brush,
            null,
            new Rect(devicePixel.X, devicePixel.Y, 1f, 1f));
    }

    internal void FillVertexMesh(Vector2[] positions, Vector4[] colors)
    {
        ThrowIfDisposed();
        var mesh = VertexMesh2D.CreateOwned(
            VertexMeshTopology.Triangles,
            positions,
            [],
            colors,
            []);
        _context.DrawVertexMesh(
            VertexColorMeshBrush,
            mesh,
            VertexColorBlendMode.Dst,
            CurrentTransform4x4(),
            isEdgeAliased: true);
    }

    public void DrawLine(Pen pen, PointF p1, PointF p2) => DrawLine(pen, p1.X, p1.Y, p2.X, p2.Y);
    public void DrawLine(Pen pen, Point p1, Point p2) => DrawLine(pen, p1.X, p1.Y, p2.X, p2.Y);
    public void DrawLine(Pen pen, int x1, int y1, int x2, int y2) => DrawLine(pen, (float)x1, y1, x2, y2);

    public void DrawLine(Pen pen, float x1, float y1, float x2, float y2)
    {
        ArgumentNullException.ThrowIfNull(pen);
        if (pen.RequiresWidenedGeometry)
        {
            var geometry = new PathGeometry();
            var figure = new PathFigure(new Vector2(x1, y1));
            figure.Segments.Add(new LineSegment(new Vector2(x2, y2)));
            geometry.Figures.Add(figure);
            DrawTransformedPath(pen, geometry);
            return;
        }

        var localStart = new Vector2(x1, y1);
        var localEnd = new Vector2(x2, y2);
        _context.DrawLine(
            pen.ToProGpuPen(pen.Width, _renderingOrigin),
            localStart,
            localEnd,
            CurrentTransform4x4());
    }

    public void DrawLines(Pen pen, PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawLines(pen, (ReadOnlySpan<PointF>)points);
    }

    public void DrawLines(Pen pen, Point[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawLines(pen, (ReadOnlySpan<Point>)points);
    }

    public void DrawLines(Pen pen, ReadOnlySpan<Point> points)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath();
        path.AddLines(points);
        DrawTransformedPath(pen, path.Geometry);
    }

    public void DrawLines(Pen pen, ReadOnlySpan<PointF> points)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath();
        path.AddLines(points);
        DrawTransformedPath(pen, path.Geometry);
    }

    public void DrawArc(Pen pen, Rectangle rect, float startAngle, float sweepAngle) =>
        DrawArc(pen, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    public void DrawArc(Pen pen, RectangleF rect, float startAngle, float sweepAngle) =>
        DrawArc(pen, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    public void DrawArc(Pen pen, int x, int y, int width, int height, int startAngle, int sweepAngle) =>
        DrawArc(pen, (float)x, y, width, height, startAngle, sweepAngle);

    public void DrawArc(Pen pen, float x, float y, float width, float height, float startAngle, float sweepAngle)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath();
        path.AddArc(x, y, width, height, startAngle, sweepAngle);
        DrawPath(pen, path);
    }

    public void DrawBezier(Pen pen, Point pt1, Point pt2, Point pt3, Point pt4) =>
        DrawBezier(pen, pt1.X, pt1.Y, pt2.X, pt2.Y, pt3.X, pt3.Y, pt4.X, pt4.Y);

    public void DrawBezier(Pen pen, PointF pt1, PointF pt2, PointF pt3, PointF pt4) =>
        DrawBezier(pen, pt1.X, pt1.Y, pt2.X, pt2.Y, pt3.X, pt3.Y, pt4.X, pt4.Y);

    public void DrawBezier(
        Pen pen,
        float x1,
        float y1,
        float x2,
        float y2,
        float x3,
        float y3,
        float x4,
        float y4)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath();
        path.AddBezier(x1, y1, x2, y2, x3, y3, x4, y4);
        DrawPath(pen, path);
    }

    public void DrawBeziers(Pen pen, Point[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawBeziers(pen, (ReadOnlySpan<Point>)points);
    }

    public void DrawBeziers(Pen pen, PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawBeziers(pen, (ReadOnlySpan<PointF>)points);
    }

    public void DrawBeziers(Pen pen, ReadOnlySpan<Point> points)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath();
        path.AddBeziers(points);
        DrawPath(pen, path);
    }

    public void DrawBeziers(Pen pen, ReadOnlySpan<PointF> points)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath();
        path.AddBeziers(points);
        DrawPath(pen, path);
    }

    public void DrawClosedCurve(Pen pen, Point[] points) =>
        DrawClosedCurve(pen, points, 0.5f, FillMode.Alternate);

    public void DrawClosedCurve(Pen pen, Point[] points, float tension, FillMode fillmode)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawClosedCurve(pen, (ReadOnlySpan<Point>)points, tension, fillmode);
    }

    public void DrawClosedCurve(Pen pen, PointF[] points) =>
        DrawClosedCurve(pen, points, 0.5f, FillMode.Alternate);

    public void DrawClosedCurve(Pen pen, PointF[] points, float tension, FillMode fillmode)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawClosedCurve(pen, (ReadOnlySpan<PointF>)points, tension, fillmode);
    }

    public void DrawClosedCurve(Pen pen, ReadOnlySpan<Point> points) =>
        DrawClosedCurve(pen, points, 0.5f, FillMode.Alternate);

    public void DrawClosedCurve(Pen pen, ReadOnlySpan<Point> points, float tension, FillMode fillmode)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath(fillmode);
        path.AddClosedCurve(points, tension);
        DrawPath(pen, path);
    }

    public void DrawClosedCurve(Pen pen, ReadOnlySpan<PointF> points) =>
        DrawClosedCurve(pen, points, 0.5f, FillMode.Alternate);

    public void DrawClosedCurve(Pen pen, ReadOnlySpan<PointF> points, float tension, FillMode fillmode)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath(fillmode);
        path.AddClosedCurve(points, tension);
        DrawPath(pen, path);
    }

    public void DrawCurve(Pen pen, PointF[] points) =>
        DrawCurve(pen, points, 0, GetCurveSegmentCount(points), 0.5f);

    public void DrawCurve(Pen pen, PointF[] points, float tension) =>
        DrawCurve(pen, points, 0, GetCurveSegmentCount(points), tension);

    public void DrawCurve(Pen pen, PointF[] points, int offset, int numberOfSegments) =>
        DrawCurve(pen, points, offset, numberOfSegments, 0.5f);

    public void DrawCurve(Pen pen, PointF[] points, int offset, int numberOfSegments, float tension)
    {
        ArgumentNullException.ThrowIfNull(pen);
        ValidateCurveRange(points, offset, numberOfSegments);

        var geometry = new PathGeometry();
        PointF start = points[offset];
        var figure = new PathFigure(new Vector2(start.X, start.Y));
        geometry.Figures.Add(figure);
        float scale = tension / 3f;

        for (int index = offset; index < offset + numberOfSegments; index++)
        {
            PointF current = points[index];
            PointF previous = index == 0 ? current : points[index - 1];
            PointF next = points[index + 1];
            PointF following = index + 2 < points.Length ? points[index + 2] : next;
            figure.Segments.Add(new CubicBezierSegment(
                new Vector2(
                    current.X + ((next.X - previous.X) * scale),
                    current.Y + ((next.Y - previous.Y) * scale)),
                new Vector2(
                    next.X - ((following.X - current.X) * scale),
                    next.Y - ((following.Y - current.Y) * scale)),
                new Vector2(next.X, next.Y)));
        }

        DrawTransformedPath(pen, geometry);
    }

    public void DrawCurve(Pen pen, Point[] points) =>
        DrawCurve(pen, points, 0, GetCurveSegmentCount(points), 0.5f);

    public void DrawCurve(Pen pen, Point[] points, float tension) =>
        DrawCurve(pen, points, 0, GetCurveSegmentCount(points), tension);

    public void DrawCurve(Pen pen, Point[] points, int offset, int numberOfSegments, float tension)
    {
        ArgumentNullException.ThrowIfNull(pen);
        ValidateCurveRange(points, offset, numberOfSegments);

        var geometry = new PathGeometry();
        Point start = points[offset];
        var figure = new PathFigure(new Vector2(start.X, start.Y));
        geometry.Figures.Add(figure);
        float scale = tension / 3f;

        for (int index = offset; index < offset + numberOfSegments; index++)
        {
            Point current = points[index];
            Point previous = index == 0 ? current : points[index - 1];
            Point next = points[index + 1];
            Point following = index + 2 < points.Length ? points[index + 2] : next;
            figure.Segments.Add(new CubicBezierSegment(
                new Vector2(
                    current.X + ((next.X - previous.X) * scale),
                    current.Y + ((next.Y - previous.Y) * scale)),
                new Vector2(
                    next.X - ((following.X - current.X) * scale),
                    next.Y - ((following.Y - current.Y) * scale)),
                new Vector2(next.X, next.Y)));
        }

        DrawTransformedPath(pen, geometry);
    }

    public void DrawCurve(Pen pen, ReadOnlySpan<Point> points) => DrawCurve(pen, points, 0.5f);

    public void DrawCurve(Pen pen, ReadOnlySpan<Point> points, float tension)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath();
        path.AddCurve(points, tension);
        DrawPath(pen, path);
    }

    public void DrawCurve(Pen pen, ReadOnlySpan<Point> points, int offset, int numberOfSegments, float tension)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath();
        path.AddCurve(points, offset, numberOfSegments, tension);
        DrawPath(pen, path);
    }

    public void DrawCurve(Pen pen, ReadOnlySpan<PointF> points) => DrawCurve(pen, points, 0.5f);

    public void DrawCurve(Pen pen, ReadOnlySpan<PointF> points, float tension)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath();
        path.AddCurve(points, tension);
        DrawPath(pen, path);
    }

    public void DrawCurve(Pen pen, ReadOnlySpan<PointF> points, int offset, int numberOfSegments) =>
        DrawCurve(pen, points, offset, numberOfSegments, 0.5f);

    public void DrawCurve(Pen pen, ReadOnlySpan<PointF> points, int offset, int numberOfSegments, float tension)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath();
        path.AddCurve(points, offset, numberOfSegments, tension);
        DrawPath(pen, path);
    }

    private static int GetCurveSegmentCount<TPoint>(TPoint[]? points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return points.Length - 1;
    }

    private static void ValidateCurveRange<TPoint>(TPoint[]? points, int offset, int numberOfSegments)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Length < 2)
        {
            throw new ArgumentException("At least two points are required to draw a curve.", nameof(points));
        }

        if (offset < 0 || numberOfSegments < 1 || offset >= points.Length || numberOfSegments > points.Length - offset - 1)
        {
            throw new ArgumentException("The curve offset and segment count must describe a valid point range.", nameof(numberOfSegments));
        }
    }

    public void DrawRectangle(Pen pen, Rectangle rect) => DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
    public void DrawRectangle(Pen pen, RectangleF rect) => DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
    public void DrawRectangle(Pen pen, int x, int y, int width, int height) => DrawRectangle(pen, (float)x, y, width, height);

    public void DrawRectangle(Pen pen, float x, float y, float width, float height)
    {
        if (RequiresTransformedStrokePath || pen.RequiresWidenedGeometry)
        {
            using var path = new GraphicsPath();
            path.AddRectangle(new RectangleF(x, y, width, height));
            DrawPath(pen, path);
        }
        else
        {
            var rect = TxRect(new RectangleF(x, y, width, height));
            var nativePen = TransformPen(pen);
            float roundedThickness = MathF.Round(nativePen.Thickness);
            if (roundedThickness > 0f
                && MathF.Abs(nativePen.Thickness - roundedThickness) <= 1e-5f
                && ((int)roundedThickness & 1) != 0)
            {
                // The vector shader samples at pixel centers. Align odd-width
                // GDI strokes to those centers so a one-pixel focus rectangle
                // covers its declared integer boundary instead of falling
                // exactly between adjacent samples.
                rect = new Rect(rect.X + 0.5f, rect.Y + 0.5f, rect.Width, rect.Height);
            }

            _context.DrawRectangle(null, nativePen, rect);
        }
    }

    public void DrawRectangles(Pen pen, Rectangle[] rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        DrawRectangles(pen, (ReadOnlySpan<Rectangle>)rects);
    }

    public void DrawRectangles(Pen pen, RectangleF[] rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        DrawRectangles(pen, (ReadOnlySpan<RectangleF>)rects);
    }

    public void DrawRectangles(Pen pen, ReadOnlySpan<Rectangle> rects)
    {
        ArgumentNullException.ThrowIfNull(pen);
        foreach (Rectangle rect in rects) DrawRectangle(pen, rect);
    }

    public void DrawRectangles(Pen pen, ReadOnlySpan<RectangleF> rects)
    {
        ArgumentNullException.ThrowIfNull(pen);
        foreach (RectangleF rect in rects) DrawRectangle(pen, rect);
    }

    public void FillRectangle(Brush brush, Rectangle rect) => FillRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height);
    public void FillRectangle(Brush brush, RectangleF rect) => FillRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height);
    public void FillRectangle(Brush brush, int x, int y, int width, int height) => FillRectangle(brush, (float)x, y, width, height);

    public void FillRectangle(Brush brush, float x, float y, float width, float height)
    {
        if (brush is TextureBrush textureBrush)
        {
            FillTextureRectangle(textureBrush, new RectangleF(x, y, width, height));
            return;
        }

        if (HasRotationOrShear)
        {
            using var path = new GraphicsPath();
            path.AddRectangle(new RectangleF(x, y, width, height));
            FillPath(brush, path);
        }
        else
        {
            var rect = TxRect(new RectangleF(x, y, width, height));
            _context.DrawRectangle(TransformBrush(brush), null, rect);
        }
    }

    private void FillTextureRectangle(TextureBrush brush, RectangleF rect)
    {
        if (rect.Width <= 0f || rect.Height <= 0f)
        {
            return;
        }

        _context.PushClip(
            new Rect(rect.X, rect.Y, rect.Width, rect.Height),
            CurrentTransform4x4());
        try
        {
            EmitTextureTiles(brush, rect);
        }
        finally
        {
            _context.PopClip();
        }
    }

    private void FillTexturePath(TextureBrush brush, PathGeometry geometry)
    {
        if (!geometry.TryGetBounds(out Vector2 minimum, out Vector2 maximum))
        {
            return;
        }

        var bounds = new RectangleF(
            minimum.X,
            minimum.Y,
            maximum.X - minimum.X,
            maximum.Y - minimum.Y);
        if (bounds.Width <= 0f || bounds.Height <= 0f)
        {
            return;
        }

        _context.PushGeometryClip(geometry, CurrentTransform4x4());
        try
        {
            EmitTextureTiles(brush, bounds);
        }
        finally
        {
            _context.PopGeometryClip();
        }
    }

    private void EmitTextureTiles(TextureBrush brush, RectangleF worldBounds)
    {
        Bitmap bitmap = brush.Bitmap;
        float tileWidth = bitmap.Width;
        float tileHeight = bitmap.Height;
        Matrix3x2 brushTransform = brush.TransformValue;
        if (!Matrix3x2.Invert(brushTransform, out Matrix3x2 inverseBrushTransform))
        {
            throw new ArgumentException("The texture transform must be invertible.", nameof(brush));
        }

        RectangleF textureBounds = TransformBounds(worldBounds, inverseBrushTransform);
        int firstX;
        int lastX;
        int firstY;
        int lastY;
        if (brush.WrapMode == WrapMode.Clamp)
        {
            firstX = lastX = firstY = lastY = 0;
        }
        else
        {
            firstX = checked((int)MathF.Floor(textureBounds.Left / tileWidth));
            lastX = checked((int)MathF.Ceiling(textureBounds.Right / tileWidth) - 1);
            firstY = checked((int)MathF.Floor(textureBounds.Top / tileHeight));
            lastY = checked((int)MathF.Ceiling(textureBounds.Bottom / tileHeight) - 1);
        }

        long tileCount = checked((long)(lastX - firstX + 1) * (lastY - firstY + 1));
        if (tileCount <= 0)
        {
            return;
        }

        const int MaxRetainedTilesPerFill = 1_000_000;
        if (tileCount > MaxRetainedTilesPerFill)
        {
            throw new InvalidOperationException(
                "The texture fill exceeds the retained tile safety limit.");
        }

        GpuTexture retainedTexture = RetainBitmapTexture(bitmap);
        var sourceRect = new Rect(0f, 0f, tileWidth, tileHeight);
        var destinationRect = new Rect(0f, 0f, tileWidth, tileHeight);
        WrapMode wrapMode = brush.WrapMode;
        Matrix3x2 graphicsTransform = CombinedTransform;
        TextureSamplingMode samplingMode = GetTextureSamplingMode();

        for (int tileY = firstY; tileY <= lastY; tileY++)
        {
            bool flipY = wrapMode is WrapMode.TileFlipY or WrapMode.TileFlipXY
                && (tileY & 1) != 0;
            for (int tileX = firstX; tileX <= lastX; tileX++)
            {
                bool flipX = wrapMode is WrapMode.TileFlipX or WrapMode.TileFlipXY
                    && (tileX & 1) != 0;
                Matrix3x2 tileTransform = Matrix3x2.CreateScale(
                        flipX ? -1f : 1f,
                        flipY ? -1f : 1f)
                    * Matrix3x2.CreateTranslation(
                        (flipX ? tileX + 1 : tileX) * tileWidth,
                        (flipY ? tileY + 1 : tileY) * tileHeight)
                    * brushTransform
                    * graphicsTransform;

                _context.DrawTexture(
                    retainedTexture,
                    destinationRect,
                    sourceRect,
                    ToMatrix4x4(tileTransform),
                    samplingMode);
            }
        }
    }

    private static RectangleF TransformBounds(RectangleF bounds, Matrix3x2 transform)
    {
        Vector2 topLeft = Vector2.Transform(new Vector2(bounds.Left, bounds.Top), transform);
        Vector2 topRight = Vector2.Transform(new Vector2(bounds.Right, bounds.Top), transform);
        Vector2 bottomLeft = Vector2.Transform(new Vector2(bounds.Left, bounds.Bottom), transform);
        Vector2 bottomRight = Vector2.Transform(new Vector2(bounds.Right, bounds.Bottom), transform);
        float left = MathF.Min(MathF.Min(topLeft.X, topRight.X), MathF.Min(bottomLeft.X, bottomRight.X));
        float top = MathF.Min(MathF.Min(topLeft.Y, topRight.Y), MathF.Min(bottomLeft.Y, bottomRight.Y));
        float right = MathF.Max(MathF.Max(topLeft.X, topRight.X), MathF.Max(bottomLeft.X, bottomRight.X));
        float bottom = MathF.Max(MathF.Max(topLeft.Y, topRight.Y), MathF.Max(bottomLeft.Y, bottomRight.Y));
        return new RectangleF(left, top, right - left, bottom - top);
    }

    public void FillRectangles(Brush brush, Rectangle[] rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        FillRectangles(brush, (ReadOnlySpan<Rectangle>)rects);
    }

    public void FillRectangles(Brush brush, RectangleF[] rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        FillRectangles(brush, (ReadOnlySpan<RectangleF>)rects);
    }

    public void FillRectangles(Brush brush, ReadOnlySpan<Rectangle> rects)
    {
        ArgumentNullException.ThrowIfNull(brush);
        foreach (Rectangle rect in rects) FillRectangle(brush, rect);
    }

    public void FillRectangles(Brush brush, ReadOnlySpan<RectangleF> rects)
    {
        ArgumentNullException.ThrowIfNull(brush);
        foreach (RectangleF rect in rects) FillRectangle(brush, rect);
    }

    public void DrawEllipse(Pen pen, Rectangle rect) => DrawEllipse(pen, rect.X, rect.Y, rect.Width, rect.Height);
    public void DrawEllipse(Pen pen, RectangleF rect) => DrawEllipse(pen, rect.X, rect.Y, rect.Width, rect.Height);
    public void DrawEllipse(Pen pen, int x, int y, int width, int height) => DrawEllipse(pen, (float)x, y, width, height);

    public void DrawEllipse(Pen pen, float x, float y, float width, float height)
    {
        if (RequiresTransformedStrokePath || pen.RequiresWidenedGeometry)
        {
            using var path = new GraphicsPath();
            path.AddEllipse(x, y, width, height);
            DrawPath(pen, path);
        }
        else
        {
            float rx = width / 2f;
            float ry = height / 2f;
            var center = Tx(x + rx, y + ry);
            var scale = new Vector2(
                Vector2.TransformNormal(Vector2.UnitX, CombinedTransform).Length(),
                Vector2.TransformNormal(Vector2.UnitY, CombinedTransform).Length()
            );
            _context.DrawEllipse(null, TransformPen(pen), center, rx * scale.X, ry * scale.Y);
        }
    }

    public void FillEllipse(Brush brush, Rectangle rect) => FillEllipse(brush, rect.X, rect.Y, rect.Width, rect.Height);
    public void FillEllipse(Brush brush, RectangleF rect) => FillEllipse(brush, rect.X, rect.Y, rect.Width, rect.Height);
    public void FillEllipse(Brush brush, int x, int y, int width, int height) => FillEllipse(brush, (float)x, y, width, height);

    public void FillEllipse(Brush brush, float x, float y, float width, float height)
    {
        if (brush is TextureBrush textureBrush)
        {
            using var path = new GraphicsPath();
            path.AddEllipse(x, y, width, height);
            FillTexturePath(textureBrush, path.Geometry);
            return;
        }

        if (HasRotationOrShear)
        {
            using var path = new GraphicsPath();
            path.AddEllipse(x, y, width, height);
            FillPath(brush, path);
        }
        else
        {
            float rx = width / 2f;
            float ry = height / 2f;
            var center = Tx(x + rx, y + ry);
            var scale = new Vector2(
                Vector2.TransformNormal(Vector2.UnitX, CombinedTransform).Length(),
                Vector2.TransformNormal(Vector2.UnitY, CombinedTransform).Length()
            );
            _context.DrawEllipse(TransformBrush(brush), null, center, rx * scale.X, ry * scale.Y);
        }
    }

    public void DrawPolygon(Pen pen, PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawPolygon(pen, (ReadOnlySpan<PointF>)points);
    }

    public void DrawPolygon(Pen pen, Point[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawPolygon(pen, (ReadOnlySpan<Point>)points);
    }

    public void DrawPolygon(Pen pen, ReadOnlySpan<Point> points)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath();
        path.AddPolygon(points);
        DrawPath(pen, path);
    }

    public void DrawPolygon(Pen pen, ReadOnlySpan<PointF> points)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath();
        path.AddPolygon(points);
        DrawPath(pen, path);
    }

    public void FillPolygon(Brush brush, PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        FillPolygon(brush, (ReadOnlySpan<PointF>)points, FillMode.Alternate);
    }

    public void FillPolygon(Brush brush, Point[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        FillPolygon(brush, (ReadOnlySpan<Point>)points, FillMode.Alternate);
    }

    public void FillPolygon(Brush brush, ReadOnlySpan<Point> points) =>
        FillPolygon(brush, points, FillMode.Alternate);

    public void FillPolygon(Brush brush, ReadOnlySpan<PointF> points) =>
        FillPolygon(brush, points, FillMode.Alternate);

    public void FillPolygon(Brush brush, Point[] points, FillMode fillMode)
    {
        ArgumentNullException.ThrowIfNull(points);
        FillPolygon(brush, (ReadOnlySpan<Point>)points, fillMode);
    }

    public void FillPolygon(Brush brush, PointF[] points, FillMode fillMode)
    {
        ArgumentNullException.ThrowIfNull(points);
        FillPolygon(brush, (ReadOnlySpan<PointF>)points, fillMode);
    }

    public void FillPolygon(Brush brush, ReadOnlySpan<Point> points, FillMode fillMode)
    {
        ArgumentNullException.ThrowIfNull(brush);
        using var path = new GraphicsPath(fillMode);
        path.AddPolygon(points);
        FillPath(brush, path);
    }

    public void FillPolygon(Brush brush, ReadOnlySpan<PointF> points, FillMode fillMode)
    {
        ArgumentNullException.ThrowIfNull(brush);
        using var path = new GraphicsPath(fillMode);
        path.AddPolygon(points);
        FillPath(brush, path);
    }

    public void DrawPie(Pen pen, Rectangle rect, float startAngle, float sweepAngle) =>
        DrawPie(pen, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    public void DrawPie(Pen pen, RectangleF rect, float startAngle, float sweepAngle) =>
        DrawPie(pen, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    public void DrawPie(Pen pen, int x, int y, int width, int height, int startAngle, int sweepAngle) =>
        DrawPie(pen, (float)x, y, width, height, startAngle, sweepAngle);

    public void DrawPie(Pen pen, float x, float y, float width, float height, float startAngle, float sweepAngle)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath();
        path.AddPie(x, y, width, height, startAngle, sweepAngle);
        DrawPath(pen, path);
    }

    public void FillPie(Brush brush, Rectangle rect, float startAngle, float sweepAngle) =>
        FillPie(brush, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    public void FillPie(Brush brush, RectangleF rect, float startAngle, float sweepAngle) =>
        FillPie(brush, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    public void FillPie(Brush brush, int x, int y, int width, int height, int startAngle, int sweepAngle) =>
        FillPie(brush, (float)x, y, width, height, startAngle, sweepAngle);

    public void FillPie(Brush brush, float x, float y, float width, float height, float startAngle, float sweepAngle)
    {
        ArgumentNullException.ThrowIfNull(brush);
        using var path = new GraphicsPath();
        path.AddPie(x, y, width, height, startAngle, sweepAngle);
        FillPath(brush, path);
    }

    public void DrawRoundedRectangle(Pen pen, Rectangle rect, Size radius)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath();
        path.AddRoundedRectangle(rect, radius);
        DrawPath(pen, path);
    }

    public void DrawRoundedRectangle(Pen pen, RectangleF rect, SizeF radius)
    {
        ArgumentNullException.ThrowIfNull(pen);
        using var path = new GraphicsPath();
        path.AddRoundedRectangle(rect, radius);
        DrawPath(pen, path);
    }

    public void FillRoundedRectangle(Brush brush, Rectangle rect, Size radius)
    {
        ArgumentNullException.ThrowIfNull(brush);
        using var path = new GraphicsPath();
        path.AddRoundedRectangle(rect, radius);
        FillPath(brush, path);
    }

    public void FillRoundedRectangle(Brush brush, RectangleF rect, SizeF radius)
    {
        ArgumentNullException.ThrowIfNull(brush);
        using var path = new GraphicsPath();
        path.AddRoundedRectangle(rect, radius);
        FillPath(brush, path);
    }

    public void FillClosedCurve(Brush brush, Point[] points) =>
        FillClosedCurve(brush, points, FillMode.Alternate, 0.5f);

    public void FillClosedCurve(Brush brush, Point[] points, FillMode fillmode) =>
        FillClosedCurve(brush, points, fillmode, 0.5f);

    public void FillClosedCurve(Brush brush, Point[] points, FillMode fillmode, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        FillClosedCurve(brush, (ReadOnlySpan<Point>)points, fillmode, tension);
    }

    public void FillClosedCurve(Brush brush, PointF[] points) =>
        FillClosedCurve(brush, points, FillMode.Alternate, 0.5f);

    public void FillClosedCurve(Brush brush, PointF[] points, FillMode fillmode) =>
        FillClosedCurve(brush, points, fillmode, 0.5f);

    public void FillClosedCurve(Brush brush, PointF[] points, FillMode fillmode, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        FillClosedCurve(brush, (ReadOnlySpan<PointF>)points, fillmode, tension);
    }

    public void FillClosedCurve(Brush brush, ReadOnlySpan<Point> points) =>
        FillClosedCurve(brush, points, FillMode.Alternate, 0.5f);

    public void FillClosedCurve(Brush brush, ReadOnlySpan<Point> points, FillMode fillmode) =>
        FillClosedCurve(brush, points, fillmode, 0.5f);

    public void FillClosedCurve(Brush brush, ReadOnlySpan<Point> points, FillMode fillmode, float tension)
    {
        ArgumentNullException.ThrowIfNull(brush);
        using var path = new GraphicsPath(fillmode);
        path.AddClosedCurve(points, tension);
        FillPath(brush, path);
    }

    public void FillClosedCurve(Brush brush, ReadOnlySpan<PointF> points) =>
        FillClosedCurve(brush, points, FillMode.Alternate, 0.5f);

    public void FillClosedCurve(Brush brush, ReadOnlySpan<PointF> points, FillMode fillmode) =>
        FillClosedCurve(brush, points, fillmode, 0.5f);

    public void FillClosedCurve(Brush brush, ReadOnlySpan<PointF> points, FillMode fillmode, float tension)
    {
        ArgumentNullException.ThrowIfNull(brush);
        using var path = new GraphicsPath(fillmode);
        path.AddClosedCurve(points, tension);
        FillPath(brush, path);
    }

    public void DrawPath(Pen pen, GraphicsPath path)
    {
        if (path == null) return;
        DrawTransformedPath(pen, path.Geometry);
    }

    private void DrawTransformedPath(Pen pen, PathGeometry geometry)
    {
        if (pen.RequiresWidenedGeometry)
        {
            if (!GraphicsPath.TryCreateWidenedGeometry(
                    geometry,
                    pen,
                    matrix: null,
                    flatness: 0.25f,
                    out PathGeometry widened))
            {
                throw new ArgumentException("Parameter is not valid.", nameof(pen));
            }

            if (widened.Figures.Count != 0)
            {
                _context.DrawPath(
                    pen.ToProGpuBrush(_renderingOrigin),
                    null,
                    widened,
                    CurrentTransform4x4());
            }

            return;
        }

        _context.DrawPath(
            null,
            pen.ToProGpuPen(pen.Width, _renderingOrigin),
            geometry,
            CurrentTransform4x4());
    }

    public void FillPath(Brush brush, GraphicsPath path)
    {
        if (path == null) return;
        if (brush is TextureBrush textureBrush)
        {
            FillTexturePath(textureBrush, path.Geometry);
            return;
        }

        _context.DrawPath(TransformBrush(brush), null, path.Geometry, CurrentTransform4x4());
    }

    public void FillRegion(Brush brush, Region region)
    {
        ArgumentNullException.ThrowIfNull(brush);
        ArgumentNullException.ThrowIfNull(region);
        PathGeometry geometry = region.CreatePathGeometry(GetFiniteDrawingUniverse());
        if (brush is TextureBrush textureBrush)
        {
            FillTexturePath(textureBrush, geometry);
            return;
        }

        _context.DrawPath(
            TransformBrush(brush),
            null,
            geometry,
            CurrentTransform4x4());
    }

    public Region[] MeasureCharacterRanges(string? text, Font font, RectangleF layoutRect, StringFormat? stringFormat) =>
        MeasureCharacterRanges(text.AsSpan(), font, layoutRect, stringFormat);

    public Region[] MeasureCharacterRanges(
        ReadOnlySpan<char> text,
        Font font,
        RectangleF layoutRect,
        StringFormat? stringFormat)
    {
        if (text.IsEmpty)
        {
            return [];
        }

        ArgumentNullException.ThrowIfNull(font);
        if (stringFormat is null)
        {
            return [];
        }

        CharacterRange[] ranges = stringFormat.GetMeasurableCharacterRanges();
        var result = new Region[ranges.Length];
        if (ranges.Length == 0)
        {
            return result;
        }

        FormattedTextLayout formatted = CreateFormattedTextLayout(
            text.ToString(),
            font,
            layoutRect.Size,
            stringFormat);
        StringFormatFlags flags = stringFormat.FormatFlags;
        float offsetX = GetNoWrapAlignmentOffset(
            formatted.Layout.ContentSize.X,
            layoutRect.Width,
            stringFormat.Alignment,
            flags);
        float offsetY = GetRectangleAlignmentOffset(
            formatted.Layout.ContentSize.Y,
            layoutRect.Height,
            stringFormat.LineAlignment);
        bool clipToLayout = (flags & StringFormatFlags.NoClip) == 0;

        for (int index = 0; index < ranges.Length; index++)
        {
            CharacterRange range = ranges[index];
            int first = Math.Clamp(range.First, 0, text.Length);
            int length = Math.Clamp(range.Length, 0, text.Length - first);
            var region = new Region();
            region.MakeEmpty();
            IReadOnlyList<ProGPU.Text.TextBounds> bounds =
                formatted.Layout.GetSelectionRectangles(first, length);
            for (int boundsIndex = 0; boundsIndex < bounds.Count; boundsIndex++)
            {
                ProGPU.Text.TextBounds item = bounds[boundsIndex];
                region.Union(new RectangleF(
                    layoutRect.X + offsetX + item.X,
                    layoutRect.Y + offsetY + item.Y,
                    item.Width,
                    item.Height));
            }

            if (clipToLayout)
            {
                region.Intersect(layoutRect);
            }

            result[index] = region;
        }

        return result;
    }

    public bool IsVisible(Point point) => IsVisible(point.X, point.Y);
    public bool IsVisible(PointF point) => IsVisible(point.X, point.Y);
    public bool IsVisible(int x, int y) => IsVisible((float)x, y);
    public bool IsVisible(float x, float y) => _clip?.IsVisible(x, y, this) ?? VisibleClipBounds.Contains(x, y);
    public bool IsVisible(Rectangle rect) => IsVisible((RectangleF)rect);
    public bool IsVisible(RectangleF rect) => _clip?.IsVisible(rect, this) ?? VisibleClipBounds.IntersectsWith(rect);
    public bool IsVisible(int x, int y, int width, int height) =>
        IsVisible((float)x, y, width, height);
    public bool IsVisible(float x, float y, float width, float height) =>
        IsVisible(new RectangleF(x, y, width, height));

    public void DrawString(string? s, Font font, Brush brush, PointF point) => DrawString(s, font, brush, point.X, point.Y);
    public void DrawString(string? s, Font font, Brush brush, PointF point, StringFormat? format) =>
        DrawString(s, font, brush, point.X, point.Y, format);
    public void DrawString(ReadOnlySpan<char> s, Font font, Brush brush, PointF point) =>
        DrawString(s, font, brush, point.X, point.Y);
    public void DrawString(ReadOnlySpan<char> s, Font font, Brush brush, PointF point, StringFormat? format) =>
        DrawString(s, font, brush, point.X, point.Y, format);

    public void DrawString(string? s, Font font, Brush brush, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (string.IsNullOrEmpty(s))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(font);
        var isBold = (font.Style & FontStyle.Bold) != 0;
        var isItalic = (font.Style & FontStyle.Italic) != 0;
        _context.DrawText(
            s,
            font.TtfFont,
            GetFontPixelSize(font),
            TransformBrush(brush),
            new Vector2(x, y),
            CurrentTransform4x4(),
            isBold,
            isItalic);
        if (font.Underline || font.Strikeout)
        {
            var layout = new ProGPU.Text.TextLayout(s, font.TtfFont, GetFontPixelSize(font));
            DrawFontDecorations(
                layout,
                font,
                brush,
                new Vector2(x, y),
                CurrentTransform4x4());
        }
    }

    public void DrawString(ReadOnlySpan<char> s, Font font, Brush brush, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (s.IsEmpty)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(font);
        DrawString(s.ToString(), font, brush, x, y);
    }

    public void DrawString(string? s, Font font, Brush brush, float x, float y, StringFormat? format)
    {
        if (format == null)
        {
            DrawString(s, font, brush, x, y);
            return;
        }

        ArgumentNullException.ThrowIfNull(brush);
        if (string.IsNullOrEmpty(s))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(font);

        DrawFormattedString(
            s,
            font,
            brush,
            new RectangleF(x, y, float.PositiveInfinity, float.PositiveInfinity),
            format,
            pointAnchor: true);
    }

    public void DrawString(
        ReadOnlySpan<char> s,
        Font font,
        Brush brush,
        float x,
        float y,
        StringFormat? format)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (s.IsEmpty)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(font);
        DrawString(s.ToString(), font, brush, x, y, format);
    }

    internal void DrawStringWithCharacterAdvances(
        string text,
        Font font,
        Brush brush,
        float x,
        float y,
        ReadOnlySpan<int> advances)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(brush);
        if (advances.Length != text.Length)
        {
            throw new ArgumentException("The advance count must match the text length.", nameof(advances));
        }
        if (text.Length == 0)
        {
            return;
        }

        using StringFormat format = StringFormat.GenericTypographic;
        FormattedTextLayout formatted = CreateFormattedTextLayout(
            text,
            font,
            new SizeF(float.PositiveInfinity, float.PositiveInfinity),
            format);
        ProGPU.Text.TextLayout layout = formatted.Layout;
        if (layout.Glyphs.Count == 0)
        {
            return;
        }

        Span<float> desiredOrigins = text.Length <= 256
            ? stackalloc float[text.Length]
            : new float[text.Length];
        int desiredOrigin = 0;
        for (int index = 0; index < advances.Length; index++)
        {
            desiredOrigins[index] = desiredOrigin;
            desiredOrigin = checked(desiredOrigin + advances[index]);
        }

        Span<float> naturalOrigins = text.Length <= 256
            ? stackalloc float[text.Length]
            : new float[text.Length];
        naturalOrigins.Fill(float.NaN);
        IReadOnlyList<ProGPU.Text.TextCaretStop> caretStops = layout.GetVisualCaretStops();
        for (int index = 0; index < caretStops.Count; index++)
        {
            ProGPU.Text.TextCaretStop stop = caretStops[index];
            if (!stop.IsTrailing && (uint)stop.TextPosition < (uint)naturalOrigins.Length)
            {
                naturalOrigins[stop.TextPosition] = stop.Position.X;
            }
        }

        for (int index = 0; index < layout.Glyphs.Count; index++)
        {
            ProGPU.Text.TextRunGlyph glyph = layout.Glyphs[index];
            int cluster = Math.Clamp(glyph.Cluster, 0, text.Length - 1);
            float naturalOrigin = naturalOrigins[cluster];
            if (float.IsNaN(naturalOrigin))
            {
                naturalOrigin = glyph.Position.X;
                naturalOrigins[cluster] = naturalOrigin;
            }
            glyph.Position.X += desiredOrigins[cluster] - naturalOrigin;
            layout.Glyphs[index] = glyph;
        }

        DrawFormattedGlyphRuns(
            layout,
            font,
            brush,
            new Vector2(x, y),
            CurrentTransform4x4());
        DrawFontDecorations(
            layout,
            font,
            brush,
            new Vector2(x, y),
            CurrentTransform4x4());
    }

    internal void DrawStringWithCharacterAdvances(
        string text,
        Font font,
        Brush brush,
        float x,
        float y,
        ReadOnlySpan<Point> advances)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(brush);
        if (advances.Length != text.Length)
        {
            throw new ArgumentException("The advance count must match the text length.", nameof(advances));
        }
        if (text.Length == 0)
        {
            return;
        }

        using StringFormat format = StringFormat.GenericTypographic;
        FormattedTextLayout formatted = CreateFormattedTextLayout(
            text,
            font,
            new SizeF(float.PositiveInfinity, float.PositiveInfinity),
            format);
        ProGPU.Text.TextLayout layout = formatted.Layout;
        if (layout.Glyphs.Count == 0)
        {
            return;
        }

        Span<Vector2> desiredOrigins = text.Length <= 256
            ? stackalloc Vector2[text.Length]
            : new Vector2[text.Length];
        int desiredX = 0;
        int desiredY = 0;
        for (int index = 0; index < advances.Length; index++)
        {
            desiredOrigins[index] = new Vector2(desiredX, desiredY);
            desiredX = checked(desiredX + advances[index].X);
            desiredY = checked(desiredY + advances[index].Y);
        }

        Span<float> naturalOrigins = text.Length <= 256
            ? stackalloc float[text.Length]
            : new float[text.Length];
        naturalOrigins.Fill(float.NaN);
        IReadOnlyList<ProGPU.Text.TextCaretStop> caretStops = layout.GetVisualCaretStops();
        for (int index = 0; index < caretStops.Count; index++)
        {
            ProGPU.Text.TextCaretStop stop = caretStops[index];
            if (!stop.IsTrailing && (uint)stop.TextPosition < (uint)naturalOrigins.Length)
            {
                naturalOrigins[stop.TextPosition] = stop.Position.X;
            }
        }

        for (int index = 0; index < layout.Glyphs.Count; index++)
        {
            ProGPU.Text.TextRunGlyph glyph = layout.Glyphs[index];
            int cluster = Math.Clamp(glyph.Cluster, 0, text.Length - 1);
            float naturalOrigin = naturalOrigins[cluster];
            if (float.IsNaN(naturalOrigin))
            {
                naturalOrigin = glyph.Position.X;
                naturalOrigins[cluster] = naturalOrigin;
            }
            Vector2 desiredOrigin = desiredOrigins[cluster];
            glyph.Position.X += desiredOrigin.X - naturalOrigin;
            glyph.Position.Y += desiredOrigin.Y;
            layout.Glyphs[index] = glyph;
        }

        DrawFormattedGlyphRuns(
            layout,
            font,
            brush,
            new Vector2(x, y),
            CurrentTransform4x4());
    }

    internal void DrawGlyphIndicesWithCharacterAdvances(
        ReadOnlySpan<ushort> glyphIndices,
        Font font,
        Brush brush,
        float x,
        float y,
        ReadOnlySpan<Vector2> advances)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(brush);
        if (glyphIndices.Length != advances.Length)
        {
            throw new ArgumentException(
                "The advance count must match the glyph count.",
                nameof(advances));
        }
        if (glyphIndices.IsEmpty)
        {
            return;
        }

        float fontSize = GetFontPixelSize(font);
        float baseline = font.TtfFont.UnitsPerEm == 0
            ? 0f
            : fontSize * font.TtfFont.Ascender / font.TtfFont.UnitsPerEm;
        Vector2[] positions = new Vector2[glyphIndices.Length];
        Vector2 origin = Vector2.Zero;
        float minimumX = 0f;
        float maximumX = 0f;
        bool hasVerticalAdvance = false;
        for (int index = 0; index < advances.Length; index++)
        {
            positions[index] = new Vector2(origin.X, baseline + origin.Y);
            hasVerticalAdvance |= advances[index].Y != 0f;
            origin += advances[index];
            if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y))
            {
                throw new ArgumentException("Glyph advances must have a finite total.", nameof(advances));
            }
            minimumX = MathF.Min(minimumX, origin.X);
            maximumX = MathF.Max(maximumX, origin.X);
        }

        ProGPU.Vector.Brush nativeBrush = TransformBrush(brush);
        Matrix4x4 transform = CurrentTransform4x4();
        _context.DrawGlyphRun(
            glyphIndices.ToArray(),
            positions,
            font.TtfFont,
            fontSize,
            nativeBrush,
            new Vector2(x, y),
            transform,
            isBold: (font.Style & FontStyle.Bold) != 0,
            isItalic: (font.Style & FontStyle.Italic) != 0,
            preferGlyphAtlas: true);
        if (!hasVerticalAdvance)
        {
            DrawHorizontalGlyphDecorations(
                font,
                nativeBrush,
                new Vector2(x, y),
                transform,
                baseline,
                minimumX,
                maximumX);
        }
    }

    internal void DrawStringWithCharacterSpacing(
        string text,
        Font font,
        Brush brush,
        float x,
        float y,
        ReadOnlySpan<float> characterSpacing)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(brush);
        if (characterSpacing.Length != text.Length)
        {
            throw new ArgumentException(
                "The spacing count must match the text length.",
                nameof(characterSpacing));
        }
        if (text.Length == 0)
        {
            return;
        }

        var layout = new ProGPU.Text.TextLayout(
            text,
            font.TtfFont,
            GetFontPixelSize(font));
        if (layout.Glyphs.Count == 0)
        {
            return;
        }

        int consumedCharacters = 0;
        float offset = 0f;
        for (int index = 0; index < layout.Glyphs.Count; index++)
        {
            ProGPU.Text.TextRunGlyph glyph = layout.Glyphs[index];
            int cluster = Math.Clamp(glyph.Cluster, 0, text.Length - 1);
            if (cluster < consumedCharacters)
            {
                throw new NotSupportedException(
                    "Character-cell spacing requires monotonically ordered text clusters.");
            }
            while (consumedCharacters < cluster)
            {
                float spacing = characterSpacing[consumedCharacters++];
                if (!float.IsFinite(spacing))
                {
                    throw new ArgumentOutOfRangeException(nameof(characterSpacing));
                }
                offset += spacing;
            }
            glyph.Position.X += offset;
            layout.Glyphs[index] = glyph;
        }

        float trailingSpacing = 0f;
        while (consumedCharacters < characterSpacing.Length)
        {
            float spacing = characterSpacing[consumedCharacters++];
            if (!float.IsFinite(spacing))
            {
                throw new ArgumentOutOfRangeException(nameof(characterSpacing));
            }
            trailingSpacing += spacing;
        }
        if (trailingSpacing != 0f)
        {
            int lastIndex = layout.Glyphs.Count - 1;
            ProGPU.Text.TextRunGlyph last = layout.Glyphs[lastIndex];
            last.Glyph.Advance += trailingSpacing;
            layout.Glyphs[lastIndex] = last;
        }

        Vector2 origin = new(x, y);
        Matrix4x4 transform = CurrentTransform4x4();
        DrawFormattedGlyphRuns(layout, font, brush, origin, transform);
        DrawFontDecorations(layout, font, brush, origin, transform);
    }

    public void DrawString(string? s, Font font, Brush brush, RectangleF layoutRectangle)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (string.IsNullOrEmpty(s))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(font);
        if (layoutRectangle.Width <= 0f
            || layoutRectangle.Height <= 0f
            || !float.IsFinite(layoutRectangle.Width)
            || !float.IsFinite(layoutRectangle.Height))
        {
            DrawString(s, font, brush, layoutRectangle.X, layoutRectangle.Y);
            return;
        }

        var isBold = (font.Style & FontStyle.Bold) != 0;
        var isItalic = (font.Style & FontStyle.Italic) != 0;
        var layoutBounds = new Rect(
            layoutRectangle.X,
            layoutRectangle.Y,
            layoutRectangle.Width,
            layoutRectangle.Height);
        var transform = CurrentTransform4x4();

        _context.PushClip(layoutBounds, transform);
        _context.DrawText(
            s,
            font.TtfFont,
            GetFontPixelSize(font),
            TransformBrush(brush),
            new Vector2(layoutRectangle.X, layoutRectangle.Y),
            transform,
            layoutBounds,
            isBold,
            isItalic);
        if (font.Underline || font.Strikeout)
        {
            var layout = new ProGPU.Text.TextLayout(
                s,
                font.TtfFont,
                GetFontPixelSize(font),
                layoutRectangle.Width);
            DrawFontDecorations(
                layout,
                font,
                brush,
                new Vector2(layoutRectangle.X, layoutRectangle.Y),
                transform);
        }
        _context.PopClip();
    }

    public void DrawString(ReadOnlySpan<char> s, Font font, Brush brush, RectangleF layoutRectangle)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (s.IsEmpty)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(font);
        DrawString(s.ToString(), font, brush, layoutRectangle);
    }

    public void DrawString(string? s, Font font, Brush brush, Rectangle layoutRectangle, StringFormat? format) =>
        DrawString(s, font, brush, (RectangleF)layoutRectangle, format);

    public void DrawString(string? s, Font font, Brush brush, RectangleF layoutRectangle, StringFormat? format)
    {
        if (format == null)
        {
            DrawString(s, font, brush, layoutRectangle);
            return;
        }

        ArgumentNullException.ThrowIfNull(brush);
        if (string.IsNullOrEmpty(s))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(font);

        if (layoutRectangle.Width <= 0f
            || layoutRectangle.Height <= 0f
            || !float.IsFinite(layoutRectangle.Width)
            || !float.IsFinite(layoutRectangle.Height))
        {
            DrawString(s, font, brush, layoutRectangle.X, layoutRectangle.Y, format);
            return;
        }

        DrawFormattedString(s, font, brush, layoutRectangle, format, pointAnchor: false);
    }

    public void DrawString(
        ReadOnlySpan<char> s,
        Font font,
        Brush brush,
        RectangleF layoutRectangle,
        StringFormat? format)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (s.IsEmpty)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(font);
        DrawString(s.ToString(), font, brush, layoutRectangle, format);
    }

    public SizeF MeasureString(string? text, Font font)
    {
        if (string.IsNullOrEmpty(text))
        {
            return SizeF.Empty;
        }

        ArgumentNullException.ThrowIfNull(font);
        var layout = new ProGPU.Text.TextLayout(text, font.TtfFont, GetFontPixelSize(font));
        return new SizeF(layout.MeasuredSize.X, layout.MeasuredSize.Y);
    }

    public SizeF MeasureString(ReadOnlySpan<char> text, Font font) =>
        MeasureStringInternal(text, font, RectangleF.Empty, null, out _, out _);

    public SizeF MeasureString(string? text, Font font, SizeF layoutArea)
    {
        if (string.IsNullOrEmpty(text))
        {
            return SizeF.Empty;
        }

        ArgumentNullException.ThrowIfNull(font);
        float maxWidth = layoutArea.Width > 0f && float.IsFinite(layoutArea.Width)
            ? layoutArea.Width
            : float.PositiveInfinity;
        var layout = new ProGPU.Text.TextLayout(
            text,
            font.TtfFont,
            GetFontPixelSize(font),
            maxWidth);

        float measuredWidth = layout.ContentSize.X;
        float measuredHeight = layout.ContentSize.Y;
        if (float.IsFinite(maxWidth))
        {
            measuredWidth = MathF.Min(measuredWidth, maxWidth);
        }

        if (layoutArea.Height > 0f && float.IsFinite(layoutArea.Height))
        {
            measuredHeight = MathF.Min(measuredHeight, layoutArea.Height);
        }

        return new SizeF(measuredWidth, measuredHeight);
    }

    public SizeF MeasureString(ReadOnlySpan<char> text, Font font, SizeF layoutArea) =>
        MeasureStringInternal(text, font, new RectangleF(PointF.Empty, layoutArea), null, out _, out _);

    public SizeF MeasureString(string? text, Font font, SizeF layoutArea, StringFormat? stringFormat)
    {
        return MeasureString(text, font, layoutArea, stringFormat, out _, out _);
    }

    public SizeF MeasureString(
        ReadOnlySpan<char> text,
        Font font,
        SizeF layoutArea,
        StringFormat? stringFormat) =>
        MeasureStringInternal(
            text,
            font,
            new RectangleF(PointF.Empty, layoutArea),
            stringFormat,
            out _,
            out _);

    public SizeF MeasureString(
        string? text,
        Font font,
        SizeF layoutArea,
        StringFormat? stringFormat,
        out int charactersFitted,
        out int linesFilled)
    {
        if (string.IsNullOrEmpty(text))
        {
            charactersFitted = 0;
            linesFilled = 0;
            return SizeF.Empty;
        }

        ArgumentNullException.ThrowIfNull(font);
        if (stringFormat == null)
        {
            SizeF measured = MeasureString(text, font, layoutArea);
            charactersFitted = text?.Length ?? 0;
            linesFilled = CountLines(text);
            return measured;
        }

        FormattedTextLayout formatted = CreateFormattedTextLayout(text, font, layoutArea, stringFormat);
        charactersFitted = formatted.CharactersFitted;
        linesFilled = formatted.LinesFilled;

        float measuredWidth = formatted.Layout.ContentSize.X;
        float measuredHeight = formatted.Layout.ContentSize.Y;
        if (layoutArea.Width > 0f && float.IsFinite(layoutArea.Width))
        {
            measuredWidth = MathF.Min(measuredWidth, layoutArea.Width);
        }

        if (layoutArea.Height > 0f && float.IsFinite(layoutArea.Height))
        {
            measuredHeight = MathF.Min(measuredHeight, layoutArea.Height);
        }

        return new SizeF(measuredWidth, measuredHeight);
    }

    public SizeF MeasureString(
        ReadOnlySpan<char> text,
        Font font,
        SizeF layoutArea,
        StringFormat? stringFormat,
        out int charactersFitted,
        out int linesFilled) =>
        MeasureStringInternal(
            text,
            font,
            new RectangleF(PointF.Empty, layoutArea),
            stringFormat,
            out charactersFitted,
            out linesFilled);

    public SizeF MeasureStringInternal(
        ReadOnlySpan<char> text,
        Font font,
        RectangleF layoutArea,
        StringFormat? stringFormat,
        out int charactersFitted,
        out int linesFilled)
    {
        if (text.IsEmpty)
        {
            charactersFitted = 0;
            linesFilled = 0;
            return SizeF.Empty;
        }

        ArgumentNullException.ThrowIfNull(font);
        return MeasureString(
            text.ToString(),
            font,
            layoutArea.Size,
            stringFormat,
            out charactersFitted,
            out linesFilled);
    }

    public SizeF MeasureString(string? text, Font font, PointF origin, StringFormat? stringFormat) =>
        MeasureStringInternal(text.AsSpan(), font, new RectangleF(origin, SizeF.Empty), stringFormat, out _, out _);

    public SizeF MeasureString(
        ReadOnlySpan<char> text,
        Font font,
        PointF origin,
        StringFormat? stringFormat) =>
        MeasureStringInternal(text, font, new RectangleF(origin, SizeF.Empty), stringFormat, out _, out _);

    public SizeF MeasureString(string? text, Font font, int width)
    {
        return MeasureString(text, font, new SizeF(width, 999999f));
    }

    public SizeF MeasureString(ReadOnlySpan<char> text, Font font, int width) =>
        MeasureString(text, font, new SizeF(width, 999999f));

    public SizeF MeasureString(string? text, Font font, int width, StringFormat? format)
    {
        return MeasureString(text, font, new SizeF(width, 999999f), format);
    }

    public SizeF MeasureString(
        ReadOnlySpan<char> text,
        Font font,
        int width,
        StringFormat? format) =>
        MeasureString(text, font, new SizeF(width, 999999f), format);

    private void DrawFormattedString(
        string text,
        Font font,
        Brush brush,
        RectangleF layoutRectangle,
        StringFormat format,
        bool pointAnchor)
    {
        var layoutArea = new SizeF(layoutRectangle.Width, layoutRectangle.Height);
        FormattedTextLayout formatted = CreateFormattedTextLayout(text, font, layoutArea, format);
        if (formatted.Layout.Glyphs.Count == 0)
        {
            return;
        }

        StringFormatFlags flags = format.FormatFlags;
        bool rightToLeft = (flags & StringFormatFlags.DirectionRightToLeft) != 0;
        float offsetX = pointAnchor
            ? GetPointAlignmentOffset(formatted.Layout.ContentSize.X, format.Alignment, rightToLeft)
            : GetNoWrapAlignmentOffset(formatted.Layout.ContentSize.X, layoutRectangle.Width, format.Alignment, flags);
        float offsetY = pointAnchor
            ? GetPointAlignmentOffset(formatted.Layout.ContentSize.Y, format.LineAlignment, rightToLeft: false)
            : GetRectangleAlignmentOffset(formatted.Layout.ContentSize.Y, layoutRectangle.Height, format.LineAlignment);
        var origin = new Vector2(layoutRectangle.X + offsetX, layoutRectangle.Y + offsetY);
        var transform = CurrentTransform4x4();
        bool useClip = !pointAnchor && (flags & StringFormatFlags.NoClip) == 0;
        if (useClip)
        {
            _context.PushClip(
                new Rect(layoutRectangle.X, layoutRectangle.Y, layoutRectangle.Width, layoutRectangle.Height),
                transform);
        }

        DrawFormattedGlyphRuns(formatted.Layout, font, brush, origin, transform);
        DrawFontDecorations(formatted.Layout, font, brush, origin, transform);
        if (formatted.MnemonicIndex >= 0)
        {
            DrawMnemonicUnderline(formatted.Layout, formatted.MnemonicIndex, brush, origin, transform);
        }

        if (useClip)
        {
            _context.PopClip();
        }
    }

    private void DrawMnemonicUnderline(
        ProGPU.Text.TextLayout layout,
        int mnemonicIndex,
        Brush brush,
        Vector2 origin,
        Matrix4x4 transform)
    {
        float left = float.PositiveInfinity;
        float right = float.NegativeInfinity;
        float baseline = 0f;
        ProGPU.Text.TtfFont? mnemonicFont = null;

        for (int index = 0; index < layout.Glyphs.Count; index++)
        {
            ProGPU.Text.TextRunGlyph glyph = layout.Glyphs[index];
            if (glyph.Cluster != mnemonicIndex)
            {
                continue;
            }

            left = MathF.Min(left, glyph.Position.X);
            right = MathF.Max(
                right,
                glyph.Position.X + MathF.Max(MathF.Abs(glyph.Glyph.Advance), glyph.Glyph.Width));
            baseline = glyph.Position.Y;
            mnemonicFont ??= glyph.Font;
        }

        if (mnemonicFont is null || mnemonicFont.UnitsPerEm == 0 || !(right > left))
        {
            return;
        }

        float scale = MathF.Abs(layout.FontSize) / mnemonicFont.UnitsPerEm;
        float thickness = MathF.Max(1f, MathF.Abs(mnemonicFont.UnderlineThickness ?? 0) * scale);
        float position = mnemonicFont.UnderlinePosition ?? (short)(-mnemonicFont.UnitsPerEm / 10);
        _context.DrawRectangle(
            TransformBrush(brush),
            null,
            new Rect(
                origin.X + left,
                origin.Y + baseline - (position * scale),
                right - left,
                thickness),
            transform);
    }

    private void DrawFormattedGlyphRuns(
        ProGPU.Text.TextLayout layout,
        Font font,
        Brush brush,
        Vector2 origin,
        Matrix4x4 transform)
    {
        var isBold = (font.Style & FontStyle.Bold) != 0;
        var isItalic = (font.Style & FontStyle.Italic) != 0;
        var nativeBrush = TransformBrush(brush);
        GlyphRunBuilder? run = null;

        for (int i = 0; i < layout.Glyphs.Count; i++)
        {
            ProGPU.Text.TextRunGlyph glyph = layout.Glyphs[i];
            if (run == null || !ReferenceEquals(run.Font, glyph.Font))
            {
                if (run != null)
                {
                    RecordGlyphRun(run);
                }

                run = new GlyphRunBuilder(glyph.Font);
            }

            run.GlyphIndices.Add(glyph.GlyphIndex);
            run.GlyphPositions.Add(glyph.Position);
        }

        if (run != null)
        {
            RecordGlyphRun(run);
        }

        void RecordGlyphRun(GlyphRunBuilder glyphRun)
        {
            _context.DrawGlyphRun(
                glyphRun.GlyphIndices.ToArray(),
                glyphRun.GlyphPositions.ToArray(),
                glyphRun.Font,
                GetFontPixelSize(font),
                nativeBrush,
                origin,
                transform,
                isBold,
                isItalic);
        }
    }

    private void DrawFontDecorations(
        ProGPU.Text.TextLayout layout,
        Font font,
        Brush brush,
        Vector2 origin,
        Matrix4x4 transform)
    {
        if ((!font.Underline && !font.Strikeout) || layout.Glyphs.Count == 0 ||
            font.TtfFont.UnitsPerEm <= 0)
        {
            return;
        }

        float scale = MathF.Abs(layout.FontSize) / font.TtfFont.UnitsPerEm;
        float underlineThickness = MathF.Max(
            1f,
            MathF.Abs(font.TtfFont.UnderlineThickness ?? 0) * scale);
        float underlinePosition = font.TtfFont.UnderlinePosition ??
            (short)(-font.TtfFont.UnitsPerEm / 10);
        float strikeoutThickness = MathF.Max(
            1f,
            MathF.Abs(font.TtfFont.StrikeoutThickness ?? 0) * scale);
        float strikeoutPosition = font.TtfFont.StrikeoutPosition ??
            (short)(font.TtfFont.UnitsPerEm / 3);
        float lineTolerance = MathF.Max(0.01f, MathF.Abs(layout.FontSize) * 0.75f);
        ProGPU.Vector.Brush nativeBrush = TransformBrush(brush);

        int lineStart = 0;
        while (lineStart < layout.Glyphs.Count)
        {
            ProGPU.Text.TextRunGlyph first = layout.Glyphs[lineStart];
            float baseline = first.Position.Y;
            float left = first.Position.X;
            float right = first.Position.X + MathF.Abs(first.Glyph.Advance);
            int lineEnd = lineStart + 1;
            while (lineEnd < layout.Glyphs.Count &&
                   MathF.Abs(layout.Glyphs[lineEnd].Position.Y - baseline) < lineTolerance)
            {
                ProGPU.Text.TextRunGlyph glyph = layout.Glyphs[lineEnd++];
                left = MathF.Min(left, glyph.Position.X);
                right = MathF.Max(right, glyph.Position.X + MathF.Abs(glyph.Glyph.Advance));
            }

            if (right > left)
            {
                if (font.Underline)
                {
                    _context.DrawRectangle(
                        nativeBrush,
                        null,
                        new Rect(
                            origin.X + left,
                            origin.Y + baseline - underlinePosition * scale,
                            right - left,
                            underlineThickness),
                        transform);
                }
                if (font.Strikeout)
                {
                    _context.DrawRectangle(
                        nativeBrush,
                        null,
                        new Rect(
                            origin.X + left,
                            origin.Y + baseline - strikeoutPosition * scale,
                            right - left,
                            strikeoutThickness),
                        transform);
                }
            }
            lineStart = lineEnd;
        }
    }

    private void DrawHorizontalGlyphDecorations(
        Font font,
        ProGPU.Vector.Brush brush,
        Vector2 origin,
        Matrix4x4 transform,
        float baseline,
        float left,
        float right)
    {
        if ((!font.Underline && !font.Strikeout) || right <= left ||
            font.TtfFont.UnitsPerEm <= 0)
        {
            return;
        }

        float fontSize = GetFontPixelSize(font);
        float scale = MathF.Abs(fontSize) / font.TtfFont.UnitsPerEm;
        float underlineThickness = MathF.Max(
            1f,
            MathF.Abs(font.TtfFont.UnderlineThickness ?? 0) * scale);
        float underlinePosition = font.TtfFont.UnderlinePosition ??
            (short)(-font.TtfFont.UnitsPerEm / 10);
        float strikeoutThickness = MathF.Max(
            1f,
            MathF.Abs(font.TtfFont.StrikeoutThickness ?? 0) * scale);
        float strikeoutPosition = font.TtfFont.StrikeoutPosition ??
            (short)(font.TtfFont.UnitsPerEm / 3);
        if (font.Underline)
        {
            _context.DrawRectangle(
                brush,
                null,
                new Rect(
                    origin.X + left,
                    origin.Y + baseline - underlinePosition * scale,
                    right - left,
                    underlineThickness),
                transform);
        }
        if (font.Strikeout)
        {
            _context.DrawRectangle(
                brush,
                null,
                new Rect(
                    origin.X + left,
                    origin.Y + baseline - strikeoutPosition * scale,
                    right - left,
                    strikeoutThickness),
                transform);
        }
    }

    private FormattedTextLayout CreateFormattedTextLayout(
        string text,
        Font font,
        SizeF layoutArea,
        StringFormat format)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(format);
        format.EnsureNotDisposed();

        StringFormatFlags flags = format.FormatFlags;
        HotkeyText hotkeyText = ApplyHotkeyPrefix(text, format.HotkeyPrefix);
        string displayText = ApplyDigitSubstitution(
            hotkeyText.Text,
            format,
            out CultureInfo? digitCulture);
        float maxWidth = layoutArea.Width > 0f && float.IsFinite(layoutArea.Width)
            ? layoutArea.Width
            : float.PositiveInfinity;
        float maxHeight = layoutArea.Height > 0f && float.IsFinite(layoutArea.Height)
            ? layoutArea.Height
            : float.PositiveInfinity;
        bool noWrap = (flags & StringFormatFlags.NoWrap) != 0;
        ProGPU.Text.TextAlignment alignment = noWrap
            ? ProGPU.Text.TextAlignment.Left
            : GetTextAlignment(format.Alignment, flags);
        float textLayoutWidth = noWrap ? float.PositiveInfinity : maxWidth;
        float fontSize = GetFontPixelSize(font);
        var shapingOptions = new ProGPU.Text.TextShapingOptions
        {
            Direction = (flags & StringFormatFlags.DirectionVertical) != 0
                ? ProGPU.Text.Shaping.ShapingDirection.TopToBottom
                : (flags & StringFormatFlags.DirectionRightToLeft) != 0
                    ? ProGPU.Text.Shaping.ShapingDirection.RightToLeft
                    : ProGPU.Text.Shaping.ShapingDirection.LeftToRight,
            Language = digitCulture?.Name,
            BufferFlags = (flags & StringFormatFlags.DisplayFormatControl) != 0
                ? ProGPU.Text.Shaping.ShapingBufferFlags.PreserveDefaultIgnorables
                : ProGPU.Text.Shaping.ShapingBufferFlags.None
        };
        float[] tabStops = format.GetTabStops(out float firstTabOffset);
        var formattingOptions = new ProGPU.Text.TextLayoutFormattingOptions
        {
            EnableFontFallback = (flags & StringFormatFlags.NoFontFallback) == 0,
            MeasureTrailingWhitespace = (flags & StringFormatFlags.MeasureTrailingSpaces) != 0,
            FirstTabOffset = firstTabOffset,
            TabStops = tabStops
        };
        var layout = new ProGPU.Text.TextLayout(
            displayText,
            font.TtfFont,
            fontSize,
            textLayoutWidth,
            alignment,
            atlas: null,
            shapingOptions,
            formattingOptions);

        bool lineLimit = (flags & StringFormatFlags.LineLimit) != 0;
        bool clipToLayout = (flags & StringFormatFlags.NoClip) == 0;
        float layoutHeightLimit = maxHeight;
        if (!lineLimit && clipToLayout && float.IsFinite(maxHeight))
        {
            float lineHeight = GetLineHeight(font, fontSize);
            if (lineHeight > 0f)
            {
                layoutHeightLimit = MathF.Ceiling((maxHeight / lineHeight) - 0.0001f) * lineHeight;
            }
        }

        bool exceedsWidth = float.IsFinite(maxWidth) && layout.ContentSize.X > maxWidth + 0.001f;
        bool exceedsHeight = float.IsFinite(layoutHeightLimit)
            && layout.ContentSize.Y > layoutHeightLimit + 0.001f;
        int charactersFitted = text.Length;
        int mnemonicIndex = hotkeyText.MnemonicIndex;

        if ((exceedsWidth || exceedsHeight)
            && (format.Trimming != StringTrimming.None || lineLimit || (clipToLayout && exceedsHeight)))
        {
            StringTrimming trimming = format.Trimming == StringTrimming.None
                ? StringTrimming.Character
                : format.Trimming;
            displayText = TrimTextToLayout(
                displayText,
                font,
                fontSize,
                maxWidth,
                layoutHeightLimit,
                noWrap,
                alignment,
                trimming,
                shapingOptions,
                formattingOptions,
                mnemonicIndex,
                out charactersFitted,
                out mnemonicIndex);
            layout = new ProGPU.Text.TextLayout(
                displayText,
                font.TtfFont,
                fontSize,
                textLayoutWidth,
                alignment,
                atlas: null,
                shapingOptions,
                formattingOptions);
        }

        int linesFilled = GetLineCount(layout, font, fontSize);
        return new FormattedTextLayout(
            layout,
            Math.Min(charactersFitted, text.Length),
            linesFilled,
            mnemonicIndex);
    }

    private static string TrimTextToLayout(
        string text,
        Font font,
        float fontSize,
        float maxWidth,
        float maxHeight,
        bool noWrap,
        ProGPU.Text.TextAlignment alignment,
        StringTrimming trimming,
        ProGPU.Text.TextShapingOptions shapingOptions,
        ProGPU.Text.TextLayoutFormattingOptions formattingOptions,
        int mnemonicIndex,
        out int charactersFitted,
        out int mappedMnemonicIndex)
    {
        string suffix = trimming is StringTrimming.EllipsisCharacter
            or StringTrimming.EllipsisWord
            or StringTrimming.EllipsisPath
                ? "\u2026"
                : string.Empty;
        if (!Fits(text, suffix: string.Empty))
        {
            if (trimming == StringTrimming.EllipsisPath
                && TryTrimPath(
                    out string pathText,
                    out charactersFitted,
                    out mappedMnemonicIndex))
            {
                return pathText;
            }

            var prefixLengths = new List<int>(text.Length + 1) { 0 };
            for (int textIndex = 0; textIndex < text.Length; textIndex++)
            {
                if (char.IsHighSurrogate(text[textIndex])
                    && textIndex + 1 < text.Length
                    && char.IsLowSurrogate(text[textIndex + 1]))
                {
                    textIndex++;
                }

                prefixLengths.Add(textIndex + 1);
            }

            int low = 0;
            int high = prefixLengths.Count - 1;
            int best = 0;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                int prefixLength = prefixLengths[middle];
                if (Fits(text[..prefixLength], suffix))
                {
                    best = prefixLength;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (trimming is StringTrimming.Word or StringTrimming.EllipsisWord)
            {
                best = FindWordBoundary(text, best);
            }

            string prefix = text[..best].TrimEnd();
            while (prefix.Length > 0 && !Fits(prefix, suffix))
            {
                int nextLength = NormalizePrefixLength(prefix, prefix.Length - 1);
                prefix = prefix[..nextLength].TrimEnd();
            }

            charactersFitted = prefix.Length;
            mappedMnemonicIndex = mnemonicIndex >= 0 && mnemonicIndex < charactersFitted
                ? mnemonicIndex
                : -1;
            return Fits(prefix, suffix) ? prefix + suffix : string.Empty;
        }

        charactersFitted = text.Length;
        mappedMnemonicIndex = mnemonicIndex;
        return text;

        bool TryTrimPath(
            out string result,
            out int pathCharactersFitted,
            out int pathMnemonicIndex)
        {
            result = string.Empty;
            pathCharactersFitted = 0;
            pathMnemonicIndex = -1;
            int separatorIndex = Math.Max(text.LastIndexOf('/'), text.LastIndexOf('\\'));
            if (separatorIndex < 0 || separatorIndex == text.Length - 1)
            {
                return false;
            }

            const string Ellipsis = "\u2026";
            if (!Fits(string.Empty, Ellipsis))
            {
                return true;
            }

            var suffixStarts = new List<int>(text.Length - separatorIndex + 1);
            for (int index = separatorIndex; index < text.Length; index++)
            {
                suffixStarts.Add(index);
                if (char.IsHighSurrogate(text[index])
                    && index + 1 < text.Length
                    && char.IsLowSurrogate(text[index + 1]))
                {
                    index++;
                }
            }

            suffixStarts.Add(text.Length);
            int low = 0;
            int high = suffixStarts.Count - 1;
            int retainedSuffixStart = text.Length;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                int candidateStart = suffixStarts[middle];
                if (Fits(string.Empty, Ellipsis + text[candidateStart..]))
                {
                    retainedSuffixStart = candidateStart;
                    high = middle - 1;
                }
                else
                {
                    low = middle + 1;
                }
            }

            string retainedSuffix = text[retainedSuffixStart..];
            string retainedTail = Ellipsis + retainedSuffix;
            var prefixLengths = new List<int>(separatorIndex + 1) { 0 };
            for (int index = 0; index < separatorIndex; index++)
            {
                if (char.IsHighSurrogate(text[index])
                    && index + 1 < separatorIndex
                    && char.IsLowSurrogate(text[index + 1]))
                {
                    index++;
                }

                prefixLengths.Add(index + 1);
            }

            low = 0;
            high = prefixLengths.Count - 1;
            int retainedPrefixLength = 0;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                int candidateLength = prefixLengths[middle];
                if (Fits(text[..candidateLength], retainedTail))
                {
                    retainedPrefixLength = candidateLength;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            pathCharactersFitted = retainedPrefixLength + text.Length - retainedSuffixStart;
            pathMnemonicIndex = mnemonicIndex switch
            {
                >= 0 when mnemonicIndex < retainedPrefixLength => mnemonicIndex,
                >= 0 when mnemonicIndex >= retainedSuffixStart =>
                    retainedPrefixLength + Ellipsis.Length + mnemonicIndex - retainedSuffixStart,
                _ => -1
            };
            result = text[..retainedPrefixLength] + retainedTail;
            return true;
        }

        bool Fits(string prefix, string suffix)
        {
            string candidate = prefix + suffix;
            var candidateLayout = new ProGPU.Text.TextLayout(
                candidate,
                font.TtfFont,
                fontSize,
                noWrap ? float.PositiveInfinity : maxWidth,
                alignment,
                atlas: null,
                shapingOptions,
                formattingOptions);
            bool widthFits = !float.IsFinite(maxWidth) || candidateLayout.ContentSize.X <= maxWidth + 0.001f;
            bool heightFits = !float.IsFinite(maxHeight) || candidateLayout.ContentSize.Y <= maxHeight + 0.001f;
            return widthFits && heightFits;
        }
    }

    private static int NormalizePrefixLength(string text, int length)
    {
        length = Math.Clamp(length, 0, text.Length);
        if (length > 0
            && length < text.Length
            && char.IsHighSurrogate(text[length - 1])
            && char.IsLowSurrogate(text[length]))
        {
            length--;
        }

        return length;
    }

    private static int FindWordBoundary(string text, int length)
    {
        length = NormalizePrefixLength(text, length);
        while (length > 0 && char.IsWhiteSpace(text[length - 1]))
        {
            length--;
        }

        if (length == text.Length || (length < text.Length && char.IsWhiteSpace(text[length])))
        {
            return length;
        }

        int boundary = length;
        while (boundary > 0 && !char.IsWhiteSpace(text[boundary - 1]))
        {
            boundary--;
        }

        return boundary == 0 ? length : boundary;
    }

    private static HotkeyText ApplyHotkeyPrefix(string text, HotkeyPrefix hotkeyPrefix)
    {
        if (hotkeyPrefix == HotkeyPrefix.None || text.IndexOf('&') < 0)
        {
            return new HotkeyText(text, -1);
        }

        var builder = new StringBuilder(text.Length);
        int mnemonicIndex = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '&')
            {
                builder.Append(text[i]);
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '&')
            {
                builder.Append('&');
                i++;
            }
            else if (hotkeyPrefix == HotkeyPrefix.Show && mnemonicIndex < 0 && i + 1 < text.Length)
            {
                mnemonicIndex = builder.Length;
            }
        }

        return new HotkeyText(builder.ToString(), mnemonicIndex);
    }

    private static string ApplyDigitSubstitution(
        string text,
        StringFormat format,
        out CultureInfo? digitCulture)
    {
        digitCulture = null;
        StringDigitSubstitute method = format.DigitSubstitutionMethod;
        if (method == StringDigitSubstitute.None || text.AsSpan().IndexOfAny("0123456789") < 0)
        {
            return text;
        }

        int language = format.DigitSubstitutionLanguage;
        try
        {
            digitCulture = language == 0
                ? CultureInfo.CurrentCulture
                : CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return text;
        }

        string[] digits = GetSubstitutionDigits(digitCulture);
        bool hasNativeDigits = false;
        for (int index = 0; index < digits.Length; index++)
        {
            if (digits[index].Length == 1 && digits[index][0] != (char)('0' + index))
            {
                hasNativeDigits = true;
                break;
            }
        }

        if (!hasNativeDigits)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        bool substituted = false;
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (character is >= '0' and <= '9')
            {
                string digit = digits[character - '0'];
                if (digit.Length == 1 && digit[0] != character)
                {
                    builder.Append(digit);
                    substituted = true;
                    continue;
                }
            }

            builder.Append(character);
        }

        return substituted ? builder.ToString() : text;
    }

    private static string[] GetSubstitutionDigits(CultureInfo culture)
    {
        string[] nativeDigits = culture.NumberFormat.NativeDigits;
        for (int index = 0; index < nativeDigits.Length; index++)
        {
            if (nativeDigits[index].Length == 1 && nativeDigits[index][0] != (char)('0' + index))
            {
                return nativeDigits;
            }
        }

        string? digits = culture.TwoLetterISOLanguageName switch
        {
            "ar" => "٠١٢٣٤٥٦٧٨٩",
            "fa" or "ur" => "۰۱۲۳۴۵۶۷۸۹",
            "th" => "๐๑๒๓๔๕๖๗๘๙",
            "bn" => "০১২৩৪৫৬৭৮৯",
            "hi" or "mr" or "ne" => "०१२३४५६७८९",
            _ => null
        };
        return digits is null
            ? nativeDigits
            : digits.Select(static digit => digit.ToString()).ToArray();
    }

    private static ProGPU.Text.TextAlignment GetTextAlignment(
        StringAlignment alignment,
        StringFormatFlags flags)
    {
        if ((flags & StringFormatFlags.DirectionRightToLeft) != 0)
        {
            alignment = alignment switch
            {
                StringAlignment.Near => StringAlignment.Far,
                StringAlignment.Far => StringAlignment.Near,
                _ => alignment
            };
        }

        return alignment switch
        {
            StringAlignment.Center => ProGPU.Text.TextAlignment.Center,
            StringAlignment.Far => ProGPU.Text.TextAlignment.Right,
            _ => ProGPU.Text.TextAlignment.Left
        };
    }

    private static float GetNoWrapAlignmentOffset(
        float contentSize,
        float availableSize,
        StringAlignment alignment,
        StringFormatFlags flags)
    {
        if ((flags & StringFormatFlags.NoWrap) == 0)
        {
            return 0f;
        }

        bool rightToLeft = (flags & StringFormatFlags.DirectionRightToLeft) != 0;
        return GetRectangleAlignmentOffset(contentSize, availableSize, SwapNearAndFar(alignment, rightToLeft));
    }

    private static float GetPointAlignmentOffset(float contentSize, StringAlignment alignment, bool rightToLeft)
    {
        alignment = SwapNearAndFar(alignment, rightToLeft);
        return alignment switch
        {
            StringAlignment.Center => -contentSize / 2f,
            StringAlignment.Far => -contentSize,
            _ => 0f
        };
    }

    private static float GetRectangleAlignmentOffset(
        float contentSize,
        float availableSize,
        StringAlignment alignment)
    {
        float remaining = MathF.Max(0f, availableSize - contentSize);
        return alignment switch
        {
            StringAlignment.Center => remaining / 2f,
            StringAlignment.Far => remaining,
            _ => 0f
        };
    }

    private static StringAlignment SwapNearAndFar(StringAlignment alignment, bool swap)
    {
        if (!swap)
        {
            return alignment;
        }

        return alignment switch
        {
            StringAlignment.Near => StringAlignment.Far,
            StringAlignment.Far => StringAlignment.Near,
            _ => alignment
        };
    }

    private static int GetLineCount(ProGPU.Text.TextLayout layout, Font font, float fontSize)
    {
        if (layout.Text.Length == 0)
        {
            return 0;
        }

        float lineHeight = GetLineHeight(font, fontSize);
        return lineHeight > 0f
            ? Math.Max(1, (int)MathF.Round(layout.ContentSize.Y / lineHeight))
            : CountLines(layout.Text);
    }

    private static float GetLineHeight(Font font, float fontSize)
    {
        ProGPU.Text.TtfFont face = font.TtfFont;
        return face.UnitsPerEm == 0
            ? 0f
            : (face.Ascender - face.Descender + face.LineGap) * (fontSize / face.UnitsPerEm);
    }

    private static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int lines = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private readonly record struct FormattedTextLayout(
        ProGPU.Text.TextLayout Layout,
        int CharactersFitted,
        int LinesFilled,
        int MnemonicIndex);

    private readonly record struct HotkeyText(string Text, int MnemonicIndex);

    private sealed class GlyphRunBuilder
    {
        public GlyphRunBuilder(ProGPU.Text.TtfFont font)
        {
            Font = font;
        }

        public ProGPU.Text.TtfFont Font { get; }
        public List<ushort> GlyphIndices { get; } = [];
        public List<Vector2> GlyphPositions { get; } = [];
    }

    private float GetFontPixelSize(Font font)
    {
        return ConvertFontSizeToPixels(font.Size, font.Unit, DpiY);
    }

    internal static float ConvertFontSizeToPixels(float size, GraphicsUnit unit, float dpi)
    {
        return unit switch
        {
            GraphicsUnit.Point => size * dpi / 72f,
            GraphicsUnit.Inch => size * dpi,
            GraphicsUnit.Document => size * dpi / 300f,
            GraphicsUnit.Millimeter => size * dpi / 25.4f,
            _ => size
        };
    }

    internal static float ConvertFontSizeToPoints(float size, GraphicsUnit unit, float dpi)
    {
        return unit switch
        {
            GraphicsUnit.Pixel or GraphicsUnit.Display or GraphicsUnit.World => size * 72f / dpi,
            GraphicsUnit.Inch => size * 72f,
            GraphicsUnit.Document => size * 72f / 300f,
            GraphicsUnit.Millimeter => size * 72f / 25.4f,
            _ => size
        };
    }

    public void DrawImage(Image image, PointF point) => DrawImage(image, point.X, point.Y);
    public void DrawImage(Image image, Point point) => DrawImage(image, point.X, point.Y);
    public void DrawImage(Image image, int x, int y) => DrawImage(image, (float)x, y);
    public void DrawImage(Image image, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image is Bitmap bmp)
        {
            DrawBitmap(bmp, new RectangleF(x, y, bmp.Width, bmp.Height));
        }
        else if (image is Metafile metafile)
        {
            DrawMetafile(metafile, new RectangleF(x, y, metafile.Width, metafile.Height));
        }
    }

    public void DrawImage(Image image, int x, int y, int width, int height)
    {
        DrawImage(image, new Rectangle(x, y, width, height));
    }

    public void DrawImage(Image image, float x, float y, float width, float height)
    {
        DrawImage(image, new RectangleF(x, y, width, height));
    }

    public void DrawImage(Image image, RectangleF rect)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image is Bitmap bmp)
        {
            DrawBitmap(bmp, rect, default, null);
        }
        else if (image is Metafile metafile)
        {
            DrawMetafile(metafile, rect);
        }
    }

    public void DrawImage(Image image, Rectangle rect) => DrawImage(image, (RectangleF)rect);

    /// <summary>Draws a device-dependent cached bitmap at the given location.</summary>
    public void DrawCachedBitmap(CachedBitmap cachedBitmap, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(cachedBitmap);
        ThrowIfDisposed();

        Matrix3x2 cacheTransform =
            _transform.Value * GetPageTransform() * _containerTransform;
        if (!IsTranslationOnly(cacheTransform))
        {
            throw new InvalidOperationException(
                "CachedBitmap drawing supports translation-only Graphics transforms.");
        }

        WgpuContext targetContext = GetTargetContextForCachedBitmap();
        Bitmap snapshot = cachedBitmap.GetSnapshotForDraw(targetContext);
        if (!_context.TryRetainTexture(snapshot, targetContext, out GpuTexture retainedTexture))
        {
            throw new ArgumentException("Parameter is not valid.", nameof(cachedBitmap));
        }

        _context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Texture = retainedTexture,
            Rect = new Rect(x, y, snapshot.Width, snapshot.Height),
            SrcRect = Rect.Empty,
            Transform = CurrentTransform4x4(),
            TextureSamplingMode = TextureSamplingMode.Nearest
        });
    }

    private static bool IsTranslationOnly(Matrix3x2 transform)
    {
        const float epsilon = 1e-5f;
        return float.IsFinite(transform.M31)
            && float.IsFinite(transform.M32)
            && MathF.Abs(transform.M11 - 1f) <= epsilon
            && MathF.Abs(transform.M12) <= epsilon
            && MathF.Abs(transform.M21) <= epsilon
            && MathF.Abs(transform.M22 - 1f) <= epsilon;
    }

    /// <inheritdoc cref="DrawImage(Image, Effect, RectangleF, Matrix?, GraphicsUnit, ImageAttributes?)"/>
    public void DrawImage(Image image, Effect effect) =>
        DrawImage(image, effect, srcRect: default, transform: default, GraphicsUnit.Pixel, imageAttr: null);

    /// <summary>Draws a portion of an image after applying a specified effect.</summary>
    public void DrawImage(
        Image image,
        Effect effect,
        RectangleF srcRect = default,
        Matrix? transform = default,
        GraphicsUnit srcUnit = GraphicsUnit.Pixel,
        ImageAttributes? imageAttr = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(effect);
        ThrowIfDisposed();

        if (image is not Bitmap bitmap)
        {
            throw new NotSupportedException("Effect drawing currently requires bitmap-backed image storage.");
        }

        RectangleF sourcePixels = srcRect.IsEmpty
            ? new RectangleF(0f, 0f, bitmap.Width, bitmap.Height)
            : ConvertSourceRect(srcRect, srcUnit);
        ValidateEffectSourceRectangle(sourcePixels, bitmap.Width, bitmap.Height);

        Matrix3x2 effectTransform = transform?.Value ?? Matrix3x2.Identity;
        Vector2 topLeft = Vector2.Transform(new Vector2(sourcePixels.Left, sourcePixels.Top), effectTransform);
        Vector2 topRight = Vector2.Transform(new Vector2(sourcePixels.Right, sourcePixels.Top), effectTransform);
        Vector2 bottomLeft = Vector2.Transform(new Vector2(sourcePixels.Left, sourcePixels.Bottom), effectTransform);
        Vector2 bottomRight = Vector2.Transform(new Vector2(sourcePixels.Right, sourcePixels.Bottom), effectTransform);
        _ = CreateProjectiveWeights(topLeft, topRight, bottomRight, bottomLeft, isPerspective: false);

        using var effectedBitmap = new Bitmap(bitmap);
        effectedBitmap.ApplyEffect(effect, Rectangle.FromLTRB(
            checked((int)MathF.Floor(sourcePixels.Left)),
            checked((int)MathF.Floor(sourcePixels.Top)),
            checked((int)MathF.Ceiling(sourcePixels.Right)),
            checked((int)MathF.Ceiling(sourcePixels.Bottom))));
        DrawMappedBitmap(
            effectedBitmap,
            topLeft,
            topRight,
            bottomRight,
            bottomLeft,
            Vector4.One,
            sourcePixels,
            imageAttr);
    }

    private static void ValidateEffectSourceRectangle(RectangleF rectangle, int imageWidth, int imageHeight)
    {
        if (!float.IsFinite(rectangle.X) || !float.IsFinite(rectangle.Y) ||
            !float.IsFinite(rectangle.Width) || !float.IsFinite(rectangle.Height) ||
            rectangle.Width <= 0f || rectangle.Height <= 0f)
        {
            throw new ArgumentException("The source rectangle must be finite and non-empty.", "srcRect");
        }

        if (rectangle.Left < 0f || rectangle.Top < 0f ||
            rectangle.Right > imageWidth || rectangle.Bottom > imageHeight)
        {
            throw new ArgumentException("The source rectangle must be contained within the image.", "srcRect");
        }
    }

    public void DrawImage(Image image, PointF[] destPoints)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateDestinationPoints(destPoints);
        DrawImageMapped(
            image,
            destPoints[0],
            destPoints[1],
            destPoints[2],
            destPoints.Length == 4 ? destPoints[3] : null,
            GetNaturalImageSourceRectangle(image),
            GraphicsUnit.Pixel,
            null,
            null,
            0);
    }

    public void DrawImage(Image image, Point[] destPoints)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateDestinationPoints(destPoints);
        DrawImageMapped(
            image,
            destPoints[0],
            destPoints[1],
            destPoints[2],
            destPoints.Length == 4 ? destPoints[3] : null,
            GetNaturalImageSourceRectangle(image),
            GraphicsUnit.Pixel,
            null,
            null,
            0);
    }

    public void DrawImage(
        Image image,
        PointF[] destPoints,
        RectangleF srcRect,
        GraphicsUnit srcUnit) =>
        DrawImage(image, destPoints, srcRect, srcUnit, null, null, 0);

    public void DrawImage(
        Image image,
        PointF[] destPoints,
        RectangleF srcRect,
        GraphicsUnit srcUnit,
        ImageAttributes? imageAttr) =>
        DrawImage(image, destPoints, srcRect, srcUnit, imageAttr, null, 0);

    public void DrawImage(
        Image image,
        PointF[] destPoints,
        RectangleF srcRect,
        GraphicsUnit srcUnit,
        ImageAttributes? imageAttr,
        DrawImageAbort? callback) =>
        DrawImage(image, destPoints, srcRect, srcUnit, imageAttr, callback, 0);

    public void DrawImage(
        Image image,
        PointF[] destPoints,
        RectangleF srcRect,
        GraphicsUnit srcUnit,
        ImageAttributes? imageAttr,
        DrawImageAbort? callback,
        int callbackData)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateDestinationPoints(destPoints);
        DrawImageMapped(
            image,
            destPoints[0],
            destPoints[1],
            destPoints[2],
            destPoints.Length == 4 ? destPoints[3] : null,
            srcRect,
            srcUnit,
            imageAttr,
            callback,
            callbackData);
    }

    public void DrawImage(
        Image image,
        Point[] destPoints,
        Rectangle srcRect,
        GraphicsUnit srcUnit) =>
        DrawImage(image, destPoints, srcRect, srcUnit, null, null, 0);

    public void DrawImage(
        Image image,
        Point[] destPoints,
        Rectangle srcRect,
        GraphicsUnit srcUnit,
        ImageAttributes? imageAttr) =>
        DrawImage(image, destPoints, srcRect, srcUnit, imageAttr, null, 0);

    public void DrawImage(
        Image image,
        Point[] destPoints,
        Rectangle srcRect,
        GraphicsUnit srcUnit,
        ImageAttributes? imageAttr,
        DrawImageAbort? callback) =>
        DrawImage(image, destPoints, srcRect, srcUnit, imageAttr, callback, 0);

    public void DrawImage(
        Image image,
        Point[] destPoints,
        Rectangle srcRect,
        GraphicsUnit srcUnit,
        ImageAttributes? imageAttr,
        DrawImageAbort? callback,
        int callbackData)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateDestinationPoints(destPoints);
        DrawImageMapped(
            image,
            destPoints[0],
            destPoints[1],
            destPoints[2],
            destPoints.Length == 4 ? destPoints[3] : null,
            srcRect,
            srcUnit,
            imageAttr,
            callback,
            callbackData);
    }

    public void DrawImage(Image image, Rectangle destRect, Rectangle srcRect, GraphicsUnit srcUnit)
    {
        DrawImage(image, (RectangleF)destRect, (RectangleF)srcRect, srcUnit);
    }

    public void DrawImage(Image image, RectangleF destRect, RectangleF srcRect, GraphicsUnit srcUnit)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image is Bitmap bmp)
        {
            DrawBitmap(bmp, destRect, ConvertSourceRect(srcRect, srcUnit), null);
        }
        else if (image is Metafile metafile)
        {
            RectangleF source = ConvertSourceRect(srcRect, srcUnit);
            DrawMetafile(
                metafile,
                destRect.Location,
                new PointF(destRect.Right, destRect.Top),
                new PointF(destRect.Left, destRect.Bottom),
                source,
                imageAttributes: null);
        }
    }

    public void DrawIcon(Icon icon, Rectangle targetRect)
    {
        ArgumentNullException.ThrowIfNull(icon);
        using var bitmap = icon.ToBitmap();
        DrawImage(bitmap, targetRect);
    }

    public void DrawIcon(Icon icon, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(icon);
        using Bitmap bitmap = icon.ToBitmap();
        DrawImageUnscaled(bitmap, x, y);
    }

    public void DrawIconUnstretched(Icon icon, Rectangle targetRect)
    {
        ArgumentNullException.ThrowIfNull(icon);
        using Bitmap bitmap = icon.ToBitmap();
        DrawImageUnscaled(bitmap, targetRect.X, targetRect.Y);
    }

    public void DrawImageUnscaled(Image image, int x, int y)
    {
        DrawImage(image, x, y);
    }

    public void DrawImageUnscaled(Image image, Point point)
    {
        DrawImageUnscaled(image, point.X, point.Y);
    }

    public void DrawImageUnscaled(Image image, Rectangle rect) => DrawImageUnscaled(image, rect.X, rect.Y);

    public void DrawImageUnscaled(Image image, int x, int y, int width, int height) =>
        DrawImageUnscaled(image, x, y);

    public void DrawImageUnscaledAndClipped(Image image, Rectangle rect)
    {
        ArgumentNullException.ThrowIfNull(image);
        int width = Math.Min(rect.Width, image.Width);
        int height = Math.Min(rect.Height, image.Height);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        DrawImage(
            image,
            new Rectangle(rect.X, rect.Y, width, height),
            0,
            0,
            width,
            height,
            GraphicsUnit.Pixel);
    }

    public void DrawImage(
        Image image,
        float x,
        float y,
        RectangleF srcRect,
        GraphicsUnit srcUnit)
    {
        ArgumentNullException.ThrowIfNull(image);
        RectangleF sourcePixels = ConvertSourceRect(srcRect, srcUnit);
        DrawImage(
            image,
            new RectangleF(x, y, sourcePixels.Width, sourcePixels.Height),
            srcRect,
            srcUnit);
    }

    public void DrawImage(
        Image image,
        int x,
        int y,
        Rectangle srcRect,
        GraphicsUnit srcUnit) =>
        DrawImage(image, x, y, (RectangleF)srcRect, srcUnit);

    public void DrawImage(
        Image image,
        Rectangle destRect,
        float srcX,
        float srcY,
        float srcWidth,
        float srcHeight,
        GraphicsUnit srcUnit) =>
        DrawImage(image, destRect, srcX, srcY, srcWidth, srcHeight, srcUnit, null);

    public void DrawImage(
        Image image,
        Rectangle destRect,
        float srcX,
        float srcY,
        float srcWidth,
        float srcHeight,
        GraphicsUnit srcUnit,
        ImageAttributes? imageAttr) =>
        DrawImage(image, destRect, srcX, srcY, srcWidth, srcHeight, srcUnit, imageAttr, null);

    public void DrawImage(
        Image image,
        Rectangle destRect,
        float srcX,
        float srcY,
        float srcWidth,
        float srcHeight,
        GraphicsUnit srcUnit,
        ImageAttributes? imageAttr,
        DrawImageAbort? callback) =>
        DrawImage(
            image,
            destRect,
            srcX,
            srcY,
            srcWidth,
            srcHeight,
            srcUnit,
            imageAttr,
            callback,
            IntPtr.Zero);

    public void DrawImage(
        Image image,
        Rectangle destRect,
        float srcX,
        float srcY,
        float srcWidth,
        float srcHeight,
        GraphicsUnit srcUnit,
        ImageAttributes? imageAttr,
        DrawImageAbort? callback,
        IntPtr callbackData)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (callback?.Invoke(callbackData) == true)
        {
            return;
        }

        if (image is Bitmap bitmap)
        {
            RectangleF source = ConvertSourceRect(
                new RectangleF(srcX, srcY, srcWidth, srcHeight),
                srcUnit);
            DrawBitmap(bitmap, destRect, source, imageAttr);
        }
        else if (image is Metafile metafile)
        {
            RectangleF source = ConvertSourceRect(
                new RectangleF(srcX, srcY, srcWidth, srcHeight),
                srcUnit);
            DrawMetafile(
                metafile,
                destRect.Location,
                new PointF(destRect.Right, destRect.Top),
                new PointF(destRect.Left, destRect.Bottom),
                source,
                imageAttr);
        }
    }

    public void DrawImage(
        Image image,
        Rectangle destRect,
        int srcX,
        int srcY,
        int srcWidth,
        int srcHeight,
        GraphicsUnit srcUnit) =>
        DrawImage(image, destRect, srcX, srcY, srcWidth, srcHeight, srcUnit, null);

    public void DrawImage(
        Image image,
        Rectangle destRect,
        int srcX,
        int srcY,
        int srcWidth,
        int srcHeight,
        GraphicsUnit srcUnit,
        ImageAttributes? imageAttr)
        => DrawImage(
            image,
            destRect,
            (float)srcX,
            srcY,
            srcWidth,
            srcHeight,
            srcUnit,
            imageAttr);

    public delegate bool DrawImageAbort(IntPtr callbackdata);

    public void DrawImage(
        Image image,
        Rectangle destRect,
        int srcX,
        int srcY,
        int srcWidth,
        int srcHeight,
        GraphicsUnit srcUnit,
        ImageAttributes? imageAttr,
        DrawImageAbort? callback) =>
        DrawImage(
            image,
            destRect,
            srcX,
            srcY,
            srcWidth,
            srcHeight,
            srcUnit,
            imageAttr,
            callback,
            IntPtr.Zero);

    public void DrawImage(
        Image image,
        Rectangle destRect,
        int srcX,
        int srcY,
        int srcWidth,
        int srcHeight,
        GraphicsUnit srcUnit,
        ImageAttributes? imageAttr,
        DrawImageAbort? callback,
        IntPtr callbackData)
        => DrawImage(
            image,
            destRect,
            (float)srcX,
            srcY,
            srcWidth,
            srcHeight,
            srcUnit,
            imageAttr,
            callback,
            callbackData);

    private void DrawImageMapped(
        Image image,
        PointF topLeft,
        PointF topRight,
        PointF bottomLeft,
        PointF? bottomRight,
        RectangleF sourceRect,
        GraphicsUnit sourceUnit,
        ImageAttributes? imageAttributes,
        DrawImageAbort? callback,
        int callbackData)
    {
        Vector2 v0 = new(topLeft.X, topLeft.Y);
        Vector2 v1 = new(topRight.X, topRight.Y);
        Vector2 v3 = new(bottomLeft.X, bottomLeft.Y);
        Vector2 v2 = bottomRight is PointF point
            ? new Vector2(point.X, point.Y)
            : v1 + v3 - v0;
        Vector4 projectiveWeights = CreateProjectiveWeights(
            v0,
            v1,
            v2,
            v3,
            bottomRight.HasValue);

        if (callback?.Invoke(new IntPtr(callbackData)) == true)
        {
            return;
        }

        if (image is Bitmap bitmap)
        {
            DrawMappedBitmap(
                bitmap,
                v0,
                v1,
                v2,
                v3,
                projectiveWeights,
                ConvertSourceRect(sourceRect, sourceUnit),
                imageAttributes);
        }
        else if (image is Metafile metafile)
        {
            Vector2 affineBottomRight = v1 + v3 - v0;
            if (Vector2.DistanceSquared(v2, affineBottomRight) > 1e-6f)
            {
                throw new NotSupportedException(
                    "Metafile playback currently supports affine destination parallelograms only.");
            }

            DrawMetafile(
                metafile,
                topLeft,
                topRight,
                bottomLeft,
                ConvertSourceRect(sourceRect, sourceUnit),
                imageAttributes);
        }
    }

    private static RectangleF GetNaturalImageSourceRectangle(Image image) =>
        image is Metafile metafile
            ? metafile.GetMetafileHeader().Bounds
            : new RectangleF(0f, 0f, image.Width, image.Height);

    private void DrawMappedBitmap(
        Bitmap bitmap,
        Vector2 destination0,
        Vector2 destination1,
        Vector2 destination2,
        Vector2 destination3,
        Vector4 projectiveWeights,
        RectangleF sourceRect,
        ImageAttributes? imageAttributes,
        GpuRasterOperation rasterOperation = default)
    {
        Bitmap? adjustedBitmap = null;
        if (imageAttributes is not null &&
            (imageAttributes.RequiresCpuAdjustment(ColorAdjustType.Bitmap) ||
                imageAttributes.GetRemapTable(ColorAdjustType.Bitmap).Length != 0 ||
                imageAttributes.GetGpuColorMatrix(ColorAdjustType.Bitmap) is not null))
        {
            adjustedBitmap = bitmap.CreateImageAttributesAdjusted(
                imageAttributes,
                ColorAdjustType.Bitmap);
        }

        using (adjustedBitmap)
        {
            GpuTexture retainedTexture = RetainBitmapTexture(adjustedBitmap ?? bitmap);
            Rect source = sourceRect.Width > 0f && sourceRect.Height > 0f
                ? new Rect(sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height)
                : Rect.Empty;
            float left = MathF.Min(
                MathF.Min(destination0.X, destination1.X),
                MathF.Min(destination2.X, destination3.X));
            float top = MathF.Min(
                MathF.Min(destination0.Y, destination1.Y),
                MathF.Min(destination2.Y, destination3.Y));
            float right = MathF.Max(
                MathF.Max(destination0.X, destination1.X),
                MathF.Max(destination2.X, destination3.X));
            float bottom = MathF.Max(
                MathF.Max(destination0.Y, destination1.Y),
                MathF.Max(destination2.Y, destination3.Y));

            _context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawTexture,
                Texture = retainedTexture,
                Rect = new Rect(left, top, right - left, bottom - top),
                SrcRect = source,
                Transform = CurrentTransform4x4(),
                TextureSamplingMode = GetTextureSamplingMode(),
                TextureDestination0 = destination0,
                TextureDestination1 = destination1,
                TextureDestination2 = destination2,
                TextureDestination3 = destination3,
                TextureDestinationProjectiveWeights = projectiveWeights,
                HasTextureDestinationQuad = true,
                RasterOperation = rasterOperation
            });
        }
    }

    internal void DrawImageRasterOperation(
        Bitmap bitmap,
        PointF topLeft,
        PointF topRight,
        PointF bottomLeft,
        RectangleF sourceRect,
        byte rasterOperationCode,
        Color patternColor)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ThrowIfDisposed();
        var pattern = new Vector4(
            patternColor.R / 255f,
            patternColor.G / 255f,
            patternColor.B / 255f,
            patternColor.A / 255f);
        DrawImageRasterOperation(
            bitmap,
            topLeft,
            topRight,
            bottomLeft,
            sourceRect,
            new GpuRasterOperation(rasterOperationCode, pattern));
    }

    internal void DrawImageRasterOperation(
        Bitmap bitmap,
        PointF topLeft,
        PointF topRight,
        PointF bottomLeft,
        RectangleF sourceRect,
        GpuRasterOperation rasterOperation)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ThrowIfDisposed();
        Vector2 destination0 = new(topLeft.X, topLeft.Y);
        Vector2 destination1 = new(topRight.X, topRight.Y);
        Vector2 destination3 = new(bottomLeft.X, bottomLeft.Y);
        Vector2 destination2 = destination1 + destination3 - destination0;
        Vector4 projectiveWeights = CreateProjectiveWeights(
            destination0,
            destination1,
            destination2,
            destination3,
            isPerspective: false);
        DrawMappedBitmap(
            bitmap,
            destination0,
            destination1,
            destination2,
            destination3,
            projectiveWeights,
            sourceRect,
            imageAttributes: null,
            rasterOperation);
    }

    private static Vector4 CreateProjectiveWeights(
        Vector2 topLeft,
        Vector2 topRight,
        Vector2 bottomRight,
        Vector2 bottomLeft,
        bool isPerspective)
    {
        if (!IsFinite(topLeft) ||
            !IsFinite(topRight) ||
            !IsFinite(bottomRight) ||
            !IsFinite(bottomLeft))
        {
            throw new ArgumentException(
                "Destination points must contain finite coordinates.",
                "destPoints");
        }

        const float epsilon = 1e-6f;
        if (!isPerspective)
        {
            float affineArea = Cross(topRight - topLeft, bottomLeft - topLeft);
            if (!float.IsFinite(affineArea) || MathF.Abs(affineArea) <= epsilon)
            {
                throw new ArgumentException(
                    "Destination points must define a non-degenerate parallelogram.",
                    "destPoints");
            }

            return Vector4.One;
        }

        Vector2 firstDiagonal = bottomRight - topLeft;
        Vector2 secondDiagonal = bottomLeft - topRight;
        float denominator = Cross(firstDiagonal, secondDiagonal);
        if (!float.IsFinite(denominator) || MathF.Abs(denominator) <= epsilon)
        {
            throw new ArgumentException(
                "Destination points must define a non-degenerate convex quadrilateral.",
                "destPoints");
        }

        Vector2 betweenStarts = topRight - topLeft;
        float firstFraction = Cross(betweenStarts, secondDiagonal) / denominator;
        float secondFraction = Cross(betweenStarts, firstDiagonal) / denominator;
        if (!float.IsFinite(firstFraction) ||
            !float.IsFinite(secondFraction) ||
            firstFraction <= epsilon ||
            firstFraction >= 1f - epsilon ||
            secondFraction <= epsilon ||
            secondFraction >= 1f - epsilon)
        {
            throw new ArgumentException(
                "Destination points must define a non-degenerate convex quadrilateral.",
                "destPoints");
        }

        var weights = new Vector4(
            1f / (1f - firstFraction),
            1f / (1f - secondFraction),
            1f / firstFraction,
            1f / secondFraction);
        float maximum = MathF.Max(
            MathF.Max(weights.X, weights.Y),
            MathF.Max(weights.Z, weights.W));
        if (!float.IsFinite(maximum) || maximum <= epsilon)
        {
            throw new ArgumentException(
                "Destination points must define a finite perspective mapping.",
                "destPoints");
        }

        return weights / maximum;
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static float Cross(Vector2 first, Vector2 second) =>
        first.X * second.Y - first.Y * second.X;

    private static void ValidateDestinationPoints<T>(T[]? destinationPoints)
    {
        ArgumentNullException.ThrowIfNull(destinationPoints);
        if (destinationPoints.Length is not 3 and not 4)
        {
            throw new ArgumentException(
                "Destination points must contain three or four points.",
                "destPoints");
        }
    }

    private void DrawBitmap(Bitmap bitmap, RectangleF rect)
    {
        DrawBitmap(bitmap, rect, default, null);
    }

    private void DrawBitmap(Bitmap bitmap, RectangleF rect, RectangleF sourceRect, ImageAttributes? imageAttributes)
    {
        Bitmap? adjustedBitmap = null;
        if (imageAttributes is not null)
        {
            if (imageAttributes.RequiresCpuAdjustment(ColorAdjustType.Bitmap))
            {
                adjustedBitmap = bitmap.CreateImageAttributesAdjusted(
                    imageAttributes,
                    ColorAdjustType.Bitmap);
            }
            else
            {
                (Color OldColor, Color NewColor)[] remapTable =
                    imageAttributes.GetRemapTable(ColorAdjustType.Bitmap);
                if (remapTable.Length != 0)
                {
                    adjustedBitmap = bitmap.CreateColorRemapped(remapTable);
                }
            }
        }

        using (adjustedBitmap)
        {
            var retainedTexture = RetainBitmapTexture(adjustedBitmap ?? bitmap);
            var srcRect = sourceRect.Width > 0f && sourceRect.Height > 0f
                ? new Rect(sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height)
                : Rect.Empty;

            var colorMatrix = TryCreateImageEffectColorMatrix(
                adjustedBitmap is null
                    ? imageAttributes?.GetGpuColorMatrix(ColorAdjustType.Bitmap)
                    : null);
            if (colorMatrix.HasValue)
            {
                _context.DrawImageWithEffect(
                    retainedTexture,
                    new Rect(rect.X, rect.Y, rect.Width, rect.Height),
                    sourceRect: srcRect,
                    samplingMode: GetTextureSamplingMode(),
                    colorMatrix: colorMatrix,
                    transform: CurrentTransform4x4());
                return;
            }

            _context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawTexture,
                Texture = retainedTexture,
                Rect = new Rect(rect.X, rect.Y, rect.Width, rect.Height),
                SrcRect = srcRect,
                Transform = CurrentTransform4x4(),
                TextureSamplingMode = GetTextureSamplingMode()
            });
        }
    }

    private RectangleF ConvertSourceRect(RectangleF sourceRect, GraphicsUnit unit)
    {
        if (unit == GraphicsUnit.Pixel || unit == GraphicsUnit.Display || unit == GraphicsUnit.World)
        {
            return sourceRect;
        }

        var scaleX = UnitToPixelScale(unit, DpiX);
        var scaleY = UnitToPixelScale(unit, DpiY);
        return new RectangleF(
            sourceRect.X * scaleX,
            sourceRect.Y * scaleY,
            sourceRect.Width * scaleX,
            sourceRect.Height * scaleY);
    }

    private static float UnitToPixelScale(GraphicsUnit unit, float dpi)
    {
        return unit switch
        {
            GraphicsUnit.Point => dpi / 72f,
            GraphicsUnit.Inch => dpi,
            GraphicsUnit.Document => dpi / 300f,
            GraphicsUnit.Millimeter => dpi / 25.4f,
            _ => 1f
        };
    }

    private static ImageEffectColorMatrix? TryCreateImageEffectColorMatrix(ColorMatrix? colorMatrix)
    {
        if (colorMatrix == null)
        {
            return null;
        }

        var matrix = colorMatrix.Matrix;
        return new ImageEffectColorMatrix(
            new Vector4(Read(matrix, 0, 0), Read(matrix, 1, 0), Read(matrix, 2, 0), Read(matrix, 3, 0)),
            new Vector4(Read(matrix, 0, 1), Read(matrix, 1, 1), Read(matrix, 2, 1), Read(matrix, 3, 1)),
            new Vector4(Read(matrix, 0, 2), Read(matrix, 1, 2), Read(matrix, 2, 2), Read(matrix, 3, 2)),
            new Vector4(Read(matrix, 0, 3), Read(matrix, 1, 3), Read(matrix, 2, 3), Read(matrix, 3, 3)),
            new Vector4(Read(matrix, 4, 0), Read(matrix, 4, 1), Read(matrix, 4, 2), Read(matrix, 4, 3)));
    }

    private static float Read(float[][] matrix, int row, int column)
    {
        if ((uint)row >= (uint)matrix.Length)
        {
            return 0f;
        }

        var rowValues = matrix[row];
        if (rowValues == null || (uint)column >= (uint)rowValues.Length)
        {
            return 0f;
        }

        return rowValues[column];
    }

    private TextureSamplingMode GetTextureSamplingMode()
    {
        return InterpolationMode switch
        {
            InterpolationMode.NearestNeighbor => TextureSamplingMode.Nearest,
            InterpolationMode.Bicubic or InterpolationMode.HighQualityBicubic => TextureSamplingMode.Cubic,
            _ => TextureSamplingMode.Linear
        };
    }

    internal GpuRasterTexturePattern RetainRasterOperationPattern(TextureBrush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);
        ThrowIfDisposed();
        Matrix3x2 transform = brush.TransformValue;
        if (transform.M11 != 1f || transform.M12 != 0f ||
            transform.M21 != 0f || transform.M22 != 1f)
        {
            throw new NotSupportedException(
                "Raster-operation texture patterns currently require a translation-only brush transform.");
        }

        return new GpuRasterTexturePattern(
            RetainBitmapTexture(brush.Bitmap),
            new Vector2(transform.M31, transform.M32));
    }

    private GpuTexture RetainBitmapTexture(Bitmap bitmap)
    {
        var targetContext = _bitmap?.GetDrawingContext() ?? _targetContext ?? GpuProvider.Context;
        if (ReferenceEquals(bitmap, _bitmap))
        {
            Bitmap snapshot;
            SuspendRecorderState();
            try
            {
                snapshot = (Bitmap)bitmap.Clone();
            }
            finally
            {
                ResumeRecorderState();
            }

            using (snapshot)
            {
                if (!_context.TryRetainTexture(snapshot, targetContext, out GpuTexture snapshotTexture))
                {
                    throw new ObjectDisposedException(nameof(bitmap), "Cannot draw a disposed GDI Bitmap.");
                }

                return snapshotTexture;
            }
        }

        if (!_context.TryRetainTexture(bitmap, targetContext, out var retainedTexture))
        {
            throw new ObjectDisposedException(nameof(bitmap), "Cannot draw a disposed GDI Bitmap.");
        }

        return retainedTexture;
    }

    internal WgpuContext GetTargetContextForCachedBitmap()
    {
        ThrowIfDisposed();
        WgpuContext targetContext =
            _bitmap?.GetDrawingContext() ?? _targetContext ?? GpuProvider.Context;
        if (targetContext.IsDisposed || !targetContext.IsInitialized)
        {
            throw new InvalidOperationException(
                "The Graphics device is not available for cached bitmap drawing.");
        }

        return targetContext;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_hasPushedClip)
        {
            _context.PopGeometryClip();
            _hasPushedClip = false;
        }
        for (int index = _savedStates.Count - 1; index >= 0; index--)
        {
            if (_savedStates[index].HasInheritedClip)
            {
                _context.PopGeometryClip();
            }
        }
        PopCurrentCompositingMode();
        _clip?.Dispose();
        _clip = null;
        foreach (SavedGraphicsContext state in _savedStates)
        {
            state.Clip?.Dispose();
        }
        _savedStates.Clear();
        _transform.Dispose();
        // Bitmap commands are intentionally retained until the image is
        // consumed (GetPixel, Save, GpuTexture, or a context-bound lease).
        // Disposing Graphics must not submit work through an ambient host
        // context whose render scope may already be ending.
        _completed?.Invoke();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ArgumentException("Parameter is not valid.");
        }
    }

    private static void ValidateCopyPixelOperation(CopyPixelOperation operation)
    {
        int value = (int)operation;
        int modifiers = (int)(CopyPixelOperation.CaptureBlt | CopyPixelOperation.NoMirrorBitmap);
        int baseOperation = value & ~modifiers;
        bool validBase = baseOperation is
            (int)CopyPixelOperation.Blackness or
            (int)CopyPixelOperation.DestinationInvert or
            (int)CopyPixelOperation.MergeCopy or
            (int)CopyPixelOperation.MergePaint or
            (int)CopyPixelOperation.NotSourceCopy or
            (int)CopyPixelOperation.NotSourceErase or
            (int)CopyPixelOperation.PatCopy or
            (int)CopyPixelOperation.PatInvert or
            (int)CopyPixelOperation.PatPaint or
            (int)CopyPixelOperation.SourceAnd or
            (int)CopyPixelOperation.SourceCopy or
            (int)CopyPixelOperation.SourceErase or
            (int)CopyPixelOperation.SourceInvert or
            (int)CopyPixelOperation.SourcePaint or
            (int)CopyPixelOperation.Whiteness;
        bool modifierOnly = baseOperation == 0 && (value & modifiers) != 0;
        if (!validBase && !modifierOnly)
        {
            throw new InvalidEnumArgumentException(
                nameof(operation),
                value,
                typeof(CopyPixelOperation));
        }
    }

    internal void EnsureNotDisposed() => ThrowIfDisposed();

    public IntPtr GetHdc() =>
        throw new PlatformNotSupportedException(
            "The portable ProGPU drawing context does not expose a native HDC.");

    public void ReleaseHdc() =>
        throw new PlatformNotSupportedException(
            "The portable ProGPU drawing context does not own a native HDC.");

    public void ReleaseHdc(IntPtr hdc) => ReleaseHdc();

    public void ReleaseHdcInternal(IntPtr hdc) => ReleaseHdc(hdc);
}
