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
        var context = new WgpuContext();
        lock (s_gate)
        {
            WgpuContext? owner = FindHealthyContext();
            if (owner is not null && SharingEnabled())
                context.InitializeSharedDevice(window, owner);
            else
                context.Initialize(window);
            s_contexts.Add(new WeakReference<WgpuContext>(context));
        }

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

    private static WgpuContext? FindHealthyContext()
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

        return null;
    }

    private static bool SharingEnabled() =>
        !string.Equals(
            Environment.GetEnvironmentVariable(
                "PROGPU_AVALONIA_SHARE_WGPU_DEVICE"),
            "0",
            StringComparison.Ordinal);
}
