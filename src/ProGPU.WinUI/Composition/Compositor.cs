using System.Numerics;
using Microsoft.UI.Dispatching;
using Windows.Foundation.Metadata;
using Windows.UI;

namespace Microsoft.UI.Composition;

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class Compositor : IDisposable
{
    [ThreadStatic]
    private static Compositor? s_sharedForCurrentThread;

    private bool _isDisposed;

    public Compositor()
    {
        DispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public string Comment { get; set; } = string.Empty;

    public DispatcherQueue? DispatcherQueue { get; }

    internal bool IsDisposed => _isDisposed;

    public AnimationPropertyInfo CreateAnimationPropertyInfo()
    {
        ThrowIfDisposed();
        return new AnimationPropertyInfo(this);
    }

    public CompositionColorBrush CreateColorBrush()
    {
        ThrowIfDisposed();
        return new CompositionColorBrush(this, default);
    }

    public CompositionColorBrush CreateColorBrush(Color color)
    {
        ThrowIfDisposed();
        return new CompositionColorBrush(this, color);
    }

    public CompositionColorGradientStop CreateColorGradientStop()
    {
        ThrowIfDisposed();
        return new CompositionColorGradientStop(this, 0f, default);
    }

    public CompositionColorGradientStop CreateColorGradientStop(
        float offset,
        Color color)
    {
        ThrowIfDisposed();
        return new CompositionColorGradientStop(this, offset, color);
    }

    public DropShadow CreateDropShadow()
    {
        ThrowIfDisposed();
        return new DropShadow(this);
    }

    public CompositionContainerShape CreateContainerShape()
    {
        ThrowIfDisposed();
        return new CompositionContainerShape(this);
    }

    public CompositionEllipseGeometry CreateEllipseGeometry()
    {
        ThrowIfDisposed();
        return new CompositionEllipseGeometry(this);
    }

    public CompositionGeometricClip CreateGeometricClip()
    {
        ThrowIfDisposed();
        return new CompositionGeometricClip(this);
    }

    public CompositionGeometricClip CreateGeometricClip(
        CompositionGeometry geometry)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(geometry);
        EnsureSameCompositor(geometry);
        return new CompositionGeometricClip(this, geometry);
    }

    public InsetClip CreateInsetClip()
    {
        ThrowIfDisposed();
        return new InsetClip(this);
    }

    public InsetClip CreateInsetClip(
        float leftInset,
        float topInset,
        float rightInset,
        float bottomInset)
    {
        ThrowIfDisposed();
        return new InsetClip(
            this,
            leftInset,
            topInset,
            rightInset,
            bottomInset);
    }

    public CompositionLineGeometry CreateLineGeometry()
    {
        ThrowIfDisposed();
        return new CompositionLineGeometry(this);
    }

    public LayerVisual CreateLayerVisual()
    {
        ThrowIfDisposed();
        return new LayerVisual(this);
    }

    public CompositionLinearGradientBrush CreateLinearGradientBrush()
    {
        ThrowIfDisposed();
        return new CompositionLinearGradientBrush(this);
    }

    public CompositionPathGeometry CreatePathGeometry()
    {
        ThrowIfDisposed();
        return new CompositionPathGeometry(this);
    }

    public CompositionPathGeometry CreatePathGeometry(
        CompositionPath path)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(path);
        return new CompositionPathGeometry(this, path);
    }

    public CompositionPropertySet CreatePropertySet()
    {
        ThrowIfDisposed();
        return new CompositionPropertySet(this);
    }

    public CompositionRectangleGeometry CreateRectangleGeometry()
    {
        ThrowIfDisposed();
        return new CompositionRectangleGeometry(this);
    }

    public RectangleClip CreateRectangleClip()
    {
        ThrowIfDisposed();
        return new RectangleClip(this);
    }

    public RectangleClip CreateRectangleClip(
        float left,
        float top,
        float right,
        float bottom)
    {
        ThrowIfDisposed();
        return new RectangleClip(this, left, top, right, bottom);
    }

    public RectangleClip CreateRectangleClip(
        float left,
        float top,
        float right,
        float bottom,
        Vector2 topLeftRadius,
        Vector2 topRightRadius,
        Vector2 bottomRightRadius,
        Vector2 bottomLeftRadius)
    {
        ThrowIfDisposed();
        return new RectangleClip(
            this,
            left,
            top,
            right,
            bottom,
            topLeftRadius,
            topRightRadius,
            bottomRightRadius,
            bottomLeftRadius);
    }

    public CompositionRoundedRectangleGeometry
        CreateRoundedRectangleGeometry()
    {
        ThrowIfDisposed();
        return new CompositionRoundedRectangleGeometry(this);
    }

    public CompositionRadialGradientBrush CreateRadialGradientBrush()
    {
        ThrowIfDisposed();
        return new CompositionRadialGradientBrush(this);
    }

    public ShapeVisual CreateShapeVisual()
    {
        ThrowIfDisposed();
        return new ShapeVisual(this);
    }

    public CompositionSpriteShape CreateSpriteShape()
    {
        ThrowIfDisposed();
        return new CompositionSpriteShape(this);
    }

    public CompositionSpriteShape CreateSpriteShape(
        CompositionGeometry geometry)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(geometry);
        EnsureSameCompositor(geometry);
        return new CompositionSpriteShape(this, geometry);
    }

    public CompositionViewBox CreateViewBox()
    {
        ThrowIfDisposed();
        return new CompositionViewBox(this);
    }

    public ContainerVisual CreateContainerVisual()
    {
        ThrowIfDisposed();
        return new ContainerVisual(this);
    }

    public SpriteVisual CreateSpriteVisual()
    {
        ThrowIfDisposed();
        return new SpriteVisual(this);
    }

    public void Dispose()
    {
        _isDisposed = true;
        if (ReferenceEquals(s_sharedForCurrentThread, this))
            s_sharedForCurrentThread = null;
        GC.SuppressFinalize(this);
    }

    internal static Compositor GetSharedForCurrentThread()
    {
        if (s_sharedForCurrentThread is null ||
            s_sharedForCurrentThread.IsDisposed)
        {
            s_sharedForCurrentThread = new Compositor();
        }

        return s_sharedForCurrentThread;
    }

    internal void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(Compositor));
    }

    private void EnsureSameCompositor(CompositionObject value)
    {
        if (!ReferenceEquals(this, value.Compositor))
        {
            throw new InvalidOperationException(
                "Composition objects must belong to the same Compositor.");
        }
    }
}
