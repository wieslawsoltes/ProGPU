using System.Diagnostics.CodeAnalysis;

namespace ProGPU.Backend;

/// <summary>
/// Describes a live top-level window that can own windows created by another
/// managed desktop stack in the same process.
/// </summary>
public interface INativeWindowOwner
{
    NativeWindowHandle NativeHandle { get; }

    bool IsAlive { get; }

    bool IsVisible { get; }

    bool IsEnabled { get; }

    bool TrySetEnabled(bool enabled);

    bool TryActivate();
}

/// <summary>
/// Resolves process-local presentation handles to typed native top-level
/// windows without exposing toolkit objects or probing them with reflection.
/// </summary>
public static class NativeWindowOwnerRegistry
{
    private sealed record Entry(long RegistrationId, WeakReference<INativeWindowOwner> Owner);

    private static readonly object s_sync = new();
    private static readonly Dictionary<nint, Entry> s_owners = [];
    private static long s_nextRegistrationId;

    public static IDisposable Register(nint presentationHandle, INativeWindowOwner owner)
    {
        if (presentationHandle == 0)
        {
            throw new ArgumentException(
                "A presentation handle must be non-zero.",
                nameof(presentationHandle));
        }

        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.IsAlive)
        {
            throw new ArgumentException("The native window owner must be live.", nameof(owner));
        }

        long registrationId = Interlocked.Increment(ref s_nextRegistrationId);
        lock (s_sync)
        {
            s_owners[presentationHandle] = new Entry(
                registrationId,
                new WeakReference<INativeWindowOwner>(owner));
        }

        return new Registration(presentationHandle, registrationId);
    }

    public static bool TryResolve(
        nint presentationHandle,
        [NotNullWhen(true)] out INativeWindowOwner? owner)
    {
        owner = null;
        if (presentationHandle == 0)
        {
            return false;
        }

        lock (s_sync)
        {
            if (!s_owners.TryGetValue(presentationHandle, out Entry? entry))
            {
                return false;
            }

            if (!entry.Owner.TryGetTarget(out INativeWindowOwner? registeredOwner)
                || !registeredOwner.IsAlive)
            {
                s_owners.Remove(presentationHandle);
                return false;
            }

            owner = registeredOwner;
            return true;
        }
    }

    public static bool TryResolveNativeHandle(
        nint presentationHandle,
        out NativeWindowHandle nativeHandle)
    {
        if (TryResolve(presentationHandle, out INativeWindowOwner? owner)
            && owner.NativeHandle is { IsValid: true } resolvedHandle)
        {
            nativeHandle = resolvedHandle;
            return true;
        }

        nativeHandle = NativeWindowHandle.Empty;
        return false;
    }

    private static void Unregister(nint presentationHandle, long registrationId)
    {
        lock (s_sync)
        {
            if (s_owners.TryGetValue(presentationHandle, out Entry? entry)
                && entry.RegistrationId == registrationId)
            {
                s_owners.Remove(presentationHandle);
            }
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly nint _presentationHandle;
        private long _registrationId;

        public Registration(nint presentationHandle, long registrationId)
        {
            _presentationHandle = presentationHandle;
            _registrationId = registrationId;
        }

        public void Dispose()
        {
            long registrationId = Interlocked.Exchange(ref _registrationId, 0);
            if (registrationId != 0)
            {
                Unregister(_presentationHandle, registrationId);
            }
        }
    }
}
