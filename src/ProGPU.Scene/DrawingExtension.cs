namespace ProGPU.Scene;

/// <summary>
/// An application-owned drawing definition. Share one definition between controls
/// and windows; its factory must return a fresh, device-independent extension for
/// each compositor. GPU resources should be created lazily by that instance.
/// </summary>
public abstract class DrawingExtension
{
    private static int s_nextId = 0x4000_0000;
    internal int Id { get; }
    public string Name { get; }

    private protected DrawingExtension(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Id = Interlocked.Increment(ref s_nextId);
        if (Id <= 0) throw new InvalidOperationException("Drawing extension identifiers are exhausted.");
    }

    internal abstract ICompositorExtension CreateInstance();
}

/// <summary>
/// Binds retained drawing data to a compositor extension factory without exposing
/// application-selected numeric IDs. Construction allocates once; recording a
/// reference-type payload neither boxes nor copies it.
/// </summary>
public sealed class DrawingExtension<TData> : DrawingExtension where TData : class
{
    private readonly Func<ICompositorExtension> _factory;

    public DrawingExtension(string name, Func<ICompositorExtension> factory) : base(name)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    internal override ICompositorExtension CreateInstance() => _factory()
        ?? throw new InvalidOperationException($"The '{Name}' drawing extension factory returned null.");
}
