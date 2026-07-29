using ProGPU.Backend;
using System.Runtime.InteropServices;

namespace ProGPU.Linux.Media;

/// <summary>
/// Borrowed compressed access unit from a V4L2 encoder CAPTURE buffer.
/// Disposal returns the native slot to the bounded capture queue.
/// </summary>
internal sealed unsafe class V4l2EncodedAccessUnit :
    IDisposable
{
    private V4l2StatefulVideoEncoder? _owner;
    private readonly int _captureIndex;
    private readonly byte* _address;
    private readonly int _length;

    internal V4l2EncodedAccessUnit(
        V4l2StatefulVideoEncoder owner,
        int captureIndex,
        byte* address,
        int length,
        TimeSpan presentationTime,
        long sequence,
        bool isKeyFrame)
    {
        _owner = owner;
        _captureIndex = captureIndex;
        _address = address;
        _length = length;
        PresentationTime = presentationTime;
        Sequence = sequence;
        IsKeyFrame = isKeyFrame;
    }

    internal ReadOnlySpan<byte> Data
    {
        get
        {
            ObjectDisposedException.ThrowIf(
                _owner is null,
                this);
            return new ReadOnlySpan<byte>(
                _address,
                _length);
        }
    }

    internal TimeSpan PresentationTime { get; }

    internal long Sequence { get; }

    internal bool IsKeyFrame { get; }

    public void Dispose()
    {
        V4l2StatefulVideoEncoder? owner =
            Interlocked.Exchange(
                ref _owner,
                null);
        owner?.ReturnCapture(_captureIndex);
    }
}

/// <summary>
/// Dependency-free V4L2 stateful H.264 encoder. Raw NV12 DMA-BUF allocations
/// are imported by the driver on OUTPUT and compressed CAPTURE buffers remain
/// MMAP-backed until their access-unit lease is disposed.
/// </summary>
/// <remarks>
/// Queueing is O(P) for P=1 or 2 raw planes with no decoded-pixel copy.
/// CAPTURE consumption is O(B) only when the caller writes the B encoded
/// bytes. Storage is bounded by six input slots and eight coded buffers.
/// </remarks>
internal sealed unsafe class V4l2StatefulVideoEncoder :
    IDisposable
{
    private const uint RequestedInputBuffers = 6;
    private const uint RequestedCaptureBuffers = 8;
    private static readonly void* s_mapFailed =
        (void*)(nint)(-1);

    private readonly object _captureGate = new();
    private readonly string _devicePath;
    private readonly uint _width;
    private readonly uint _height;
    private readonly uint _bitrate;
    private readonly uint _frameRateNumerator;
    private readonly uint _frameRateDenominator;
    private readonly bool _multiPlaneInput;

    private int _fileDescriptor = -1;
    private InputSlot[] _inputSlots = [];
    private CaptureBuffer[] _captureBuffers = [];
    private int[] _returnedCaptureIndices = [];
    private int _returnedHead;
    private int _returnedTail;
    private int _returnedCount;
    private int _outstandingAccessUnits;
    private uint _inputPlaneCount;
    private readonly uint[] _inputPlaneLengths = new uint[2];
    private bool _inputStreaming;
    private bool _captureStreaming;
    private bool _draining;
    private bool _endOfStreamReached;
    private bool _disposeRequested;
    private bool _captureMappingsReleased;

    internal V4l2StatefulVideoEncoder(
        string devicePath,
        uint width,
        uint height,
        uint bitrate,
        uint frameRateNumerator,
        uint frameRateDenominator,
        bool multiPlaneInput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        ArgumentOutOfRangeException.ThrowIfZero(bitrate);
        ArgumentOutOfRangeException.ThrowIfZero(
            frameRateNumerator);
        ArgumentOutOfRangeException.ThrowIfZero(
            frameRateDenominator);

        _devicePath = devicePath;
        _width = width;
        _height = height;
        _bitrate = bitrate;
        _frameRateNumerator = frameRateNumerator;
        _frameRateDenominator = frameRateDenominator;
        _multiPlaneInput = multiPlaneInput;
    }

    internal bool EndOfStreamReached =>
        _endOfStreamReached;

    internal bool HasQueuedInput
    {
        get
        {
            foreach (InputSlot slot in _inputSlots)
            {
                if (slot.Queued)
                {
                    return true;
                }
            }
            return false;
        }
    }

    internal bool CanQueueFrame
    {
        get
        {
            EnsureOpen();
            ReclaimInputSlots();
            foreach (InputSlot slot in _inputSlots)
            {
                if (!slot.Queued)
                {
                    return true;
                }
            }
            return false;
        }
    }

    internal void Open()
    {
        ObjectDisposedException.ThrowIf(
            _disposeRequested,
            this);
        if (_fileDescriptor >= 0)
        {
            return;
        }
        if (!OperatingSystem.IsLinux() ||
            nint.Size != 8)
        {
            throw new PlatformNotSupportedException(
                "The V4L2 encoder requires 64-bit Linux.");
        }

        _fileDescriptor = LinuxMediaNative.Open(
            _devicePath,
            V4l2Constants.OpenReadWrite |
            V4l2Constants.OpenNonBlocking |
            V4l2Constants.OpenCloseOnExec,
            0);
        if (_fileDescriptor < 0)
        {
            ThrowNative("open the V4L2 encoder");
        }

        try
        {
            ConfigureCodedCapture();
            ConfigureRawInput();
            ConfigureBitrate();
            ConfigureFrameRate();
            AllocateCaptureBuffers();
            AllocateInputSlots();
            QueueEveryCaptureBuffer();
            StreamOn(
                V4l2Constants.VideoCaptureMultiPlanar);
            _captureStreaming = true;
            StreamOn(
                V4l2Constants.VideoOutputMultiPlanar);
            _inputStreaming = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Queues one driver- or WebGPU-owned NV12 allocation. Ownership transfers
    /// only when true is returned and remains retained until OUTPUT dequeue.
    /// </summary>
    internal bool TryQueueFrame(
        in ProGpuDmaBufDescriptor dmaBuf,
        TimeSpan presentationTime,
        IDisposable owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        EnsureOpen();
        if (presentationTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(presentationTime));
        }
        if (dmaBuf.DrmFormat !=
                V4l2Constants.DrmNv12 ||
            dmaBuf.PlaneCount != _inputPlaneCount)
        {
            throw new NotSupportedException(
                $"The encoder requires linear NV12 with {_inputPlaneCount} DMA-BUF plane(s).");
        }

        ReclaimInputSlots();
        for (int index = 0;
             index < _inputSlots.Length;
             index++)
        {
            ref InputSlot slot =
                ref _inputSlots[index];
            if (slot.Queued)
            {
                continue;
            }

            V4l2Plane* planes =
                stackalloc V4l2Plane[
                    checked((int)_inputPlaneCount)];
            new Span<V4l2Plane>(
                    planes,
                    checked((int)_inputPlaneCount))
                .Clear();
            for (int planeIndex = 0;
                 planeIndex < _inputPlaneCount;
                 planeIndex++)
            {
                ProGpuDmaBufPlane plane =
                    dmaBuf.GetPlane(planeIndex);
                if (plane.FileDescriptor < 0 ||
                    plane.Offset > uint.MaxValue)
                {
                    throw new ArgumentException(
                        "The NV12 DMA-BUF plane descriptor is invalid.",
                        nameof(dmaBuf));
                }
                planes[planeIndex].Memory =
                    checked((uint)plane.FileDescriptor);
                planes[planeIndex].Length =
                    _inputPlaneLengths[planeIndex];
                planes[planeIndex].BytesUsed =
                    _inputPlaneLengths[planeIndex];
                planes[planeIndex].DataOffset =
                    checked((uint)plane.Offset);
            }

            V4l2Buffer buffer = CreateBuffer(
                checked((uint)index),
                V4l2Constants.VideoOutputMultiPlanar,
                V4l2Constants.MemoryDmaBuf,
                planes,
                checked((int)_inputPlaneCount));
            buffer.Flags =
                V4l2Constants.BufferFlagTimestampCopy;
            SetTimestamp(
                ref buffer,
                presentationTime);
            Ioctl(
                V4l2Constants.QueueBuffer,
                &buffer,
                "queue an NV12 DMA-BUF encoder frame");
            slot.Owner = owner;
            slot.Queued = true;
            return true;
        }
        return false;
    }

    internal bool TryDequeueAccessUnit(
        out V4l2EncodedAccessUnit accessUnit)
    {
        EnsureOpen();
        RequeueReturnedCaptureBuffers();
        ReclaimInputSlots();
        if (_endOfStreamReached)
        {
            accessUnit = null!;
            return false;
        }

        V4l2Plane* planes =
            stackalloc V4l2Plane[1];
        new Span<V4l2Plane>(planes, 1).Clear();
        V4l2Buffer buffer = CreateBuffer(
            0,
            V4l2Constants.VideoCaptureMultiPlanar,
            V4l2Constants.MemoryMap,
            planes,
            1);
        if (!TryIoctl(
                V4l2Constants.DequeueBuffer,
                &buffer))
        {
            accessUnit = null!;
            return false;
        }
        if (buffer.Index >= _captureBuffers.Length)
        {
            throw new InvalidDataException(
                "The V4L2 encoder returned an invalid CAPTURE index.");
        }

        ref CaptureBuffer capture =
            ref _captureBuffers[
                checked((int)buffer.Index)];
        capture.Queued = false;
        bool isLast =
            (buffer.Flags &
             V4l2Constants.BufferFlagLast) != 0;
        _endOfStreamReached |= isLast;
        if ((buffer.Flags &
             V4l2Constants.BufferFlagError) != 0 ||
            planes[0].BytesUsed == 0)
        {
            EnqueueReturnedCapture(
                checked((int)buffer.Index),
                ownsLease: false);
            accessUnit = null!;
            return false;
        }
        if (planes[0].DataOffset >
                planes[0].BytesUsed ||
            planes[0].BytesUsed >
                capture.Length)
        {
            throw new InvalidDataException(
                "The V4L2 encoder returned an invalid coded-byte range.");
        }

        int offset =
            checked((int)planes[0].DataOffset);
        int length = checked(
            (int)(planes[0].BytesUsed -
                  planes[0].DataOffset));
        Interlocked.Increment(
            ref _outstandingAccessUnits);
        accessUnit = new V4l2EncodedAccessUnit(
            this,
            checked((int)buffer.Index),
            capture.Address + offset,
            length,
            GetTimestamp(in buffer),
            buffer.Sequence,
            (buffer.Flags &
             V4l2Constants.BufferFlagKeyFrame) != 0);
        return true;
    }

    internal void Pump(int timeoutMilliseconds = 0)
    {
        EnsureOpen();
        if (timeoutMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutMilliseconds));
        }

        RequeueReturnedCaptureBuffers();
        var poll = new LinuxPollDescriptor
        {
            FileDescriptor = _fileDescriptor,
            Events = (short)(
                V4l2Constants.PollInput |
                V4l2Constants.PollOutput)
        };
        int result;
        do
        {
            result = LinuxMediaNative.Poll(
                &poll,
                1,
                timeoutMilliseconds);
        }
        while (result < 0 &&
               Marshal.GetLastPInvokeError() ==
               V4l2Constants.ErrorInterrupted);
        if (result < 0)
        {
            ThrowNative("poll the V4L2 encoder");
        }
        ReclaimInputSlots();
    }

    internal void BeginDrain()
    {
        EnsureOpen();
        if (_draining)
        {
            return;
        }
        V4l2EncoderCommand command = new()
        {
            Command = 1
        };
        Ioctl(
            V4l2Constants.EncoderCommand,
            &command,
            "begin V4L2 encoder drain");
        _draining = true;
    }

    public void Dispose()
    {
        lock (_captureGate)
        {
            if (_disposeRequested)
            {
                return;
            }
            _disposeRequested = true;
        }

        int fileDescriptor =
            Interlocked.Exchange(
                ref _fileDescriptor,
                -1);
        if (fileDescriptor >= 0)
        {
            if (_inputStreaming)
            {
                TryStreamOff(
                    fileDescriptor,
                    V4l2Constants.VideoOutputMultiPlanar);
            }
            if (_captureStreaming)
            {
                TryStreamOff(
                    fileDescriptor,
                    V4l2Constants.VideoCaptureMultiPlanar);
            }
            LinuxMediaNative.Close(fileDescriptor);
        }

        foreach (ref InputSlot slot in
                 _inputSlots.AsSpan())
        {
            slot.Owner?.Dispose();
            slot = default;
        }
        _inputSlots = [];

        lock (_captureGate)
        {
            TryReleaseCaptureMappingsLocked();
        }
    }

    internal void ReturnCapture(int index)
    {
        lock (_captureGate)
        {
            if (!_disposeRequested)
            {
                if (_returnedCount ==
                    _returnedCaptureIndices.Length)
                {
                    throw new InvalidOperationException(
                        "The bounded encoded CAPTURE return queue overflowed.");
                }
                _returnedCaptureIndices[
                    _returnedTail] = index;
                _returnedTail =
                    (_returnedTail + 1) %
                    _returnedCaptureIndices.Length;
                _returnedCount++;
            }
            _outstandingAccessUnits--;
            TryReleaseCaptureMappingsLocked();
        }
    }

    private void ConfigureCodedCapture()
    {
        V4l2Format format = new()
        {
            Type =
                V4l2Constants.VideoCaptureMultiPlanar
        };
        format.Pixel.Width = _width;
        format.Pixel.Height = _height;
        format.Pixel.PixelFormat =
            V4l2Constants.H264;
        format.Pixel.Field =
            V4l2Constants.FieldNone;
        format.Pixel.PlaneCount = 1;
        uint maximumCodedFrame = checked(
            Math.Max(
                1_048_576u,
                _bitrate / 4));
        format.Pixel.SetPlane(
            0,
            maximumCodedFrame);
        Ioctl(
            V4l2Constants.SetFormat,
            &format,
            "configure the H.264 CAPTURE format");
        if (format.Pixel.PixelFormat !=
                V4l2Constants.H264 ||
            format.Pixel.PlaneCount != 1)
        {
            throw new NotSupportedException(
                "The V4L2 encoder rejected H.264 CAPTURE.");
        }
    }

    private void ConfigureRawInput()
    {
        uint formatCode = _multiPlaneInput
            ? V4l2Constants.Nv12MultiPlanar
            : V4l2Constants.Nv12;
        V4l2Format format = new()
        {
            Type =
                V4l2Constants.VideoOutputMultiPlanar
        };
        format.Pixel.Width = _width;
        format.Pixel.Height = _height;
        format.Pixel.PixelFormat = formatCode;
        format.Pixel.Field =
            V4l2Constants.FieldNone;
        format.Pixel.PlaneCount =
            _multiPlaneInput
                ? (byte)2
                : (byte)1;
        format.Pixel.SetPlane(
            0,
            checked(_width * _height),
            _width);
        if (_multiPlaneInput)
        {
            format.Pixel.SetPlane(
                1,
                checked(
                    _width *
                    ((_height + 1) / 2)),
                _width);
        }
        else
        {
            format.Pixel.SetPlane(
                0,
                checked(
                    _width * _height +
                    _width *
                    ((_height + 1) / 2)),
                _width);
        }

        Ioctl(
            V4l2Constants.SetFormat,
            &format,
            "configure the NV12 OUTPUT format");
        if (format.Pixel.PixelFormat != formatCode ||
            format.Pixel.PlaneCount is 0 or > 2)
        {
            throw new NotSupportedException(
                "The V4L2 encoder rejected the requested NV12 layout.");
        }
        _inputPlaneCount =
            format.Pixel.PlaneCount;
        for (int index = 0;
             index < _inputPlaneCount;
             index++)
        {
            uint size =
                format.Pixel
                    .GetPlane(index)
                    .SizeImage;
            if (size == 0)
            {
                throw new InvalidDataException(
                    "The V4L2 encoder reported an empty raw plane.");
            }
            _inputPlaneLengths[index] = size;
        }
    }

    private void ConfigureBitrate()
    {
        V4l2Control control = new()
        {
            Id =
                V4l2Constants.VideoBitrateControl,
            Value = checked((int)_bitrate)
        };
        Ioctl(
            V4l2Constants.SetControl,
            &control,
            "configure the V4L2 encoder bitrate");
    }

    private void ConfigureFrameRate()
    {
        V4l2StreamParameters parameters = new()
        {
            Type =
                V4l2Constants.VideoOutputMultiPlanar,
            TimePerFrameNumerator =
                _frameRateDenominator,
            TimePerFrameDenominator =
                _frameRateNumerator
        };
        Ioctl(
            V4l2Constants.SetStreamParameters,
            &parameters,
            "configure the V4L2 encoder frame rate");
    }

    private void AllocateInputSlots()
    {
        V4l2RequestBuffers request = new()
        {
            Count = RequestedInputBuffers,
            Type =
                V4l2Constants.VideoOutputMultiPlanar,
            Memory =
                V4l2Constants.MemoryDmaBuf
        };
        Ioctl(
            V4l2Constants.RequestBuffers,
            &request,
            "allocate V4L2 DMA-BUF input slots");
        ValidateBufferCount(
            request.Count,
            "OUTPUT");
        _inputSlots =
            new InputSlot[request.Count];
    }

    private void AllocateCaptureBuffers()
    {
        V4l2RequestBuffers request = new()
        {
            Count = RequestedCaptureBuffers,
            Type =
                V4l2Constants.VideoCaptureMultiPlanar,
            Memory =
                V4l2Constants.MemoryMap
        };
        Ioctl(
            V4l2Constants.RequestBuffers,
            &request,
            "allocate V4L2 coded CAPTURE buffers");
        ValidateBufferCount(
            request.Count,
            "CAPTURE");

        var buffers =
            new CaptureBuffer[request.Count];
        try
        {
            for (uint index = 0;
                 index < request.Count;
                 index++)
            {
                V4l2Plane* planes =
                    stackalloc V4l2Plane[1];
                new Span<V4l2Plane>(
                        planes,
                        1)
                    .Clear();
                V4l2Buffer buffer =
                    CreateBuffer(
                        index,
                        V4l2Constants
                            .VideoCaptureMultiPlanar,
                        V4l2Constants.MemoryMap,
                        planes,
                        1);
                Ioctl(
                    V4l2Constants.QueryBuffer,
                    &buffer,
                    "query a coded CAPTURE buffer");
                if (buffer.Length != 1 ||
                    planes[0].Length == 0)
                {
                    throw new InvalidDataException(
                        "The coded CAPTURE queue must expose one non-empty plane.");
                }
                void* address =
                    LinuxMediaNative.MapMemory(
                        null,
                        planes[0].Length,
                        V4l2Constants.ProtectRead,
                        V4l2Constants.MapShared,
                        _fileDescriptor,
                        checked(
                            (nint)planes[0].Memory));
                if (address == s_mapFailed)
                {
                    ThrowNative(
                        "map a coded CAPTURE buffer");
                }
                buffers[index] =
                    new CaptureBuffer(
                        (byte*)address,
                        planes[0].Length);
            }
        }
        catch
        {
            ReleaseCaptureMappings(buffers);
            throw;
        }
        _captureBuffers = buffers;
        _returnedCaptureIndices =
            new int[buffers.Length];
    }

    private void QueueEveryCaptureBuffer()
    {
        for (int index = 0;
             index < _captureBuffers.Length;
             index++)
        {
            QueueCaptureBuffer(index);
        }
    }

    private void QueueCaptureBuffer(int index)
    {
        ref CaptureBuffer capture =
            ref _captureBuffers[index];
        V4l2Plane* planes =
            stackalloc V4l2Plane[1];
        new Span<V4l2Plane>(planes, 1).Clear();
        planes[0].Length =
            checked((uint)capture.Length);
        V4l2Buffer buffer = CreateBuffer(
            checked((uint)index),
            V4l2Constants.VideoCaptureMultiPlanar,
            V4l2Constants.MemoryMap,
            planes,
            1);
        Ioctl(
            V4l2Constants.QueueBuffer,
            &buffer,
            "queue a coded CAPTURE buffer");
        capture.Queued = true;
    }

    private void ReclaimInputSlots()
    {
        while (true)
        {
            V4l2Plane* planes =
                stackalloc V4l2Plane[2];
            new Span<V4l2Plane>(planes, 2)
                .Clear();
            V4l2Buffer buffer = CreateBuffer(
                0,
                V4l2Constants.VideoOutputMultiPlanar,
                V4l2Constants.MemoryDmaBuf,
                planes,
                checked((int)_inputPlaneCount));
            if (!TryIoctl(
                    V4l2Constants.DequeueBuffer,
                    &buffer))
            {
                return;
            }
            if (buffer.Index >= _inputSlots.Length)
            {
                throw new InvalidDataException(
                    "The V4L2 encoder returned an invalid OUTPUT index.");
            }
            ref InputSlot slot =
                ref _inputSlots[
                    checked((int)buffer.Index)];
            if (!slot.Queued)
            {
                throw new InvalidDataException(
                    "The V4L2 encoder dequeued an unowned OUTPUT slot.");
            }
            slot.Queued = false;
            slot.Owner?.Dispose();
            slot.Owner = null;
        }
    }

    private void RequeueReturnedCaptureBuffers()
    {
        while (true)
        {
            int index;
            lock (_captureGate)
            {
                if (_disposeRequested ||
                    _returnedCount == 0)
                {
                    return;
                }
                index =
                    _returnedCaptureIndices[
                        _returnedHead];
                _returnedHead =
                    (_returnedHead + 1) %
                    _returnedCaptureIndices.Length;
                _returnedCount--;
            }
            QueueCaptureBuffer(index);
        }
    }

    private void EnqueueReturnedCapture(
        int index,
        bool ownsLease)
    {
        lock (_captureGate)
        {
            if (!_disposeRequested)
            {
                if (_returnedCount ==
                    _returnedCaptureIndices.Length)
                {
                    throw new InvalidOperationException(
                        "The bounded encoded CAPTURE return queue overflowed.");
                }
                _returnedCaptureIndices[
                    _returnedTail] = index;
                _returnedTail =
                    (_returnedTail + 1) %
                    _returnedCaptureIndices.Length;
                _returnedCount++;
            }
            if (ownsLease)
            {
                _outstandingAccessUnits--;
                TryReleaseCaptureMappingsLocked();
            }
        }
    }

    private void TryReleaseCaptureMappingsLocked()
    {
        if (!_disposeRequested ||
            _outstandingAccessUnits != 0 ||
            _captureMappingsReleased)
        {
            return;
        }
        _captureMappingsReleased = true;
        ReleaseCaptureMappings(
            _captureBuffers);
        _captureBuffers = [];
        _returnedCaptureIndices = [];
    }

    private static void ReleaseCaptureMappings(
        Span<CaptureBuffer> buffers)
    {
        foreach (ref CaptureBuffer capture in
                 buffers)
        {
            if (capture.Address != null &&
                capture.Address != s_mapFailed)
            {
                _ = LinuxMediaNative.UnmapMemory(
                    capture.Address,
                    capture.Length);
            }
            capture = default;
        }
    }

    private void StreamOn(uint queueType)
    {
        uint type = queueType;
        Ioctl(
            V4l2Constants.StreamOn,
            &type,
            "start a V4L2 encoder queue");
    }

    private static void TryStreamOff(
        int fileDescriptor,
        uint queueType)
    {
        uint type = queueType;
        _ = LinuxMediaNative.Ioctl(
            fileDescriptor,
            V4l2Constants.StreamOff,
            &type);
    }

    private static V4l2Buffer CreateBuffer(
        uint index,
        uint type,
        uint memoryType,
        V4l2Plane* planes,
        int planeCount) =>
        new()
        {
            Index = index,
            Type = type,
            MemoryType = memoryType,
            Planes = (nint)planes,
            Length = checked((uint)planeCount)
        };

    private void Ioctl(
        nuint request,
        void* value,
        string operation)
    {
        if (!TryIoctl(
                request,
                value,
                allowAgain: false))
        {
            throw new InvalidOperationException(
                $"Could not {operation}.");
        }
    }

    private bool TryIoctl(
        nuint request,
        void* value,
        bool allowAgain = true)
    {
        int result;
        do
        {
            result = LinuxMediaNative.Ioctl(
                _fileDescriptor,
                request,
                value);
        }
        while (result < 0 &&
               Marshal.GetLastPInvokeError() ==
               V4l2Constants.ErrorInterrupted);
        if (result >= 0)
        {
            return true;
        }
        int error =
            Marshal.GetLastPInvokeError();
        if (allowAgain &&
            error == V4l2Constants.ErrorAgain)
        {
            return false;
        }
        throw new InvalidOperationException(
            $"V4L2 encoder ioctl 0x{request:X} failed with errno {error}.");
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(
            _disposeRequested,
            this);
        if (_fileDescriptor < 0)
        {
            throw new InvalidOperationException(
                "The V4L2 encoder is not open.");
        }
    }

    private static void SetTimestamp(
        ref V4l2Buffer buffer,
        TimeSpan value)
    {
        long microseconds = value.Ticks / 10;
        buffer.TimestampSeconds =
            Math.DivRem(
                microseconds,
                1_000_000,
                out long remainder);
        buffer.TimestampMicroseconds =
            remainder;
    }

    private static TimeSpan GetTimestamp(
        in V4l2Buffer buffer)
    {
        long microseconds = checked(
            buffer.TimestampSeconds *
            1_000_000 +
            buffer.TimestampMicroseconds);
        return TimeSpan.FromTicks(
            checked(microseconds * 10));
    }

    private static void ValidateBufferCount(
        uint count,
        string queue)
    {
        if (count is 0 or >
            V4l2Constants.MaximumQueueBuffers)
        {
            throw new InvalidDataException(
                $"The V4L2 encoder {queue} queue returned unsupported buffer count {count}.");
        }
    }

    private static void ThrowNative(
        string operation)
    {
        int error =
            Marshal.GetLastPInvokeError();
        throw new InvalidOperationException(
            $"Could not {operation}; errno {error}.");
    }

    private struct InputSlot
    {
        internal bool Queued;
        internal IDisposable? Owner;
    }

    private struct CaptureBuffer
    {
        internal CaptureBuffer(
            byte* address,
            nuint length)
        {
            Address = address;
            Length = length;
        }

        internal byte* Address;
        internal nuint Length;
        internal bool Queued;
    }
}
