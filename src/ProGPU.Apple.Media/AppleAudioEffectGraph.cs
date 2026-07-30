using AudioToolbox;
using AVFoundation;
using CoreMedia;
using MediaToolbox;
using ProGPU.Media.Audio;
using System.Runtime.InteropServices;

namespace ProGPU.Apple.Media;

/// <summary>
/// AVFoundation audio-mix tap graph. Source audio remains in the native
/// playback pipeline. Float PCM is processed in place when interleaved, or
/// through one prepare-time bounded scratch buffer when planar. The callback
/// is O(P * F * C) for P processors, F frames, and C channels, with no
/// callback allocation, locking, dispatch, or I/O.
/// </summary>
internal sealed class AppleAudioEffectGraph : IDisposable
{
    private readonly AVPlayerItem _item;
    private readonly AVMutableAudioMix _mix;
    private readonly AVMutableAudioMixInputParameters[]
        _parameters;
    private readonly AppleAudioEffectTap[] _taps;
    private IMediaAudioProcessor[] _processors = [];
    private int _disposed;

    public AppleAudioEffectGraph(AVPlayerItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _item = item;

        AVAssetTrack[] tracks =
            item.Asset.GetTracks(AVMediaTypes.Audio);
        _parameters =
            new AVMutableAudioMixInputParameters[
                tracks.Length];
        _taps = new AppleAudioEffectTap[tracks.Length];
        try
        {
            for (int index = 0;
                 index < tracks.Length;
                 index++)
            {
                var tap = new AppleAudioEffectTap(
                    GetProcessors);
                AVMutableAudioMixInputParameters parameters =
                    AVMutableAudioMixInputParameters.FromTrack(
                        tracks[index]);
                tap.AttachTo(parameters);
                _taps[index] = tap;
                _parameters[index] = parameters;
            }

            _mix = AVMutableAudioMix.Create();
            _mix.InputParameters = _parameters;
            _item.AudioMix = _mix;
        }
        catch
        {
            for (int index = 0;
                 index < _parameters.Length;
                 index++)
            {
                _parameters[index]?.Dispose();
                _taps[index]?.Dispose();
            }
            throw;
        }
    }

    public bool HasUnsupportedFormat
    {
        get
        {
            for (int index = 0;
                 index < _taps.Length;
                 index++)
            {
                if (_taps[index].HasUnsupportedFormat)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public void SetProcessors(
        IMediaAudioProcessor[] processors)
    {
        ArgumentNullException.ThrowIfNull(processors);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        Volatile.Write(ref _processors, processors);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _processors, []);
        _item.AudioMix = null;
        for (int index = 0;
             index < _parameters.Length;
             index++)
        {
            _parameters[index]?.Dispose();
            _taps[index]?.Dispose();
        }
        _mix.Dispose();
    }

    private IMediaAudioProcessor[] GetProcessors() =>
        Volatile.Read(ref _processors);
}

internal sealed unsafe class AppleAudioEffectTap :
    IDisposable
{
    private readonly Func<IMediaAudioProcessor[]>?
        _getProcessors;
    private readonly IMediaAudioProcessor[]?
        _fixedProcessors;
    private AppleAudioTapFormat _format =
        AppleAudioTapFormat.Unsupported;
    private float[] _scratch = [];
    private int _unsupportedFormat;
    private int _disposed;

    public AppleAudioEffectTap(
        Func<IMediaAudioProcessor[]> getProcessors)
    {
        _getProcessors =
            getProcessors ??
            throw new ArgumentNullException(
                nameof(getProcessors));
        NativeTap = AppleAudioTapNative.Create(this);
    }

    public AppleAudioEffectTap(
        IMediaAudioProcessor[] processors)
    {
        ArgumentNullException.ThrowIfNull(processors);
        for (int index = 0; index < processors.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(
                processors[index]);
        }
        _fixedProcessors = processors;
        NativeTap = AppleAudioTapNative.Create(this);
    }

    public nint NativeTap { get; private set; }

    public bool HasUnsupportedFormat =>
        Volatile.Read(ref _unsupportedFormat) != 0;

    public void AttachTo(
        AVMutableAudioMixInputParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(
            NativeTap == 0,
            this);
        AppleAudioTapNative.SetAudioTapProcessor(
            parameters.Handle,
            NativeTap);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        nint tap = NativeTap;
        NativeTap = 0;
        if (tap != 0)
        {
            AppleAudioTapNative.Release(tap);
        }
    }

    internal void Prepare(
        nint maxFrames,
        ref AudioStreamBasicDescription description)
    {
        _format = AppleAudioTapFormat.Create(
            in description);
        if (!_format.IsSupported ||
            maxFrames <= 0 ||
            maxFrames > int.MaxValue)
        {
            _scratch = [];
            Volatile.Write(
                ref _unsupportedFormat,
                1);
            return;
        }

        IMediaAudioProcessor[] processors =
            _fixedProcessors ??
            _getProcessors!();
        var mediaFormat =
            new MediaAudioFormat(
                _format.SampleRate,
                _format.ChannelCount);
        for (int index = 0;
             index < processors.Length;
             index++)
        {
            if (processors[index] is
                    IMediaAudioProcessorTiming timed &&
                timed.GetTiming(
                    in mediaFormat) !=
                MediaAudioProcessorTiming.Zero)
            {
                _scratch = [];
                Volatile.Write(
                    ref _unsupportedFormat,
                    1);
                return;
            }
        }

        int samples = checked(
            (int)maxFrames *
            _format.ChannelCount);
        _scratch = _format.IsNonInterleaved
            ? new float[samples]
            : [];
        Volatile.Write(
            ref _unsupportedFormat,
            0);
    }

    internal void Unprepare()
    {
        _format = AppleAudioTapFormat.Unsupported;
        _scratch = [];
    }

    internal void Process(
        nint tap,
        nint requestedFrames,
        MTAudioProcessingTapFlags flags,
        nint bufferList,
        out nint framesOut,
        out MTAudioProcessingTapFlags flagsOut)
    {
        framesOut = 0;
        flagsOut = flags;
        try
        {
            MTAudioProcessingTapError error =
                AppleAudioTapNative.GetSourceAudio(
                    tap,
                    requestedFrames,
                    bufferList,
                    out flagsOut,
                    out CMTimeRange timeRange,
                    out framesOut);
            if (error !=
                    MTAudioProcessingTapError.None ||
                framesOut <= 0 ||
                framesOut > int.MaxValue)
            {
                return;
            }

            AppleAudioTapFormat format = _format;
            int frameCount = (int)framesOut;
            var buffers = new AudioBuffers(bufferList);
            if (!format.IsSupported ||
                !TryGetSamples(
                    buffers,
                    frameCount,
                    format,
                    out Span<float> samples,
                    out bool copyBack))
            {
                Volatile.Write(
                    ref _unsupportedFormat,
                    1);
                return;
            }

            var context =
                new MediaAudioProcessContext(
                    new MediaAudioFormat(
                        format.SampleRate,
                        format.ChannelCount),
                    frameCount,
                    ToTimeSpan(timeRange.Start));
            IMediaAudioProcessor[] processors =
                _fixedProcessors ??
                _getProcessors!();
            for (int index = 0;
                 index < processors.Length;
                 index++)
            {
                processors[index].Process(
                    samples,
                    in context);
            }

            if (copyBack)
            {
                CopyToPlanar(
                    buffers,
                    samples,
                    frameCount,
                    format.ChannelCount);
            }
        }
        catch
        {
            // Exceptions must never cross the native real-time callback.
            Volatile.Write(ref _unsupportedFormat, 1);
        }
    }

    private bool TryGetSamples(
        AudioBuffers buffers,
        int frameCount,
        in AppleAudioTapFormat format,
        out Span<float> samples,
        out bool copyBack)
    {
        int sampleCount = checked(
            frameCount *
            format.ChannelCount);
        if (!format.IsNonInterleaved)
        {
            if (buffers.Count < 1)
            {
                samples = default;
                copyBack = false;
                return false;
            }
            AudioBuffer buffer = buffers[0];
            int requiredBytes = checked(
                sampleCount *
                sizeof(float));
            if (buffer.Data == 0 ||
                buffer.DataByteSize < requiredBytes)
            {
                samples = default;
                copyBack = false;
                return false;
            }
            samples = new Span<float>(
                (void*)buffer.Data,
                sampleCount);
            copyBack = false;
            return true;
        }

        if (buffers.Count < format.ChannelCount ||
            _scratch.Length < sampleCount)
        {
            samples = default;
            copyBack = false;
            return false;
        }
        samples = _scratch.AsSpan(0, sampleCount);
        for (int channel = 0;
             channel < format.ChannelCount;
             channel++)
        {
            AudioBuffer buffer = buffers[channel];
            if (buffer.Data == 0 ||
                buffer.DataByteSize <
                    frameCount * sizeof(float))
            {
                samples = default;
                copyBack = false;
                return false;
            }
            var channelSamples =
                new ReadOnlySpan<float>(
                    (void*)buffer.Data,
                    frameCount);
            for (int frame = 0;
                 frame < frameCount;
                 frame++)
            {
                samples[
                    frame * format.ChannelCount +
                    channel] =
                    channelSamples[frame];
            }
        }
        copyBack = true;
        return true;
    }

    private static void CopyToPlanar(
        AudioBuffers buffers,
        ReadOnlySpan<float> samples,
        int frameCount,
        int channelCount)
    {
        for (int channel = 0;
             channel < channelCount;
             channel++)
        {
            AudioBuffer buffer = buffers[channel];
            var channelSamples =
                new Span<float>(
                    (void*)buffer.Data,
                    frameCount);
            for (int frame = 0;
                 frame < frameCount;
                 frame++)
            {
                channelSamples[frame] =
                    samples[
                        frame * channelCount +
                        channel];
            }
        }
    }

    private static TimeSpan ToTimeSpan(CMTime time) =>
        time.IsNumeric &&
        double.IsFinite(time.Seconds) &&
        time.Seconds > 0d
            ? TimeSpan.FromSeconds(time.Seconds)
            : TimeSpan.Zero;
}

/// <summary>
/// Minimal typed ownership wrapper around Apple's public audio-processing-tap
/// C contract. The GC handle is owned by the native tap's storage and is
/// released only from the native finalize callback, so AVFoundation may finish
/// asynchronous process/unprepare callbacks after the managed graph drops its
/// reference without observing a torn-down callback registration.
/// </summary>
internal static unsafe class AppleAudioTapNative
{
    private const string MediaToolboxLibrary =
        "/System/Library/Frameworks/MediaToolbox.framework/MediaToolbox";
    private const string CoreFoundationLibrary =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ObjectiveCLibrary =
        "/usr/lib/libobjc.A.dylib";
    private const uint PostEffects = 1u << 1;
    private static readonly nint s_setAudioTapProcessor =
        sel_registerName("setAudioTapProcessor:");

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Callbacks
    {
        public int Version;
        public nint ClientInfo;
        public delegate* unmanaged<nint, nint, nint*, void>
            Initialize;
        public delegate* unmanaged<nint, void> Finalize;
        public delegate* unmanaged<
            nint,
            nint,
            AudioStreamBasicDescription*,
            void> Prepare;
        public delegate* unmanaged<nint, void> Unprepare;
        public delegate* unmanaged<
            nint,
            nint,
            MTAudioProcessingTapFlags,
            nint,
            nint*,
            MTAudioProcessingTapFlags*,
            void> Process;
    }

    public static nint Create(AppleAudioEffectTap owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        GCHandle ownerHandle =
            GCHandle.Alloc(owner, GCHandleType.Normal);
        var callbacks = new Callbacks
        {
            Version = 0,
            ClientInfo = GCHandle.ToIntPtr(ownerHandle),
            Initialize = &Initialize,
            Finalize = &Finalize,
            Prepare = &Prepare,
            Unprepare = &Unprepare,
            Process = &Process
        };
        nint tap = 0;
        MTAudioProcessingTapError error =
            MTAudioProcessingTapCreate(
                0,
                &callbacks,
                PostEffects,
                &tap);
        if (error == MTAudioProcessingTapError.None &&
            tap != 0)
        {
            return tap;
        }

        ownerHandle.Free();
        throw new InvalidOperationException(
            $"MTAudioProcessingTapCreate failed: {error}.");
    }

    public static void SetAudioTapProcessor(
        nint parameters,
        nint tap)
    {
        ArgumentOutOfRangeException.ThrowIfZero(parameters);
        objc_msgSend(
            parameters,
            s_setAudioTapProcessor,
            tap);
    }

    public static MTAudioProcessingTapError GetSourceAudio(
        nint tap,
        nint requestedFrames,
        nint bufferList,
        out MTAudioProcessingTapFlags flags,
        out CMTimeRange timeRange,
        out nint framesOut) =>
        MTAudioProcessingTapGetSourceAudio(
            tap,
            requestedFrames,
            bufferList,
            out flags,
            out timeRange,
            out framesOut);

    public static void Release(nint tap) =>
        CFRelease(tap);

    [UnmanagedCallersOnly]
    private static void Initialize(
        nint tap,
        nint clientInfo,
        nint* storageOut)
    {
        if (storageOut != null)
        {
            *storageOut = clientInfo;
        }
    }

    [UnmanagedCallersOnly]
    private static void Finalize(nint tap)
    {
        nint storage = MTAudioProcessingTapGetStorage(tap);
        if (storage == 0)
        {
            return;
        }

        try
        {
            GCHandle ownerHandle =
                GCHandle.FromIntPtr(storage);
            if (ownerHandle.IsAllocated)
            {
                ownerHandle.Free();
            }
        }
        catch
        {
            // Native finalizers cannot propagate managed exceptions.
        }
    }

    [UnmanagedCallersOnly]
    private static void Prepare(
        nint tap,
        nint maxFrames,
        AudioStreamBasicDescription* description)
    {
        try
        {
            if (description is not null &&
                TryGetOwner(
                    tap,
                    out AppleAudioEffectTap owner))
            {
                owner.Prepare(
                    maxFrames,
                    ref *description);
            }
        }
        catch
        {
            // Native real-time callbacks cannot propagate exceptions.
        }
    }

    [UnmanagedCallersOnly]
    private static void Unprepare(nint tap)
    {
        try
        {
            if (TryGetOwner(
                    tap,
                    out AppleAudioEffectTap owner))
            {
                owner.Unprepare();
            }
        }
        catch
        {
            // Native real-time callbacks cannot propagate exceptions.
        }
    }

    [UnmanagedCallersOnly]
    private static void Process(
        nint tap,
        nint requestedFrames,
        MTAudioProcessingTapFlags flags,
        nint bufferList,
        nint* framesOut,
        MTAudioProcessingTapFlags* flagsOut)
    {
        nint producedFrames = 0;
        MTAudioProcessingTapFlags producedFlags = flags;
        try
        {
            if (TryGetOwner(
                    tap,
                    out AppleAudioEffectTap owner))
            {
                owner.Process(
                    tap,
                    requestedFrames,
                    flags,
                    bufferList,
                    out producedFrames,
                    out producedFlags);
            }
        }
        catch
        {
            // Native real-time callbacks cannot propagate exceptions.
        }

        if (framesOut != null)
        {
            *framesOut = producedFrames;
        }
        if (flagsOut != null)
        {
            *flagsOut = producedFlags;
        }
    }

    private static bool TryGetOwner(
        nint tap,
        out AppleAudioEffectTap owner)
    {
        nint storage = MTAudioProcessingTapGetStorage(tap);
        if (storage == 0)
        {
            owner = null!;
            return false;
        }

        AppleAudioEffectTap? resolved =
            GCHandle.FromIntPtr(storage).Target
                as AppleAudioEffectTap;
        owner = resolved!;
        return resolved is not null;
    }

    [DllImport(MediaToolboxLibrary)]
    private static extern MTAudioProcessingTapError
        MTAudioProcessingTapCreate(
            nint allocator,
            Callbacks* callbacks,
            uint flags,
            nint* tapOut);

    [DllImport(MediaToolboxLibrary)]
    private static extern nint
        MTAudioProcessingTapGetStorage(nint tap);

    [DllImport(MediaToolboxLibrary)]
    private static extern MTAudioProcessingTapError
        MTAudioProcessingTapGetSourceAudio(
            nint tap,
            nint requestedFrames,
            nint bufferList,
            out MTAudioProcessingTapFlags flagsOut,
            out CMTimeRange timeRangeOut,
            out nint framesOut);

    [DllImport(CoreFoundationLibrary)]
    private static extern void CFRelease(nint value);

    [DllImport(ObjectiveCLibrary)]
    private static extern nint sel_registerName(
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend(
        nint receiver,
        nint selector,
        nint value);
}

internal readonly record struct AppleAudioTapFormat(
    bool IsSupported,
    bool IsNonInterleaved,
    int SampleRate,
    int ChannelCount)
{
    public static AppleAudioTapFormat Unsupported =>
        default;

    public static AppleAudioTapFormat Create(
        in AudioStreamBasicDescription value)
    {
        AudioFormatFlags flags = value.FormatFlags;
        bool supported =
            value.Format == AudioFormatType.LinearPCM &&
            (flags & AudioFormatFlags.IsFloat) != 0 &&
            (flags & AudioFormatFlags.IsBigEndian) == 0 &&
            (flags & AudioFormatFlags.IsPacked) != 0 &&
            value.BitsPerChannel == 32 &&
            value.SampleRate is >= 8_000d and <= 768_000d &&
            value.ChannelsPerFrame is >= 1 and <= 64;
        return supported
            ? new AppleAudioTapFormat(
                true,
                (flags &
                 AudioFormatFlags.IsNonInterleaved) != 0,
                checked(
                    (int)Math.Round(
                        value.SampleRate,
                        MidpointRounding.AwayFromZero)),
                value.ChannelsPerFrame)
            : Unsupported;
    }
}
