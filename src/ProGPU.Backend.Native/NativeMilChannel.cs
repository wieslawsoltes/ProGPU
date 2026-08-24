using System.Runtime.CompilerServices;

namespace ProGPU.Backend.Native;

/// <summary>
/// Owns one transactional C++ channel for canonical WPF DUCE/MIL batches.
/// </summary>
/// <remarks>
/// The channel retains only native protocol state and is independent of a GPU
/// device. Select the module that will later own the semantic compositor so
/// protocol and renderer binaries are guaranteed to use the same native ABI.
/// Unsupported commands fail without partially mutating the channel.
/// </remarks>
public sealed unsafe class NativeMilChannel : IDisposable
{
    private readonly NativeMilBackend _backend;
    private nint _channel;
    private int _disposeState;

    public NativeMilChannel(NativeMilBackend backend = NativeMilBackend.WgpuNative)
    {
        nint channel = 0;
        NativeMilStatus status = backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.Create(&channel)
            : NativeMilMethods.Create(&channel);
        if (status != NativeMilStatus.Success || channel == 0)
        {
            throw new NativeMilException(
                status,
                "The ProGPU native MIL channel could not be created.");
        }
        _backend = backend;
        _channel = channel;
    }

    public NativeMilBackend Backend => _backend;

    public bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    public nuint ResourceCount
    {
        get
        {
            nint channel = GetChannel();
            return _backend == NativeMilBackend.Dawn
                ? NativeMilDawnMethods.GetResourceCount(channel)
                : NativeMilMethods.GetResourceCount(channel);
        }
    }

    public NativeMilBatchMetrics Apply(ReadOnlySpan<byte> batch)
    {
        nint channel = GetChannel();
        var metrics = new NativeMilMethods.BatchMetrics
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMilMethods.BatchMetrics>()
        };
        fixed (byte* batchPointer = batch)
        {
            NativeMilStatus status = _backend == NativeMilBackend.Dawn
                ? NativeMilDawnMethods.Apply(
                    channel, batchPointer, (nuint)batch.Length, &metrics)
                : NativeMilMethods.Apply(
                    channel, batchPointer, (nuint)batch.Length, &metrics);
            if (status != NativeMilStatus.Success)
            {
                throw new NativeMilException(
                    status,
                    $"The MIL batch was rejected after {metrics.CommandCount} command(s) ({metrics.TotalBytes} bytes).");
            }
        }
        return new NativeMilBatchMetrics(
            metrics.CommandCount,
            metrics.SupportedCommandCount,
            metrics.UnsupportedCommandCount,
            metrics.CreatedResourceCount,
            metrics.DeletedResourceCount,
            metrics.UpdatedResourceCount,
            metrics.TotalBytes);
    }

    public bool HasResource(uint handle)
    {
        nint channel = GetChannel();
        return (_backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.HasResource(channel, handle)
            : NativeMilMethods.HasResource(channel, handle)) != 0;
    }

    public uint GetResourceType(uint handle)
    {
        nint channel = GetChannel();
        return _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.GetResourceType(channel, handle)
            : NativeMilMethods.GetResourceType(channel, handle);
    }

    public ulong GetResourceGeneration(uint handle)
    {
        nint channel = GetChannel();
        return _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.GetResourceGeneration(channel, handle)
            : NativeMilMethods.GetResourceGeneration(channel, handle);
    }

    public bool TryGetVisual(uint handle, out NativeMilVisualSnapshot snapshot)
    {
        nint channel = GetChannel();
        var native = new NativeMilMethods.VisualSnapshot
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMilMethods.VisualSnapshot>()
        };
        byte found = _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.GetVisual(channel, handle, &native)
            : NativeMilMethods.GetVisual(channel, handle, &native);
        snapshot = found == 0
            ? default
            : new NativeMilVisualSnapshot(
                native.Handle,
                native.OffsetX,
                native.OffsetY,
                native.Opacity,
                native.ContentHandle,
                native.ChildCount);
        return found != 0;
    }

    public bool TryGetVisualChild(uint handle, uint index, out uint childHandle)
    {
        nint channel = GetChannel();
        uint child = 0;
        byte found = _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.GetVisualChild(channel, handle, index, &child)
            : NativeMilMethods.GetVisualChild(channel, handle, index, &child);
        childHandle = child;
        return found != 0;
    }

    public bool TryGetTarget(uint handle, out NativeMilTargetSnapshot snapshot)
    {
        nint channel = GetChannel();
        var native = new NativeMilMethods.TargetSnapshot
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMilMethods.TargetSnapshot>()
        };
        byte found = _backend == NativeMilBackend.Dawn
            ? NativeMilDawnMethods.GetTarget(channel, handle, &native)
            : NativeMilMethods.GetTarget(channel, handle, &native);
        snapshot = found == 0
            ? default
            : new NativeMilTargetSnapshot(
                native.Handle,
                native.RootHandle,
                native.ClearRed,
                native.ClearGreen,
                native.ClearBlue,
                native.ClearAlpha,
                native.Flags);
        return found != 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }
        nint channel = Interlocked.Exchange(ref _channel, 0);
        if (channel != 0)
        {
            if (_backend == NativeMilBackend.Dawn)
            {
                NativeMilDawnMethods.Destroy(channel);
            }
            else
            {
                NativeMilMethods.Destroy(channel);
            }
        }
        GC.SuppressFinalize(this);
    }

    private nint GetChannel()
    {
        nint channel = Volatile.Read(ref _channel);
        ObjectDisposedException.ThrowIf(channel == 0, this);
        return channel;
    }
}
