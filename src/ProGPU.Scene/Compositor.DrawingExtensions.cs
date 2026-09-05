namespace ProGPU.Scene;

public unsafe partial class Compositor
{
    private Dictionary<DrawingExtension, ICompositorExtension>? _applicationDrawingExtensions;

    /// <summary>
    /// Creates and owns one extension instance for this compositor. Re-registering
    /// the same definition is an idempotent lookup. Call on the renderer's owning
    /// thread outside compilation/render callbacks. IDisposable instances are
    /// disposed with this compositor through the existing extension lifecycle.
    /// </summary>
    public ICompositorExtension RegisterDrawingExtension(DrawingExtension definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        lock (_registeredExtensions)
        {
            if (_applicationDrawingExtensions is not null && _applicationDrawingExtensions.TryGetValue(definition, out var existing))
                return existing;
            if (_extensionsById.ContainsKey(definition.Id))
                throw new InvalidOperationException($"The '{definition.Name}' drawing identifier is already registered through the legacy API.");
            var instance = definition.CreateInstance();
            if (_registeredExtensions.Contains(instance))
                throw new InvalidOperationException("A drawing extension factory must return a fresh instance for each registration.");
            _registeredExtensions.Add(instance);
            _extensionsById.Add(definition.Id, instance);
            (_applicationDrawingExtensions ??= new()).Add(definition, instance);
            _compiledSceneReusable = false;
            return instance;
        }
    }

    /// <summary>Returns the compositor-owned instance, or null before registration.</summary>
    public ICompositorExtension? GetDrawingExtension(DrawingExtension definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return GetExtension(definition.Id);
    }
}
