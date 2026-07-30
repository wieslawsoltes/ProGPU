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
        if (request.BackgroundAudioTracks.Count != 0)
        {
            return true;
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
        return TryGetEffectiveAudioLevels(
            clip.Volume,
            clip.AudioEffectDefinitions,
            effects,
            out levels);
    }

    private static bool TryGetEffectiveAudioLevels(
        double volume,
        IReadOnlyList<MediaCompositionEffectDefinition>
            definitions,
        MediaEffectRegistry effects,
        out MediaAudioStereoLevels levels)
    {
        if (!MediaAudioGraphEffectResolver
                .TryCaptureCombinedStereoLevels(
                    effects,
                    definitions,
                    out MediaAudioStereoLevels
                        effectLevels))
        {
            levels = default;
            return false;
        }

        try
        {
            levels = effectLevels.Scale(
                checked((float)volume));
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
    /// Decodes and mixes the selected main/background timeline through direct
    /// PCM16 codec buffers, then writes one native AAC-only staging asset.
    /// Work is O(A + F * L) for A compressed access units, F output frames,
    /// and L active layers. Managed source state is O(P) for P scheduled
    /// sources; the PCM accumulator is fixed at 1,024 frames. Codec buffers
    /// and the encoded staging file remain platform-owned.
    /// </summary>
    private static void BakeAudioTimeline(
        MediaCompositionExportRequest request,
        MediaEffectRegistry effects,
        string outputPath,
        CancellationToken cancellationToken)
    {
        MediaCompositionEncodingProfile profile =
            request.EncodingProfile;
        if (!AndroidMediaCodecAudioPlanner.TryCapture(
                request,
                effects,
                out AndroidMediaCodecAudioPlan[] plans,
                out long compositionFrameCount))
        {
            throw new InvalidDataException(
                "The Android composition audio timeline is invalid.");
        }
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
            using var timelineMixer =
                new AndroidMediaCodecAudioTimelineMixer(
                    plans,
                    profile,
                    compositionFrameCount);
            timelineMixer.Encode(
                encoder,
                muxer,
                encoderInfo,
                ref muxerTrack,
                ref muxerStarted,
                cancellationToken);

            QueueAudioEncoderEndOfStream(
                encoder,
                profile,
                compositionFrameCount,
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
