using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using WgpuBuffer = Silk.NET.WebGPU.Buffer;

namespace ProGPU.Backend;

/// <summary>
/// A bounded ring of persistently reused MAP_WRITE/COPY_SRC transfer buffers.
/// </summary>
/// <remarks>
/// CPU writes and map completion are O(B) and O(1), respectively, for B uploaded
/// bytes. Retained GPU transfer storage is O(C * N), where C is slot capacity and
/// N is the fixed slot count. No managed object is allocated per upload or remap.
/// </remarks>
public unsafe sealed class GpuMappedUploadBufferRing : IDisposable
{
    private const int Unavailable = 0;
    private const int Mapped = 1;
    private const int Mapping = 2;
    private const int Failed = 3;

    private sealed class Slot
    {
        internal WgpuBuffer* Buffer;
        internal GCHandle CallbackHandle;
        internal int State;
    }

    private readonly WgpuContext _context;
    private readonly Slot[] _slots;
    private readonly uint _capacity;
    private int _nextSlot;
    private Slot? _pendingSubmission;
    private bool _disposed;

    public GpuMappedUploadBufferRing(
        WgpuContext context,
        uint capacity,
        int slotCount = 3,
        string label = "ProGPU Mapped Upload Ring")
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.BackendKind == WgpuBackendKind.BrowserWebGpu)
        {
            throw new NotSupportedException(
                "Mapped upload rings are currently available on native WebGPU backends.");
        }
        if (capacity == 0 || (capacity & 3u) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                "Mapped upload capacity must be non-zero and 4-byte aligned.");
        }
        if (slotCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(slotCount));
        }

        GpuBuffer.ValidateAndAlignAllocationSize(
            capacity,
            context.MaxBufferSize,
            label);

        _context = context;
        _capacity = capacity;
        _slots = new Slot[slotCount];
        nint labelPointer = SilkMarshal.StringToPtr(label);
        try
        {
            for (int index = 0; index < slotCount; index++)
            {
                var descriptor = new BufferDescriptor
                {
                    Label = (byte*)labelPointer,
                    Size = capacity,
                    Usage = BufferUsage.MapWrite | BufferUsage.CopySrc,
                    MappedAtCreation = true
                };
                WgpuBuffer* buffer =
                    context.Api.DeviceCreateBuffer(context.Device, &descriptor);
                if (buffer == null)
                {
                    throw new InvalidOperationException(
                        $"Failed to allocate mapped upload slot {index}.");
                }

                var slot = new Slot
                {
                    Buffer = buffer,
                    State = Mapped
                };
                _slots[index] = slot;
                slot.CallbackHandle = GCHandle.Alloc(slot);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            SilkMarshal.Free(labelPointer);
        }
    }

    public uint Capacity => _capacity;

    public ulong AllocatedBytes =>
        checked((ulong)_capacity * (ulong)_slots.Length);

    public int SlotCount => _slots.Length;

    public bool TryWrite(
        ReadOnlySpan<byte> data,
        out WgpuBuffer* sourceBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingSubmission != null)
        {
            throw new InvalidOperationException(
                "The previous mapped upload must be submitted before another write.");
        }
        if (data.IsEmpty || data.Length > _capacity)
        {
            sourceBuffer = null;
            return false;
        }

        for (int pass = 0; pass < 2; pass++)
        {
            for (int attempt = 0; attempt < _slots.Length; attempt++)
            {
                int index = (_nextSlot + attempt) % _slots.Length;
                Slot slot = _slots[index];
                if (Interlocked.CompareExchange(
                        ref slot.State,
                        Unavailable,
                        Mapped) != Mapped)
                {
                    continue;
                }

                void* mapped = _context.Api.BufferGetMappedRange(
                    slot.Buffer,
                    0,
                    checked((nuint)data.Length));
                if (mapped == null)
                {
                    _context.Api.BufferUnmap(slot.Buffer);
                    BeginMap(slot);
                    continue;
                }

                data.CopyTo(new Span<byte>(mapped, data.Length));
                _context.Api.BufferUnmap(slot.Buffer);
                _pendingSubmission = slot;
                _nextSlot = (index + 1) % _slots.Length;
                sourceBuffer = slot.Buffer;
                return true;
            }

            if (pass == 0)
            {
                // A map completion can become ready after the frame-end poll.
                // Process callbacks once more before allocating queue staging.
                _context.PollDevice(wait: false);
            }
        }

        sourceBuffer = null;
        return false;
    }

    public void RecallAfterSubmit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Slot? slot = _pendingSubmission;
        if (slot == null)
        {
            return;
        }

        _pendingSubmission = null;
        BeginMap(slot);
    }

    private void BeginMap(Slot slot)
    {
        Volatile.Write(ref slot.State, Mapping);
        try
        {
            _context.Api.BufferMapAsync(
                slot.Buffer,
                MapMode.Write,
                0,
                _capacity,
                new PfnBufferMapCallback(&OnMapped),
                (void*)GCHandle.ToIntPtr(slot.CallbackHandle));
        }
        catch
        {
            Volatile.Write(ref slot.State, Failed);
        }
    }

    [UnmanagedCallersOnly(CallConvs =
        new[] { typeof(CallConvCdecl) })]
    private static void OnMapped(
        BufferMapAsyncStatus status,
        void* userData)
    {
        var handle = GCHandle.FromIntPtr((nint)userData);
        if (handle.Target is Slot slot)
        {
            Volatile.Write(
                ref slot.State,
                status == BufferMapAsyncStatus.Success
                    ? Mapped
                    : Failed);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_context.IsDisposed)
        {
            _context.PollDevice(wait: true);
        }

        for (int index = 0; index < _slots.Length; index++)
        {
            Slot? slot = _slots[index];
            if (slot == null)
            {
                continue;
            }

            if (!_context.IsDisposed && slot.Buffer != null)
            {
                if (Volatile.Read(ref slot.State) == Mapped)
                {
                    _context.Api.BufferUnmap(slot.Buffer);
                }
                _context.Api.BufferDestroy(slot.Buffer);
                _context.Api.BufferRelease(slot.Buffer);
                slot.Buffer = null;
            }

            if (slot.CallbackHandle.IsAllocated)
            {
                slot.CallbackHandle.Free();
            }
        }

        _pendingSubmission = null;
    }
}
