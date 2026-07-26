using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using WebGpuSharp;
using WebGpuSharp.FFI;
using WebGpuSharp.Marshalling;

namespace ProGPU.Backend.Dawn;

/// <summary>
/// Dawn-native feature values generated from the same native extension
/// namespace as the packaged <c>webgpu_dawn</c> binary.
/// </summary>
public static class DawnSharedTextureMemoryFeatures
{
    private const int DawnNativeEnumBase = 0x0005_0000;

    public static FeatureName SharedTextureMemoryIOSurface =>
        (FeatureName)(DawnNativeEnumBase + 36);

    public static FeatureName SharedFenceMTLSharedEvent =>
        (FeatureName)(DawnNativeEnumBase + 42);

    public static bool SupportsMacPresentation(Adapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return adapter.HasFeature(SharedTextureMemoryIOSurface) &&
               adapter.HasFeature(SharedFenceMTLSharedEvent);
    }

    public static int WriteMacPresentationFeatures(
        Adapter adapter,
        Span<FeatureName> destination)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (destination.Length < 2)
        {
            throw new ArgumentException(
                "Two feature slots are required.",
                nameof(destination));
        }
        if (!SupportsMacPresentation(adapter))
        {
            return 0;
        }

        destination[0] = SharedTextureMemoryIOSurface;
        destination[1] = SharedFenceMTLSharedEvent;
        return 2;
    }
}

public readonly record struct DawnSharedTextureMemoryProperties(
    TextureUsage Usage,
    Extent3D Size,
    TextureFormat Format);

/// <summary>
/// Owns the retained Metal shared-event reference exported by one Dawn
/// end-access operation.
/// </summary>
public sealed class DawnMetalEndAccessResult : IDisposable
{
    private nint _sharedEvent;

    public DawnMetalEndAccessResult()
    {
    }

    private void Reset(
        bool initialized,
        nint sharedEvent,
        ulong signaledValue,
        Future commandsScheduledFuture)
    {
        Initialized = initialized;
        SignaledValue = signaledValue;
        CommandsScheduledFuture = commandsScheduledFuture;
        nint previous = Volatile.Read(ref _sharedEvent);
        if (previous == sharedEvent)
        {
            return;
        }

        DawnMetalObjectLifetime.Retain(sharedEvent);
        previous = Interlocked.Exchange(
            ref _sharedEvent,
            sharedEvent);
        DawnMetalObjectLifetime.Release(previous);
    }

    public bool Initialized { get; private set; }
    public nint SharedEvent => Volatile.Read(ref _sharedEvent);
    public ulong SignaledValue { get; private set; }
    public Future CommandsScheduledFuture { get; private set; }

    public void Dispose()
    {
        nint sharedEvent = Interlocked.Exchange(
            ref _sharedEvent,
            0);
        if (sharedEvent != 0)
        {
            DawnMetalObjectLifetime.Release(sharedEvent);
        }
    }

    internal void Set(
        bool initialized,
        nint sharedEvent,
        ulong signaledValue,
        Future commandsScheduledFuture)
    {
        Reset(
            initialized,
            sharedEvent,
            signaledValue,
            commandsScheduledFuture);
    }
}

/// <summary>
/// Typed Dawn-native shared-memory entry point for a device created by the
/// matching WebGPUSharp/Dawn package. Import is O(1), performs no reflection,
/// and never loads or probes symbols dynamically.
/// </summary>
public sealed unsafe class DawnSharedTextureMemoryFeature
{
    private readonly DeviceHandle _device;

    public DawnSharedTextureMemoryFeature(Device device)
        : this(WebGPUMarshal.GetHandle(device))
    {
    }

    public DawnSharedTextureMemoryFeature(DeviceHandle device)
    {
        if (device == DeviceHandle.Null)
        {
            throw new ArgumentException(
                "A live Dawn device is required.",
                nameof(device));
        }

        _device = device;
    }

    public DawnSharedTextureMemory ImportIOSurface(
        nint ioSurface,
        bool allowStorageBinding = false)
    {
        if (ioSurface == 0)
        {
            throw new ArgumentException(
                "A valid IOSurfaceRef is required.",
                nameof(ioSurface));
        }

        var ioSurfaceDescriptor =
            new DawnSharedTextureMemoryIOSurfaceDescriptorNative
            {
                Chain = new ChainedStruct
                {
                    SType = SType.SharedTextureMemoryIOSurfaceDescriptor
                },
                IOSurface = ioSurface,
                AllowStorageBinding = allowStorageBinding
            };
        var descriptor = new DawnSharedTextureMemoryDescriptorNative
        {
            NextInChain = &ioSurfaceDescriptor.Chain,
            Label = StringViewFFI.NullValue
        };
        nint memory = DawnNativeSharedTextureMemory.DeviceImportSharedTextureMemory(
            _device,
            &descriptor);
        if (memory == 0)
        {
            throw new InvalidOperationException(
                "Dawn could not import the IOSurface.");
        }

        return new DawnSharedTextureMemory(memory);
    }

    public DawnSharedFence ImportMetalSharedEvent(nint sharedEvent)
    {
        if (sharedEvent == 0)
        {
            throw new ArgumentException(
                "A valid MTLSharedEvent is required.",
                nameof(sharedEvent));
        }

        var eventDescriptor = new DawnSharedFenceMTLSharedEventDescriptorNative
        {
            Chain = new ChainedStruct
            {
                SType = SType.SharedFenceMTLSharedEventDescriptor
            },
            SharedEvent = sharedEvent
        };
        var descriptor = new DawnSharedFenceDescriptorNative
        {
            NextInChain = &eventDescriptor.Chain,
            Label = StringViewFFI.NullValue
        };
        nint fence = DawnNativeSharedTextureMemory.DeviceImportSharedFence(
            _device,
            &descriptor);
        if (fence == 0)
        {
            throw new InvalidOperationException(
                "Dawn could not import the MTLSharedEvent.");
        }

        return new DawnSharedFence(fence);
    }
}

/// <summary>
/// Owns one Dawn shared-texture-memory reference. Texture creation is O(1).
/// Begin/end access use stack-only descriptors and are allocation-free.
/// </summary>
public sealed unsafe class DawnSharedTextureMemory : IDisposable
{
    private nint _handle;

    internal DawnSharedTextureMemory(nint handle)
    {
        _handle = handle;
    }

    public bool IsDisposed => Volatile.Read(ref _handle) == 0;

    public DawnSharedTextureMemoryProperties GetProperties()
    {
        nint handle = GetHandle();
        var native = new DawnSharedTextureMemoryPropertiesNative();
        ThrowIfError(
            DawnNativeSharedTextureMemory.SharedTextureMemoryGetProperties(
                handle,
                &native),
            "query shared texture memory properties");
        return new DawnSharedTextureMemoryProperties(
            native.Usage,
            native.Size,
            native.Format);
    }

    public TextureHandle CreateTexture(
        TextureUsage usage,
        ReadOnlySpan<byte> utf8Label = default)
    {
        DawnSharedTextureMemoryProperties properties = GetProperties();
        if ((usage & ~properties.Usage) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(usage),
                usage,
                "Texture usage must be a subset of the imported memory usage.");
        }

        fixed (byte* label = utf8Label)
        {
            var descriptor = new TextureDescriptorFFI
            {
                Label = utf8Label.IsEmpty
                    ? StringViewFFI.NullValue
                    : StringViewFFI.CreateExplicitlySized(
                        label,
                        utf8Label.Length),
                Usage = usage,
                Dimension = TextureDimension.D2,
                Size = properties.Size,
                Format = properties.Format,
                MipLevelCount = 1,
                SampleCount = 1
            };
            TextureHandle texture =
                DawnNativeSharedTextureMemory.SharedTextureMemoryCreateTexture(
                    GetHandle(),
                    &descriptor);
            if (texture == TextureHandle.Null)
            {
                throw new InvalidOperationException(
                    "Dawn could not create a texture from the shared memory.");
            }

            return texture;
        }
    }

    public void BeginAccess(
        TextureHandle texture,
        bool initialized,
        DawnSharedFence? waitFence = null,
        ulong waitValue = 0)
    {
        if (texture == TextureHandle.Null)
        {
            throw new ArgumentException(
                "A live Dawn texture is required.",
                nameof(texture));
        }
        nint fenceHandle = waitFence?.GetHandle() ?? 0;
        ulong signaledValue = waitValue;
        var descriptor =
            new DawnSharedTextureMemoryBeginAccessDescriptorNative
            {
                ConcurrentRead = false,
                Initialized = initialized,
                FenceCount = fenceHandle == 0 ? 0u : 1u,
                Fences = fenceHandle == 0 ? null : &fenceHandle,
                SignaledValueCount = fenceHandle == 0 ? 0u : 1u,
                SignaledValues =
                    fenceHandle == 0 ? null : &signaledValue
            };
        ThrowIfError(
            DawnNativeSharedTextureMemory.SharedTextureMemoryBeginAccess(
                GetHandle(),
                texture,
                &descriptor),
            "begin shared texture access");
    }

    public DawnMetalEndAccessResult EndAccessAndExportMetalSharedEvent(
        TextureHandle texture)
    {
        var result = new DawnMetalEndAccessResult();
        try
        {
            EndAccessAndExportMetalSharedEvent(
                texture,
                result);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Ends access and updates a caller-owned reusable result without a
    /// per-frame managed allocation.
    /// </summary>
    public void EndAccessAndExportMetalSharedEvent(
        TextureHandle texture,
        DawnMetalEndAccessResult destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (texture == TextureHandle.Null)
        {
            throw new ArgumentException(
                "A live Dawn texture is required.",
                nameof(texture));
        }
        var metalState =
            new DawnSharedTextureMemoryMetalEndAccessStateNative
            {
                Chain = new DawnChainedStructOut
                {
                    SType = SType.SharedTextureMemoryMetalEndAccessState
                }
            };
        var state = new DawnSharedTextureMemoryEndAccessStateNative
        {
            NextInChain = &metalState.Chain
        };

        ThrowIfError(
            DawnNativeSharedTextureMemory.SharedTextureMemoryEndAccess(
                GetHandle(),
                texture,
                &state),
            "end shared texture access");
        try
        {
            if (state.FenceCount != state.SignaledValueCount)
            {
                throw new InvalidOperationException(
                    "Dawn returned mismatched fence and signal-value counts.");
            }
            if (state.FenceCount == 0)
            {
                destination.Set(
                    state.Initialized,
                    0,
                    0,
                    metalState.CommandsScheduledFuture);
                return;
            }
            if (state.FenceCount != 1 ||
                state.Fences == null ||
                state.SignaledValues == null)
            {
                throw new NotSupportedException(
                    "ProGPU currently requires one Metal timeline fence per shared texture access.");
            }

            nint sharedFence = state.Fences[0];
            var metalInfo = new DawnSharedFenceMTLSharedEventExportInfoNative
            {
                Chain = new DawnChainedStructOut
                {
                    SType = SType.SharedFenceMTLSharedEventExportInfo
                }
            };
            var exportInfo = new DawnSharedFenceExportInfoNative
            {
                NextInChain = &metalInfo.Chain
            };
            DawnNativeSharedTextureMemory.SharedFenceExportInfo(
                sharedFence,
                &exportInfo);
            if (exportInfo.Type != DawnSharedFenceType.MTLSharedEvent ||
                metalInfo.SharedEvent == 0)
            {
                throw new InvalidOperationException(
                    "Dawn did not export an MTLSharedEvent fence.");
            }

            destination.Set(
                state.Initialized,
                metalInfo.SharedEvent,
                state.SignaledValues[0],
                metalState.CommandsScheduledFuture);
        }
        finally
        {
            DawnNativeSharedTextureMemory
                .SharedTextureMemoryEndAccessStateFreeMembers(state);
        }
    }

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            DawnNativeSharedTextureMemory.SharedTextureMemoryRelease(handle);
        }
    }

    private nint GetHandle()
    {
        nint handle = Volatile.Read(ref _handle);
        ObjectDisposedException.ThrowIf(handle == 0, this);
        return handle;
    }

    private static void ThrowIfError(Status status, string operation)
    {
        if (status != Status.Success)
        {
            throw new InvalidOperationException(
                $"Dawn failed to {operation}: {status}.");
        }
    }
}

internal static unsafe partial class DawnMetalObjectLifetime
{
    private const string ObjectiveCLibrary =
        "/usr/lib/libobjc.A.dylib";

    [LibraryImport(
        ObjectiveCLibrary,
        EntryPoint = "objc_retain")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint RetainNative(nint value);

    [LibraryImport(
        ObjectiveCLibrary,
        EntryPoint = "objc_release")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void ReleaseNative(nint value);

    internal static void Retain(nint value)
    {
        if (value != 0)
        {
            _ = RetainNative(value);
        }
    }

    internal static void Release(nint value)
    {
        if (value != 0)
        {
            ReleaseNative(value);
        }
    }
}

/// <summary>
/// Owns one Dawn shared-fence reference imported from an external GPU object.
/// </summary>
public sealed class DawnSharedFence : IDisposable
{
    private nint _handle;

    internal DawnSharedFence(nint handle)
    {
        _handle = handle;
    }

    public bool IsDisposed => Volatile.Read(ref _handle) == 0;

    internal nint GetHandle()
    {
        nint handle = Volatile.Read(ref _handle);
        ObjectDisposedException.ThrowIf(handle == 0, this);
        return handle;
    }

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            DawnNativeSharedTextureMemory.SharedFenceRelease(handle);
        }
    }
}

internal enum DawnSharedFenceType
{
    Undefined = 0,
    VkSemaphoreOpaqueFD = 1,
    SyncFD = 2,
    VkSemaphoreZirconHandle = 3,
    DXGISharedHandle = 4,
    MTLSharedEvent = 5,
    EGLSync = 6
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DawnChainedStructOut
{
    public DawnChainedStructOut* Next;
    public SType SType;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DawnSharedTextureMemoryDescriptorNative
{
    public ChainedStruct* NextInChain;
    public StringViewFFI Label;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DawnSharedTextureMemoryIOSurfaceDescriptorNative
{
    public ChainedStruct Chain;
    public nint IOSurface;
    public WebGPUBool AllowStorageBinding;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DawnSharedTextureMemoryPropertiesNative
{
    public DawnChainedStructOut* NextInChain;
    public TextureUsage Usage;
    public Extent3D Size;
    public TextureFormat Format;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DawnSharedTextureMemoryBeginAccessDescriptorNative
{
    public ChainedStruct* NextInChain;
    public WebGPUBool ConcurrentRead;
    public WebGPUBool Initialized;
    public nuint FenceCount;
    public nint* Fences;
    public nuint SignaledValueCount;
    public ulong* SignaledValues;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DawnSharedTextureMemoryEndAccessStateNative
{
    public DawnChainedStructOut* NextInChain;
    public WebGPUBool Initialized;
    public nuint FenceCount;
    public nint* Fences;
    public nuint SignaledValueCount;
    public ulong* SignaledValues;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DawnSharedTextureMemoryMetalEndAccessStateNative
{
    public DawnChainedStructOut Chain;
    public Future CommandsScheduledFuture;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DawnSharedFenceDescriptorNative
{
    public ChainedStruct* NextInChain;
    public StringViewFFI Label;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DawnSharedFenceMTLSharedEventDescriptorNative
{
    public ChainedStruct Chain;
    public nint SharedEvent;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DawnSharedFenceExportInfoNative
{
    public DawnChainedStructOut* NextInChain;
    public DawnSharedFenceType Type;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DawnSharedFenceMTLSharedEventExportInfoNative
{
    public DawnChainedStructOut Chain;
    public nint SharedEvent;
}

internal static unsafe partial class DawnNativeSharedTextureMemory
{
    private const string LibraryName = "webgpu_dawn";

    [LibraryImport(
        LibraryName,
        EntryPoint = "wgpuDeviceImportSharedTextureMemory")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint DeviceImportSharedTextureMemory(
        DeviceHandle device,
        DawnSharedTextureMemoryDescriptorNative* descriptor);

    [LibraryImport(
        LibraryName,
        EntryPoint = "wgpuDeviceImportSharedFence")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint DeviceImportSharedFence(
        DeviceHandle device,
        DawnSharedFenceDescriptorNative* descriptor);

    [LibraryImport(
        LibraryName,
        EntryPoint = "wgpuSharedTextureMemoryGetProperties")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial Status SharedTextureMemoryGetProperties(
        nint memory,
        DawnSharedTextureMemoryPropertiesNative* properties);

    [LibraryImport(
        LibraryName,
        EntryPoint = "wgpuSharedTextureMemoryCreateTexture")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial TextureHandle SharedTextureMemoryCreateTexture(
        nint memory,
        TextureDescriptorFFI* descriptor);

    [LibraryImport(
        LibraryName,
        EntryPoint = "wgpuSharedTextureMemoryBeginAccess")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial Status SharedTextureMemoryBeginAccess(
        nint memory,
        TextureHandle texture,
        DawnSharedTextureMemoryBeginAccessDescriptorNative* descriptor);

    [LibraryImport(
        LibraryName,
        EntryPoint = "wgpuSharedTextureMemoryEndAccess")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial Status SharedTextureMemoryEndAccess(
        nint memory,
        TextureHandle texture,
        DawnSharedTextureMemoryEndAccessStateNative* state);

    [LibraryImport(
        LibraryName,
        EntryPoint =
            "wgpuSharedTextureMemoryEndAccessStateFreeMembers")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void
        SharedTextureMemoryEndAccessStateFreeMembers(
            DawnSharedTextureMemoryEndAccessStateNative state);

    [LibraryImport(
        LibraryName,
        EntryPoint = "wgpuSharedTextureMemoryRelease")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SharedTextureMemoryRelease(nint memory);

    [LibraryImport(
        LibraryName,
        EntryPoint = "wgpuSharedFenceExportInfo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SharedFenceExportInfo(
        nint fence,
        DawnSharedFenceExportInfoNative* info);

    [LibraryImport(
        LibraryName,
        EntryPoint = "wgpuSharedFenceRelease")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SharedFenceRelease(nint fence);
}
