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

    public CompositionPropertySet CreatePropertySet()
    {
        ThrowIfDisposed();
        return new CompositionPropertySet(this);
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
}
