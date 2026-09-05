using Android.Media;
using Android.Runtime;
using Java.Nio;
using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

namespace ProGPU.Android.Media;

public sealed partial class
    AndroidMediaCodecCompositionExportProvider
{
    /// <summary>
    /// Bounded synchronous MediaCodec timeline mixer.
    /// </summary>
    /// <remarks>
    /// Output advances in blocks of at most 1,024 PCM frames. Only scheduled
    /// sources overlapping the current block are active, and each source owns
    /// at most one dequeued native decoder output buffer. A stereo block uses
    /// at most 16 KiB of stack accumulator storage plus one 8 KiB float
    /// workspace when a typed effect is active. Mixing is O(F * (L + E)) for F
    /// output frames, L active layers, and E active effect stages; managed
    /// storage is O(P) for P
    /// scheduled sources and does not grow with composition duration.
    /// </remarks>
    private sealed class AndroidMediaCodecAudioTimelineMixer :
        IDisposable
    {
        private readonly AudioSource[] _sources;
        private readonly int[] _activeSources;
        private readonly MediaCompositionEncodingProfile
            _profile;
        private readonly long _compositionFrameCount;
        private readonly bool _hasProcessors;
        private int _nextSource;
        private int _activeCount;
        private bool _disposed;

        internal AndroidMediaCodecAudioTimelineMixer(
            IReadOnlyList<AndroidMediaCodecAudioPlan> plans,
            MediaEffectRegistry effects,
            MediaCompositionEncodingProfile profile,
            long compositionFrameCount)
        {
            ArgumentNullException.ThrowIfNull(plans);
            ArgumentNullException.ThrowIfNull(effects);
            ArgumentNullException.ThrowIfNull(profile);
            if (profile.AudioChannelCount is not (1u or 2u) ||
                profile.AudioSampleRate == 0 ||
                compositionFrameCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(profile));
            }

            _profile = profile;
            _compositionFrameCount =
                compositionFrameCount;
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
                            profile);
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
        }

        internal void Encode(
            MediaCodec encoder,
            MediaMuxer muxer,
            MediaCodec.BufferInfo encoderInfo,
            ref int muxerTrack,
            ref bool muxerStarted,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(
                _disposed,
                this);

            int channels =
                checked(
                    (int)_profile.AudioChannelCount);
            int bytesPerFrame =
                checked(channels * sizeof(short));
            Span<long> accumulatorStorage =
                stackalloc long[
                    AndroidPcm16Mixer.FramesPerBlock *
                    2];
            Span<float> effectStorage =
                _hasProcessors
                    ? stackalloc float[
                        AndroidPcm16Mixer.FramesPerBlock *
                        2]
                    : Span<float>.Empty;
            long blockStart = 0;
            while (blockStart <
                   _compositionFrameCount)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
                int inputIndex =
                    encoder.DequeueInputBuffer(
                        CodecTimeoutMicroseconds);
                if (inputIndex < 0)
                {
                    DrainAudioEncoder(
                        encoder,
                        muxer,
                        encoderInfo,
                        ref muxerTrack,
                        ref muxerStarted,
                        waitForEndOfStream: false,
                        cancellationToken);
                    continue;
                }

                ByteBuffer input =
                    encoder.GetInputBuffer(
                        inputIndex) ??
                    throw new InvalidOperationException(
                        "Android AAC encoder returned no PCM input buffer.");
                input.Clear();
                int capacityFrames =
                    input.Remaining() /
                    bytesPerFrame;
                if (capacityFrames <= 0)
                {
                    throw new InvalidDataException(
                        "An Android AAC encoder input buffer cannot hold one PCM frame.");
                }

                int frameCount =
                    checked(
                        (int)Math.Min(
                            Math.Min(
                                AndroidPcm16Mixer
                                    .FramesPerBlock,
                                capacityFrames),
                            _compositionFrameCount -
                                blockStart));
                long blockEnd =
                    checked(
                        blockStart +
                        frameCount);
                Span<long> accumulator =
                    accumulatorStorage[
                        ..checked(
                            frameCount *
                            channels)];
                accumulator.Clear();

                ActivateSources(blockEnd);
                MixActiveSources(
                    blockStart,
                    frameCount,
                    accumulator,
                    effectStorage,
                    cancellationToken);

                int byteLength =
                    checked(
                        frameCount *
                        bytesPerFrame);
                Span<short> output =
                    GetWritableDirectPcm16Span(
                        input,
                        byteLength);
                AndroidPcm16Mixer.WriteSaturated(
                    accumulator,
                    output);

                encoder.QueueInputBuffer(
                    inputIndex,
                    0,
                    byteLength,
                    MediaPcmTimelineMath
                        .GetFrameTimestampMicroseconds(
                            blockStart,
                            _profile.AudioSampleRate),
                    MediaCodecBufferFlags.None);
                blockStart = blockEnd;
                DrainAudioEncoder(
                    encoder,
                    muxer,
                    encoderInfo,
                    ref muxerTrack,
                    ref muxerStarted,
                    waitForEndOfStream: false,
                    cancellationToken);
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

        private void ActivateSources(
            long blockEnd)
        {
            while (_nextSource <
                       _sources.Length &&
                   _sources[_nextSource]
                       .DestinationStartFrame <
                       blockEnd)
            {
                _activeSources[_activeCount++] =
                    _nextSource++;
            }
        }

        private void MixActiveSources(
            long blockStart,
            int frameCount,
            Span<long> accumulator,
            Span<float> effectWorkspace,
            CancellationToken cancellationToken)
        {
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
                    effectWorkspace,
                    cancellationToken);
                activeIndex++;
            }
        }
    }

    private sealed class AudioSource :
        IDisposable
    {
        private readonly AndroidMediaCodecAudioPlan _plan;
        private readonly MediaCompositionEncodingProfile
            _profile;
        private readonly long _sourceStartFrame;
        private readonly MediaAudioFormat _format;
        private readonly MediaAudioEffectProcessorChain?
            _processorChain;
        private MediaExtractor? _extractor;
        private MediaCodec? _decoder;
        private MediaCodec.BufferInfo? _decoderInfo;
        private int _outputIndex = -1;
        private long _outputDestinationStartFrame;
        private int _outputFrameCount;
        private bool _decoderStarted;
        private bool _inputComplete;
        private bool _outputComplete;
        private bool _formatValidated;
        private bool _disposed;

        internal AudioSource(
            in AndroidMediaCodecAudioPlan plan,
            MediaEffectRegistry effects,
            MediaCompositionEncodingProfile profile)
        {
            _plan = plan;
            _profile = profile;
            _format =
                new MediaAudioFormat(
                    checked(
                        (int)profile.AudioSampleRate),
                    checked(
                        (int)profile.AudioChannelCount));
            _sourceStartFrame =
                AndroidMediaCodecAudioPlanner
                    .MicrosecondsToFramesCeiling(
                        plan.SourceStartMicroseconds,
                        profile.AudioSampleRate);
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
                    "A registered Android composition audio effect could not be activated.");
            }
            if (processorChain is not null &&
                processorChain.GetTiming(
                    in _format) !=
                MediaAudioProcessorTiming.Zero)
            {
                processorChain.Dispose();
                throw new NotSupportedException(
                    "Android composition export does not yet compensate custom audio effect latency or drain effect tails.");
            }
            _processorChain = processorChain;
        }

        internal long DestinationStartFrame =>
            _plan.DestinationStartFrame;

        internal long DestinationEndFrame =>
            _plan.DestinationEndFrame;

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
                _plan.DestinationEndFrame <=
                    blockStartFrame)
            {
                return;
            }

            long blockEndFrame =
                checked(
                    blockStartFrame +
                    blockFrameCount);
            while (_outputIndex >= 0 ||
                   !_outputComplete)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
                if (_outputIndex < 0 &&
                    !ReadNextOutput(
                        cancellationToken))
                {
                    return;
                }

                long outputEndFrame =
                    checked(
                        _outputDestinationStartFrame +
                        _outputFrameCount);
                if (outputEndFrame <=
                    blockStartFrame)
                {
                    ReleaseOutput();
                    continue;
                }
                if (_outputDestinationStartFrame >=
                        blockEndFrame ||
                    _outputDestinationStartFrame >=
                        _plan.DestinationEndFrame)
                {
                    return;
                }

                long overlapStart =
                    Math.Max(
                        blockStartFrame,
                        Math.Max(
                            _plan.DestinationStartFrame,
                            _outputDestinationStartFrame));
                long overlapEnd =
                    Math.Min(
                        blockEndFrame,
                        Math.Min(
                            _plan.DestinationEndFrame,
                            outputEndFrame));
                if (overlapStart < overlapEnd)
                {
                    MixOutputRange(
                        checked(
                            (int)(
                                overlapStart -
                                _outputDestinationStartFrame)),
                        checked(
                            (int)(
                                overlapEnd -
                                overlapStart)),
                        accumulator,
                        checked(
                            (int)(
                                overlapStart -
                                blockStartFrame)),
                        effectWorkspace,
                        overlapStart);
                }

                if (outputEndFrame <= blockEndFrame)
                {
                    ReleaseOutput();
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
            ReleaseOutput();
            if (_decoderStarted)
            {
                TryStop(_decoder);
            }
            _decoder?.Release();
            _decoder?.Dispose();
            _decoder = null;
            _decoderInfo?.Dispose();
            _decoderInfo = null;
            _extractor?.Release();
            _extractor?.Dispose();
            _extractor = null;
        }

        private bool ReadNextOutput(
            CancellationToken cancellationToken)
        {
            EnsureDecoder();
            MediaCodec decoder = _decoder!;
            MediaExtractor extractor = _extractor!;
            MediaCodec.BufferInfo info = _decoderInfo!;
            int channels =
                checked(
                    (int)_profile.AudioChannelCount);
            int bytesPerFrame =
                checked(channels * sizeof(short));

            while (!_outputComplete)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
                if (!_inputComplete)
                {
                    int inputIndex =
                        decoder.DequeueInputBuffer(
                            CodecTimeoutMicroseconds);
                    if (inputIndex >= 0)
                    {
                        _inputComplete =
                            QueueDecoderInput(
                                extractor,
                                decoder,
                                inputIndex,
                                _plan
                                    .SourceEndMicroseconds);
                    }
                }

                int outputIndex =
                    decoder.DequeueOutputBuffer(
                        info,
                        CodecTimeoutMicroseconds);
                if (outputIndex ==
                    (int)MediaCodecInfoState.TryAgainLater)
                {
                    continue;
                }
                if (outputIndex ==
                    (int)MediaCodecInfoState
                        .OutputFormatChanged)
                {
                    using MediaFormat outputFormat =
                        decoder.OutputFormat;
                    ValidatePcmOutputFormat(
                        outputFormat,
                        _profile);
                    _formatValidated = true;
                    continue;
                }
                if (outputIndex < 0)
                {
                    continue;
                }

                bool endOfStream =
                    (info.Flags &
                     MediaCodecBufferFlags
                         .EndOfStream) != 0;
                if (info.Size <= 0 ||
                    (info.Flags &
                     MediaCodecBufferFlags
                         .CodecConfig) != 0)
                {
                    decoder.ReleaseOutputBuffer(
                        outputIndex,
                        false);
                    _outputComplete |= endOfStream;
                    continue;
                }
                if (!_formatValidated)
                {
                    using MediaFormat outputFormat =
                        decoder.GetOutputFormat(
                            outputIndex);
                    ValidatePcmOutputFormat(
                        outputFormat,
                        _profile);
                    _formatValidated = true;
                }
                if (info.Offset < 0 ||
                    info.Size < 0 ||
                    info.Size % bytesPerFrame != 0)
                {
                    decoder.ReleaseOutputBuffer(
                        outputIndex,
                        false);
                    throw new InvalidDataException(
                        "Android audio decoder returned a partial PCM16 frame.");
                }

                int decodedFrames =
                    info.Size /
                    bytesPerFrame;
                int firstFrame =
                    MediaPcmTimelineMath
                        .GetBoundaryFrameOffset(
                            _plan
                                .SourceStartMicroseconds -
                            info.PresentationTimeUs,
                            _profile.AudioSampleRate,
                            decodedFrames);
                int endFrame =
                    MediaPcmTimelineMath
                        .GetBoundaryFrameOffset(
                            _plan
                                .SourceEndMicroseconds -
                            info.PresentationTimeUs,
                            _profile.AudioSampleRate,
                            decodedFrames);
                if (endFrame <= firstFrame)
                {
                    decoder.ReleaseOutputBuffer(
                        outputIndex,
                        false);
                    _outputComplete |=
                        endOfStream ||
                        info.PresentationTimeUs >=
                        _plan.SourceEndMicroseconds;
                    continue;
                }

                long absoluteSourceFrame =
                    checked(
                        AndroidMediaCodecAudioPlanner
                            .MicrosecondsToFramesFloor(
                                Math.Max(
                                    0,
                                    info.PresentationTimeUs),
                                _profile.AudioSampleRate) +
                        firstFrame);
                long destinationStartFrame =
                    checked(
                        _plan.DestinationStartFrame +
                        absoluteSourceFrame -
                        _sourceStartFrame);
                int selectedFrames =
                    endFrame -
                    firstFrame;
                long destinationEndFrame =
                    checked(
                        destinationStartFrame +
                        selectedFrames);
                if (destinationEndFrame <=
                        _plan.DestinationStartFrame ||
                    destinationStartFrame >=
                        _plan.DestinationEndFrame)
                {
                    decoder.ReleaseOutputBuffer(
                        outputIndex,
                        false);
                    _outputComplete |=
                        endOfStream ||
                        destinationStartFrame >=
                        _plan.DestinationEndFrame;
                    continue;
                }

                _outputIndex = outputIndex;
                _outputDestinationStartFrame =
                    destinationStartFrame;
                _outputFrameCount =
                    selectedFrames;
                info.Offset =
                    checked(
                        info.Offset +
                        firstFrame *
                        bytesPerFrame);
                info.Size =
                    checked(
                        selectedFrames *
                        bytesPerFrame);
                _outputComplete |= endOfStream;
                return true;
            }
            return false;
        }

        private void EnsureDecoder()
        {
            if (_decoder is not null)
            {
                return;
            }

            var extractor = new MediaExtractor();
            MediaCodec? decoder = null;
            bool decoderStarted = false;
            try
            {
                extractor.SetDataSource(
                    ToSource(
                        _plan.SourceUri));
                int audioTrack =
                    FindTrack(
                        extractor,
                        "audio/");
                if (audioTrack < 0)
                {
                    throw new InvalidDataException(
                        "An AAC output was requested but a composition audio source has no audio track.");
                }

                using MediaFormat sourceFormat =
                    extractor.GetTrackFormat(
                        audioTrack);
                string? sourceMime =
                    sourceFormat.GetString(
                        MediaFormat.KeyMime);
                if (string.IsNullOrWhiteSpace(
                        sourceMime) ||
                    sourceFormat.GetInteger(
                        MediaFormat.KeySampleRate,
                        0) !=
                        _profile.AudioSampleRate ||
                    sourceFormat.GetInteger(
                        MediaFormat.KeyChannelCount,
                        0) !=
                        _profile.AudioChannelCount)
                {
                    throw new InvalidDataException(
                        "Android mixed-audio export requires every source sample rate and channel count to match the requested AAC profile.");
                }
                sourceFormat.SetInteger(
                    MediaFormat.KeyPcmEncoding,
                    (int)Encoding.Pcm16bit);

                decoder =
                    MediaCodec.CreateDecoderByType(
                        sourceMime);
                decoder.Configure(
                    sourceFormat,
                    null,
                    null,
                    MediaCodecConfigFlags.None);
                decoder.Start();
                decoderStarted = true;
                extractor.SelectTrack(
                    audioTrack);
                extractor.SeekTo(
                    _plan.SourceStartMicroseconds,
                    MediaExtractorSeekTo
                        .PreviousSync);

                _extractor = extractor;
                _decoder = decoder;
                _decoderInfo =
                    new MediaCodec.BufferInfo();
                _decoderStarted = true;
            }
            catch
            {
                if (decoderStarted)
                {
                    TryStop(decoder);
                }
                decoder?.Release();
                decoder?.Dispose();
                extractor.Release();
                extractor.Dispose();
                throw;
            }
        }

        private void MixOutputRange(
            int sourceFrameOffset,
            int frameCount,
            Span<long> accumulator,
            int destinationFrameOffset,
            Span<float> effectWorkspace,
            long presentationFirstFrame)
        {
            MediaCodec decoder =
                _decoder ??
                throw new ObjectDisposedException(
                    nameof(AudioSource));
            ByteBuffer output =
                decoder.GetOutputBuffer(
                    _outputIndex) ??
                throw new InvalidOperationException(
                    "Android audio decoder returned no retained PCM buffer.");
            int channels =
                checked(
                    (int)_profile.AudioChannelCount);
            int sampleOffset =
                checked(
                    sourceFrameOffset *
                    channels);
            int sampleCount =
                checked(
                    frameCount *
                    channels);
            ReadOnlySpan<short> samples =
                GetReadOnlyDirectPcm16Span(
                    output,
                    _decoderInfo!.Offset,
                    _decoderInfo.Size)
                    .Slice(
                        sampleOffset,
                        sampleCount);
            if (_processorChain is null)
            {
                AndroidPcm16Mixer.Add(
                    samples,
                    _profile.AudioChannelCount,
                    _plan.Levels,
                    accumulator,
                    destinationFrameOffset);
                return;
            }

            Span<float> processed =
                effectWorkspace[
                    ..sampleCount];
            MediaPcm16FloatConverter
                .ConvertToNormalizedFloat(
                    samples,
                    processed);
            long presentationMicroseconds =
                MediaPcmTimelineMath
                    .GetFrameTimestampMicroseconds(
                        presentationFirstFrame,
                        _profile.AudioSampleRate);
            var context =
                new MediaAudioProcessContext(
                    _format,
                    frameCount,
                    TimeSpan.FromTicks(
                        checked(
                            presentationMicroseconds *
                            10)));
            _processorChain.Process(
                processed,
                context);
            AndroidPcm16Mixer.AddProcessed(
                processed,
                _profile.AudioChannelCount,
                _plan.Levels,
                accumulator,
                destinationFrameOffset);
        }

        private void ReleaseOutput()
        {
            if (_outputIndex >= 0)
            {
                _decoder?.ReleaseOutputBuffer(
                    _outputIndex,
                    false);
            }
            _outputIndex = -1;
            _outputDestinationStartFrame = 0;
            _outputFrameCount = 0;
        }
    }

    private static unsafe ReadOnlySpan<short>
        GetReadOnlyDirectPcm16Span(
        ByteBuffer buffer,
        int byteOffset,
        int byteLength)
    {
        if (byteOffset < 0 ||
            byteLength < 0 ||
            (byteOffset & 1) != 0 ||
            (byteLength & 1) != 0)
        {
            throw new InvalidDataException(
                "Android decoded PCM16 range is invalid.");
        }

        nint address =
            JNIEnv.GetDirectBufferAddress(
                buffer.Handle);
        long capacity =
            JNIEnv.GetDirectBufferCapacity(
                buffer.Handle);
        if (address == 0 ||
            capacity <
                (long)byteOffset +
                byteLength)
        {
            throw new InvalidDataException(
                "Android audio decoder did not expose a readable direct PCM buffer.");
        }

        return new ReadOnlySpan<short>(
            (void*)(address + byteOffset),
            byteLength / sizeof(short));
    }
}
