using System;
using System.Runtime.InteropServices;

namespace ProGPU.Backend;

public readonly record struct WgpuRegistrySnapshot(
    ulong AllocatedSlots,
    ulong KeptFromUser,
    ulong ReleasedFromUser,
    ulong ErrorSlots,
    ulong ElementSize)
{
    internal static WgpuRegistrySnapshot FromNative(WgpuRegistryReportNative value)
        => new(
            checked((ulong)value.NumAllocated),
            checked((ulong)value.NumKeptFromUser),
            checked((ulong)value.NumReleasedFromUser),
            checked((ulong)value.NumError),
            checked((ulong)value.ElementSize));
}

public readonly record struct WgpuNativeResourceSnapshot(
    WgpuRegistrySnapshot CommandBuffers,
    WgpuRegistrySnapshot Buffers,
    WgpuRegistrySnapshot Textures,
    WgpuRegistrySnapshot TextureViews,
    WgpuRegistrySnapshot BindGroups,
    WgpuRegistrySnapshot BindGroupLayouts,
    WgpuRegistrySnapshot ShaderModules,
    WgpuRegistrySnapshot RenderPipelines,
    WgpuRegistrySnapshot ComputePipelines,
    ulong MetalAllocatedBytes)
{
    /// <summary>
    /// Gets whether <see cref="MetalAllocatedBytes"/> was obtained from the
    /// system-default Metal device. A zero byte value is otherwise ambiguous.
    /// </summary>
    public bool MetalAllocatedBytesAvailable { get; init; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct WgpuRegistryReportNative
{
    public nuint NumAllocated;
    public nuint NumKeptFromUser;
    public nuint NumReleasedFromUser;
    public nuint NumError;
    public nuint ElementSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WgpuHubReportNative
{
    public WgpuRegistryReportNative Adapters;
    public WgpuRegistryReportNative Devices;
    public WgpuRegistryReportNative Queues;
    public WgpuRegistryReportNative PipelineLayouts;
    public WgpuRegistryReportNative ShaderModules;
    public WgpuRegistryReportNative BindGroupLayouts;
    public WgpuRegistryReportNative BindGroups;
    public WgpuRegistryReportNative CommandBuffers;
    public WgpuRegistryReportNative RenderBundles;
    public WgpuRegistryReportNative RenderPipelines;
    public WgpuRegistryReportNative ComputePipelines;
    public WgpuRegistryReportNative QuerySets;
    public WgpuRegistryReportNative Buffers;
    public WgpuRegistryReportNative Textures;
    public WgpuRegistryReportNative TextureViews;
    public WgpuRegistryReportNative Samplers;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WgpuGlobalReportNative
{
    public WgpuRegistryReportNative Surfaces;
    public uint BackendType;
    private uint _backendTypePadding;
    public WgpuHubReportNative Vulkan;
    public WgpuHubReportNative Metal;
    public WgpuHubReportNative Dx12;
    public WgpuHubReportNative Gl;
}

internal static class MacMetalMemory
{
    private const string MetalLibrary =
        "/System/Library/Frameworks/Metal.framework/Versions/A/Metal";
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";

    public static bool TryGetCurrentAllocatedBytes(out ulong bytes)
    {
        bytes = 0;
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        IntPtr device = MTLCreateSystemDefaultDevice();
        if (device == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            IntPtr selector = sel_registerName("currentAllocatedSize");
            if (selector == IntPtr.Zero)
            {
                return false;
            }

            bytes = checked((ulong)objc_msgSend_nuint(device, selector));
            return true;
        }
        finally
        {
            objc_release(device);
        }
    }

    [DllImport(MetalLibrary)]
    private static extern IntPtr MTLCreateSystemDefaultDevice();

    [DllImport(ObjectiveCLibrary)]
    private static extern IntPtr sel_registerName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nuint objc_msgSend_nuint(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveCLibrary)]
    private static extern void objc_release(IntPtr value);
}
