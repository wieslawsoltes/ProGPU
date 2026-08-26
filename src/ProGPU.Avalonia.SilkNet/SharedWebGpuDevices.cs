using System;
using System.Collections.Generic;
using ProGPU.Backend;
using Silk.NET.Windowing;

namespace Avalonia.SilkNet;

/// <summary>
/// Creates one presentation surface per native window while sharing a WebGPU
/// device domain across healthy windows.
/// </summary>
internal static class SharedWebGpuDevices
{
    private static readonly object s_gate = new();
    private static readonly List<WeakReference<WgpuContext>> s_contexts = [];

    internal static WgpuContext Create(IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (s_gate)
        {
            WgpuContext context = CreateCore(window);
            if (context.IsDeviceLost)
            {
                context.Dispose();
                context = CreateCore(window);
            }
            s_contexts.Add(new WeakReference<WgpuContext>(context));
            return context;
        }
    }

    private static WgpuContext CreateCore(IWindow window)
    {
        var context = new WgpuContext();
        WgpuContext? owner = FindHealthyContext();
        if (owner is not null && SharingEnabled())
            context.InitializeSharedDevice(window, owner);
        else
            context.Initialize(window);
        return context;
    }

    internal static void Release(WgpuContext? context)
    {
        if (context is null)
            return;

        lock (s_gate)
        {
            for (int index = s_contexts.Count - 1; index >= 0; index--)
            {
                if (!s_contexts[index].TryGetTarget(out WgpuContext? candidate) ||
                    ReferenceEquals(candidate, context))
                {
                    s_contexts.RemoveAt(index);
                }
            }
        }

        context.Dispose();
    }

    internal static WgpuContext? FindHealthyContext()
    {
        for (int index = s_contexts.Count - 1; index >= 0; index--)
        {
            if (!s_contexts[index].TryGetTarget(out WgpuContext? context) ||
                context.IsDisposed)
            {
                s_contexts.RemoveAt(index);
                continue;
            }

            if (!context.IsDeviceLost)
                return context;
        }

        WgpuContext? current = WgpuContext.Current;
        if (IsHealthy(current))
            return current;

        if (WgpuContext.TryGetFirstActiveContext(
                out WgpuContext? active) &&
            IsHealthy(active))
        {
            return active;
        }

        return null;
    }

    private static bool IsHealthy(WgpuContext? context) =>
        context is
        {
            IsInitialized: true,
            IsDisposed: false,
            IsDeviceLost: false
        };

    private static bool SharingEnabled() =>
        !string.Equals(
            Environment.GetEnvironmentVariable(
                "PROGPU_AVALONIA_SHARE_WGPU_DEVICE"),
            "0",
            StringComparison.Ordinal);
}
