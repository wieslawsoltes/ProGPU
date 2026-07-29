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
    private static bool RequiresAudioTranscode(
        MediaCompositionExportRequest request)
    {
        if (request.EncodingProfile.AudioSubtype is null)
        {
            return false;
        }

        for (int index = 0;
             index < request.Clips.Count;
             index++)
        {
            MediaCompositionExportClip clip =
                request.Clips[index];
            if (clip.ArgbColor.HasValue ||
                clip.Volume != 1d ||
                clip.AudioEffectDefinitions.Count != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryGetEffectiveAudioLevels(
        MediaCompositionExportClip clip,
        MediaEffectRegistry effects,
        out MediaAudioStereoLevels levels)
    {
        if (!MediaAudioGraphEffectResolver
                .TryCaptureCombinedStereoLevels(
                    effects,
                    clip.AudioEffectDefinitions,
                    out MediaAudioStereoLevels
                        effectLevels))
        {
            levels = default;
            return false;
        }

        try
        {
            levels = effectLevels.Scale(
                checked((float)clip.Volume));
            return levels.Peak <=
                MediaPcm16StereoProcessor
                    .MaximumLevel;
        }
        catch (Exception exception)
            when (exception is
                OverflowException or
                ArgumentOutOfRangeException)
        {
            levels = default;
            return false;
        }
    }

    /// <summary>
    /// Decodes the selected audio timeline into direct PCM16 codec buffers,
    /// applies the portable gain/balance graph in the writable AAC-encoder
    /// input buffer, and writes one native AAC-only staging asset. Work is
    /// O(A + S) for A compressed access units and S PCM samples. Managed
    /// working storage is O(1); codec buffers and the encoded staging file
    /// remain platform-owned.
    /// </summary>
    private static void BakeAudioTimeline(
        MediaCompositionExportRequest request,
        MediaEffectRegistry effects,
        string outputPath,
        CancellationToken cancellationToken)
    {
        MediaCompositionEncodingProfile profile =
            request.EncodingProfile;
        using MediaFormat encoderFormat =
            CreateAudioEncoderFormat(profile);
        MediaCodec? encoder = null;
        MediaMuxer? muxer = null;
        bool encoderStarted = false;
        bool muxerStarted = false;
        try
        {
            encoder =
                MediaCodec.CreateEncoderByType(
                    AudioMime);
            encoder.Configure(
                encoderFormat,
                null,
                null,
                MediaCodecConfigFlags.Encode);
            encoder.Start();
            encoderStarted = true;
            muxer = new MediaMuxer(
                outputPath,
                MuxerOutputType.Mpeg4);
            using var encoderInfo =
                new MediaCodec.BufferInfo();
            int muxerTrack = -1;
            long pcmFrameCursor = 0;
            long timelineTicks = 0;

            for (int index = 0;
                 index < request.Clips.Count;
                 index++)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
                MediaCompositionExportClip clip =
                    request.Clips[index];
                timelineTicks =
                    checked(
                        timelineTicks +
                        (clip.OriginalDuration -
                         clip.TrimTimeFromStart -
                         clip.TrimTimeFromEnd)
                            .Ticks);
                long targetEndFrame =
                    MediaPcmTimelineMath
                        .GetDurationFrameCountCeiling(
                            TimeSpan.FromTicks(
                                timelineTicks),
                            profile.AudioSampleRate);
                if (!TryGetEffectiveAudioLevels(
                        clip,
                        effects,
                        out MediaAudioStereoLevels
                            levels))
                {
                    throw new InvalidDataException(
                        "The Android audio timeline contains an unsupported effect graph.");
                }

                if (clip.ArgbColor.HasValue)
                {
                    QueueSilenceToFrame(
                        targetEndFrame,
                        profile,
                        encoder,
                        muxer,
                        encoderInfo,
                        ref muxerTrack,
                        ref muxerStarted,
                        ref pcmFrameCursor,
                        cancellationToken);
                }
                else
                {
                    DecodeClipIntoAudioEncoder(
                        clip,
                        profile,
                        levels,
                        targetEndFrame,
                        encoder,
                        muxer,
                        encoderInfo,
                        ref muxerTrack,
                        ref muxerStarted,
                        ref pcmFrameCursor,
                        cancellationToken);
                }
            }

            QueueAudioEncoderEndOfStream(
                encoder,
                profile,
                pcmFrameCursor,
                muxer,
                encoderInfo,
                ref muxerTrack,
                ref muxerStarted,
                cancellationToken);
            DrainAudioEncoder(
                encoder,
                muxer,
                encoderInfo,
                ref muxerTrack,
                ref muxerStarted,
                waitForEndOfStream: true,
                cancellationToken);
            if (!muxerStarted || muxerTrack < 0)
            {
                throw new InvalidDataException(
                    "The Android AAC encoder did not expose an output track.");
            }

            muxer.Stop();
            muxerStarted = false;
        }
        finally
        {
            if (encoderStarted)
            {
                TryStop(encoder);
            }
            encoder?.Release();
            encoder?.Dispose();
            if (muxerStarted)
            {
                TryStop(muxer);
            }
            muxer?.Release();
            muxer?.Dispose();
        }
    }

    /// <summary>
    /// Fills native AAC-encoder input buffers until an exact cumulative
    /// timeline frame. Work is O(S) for S zeroed samples with O(1) managed
    /// storage.
    /// </summary>
    private static void QueueSilenceToFrame(
        long targetEndFrame,
        MediaCompositionEncodingProfile profile,
        MediaCodec encoder,
        MediaMuxer muxer,
        MediaCodec.BufferInfo encoderInfo,
        ref int muxerTrack,
        ref bool muxerStarted,
        ref long pcmFrameCursor,
        CancellationToken cancellationToken)
    {
        if (targetEndFrame < pcmFrameCursor)
        {
            throw new InvalidDataException(
                "Android audio timeline moved behind its encoded PCM cursor.");
        }
        long remainingFrames =
            targetEndFrame -
            pcmFrameCursor;
        int bytesPerFrame =
            checked(
                (int)profile.AudioChannelCount *
                sizeof(short));
        while (remainingFrames > 0)
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
                        remainingFrames,
                        capacityFrames));
            int byteLength =
                checked(
                    frameCount *
                    bytesPerFrame);
            GetWritableDirectPcm16Span(
                    input,
                    byteLength)
                .Clear();

            encoder.QueueInputBuffer(
                inputIndex,
                0,
                byteLength,
                MediaPcmTimelineMath
                    .GetFrameTimestampMicroseconds(
                        pcmFrameCursor,
                        profile.AudioSampleRate),
                MediaCodecBufferFlags.None);
            pcmFrameCursor =
                checked(
                    pcmFrameCursor +
                    frameCount);
            remainingFrames -=
                frameCount;
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

    private static void DecodeClipIntoAudioEncoder(
        MediaCompositionExportClip clip,
        MediaCompositionEncodingProfile profile,
        in MediaAudioStereoLevels levels,
        long targetEndFrame,
        MediaCodec encoder,
        MediaMuxer muxer,
        MediaCodec.BufferInfo encoderInfo,
        ref int muxerTrack,
        ref bool muxerStarted,
        ref long pcmFrameCursor,
        CancellationToken cancellationToken)
    {
        using var extractor = new MediaExtractor();
        try
        {
            extractor.SetDataSource(
                ToSource(clip.SourceUri!));
            int audioTrack =
                FindTrack(extractor, "audio/");
            if (audioTrack < 0)
            {
                throw new InvalidDataException(
                    "An AAC output was requested but a clip has no audio track.");
            }

            using MediaFormat sourceFormat =
                extractor.GetTrackFormat(audioTrack);
            string? sourceMime =
                sourceFormat.GetString(
                    MediaFormat.KeyMime);
            if (string.IsNullOrWhiteSpace(sourceMime) ||
                sourceFormat.GetInteger(
                    MediaFormat.KeySampleRate,
                    0) != profile.AudioSampleRate ||
                sourceFormat.GetInteger(
                    MediaFormat.KeyChannelCount,
                    0) != profile.AudioChannelCount)
            {
                throw new InvalidDataException(
                    "Android audio-effect export requires source sample rate and channel count to match the requested AAC profile.");
            }
            sourceFormat.SetInteger(
                MediaFormat.KeyPcmEncoding,
                (int)Encoding.Pcm16bit);

            using MediaCodec decoder =
                MediaCodec.CreateDecoderByType(
                    sourceMime);
            bool decoderStarted = false;
            try
            {
                decoder.Configure(
                    sourceFormat,
                    null,
                    null,
                    MediaCodecConfigFlags.None);
                decoder.Start();
                decoderStarted = true;
                extractor.SelectTrack(audioTrack);
                long sourceStart =
                    ToMicroseconds(
                        clip.TrimTimeFromStart);
                long sourceEnd =
                    checked(
                        sourceStart +
                        ToMicroseconds(
                            clip.OriginalDuration -
                            clip.TrimTimeFromStart -
                            clip.TrimTimeFromEnd));
                extractor.SeekTo(
                    sourceStart,
                    MediaExtractorSeekTo.PreviousSync);

                using var decoderInfo =
                    new MediaCodec.BufferInfo();
                bool inputComplete = false;
                bool outputComplete = false;
                bool outputFormatValidated = false;
                while (!outputComplete)
                {
                    cancellationToken
                        .ThrowIfCancellationRequested();
                    if (!inputComplete)
                    {
                        int inputIndex =
                            decoder.DequeueInputBuffer(
                                CodecTimeoutMicroseconds);
                        if (inputIndex >= 0)
                        {
                            inputComplete =
                                QueueDecoderInput(
                                    extractor,
                                    decoder,
                                    inputIndex,
                                    sourceEnd);
                        }
                    }

                    int outputIndex =
                        decoder.DequeueOutputBuffer(
                            decoderInfo,
                            CodecTimeoutMicroseconds);
                    if (outputIndex ==
                        (int)MediaCodecInfoState
                            .TryAgainLater)
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
                    if (outputIndex ==
                        (int)MediaCodecInfoState
                            .OutputFormatChanged)
                    {
                        using MediaFormat outputFormat =
                            decoder.OutputFormat;
                        ValidatePcmOutputFormat(
                            outputFormat,
                            profile);
                        outputFormatValidated = true;
                        continue;
                    }
                    if (outputIndex < 0)
                    {
                        continue;
                    }

                    try
                    {
                        if (decoderInfo.Size > 0)
                        {
                            ByteBuffer output =
                                decoder.GetOutputBuffer(
                                    outputIndex) ??
                                throw new InvalidOperationException(
                                    "Android audio decoder returned no PCM buffer.");
                            if (!outputFormatValidated)
                            {
                                using MediaFormat outputFormat =
                                    decoder.GetOutputFormat(
                                        outputIndex);
                                ValidatePcmOutputFormat(
                                    outputFormat,
                                    profile);
                                outputFormatValidated = true;
                            }
                            FeedDecodedPcmRange(
                                output,
                                decoderInfo,
                                sourceStart,
                                sourceEnd,
                                profile,
                                levels,
                                targetEndFrame,
                                encoder,
                                muxer,
                                encoderInfo,
                                ref muxerTrack,
                                ref muxerStarted,
                                ref pcmFrameCursor,
                                cancellationToken);
                        }
                        outputComplete =
                            (decoderInfo.Flags &
                             MediaCodecBufferFlags
                                 .EndOfStream) != 0;
                    }
                    finally
                    {
                        decoder.ReleaseOutputBuffer(
                            outputIndex,
                            false);
                    }
                }
                QueueSilenceToFrame(
                    targetEndFrame,
                    profile,
                    encoder,
                    muxer,
                    encoderInfo,
                    ref muxerTrack,
                    ref muxerStarted,
                    ref pcmFrameCursor,
                    cancellationToken);
            }
            finally
            {
                if (decoderStarted)
                {
                    TryStop(decoder);
                }
                decoder.Release();
            }
        }
        finally
        {
            extractor.Release();
        }
    }

    private static bool QueueDecoderInput(
        MediaExtractor extractor,
        MediaCodec decoder,
        int inputIndex,
        long sourceEnd)
    {
        ByteBuffer input =
            decoder.GetInputBuffer(inputIndex) ??
            throw new InvalidOperationException(
                "Android audio decoder returned no input buffer.");
        input.Clear();
        long timestamp =
            extractor.SampleTime;
        if (timestamp < 0 ||
            timestamp >= sourceEnd)
        {
            decoder.QueueInputBuffer(
                inputIndex,
                0,
                0,
                Math.Max(0, sourceEnd),
                MediaCodecBufferFlags
                    .EndOfStream);
            return true;
        }

        int size =
            extractor.ReadSampleData(
                input,
                0);
        if (size < 0)
        {
            decoder.QueueInputBuffer(
                inputIndex,
                0,
                0,
                Math.Max(0, timestamp),
                MediaCodecBufferFlags
                    .EndOfStream);
            return true;
        }
        decoder.QueueInputBuffer(
            inputIndex,
            0,
            size,
            timestamp,
            ToCodecFlags(
                extractor.SampleFlags));
        extractor.Advance();
        return false;
    }

    private static void ValidatePcmOutputFormat(
        MediaFormat outputFormat,
        MediaCompositionEncodingProfile profile)
    {
        if (outputFormat.GetInteger(
                MediaFormat.KeySampleRate,
                0) != profile.AudioSampleRate ||
            outputFormat.GetInteger(
                MediaFormat.KeyChannelCount,
                0) != profile.AudioChannelCount ||
            outputFormat.GetInteger(
                MediaFormat.KeyPcmEncoding,
                (int)Encoding.Pcm16bit) !=
                (int)Encoding.Pcm16bit)
        {
            throw new InvalidDataException(
                "Android audio-effect export requires interleaved PCM16 at the requested sample rate and channel count.");
        }
    }

    private static void FeedDecodedPcmRange(
        ByteBuffer decoded,
        MediaCodec.BufferInfo info,
        long sourceStart,
        long sourceEnd,
        MediaCompositionEncodingProfile profile,
        in MediaAudioStereoLevels levels,
        long targetEndFrame,
        MediaCodec encoder,
        MediaMuxer muxer,
        MediaCodec.BufferInfo encoderInfo,
        ref int muxerTrack,
        ref bool muxerStarted,
        ref long pcmFrameCursor,
        CancellationToken cancellationToken)
    {
        if (info.Size <= 0 ||
            (info.Flags &
             MediaCodecBufferFlags.CodecConfig) != 0)
        {
            return;
        }

        int channelCount =
            checked((int)profile.AudioChannelCount);
        int bytesPerFrame =
            checked(channelCount * sizeof(short));
        if (info.Offset < 0 ||
            info.Size < 0 ||
            info.Size % bytesPerFrame != 0)
        {
            throw new InvalidDataException(
                "Android audio decoder returned a partial PCM16 frame.");
        }

        int frameCount =
            info.Size / bytesPerFrame;
        int firstFrame =
            MediaPcmTimelineMath
                .GetBoundaryFrameOffset(
                sourceStart - info.PresentationTimeUs,
                profile.AudioSampleRate,
                frameCount);
        int endFrame =
            MediaPcmTimelineMath
                .GetBoundaryFrameOffset(
                sourceEnd - info.PresentationTimeUs,
                profile.AudioSampleRate,
                frameCount);
        if (endFrame <= firstFrame)
        {
            return;
        }

        int start =
            checked(
                info.Offset +
                firstFrame * bytesPerFrame);
        int end =
            checked(
                info.Offset +
                endFrame * bytesPerFrame);
        decoded.Position(start);
        decoded.Limit(end);
        FeedPcmToAudioEncoder(
            decoded,
            profile,
            levels,
            targetEndFrame,
            encoder,
            muxer,
            encoderInfo,
            ref muxerTrack,
            ref muxerStarted,
            ref pcmFrameCursor,
            cancellationToken);
    }

    private static void FeedPcmToAudioEncoder(
        ByteBuffer source,
        MediaCompositionEncodingProfile profile,
        in MediaAudioStereoLevels levels,
        long targetEndFrame,
        MediaCodec encoder,
        MediaMuxer muxer,
        MediaCodec.BufferInfo encoderInfo,
        ref int muxerTrack,
        ref bool muxerStarted,
        ref long pcmFrameCursor,
        CancellationToken cancellationToken)
    {
        int bytesPerFrame =
            checked(
                (int)profile.AudioChannelCount *
                sizeof(short));
        while (source.HasRemaining &&
               pcmFrameCursor < targetEndFrame)
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
            int chunk =
                Math.Min(
                    source.Remaining(),
                    input.Remaining());
            long remainingTimelineBytes =
                checked(
                    (targetEndFrame -
                     pcmFrameCursor) *
                    bytesPerFrame);
            chunk =
                checked(
                    (int)Math.Min(
                        chunk,
                        remainingTimelineBytes));
            chunk -= chunk % bytesPerFrame;
            if (chunk <= 0)
            {
                throw new InvalidDataException(
                    "An Android AAC encoder input buffer cannot hold one PCM frame.");
            }

            int sourceLimit = source.Limit();
            source.Limit(
                checked(
                    source.Position() +
                    chunk));
            input.Put(source);
            source.Limit(sourceLimit);
            ApplyPcm16Levels(
                input,
                chunk,
                profile.AudioChannelCount,
                levels);

            long timestamp =
                MediaPcmTimelineMath
                    .GetFrameTimestampMicroseconds(
                    pcmFrameCursor,
                    profile.AudioSampleRate);
            encoder.QueueInputBuffer(
                inputIndex,
                0,
                chunk,
                timestamp,
                MediaCodecBufferFlags.None);
            pcmFrameCursor =
                checked(
                    pcmFrameCursor +
                    chunk / bytesPerFrame);
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

    private static unsafe void ApplyPcm16Levels(
        ByteBuffer buffer,
        int byteLength,
        uint channelCount,
        in MediaAudioStereoLevels levels)
    {
        if (channelCount is not (1u or 2u) ||
            byteLength < 0 ||
            byteLength %
                checked(
                    (int)channelCount *
                    sizeof(short)) != 0)
        {
            throw new InvalidDataException(
                "Android PCM16 channel layout is invalid.");
        }
        Span<short> samples =
            GetWritableDirectPcm16Span(
                buffer,
                byteLength);
        int channelOffset = 0;
        MediaPcm16StereoProcessor.ApplyStereo(
            samples,
            channelCount,
            levels,
            ref channelOffset);
        if (channelOffset != 0)
        {
            throw new InvalidDataException(
                "Android PCM16 input ended on a partial channel frame.");
        }
    }

    private static unsafe Span<short>
        GetWritableDirectPcm16Span(
        ByteBuffer buffer,
        int byteLength)
    {
        if (byteLength < 0 ||
            (byteLength & 1) != 0)
        {
            throw new InvalidDataException(
                "Android PCM16 byte length is invalid.");
        }
        nint address =
            JNIEnv.GetDirectBufferAddress(
                buffer.Handle);
        long capacity =
            JNIEnv.GetDirectBufferCapacity(
                buffer.Handle);
        if (address == 0 ||
            capacity < byteLength)
        {
            throw new InvalidDataException(
                "Android AAC encoder did not expose a writable direct PCM buffer.");
        }

        return new Span<short>(
            (void*)address,
            byteLength / sizeof(short));
    }

    private static void QueueAudioEncoderEndOfStream(
        MediaCodec encoder,
        MediaCompositionEncodingProfile profile,
        long pcmFrameCursor,
        MediaMuxer muxer,
        MediaCodec.BufferInfo encoderInfo,
        ref int muxerTrack,
        ref bool muxerStarted,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            int inputIndex =
                encoder.DequeueInputBuffer(
                    CodecTimeoutMicroseconds);
            if (inputIndex >= 0)
            {
                encoder.QueueInputBuffer(
                    inputIndex,
                    0,
                    0,
                    MediaPcmTimelineMath
                        .GetFrameTimestampMicroseconds(
                        pcmFrameCursor,
                        profile.AudioSampleRate),
                    MediaCodecBufferFlags
                        .EndOfStream);
                return;
            }
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

    private static void DrainAudioEncoder(
        MediaCodec encoder,
        MediaMuxer muxer,
        MediaCodec.BufferInfo info,
        ref int muxerTrack,
        ref bool muxerStarted,
        bool waitForEndOfStream,
        CancellationToken cancellationToken)
    {
        bool complete = false;
        while (!complete)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            int outputIndex =
                encoder.DequeueOutputBuffer(
                    info,
                    waitForEndOfStream
                        ? CodecTimeoutMicroseconds
                        : 0);
            if (outputIndex ==
                (int)MediaCodecInfoState.TryAgainLater)
            {
                if (!waitForEndOfStream)
                {
                    return;
                }
                continue;
            }
            if (outputIndex ==
                (int)MediaCodecInfoState
                    .OutputFormatChanged)
            {
                if (muxerTrack >= 0)
                {
                    throw new InvalidDataException(
                        "Android AAC encoder changed format twice.");
                }
                using MediaFormat outputFormat =
                    encoder.OutputFormat;
                muxerTrack =
                    muxer.AddTrack(outputFormat);
                muxer.Start();
                muxerStarted = true;
                continue;
            }
            if (outputIndex < 0)
            {
                continue;
            }

            try
            {
                bool codecConfiguration =
                    (info.Flags &
                     MediaCodecBufferFlags
                         .CodecConfig) != 0;
                if (!codecConfiguration &&
                    info.Size > 0)
                {
                    if (!muxerStarted ||
                        muxerTrack < 0)
                    {
                        throw new InvalidOperationException(
                            "Android AAC encoder emitted a sample before its output format.");
                    }
                    ByteBuffer output =
                        encoder.GetOutputBuffer(
                            outputIndex) ??
                        throw new InvalidOperationException(
                            "Android AAC encoder returned no output buffer.");
                    output.Position(info.Offset);
                    output.Limit(
                        info.Offset +
                        info.Size);
                    muxer.WriteSampleData(
                        muxerTrack,
                        output,
                        info);
                }
                complete =
                    (info.Flags &
                     MediaCodecBufferFlags
                         .EndOfStream) != 0;
            }
            finally
            {
                encoder.ReleaseOutputBuffer(
                    outputIndex,
                    false);
            }
        }
    }

    private static MediaFormat
        CreateAudioEncoderFormat(
        MediaCompositionEncodingProfile profile)
    {
        MediaFormat format =
            MediaFormat.CreateAudioFormat(
                AudioMime,
                checked(
                    (int)profile.AudioSampleRate),
                checked(
                    (int)profile.AudioChannelCount));
        format.SetInteger(
            MediaFormat.KeyBitRate,
            checked(
                (int)profile.AudioBitrate));
        format.SetInteger(
            MediaFormat.KeyAacProfile,
            (int)MediaCodecProfileType
                .Aacobjectlc);
        format.SetInteger(
            MediaFormat.KeyPcmEncoding,
            (int)Encoding.Pcm16bit);
        return format;
    }

    private static MediaFormat? InspectBakedAudio(
        string sourcePath,
        MediaCompositionEncodingProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken
            .ThrowIfCancellationRequested();
        using var extractor = new MediaExtractor();
        try
        {
            extractor.SetDataSource(sourcePath);
            int track =
                FindTrack(extractor, "audio/");
            if (track < 0)
            {
                return null;
            }
            MediaFormat format =
                extractor.GetTrackFormat(track);
            if (!string.Equals(
                    format.GetString(
                        MediaFormat.KeyMime),
                    AudioMime,
                    StringComparison.OrdinalIgnoreCase) ||
                format.GetInteger(
                    MediaFormat.KeySampleRate,
                    0) != profile.AudioSampleRate ||
                format.GetInteger(
                    MediaFormat.KeyChannelCount,
                    0) != profile.AudioChannelCount)
            {
                format.Dispose();
                return null;
            }
            return format;
        }
        finally
        {
            extractor.Release();
        }
    }

    private static void CopyBakedAudio(
        string sourcePath,
        MediaMuxer muxer,
        int audioTrack,
        long totalDuration,
        AndroidExportProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        ByteBuffer buffer =
            ByteBuffer.AllocateDirect(
                64 * 1024);
        using var info =
            new MediaCodec.BufferInfo();
        using var extractor =
            new MediaExtractor();
        try
        {
            extractor.SetDataSource(sourcePath);
            int track =
                FindTrack(
                    extractor,
                    "audio/");
            if (track < 0)
            {
                throw new InvalidDataException(
                    "The baked Android AAC asset has no audio track.");
            }
            extractor.SelectTrack(track);
            while (extractor.SampleTime >= 0 &&
                   extractor.SampleTime <
                       totalDuration)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
                long sampleSize =
                    extractor.SampleSize;
                if (sampleSize <= 0 ||
                    sampleSize >
                        MaximumCompressedAudioSample)
                {
                    throw new InvalidDataException(
                        "Android baked AAC sample size is invalid.");
                }
                if (sampleSize >
                    buffer.Capacity())
                {
                    buffer.Dispose();
                    buffer =
                        ByteBuffer.AllocateDirect(
                            checked(
                                (int)sampleSize));
                }
                buffer.Clear();
                int size =
                    extractor.ReadSampleData(
                        buffer,
                        0);
                if (size < 0)
                {
                    break;
                }
                long timestamp =
                    extractor.SampleTime;
                info.Set(
                    0,
                    size,
                    timestamp,
                    ToCodecFlags(
                        extractor.SampleFlags));
                buffer.Position(0);
                buffer.Limit(size);
                muxer.WriteSampleData(
                    audioTrack,
                    buffer,
                    info);
                reporter.ReportTimeline(
                    timestamp,
                    totalDuration,
                    offset: 90d,
                    scale: 0.1d);
                extractor.Advance();
            }
        }
        finally
        {
            extractor.Release();
            buffer.Dispose();
        }
    }
}
