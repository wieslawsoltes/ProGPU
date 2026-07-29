using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Media.Audio;

namespace ProGPU.Linux.Media;

internal enum PipeWireAudioRole
{
    Music,
    Movie,
    Communication,
    Game,
    Notification
}

/// <summary>
/// Native PipeWire float-PCM output backed by a fixed SPSC ring. The realtime
/// callback performs O(F*C) copies/effect multiplies for F requested frames and
/// C channels, takes no locks, and allocates no managed memory. Ring storage is
/// fixed at construction and never grows on the audio thread.
/// </summary>
internal sealed class PipeWirePcmOutput :
    IDisposable
{
    private const uint DirectionOutput = 1;
    private const uint AnyNode = uint.MaxValue;
    private const uint StreamAutoConnect = 1 << 0;
    private const uint StreamMapBuffers = 1 << 2;
    private const uint StreamRealtimeProcess = 1 << 4;
    private static readonly TimeSpan s_joinTimeout =
        TimeSpan.FromSeconds(5);
    private static readonly object s_runtimeGate =
        new();
    private static int s_runtimeReferences;

    private readonly float[] _samples;
    private readonly int _ringMask;
    private readonly uint _sampleRate;
    private readonly uint _channels;
    private readonly PipeWireAudioRole _role;
    private readonly MediaAudioFormat _mediaFormat;
    private readonly MediaAudioProcessorChain
        _processorChain = new();
    private readonly ManualResetEventSlim _started =
        new(false);
    private readonly TaskCompletionSource _ready =
        new(
            TaskCreationOptions
                .RunContinuationsAsynchronously);
    private Thread? _thread;
    private long _readSample;
    private long _writeSample;
    private long _volumeBits =
        BitConverter.DoubleToInt64Bits(1d);
    private long _balanceBits;
    private long _underflows;
    private long _processingErrors;
    private long _processedFrames;
    private long _presentationBaseTicks;
    private int _resetGeneration;
    private nint _mainLoop;
    private nint _stream;
    private nint _events;
    private nint _callbackHandle;
    private int _startRequested;
    private int _stopRequested;
    private int _disposed;

    internal PipeWirePcmOutput(
        uint sampleRate,
        uint channels,
        PipeWireAudioRole role =
            PipeWireAudioRole.Movie,
        int ringFrameCapacity = 16_384)
    {
        if (sampleRate is < 8_000 or > 384_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate));
        }
        if (channels is 0 or > 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channels));
        }
        if (ringFrameCapacity < 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ringFrameCapacity));
        }

        int requestedSamples = checked(
            ringFrameCapacity *
            checked((int)channels));
        int capacity = 1;
        while (capacity < requestedSamples)
        {
            capacity = checked(capacity << 1);
        }
        _samples = new float[capacity];
        _ringMask = capacity - 1;
        _sampleRate = sampleRate;
        _channels = channels;
        _role = role;
        _mediaFormat =
            new MediaAudioFormat(
                checked((int)sampleRate),
                checked((int)channels));
    }

    internal uint SampleRate => _sampleRate;
    internal uint Channels => _channels;
    internal long UnderflowCount =>
        Interlocked.Read(ref _underflows);
    internal long ProcessingErrorCount =>
        Interlocked.Read(
            ref _processingErrors);
    internal int QueuedFrames
    {
        get
        {
            long samples =
                Volatile.Read(ref _writeSample) -
                Volatile.Read(ref _readSample);
            return checked(
                (int)(samples / _channels));
        }
    }
    internal TimeSpan QueuedDuration =>
        TimeSpan.FromSeconds(
            QueuedFrames /
            (double)_sampleRate);

    internal unsafe bool TryGetClock(
        out TimeSpan position,
        out TimeSpan latency)
    {
        nint stream =
            Volatile.Read(ref _stream);
        if (stream == 0)
        {
            position = default;
            latency = default;
            return false;
        }

        PipeWireTime value = default;
        if (PipeWireNative.StreamGetTime(
                stream,
                &value,
                (nuint)sizeof(PipeWireTime)) < 0 ||
            value.RateNumerator == 0 ||
            value.RateDenominator == 0)
        {
            position = default;
            latency = default;
            return false;
        }

        ulong now =
            PipeWireNative.StreamGetNanoseconds(
                stream);
        long nanoseconds =
            now > (ulong)long.MaxValue
                ? long.MaxValue
                : (long)now;
        long difference =
            Math.Max(
                0,
                nanoseconds - value.Now);
        double elapsedGraphTicks =
            value.RateDenominator *
            (double)difference /
            (value.RateNumerator *
             1_000_000_000d);
        double remainingGraphDelay =
            Math.Max(
                0d,
                value.Delay -
                elapsedGraphTicks);
        double deviceDelayFrames =
            remainingGraphDelay *
            _sampleRate *
            value.RateNumerator /
            value.RateDenominator;
        double pendingFrames =
            value.Queued +
            value.Buffered +
            deviceDelayFrames;
        long processed =
            Volatile.Read(
                ref _processedFrames);
        double presentedFrames =
            Math.Max(
                0d,
                processed -
                pendingFrames);
        long baseTicks =
            Volatile.Read(
                ref _presentationBaseTicks);
        position =
            TimeSpan.FromTicks(
                checked(
                    baseTicks +
                    (long)Math.Round(
                        presentedFrames *
                        TimeSpan.TicksPerSecond /
                        _sampleRate)));
        latency =
            TimeSpan.FromSeconds(
                Math.Max(
                    0d,
                    pendingFrames /
                    _sampleRate));
        return true;
    }

    internal async ValueTask StartAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        cancellationToken
            .ThrowIfCancellationRequested();
        if (Interlocked.Exchange(
                ref _startRequested,
                1) != 0)
        {
            throw new InvalidOperationException(
                "The PipeWire stream is already started.");
        }

        AcquireRuntime();
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ProGPU PipeWire PCM"
        };
        _thread.Start();
        _started.Wait(cancellationToken);
        await _ready.Task
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Adds complete interleaved frames without blocking. The returned value is
    /// the number of frames accepted; the caller retains any unwritten tail.
    /// </summary>
    internal int Write(
        ReadOnlySpan<float> interleavedSamples)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (interleavedSamples.Length %
                _channels != 0)
        {
            throw new ArgumentException(
                "Interleaved PCM must contain complete frames.",
                nameof(interleavedSamples));
        }

        long write =
            Volatile.Read(ref _writeSample);
        long read =
            Volatile.Read(ref _readSample);
        int free = checked(
            _samples.Length -
            (int)(write - read));
        int count =
            Math.Min(
                free,
                interleavedSamples.Length);
        count -=
            count % checked((int)_channels);
        if (count == 0)
        {
            return 0;
        }

        int first = Math.Min(
            count,
            _samples.Length -
            ((int)write & _ringMask));
        interleavedSamples[..first].CopyTo(
            _samples.AsSpan(
                (int)write & _ringMask,
                first));
        if (first < count)
        {
            interleavedSamples[
                    first..count]
                .CopyTo(
                    _samples.AsSpan(
                        0,
                        count - first));
        }
        Volatile.Write(
            ref _writeSample,
            write + count);
        return checked(
            count /
            (int)_channels);
    }

    internal void SetVolume(
        double volume,
        double balance,
        bool muted)
    {
        if (!double.IsFinite(volume) ||
            !double.IsFinite(balance))
        {
            throw new ArgumentOutOfRangeException(
                nameof(volume));
        }
        volume =
            Math.Clamp(volume, 0d, 1d);
        balance =
            Math.Clamp(balance, -1d, 1d);
        Volatile.Write(
            ref _volumeBits,
            BitConverter.DoubleToInt64Bits(
                muted ? 0d : volume));
        Volatile.Write(
            ref _balanceBits,
            BitConverter.DoubleToInt64Bits(
                balance));
    }

    internal void Reset(
        TimeSpan presentationTime =
            default)
    {
        Interlocked.Increment(
            ref _resetGeneration);
        long write =
            Volatile.Read(ref _writeSample);
        Volatile.Write(
            ref _readSample,
            write);
        Volatile.Write(
            ref _processedFrames,
            0);
        Volatile.Write(
            ref _presentationBaseTicks,
            presentationTime.Ticks);
    }

    internal void SetProcessors(
        IEnumerable<IMediaAudioProcessor>
            processors)
    {
        _processorChain.SetProcessors(
            processors);
    }

    internal void ClearProcessors()
    {
        _processorChain.Clear();
    }

    internal void SetActive(bool active)
    {
        nint stream =
            Volatile.Read(ref _stream);
        if (stream != 0)
        {
            int result =
                PipeWireNative.StreamSetActive(
                    stream,
                    active ? (byte)1 : (byte)0);
            if (result < 0)
            {
                throw new InvalidOperationException(
                    $"PipeWire could not change stream activity: {result}.");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(
                ref _disposed,
                1) != 0)
        {
            return;
        }

        Interlocked.Exchange(
            ref _stopRequested,
            1);
        nint mainLoop =
            Volatile.Read(ref _mainLoop);
        if (mainLoop != 0)
        {
            _ = PipeWireNative.MainLoopQuit(
                mainLoop);
        }
        Thread? thread = _thread;
        if (thread is not null &&
            thread != Thread.CurrentThread &&
            !thread.Join(s_joinTimeout))
        {
            _ready.TrySetException(
                new TimeoutException(
                    "The PipeWire loop did not stop within five seconds."));
        }
        _started.Dispose();
    }

    private unsafe void Run()
    {
        try
        {
            nint mainLoop =
                PipeWireNative.MainLoopNew(null);
            if (mainLoop == 0)
            {
                throw new InvalidOperationException(
                    "PipeWire could not create a main loop.");
            }
            Volatile.Write(
                ref _mainLoop,
                mainLoop);

            GCHandle callbackHandle =
                GCHandle.Alloc(this);
            _callbackHandle =
                GCHandle.ToIntPtr(
                    callbackHandle);
            _events = (nint)
                NativeMemory.AllocZeroed(
                    (nuint)sizeof(
                        PipeWireStreamEvents));
            PipeWireStreamEvents* events =
                (PipeWireStreamEvents*)_events;
            events->Version = 2;
            events->StateChanged =
                &OnStateChanged;
            events->Process =
                &OnProcess;

            nint properties =
                CreateProperties(_role);
            nint stream =
                PipeWireNative.StreamNewSimple(
                    PipeWireNative
                        .MainLoopGetLoop(
                            mainLoop),
                    "ProGPU media audio",
                    properties,
                    events,
                    (void*)_callbackHandle);
            if (stream == 0)
            {
                throw new InvalidOperationException(
                    "PipeWire could not create the PCM stream.");
            }
            Volatile.Write(
                ref _stream,
                stream);

            PipeWireAudioFormatPod format =
                PipeWireAudioFormatPod.Create(
                    _sampleRate,
                    _channels);
            PipeWireAudioFormatPod* formatPointer =
                &format;
            void** parameters =
                stackalloc void*[1];
            parameters[0] = formatPointer;
            int connect =
                PipeWireNative.StreamConnect(
                    stream,
                    DirectionOutput,
                    AnyNode,
                    StreamAutoConnect |
                    StreamMapBuffers |
                    StreamRealtimeProcess,
                    parameters,
                    1);
            if (connect < 0)
            {
                throw new InvalidOperationException(
                    $"PipeWire could not connect the PCM stream: {connect}.");
            }

            _started.Set();
            if (Volatile.Read(
                    ref _stopRequested) == 0)
            {
                int result =
                    PipeWireNative.MainLoopRun(
                        mainLoop);
                if (result < 0 &&
                    Volatile.Read(
                        ref _stopRequested) == 0)
                {
                    throw new InvalidOperationException(
                        $"The PipeWire main loop failed: {result}.");
                }
            }
        }
        catch (Exception exception)
        {
            _started.Set();
            _ready.TrySetException(
                exception);
        }
        finally
        {
            nint stream =
                Interlocked.Exchange(
                    ref _stream,
                    0);
            if (stream != 0)
            {
                PipeWireNative.StreamDestroy(
                    stream);
            }
            nint events =
                Interlocked.Exchange(
                    ref _events,
                    0);
            if (events != 0)
            {
                NativeMemory.Free(
                    (void*)events);
            }
            nint handle =
                Interlocked.Exchange(
                    ref _callbackHandle,
                    0);
            if (handle != 0)
            {
                GCHandle.FromIntPtr(
                        handle)
                    .Free();
            }
            nint mainLoop =
                Interlocked.Exchange(
                    ref _mainLoop,
                    0);
            if (mainLoop != 0)
            {
                PipeWireNative.MainLoopDestroy(
                    mainLoop);
            }
            ReleaseRuntime();
        }
    }

    private static unsafe nint CreateProperties(
        PipeWireAudioRole role)
    {
        ReadOnlySpan<byte> mediaType =
            "media.type\0"u8;
        ReadOnlySpan<byte> audio =
            "Audio\0"u8;
        ReadOnlySpan<byte> mediaCategory =
            "media.category\0"u8;
        ReadOnlySpan<byte> playback =
            "Playback\0"u8;
        ReadOnlySpan<byte> mediaRole =
            "media.role\0"u8;
        ReadOnlySpan<byte> roleValue =
            role switch
            {
                PipeWireAudioRole.Music =>
                    "Music\0"u8,
                PipeWireAudioRole
                    .Communication =>
                    "Communication\0"u8,
                PipeWireAudioRole.Game =>
                    "Game\0"u8,
                PipeWireAudioRole
                    .Notification =>
                    "Notification\0"u8,
                _ => "Movie\0"u8
            };
        fixed (byte* mediaTypePointer =
                   mediaType)
        fixed (byte* audioPointer = audio)
        fixed (byte* mediaCategoryPointer =
                   mediaCategory)
        fixed (byte* playbackPointer =
                   playback)
        fixed (byte* mediaRolePointer =
                   mediaRole)
        fixed (byte* roleValuePointer =
                   roleValue)
        {
            PipeWireDictionaryItem* items =
                stackalloc PipeWireDictionaryItem[3];
            items[0] = new PipeWireDictionaryItem(
                mediaTypePointer,
                audioPointer);
            items[1] = new PipeWireDictionaryItem(
                mediaCategoryPointer,
                playbackPointer);
            items[2] = new PipeWireDictionaryItem(
                mediaRolePointer,
                roleValuePointer);
            var dictionary =
                new PipeWireDictionary
                {
                    ItemCount = 3,
                    Items = items
                };
            nint properties =
                PipeWireNative.PropertiesNewDictionary(
                    &dictionary);
            if (properties == 0)
            {
                throw new InvalidOperationException(
                    "PipeWire could not allocate stream properties.");
            }
            return properties;
        }
    }

    [UnmanagedCallersOnly(
        CallConvs =
        [
            typeof(CallConvCdecl)
        ])]
    private static unsafe void OnStateChanged(
        void* data,
        int oldState,
        int newState,
        byte* error)
    {
        _ = oldState;
        PipeWirePcmOutput? owner =
            FromCallback(data);
        if (owner is null)
        {
            return;
        }
        if (newState >= 2)
        {
            owner._ready.TrySetResult();
        }
        else if (newState == -1)
        {
            string message =
                error == null
                    ? "unknown PipeWire error"
                    : Marshal.PtrToStringUTF8(
                          (nint)error) ??
                      "unknown PipeWire error";
            owner._ready.TrySetException(
                new InvalidOperationException(
                    message));
        }
    }

    [UnmanagedCallersOnly(
        CallConvs =
        [
            typeof(CallConvCdecl)
        ])]
    private static unsafe void OnProcess(void* data)
    {
        FromCallback(data)?.Process();
    }

    private static unsafe PipeWirePcmOutput?
        FromCallback(void* data)
    {
        if (data == null)
        {
            return null;
        }
        return GCHandle.FromIntPtr(
                (nint)data)
            .Target as PipeWirePcmOutput;
    }

    private unsafe void Process()
    {
        nint stream =
            Volatile.Read(ref _stream);
        if (stream == 0)
        {
            return;
        }
        PipeWireBuffer* wrapper =
            PipeWireNative.StreamDequeueBuffer(
                stream);
        if (wrapper == null)
        {
            Interlocked.Increment(
                ref _underflows);
            return;
        }

        SpaBuffer* buffer =
            wrapper->Buffer;
        if (buffer == null ||
            buffer->DataCount == 0 ||
            buffer->Data == null)
        {
            _ = PipeWireNative
                .StreamQueueBuffer(
                    stream,
                    wrapper);
            return;
        }
        SpaData* data =
            &buffer->Data[0];
        if (data->Data == null ||
            data->Chunk == null)
        {
            _ = PipeWireNative
                .StreamQueueBuffer(
                    stream,
                    wrapper);
            return;
        }

        uint bytesPerFrame =
            checked(_channels * 4);
        uint capacityFrames =
            data->MaximumSize /
            bytesPerFrame;
        uint requestedFrames =
            wrapper->Requested == 0
                ? capacityFrames
                : checked(
                    (uint)Math.Min(
                        wrapper->Requested,
                        capacityFrames));
        int requestedSamples =
            checked(
                (int)(requestedFrames *
                      _channels));
        Span<float> destination =
            new(
                data->Data,
                requestedSamples);
        try
        {
            CopyAndProcess(destination);
            long firstFrame =
                Interlocked.Add(
                    ref _processedFrames,
                    requestedFrames) -
                requestedFrames;
            var context =
                new MediaAudioProcessContext(
                    _mediaFormat,
                    checked(
                        (int)requestedFrames),
                    TimeSpan.FromTicks(
                        checked(
                            Volatile.Read(
                                ref _presentationBaseTicks) +
                            (long)Math.Round(
                                firstFrame *
                                (double)TimeSpan
                                    .TicksPerSecond /
                                _sampleRate))));
            _processorChain.Process(
                destination,
                in context);
        }
        catch
        {
            destination.Clear();
            Interlocked.Increment(
                ref _processingErrors);
        }

        data->Chunk->Offset = 0;
        data->Chunk->Size =
            checked(
                (uint)requestedSamples * 4);
        data->Chunk->Stride =
            checked((int)bytesPerFrame);
        data->Chunk->Flags = 0;
        wrapper->Size =
            requestedFrames;
        _ = PipeWireNative
            .StreamQueueBuffer(
                stream,
                wrapper);
    }

    private void CopyAndProcess(
        Span<float> destination)
    {
        int generation =
            Volatile.Read(
                ref _resetGeneration);
        long read =
            Volatile.Read(ref _readSample);
        long write =
            Volatile.Read(ref _writeSample);
        int available =
            checked((int)(write - read));
        int count =
            Math.Min(
                available,
                destination.Length);
        int first =
            Math.Min(
                count,
                _samples.Length -
                ((int)read & _ringMask));
        _samples.AsSpan(
                (int)read & _ringMask,
                first)
            .CopyTo(destination);
        if (first < count)
        {
            _samples.AsSpan(
                    0,
                    count - first)
                .CopyTo(
                    destination[first..]);
        }
        if (count < destination.Length)
        {
            destination[count..].Clear();
            Interlocked.Increment(
                ref _underflows);
        }
        if (generation ==
            Volatile.Read(
                ref _resetGeneration))
        {
            Volatile.Write(
                ref _readSample,
                read + count);
        }
        else
        {
            destination.Clear();
        }

        float volume = (float)
            BitConverter.Int64BitsToDouble(
                Volatile.Read(
                    ref _volumeBits));
        float balance = (float)
            BitConverter.Int64BitsToDouble(
                Volatile.Read(
                    ref _balanceBits));
        if (_channels == 2)
        {
            float left =
                volume *
                (balance > 0f
                    ? 1f - balance
                    : 1f);
            float right =
                volume *
                (balance < 0f
                    ? 1f + balance
                    : 1f);
            for (int index = 0;
                 index < destination.Length;
                 index += 2)
            {
                destination[index] *= left;
                destination[index + 1] *= right;
            }
        }
        else if (volume != 1f)
        {
            for (int index = 0;
                 index < destination.Length;
                 index++)
            {
                destination[index] *= volume;
            }
        }
    }

    private static unsafe void AcquireRuntime()
    {
        lock (s_runtimeGate)
        {
            if (s_runtimeReferences++ == 0)
            {
                PipeWireNative.Initialize(
                    null,
                    null);
            }
        }
    }

    private static void ReleaseRuntime()
    {
        lock (s_runtimeGate)
        {
            if (--s_runtimeReferences == 0)
            {
                PipeWireNative.Deinitialize();
            }
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PipeWireDictionaryItem
{
    internal PipeWireDictionaryItem(
        byte* key,
        byte* value)
    {
        Key = key;
        Value = value;
    }

    internal byte* Key;
    internal byte* Value;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PipeWireDictionary
{
    internal uint Flags;
    internal uint ItemCount;
    internal PipeWireDictionaryItem* Items;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PipeWireStreamEvents
{
    internal uint Version;
    internal delegate* unmanaged[Cdecl]<
        void*,
        void> Destroy;
    internal delegate* unmanaged[Cdecl]<
        void*,
        int,
        int,
        byte*,
        void> StateChanged;
    internal nint ControlInformation;
    internal nint InputOutputChanged;
    internal nint ParameterChanged;
    internal nint AddBuffer;
    internal nint RemoveBuffer;
    internal delegate* unmanaged[Cdecl]<
        void*,
        void> Process;
    internal nint Drained;
    internal nint Command;
    internal nint TriggerDone;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PipeWireBuffer
{
    internal SpaBuffer* Buffer;
    internal void* UserData;
    internal ulong Size;
    internal ulong Requested;
    internal ulong Time;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SpaBuffer
{
    internal uint MetadataCount;
    internal uint DataCount;
    internal void* Metadata;
    internal SpaData* Data;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SpaData
{
    internal uint Type;
    internal uint Flags;
    internal long FileDescriptor;
    internal uint MapOffset;
    internal uint MaximumSize;
    internal void* Data;
    internal SpaChunk* Chunk;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpaChunk
{
    internal uint Offset;
    internal uint Size;
    internal int Stride;
    internal int Flags;
}

[StructLayout(LayoutKind.Sequential, Size = 64)]
internal struct PipeWireTime
{
    internal long Now;
    internal uint RateNumerator;
    internal uint RateDenominator;
    internal ulong Ticks;
    internal long Delay;
    internal ulong Queued;
    internal ulong Buffered;
    internal uint QueuedBuffers;
    internal uint AvailableBuffers;
    internal ulong Size;
}

[StructLayout(LayoutKind.Explicit, Size = 136)]
internal struct PipeWireAudioFormatPod
{
    private const uint PodObject = 15;
    private const uint PodId = 3;
    private const uint PodInt = 4;
    private const uint ObjectFormat = 0x0004_0003;
    private const uint ParameterEnumerateFormat = 3;
    private const uint MediaType = 1;
    private const uint MediaSubtype = 2;
    private const uint AudioFormat = 0x0001_0001;
    private const uint AudioRate = 0x0001_0003;
    private const uint AudioChannels = 0x0001_0004;
    private const uint MediaAudio = 1;
    private const uint MediaRaw = 1;
    private const uint AudioFloat32LittleEndian = 0x011B;

    [FieldOffset(0)]
    private uint _size;

    [FieldOffset(4)]
    private uint _type;

    [FieldOffset(8)]
    private uint _objectType;

    [FieldOffset(12)]
    private uint _objectId;

    [FieldOffset(16)]
    private PipeWirePodProperty _mediaType;

    [FieldOffset(40)]
    private PipeWirePodProperty _mediaSubtype;

    [FieldOffset(64)]
    private PipeWirePodProperty _audioFormat;

    [FieldOffset(88)]
    private PipeWirePodProperty _audioRate;

    [FieldOffset(112)]
    private PipeWirePodProperty _audioChannels;

    internal static PipeWireAudioFormatPod Create(
        uint sampleRate,
        uint channels) =>
        new()
        {
            _size = 128,
            _type = PodObject,
            _objectType = ObjectFormat,
            _objectId =
                ParameterEnumerateFormat,
            _mediaType =
                PipeWirePodProperty.Create(
                    MediaType,
                    PodId,
                    MediaAudio),
            _mediaSubtype =
                PipeWirePodProperty.Create(
                    MediaSubtype,
                    PodId,
                    MediaRaw),
            _audioFormat =
                PipeWirePodProperty.Create(
                    AudioFormat,
                    PodId,
                    AudioFloat32LittleEndian),
            _audioRate =
                PipeWirePodProperty.Create(
                    AudioRate,
                    PodInt,
                    sampleRate),
            _audioChannels =
                PipeWirePodProperty.Create(
                    AudioChannels,
                    PodInt,
                    channels)
        };
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PipeWirePodProperty
{
    [FieldOffset(0)]
    private uint _key;

    [FieldOffset(4)]
    private uint _flags;

    [FieldOffset(8)]
    private uint _size;

    [FieldOffset(12)]
    private uint _type;

    [FieldOffset(16)]
    private uint _value;

    internal static PipeWirePodProperty Create(
        uint key,
        uint type,
        uint value) =>
        new()
        {
            _key = key,
            _size = 4,
            _type = type,
            _value = value
        };
}

internal static unsafe partial class PipeWireNative
{
    private const string Library =
        "libpipewire-0.3.so.0";

    [LibraryImport(
        Library,
        EntryPoint = "pw_init")]
    internal static partial void Initialize(
        int* argumentCount,
        byte*** argumentValues);

    [LibraryImport(
        Library,
        EntryPoint = "pw_deinit")]
    internal static partial void Deinitialize();

    [LibraryImport(
        Library,
        EntryPoint = "pw_main_loop_new")]
    internal static partial nint MainLoopNew(
        PipeWireDictionary* properties);

    [LibraryImport(
        Library,
        EntryPoint = "pw_main_loop_get_loop")]
    internal static partial nint MainLoopGetLoop(
        nint mainLoop);

    [LibraryImport(
        Library,
        EntryPoint = "pw_main_loop_run")]
    internal static partial int MainLoopRun(
        nint mainLoop);

    [LibraryImport(
        Library,
        EntryPoint = "pw_main_loop_quit")]
    internal static partial int MainLoopQuit(
        nint mainLoop);

    [LibraryImport(
        Library,
        EntryPoint = "pw_main_loop_destroy")]
    internal static partial void MainLoopDestroy(
        nint mainLoop);

    [LibraryImport(
        Library,
        EntryPoint = "pw_properties_new_dict")]
    internal static partial nint
        PropertiesNewDictionary(
            PipeWireDictionary* dictionary);

    [LibraryImport(
        Library,
        EntryPoint = "pw_stream_new_simple",
        StringMarshalling =
            StringMarshalling.Utf8)]
    internal static partial nint StreamNewSimple(
        nint loop,
        string name,
        nint properties,
        PipeWireStreamEvents* events,
        void* data);

    [LibraryImport(
        Library,
        EntryPoint = "pw_stream_destroy")]
    internal static partial void StreamDestroy(
        nint stream);

    [LibraryImport(
        Library,
        EntryPoint = "pw_stream_connect")]
    internal static partial int StreamConnect(
        nint stream,
        uint direction,
        uint targetId,
        uint flags,
        void** parameters,
        uint parameterCount);

    [LibraryImport(
        Library,
        EntryPoint = "pw_stream_set_active")]
    internal static partial int StreamSetActive(
        nint stream,
        byte active);

    [LibraryImport(
        Library,
        EntryPoint = "pw_stream_dequeue_buffer")]
    internal static partial PipeWireBuffer*
        StreamDequeueBuffer(nint stream);

    [LibraryImport(
        Library,
        EntryPoint = "pw_stream_queue_buffer")]
    internal static partial int StreamQueueBuffer(
        nint stream,
        PipeWireBuffer* buffer);

    [LibraryImport(
        Library,
        EntryPoint = "pw_stream_get_time_n")]
    internal static partial int StreamGetTime(
        nint stream,
        PipeWireTime* time,
        nuint size);

    [LibraryImport(
        Library,
        EntryPoint = "pw_stream_get_nsec")]
    internal static partial ulong
        StreamGetNanoseconds(nint stream);
}
