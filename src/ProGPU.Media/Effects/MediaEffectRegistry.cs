namespace ProGPU.Media.Effects;

using ProGPU.Media.Audio;

public enum MediaEffectKind
{
    Audio,
    Video
}

public readonly record struct MediaEffectDescriptor(
    string ActivatableClassId,
    MediaEffectKind Kind,
    IReadOnlyDictionary<string, object?> Properties);

public interface IMediaEffect : IDisposable
{
    string Id { get; }
    MediaEffectKind Kind { get; }
}

/// <summary>
/// Audio effect contract that can be installed directly in a provider's
/// allocation-free native callback processor chain.
/// </summary>
public interface IMediaAudioEffect :
    IMediaEffect,
    IMediaAudioProcessor
{
}

public interface IMediaEffectFactory
{
    string ActivatableClassId { get; }

    IMediaEffect Create(in MediaEffectDescriptor descriptor);
}

/// <summary>
/// Typed activation table used by WinUI-compatible effect methods without
/// runtime reflection or assembly scanning.
/// </summary>
public sealed class MediaEffectRegistry
{
    private readonly object _gate = new();
    private Dictionary<string, IMediaEffectFactory> _factories =
        new(StringComparer.Ordinal);

    public static MediaEffectRegistry Default { get; } = new();

    public IDisposable Register(IMediaEffectFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (string.IsNullOrWhiteSpace(factory.ActivatableClassId))
        {
            throw new ArgumentException(
                "An effect factory must have an activatable class ID.",
                nameof(factory));
        }

        lock (_gate)
        {
            if (_factories.ContainsKey(factory.ActivatableClassId))
            {
                throw new InvalidOperationException(
                    $"The media effect '{factory.ActivatableClassId}' is already registered.");
            }

            var next = new Dictionary<string, IMediaEffectFactory>(
                _factories,
                StringComparer.Ordinal)
            {
                [factory.ActivatableClassId] = factory
            };
            Volatile.Write(ref _factories, next);
        }

        return new Registration(this, factory);
    }

    /// <summary>
    /// Returns whether a typed factory is registered for the activatable
    /// class ID. This is an O(1), lock-free snapshot lookup suitable for
    /// provider capability negotiation; it does not instantiate the effect.
    /// </summary>
    public bool IsRegistered(string activatableClassId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            activatableClassId);
        Dictionary<string, IMediaEffectFactory> factories =
            Volatile.Read(ref _factories);
        return factories.ContainsKey(activatableClassId);
    }

    /// <summary>
    /// Creates an effect through the explicitly registered typed factory.
    /// Activation performs no reflection or assembly scanning.
    /// </summary>
    public bool TryCreate(
        in MediaEffectDescriptor descriptor,
        out IMediaEffect? effect)
    {
        Dictionary<string, IMediaEffectFactory> factories =
            Volatile.Read(ref _factories);
        if (!factories.TryGetValue(
                descriptor.ActivatableClassId,
                out IMediaEffectFactory? factory))
        {
            effect = null;
            return false;
        }

        effect = factory.Create(descriptor);
        return true;
    }

    private void Unregister(IMediaEffectFactory factory)
    {
        lock (_gate)
        {
            if (!_factories.TryGetValue(
                    factory.ActivatableClassId,
                    out IMediaEffectFactory? registered) ||
                !ReferenceEquals(factory, registered))
            {
                return;
            }

            var next = new Dictionary<string, IMediaEffectFactory>(
                _factories,
                StringComparer.Ordinal);
            next.Remove(factory.ActivatableClassId);
            Volatile.Write(ref _factories, next);
        }
    }

    private sealed class Registration : IDisposable
    {
        private MediaEffectRegistry? _owner;
        private IMediaEffectFactory? _factory;

        public Registration(
            MediaEffectRegistry owner,
            IMediaEffectFactory factory)
        {
            _owner = owner;
            _factory = factory;
        }

        public void Dispose()
        {
            MediaEffectRegistry? owner =
                Interlocked.Exchange(ref _owner, null);
            IMediaEffectFactory? factory =
                Interlocked.Exchange(ref _factory, null);
            if (owner is not null && factory is not null)
            {
                owner.Unregister(factory);
            }
        }
    }
}
