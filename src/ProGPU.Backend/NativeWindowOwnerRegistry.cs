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
        => TryResolveEntry(presentationHandle, out owner, out _);

    private static bool TryResolveEntry(
        nint presentationHandle,
        [NotNullWhen(true)] out INativeWindowOwner? owner,
        [NotNullWhen(true)] out Entry? resolvedEntry)
    {
        owner = null;
        resolvedEntry = null;
        if (presentationHandle == 0) return false;

        Entry entry;
        INativeWindowOwner registeredOwner;
        lock (s_sync)
        {
            if (!s_owners.TryGetValue(presentationHandle, out entry!)) return false;
            if (!entry.Owner.TryGetTarget(out registeredOwner!))
            {
                s_owners.Remove(presentationHandle);
                return false;
            }
        }

        // Provider callbacks may retire or replace this exact registration.
        // Do not hold the registry lock across them or retry into a replacement.
        bool isAlive = registeredOwner.IsAlive;
        if (!ValidateEntry(presentationHandle, entry, isAlive)) return false;
        owner = registeredOwner;
        resolvedEntry = entry;
        return true;
    }

    public static bool TryResolveNativeHandle(
        nint presentationHandle,
        out NativeWindowHandle nativeHandle)
    {
        nativeHandle = NativeWindowHandle.Empty;
        if (!TryResolveEntry(presentationHandle, out INativeWindowOwner? owner, out Entry? entry)) return false;

        NativeWindowHandle resolvedHandle = owner.NativeHandle;
        // Revalidate immediately after the native getter, before invoking the
        // old provider again. A valid numeric handle is not a lifetime lease.
        if (!ValidateEntry(presentationHandle, entry, true) || !resolvedHandle.IsValid) return false;
        bool isAlive = owner.IsAlive;
        if (!ValidateEntry(presentationHandle, entry, isAlive)) return false;
        nativeHandle = resolvedHandle;
        return true;
    }

    private static bool ValidateEntry(nint presentationHandle, Entry entry, bool isAlive)
    {
        lock (s_sync)
        {
            if (!s_owners.TryGetValue(presentationHandle, out Entry? current) || !ReferenceEquals(current, entry))
                return false;
            if (!isAlive) s_owners.Remove(presentationHandle);
            return isAlive;
        }
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
