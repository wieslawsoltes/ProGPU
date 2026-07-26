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
    private IncrementalBufferShadow? _gradientStopUploadShadow;
    private int _incrementalSceneUploadPageWrites;
    private long _incrementalSceneUploadBytes;
    private long _incrementalSceneUploadShadowBytes;
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
            _incrementalSceneUploadBytes += bytes.Length;
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
            _incrementalSceneUploadBytes += bytes.Length;
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
            _incrementalSceneUploadBytes += bytes.Length;
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
            _incrementalSceneUploadBytes += length;
        }
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

        int sourceOffset = AlignToFour(_pendingSceneUploadByteCount);
        int requiredSize = checked(sourceOffset + data.Length);
        EnsurePendingSceneUploadCapacity(requiredSize);
        data.CopyTo(
            _pendingSceneUploadBytes!.AsSpan(sourceOffset, data.Length));
        _pendingSceneUploadByteCount = requiredSize;
        _pendingSceneBufferUploads.Add(
            new PendingSceneBufferUpload(
                destination,
                destinationOffset,
                checked((uint)sourceOffset),
                checked((uint)data.Length)));
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

    private void EnsurePendingSceneUploadCapacity(int requiredSize)
    {
        if (_pendingSceneUploadBytes != null &&
            _pendingSceneUploadBytes.Length >= requiredSize)
        {
            return;
        }

        int capacity = InitialSceneUploadArenaBytes;
        while (capacity < requiredSize)
        {
            capacity = checked(capacity * 2);
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

        uint capacity = InitialSceneUploadArenaBytes;
        while (capacity < requiredSize)
        {
            capacity = checked(capacity * 2);
        }

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

        uint capacity = InitialSceneUploadArenaBytes;
        while (capacity < requiredSize)
        {
            capacity = checked(capacity * 2);
        }

        _sceneMappedUploadRing?.Dispose();
        _sceneMappedUploadRing = new GpuMappedUploadBufferRing(
            _context,
            capacity,
            slotCount: 2);
        _sceneUploadStagingBuffer?.Dispose();
        _sceneUploadStagingBuffer = null;
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
        _gradientStopUploadShadow = null;
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
