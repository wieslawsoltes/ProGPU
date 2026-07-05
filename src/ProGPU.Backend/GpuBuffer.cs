using System;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Buffer = Silk.NET.WebGPU.Buffer;

namespace ProGPU.Backend;

public unsafe class GpuBuffer : IDisposable
{
    public const int DefaultMapTimeoutMilliseconds = 30000;

    private readonly WgpuContext _context;
    private byte[]? _partialWriteShadow;
    private bool _hasUnmirroredWrites;
    
    public Buffer* BufferPtr { get; private set; }
    public uint Size { get; private set; }
    public uint AllocatedSize { get; private set; }
    public BufferUsage Usage { get; private set; }
    
    private bool _isDisposed;

    public GpuBuffer(WgpuContext context, uint size, BufferUsage usage, string label = "GpuBuffer")
    {
        _context = context;
        Size = size;
        Usage = usage;
        var allocatedSize = AlignToQueueWriteSize(size);
        AllocatedSize = allocatedSize;

        var labelPtr = SilkMarshal.StringToPtr(label);
        var desc = new BufferDescriptor
        {
            Label = (byte*)labelPtr,
            Size = allocatedSize,
            Usage = usage,
            MappedAtCreation = false
        };

        BufferPtr = _context.Wgpu.DeviceCreateBuffer(_context.Device, &desc);
        SilkMarshal.Free(labelPtr);

        if (BufferPtr == null)
        {
            throw new InvalidOperationException($"Failed to allocate GPU Buffer of size {size} bytes.");
        }
    }

    public void Write<T>(ReadOnlySpan<T> data, uint offsetBytes = 0) where T : unmanaged
    {
        WriteBytes(MemoryMarshal.AsBytes(data), offsetBytes);
    }

    public void WriteBytes(ReadOnlySpan<byte> data, uint offsetBytes = 0)
    {
        if (_isDisposed || BufferPtr == null) throw new ObjectDisposedException(nameof(GpuBuffer));

        var dataSize = checked((uint)data.Length);
        if (offsetBytes + dataSize > Size)
        {
            throw new ArgumentOutOfRangeException(nameof(data), $"Data size {dataSize} at offset {offsetBytes} exceeds buffer size {Size}.");
        }

        if (dataSize == 0)
        {
            return;
        }

        if (IsQueueWriteAligned(offsetBytes, dataSize))
        {
            QueueWriteAligned(data, offsetBytes);
            if (_partialWriteShadow is null)
            {
                _hasUnmirroredWrites = true;
            }
            else
            {
                data.CopyTo(_partialWriteShadow.AsSpan(checked((int)offsetBytes), checked((int)dataSize)));
            }

            return;
        }

        var uploadRange = CreateAlignedUploadRange(offsetBytes, dataSize);
        EnsurePartialWriteShadow();
        data.CopyTo(_partialWriteShadow!.AsSpan(checked((int)offsetBytes), checked((int)dataSize)));
        WriteAlignedBytes(
            _partialWriteShadow.AsSpan(checked((int)uploadRange.OffsetBytes), checked((int)uploadRange.SizeBytes)),
            uploadRange.OffsetBytes);
    }

    public void WriteAlignedBytes(ReadOnlySpan<byte> data, uint offsetBytes = 0)
    {
        if (_isDisposed || BufferPtr == null) throw new ObjectDisposedException(nameof(GpuBuffer));

        var dataSize = checked((uint)data.Length);
        if (!IsQueueWriteAligned(offsetBytes, dataSize))
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                "Aligned GPU buffer writes require 4-byte aligned offsets and sizes.");
        }

        ValidateAllocatedWriteRange(offsetBytes, dataSize);
        if (dataSize == 0)
        {
            return;
        }

        QueueWriteAligned(data, offsetBytes);
        if (_partialWriteShadow is null)
        {
            _hasUnmirroredWrites = true;
        }
        else
        {
            data.CopyTo(_partialWriteShadow.AsSpan(checked((int)offsetBytes), checked((int)dataSize)));
        }
    }

    private void QueueWriteAligned(ReadOnlySpan<byte> data, uint offsetBytes)
    {
        fixed (byte* ptr = data)
        {
            _context.Wgpu.QueueWriteBuffer(_context.Queue, BufferPtr, offsetBytes, ptr, (uint)data.Length);
        }
    }

    private void EnsurePartialWriteShadow()
    {
        if (_partialWriteShadow is not null)
        {
            return;
        }

        if (_hasUnmirroredWrites)
        {
            throw new InvalidOperationException(
                "Unaligned GPU buffer writes after prior unmirrored writes cannot preserve existing boundary bytes. Use WriteAlignedBytes with a caller-preserved aligned span.");
        }

        _partialWriteShadow = new byte[AllocatedSize];
    }

    private static uint AlignToQueueWriteSize(uint size)
    {
        return (size + 3) & ~3u;
    }

    public void WriteSingle<T>(T value, uint offsetBytes = 0) where T : unmanaged
    {
        Write(new ReadOnlySpan<T>(&value, 1), offsetBytes);
    }

    public byte[] ReadBytes(uint offsetBytes = 0, uint? sizeBytes = null)
    {
        if (_isDisposed || BufferPtr == null) throw new ObjectDisposedException(nameof(GpuBuffer));

        var readSize = sizeBytes ?? (Size - offsetBytes);
        ValidateReadRange(offsetBytes, readSize);
        if (readSize == 0)
        {
            return [];
        }

        var bytes = new byte[checked((int)readSize)];
        ReadBytes(bytes, offsetBytes);
        return bytes;
    }

    public void ReadBytes(Span<byte> destination, uint offsetBytes = 0)
    {
        if (_isDisposed || BufferPtr == null) throw new ObjectDisposedException(nameof(GpuBuffer));

        var readSize = checked((uint)destination.Length);
        ValidateReadRange(offsetBytes, readSize);
        if (readSize == 0)
        {
            return;
        }

        if (Usage.HasFlag(BufferUsage.MapRead))
        {
            var mappedRange = CreateAlignedReadbackRange(offsetBytes, readSize, offsetAlignment: 8);
            MapReadBuffer(BufferPtr, mappedRange.OffsetBytes, mappedRange.SizeBytes, mappedRange.LeadingBytes, destination, destroyAfterRead: false);
            return;
        }

        if (!Usage.HasFlag(BufferUsage.CopySrc))
        {
            throw new InvalidOperationException("Buffer was not created with CopySrc or MapRead usage.");
        }

        var copyRange = CreateAlignedReadbackRange(offsetBytes, readSize, offsetAlignment: 4);
        var readbackDesc = new BufferDescriptor
        {
            Usage = BufferUsage.CopyDst | BufferUsage.MapRead,
            Size = copyRange.SizeBytes,
            MappedAtCreation = false
        };
        var readbackBuffer = _context.Wgpu.DeviceCreateBuffer(_context.Device, &readbackDesc);
        if (readbackBuffer == null)
        {
            throw new InvalidOperationException("Failed to create readback buffer.");
        }

        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Buffer Readback Encoder") };
        var encoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);
        if (encoder == null)
        {
            QueueTemporaryReadbackBufferDisposal(readbackBuffer);
            throw new InvalidOperationException("Failed to create command encoder for buffer readback.");
        }

        _context.Wgpu.CommandEncoderCopyBufferToBuffer(
            encoder,
            BufferPtr,
            copyRange.OffsetBytes,
            readbackBuffer,
            0,
            copyRange.SizeBytes);

        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Buffer Readback Command Buffer") };
        var commandBuffer = _context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);
        if (commandBuffer == null)
        {
            _context.Wgpu.CommandEncoderRelease(encoder);
            QueueTemporaryReadbackBufferDisposal(readbackBuffer);
            throw new InvalidOperationException("Failed to finish buffer readback command encoder.");
        }

        _context.Wgpu.QueueSubmit(_context.Queue, 1, &commandBuffer);
        _context.Wgpu.CommandBufferRelease(commandBuffer);
        _context.Wgpu.CommandEncoderRelease(encoder);

        MapReadBuffer(readbackBuffer, 0, copyRange.SizeBytes, copyRange.LeadingBytes, destination, destroyAfterRead: true);
    }

    private void MapReadBuffer(
        Buffer* buffer,
        uint offsetBytes,
        uint sizeBytes,
        uint leadingBytes,
        Span<byte> destination,
        bool destroyAfterRead)
    {
        var mapSignal = new System.Threading.ManualResetEventSlim(false);
        var mapStatus = BufferMapAsyncStatus.ValidationError;
        var onMapped = PfnBufferMapCallback.From((status, userData) =>
        {
            mapStatus = status;
            mapSignal.Set();
        });

        _context.Wgpu.BufferMapAsync(buffer, MapMode.Read, offsetBytes, (nuint)sizeBytes, onMapped, null);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!mapSignal.IsSet)
        {
            _context.PollDevice(wait: false);
            System.Threading.Thread.Sleep(1);
            if (stopwatch.ElapsedMilliseconds > DefaultMapTimeoutMilliseconds)
            {
                CleanupMappedReadBuffer(buffer, destroyAfterRead);
                throw new TimeoutException($"WebGPU BufferMapAsync timed out after {DefaultMapTimeoutMilliseconds / 1000} seconds during buffer readback.");
            }
        }

        if (mapStatus != BufferMapAsyncStatus.Success)
        {
            CleanupMappedReadBuffer(buffer, destroyAfterRead);
            throw new InvalidOperationException($"Failed to map readback buffer. WebGPU Status: {mapStatus}");
        }

        var mappedPtr = _context.Wgpu.BufferGetConstMappedRange(buffer, offsetBytes, (nuint)sizeBytes);
        if (mappedPtr != null)
        {
            new ReadOnlySpan<byte>(
                (byte*)mappedPtr + checked((int)leadingBytes),
                destination.Length).CopyTo(destination);
        }

        _context.Wgpu.BufferUnmap(buffer);
        if (destroyAfterRead)
        {
            QueueTemporaryReadbackBufferDisposal(buffer);
        }
    }

    private void CleanupMappedReadBuffer(Buffer* buffer, bool destroyAfterRead)
    {
        if (destroyAfterRead)
        {
            QueueTemporaryReadbackBufferDisposal(buffer);
        }
    }

    private void QueueTemporaryReadbackBufferDisposal(Buffer* buffer)
    {
        if (buffer == null || _context.IsDisposed)
        {
            return;
        }

        _context.QueueBufferDisposal((IntPtr)buffer);
        _context.CleanupPendingResources();
    }

    private void ValidateReadRange(uint offsetBytes, uint sizeBytes)
    {
        if (offsetBytes > Size || sizeBytes > Size - offsetBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Buffer read exceeds the buffer bounds.");
        }
    }

    private void ValidateAllocatedWriteRange(uint offsetBytes, uint sizeBytes)
    {
        if (offsetBytes > AllocatedSize || sizeBytes > AllocatedSize - offsetBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "GPU buffer write exceeds the allocated buffer bounds.");
        }
    }

    private ReadbackRange CreateAlignedReadbackRange(uint offsetBytes, uint sizeBytes, uint offsetAlignment)
    {
        var alignedOffset = AlignDown(offsetBytes, offsetAlignment);
        var leadingBytes = offsetBytes - alignedOffset;
        var minimumSize = (ulong)leadingBytes + sizeBytes;
        var alignedSize = AlignUp(minimumSize, 4);
        var availableSize = AllocatedSize - alignedOffset;
        if (alignedSize > availableSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sizeBytes),
                "GPU buffer readback cannot form an aligned enclosing range inside the buffer bounds.");
        }

        return new ReadbackRange(alignedOffset, alignedSize, leadingBytes);
    }

    private ReadbackRange CreateAlignedUploadRange(uint offsetBytes, uint sizeBytes)
    {
        var alignedOffset = AlignDown(offsetBytes, 4);
        var leadingBytes = offsetBytes - alignedOffset;
        var alignedSize = AlignUp((ulong)leadingBytes + sizeBytes, 4);
        var availableSize = AllocatedSize - alignedOffset;
        if (alignedSize > availableSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sizeBytes),
                "GPU buffer upload cannot form an aligned enclosing range inside the allocated buffer bounds.");
        }

        return new ReadbackRange(alignedOffset, alignedSize, leadingBytes);
    }

    private static bool IsQueueWriteAligned(uint offsetBytes, uint sizeBytes)
    {
        return offsetBytes % 4 == 0 && sizeBytes % 4 == 0;
    }

    private static uint AlignDown(uint value, uint alignment)
    {
        return value - (value % alignment);
    }

    private static uint AlignUp(ulong value, uint alignment)
    {
        return checked((uint)(((value + alignment - 1) / alignment) * alignment));
    }

    private readonly record struct ReadbackRange(uint OffsetBytes, uint SizeBytes, uint LeadingBytes);

    public void Dispose()
    {
        if (_isDisposed) return;

        lock (_context.RenderLock)
        {
            if (BufferPtr != null)
            {
                if (!_context.IsDisposed)
                {
                    _context.QueueBufferDisposal((IntPtr)BufferPtr);
                }

                BufferPtr = null;
            }
        }

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    ~GpuBuffer()
    {
        if (BufferPtr != null)
        {
            try
            {
                _context.QueueBufferDisposal((IntPtr)BufferPtr);
            }
            catch {}
        }
    }
}
