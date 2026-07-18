using System.Buffers.Binary;
using Silk.NET.WebGPU;

namespace ProGPU.Backend;

public readonly record struct GpuTimestampMetrics(
    long SubmittedSamples,
    long CompletedSamples,
    long FailedSamples,
    long DroppedSamples,
    double LastFrameMilliseconds,
    double AverageFrameMilliseconds,
    double MaximumFrameMilliseconds);

/// <summary>
/// Resolves timestamp pairs through three independently mappable readback buffers. A frame never
/// waits for a previous mapping; when all slots are busy the diagnostic sample is dropped.
/// Normal rendering does not create this object unless timestamp diagnostics are explicitly enabled.
/// </summary>
internal unsafe sealed class GpuTimestampRing : IDisposable
{
    private const int SlotCount = 3;
    private const uint TimestampBytes = 16;
    private const uint ResolveStride = 256;

    private readonly WgpuContext _context;
    private readonly QuerySet* _querySet;
    private readonly GpuBuffer _resolveBuffer;
    private readonly Slot[] _slots = new Slot[SlotCount];
    private int _nextSlot;
    private int _activeSlot = -1;
    private long _submittedSamples;
    private long _completedSamples;
    private long _failedSamples;
    private long _droppedSamples;
    private double _totalMilliseconds;
    private double _lastMilliseconds;
    private double _maximumMilliseconds;
    private bool _disposed;

    public GpuTimestampRing(WgpuContext context)
    {
        _context = context;
        var queryDescriptor = new QuerySetDescriptor
        {
            Type = QueryType.Timestamp,
            Count = 2
        };
        _querySet = context.Api.DeviceCreateQuerySet(context.Device, &queryDescriptor);
        if (_querySet == null)
        {
            throw new InvalidOperationException("The WebGPU device did not create a timestamp query set.");
        }

        _resolveBuffer = new GpuBuffer(
            context,
            ResolveStride * SlotCount,
            BufferUsage.QueryResolve | BufferUsage.CopySrc,
            "GPU timestamp resolve ring");
        for (int index = 0; index < SlotCount; index++)
        {
            _slots[index] = new Slot(new GpuBuffer(
                context,
                TimestampBytes,
                BufferUsage.CopyDst | BufferUsage.MapRead,
                $"GPU timestamp readback {index}"));
        }
    }

    public GpuTimestampMetrics Metrics
    {
        get
        {
            HarvestCompletedMappings();
            return new GpuTimestampMetrics(
                _submittedSamples,
                _completedSamples,
                _failedSamples,
                _droppedSamples,
                _lastMilliseconds,
                _completedSamples == 0 ? 0d : _totalMilliseconds / _completedSamples,
                _maximumMilliseconds);
        }
    }

    public bool BeginFrame(CommandEncoder* encoder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        HarvestCompletedMappings();
        if (_activeSlot >= 0)
        {
            throw new InvalidOperationException("A GPU timestamp frame is already active.");
        }

        int selected = -1;
        for (int offset = 0; offset < SlotCount; offset++)
        {
            int candidate = (_nextSlot + offset) % SlotCount;
            if (_slots[candidate].MapTask == null)
            {
                selected = candidate;
                break;
            }
        }

        if (selected < 0)
        {
            _droppedSamples++;
            return false;
        }

        _nextSlot = (selected + 1) % SlotCount;
        _activeSlot = selected;
        _context.Api.CommandEncoderWriteTimestamp(encoder, _querySet, 0);
        return true;
    }

    public void EndFrame(CommandEncoder* encoder)
    {
        if (_activeSlot < 0)
        {
            return;
        }

        int slot = _activeSlot;
        ulong resolveOffset = (ulong)slot * ResolveStride;
        _context.Api.CommandEncoderWriteTimestamp(encoder, _querySet, 1);
        _context.Api.CommandEncoderResolveQuerySet(encoder, _querySet, 0, 2, _resolveBuffer.BufferPtr, resolveOffset);
        _context.Api.CommandEncoderCopyBufferToBuffer(
            encoder,
            _resolveBuffer.BufferPtr,
            resolveOffset,
            _slots[slot].Readback.BufferPtr,
            0,
            TimestampBytes);
    }

    public void NotifySubmitted()
    {
        if (_activeSlot < 0)
        {
            return;
        }

        var slot = _slots[_activeSlot];
        slot.MapTask = _context.Api.BufferMapAsyncTask(
            slot.Readback.BufferPtr,
            MapMode.Read,
            0,
            TimestampBytes);
        _submittedSamples++;
        _activeSlot = -1;
    }

    private void HarvestCompletedMappings()
    {
        for (int index = 0; index < SlotCount; index++)
        {
            var slot = _slots[index];
            var task = slot.MapTask;
            if (task == null || !task.IsCompleted)
            {
                continue;
            }

            slot.MapTask = null;
            bool shouldUnmap = false;
            try
            {
                if (task.GetAwaiter().GetResult() != BufferMapAsyncStatus.Success)
                {
                    _failedSamples++;
                    continue;
                }
                shouldUnmap = true;

                var mapped = _context.Api.BufferGetConstMappedRange(
                    slot.Readback.BufferPtr,
                    0,
                    TimestampBytes);
                if (mapped == null)
                {
                    _failedSamples++;
                    continue;
                }

                var bytes = new ReadOnlySpan<byte>(mapped, checked((int)TimestampBytes));
                ulong begin = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
                ulong end = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
                if (end <= begin)
                {
                    _failedSamples++;
                    continue;
                }

                double milliseconds = (end - begin) / 1_000_000d;
                _lastMilliseconds = milliseconds;
                _totalMilliseconds += milliseconds;
                _maximumMilliseconds = Math.Max(_maximumMilliseconds, milliseconds);
                _completedSamples++;
            }
            catch
            {
                _failedSamples++;
            }
            finally
            {
                if (shouldUnmap)
                {
                    _context.Api.BufferUnmap(slot.Readback.BufferPtr);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        HarvestCompletedMappings();
        foreach (var slot in _slots)
        {
            slot.Readback.Dispose();
        }
        _resolveBuffer.Dispose();
        _context.Api.QuerySetRelease(_querySet);
        _disposed = true;
    }

    private sealed class Slot(GpuBuffer readback)
    {
        public GpuBuffer Readback { get; } = readback;
        public Task<BufferMapAsyncStatus>? MapTask { get; set; }
    }
}
