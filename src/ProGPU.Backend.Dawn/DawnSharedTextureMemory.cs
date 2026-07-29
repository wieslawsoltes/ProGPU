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

    public static FeatureName SharedTextureMemoryAHardwareBuffer =>
        (FeatureName)(DawnNativeEnumBase + 30);

    public static FeatureName SharedTextureMemoryDmaBuf =>
        (FeatureName)(DawnNativeEnumBase + 31);

    public static FeatureName SharedTextureMemoryDXGISharedHandle =>
        (FeatureName)(DawnNativeEnumBase + 34);

    public static FeatureName SharedTextureMemoryD3D11Texture2D =>
        (FeatureName)(DawnNativeEnumBase + 35);

    public static FeatureName SharedTextureMemoryIOSurface =>
        (FeatureName)(DawnNativeEnumBase + 36);

    public static FeatureName SharedFenceDXGISharedHandle =>
        (FeatureName)(DawnNativeEnumBase + 41);

    public static FeatureName SharedFenceMTLSharedEvent =>
        (FeatureName)(DawnNativeEnumBase + 42);

    public static FeatureName SharedFenceSyncFD =>
        (FeatureName)(DawnNativeEnumBase + 39);

    public static bool SupportsDxgiSharedHandle(Adapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return adapter.HasFeature(
            SharedTextureMemoryDXGISharedHandle);
    }

    public static bool SupportsAHardwareBuffer(Adapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return adapter.HasFeature(
            SharedTextureMemoryAHardwareBuffer);
    }

    public static bool SupportsDmaBuf(Adapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return adapter.HasFeature(SharedTextureMemoryDmaBuf);
    }

    public static bool SupportsAndroidGpuEncoderInterop(
        Adapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return adapter.HasFeature(
                   SharedTextureMemoryAHardwareBuffer) &&
               adapter.HasFeature(SharedFenceSyncFD);
    }

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

/// <summary>
/// Owns a duplicated POSIX sync-file descriptor exported by one Dawn
/// end-access operation.
/// </summary>
/// <remarks>
/// Dawn owns the descriptor returned by <c>SharedFenceExportInfo</c> and
/// closes it when the end-access state is freed. ProGPU duplicates it before
/// freeing that state. The caller may transfer the duplicate to EGL with
/// <see cref="DetachHandle"/>; otherwise <see cref="Dispose"/> closes it.
/// </remarks>
public sealed class DawnSyncFdEndAccessResult : IDisposable
{
    private int _handle = -1;

    public bool Initialized { get; private set; }

    public int Handle => Volatile.Read(ref _handle);

    public bool HasFence => Handle >= 0;

    public ulong SignaledValue { get; private set; }

    public int DetachHandle()
    {
        int handle = Interlocked.Exchange(ref _handle, -1);
        if (handle < 0)
        {
            throw new InvalidOperationException(
                "The end-access result does not own a sync-file descriptor.");
        }

        return handle;
    }

    public void Dispose()
    {
        int handle = Interlocked.Exchange(ref _handle, -1);
        if (handle >= 0)
        {
            PosixFileDescriptor.Close(handle);
        }
    }

    internal void Set(
        bool initialized,
        int handle,
        ulong signaledValue)
    {
        int previous = Interlocked.Exchange(
            ref _handle,
            handle);
        if (previous >= 0 && previous != handle)
        {
            PosixFileDescriptor.Close(previous);
        }

        Initialized = initialized;
        SignaledValue = signaledValue;
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

    public DawnSharedTextureMemory ImportAHardwareBuffer(
        nint hardwareBuffer)
    {
        if (hardwareBuffer == 0)
        {
            throw new ArgumentException(
                "A valid AHardwareBuffer is required.",
                nameof(hardwareBuffer));
        }

        var hardwareBufferDescriptor =
            new DawnSharedTextureMemoryAHardwareBufferDescriptorNative
            {
                Chain = new ChainedStruct
                {
                    SType =
                        SType
                            .SharedTextureMemoryAHardwareBufferDescriptor
                },
                Handle = hardwareBuffer
            };
        var descriptor = new DawnSharedTextureMemoryDescriptorNative
        {
            NextInChain = &hardwareBufferDescriptor.Chain,
            Label = StringViewFFI.NullValue
        };
        nint memory =
            DawnNativeSharedTextureMemory
                .DeviceImportSharedTextureMemory(
                    _device,
                    &descriptor);
        if (memory == 0)
        {
            throw new InvalidOperationException(
                "Dawn could not import the AHardwareBuffer.");
        }

        return new DawnSharedTextureMemory(memory);
    }

    public DawnSharedTextureMemory ImportDmaBuf(
        uint width,
        uint height,
        in ProGpuDmaBufDescriptor dmaBuf)
    {
        if (width == 0 || height == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "DMA-BUF dimensions must be nonzero.");
        }
        if (dmaBuf.PlaneCount is 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dmaBuf),
                "DMA-BUF images require between one and four planes.");
        }

        DawnSharedTextureMemoryDmaBufPlaneNative* planes =
            stackalloc DawnSharedTextureMemoryDmaBufPlaneNative[
                checked((int)dmaBuf.PlaneCount)];
        for (int index = 0;
             index < checked((int)dmaBuf.PlaneCount);
             index++)
        {
            ProGpuDmaBufPlane source = dmaBuf.GetPlane(index);
            if (source.FileDescriptor < 0 || source.Stride == 0)
            {
                throw new ArgumentException(
                    "Every DMA-BUF plane requires a valid file descriptor and stride.",
                    nameof(dmaBuf));
            }
            planes[index] =
                new DawnSharedTextureMemoryDmaBufPlaneNative
                {
                    FileDescriptor = source.FileDescriptor,
                    Offset = source.Offset,
                    Stride = source.Stride
                };
        }

        var dmaBufDescriptor =
            new DawnSharedTextureMemoryDmaBufDescriptorNative
            {
                Chain = new ChainedStruct
                {
                    SType =
                        SType.SharedTextureMemoryDmaBufDescriptor
                },
                Size = new Extent3D
                {
                    Width = width,
                    Height = height,
                    DepthOrArrayLayers = 1
                },
                DrmFormat = dmaBuf.DrmFormat,
                DrmModifier = dmaBuf.DrmModifier,
                PlaneCount = dmaBuf.PlaneCount,
                Planes = planes
            };
        var descriptor = new DawnSharedTextureMemoryDescriptorNative
        {
            NextInChain = &dmaBufDescriptor.Chain,
            Label = StringViewFFI.NullValue
        };
        nint memory =
            DawnNativeSharedTextureMemory
                .DeviceImportSharedTextureMemory(
                    _device,
                    &descriptor);
        if (memory == 0)
        {
            throw new InvalidOperationException(
                "Dawn could not import the DMA-BUF image.");
        }

        return new DawnSharedTextureMemory(memory);
    }

    public DawnSharedTextureMemory ImportDXGISharedHandle(
        nint sharedHandle,
        bool useKeyedMutex = false)
    {
        if (sharedHandle == 0)
        {
            throw new ArgumentException(
                "A valid DXGI shared HANDLE is required.",
                nameof(sharedHandle));
        }

        var dxgiDescriptor =
            new DawnSharedTextureMemoryDXGISharedHandleDescriptorNative
            {
                Chain = new ChainedStruct
                {
                    SType =
                        SType
                            .SharedTextureMemoryDXGISharedHandleDescriptor
                },
                Handle = sharedHandle,
                UseKeyedMutex = useKeyedMutex
            };
        var descriptor = new DawnSharedTextureMemoryDescriptorNative
        {
            NextInChain = &dxgiDescriptor.Chain,
            Label = StringViewFFI.NullValue
        };
        nint memory =
            DawnNativeSharedTextureMemory
                .DeviceImportSharedTextureMemory(
                    _device,
                    &descriptor);
        if (memory == 0)
        {
            throw new InvalidOperationException(
                "Dawn could not import the DXGI shared handle.");
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

    /// <summary>
    /// Imports one caller-owned POSIX sync-file descriptor. Dawn duplicates
    /// the descriptor; ownership of <paramref name="syncFd"/> remains with
    /// the caller.
    /// </summary>
    public DawnSharedFence ImportSyncFd(int syncFd)
    {
        if (syncFd < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(syncFd),
                "A valid sync-file descriptor is required.");
        }

        var syncFdDescriptor =
            new DawnSharedFenceSyncFDDescriptorNative
            {
                Chain = new ChainedStruct
                {
                    SType = SType.SharedFenceSyncFDDescriptor
                },
                Handle = syncFd
            };
        var descriptor = new DawnSharedFenceDescriptorNative
        {
            NextInChain = &syncFdDescriptor.Chain,
            Label = StringViewFFI.NullValue
        };
        nint fence =
            DawnNativeSharedTextureMemory.DeviceImportSharedFence(
                _device,
                &descriptor);
        if (fence == 0)
        {
            throw new InvalidOperationException(
                "Dawn could not import the sync-file fence.");
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
    /// Ends read access after the target queue is idle when the native producer
    /// is synchronized by allocation ownership rather than a shared fence.
    /// </summary>
    public void EndAccess(TextureHandle texture)
    {
        if (texture == TextureHandle.Null)
        {
            throw new ArgumentException(
                "A live Dawn texture is required.",
                nameof(texture));
        }
        var state =
            new DawnSharedTextureMemoryEndAccessStateNative();
        try
        {
            ThrowIfError(
                DawnNativeSharedTextureMemory.SharedTextureMemoryEndAccess(
                    GetHandle(),
                    texture,
                    &state),
                "end shared texture access");
        }
        finally
        {
            DawnNativeSharedTextureMemory
                .SharedTextureMemoryEndAccessStateFreeMembers(state);
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

    /// <summary>
    /// Ends access and duplicates the binary sync-file fence returned by
    /// Dawn. The duplicate remains valid after Dawn's returned arrays and
    /// shared-fence references are released.
    /// </summary>
    public DawnSyncFdEndAccessResult EndAccessAndExportSyncFd(
        TextureHandle texture)
    {
        var result = new DawnSyncFdEndAccessResult();
        try
        {
            EndAccessAndExportSyncFd(texture, result);
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
    public void EndAccessAndExportSyncFd(
        TextureHandle texture,
        DawnSyncFdEndAccessResult destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (texture == TextureHandle.Null)
        {
            throw new ArgumentException(
                "A live Dawn texture is required.",
                nameof(texture));
        }

        var state =
            new DawnSharedTextureMemoryEndAccessStateNative();
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
                    -1,
                    0);
                return;
            }
            if (state.FenceCount != 1 ||
                state.Fences == null ||
                state.SignaledValues == null)
            {
                throw new NotSupportedException(
                    "ProGPU currently requires one binary sync-file fence per shared texture access.");
            }

            var syncFdInfo =
                new DawnSharedFenceSyncFDExportInfoNative
                {
                    Chain = new DawnChainedStructOut
                    {
                        SType = SType.SharedFenceSyncFDExportInfo
                    },
                    Handle = -1
                };
            var exportInfo =
                new DawnSharedFenceExportInfoNative
                {
                    NextInChain = &syncFdInfo.Chain
                };
            DawnNativeSharedTextureMemory.SharedFenceExportInfo(
                state.Fences[0],
                &exportInfo);
            if (exportInfo.Type != DawnSharedFenceType.SyncFD ||
                syncFdInfo.Handle < 0)
            {
                throw new InvalidOperationException(
                    "Dawn did not export a sync-file fence.");
            }
            if (state.SignaledValues[0] != 1)
            {
                throw new InvalidOperationException(
                    "A sync-file fence must use the binary signal value 1.");
            }

            int ownedHandle =
                PosixFileDescriptor.Duplicate(syncFdInfo.Handle);
            destination.Set(
                state.Initialized,
                ownedHandle,
                state.SignaledValues[0]);
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
internal struct DawnSharedTextureMemoryAHardwareBufferDescriptorNative
{
    public ChainedStruct Chain;
    public nint Handle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DawnSharedTextureMemoryDmaBufPlaneNative
{
    public int FileDescriptor;
    public ulong Offset;
    public uint Stride;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DawnSharedTextureMemoryDmaBufDescriptorNative
{
    public ChainedStruct Chain;
    public Extent3D Size;
    public uint DrmFormat;
    public ulong DrmModifier;
    public nuint PlaneCount;
    public DawnSharedTextureMemoryDmaBufPlaneNative* Planes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DawnSharedTextureMemoryDXGISharedHandleDescriptorNative
{
    public ChainedStruct Chain;
    public nint Handle;
    public WebGPUBool UseKeyedMutex;
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
internal struct DawnSharedFenceSyncFDDescriptorNative
{
    public ChainedStruct Chain;
    public int Handle;
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

[StructLayout(LayoutKind.Sequential)]
internal struct DawnSharedFenceSyncFDExportInfoNative
{
    public DawnChainedStructOut Chain;
    public int Handle;
}

internal static partial class PosixFileDescriptor
{
    private const string LibC = "libc";

    [LibraryImport(
        LibC,
        EntryPoint = "dup",
        SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int DuplicateNative(int descriptor);

    [LibraryImport(
        LibC,
        EntryPoint = "close",
        SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int CloseNative(int descriptor);

    internal static int Duplicate(int descriptor)
    {
        int duplicate = DuplicateNative(descriptor);
        if (duplicate < 0)
        {
            throw new InvalidOperationException(
                $"Could not duplicate the sync-file descriptor: errno {Marshal.GetLastPInvokeError()}.");
        }

        return duplicate;
    }

    internal static void Close(int descriptor)
    {
        _ = CloseNative(descriptor);
    }
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
