namespace System.Windows.Media;

public abstract class Brush : ProGPU.Wpf.Interop.IPortableInvalidationSource
{
    private double _opacity = 1.0;
    private uint _changeVersion;

    public event EventHandler? Changed;

    public double Opacity
    {
        get => _opacity;
        set
        {
            if (_opacity.Equals(value))
            {
                return;
            }

            _opacity = value;
            OnChanged();
        }
    }

    public uint ChangeVersion => _changeVersion;

    public abstract ProGPU.Vector.Brush ToNative();

    public virtual ProGPU.Vector.Brush ToNative(Rect targetBounds)
    {
        return ToNative();
    }

    protected void OnChanged()
    {
        unchecked
        {
            _changeVersion++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    bool ProGPU.Wpf.Interop.IPortableInvalidationSource.TrySubscribeInvalidated(EventHandler handler, out IDisposable subscription)
    {
        Changed += handler;
        subscription = new ProGPU.Wpf.Interop.PortableInvalidationSubscription(() => Changed -= handler);
        return true;
    }
}
