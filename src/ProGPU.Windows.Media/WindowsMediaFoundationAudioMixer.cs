using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

namespace ProGPU.Windows.Media;

/// <summary>
/// One immutable decoded-audio interval on the composition timeline.
/// </summary>
internal readonly record struct
    WindowsMediaFoundationAudioPlan(
    Uri SourceUri,
    long SourceStartTicks,
    long SourceEndTicks,
    long DestinationStartTicks,
    long DestinationEndTicks,
    WindowsPcm16MixLevels Levels,
    MediaCompositionEffectDefinition[]
        ProcessorDefinitions);

/// <summary>
/// Clean-room WinUI composition-audio schedule capture.
/// </summary>
/// <remarks>
/// Capture is O(C + B + O) time and storage for main clips, background
/// tracks, and overlays. Negative background delay advances the source start;
/// positive delay inserts silence. All intervals are clipped to the main
/// composition duration before native readers are created.
/// </remarks>
internal static class WindowsMediaFoundationAudioPlanner
{
    internal static bool TryCapture(
        MediaCompositionExportRequest request,
        MediaEffectRegistry effects,
        bool includeAudio,
        out WindowsMediaFoundationAudioPlan[] plans,
        out long compositionDurationTicks)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(effects);
        var captured =
            new List<WindowsMediaFoundationAudioPlan>();
        try
        {
            long timelineTicks = 0;
            for (int index = 0;
                 index < request.Clips.Count;
                 index++)
            {
                MediaCompositionExportClip clip =
                    request.Clips[index];
                long duration =
                    GetTrimmedDuration(
                        clip.OriginalDuration.Ticks,
                        clip.TrimTimeFromStart.Ticks,
                        clip.TrimTimeFromEnd.Ticks);
                if (!WindowsMediaFoundationCompositionExportProvider
                        .TryGetEffectiveAudioProcessing(
                            clip.Volume,
                            clip.AudioEffectDefinitions,
                            effects,
                            out MediaAudioStereoLevels
                                audioLevels,
                            out MediaCompositionEffectDefinition[]
                                processorDefinitions) ||
                    !WindowsPcm16MixLevels.TryCreate(
                        audioLevels,
                        out WindowsPcm16MixLevels
                            mixLevels))
                {
                    plans = [];
                    compositionDurationTicks = 0;
                    return false;
                }
                long destinationEnd =
                    checked(timelineTicks + duration);
                if (includeAudio &&
                    clip.SourceUri is
                    { IsAbsoluteUri: true } source &&
                    mixLevels is not
                    { Left: 0, Right: 0 })
                {
                    long sourceStart =
                        clip.TrimTimeFromStart.Ticks;
                    captured.Add(
                        new WindowsMediaFoundationAudioPlan(
                            source,
                            sourceStart,
                            checked(
                                sourceStart +
                                duration),
                            timelineTicks,
                            destinationEnd,
                            mixLevels,
                            processorDefinitions));
                }
                timelineTicks = destinationEnd;
            }
            compositionDurationTicks = timelineTicks;

            for (int index = 0;
                 index <
                    request.BackgroundAudioTracks.Count;
                 index++)
            {
                MediaCompositionExportAudioTrack track =
                    request.BackgroundAudioTracks[index];
                if (!track.SourceUri.IsAbsoluteUri ||
                    !double.IsFinite(track.Volume) ||
                    track.Volume is < 0d or > 1d ||
                    !WindowsMediaFoundationCompositionExportProvider
                        .TryGetEffectiveAudioProcessing(
                            track.Volume,
                            track.AudioEffectDefinitions,
                            effects,
                            out MediaAudioStereoLevels
                                audioLevels,
                            out MediaCompositionEffectDefinition[]
                                processorDefinitions) ||
                    !WindowsPcm16MixLevels.TryCreate(
                        audioLevels,
                        out WindowsPcm16MixLevels
                            mixLevels))
                {
                    plans = [];
                    compositionDurationTicks = 0;
                    return false;
                }

                long sourceSkip =
                    track.Delay.Ticks < 0
                        ? checked(-track.Delay.Ticks)
                        : 0;
                long sourceStart =
                    checked(
                        track.TrimTimeFromStart.Ticks +
                        sourceSkip);
                long sourceEnd =
                    checked(
                        track.OriginalDuration.Ticks -
                        track.TrimTimeFromEnd.Ticks);
                long destinationStart =
                    Math.Max(
                        0,
                        track.Delay.Ticks);
                if (sourceStart < 0 ||
                    track.TrimTimeFromStart < TimeSpan.Zero ||
                    track.TrimTimeFromEnd < TimeSpan.Zero ||
                    track.TrimTimeFromStart.Ticks >
                        track.OriginalDuration.Ticks ||
                    track.TrimTimeFromEnd.Ticks >
                        track.OriginalDuration.Ticks -
                        track.TrimTimeFromStart.Ticks)
                {
                    plans = [];
                    compositionDurationTicks = 0;
                    return false;
                }
                if (sourceStart >= sourceEnd ||
                    destinationStart >= timelineTicks)
                {
                    continue;
                }
                long duration =
                    sourceEnd - sourceStart;
                long destinationEnd =
                    Math.Min(
                        timelineTicks,
                        checked(
                            destinationStart +
                            duration));
                if (includeAudio &&
                    sourceStart < sourceEnd &&
                    destinationStart <
                        destinationEnd &&
                    mixLevels is not
                    { Left: 0, Right: 0 })
                {
                    captured.Add(
                        new WindowsMediaFoundationAudioPlan(
                            track.SourceUri,
                            sourceStart,
                            checked(
                                sourceStart +
                                destinationEnd -
                                destinationStart),
                            destinationStart,
                            destinationEnd,
                            mixLevels,
                            processorDefinitions));
                }
            }

            for (int layerIndex = 0;
                 layerIndex <
                    request.OverlayLayers.Count;
                 layerIndex++)
            {
                MediaCompositionExportOverlayLayer layer =
                    request.OverlayLayers[layerIndex];
                for (int overlayIndex = 0;
                     overlayIndex <
                        layer.Overlays.Count;
                     overlayIndex++)
                {
                    MediaCompositionExportOverlay overlay =
                        layer.Overlays[overlayIndex];
                    MediaCompositionExportClip clip =
                        overlay.Clip;
                    if (!WindowsMediaFoundationCompositionExportProvider
                            .TryGetEffectiveAudioProcessing(
                                clip.Volume,
                                clip.AudioEffectDefinitions,
                                effects,
                                out MediaAudioStereoLevels
                                    audioLevels,
                                out MediaCompositionEffectDefinition[]
                                    processorDefinitions) ||
                        !WindowsPcm16MixLevels.TryCreate(
                            audioLevels,
                            out WindowsPcm16MixLevels
                                mixLevels))
                    {
                        plans = [];
                        compositionDurationTicks = 0;
                        return false;
                    }
                    if (!overlay.AudioEnabled ||
                        !includeAudio ||
                        clip.SourceUri is not
                        { IsAbsoluteUri: true } source ||
                        mixLevels is
                        { Left: 0, Right: 0 })
                    {
                        continue;
                    }
                    long duration =
                        GetTrimmedDuration(
                            clip.OriginalDuration.Ticks,
                            clip.TrimTimeFromStart.Ticks,
                            clip.TrimTimeFromEnd.Ticks);
                    long destinationStart =
                        overlay.Delay.Ticks;
                    if (destinationStart < 0)
                    {
                        plans = [];
                        compositionDurationTicks = 0;
                        return false;
                    }
                    if (destinationStart >= timelineTicks)
                    {
                        continue;
                    }
                    long destinationEnd =
                        Math.Min(
                            timelineTicks,
                            checked(
                                destinationStart +
                                duration));
                    if (destinationStart >= destinationEnd)
                    {
                        continue;
                    }
                    long sourceStart =
                        clip.TrimTimeFromStart.Ticks;
                    captured.Add(
                        new WindowsMediaFoundationAudioPlan(
                            source,
                            sourceStart,
                            checked(
                                sourceStart +
                                destinationEnd -
                                destinationStart),
                            destinationStart,
                            destinationEnd,
                            mixLevels,
                            processorDefinitions));
                }
            }
        }
        catch (OverflowException)
        {
            plans = [];
            compositionDurationTicks = 0;
            return false;
        }
        plans = captured.ToArray();
        return compositionDurationTicks > 0;
    }

    private static long GetTrimmedDuration(
        long originalTicks,
        long trimStartTicks,
        long trimEndTicks)
    {
        if (originalTicks <= 0 ||
            trimStartTicks < 0 ||
            trimEndTicks < 0)
        {
            throw new OverflowException();
        }
        long duration =
            checked(
                originalTicks -
                trimStartTicks -
                trimEndTicks);
        if (duration <= 0)
        {
            throw new OverflowException();
        }
        return duration;
    }
}

/// <summary>
/// Bounded Media Foundation PCM16 timeline mixer.
/// </summary>
/// <remarks>
/// The output clock advances in fixed blocks of at most 1,024 frames.
/// Source Readers decode and resample to the requested PCM type. Only sources
/// overlapping the current block are live; each retains at most one decoded
/// sample. A block uses at most 16 KiB of stack accumulator storage for
/// stereo plus one 8 KiB float workspace when a typed effect is active, then
/// writes one MF-owned 32-byte-aligned PCM buffer. Managed
/// working storage is O(A) for A scheduled sources and does not grow with
/// duration. Mixing is O(F * (L + E)) for F output frames, L active layers,
/// and E active typed effect stages.
/// </remarks>
internal sealed class WindowsMediaFoundationAudioMixer :
    IDisposable
{
    private readonly AudioSource[] _sources;
    private readonly int[] _activeSources;
    private readonly uint _channelCount;
    private readonly uint _sampleRate;
    private readonly long _compositionDurationTicks;
    private readonly bool _hasProcessors;
    private int _nextSource;
    private int _activeCount;
    private bool _disposed;

    internal WindowsMediaFoundationAudioMixer(
        IReadOnlyList<WindowsMediaFoundationAudioPlan> plans,
        MediaEffectRegistry effects,
        nint dxgiManager,
        MediaCompositionEncodingProfile profile,
        long compositionDurationTicks)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.AudioChannelCount is not (1u or 2u) ||
            profile.AudioSampleRate == 0 ||
            compositionDurationTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile));
        }
        _channelCount = profile.AudioChannelCount;
        _sampleRate = profile.AudioSampleRate;
        _compositionDurationTicks =
            compositionDurationTicks;
        _sources = new AudioSource[plans.Count];
        _activeSources = new int[plans.Count];
        int created = 0;
        bool hasProcessors = false;
        try
        {
            for (int index = 0;
                 index < plans.Count;
                 index++)
            {
                AudioSource source =
                    new(
                        plans[index],
                        effects,
                        dxgiManager,
                        _sampleRate,
                        _channelCount);
                _sources[index] = source;
                created++;
                hasProcessors |=
                    source.HasProcessor;
            }
        }
        catch
        {
            for (int index = created - 1;
                 index >= 0;
                 index--)
            {
                _sources[index].Dispose();
            }
            throw;
        }
        _hasProcessors = hasProcessors;
        Array.Sort(
            _sources,
            AudioSourceComparer.Instance);
    }

    internal void Render(
        nint sinkWriter,
        uint audioStream,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
        long totalFrames =
            TicksToFramesCeiling(
                _compositionDurationTicks,
                _sampleRate);
        int channels = checked((int)_channelCount);
        Span<long> accumulatorStorage =
            stackalloc long[
                WindowsPcm16Mixer.FramesPerBlock *
                2];
        Span<float> effectStorage =
            _hasProcessors
                ? stackalloc float[
                    WindowsPcm16Mixer.FramesPerBlock *
                    2]
                : Span<float>.Empty;
        for (long blockStart = 0;
             blockStart < totalFrames;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int frameCount =
                checked(
                    (int)Math.Min(
                        WindowsPcm16Mixer.FramesPerBlock,
                        totalFrames - blockStart));
            long blockEnd =
                checked(blockStart + frameCount);
            Span<long> accumulator =
                accumulatorStorage[
                    ..checked(frameCount * channels)];
            accumulator.Clear();

            while (_nextSource < _sources.Length &&
                   _sources[_nextSource]
                       .DestinationStartFrame <
                       blockEnd)
            {
                _activeSources[_activeCount++] =
                    _nextSource++;
            }
            for (int activeIndex = 0;
                 activeIndex < _activeCount;)
            {
                AudioSource source =
                    _sources[
                        _activeSources[activeIndex]];
                if (source.DestinationEndFrame <=
                    blockStart)
                {
                    source.Dispose();
                    _activeCount--;
                    _activeSources[activeIndex] =
                        _activeSources[_activeCount];
                    continue;
                }
                source.Mix(
                    blockStart,
                    frameCount,
                    accumulator,
                    effectStorage,
                    cancellationToken);
                activeIndex++;
            }

            nint sample =
                WindowsMediaNative.CreatePcm16Sample(
                    accumulator);
            try
            {
                long timestamp =
                    FramesToTicksFloor(
                        blockStart,
                        _sampleRate);
                long sampleEnd =
                    blockEnd == totalFrames
                        ? _compositionDurationTicks
                        : FramesToTicksFloor(
                            blockEnd,
                            _sampleRate);
                WindowsMediaNative.SetSampleTime(
                    sample,
                    timestamp);
                WindowsMediaNative.SetSampleDuration(
                    sample,
                    checked(sampleEnd - timestamp));
                WindowsMediaNative.WriteSinkSample(
                    sinkWriter,
                    audioStream,
                    sample);
            }
            finally
            {
                WindowsMediaNative.Release(sample);
            }
            blockStart = blockEnd;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        for (int index = 0;
             index < _sources.Length;
             index++)
        {
            _sources[index].Dispose();
        }
        _nextSource = _sources.Length;
        _activeCount = 0;
    }

    internal static long TicksToFramesFloor(
        long ticks,
        uint sampleRate)
    {
        if (sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate));
        }
        Int128 numerator =
            (Int128)ticks * sampleRate;
        Int128 quotient =
            numerator /
            TimeSpan.TicksPerSecond;
        if (numerator < 0 &&
            numerator %
                TimeSpan.TicksPerSecond != 0)
        {
            quotient--;
        }
        return checked((long)quotient);
    }

    internal static long TicksToFramesCeiling(
        long ticks,
        uint sampleRate)
    {
        if (sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate));
        }
        Int128 numerator =
            (Int128)ticks * sampleRate;
        Int128 quotient =
            numerator /
            TimeSpan.TicksPerSecond;
        if (numerator > 0 &&
            numerator %
                TimeSpan.TicksPerSecond != 0)
        {
            quotient++;
        }
        return checked((long)quotient);
    }

    internal static long FramesToTicksFloor(
        long frames,
        uint sampleRate)
    {
        if (frames < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frames));
        }
        if (sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate));
        }
        UInt128 ticks =
            (UInt128)(ulong)frames *
            TimeSpan.TicksPerSecond /
            sampleRate;
        return checked((long)ticks);
    }

    private sealed class AudioSource :
        IDisposable
    {
        private readonly Uri _sourceUri;
        private readonly nint _dxgiManager;
        private readonly uint _sampleRate;
        private readonly uint _channelCount;
        private readonly long _sourceStartTicks;
        private readonly long _sourceStartFrame;
        private readonly WindowsPcm16MixLevels _levels;
        private readonly MediaAudioFormat _format;
        private readonly MediaAudioEffectProcessorChain?
            _processorChain;
        private nint _reader;
        private nint _mediaType;
        private nint _sample;
        private long _sampleDestinationStartFrame;
        private int _sampleFrameCount;
        private bool _endOfStream;
        private bool _disposed;

        internal AudioSource(
            in WindowsMediaFoundationAudioPlan plan,
            MediaEffectRegistry effects,
            nint dxgiManager,
            uint sampleRate,
            uint channelCount)
        {
            _sourceUri = plan.SourceUri;
            _dxgiManager = dxgiManager;
            _sampleRate = sampleRate;
            _channelCount = channelCount;
            _format =
                new MediaAudioFormat(
                    checked((int)sampleRate),
                    checked((int)channelCount));
            _sourceStartTicks = plan.SourceStartTicks;
            _sourceStartFrame =
                TicksToFramesCeiling(
                    plan.SourceStartTicks,
                    sampleRate);
            DestinationStartFrame =
                TicksToFramesCeiling(
                    plan.DestinationStartTicks,
                    sampleRate);
            long sourceEndFrame =
                TicksToFramesFloor(
                    plan.SourceEndTicks,
                    sampleRate);
            long requestedEndFrame =
                TicksToFramesFloor(
                    plan.DestinationEndTicks,
                    sampleRate);
            DestinationEndFrame =
                Math.Min(
                    requestedEndFrame,
                    checked(
                        DestinationStartFrame +
                        Math.Max(
                            0,
                            sourceEndFrame -
                            _sourceStartFrame)));
            _levels = plan.Levels;
            MediaAudioEffectProcessorChain?
                processorChain = null;
            if (plan.ProcessorDefinitions.Length != 0 &&
                !MediaAudioEffectProcessorChain
                    .TryCreate(
                        effects,
                        plan.ProcessorDefinitions,
                        out processorChain))
            {
                throw new NotSupportedException(
                    "A registered Windows composition audio effect could not be activated.");
            }
            _processorChain = processorChain;
        }

        internal long DestinationStartFrame
        {
            get;
        }

        internal long DestinationEndFrame
        {
            get;
        }

        internal bool HasProcessor =>
            _processorChain is not null;

        internal void Mix(
            long blockStartFrame,
            int blockFrameCount,
            Span<long> accumulator,
            Span<float> effectWorkspace,
            CancellationToken cancellationToken)
        {
            if (_disposed ||
                DestinationEndFrame <=
                    blockStartFrame)
            {
                return;
            }
            long blockEndFrame =
                checked(
                    blockStartFrame +
                    blockFrameCount);
            while (!_endOfStream)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
                if (_sample == 0 &&
                    !ReadNextSample(
                        cancellationToken))
                {
                    return;
                }
                long sampleEndFrame =
                    checked(
                        _sampleDestinationStartFrame +
                        _sampleFrameCount);
                if (sampleEndFrame <=
                    blockStartFrame)
                {
                    ReleaseSample();
                    continue;
                }
                if (_sampleDestinationStartFrame >=
                        blockEndFrame ||
                    _sampleDestinationStartFrame >=
                        DestinationEndFrame)
                {
                    return;
                }

                long overlapStart =
                    Math.Max(
                        blockStartFrame,
                        Math.Max(
                            DestinationStartFrame,
                            _sampleDestinationStartFrame));
                long overlapEnd =
                    Math.Min(
                        blockEndFrame,
                        Math.Min(
                            DestinationEndFrame,
                            sampleEndFrame));
                if (overlapStart < overlapEnd)
                {
                    int sourceFrameOffset =
                        checked(
                            (int)(
                                overlapStart -
                                _sampleDestinationStartFrame));
                    int overlapFrameCount =
                        checked(
                            (int)(
                                overlapEnd -
                                overlapStart));
                    int destinationFrameOffset =
                        checked(
                            (int)(
                                overlapStart -
                                blockStartFrame));
                    if (_processorChain is null)
                    {
                        WindowsMediaNative.MixPcm16Sample(
                            _sample,
                            sourceFrameOffset,
                            overlapFrameCount,
                            _channelCount,
                            _levels,
                            accumulator,
                            destinationFrameOffset);
                    }
                    else
                    {
                        Span<float> processed =
                            effectWorkspace[
                                ..checked(
                                    overlapFrameCount *
                                    (int)_channelCount)];
                        WindowsMediaNative
                            .CopyPcm16SampleToFloat(
                                _sample,
                                sourceFrameOffset,
                                overlapFrameCount,
                                _channelCount,
                                processed);
                        var context =
                            new MediaAudioProcessContext(
                                _format,
                                overlapFrameCount,
                                TimeSpan.FromTicks(
                                    FramesToTicksFloor(
                                        overlapStart,
                                        _sampleRate)));
                        _processorChain.Process(
                            processed,
                            context);
                        WindowsPcm16Mixer.AddProcessed(
                            processed,
                            _channelCount,
                            _levels,
                            accumulator,
                            destinationFrameOffset);
                    }
                }
                if (sampleEndFrame <= blockEndFrame)
                {
                    ReleaseSample();
                    continue;
                }
                return;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _processorChain?.Dispose();
            ReleaseSample();
            WindowsMediaNative.Release(
                Interlocked.Exchange(
                    ref _mediaType,
                    0));
            WindowsMediaNative.Release(
                Interlocked.Exchange(
                    ref _reader,
                    0));
        }

        private bool ReadNextSample(
            CancellationToken cancellationToken)
        {
            EnsureReader();
            while (!_endOfStream)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
                nint sample =
                    WindowsMediaNative.ReadSourceSample(
                        _reader,
                        WindowsMediaNative.FirstAudioStream,
                        out uint flags,
                        out long timestamp);
                if ((flags &
                     WindowsMediaNative
                         .SourceReaderEndOfStream) != 0)
                {
                    WindowsMediaNative.Release(sample);
                    _endOfStream = true;
                    return false;
                }
                if (sample == 0)
                {
                    continue;
                }
                int frameCount =
                    WindowsMediaNative
                        .GetPcm16SampleFrameCount(
                            sample,
                            _channelCount);
                long absoluteStartFrame =
                    TicksToFramesFloor(
                        timestamp,
                        _sampleRate);
                long destinationStartFrame =
                    checked(
                        DestinationStartFrame +
                        absoluteStartFrame -
                        _sourceStartFrame);
                long destinationEndFrame =
                    checked(
                        destinationStartFrame +
                        frameCount);
                if (destinationEndFrame <=
                        DestinationStartFrame ||
                    destinationStartFrame >=
                        DestinationEndFrame)
                {
                    WindowsMediaNative.Release(sample);
                    if (destinationStartFrame >=
                        DestinationEndFrame)
                    {
                        _endOfStream = true;
                        return false;
                    }
                    continue;
                }
                _sample = sample;
                _sampleDestinationStartFrame =
                    destinationStartFrame;
                _sampleFrameCount = frameCount;
                return true;
            }
            return false;
        }

        private void EnsureReader()
        {
            if (_reader != 0)
            {
                return;
            }
            try
            {
                _reader =
                    WindowsMediaNative
                        .CreateTranscodeSourceReader(
                            WindowsMediaFoundationCompositionExportProvider
                                .ToSourceUrl(
                                    _sourceUri),
                            _dxgiManager);
                _mediaType =
                    WindowsMediaNative.CreatePcmAudioType(
                        _channelCount,
                        _sampleRate);
                WindowsMediaNative.ConfigureSourceReaderStream(
                    _reader,
                    WindowsMediaNative.FirstAudioStream,
                    _mediaType);
                WindowsMediaNative.SetSourceReaderPosition(
                    _reader,
                    _sourceStartTicks);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void ReleaseSample()
        {
            WindowsMediaNative.Release(
                Interlocked.Exchange(
                    ref _sample,
                    0));
            _sampleDestinationStartFrame = 0;
            _sampleFrameCount = 0;
        }
    }

    private sealed class AudioSourceComparer :
        IComparer<AudioSource>
    {
        internal static readonly AudioSourceComparer
            Instance = new();

        public int Compare(
            AudioSource? left,
            AudioSource? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left is null)
            {
                return -1;
            }
            if (right is null)
            {
                return 1;
            }
            return left.DestinationStartFrame
                .CompareTo(
                    right.DestinationStartFrame);
        }
    }
}
