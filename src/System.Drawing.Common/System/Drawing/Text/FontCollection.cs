namespace System.Drawing.Text;

/// <summary>
/// Enumerates font families supplied by an installed or private collection.
/// </summary>
public abstract class FontCollection : IDisposable
{
    private bool _disposed;

    private protected FontCollection(bool remainsUsableAfterDispose)
    {
        RemainsUsableAfterDispose = remainsUsableAfterDispose;
    }

    private protected bool RemainsUsableAfterDispose { get; }

    private protected bool IsDisposed => _disposed;

    public FontFamily[] Families
    {
        get
        {
            ThrowIfUnavailable();
            return GetFamiliesCore();
        }
    }

    internal FontFamilySource ResolveFamily(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ThrowIfUnavailable();
        return TryResolveFamilyCore(name, out FontFamilySource? source)
            ? source!
            : throw new ArgumentException($"Font family '{name}' was not found in the collection.", nameof(name));
    }

    internal abstract bool TryResolveFamilyCore(string name, out FontFamilySource? source);

    private protected abstract FontFamily[] GetFamiliesCore();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed && !RemainsUsableAfterDispose)
        {
            throw new ArgumentException("Parameter is not valid.");
        }
    }
}
