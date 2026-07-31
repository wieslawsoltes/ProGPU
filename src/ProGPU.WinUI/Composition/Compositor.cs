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

    public CompositionLineGeometry CreateLineGeometry()
    {
        ThrowIfDisposed();
        return new CompositionLineGeometry(this);
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

    public CompositionRoundedRectangleGeometry
        CreateRoundedRectangleGeometry()
    {
        ThrowIfDisposed();
        return new CompositionRoundedRectangleGeometry(this);
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
