using System;
using System.Collections.Generic;

namespace ProGPU.Backend;

/// <summary>
/// Presents a GPU-backed drawing surface associated with a CPU framebuffer
/// address.
/// </summary>
public interface IGpuFramebufferPresenter
{
    void Present(WgpuContext context, IntPtr surfaceHandle);
}

/// <summary>
/// Connects a framebuffer-style platform API to a GPU-backed compatibility
/// surface without exposing the compatibility surface type to the platform.
/// </summary>
public static class GpuFramebufferPresentationRegistry
{
    private static readonly object s_sync = new();
    private static readonly Dictionary<IntPtr, WeakReference<IGpuFramebufferPresenter>> s_presenters = new();

    public static void Register(IntPtr framebufferAddress, IGpuFramebufferPresenter presenter)
    {
        if (framebufferAddress == IntPtr.Zero)
        {
            throw new ArgumentException("A framebuffer presentation address must be non-zero.", nameof(framebufferAddress));
        }

        ArgumentNullException.ThrowIfNull(presenter);
        lock (s_sync)
        {
            s_presenters[framebufferAddress] = new WeakReference<IGpuFramebufferPresenter>(presenter);
        }
    }

    public static void Unregister(IntPtr framebufferAddress, IGpuFramebufferPresenter presenter)
    {
        if (framebufferAddress == IntPtr.Zero)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(presenter);
        lock (s_sync)
        {
            if (s_presenters.TryGetValue(framebufferAddress, out var weak)
                && weak.TryGetTarget(out var registered)
                && ReferenceEquals(registered, presenter))
            {
                s_presenters.Remove(framebufferAddress);
            }
        }
    }

    public static bool TryPresent(IntPtr framebufferAddress, IntPtr surfaceHandle)
    {
        if (framebufferAddress == IntPtr.Zero || surfaceHandle == IntPtr.Zero)
        {
            return false;
        }

        IGpuFramebufferPresenter? presenter;
        lock (s_sync)
        {
            if (!s_presenters.TryGetValue(framebufferAddress, out var weak)
                || !weak.TryGetTarget(out presenter))
            {
                s_presenters.Remove(framebufferAddress);
                return false;
            }
        }

        var context = ResolveContext(surfaceHandle);
        if (context is null)
        {
            return false;
        }

        presenter.Present(context, surfaceHandle);
        return true;
    }

    private static WgpuContext? ResolveContext(IntPtr surfaceHandle)
    {
        var current = WgpuContext.Current;
        if (MatchesSurface(current, surfaceHandle))
        {
            return current;
        }

        var activeContexts = WgpuContext.ActiveContexts;
        for (var index = 0; index < activeContexts.Count; index++)
        {
            if (MatchesSurface(activeContexts[index], surfaceHandle))
            {
                return activeContexts[index];
            }
        }

        return null;
    }

    private static unsafe bool MatchesSurface(WgpuContext? context, IntPtr surfaceHandle)
    {
        return context is { IsDisposed: false }
               && (IntPtr)context.Surface == surfaceHandle;
    }
}
