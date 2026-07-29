using ProGPU.Backend;
using Silk.NET.WebGPU;
using System.Runtime.InteropServices;
using ProGPU.Media.Containers;

namespace ProGPU.Linux.Media;

internal enum V4l2DecodedPixelFormat
{
    Unsupported,
    Bgra8,
    Rgba8,
    Nv12,
    P010
}

internal enum V4l2DecoderPumpResult
{
    Idle,
    Progress,
    SourceChanged,
    EndOfStream
}

internal readonly record struct V4l2DecodedFrame(
    long Sequence,
    TimeSpan PresentationTime,
    uint Width,
    uint Height,
    V4l2DecodedPixelFormat PixelFormat,
    ProGpuDmaBufDescriptor DmaBuf,
    IDisposable Owner)
{
    internal bool TryCreateExternalDescriptor(
        out ProGpuExternalTextureDescriptor descriptor)
    {
        TextureFormat format = PixelFormat switch
        {
            V4l2DecodedPixelFormat.Bgra8 =>
                TextureFormat.Bgra8Unorm,
            V4l2DecodedPixelFormat.Rgba8 =>
                TextureFormat.Rgba8Unorm,
            _ => TextureFormat.Undefined
        };
        if (format == TextureFormat.Undefined)
        {
            descriptor = default;
            return false;
        }

        descriptor =
            new ProGpuExternalTextureDescriptor(
                ProGpuExternalTextureHandleKind.DmaBuf,
                (nint)DmaBuf.Plane0.FileDescriptor,
                Width,
                Height,
                format,
                TextureUsage.TextureBinding,
                GpuTextureAlphaMode.Straight,
                IsInitialized: true)
            {
                DmaBuf = DmaBuf
            };
        return true;
    }

    internal bool TryCreatePlanarExternalDescriptors(
        out ProGpuExternalTextureDescriptor luma,
        out ProGpuExternalTextureDescriptor chroma)
    {
        bool isNv12 =
            PixelFormat ==
            V4l2DecodedPixelFormat.Nv12;
        bool isP010 =
            PixelFormat ==
            V4l2DecodedPixelFormat.P010;
        if ((!isNv12 && !isP010) ||
            DmaBuf.PlaneCount is 0 or > 2)
        {
            luma = default;
            chroma = default;
            return false;
        }

        ProGpuDmaBufPlane sourceLuma =
            DmaBuf.Plane0;
        ProGpuDmaBufPlane sourceChroma =
            DmaBuf.PlaneCount == 2
                ? DmaBuf.Plane1
                : new ProGpuDmaBufPlane(
                    sourceLuma.FileDescriptor,
                    checked(
                        sourceLuma.Offset +
                        (ulong)sourceLuma.Stride *
                        Height),
                    sourceLuma.Stride);
        var lumaDmaBuf =
            new ProGpuDmaBufDescriptor(
                isP010
                    ? V4l2Constants.DrmR16
                    : V4l2Constants.DrmR8,
                DmaBuf.DrmModifier,
                1,
                sourceLuma);
        var chromaDmaBuf =
            new ProGpuDmaBufDescriptor(
                isP010
                    ? V4l2Constants.DrmGr1616
                    : V4l2Constants.DrmGr88,
                DmaBuf.DrmModifier,
                1,
                sourceChroma);
        luma =
            new ProGpuExternalTextureDescriptor(
                ProGpuExternalTextureHandleKind.DmaBuf,
                (nint)sourceLuma.FileDescriptor,
                Width,
                Height,
                isP010
                    ? ProGpuTextureFormats.R16Unorm
                    : TextureFormat.R8Unorm,
                TextureUsage.TextureBinding,
                GpuTextureAlphaMode.Straight,
                IsInitialized: true)
            {
                DmaBuf = lumaDmaBuf
            };
        chroma =
            new ProGpuExternalTextureDescriptor(
                ProGpuExternalTextureHandleKind.DmaBuf,
                (nint)sourceChroma.FileDescriptor,
                (Width + 1) / 2,
                (Height + 1) / 2,
                isP010
                    ? ProGpuTextureFormats.RG16Unorm
                    : TextureFormat.RG8Unorm,
                TextureUsage.TextureBinding,
                GpuTextureAlphaMode.Straight,
                IsInitialized: true)
            {
                DmaBuf = chromaDmaBuf
            };
        return true;
    }
}

/// <summary>
/// Drives the Linux V4L2 stateful decoder interface with MMAP compressed-input
/// buffers and exported DMA-BUF capture buffers. Queue work is O(B + P) for B
/// compressed bytes and P capture planes; steady-state storage is bounded by
/// the driver-selected OUTPUT and CAPTURE queue sizes. Decoded pixels are
/// never CPU-mapped or copied.
/// </summary>
internal sealed unsafe class V4l2StatefulVideoDecoder : IDisposable
{
    private const uint RequestedOutputBuffers = 6;
    private const uint RequestedCaptureBuffers = 10;
    private static readonly void* s_mapFailed = (void*)(nint)(-1);

    private readonly object _returnGate = new();
    private readonly string _devicePath;
    private readonly uint _codedFormat;
    private readonly uint _codedWidth;
    private readonly uint _codedHeight;
    private readonly uint _maximumAccessUnitSize;
    private readonly bool _preferNv12Capture;

    private int _fileDescriptor = -1;
    private OutputBuffer[] _outputBuffers = [];
    private CaptureBuffer[] _captureBuffers = [];
    private int[] _returnedCaptureIndices = [];
    private int _returnedHead;
    private int _returnedTail;
    private int _returnedCount;
    private int _outstandingCaptureLeases;
    private uint _captureWidth;
    private uint _captureHeight;
    private uint _captureFourCc;
    private V4l2DecodedPixelFormat _decodedPixelFormat;
    private bool _outputStreaming;
    private bool _captureStreaming;
    private bool _captureConfigured;
    private bool _draining;
    private bool _endOfStreamReached;
    private bool _disposeRequested;
    private bool _captureDescriptorsReleased;

    internal V4l2StatefulVideoDecoder(
        string devicePath,
        IsoBmffTrack track,
        bool preferNv12Capture = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        ArgumentNullException.ThrowIfNull(track);
        if (track.Kind != IsoBmffTrackKind.Video)
        {
            throw new ArgumentException(
                "A video track is required.",
                nameof(track));
        }

        _devicePath = devicePath;
        _preferNv12Capture = preferNv12Capture;
        _codedFormat = track.Codec switch
        {
            IsoBmffCodec.H264 => V4l2Constants.H264,
            IsoBmffCodec.H265 => V4l2Constants.H265,
            _ => throw new NotSupportedException(
                $"The V4L2 stateful lane does not support {track.Codec}.")
        };
        _codedWidth = track.Width;
        _codedHeight = track.Height;
        uint largest = 1;
        foreach (IsoBmffSample sample in track.Samples)
        {
            largest = Math.Max(
                largest,
                checked((uint)sample.Size));
        }
        _maximumAccessUnitSize =
            checked(Math.Max(largest * 4, 1_048_576u));
    }

    internal bool IsCaptureConfigured => _captureConfigured;
    internal V4l2DecodedPixelFormat DecodedPixelFormat =>
        _decodedPixelFormat;
    internal uint CaptureWidth => _captureWidth;
    internal uint CaptureHeight => _captureHeight;
    internal bool HasQueuedOutput
    {
        get
        {
            foreach (OutputBuffer output in _outputBuffers)
            {
                if (output.Queued)
                {
                    return true;
                }
            }
            return false;
        }
    }
    internal bool EndOfStreamReached =>
        _endOfStreamReached;

    internal void Open()
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        if (_fileDescriptor >= 0)
        {
            return;
        }
        if (!OperatingSystem.IsLinux() ||
            nint.Size != 8)
        {
            throw new PlatformNotSupportedException(
                "The V4L2 media provider requires 64-bit Linux.");
        }

        int fileDescriptor = LinuxMediaNative.Open(
            _devicePath,
            V4l2Constants.OpenReadWrite |
            V4l2Constants.OpenNonBlocking |
            V4l2Constants.OpenCloseOnExec,
            0);
        if (fileDescriptor < 0)
        {
            ThrowNative("open the V4L2 decoder");
        }

        _fileDescriptor = fileDescriptor;
        try
        {
            SubscribeToSourceChanges();
            ConfigureOutputFormat();
            AllocateOutputBuffers();
            StreamOn(V4l2Constants.VideoOutputMultiPlanar);
            _outputStreaming = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool TryQueueAccessUnit(
        ReadOnlySpan<byte> accessUnit,
        TimeSpan presentationTime)
    {
        EnsureOpen();
        if (accessUnit.IsEmpty)
        {
            throw new ArgumentException(
                "An encoded access unit cannot be empty.",
                nameof(accessUnit));
        }

        ReclaimOutputBuffers();
        for (int index = 0;
             index < _outputBuffers.Length;
             index++)
        {
            ref OutputBuffer output =
                ref _outputBuffers[index];
            if (output.Queued)
            {
                continue;
            }
            if ((nuint)accessUnit.Length >
                output.Length)
            {
                throw new InvalidDataException(
                    $"The encoded access unit ({accessUnit.Length} bytes) exceeds the driver OUTPUT buffer ({output.Length} bytes).");
            }

            accessUnit.CopyTo(
                new Span<byte>(
                    output.Address,
                    accessUnit.Length));
            V4l2Plane* planes =
                stackalloc V4l2Plane[1];
            new Span<V4l2Plane>(
                    planes,
                    1)
                .Clear();
            planes[0].BytesUsed =
                checked((uint)accessUnit.Length);
            planes[0].Length =
                checked((uint)output.Length);
            V4l2Buffer buffer = CreateBuffer(
                checked((uint)index),
                V4l2Constants.VideoOutputMultiPlanar,
                planes,
                1);
            buffer.Flags =
                V4l2Constants.BufferFlagTimestampCopy;
            SetTimestamp(
                ref buffer,
                presentationTime);
            Ioctl(
                V4l2Constants.QueueBuffer,
                &buffer,
                "queue a compressed V4L2 access unit");
            output.Queued = true;
            return true;
        }
        return false;
    }

    internal V4l2DecoderPumpResult Pump(
        int timeoutMilliseconds = 0)
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
                V4l2Constants.PollOutput |
                V4l2Constants.PollPriority)
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
            ThrowNative("poll the V4L2 decoder");
        }

        bool progress = ReclaimOutputBuffers();
        if (DequeueSourceChangeEvents())
        {
            return V4l2DecoderPumpResult.SourceChanged;
        }
        return progress
            ? V4l2DecoderPumpResult.Progress
            : V4l2DecoderPumpResult.Idle;
    }

    internal void ConfigureCapture()
    {
        EnsureOpen();
        if (_captureConfigured)
        {
            return;
        }

        V4l2Format format = new()
        {
            Type =
                V4l2Constants.VideoCaptureMultiPlanar
        };
        Ioctl(
            V4l2Constants.GetFormat,
            &format,
            "query the decoded V4L2 format");

        uint selected = SelectSampleableCaptureFormat();
        if (selected != 0 &&
            selected != format.Pixel.PixelFormat)
        {
            format.Pixel.PixelFormat = selected;
            Ioctl(
                V4l2Constants.SetFormat,
                &format,
                "select a WebGPU-sampleable V4L2 capture format");
        }

        V4l2DecodedPixelFormat decodedFormat =
            ClassifyCaptureFormat(
                format.Pixel.PixelFormat);
        if (decodedFormat ==
            V4l2DecodedPixelFormat.Unsupported)
        {
            throw new NotSupportedException(
                $"V4L2 selected unsupported capture fourcc {FormatFourCc(format.Pixel.PixelFormat)}. The zero-copy provider currently accepts linear BGRA, RGBA, NV12/NV12M, and P010 DMA-BUF output.");
        }
        if (format.Pixel.PlaneCount is 0 or >
            V4l2Constants.MaximumPlanes)
        {
            throw new InvalidDataException(
                "The V4L2 capture format reported an invalid plane count.");
        }

        _captureWidth = format.Pixel.Width;
        _captureHeight = format.Pixel.Height;
        _captureFourCc = format.Pixel.PixelFormat;
        _decodedPixelFormat = decodedFormat;
        AllocateCaptureBuffers(
            in format.Pixel);
        QueueEveryCaptureBuffer();
        StreamOn(
            V4l2Constants.VideoCaptureMultiPlanar);
        _captureStreaming = true;
        _captureConfigured = true;
    }

    internal bool TryDequeueFrame(
        out V4l2DecodedFrame frame)
    {
        EnsureOpen();
        RequeueReturnedCaptureBuffers();
        if (!_captureConfigured)
        {
            frame = default;
            return false;
        }

        V4l2Plane* planes =
            stackalloc V4l2Plane[
                V4l2Constants.MaximumPlanes];
        new Span<V4l2Plane>(
                planes,
                V4l2Constants.MaximumPlanes)
            .Clear();
        V4l2Buffer buffer = CreateBuffer(
            0,
            V4l2Constants.VideoCaptureMultiPlanar,
            planes,
            V4l2Constants.MaximumPlanes);
        if (!TryIoctl(
                V4l2Constants.DequeueBuffer,
                &buffer))
        {
            frame = default;
            return false;
        }
        if (buffer.Index >=
            _captureBuffers.Length)
        {
            throw new InvalidDataException(
                "The V4L2 driver returned an invalid CAPTURE buffer index.");
        }

        ref CaptureBuffer capture =
            ref _captureBuffers[
                checked((int)buffer.Index)];
        if (!capture.Queued)
        {
            throw new InvalidDataException(
                "The V4L2 driver dequeued a CAPTURE buffer that was not queued.");
        }
        capture.Queued = false;
        bool isLast =
            (buffer.Flags &
             V4l2Constants.BufferFlagLast) != 0;
        _endOfStreamReached |= isLast;
        bool hasPayload = false;
        for (uint plane = 0;
             plane < buffer.Length;
             plane++)
        {
            hasPayload |=
                planes[plane].BytesUsed >
                planes[plane].DataOffset;
        }
        if (isLast && !hasPayload)
        {
            EnqueueReturnedCapture(
                checked((int)buffer.Index),
                ownsLease: false);
            frame = default;
            return false;
        }
        if ((buffer.Flags &
             V4l2Constants.BufferFlagError) != 0)
        {
            EnqueueReturnedCapture(
                checked((int)buffer.Index),
                ownsLease: false);
            frame = default;
            return false;
        }

        Interlocked.Increment(
            ref _outstandingCaptureLeases);
        var owner = new CaptureLease(
            this,
            checked((int)buffer.Index));
        frame = new V4l2DecodedFrame(
            buffer.Sequence,
            GetTimestamp(in buffer),
            _captureWidth,
            _captureHeight,
            _decodedPixelFormat,
            capture.DmaBuf,
            owner);
        return true;
    }

    internal void BeginDrain()
    {
        EnsureOpen();
        if (_draining)
        {
            return;
        }
        V4l2DecoderCommand command =
            new()
            {
                Command = 1
            };
        Ioctl(
            V4l2Constants.DecoderCommand,
            &command,
            "begin V4L2 decoder drain");
        _draining = true;
    }

    public void Dispose()
    {
        lock (_returnGate)
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
            if (_captureStreaming)
            {
                TryStreamOff(
                    fileDescriptor,
                    V4l2Constants.VideoCaptureMultiPlanar);
            }
            if (_outputStreaming)
            {
                TryStreamOff(
                    fileDescriptor,
                    V4l2Constants.VideoOutputMultiPlanar);
            }
            foreach (OutputBuffer output in _outputBuffers)
            {
                if (output.Address != null &&
                    output.Address != s_mapFailed)
                {
                    _ = LinuxMediaNative.UnmapMemory(
                        output.Address,
                        output.Length);
                }
            }
            LinuxMediaNative.Close(fileDescriptor);
        }

        _outputBuffers = [];
        lock (_returnGate)
        {
            TryReleaseCaptureDescriptorsLocked();
        }
    }

    private void SubscribeToSourceChanges()
    {
        V4l2EventSubscription subscription =
            new()
            {
                Type =
                    V4l2Constants.EventSourceChange
            };
        Ioctl(
            V4l2Constants.SubscribeEvent,
            &subscription,
            "subscribe to V4L2 source-change events");
    }

    private void ConfigureOutputFormat()
    {
        V4l2Format format = new()
        {
            Type =
                V4l2Constants.VideoOutputMultiPlanar
        };
        format.Pixel.Width = _codedWidth;
        format.Pixel.Height = _codedHeight;
        format.Pixel.PixelFormat = _codedFormat;
        format.Pixel.Field = V4l2Constants.FieldNone;
        format.Pixel.PlaneCount = 1;
        format.Pixel.SetPlane(
            0,
            _maximumAccessUnitSize);
        Ioctl(
            V4l2Constants.SetFormat,
            &format,
            "configure the coded V4L2 OUTPUT format");
        if (format.Pixel.PixelFormat !=
            _codedFormat)
        {
            throw new NotSupportedException(
                "The V4L2 decoder rejected the requested coded format.");
        }
    }

    private void AllocateOutputBuffers()
    {
        V4l2RequestBuffers request = new()
        {
            Count = RequestedOutputBuffers,
            Type =
                V4l2Constants.VideoOutputMultiPlanar,
            Memory = V4l2Constants.MemoryMap
        };
        Ioctl(
            V4l2Constants.RequestBuffers,
            &request,
            "allocate V4L2 OUTPUT buffers");
        ValidateBufferCount(
            request.Count,
            "OUTPUT");

        var buffers =
            new OutputBuffer[request.Count];
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
                V4l2Buffer buffer = CreateBuffer(
                    index,
                    V4l2Constants.VideoOutputMultiPlanar,
                    planes,
                    1);
                Ioctl(
                    V4l2Constants.QueryBuffer,
                    &buffer,
                    "query a V4L2 OUTPUT buffer");
                if (buffer.Length != 1 ||
                    planes[0].Length == 0)
                {
                    throw new InvalidDataException(
                        "The coded V4L2 OUTPUT queue must expose one non-empty plane.");
                }
                void* address =
                    LinuxMediaNative.MapMemory(
                        null,
                        planes[0].Length,
                        V4l2Constants.ProtectRead |
                        V4l2Constants.ProtectWrite,
                        V4l2Constants.MapShared,
                        _fileDescriptor,
                        checked((nint)planes[0].Memory));
                if (address == s_mapFailed)
                {
                    ThrowNative(
                        "map a V4L2 OUTPUT buffer");
                }
                buffers[index] =
                    new OutputBuffer(
                        address,
                        planes[0].Length);
            }
            _outputBuffers = buffers;
        }
        catch
        {
            foreach (OutputBuffer output in buffers)
            {
                if (output.Address != null &&
                    output.Address != s_mapFailed)
                {
                    _ = LinuxMediaNative.UnmapMemory(
                        output.Address,
                        output.Length);
                }
            }
            throw;
        }
    }

    private uint SelectSampleableCaptureFormat()
    {
        uint selected = 0;
        int selectedPriority = int.MaxValue;
        for (uint index = 0;
             index < 256;
             index++)
        {
            V4l2FormatDescription format = new()
            {
                Index = index,
                Type =
                    V4l2Constants.VideoCaptureMultiPlanar
            };
            if (!TryIoctl(
                    V4l2Constants.EnumerateFormat,
                    &format))
            {
                break;
            }

            int priority =
                CapturePriority(format.PixelFormat);
            if (priority < selectedPriority)
            {
                selectedPriority = priority;
                selected = format.PixelFormat;
            }
        }
        return selected;
    }

    private void AllocateCaptureBuffers(
        in V4l2PixelFormatMultiPlanar format)
    {
        V4l2RequestBuffers request = new()
        {
            Count = RequestedCaptureBuffers,
            Type =
                V4l2Constants.VideoCaptureMultiPlanar,
            Memory = V4l2Constants.MemoryMap
        };
        Ioctl(
            V4l2Constants.RequestBuffers,
            &request,
            "allocate V4L2 CAPTURE buffers");
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
                buffers[index] =
                    QueryAndExportCaptureBuffer(
                        index,
                        in format);
            }
        }
        catch
        {
            CloseCaptureDescriptors(buffers);
            throw;
        }

        _captureBuffers = buffers;
        _returnedCaptureIndices =
            new int[buffers.Length];
    }

    private CaptureBuffer QueryAndExportCaptureBuffer(
        uint index,
        in V4l2PixelFormatMultiPlanar format)
    {
        V4l2Plane* planes =
            stackalloc V4l2Plane[
                V4l2Constants.MaximumPlanes];
        new Span<V4l2Plane>(
                planes,
                V4l2Constants.MaximumPlanes)
            .Clear();
        V4l2Buffer buffer = CreateBuffer(
            index,
            V4l2Constants.VideoCaptureMultiPlanar,
            planes,
            V4l2Constants.MaximumPlanes);
        Ioctl(
            V4l2Constants.QueryBuffer,
            &buffer,
            "query a V4L2 CAPTURE buffer");
        if (buffer.Length !=
            format.PlaneCount)
        {
            throw new InvalidDataException(
                "The V4L2 CAPTURE buffer plane count does not match its format.");
        }

        Span<int> descriptors =
            stackalloc int[
                V4l2Constants.MaximumPlanes];
        descriptors.Fill(-1);
        try
        {
            for (uint plane = 0;
                 plane < buffer.Length;
                 plane++)
            {
                V4l2ExportBuffer export = new()
                {
                    Type =
                        V4l2Constants.VideoCaptureMultiPlanar,
                    Index = index,
                    Plane = plane,
                    Flags =
                        V4l2Constants.OpenCloseOnExec
                };
                Ioctl(
                    V4l2Constants.ExportBuffer,
                    &export,
                    "export a V4L2 CAPTURE DMA-BUF");
                descriptors[
                    checked((int)plane)] =
                    export.FileDescriptor;
            }

            ProGpuDmaBufPlane plane0 =
                CreateDmaBufPlane(
                    in format,
                    descriptors,
                    0);
            ProGpuDmaBufPlane plane1 =
                buffer.Length > 1
                    ? CreateDmaBufPlane(
                        in format,
                        descriptors,
                        1)
                    : default;
            ProGpuDmaBufPlane plane2 =
                buffer.Length > 2
                    ? CreateDmaBufPlane(
                        in format,
                        descriptors,
                        2)
                    : default;
            ProGpuDmaBufPlane plane3 =
                buffer.Length > 3
                    ? CreateDmaBufPlane(
                        in format,
                        descriptors,
                        3)
                    : default;

            var dmaBuf =
                new ProGpuDmaBufDescriptor(
                    DrmFormatFor(
                        format.PixelFormat),
                    DrmModifier: 0,
                    buffer.Length,
                    plane0,
                    plane1,
                    plane2,
                    plane3);
            return new CaptureBuffer(
                dmaBuf,
                checked((int)buffer.Length));
        }
        catch
        {
            foreach (int descriptor in descriptors)
            {
                if (descriptor >= 0)
                {
                    LinuxMediaNative.Close(descriptor);
                }
            }
            throw;
        }
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
            stackalloc V4l2Plane[
                capture.PlaneCount];
        new Span<V4l2Plane>(
                planes,
                capture.PlaneCount)
            .Clear();
        V4l2Buffer buffer = CreateBuffer(
            checked((uint)index),
            V4l2Constants.VideoCaptureMultiPlanar,
            planes,
            capture.PlaneCount);
        Ioctl(
            V4l2Constants.QueueBuffer,
            &buffer,
            "queue a V4L2 CAPTURE buffer");
        capture.Queued = true;
    }

    private bool ReclaimOutputBuffers()
    {
        bool progress = false;
        while (true)
        {
            V4l2Plane* planes =
                stackalloc V4l2Plane[1];
            new Span<V4l2Plane>(
                    planes,
                    1)
                .Clear();
            V4l2Buffer buffer = CreateBuffer(
                0,
                V4l2Constants.VideoOutputMultiPlanar,
                planes,
                1);
            if (!TryIoctl(
                    V4l2Constants.DequeueBuffer,
                    &buffer))
            {
                return progress;
            }
            if (buffer.Index >=
                _outputBuffers.Length)
            {
                throw new InvalidDataException(
                    "The V4L2 driver returned an invalid OUTPUT buffer index.");
            }
            _outputBuffers[
                checked((int)buffer.Index)]
                .Queued = false;
            progress = true;
        }
    }

    private bool DequeueSourceChangeEvents()
    {
        bool changed = false;
        while (true)
        {
            V4l2Event mediaEvent = default;
            if (!TryIoctl(
                    V4l2Constants.DequeueEvent,
                    &mediaEvent))
            {
                return changed;
            }
            if (mediaEvent.Type ==
                    V4l2Constants.EventSourceChange &&
                (mediaEvent.SourceChangeFlags &
                 V4l2Constants
                     .EventSourceChangeResolution) != 0)
            {
                changed = true;
            }
        }
    }

    private void RequeueReturnedCaptureBuffers()
    {
        while (true)
        {
            int index;
            lock (_returnGate)
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
        lock (_returnGate)
        {
            if (!_disposeRequested)
            {
                if (_returnedCount ==
                    _returnedCaptureIndices.Length)
                {
                    throw new InvalidOperationException(
                        "The bounded V4L2 CAPTURE return queue overflowed.");
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
                _outstandingCaptureLeases--;
                TryReleaseCaptureDescriptorsLocked();
            }
        }
    }

    private void TryReleaseCaptureDescriptorsLocked()
    {
        if (!_disposeRequested ||
            _outstandingCaptureLeases != 0 ||
            _captureDescriptorsReleased)
        {
            return;
        }
        _captureDescriptorsReleased = true;
        CloseCaptureDescriptors(
            _captureBuffers);
        _captureBuffers = [];
        _returnedCaptureIndices = [];
    }

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
            $"V4L2 ioctl 0x{request:X} failed with errno {error}.");
    }

    private void StreamOn(uint queueType)
    {
        uint type = queueType;
        Ioctl(
            V4l2Constants.StreamOn,
            &type,
            "start a V4L2 queue");
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

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(
            _disposeRequested,
            this);
        if (_fileDescriptor < 0)
        {
            throw new InvalidOperationException(
                "The V4L2 decoder is not open.");
        }
    }

    private static V4l2Buffer CreateBuffer(
        uint index,
        uint type,
        V4l2Plane* planes,
        int planeCount) =>
        new()
        {
            Index = index,
            Type = type,
            MemoryType =
                V4l2Constants.MemoryMap,
            Planes = (nint)planes,
            Length = checked((uint)planeCount)
        };

    private static void SetTimestamp(
        ref V4l2Buffer buffer,
        TimeSpan value)
    {
        long microseconds =
            value.Ticks / 10;
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
            buffer.TimestampSeconds * 1_000_000 +
            buffer.TimestampMicroseconds);
        return TimeSpan.FromTicks(
            checked(microseconds * 10));
    }

    private int CapturePriority(uint format)
    {
        if (_preferNv12Capture)
        {
            return
                format ==
                    V4l2Constants.Nv12MultiPlanar
                    ? 0 :
                format ==
                    V4l2Constants.Nv12
                    ? 1 :
                int.MaxValue;
        }
        return
            format == V4l2Constants.Abgr32 ? 0 :
            format == V4l2Constants.Xbgr32 ? 1 :
            format == V4l2Constants.Argb32 ? 2 :
            format == V4l2Constants.Xrgb32 ? 3 :
            format == V4l2Constants.Nv12MultiPlanar ? 4 :
            format == V4l2Constants.Nv12 ? 5 :
            format == V4l2Constants.P010 ? 6 :
            int.MaxValue;
    }

    private static V4l2DecodedPixelFormat
        ClassifyCaptureFormat(uint format) =>
        format == V4l2Constants.Abgr32 ||
        format == V4l2Constants.Xbgr32
            ? V4l2DecodedPixelFormat.Bgra8
            : format == V4l2Constants.Argb32 ||
              format == V4l2Constants.Xrgb32
                ? V4l2DecodedPixelFormat.Rgba8
                : format == V4l2Constants.Nv12 ||
                  format ==
                  V4l2Constants.Nv12MultiPlanar
                    ? V4l2DecodedPixelFormat.Nv12
                    : format == V4l2Constants.P010
                        ? V4l2DecodedPixelFormat.P010
                    : V4l2DecodedPixelFormat.Unsupported;

    private static uint DrmFormatFor(uint format) =>
        format == V4l2Constants.Abgr32
            ? V4l2Constants.DrmArgb8888
            : format == V4l2Constants.Xbgr32
                ? V4l2Constants.DrmXrgb8888
                : format == V4l2Constants.Argb32
                    ? V4l2Constants.DrmAbgr8888
                    : format == V4l2Constants.Xrgb32
                        ? V4l2Constants.DrmXbgr8888
                        : format == V4l2Constants.Nv12 ||
                          format ==
                          V4l2Constants.Nv12MultiPlanar
                            ? V4l2Constants.DrmNv12
                            : format ==
                              V4l2Constants.P010
                                ? V4l2Constants.DrmP010
                            : throw new NotSupportedException(
                                $"Unsupported V4L2 capture fourcc {FormatFourCc(format)}.");

    private static string FormatFourCc(uint value) =>
        string.Create(
            4,
            value,
            static (destination, state) =>
            {
                destination[0] =
                    (char)(state & 0xFF);
                destination[1] =
                    (char)((state >> 8) & 0xFF);
                destination[2] =
                    (char)((state >> 16) & 0xFF);
                destination[3] =
                    (char)((state >> 24) & 0xFF);
            });

    private static ProGpuDmaBufPlane CreateDmaBufPlane(
        in V4l2PixelFormatMultiPlanar format,
        ReadOnlySpan<int> descriptors,
        int index)
    {
        V4l2PlanePixelFormat planeFormat =
            format.GetPlane(index);
        if (planeFormat.BytesPerLine == 0)
        {
            throw new InvalidDataException(
                "The V4L2 CAPTURE plane did not report a row stride.");
        }
        return new ProGpuDmaBufPlane(
            descriptors[index],
            0,
            planeFormat.BytesPerLine);
    }

    private static void ValidateBufferCount(
        uint count,
        string queue)
    {
        if (count is 0 or >
            V4l2Constants.MaximumQueueBuffers)
        {
            throw new InvalidDataException(
                $"The V4L2 {queue} queue returned unsupported buffer count {count}.");
        }
    }

    private static void CloseCaptureDescriptors(
        Span<CaptureBuffer> buffers)
    {
        foreach (ref CaptureBuffer buffer in buffers)
        {
            for (int plane = 0;
                 plane < buffer.PlaneCount;
                 plane++)
            {
                int descriptor =
                    buffer.DmaBuf
                        .GetPlane(plane)
                        .FileDescriptor;
                if (descriptor >= 0)
                {
                    LinuxMediaNative.Close(descriptor);
                }
            }
            buffer = default;
        }
    }

    private static void ThrowNative(string operation)
    {
        int error =
            Marshal.GetLastPInvokeError();
        throw new InvalidOperationException(
            $"Could not {operation}; errno {error}.");
    }

    private struct OutputBuffer
    {
        internal OutputBuffer(
            void* address,
            nuint length)
        {
            Address = address;
            Length = length;
        }

        internal void* Address;
        internal nuint Length;
        internal bool Queued;
    }

    private struct CaptureBuffer
    {
        internal CaptureBuffer(
            in ProGpuDmaBufDescriptor dmaBuf,
            int planeCount)
        {
            DmaBuf = dmaBuf;
            PlaneCount = planeCount;
        }

        internal ProGpuDmaBufDescriptor DmaBuf;
        internal int PlaneCount;
        internal bool Queued;
    }

    private sealed class CaptureLease : IDisposable
    {
        private V4l2StatefulVideoDecoder? _owner;
        private readonly int _index;

        internal CaptureLease(
            V4l2StatefulVideoDecoder owner,
            int index)
        {
            _owner = owner;
            _index = index;
        }

        public void Dispose()
        {
            V4l2StatefulVideoDecoder? owner =
                Interlocked.Exchange(
                    ref _owner,
                    null);
            owner?.EnqueueReturnedCapture(
                _index,
                ownsLease: true);
        }
    }
}

[StructLayout(LayoutKind.Sequential, Size = 64)]
internal unsafe struct V4l2FormatDescription
{
    internal uint Index;
    internal uint Type;
    internal uint Flags;
    private fixed byte _description[32];
    internal uint PixelFormat;
    internal uint MediaBusCode;
    private fixed uint _reserved[3];
}
