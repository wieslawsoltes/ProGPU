using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace ProGPU.Scene;

public unsafe partial class Compositor
{
    private const int IncrementalUploadPageBytes = 4096;
    private const int InitialSceneUploadArenaBytes = 4096;
    private const uint MaximumSceneUploadBatchBytes = 64U * 1024U * 1024U;

    private readonly record struct PendingSceneBufferUpload(
        GpuBuffer Destination,
        uint DestinationOffset,
        uint SourceOffset,
        uint Size);

    private sealed class IncrementalBufferShadow
    {
        internal required GpuBuffer Owner { get; init; }
        internal required byte[] Bytes { get; init; }
    }

    private IncrementalBufferShadow? _vectorVertexUploadShadow;
    private IncrementalBufferShadow? _vectorIndexUploadShadow;
    private IncrementalBufferShadow? _textVertexUploadShadow;
    private IncrementalBufferShadow? _textureVertexUploadShadow;
    private IncrementalBufferShadow? _textureIndexUploadShadow;
    private IncrementalBufferShadow? _brushUploadShadow;
    private IncrementalBufferShadow? _textStyleUploadShadow;
    private IncrementalBufferShadow? _gradientStopUploadShadow;
    private IncrementalBufferShadow? _uniformUploadShadow;
    private int _incrementalSceneUploadPageWrites;
    private long _incrementalSceneUploadBytes;
    private long _incrementalSceneUploadShadowBytes;
    private long _incrementalSceneVectorVertexUploadBytes;
    private long _incrementalSceneVectorIndexUploadBytes;
    private long _incrementalSceneTextVertexUploadBytes;
    private long _incrementalSceneTextureVertexUploadBytes;
    private long _incrementalSceneTextureIndexUploadBytes;
    private long _incrementalSceneBrushUploadBytes;
    private long _incrementalSceneTextStyleUploadBytes;
    private long _incrementalSceneGradientStopUploadBytes;
    private readonly List<PendingSceneBufferUpload> _pendingSceneBufferUploads =
        new(16);
    private byte[]? _pendingSceneUploadBytes;
    private int _pendingSceneUploadByteCount;
    private GpuBuffer? _sceneUploadStagingBuffer;
    private GpuMappedUploadBufferRing? _sceneMappedUploadRing;
    private int _sceneUploadBatchCount;
    private int _sceneUploadCopyCount;

    private void ResetIncrementalSceneUploadFrameMetrics()
    {
        _pendingSceneBufferUploads.Clear();
        _pendingSceneUploadByteCount = 0;
        _incrementalSceneUploadPageWrites = 0;
        _incrementalSceneUploadBytes = 0;
        _incrementalSceneVectorVertexUploadBytes = 0;
        _incrementalSceneVectorIndexUploadBytes = 0;
        _incrementalSceneTextVertexUploadBytes = 0;
        _incrementalSceneTextureVertexUploadBytes = 0;
        _incrementalSceneTextureIndexUploadBytes = 0;
        _incrementalSceneBrushUploadBytes = 0;
        _incrementalSceneTextStyleUploadBytes = 0;
        _incrementalSceneGradientStopUploadBytes = 0;
        _sceneUploadBatchCount = 0;
        _sceneUploadCopyCount = 0;
    }

    private void UploadIncrementalSceneBuffer<T>(
        GpuBuffer buffer,
        ReadOnlySpan<T> data,
        ref IncrementalBufferShadow? shadow)
        where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        if (bytes.IsEmpty)
        {
            return;
        }

        if (!Options.EnableIncrementalScenePages)
        {
            QueuePendingSceneUpload(buffer, bytes, 0);
            _incrementalSceneUploadPageWrites++;
            RecordIncrementalSceneUpload(buffer, bytes.Length);
            return;
        }

        if (bytes.Length % 4 != 0)
        {
            buffer.Write(data);
            ReplaceIncrementalUploadShadow(
                buffer,
                bytes,
                ref shadow);
            _incrementalSceneUploadPageWrites++;
            RecordIncrementalSceneUpload(buffer, bytes.Length);
            return;
        }

        if (shadow == null ||
            !ReferenceEquals(shadow.Owner, buffer) ||
            shadow.Bytes.Length < bytes.Length)
        {
            QueuePendingSceneUpload(buffer, bytes, 0);
            ReplaceIncrementalUploadShadow(
                buffer,
                bytes,
                ref shadow);
            _incrementalSceneUploadPageWrites++;
            RecordIncrementalSceneUpload(buffer, bytes.Length);
            return;
        }

        Span<byte> previous = shadow.Bytes.AsSpan(0, bytes.Length);
        for (int offset = 0;
             offset < bytes.Length;
             offset += IncrementalUploadPageBytes)
        {
            int length = Math.Min(
                IncrementalUploadPageBytes,
                bytes.Length - offset);
            ReadOnlySpan<byte> currentPage = bytes.Slice(offset, length);
            Span<byte> previousPage = previous.Slice(offset, length);
            if (currentPage.SequenceEqual(previousPage))
            {
                continue;
            }

            QueuePendingSceneUpload(
                buffer,
                currentPage,
                checked((uint)offset));
            currentPage.CopyTo(previousPage);
            _incrementalSceneUploadPageWrites++;
            RecordIncrementalSceneUpload(buffer, length);
        }
    }

    private void RecordIncrementalSceneUpload(
        GpuBuffer buffer,
        int byteCount)
    {
        _incrementalSceneUploadBytes += byteCount;
        if (ReferenceEquals(buffer, _vectorVertexBuffer))
            _incrementalSceneVectorVertexUploadBytes += byteCount;
        else if (ReferenceEquals(buffer, _vectorIndexBuffer))
            _incrementalSceneVectorIndexUploadBytes += byteCount;
        else if (ReferenceEquals(buffer, _textVertexBuffer))
            _incrementalSceneTextVertexUploadBytes += byteCount;
        else if (ReferenceEquals(buffer, _textureVertexBuffer))
            _incrementalSceneTextureVertexUploadBytes += byteCount;
        else if (ReferenceEquals(buffer, _textureIndexBuffer))
            _incrementalSceneTextureIndexUploadBytes += byteCount;
        else if (ReferenceEquals(buffer, _brushesStorageBuffer))
            _incrementalSceneBrushUploadBytes += byteCount;
        else if (ReferenceEquals(buffer, _textStylesStorageBuffer))
            _incrementalSceneTextStyleUploadBytes += byteCount;
        else if (ReferenceEquals(buffer, _gradientStopsStorageBuffer))
            _incrementalSceneGradientStopUploadBytes += byteCount;
    }

    private void QueuePendingSceneUpload<T>(
        GpuBuffer destination,
        ReadOnlySpan<T> data,
        uint destinationOffset)
        where T : unmanaged
    {
        QueuePendingSceneUpload(
            destination,
            MemoryMarshal.AsBytes(data),
            destinationOffset);
    }

    private void QueuePendingSceneUpload(
        GpuBuffer destination,
        ReadOnlySpan<byte> data,
        uint destinationOffset)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if ((destinationOffset & 3u) != 0 ||
            (data.Length & 3) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                "Batched scene uploads require 4-byte aligned offsets and sizes.");
        }

        int batchCapacity = checked((int)GetSceneUploadBatchCapacity(
            _context.MaxBufferSize));
        int consumed = 0;
        while (consumed < data.Length)
        {
            int sourceOffset = AlignToFour(_pendingSceneUploadByteCount);
            int available = batchCapacity - sourceOffset;
            if (available == 0)
            {
                FlushPendingSceneUploadsToQueue();
                continue;
            }

            int chunkSize = Math.Min(data.Length - consumed, available) & ~3;
            if (chunkSize == 0)
            {
                FlushPendingSceneUploadsToQueue();
                continue;
            }

            int requiredSize = checked(sourceOffset + chunkSize);
            EnsurePendingSceneUploadCapacity(requiredSize, batchCapacity);
            data.Slice(consumed, chunkSize).CopyTo(
                _pendingSceneUploadBytes!.AsSpan(sourceOffset, chunkSize));
            _pendingSceneUploadByteCount = requiredSize;
            _pendingSceneBufferUploads.Add(
                new PendingSceneBufferUpload(
                    destination,
                    checked(destinationOffset + (uint)consumed),
                    checked((uint)sourceOffset),
                    checked((uint)chunkSize)));
            consumed += chunkSize;
        }
    }

    private void FlushPendingSceneUploadsToQueue()
    {
        if (_pendingSceneBufferUploads.Count == 0)
        {
            return;
        }

        for (int index = 0;
             index < _pendingSceneBufferUploads.Count;
             index++)
        {
            PendingSceneBufferUpload upload =
                _pendingSceneBufferUploads[index];
            upload.Destination.WriteAlignedBytes(
                _pendingSceneUploadBytes.AsSpan(
                    checked((int)upload.SourceOffset),
                    checked((int)upload.Size)),
                upload.DestinationOffset);
        }

        _sceneUploadBatchCount++;
        _sceneUploadCopyCount += _pendingSceneBufferUploads.Count;
        _pendingSceneBufferUploads.Clear();
        _pendingSceneUploadByteCount = 0;
    }

    private void EncodePendingSceneUploads(CommandEncoder* encoder)
    {
        PrepareMaskRenderResourceUploads();
        if (_pendingSceneBufferUploads.Count == 0)
        {
            return;
        }

        int uploadByteCount = AlignToFour(_pendingSceneUploadByteCount);
        ReadOnlySpan<byte> uploadBytes =
            _pendingSceneUploadBytes.AsSpan(0, uploadByteCount);
        Silk.NET.WebGPU.Buffer* sourceBuffer;
        if (_context.BackendKind != WgpuBackendKind.BrowserWebGpu)
        {
            EnsureSceneMappedUploadRing(checked((uint)uploadByteCount));
            if (!_sceneMappedUploadRing!.TryWrite(
                    uploadBytes,
                    out sourceBuffer))
            {
                EnsureSceneUploadStagingBuffer(
                    checked((uint)uploadByteCount));
                _sceneUploadStagingBuffer!.WriteAlignedBytes(uploadBytes);
                sourceBuffer =
                    _sceneUploadStagingBuffer.BufferPtr;
            }
        }
        else
        {
            EnsureSceneUploadStagingBuffer(checked((uint)uploadByteCount));
            _sceneUploadStagingBuffer!.WriteAlignedBytes(uploadBytes);
            sourceBuffer = _sceneUploadStagingBuffer.BufferPtr;
        }

        for (int index = 0;
             index < _pendingSceneBufferUploads.Count;
             index++)
        {
            PendingSceneBufferUpload upload =
                _pendingSceneBufferUploads[index];
            _context.Api.CommandEncoderCopyBufferToBuffer(
                encoder,
                sourceBuffer,
                upload.SourceOffset,
                upload.Destination.BufferPtr,
                upload.DestinationOffset,
                upload.Size);
        }

        _sceneUploadBatchCount++;
        _sceneUploadCopyCount += _pendingSceneBufferUploads.Count;
        _pendingSceneBufferUploads.Clear();
        _pendingSceneUploadByteCount = 0;
    }

    private void RecallSubmittedSceneUpload()
    {
        _sceneMappedUploadRing?.RecallAfterSubmit();
    }

    private void EnsurePendingSceneUploadCapacity(
        int requiredSize,
        int maximumCapacity)
    {
        if (_pendingSceneUploadBytes != null &&
            _pendingSceneUploadBytes.Length >= requiredSize)
        {
            return;
        }

        int capacity = Math.Min(
            InitialSceneUploadArenaBytes,
            maximumCapacity);
        while (capacity < requiredSize)
        {
            capacity = capacity > maximumCapacity / 2
                ? maximumCapacity
                : checked(capacity * 2);
        }

        byte[] replacement = ArrayPool<byte>.Shared.Rent(capacity);
        if (_pendingSceneUploadBytes != null)
        {
            _pendingSceneUploadBytes
                .AsSpan(0, _pendingSceneUploadByteCount)
                .CopyTo(replacement);
            ArrayPool<byte>.Shared.Return(_pendingSceneUploadBytes);
        }

        _pendingSceneUploadBytes = replacement;
    }

    private void EnsureSceneUploadStagingBuffer(uint requiredSize)
    {
        if (_sceneUploadStagingBuffer != null &&
            _sceneUploadStagingBuffer.Size >= requiredSize)
        {
            return;
        }

        uint capacity = CalculateSceneUploadBufferCapacity(
            _sceneUploadStagingBuffer?.Size ?? 0U,
            requiredSize,
            GetSceneUploadBatchCapacity(_context.MaxBufferSize));

        var replacement = new GpuBuffer(
            _context,
            capacity,
            BufferUsage.CopySrc | BufferUsage.CopyDst,
            "Compositor Scene Upload Arena");
        _sceneUploadStagingBuffer?.Dispose();
        _sceneUploadStagingBuffer = replacement;
    }

    private void EnsureSceneMappedUploadRing(uint requiredSize)
    {
        if (_sceneMappedUploadRing != null &&
            _sceneMappedUploadRing.Capacity >= requiredSize)
        {
            return;
        }

        uint capacity = CalculateSceneUploadBufferCapacity(
            _sceneMappedUploadRing?.Capacity ?? 0U,
            requiredSize,
            GetSceneUploadBatchCapacity(_context.MaxBufferSize));
        var replacement = new GpuMappedUploadBufferRing(
            _context,
            capacity,
            slotCount: 2);
        _sceneMappedUploadRing?.Dispose();
        _sceneMappedUploadRing = replacement;
        _sceneUploadStagingBuffer?.Dispose();
        _sceneUploadStagingBuffer = null;
    }

    internal static uint GetSceneUploadBatchCapacity(ulong maxBufferSize)
    {
        ulong capacity = Math.Min(
            maxBufferSize,
            MaximumSceneUploadBatchBytes);
        capacity = Math.Min(capacity, int.MaxValue & ~3UL);
        capacity &= ~3UL;
        if (capacity < 4UL)
        {
            throw new InvalidOperationException(
                $"The WebGPU device maximum buffer size of {maxBufferSize} bytes cannot hold an aligned scene upload.");
        }

        return checked((uint)capacity);
    }

    internal static uint CalculateSceneUploadBufferCapacity(
        uint currentSize,
        uint requiredSize,
        uint maximumCapacity)
    {
        if (requiredSize > maximumCapacity)
        {
            throw new InvalidOperationException(
                $"Scene upload requires {requiredSize} bytes, exceeding the bounded batch capacity of {maximumCapacity} bytes.");
        }

        uint capacity = Math.Max(
            currentSize,
            Math.Min((uint)InitialSceneUploadArenaBytes, maximumCapacity));
        while (capacity < requiredSize)
        {
            capacity = capacity > maximumCapacity / 2U
                ? maximumCapacity
                : checked(capacity * 2U);
        }

        return capacity;
    }

    private static int AlignToFour(int value) =>
        checked((value + 3) & ~3);

    private void ReplaceIncrementalUploadShadow(
        GpuBuffer owner,
        ReadOnlySpan<byte> bytes,
        ref IncrementalBufferShadow? shadow)
    {
        int capacity = IncrementalUploadPageBytes;
        while (capacity < bytes.Length)
        {
            capacity = checked(capacity * 2);
        }

        long previousSize = shadow?.Bytes.LongLength ?? 0L;
        var storage = new byte[capacity];
        bytes.CopyTo(storage);
        shadow = new IncrementalBufferShadow
        {
            Owner = owner,
            Bytes = storage
        };
        _incrementalSceneUploadShadowBytes +=
            storage.LongLength - previousSize;
    }

    private void ClearIncrementalUploadShadows()
    {
        _vectorVertexUploadShadow = null;
        _vectorIndexUploadShadow = null;
        _textVertexUploadShadow = null;
        _textureVertexUploadShadow = null;
        _textureIndexUploadShadow = null;
        _brushUploadShadow = null;
        _textStyleUploadShadow = null;
        _gradientStopUploadShadow = null;
        _uniformUploadShadow = null;
        _incrementalSceneUploadShadowBytes = 0;
        _pendingSceneBufferUploads.Clear();
        _pendingSceneUploadByteCount = 0;
        _sceneUploadStagingBuffer?.Dispose();
        _sceneUploadStagingBuffer = null;
        _sceneMappedUploadRing?.Dispose();
        _sceneMappedUploadRing = null;
        if (_pendingSceneUploadBytes != null)
        {
            ArrayPool<byte>.Shared.Return(_pendingSceneUploadBytes);
            _pendingSceneUploadBytes = null;
        }
    }
}
