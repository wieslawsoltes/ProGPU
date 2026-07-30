using System.Globalization;
using System.Diagnostics;
using Android.Graphics;
using Android.Media;
using Android.Opengl;
using Android.OS;
using Android.Views;
using Java.Nio;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using IOPath = System.IO.Path;
using OperationCanceledException =
    System.OperationCanceledException;

namespace ProGPU.Android.Media;

internal interface IAndroidEncoderSurfaceRenderer :
    IDisposable
{
    Surface DecoderSurface { get; }

    void DrawFrame(
        long presentationTimeMicroseconds,
        MediaVideoEffectPlan effectPlan,
        CancellationToken cancellationToken);

    void DrawColorFrame(
        long presentationTimeMicroseconds,
        uint argbColor,
        MediaVideoEffectPlan effectPlan,
        CancellationToken cancellationToken);
}

/// <summary>
/// Android-native precise/effect export lane. MediaExtractor feeds a
/// surface-output MediaCodec decoder. When the host owns a Vulkan Dawn
/// context, decoded frames cross a three-slot AHardwareBuffer ring, fused
/// WebGPU effects, and explicit SyncFD handoffs before one terminal EGL blit
/// into the H.264 encoder Surface. Devices without that interop retain the
/// direct SurfaceTexture/EGL GPU lane. Compatible AAC access units are
/// remuxed without decode or managed copies. Video-only solid colors are
/// generated on the same GPU paths. For C clips, V decoded/generated frames,
/// A compressed audio samples, and P output pixels, export is
/// O(C + V + A + P + F * L) time and O(C + B) managed audio-source state
/// beyond bounded native rings, one compressed-sample buffer, and a fixed
/// block accumulator, for F PCM frames, L active audio layers, and B
/// background tracks. No decoded video pixel or managed PCM array enters
/// managed memory.
/// </summary>
public sealed partial class
    AndroidMediaCodecCompositionExportProvider :
        IMediaCompositionExportProvider,
        IMediaCompositionExportCapabilityProvider
{
    private const string ProviderId =
        "progpu.android.mediacodec.export";
    private const string VideoMime = "video/avc";
    private const string AudioMime = "audio/mp4a-latm";
    internal const int MaximumDimension = 8_192;
    private const int MaximumCompressedAudioSample =
        16 * 1024 * 1024;
    private const long CodecTimeoutMicroseconds = 10_000;
    private readonly MediaEffectRegistry _effects;

    public AndroidMediaCodecCompositionExportProvider(
        int priority = 100,
        MediaEffectRegistry? effects = null)
    {
        Priority = priority;
        _effects = effects ?? MediaEffectRegistry.Default;
    }

    public string Id => ProviderId;
    public int Priority { get; }

    public bool CanRender(
        MediaCompositionExportRequest request) =>
        IsRequestSupported(
            request,
            OperatingSystem.IsAndroid(),
            _effects) &&
        (!HasSpatialVideoEffects(
             request,
             _effects) ||
         HasActiveVulkanDawnContext());

    internal static bool IsRequestSupported(
        MediaCompositionExportRequest request,
        bool isAndroid,
        MediaEffectRegistry? effects = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        MediaCompositionEncodingProfile profile =
            request.EncodingProfile;
        if (!isAndroid ||
            string.IsNullOrWhiteSpace(request.DestinationPath) ||
            request.Clips.Count == 0 ||
            request.OverlayLayers.Count != 0 ||
            !string.Equals(
                profile.ContainerSubtype,
                "MPEG4",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                profile.VideoSubtype,
                "H264",
                StringComparison.OrdinalIgnoreCase) ||
            profile.AudioSubtype is not null &&
            !string.Equals(
                profile.AudioSubtype,
                "AAC",
                StringComparison.OrdinalIgnoreCase) ||
            profile.Width is 0 or > MaximumDimension ||
            profile.Height is 0 or > MaximumDimension ||
            profile.VideoBitrate is 0 or > int.MaxValue ||
            profile.FrameRateNumerator == 0 ||
            profile.FrameRateDenominator == 0 ||
            profile.FrameRateNumerator >
                240u * profile.FrameRateDenominator ||
            profile.AudioSubtype is not null &&
            (profile.AudioChannelCount is not (1 or 2) ||
             profile.AudioSampleRate is not
                 (44_100 or 48_000) ||
             profile.AudioBitrate is 0 or > int.MaxValue))
        {
            return false;
        }

        bool requiresTranscode =
            request.TrimmingMode ==
                MediaCompositionTrimmingMode.Precise ||
            request.BackgroundAudioTracks.Count != 0;
        if (profile.AudioSubtype is null &&
            request.BackgroundAudioTracks.Count != 0)
        {
            return false;
        }
        MediaEffectRegistry effectRegistry =
            effects ?? MediaEffectRegistry.Default;
        for (int index = 0;
             index < request.Clips.Count;
             index++)
        {
            MediaCompositionExportClip clip =
                request.Clips[index];
            bool hasSource =
                clip.SourceUri is
                { IsAbsoluteUri: true };
            bool hasColor =
                clip.ArgbColor.HasValue;
            if (hasSource == hasColor ||
                !double.IsFinite(clip.Volume) ||
                clip.Volume is < 0d or > 1d ||
                (profile.AudioSubtype is null &&
                 (clip.Volume != 1d ||
                  clip.AudioEffectDefinitions.Count != 0)) ||
                !TryGetEffectiveAudioLevels(
                    clip,
                    effectRegistry,
                    out _) ||
                clip.OriginalDuration <= TimeSpan.Zero ||
                clip.TrimTimeFromStart < TimeSpan.Zero ||
                clip.TrimTimeFromEnd < TimeSpan.Zero ||
                clip.TrimTimeFromStart +
                    clip.TrimTimeFromEnd >=
                    clip.OriginalDuration ||
                !TryGetVideoEffectPlan(
                    clip,
                    effectRegistry,
                    out MediaVideoEffectPlan plan))
            {
                return false;
            }

            requiresTranscode |=
                hasColor ||
                !plan.IsIdentity ||
                clip.Volume != 1d ||
                clip.AudioEffectDefinitions.Count != 0;
        }

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
                track.OriginalDuration <= TimeSpan.Zero ||
                track.TrimTimeFromStart < TimeSpan.Zero ||
                track.TrimTimeFromEnd < TimeSpan.Zero ||
                track.TrimTimeFromStart +
                    track.TrimTimeFromEnd >=
                    track.OriginalDuration ||
                !TryGetEffectiveAudioLevels(
                    track.Volume,
                    track.AudioEffectDefinitions,
                    effectRegistry,
                    out _))
            {
                return false;
            }
        }

        return requiresTranscode;
    }

    public MediaCompositionExportCapabilities GetCapabilities(
        MediaCompositionExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanRender(request))
        {
            throw new ArgumentException(
                "The request is not supported by this provider.",
                nameof(request));
        }

        bool effects = HasVideoEffects(request);
        bool webGpu = HasActiveVulkanDawnContext();
        bool transcodeAudio =
            RequiresAudioTranscode(request);
        return new MediaCompositionExportCapabilities(
            ProviderId,
            webGpu
                ? MediaCompositionExportVideoPath.GpuCopy
                : MediaCompositionExportVideoPath.NativeGpuSurface,
            request.EncodingProfile.AudioSubtype is null
                ? MediaCompositionExportAudioPath.None
                : transcodeAudio
                    ? MediaCompositionExportAudioPath
                        .NativeBuffer
                    : MediaCompositionExportAudioPath
                        .CompressedSampleCopy,
            HardwareVideoEncoderRequested: true,
            HardwareVideoEncoderGuaranteed: false,
            EffectsBakedOnGpu: effects,
            Limitation:
                "The provider selects a hardware-accelerated H.264 " +
                "MediaCodec at runtime. Registered affine color effects " +
                (webGpu
                    ? "use the active Vulkan Dawn device and a bounded " +
                      "AHardwareBuffer/SyncFD WebGPU lane when EGL interop " +
                      "validation succeeds. Registered clamped Gaussian " +
                      "effects use one retained intermediate and a two-axis " +
                      "WebGPU submission. Affine-only rendering can fall " +
                      "back to the direct native GPU surface lane. "
                    : "use a decoder-surface to encoder-surface native GPU pass. ") +
                "Identity AAC is remuxed only when every source exactly " +
                "matches the requested sample rate, channels, bitrate, and " +
                "codec configuration. Effect-bearing main and background " +
                "audio is decoded from synchronous MediaCodec buffers, mixed " +
                "in bounded 1,024-frame wide-accumulator blocks with " +
                "registered gain and balance, then encoded by the native AAC " +
                "codec without managed PCM arrays. Positive and negative " +
                "WinUI background delays and trim intervals are applied on " +
                "the exact PCM timeline. Solid-color clips generate native " +
                "PCM16 silence when audio is requested. Visual overlays, " +
                "unsupported or unregistered effects remain rejected.");
    }

    public ValueTask<MediaCompositionExportFailure>
        RenderAsync(
        MediaCompositionExportRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRender(request))
        {
            return ValueTask.FromResult(
                MediaCompositionExportFailure.InvalidProfile);
        }

        return new ValueTask<MediaCompositionExportFailure>(
            Task.Run(
                () => RenderCore(
                    request,
                    _effects,
                    progress,
                    cancellationToken),
                CancellationToken.None));
    }

    private static MediaCompositionExportFailure RenderCore(
        MediaCompositionExportRequest request,
        MediaEffectRegistry effects,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        string destination =
            IOPath.GetFullPath(request.DestinationPath);
        string? directory = IOPath.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory))
        {
            return MediaCompositionExportFailure.InvalidProfile;
        }

        Directory.CreateDirectory(directory);
        string temporary = IOPath.Combine(
            directory,
            $".{IOPath.GetFileNameWithoutExtension(destination)}." +
            $"{Guid.NewGuid():N}.android.tmp.mp4");
        string? bakedAudio = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reporter =
                new AndroidExportProgressReporter(progress);
            reporter.Report(0d);
            if (RequiresAudioTranscode(request))
            {
                bakedAudio = IOPath.Combine(
                    directory,
                    $".{IOPath.GetFileNameWithoutExtension(destination)}." +
                    $"{Guid.NewGuid():N}.android.audio.tmp.mp4");
                BakeAudioTimeline(
                    request,
                    effects,
                    bakedAudio,
                    cancellationToken);
            }
            MediaCompositionExportFailure result =
                RenderNative(
                    request,
                    effects,
                    temporary,
                    bakedAudio,
                    reporter,
                    cancellationToken);
            if (result != MediaCompositionExportFailure.None)
            {
                TryDelete(temporary);
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            reporter.Report(100d);
            return MediaCompositionExportFailure.None;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(temporary);
            throw;
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            TryDelete(temporary);
            return MediaCompositionExportFailure.InvalidProfile;
        }
        catch (InvalidDataException)
        {
            TryDelete(temporary);
            return MediaCompositionExportFailure.InvalidProfile;
        }
        catch (MediaCodec.CodecException)
        {
            TryDelete(temporary);
            return MediaCompositionExportFailure.CodecNotFound;
        }
        catch
        {
            TryDelete(temporary);
            return MediaCompositionExportFailure.Unknown;
        }
        finally
        {
            if (bakedAudio is not null)
            {
                TryDelete(bakedAudio);
            }
        }
    }

    private static MediaCompositionExportFailure RenderNative(
        MediaCompositionExportRequest request,
        MediaEffectRegistry effects,
        string temporary,
        string? bakedAudio,
        AndroidExportProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        MediaFormat? audioFormat =
            bakedAudio is null
                ? InspectSources(
                    request,
                    cancellationToken)
                : InspectBakedAudio(
                    bakedAudio,
                    request.EncodingProfile,
                    cancellationToken);
        if (request.EncodingProfile.AudioSubtype is not null &&
            audioFormat is null)
        {
            return MediaCompositionExportFailure.InvalidProfile;
        }

        MediaMuxer? muxer = null;
        MediaCodec? encoder = null;
        Surface? encoderSurface = null;
        IAndroidEncoderSurfaceRenderer? renderer = null;
        bool muxerStarted = false;
        bool encoderStarted = false;
        try
        {
            muxer = new MediaMuxer(
                temporary,
                MuxerOutputType.Mpeg4);
            int audioTrack = audioFormat is null
                ? -1
                : muxer.AddTrack(audioFormat);

            using MediaFormat encoderFormat =
                CreateVideoEncoderFormat(
                    request.EncodingProfile);
            string? encoderName =
                FindHardwareEncoder(encoderFormat);
            if (string.IsNullOrEmpty(encoderName))
            {
                return MediaCompositionExportFailure.CodecNotFound;
            }

            encoder = MediaCodec.CreateByCodecName(encoderName);
            encoder.Configure(
                encoderFormat,
                null,
                null,
                MediaCodecConfigFlags.Encode);
            encoderSurface = encoder.CreateInputSurface();
            renderer = CreateRenderer(
                encoderSurface,
                checked((int)request.EncodingProfile.Width),
                checked((int)request.EncodingProfile.Height),
                HasSpatialVideoEffects(
                    request,
                    effects));
            encoder.Start();
            encoderStarted = true;

            var encoderInfo = new MediaCodec.BufferInfo();
            int videoTrack = -1;
            long timelineOffset = 0;
            long totalDuration =
                GetTimelineDurationMicroseconds(request);
            for (int index = 0;
                 index < request.Clips.Count;
                 index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MediaCompositionExportClip clip =
                    request.Clips[index];
                if (!TryGetVideoEffectPlan(
                        clip,
                        effects,
                        out MediaVideoEffectPlan
                            effectPlan))
                {
                    throw new InvalidOperationException(
                        "The clip contains an unsupported video effect.");
                }
                long sourceStart =
                    ToMicroseconds(clip.TrimTimeFromStart);
                long clipDuration =
                    ToMicroseconds(
                        clip.OriginalDuration -
                        clip.TrimTimeFromStart -
                        clip.TrimTimeFromEnd);
                long sourceEnd =
                    checked(sourceStart + clipDuration);

                if (clip.ArgbColor is uint argbColor)
                {
                    RenderColorClip(
                        request.EncodingProfile,
                        argbColor,
                        encoder,
                        renderer,
                        muxer,
                        encoderInfo,
                        ref videoTrack,
                        audioTrack,
                        ref muxerStarted,
                        clipDuration,
                        timelineOffset,
                        effectPlan,
                        totalDuration,
                        reporter,
                        cancellationToken);
                }
                else
                {
                    RenderVideoClip(
                        clip.SourceUri!,
                        encoder,
                        renderer,
                        muxer,
                        encoderInfo,
                        ref videoTrack,
                        audioTrack,
                        ref muxerStarted,
                        sourceStart,
                        sourceEnd,
                        timelineOffset,
                        effectPlan,
                        totalDuration,
                        reporter,
                        cancellationToken);
                }
                timelineOffset =
                    checked(timelineOffset + clipDuration);
            }

            encoder.SignalEndOfInputStream();
            DrainEncoder(
                encoder,
                muxer,
                encoderInfo,
                ref videoTrack,
                audioTrack,
                ref muxerStarted,
                waitForEndOfStream: true,
                cancellationToken);
            if (!muxerStarted || videoTrack < 0)
            {
                return MediaCompositionExportFailure.CodecNotFound;
            }

            if (audioTrack >= 0)
            {
                if (bakedAudio is null)
                {
                    CopyAudioTimeline(
                        request,
                        muxer,
                        audioTrack,
                        totalDuration,
                        reporter,
                        cancellationToken);
                }
                else
                {
                    CopyBakedAudio(
                        bakedAudio,
                        muxer,
                        audioTrack,
                        totalDuration,
                        reporter,
                        cancellationToken);
                }
            }

            muxer.Stop();
            muxerStarted = false;
            return MediaCompositionExportFailure.None;
        }
        finally
        {
            if (encoderStarted)
            {
                TryStop(encoder);
            }
            renderer?.Dispose();
            encoderSurface?.Release();
            encoderSurface?.Dispose();
            encoder?.Release();
            encoder?.Dispose();
            if (muxerStarted)
            {
                TryStop(muxer);
            }
            muxer?.Release();
            muxer?.Dispose();
            audioFormat?.Dispose();
        }
    }

    private static void RenderColorClip(
        MediaCompositionEncodingProfile profile,
        uint argbColor,
        MediaCodec encoder,
        IAndroidEncoderSurfaceRenderer renderer,
        MediaMuxer muxer,
        MediaCodec.BufferInfo encoderInfo,
        ref int videoTrack,
        int audioTrack,
        ref bool muxerStarted,
        long clipDuration,
        long timelineOffset,
        MediaVideoEffectPlan effectPlan,
        long totalDuration,
        AndroidExportProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        long frameOffset = 0;
        ulong frameRemainder = 0;
        while (frameOffset < clipDuration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long outputTimestamp =
                checked(timelineOffset + frameOffset);
            renderer.DrawColorFrame(
                outputTimestamp,
                argbColor,
                effectPlan,
                cancellationToken);
            DrainEncoder(
                encoder,
                muxer,
                encoderInfo,
                ref videoTrack,
                audioTrack,
                ref muxerStarted,
                waitForEndOfStream: false,
                cancellationToken);
            reporter.ReportTimeline(
                outputTimestamp,
                totalDuration,
                scale: 0.9d);
            frameOffset =
                GetNextColorFrameTimestampMicroseconds(
                    frameOffset,
                    ref frameRemainder,
                    profile.FrameRateNumerator,
                    profile.FrameRateDenominator);
        }
    }

    /// <summary>
    /// Advances a microsecond rational frame clock without cumulative drift.
    /// </summary>
    /// <remarks>
    /// O(1) time and storage; supported Android profiles are capped at
    /// 240 fps, so every step advances by at least one microsecond.
    /// </remarks>
    internal static long
        GetNextColorFrameTimestampMicroseconds(
        long currentTimestamp,
        ref ulong remainder,
        uint frameRateNumerator,
        uint frameRateDenominator)
    {
        if (currentTimestamp < 0 ||
            frameRateNumerator == 0 ||
            frameRateDenominator == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameRateNumerator));
        }
        ulong stepNumerator =
            1_000_000ul *
            frameRateDenominator;
        ulong wholeMicroseconds =
            stepNumerator / frameRateNumerator;
        remainder =
            checked(
                remainder +
                stepNumerator %
                frameRateNumerator);
        ulong carry =
            remainder / frameRateNumerator;
        remainder %=
            frameRateNumerator;
        ulong delta =
            checked(
                wholeMicroseconds +
                carry);
        if (delta == 0 ||
            delta > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameRateNumerator));
        }
        return checked(
            currentTimestamp +
            (long)delta);
    }

    private static void RenderVideoClip(
        Uri source,
        MediaCodec encoder,
        IAndroidEncoderSurfaceRenderer renderer,
        MediaMuxer muxer,
        MediaCodec.BufferInfo encoderInfo,
        ref int videoTrack,
        int audioTrack,
        ref bool muxerStarted,
        long sourceStart,
        long sourceEnd,
        long timelineOffset,
        MediaVideoEffectPlan effectPlan,
        long totalDuration,
        AndroidExportProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        using var extractor = new MediaExtractor();
        extractor.SetDataSource(ToSource(source));
        int track = FindTrack(extractor, "video/");
        if (track < 0)
        {
            throw new InvalidDataException(
                "The Android source has no video track.");
        }

        MediaFormat format = extractor.GetTrackFormat(track);
        string mime =
            format.GetString("mime") ??
            throw new InvalidDataException(
                "The Android video track has no MIME type.");
        extractor.SelectTrack(track);
        extractor.SeekTo(
            sourceStart,
            MediaExtractorSeekTo.PreviousSync);

        using MediaCodec decoder =
            MediaCodec.CreateDecoderByType(mime);
        decoder.Configure(
            format,
            renderer.DecoderSurface,
            null,
            MediaCodecConfigFlags.None);
        decoder.Start();
        var decoderInfo = new MediaCodec.BufferInfo();
        bool inputEnded = false;
        bool outputEnded = false;
        try
        {
            while (!outputEnded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!inputEnded)
                {
                    int inputIndex =
                        decoder.DequeueInputBuffer(
                            CodecTimeoutMicroseconds);
                    if (inputIndex >= 0)
                    {
                        ByteBuffer input =
                            decoder.GetInputBuffer(inputIndex) ??
                            throw new InvalidOperationException(
                                "Android decoder returned no input buffer.");
                        long sampleTime = extractor.SampleTime;
                        if (sampleTime < 0 ||
                            sampleTime >= sourceEnd)
                        {
                            decoder.QueueInputBuffer(
                                inputIndex,
                                0,
                                0,
                                0,
                                MediaCodecBufferFlags.EndOfStream);
                            inputEnded = true;
                        }
                        else
                        {
                            int size =
                                extractor.ReadSampleData(input, 0);
                            if (size < 0)
                            {
                                decoder.QueueInputBuffer(
                                    inputIndex,
                                    0,
                                    0,
                                    0,
                                    MediaCodecBufferFlags
                                        .EndOfStream);
                                inputEnded = true;
                            }
                            else
                            {
                                decoder.QueueInputBuffer(
                                    inputIndex,
                                    0,
                                    size,
                                    sampleTime,
                                    ToCodecFlags(
                                        extractor.SampleFlags));
                                extractor.Advance();
                            }
                        }
                    }
                }

                int outputIndex =
                    decoder.DequeueOutputBuffer(
                        decoderInfo,
                        CodecTimeoutMicroseconds);
                if (outputIndex >= 0)
                {
                    long timestamp =
                        decoderInfo.PresentationTimeUs;
                    bool render =
                        decoderInfo.Size != 0 &&
                        timestamp >= sourceStart &&
                        timestamp < sourceEnd;
                    decoder.ReleaseOutputBuffer(
                        outputIndex,
                        render);
                    if (render)
                    {
                        long outputTimestamp =
                            checked(
                                timelineOffset +
                                timestamp -
                                sourceStart);
                        renderer.DrawFrame(
                            outputTimestamp,
                            effectPlan,
                            cancellationToken);
                        DrainEncoder(
                            encoder,
                            muxer,
                            encoderInfo,
                            ref videoTrack,
                            audioTrack,
                            ref muxerStarted,
                            waitForEndOfStream: false,
                            cancellationToken);
                        reporter.ReportTimeline(
                            outputTimestamp,
                            totalDuration,
                            scale: 0.9d);
                    }

                    outputEnded =
                        (decoderInfo.Flags &
                         MediaCodecBufferFlags.EndOfStream) != 0;
                }
            }
        }
        finally
        {
            TryStop(decoder);
            decoder.Release();
            extractor.Release();
            format.Dispose();
        }
    }

    internal static IAndroidEncoderSurfaceRenderer CreateRenderer(
        Surface encoderSurface,
        int width,
        int height,
        bool requiresSpatialEffects = false)
    {
        IReadOnlyList<WgpuContext> contexts =
            WgpuContext.ActiveContexts;
        for (int index = 0; index < contexts.Count; index++)
        {
            if (contexts[index].ExternalTextureImporter is
                    DawnGpuContext dawn &&
                contexts[index].AdapterBackendType ==
                    Silk.NET.WebGPU.BackendType.Vulkan)
            {
                try
                {
                    return new AndroidMediaCodecGpuEncoderFrameSink(
                        dawn,
                        encoderSurface,
                        checked((uint)width),
                        checked((uint)height));
                }
                catch (NotSupportedException)
                {
                    // Preserve the dependency-free direct EGL surface lane
                    // when this device lacks the required AHB/fence features.
                }
                catch (EntryPointNotFoundException)
                {
                    // Vendor EGL does not expose the required extension ABI.
                }
            }
        }

        if (requiresSpatialEffects)
        {
            throw new NotSupportedException(
                "Android Gaussian media effects require the active Vulkan Dawn AHardwareBuffer lane.");
        }

        return new AndroidEncoderSurfaceRenderer(
            encoderSurface,
            width,
            height);
    }

    internal static bool HasActiveVulkanDawnContext()
    {
        IReadOnlyList<WgpuContext> contexts =
            WgpuContext.ActiveContexts;
        for (int index = 0; index < contexts.Count; index++)
        {
            if (contexts[index].ExternalTextureImporter is
                    DawnGpuContext &&
                contexts[index].AdapterBackendType ==
                    Silk.NET.WebGPU.BackendType.Vulkan)
            {
                return true;
            }
        }

        return false;
    }

    private static void DrainEncoder(
        MediaCodec encoder,
        MediaMuxer muxer,
        MediaCodec.BufferInfo info,
        ref int videoTrack,
        int audioTrack,
        ref bool muxerStarted,
        bool waitForEndOfStream,
        CancellationToken cancellationToken)
    {
        bool complete = false;
        while (!complete)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                (int)MediaCodecInfoState.OutputFormatChanged)
            {
                if (videoTrack >= 0)
                {
                    throw new InvalidDataException(
                        "Android H.264 encoder changed format twice.");
                }
                using MediaFormat outputFormat =
                    encoder.OutputFormat;
                videoTrack =
                    muxer.AddTrack(outputFormat);
                muxer.Start();
                muxerStarted = true;
                continue;
            }
            if (outputIndex < 0)
            {
                continue;
            }

            ByteBuffer output =
                encoder.GetOutputBuffer(outputIndex) ??
                throw new InvalidOperationException(
                    "Android encoder returned no output buffer.");
            try
            {
                bool codecConfiguration =
                    (info.Flags &
                     MediaCodecBufferFlags.CodecConfig) != 0;
                if (!codecConfiguration &&
                    info.Size > 0)
                {
                    if (!muxerStarted || videoTrack < 0)
                    {
                        throw new InvalidOperationException(
                            "Android encoder emitted a sample before its output format.");
                    }
                    output.Position(info.Offset);
                    output.Limit(info.Offset + info.Size);
                    muxer.WriteSampleData(
                        videoTrack,
                        output,
                        info);
                }
                complete =
                    (info.Flags &
                     MediaCodecBufferFlags.EndOfStream) != 0;
            }
            finally
            {
                encoder.ReleaseOutputBuffer(
                    outputIndex,
                    false);
            }
        }
    }

    private static MediaFormat? InspectSources(
        MediaCompositionExportRequest request,
        CancellationToken cancellationToken)
    {
        MediaFormat? retainedAudio = null;
        for (int index = 0;
             index < request.Clips.Count;
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaCompositionExportClip clip =
                request.Clips[index];
            if (clip.ArgbColor.HasValue)
            {
                continue;
            }
            var extractor = new MediaExtractor();
            try
            {
                extractor.SetDataSource(
                    ToSource(clip.SourceUri!));
                int videoTrack =
                    FindTrack(extractor, "video/");
                if (videoTrack < 0)
                {
                    retainedAudio?.Dispose();
                    throw new InvalidDataException(
                        "An Android export clip has no video track.");
                }

                if (request.EncodingProfile.AudioSubtype is
                    null)
                {
                    continue;
                }
                int audioTrack =
                    FindTrack(extractor, "audio/");
                if (audioTrack < 0)
                {
                    retainedAudio?.Dispose();
                    throw new InvalidDataException(
                        "An AAC output was requested but a clip has no audio track.");
                }

                MediaFormat candidate =
                    extractor.GetTrackFormat(audioTrack);
                if (!IsCompatibleAac(
                        candidate,
                        request.EncodingProfile,
                        retainedAudio))
                {
                    candidate.Dispose();
                    retainedAudio?.Dispose();
                    throw new InvalidDataException(
                        "Android compressed AAC export requires identical source AAC configuration matching the requested profile.");
                }

                if (retainedAudio is null)
                {
                    retainedAudio = candidate;
                }
                else
                {
                    candidate.Dispose();
                }
            }
            finally
            {
                extractor.Release();
                extractor.Dispose();
            }
        }
        return retainedAudio;
    }

    private static bool IsCompatibleAac(
        MediaFormat candidate,
        MediaCompositionEncodingProfile profile,
        MediaFormat? baseline)
    {
        if (!string.Equals(
                candidate.GetString("mime"),
                AudioMime,
                StringComparison.OrdinalIgnoreCase) ||
            candidate.GetInteger("sample-rate", 0) !=
                profile.AudioSampleRate ||
            candidate.GetInteger("channel-count", 0) !=
                profile.AudioChannelCount)
        {
            return false;
        }

        int bitrate = candidate.GetInteger("bitrate", 0);
        if (bitrate != profile.AudioBitrate)
        {
            return false;
        }
        if (baseline is null)
        {
            return true;
        }
        return candidate.GetInteger("sample-rate", 0) ==
                   baseline.GetInteger("sample-rate", 0) &&
               candidate.GetInteger("channel-count", 0) ==
                   baseline.GetInteger("channel-count", 0) &&
               EqualCodecData(candidate, baseline, "csd-0") &&
               EqualCodecData(candidate, baseline, "csd-1");
    }

    private static bool EqualCodecData(
        MediaFormat left,
        MediaFormat right,
        string key)
    {
        ByteBuffer? leftData = left.GetByteBuffer(key);
        ByteBuffer? rightData = right.GetByteBuffer(key);
        if (leftData is null || rightData is null)
        {
            return leftData is null && rightData is null;
        }
        if (leftData.Remaining() != rightData.Remaining())
        {
            return false;
        }
        int leftPosition = leftData.Position();
        int rightPosition = rightData.Position();
        for (int index = 0;
             index < leftData.Remaining();
             index++)
        {
            if (leftData.Get(leftPosition + index) !=
                rightData.Get(rightPosition + index))
            {
                return false;
            }
        }
        return true;
    }

    private static void CopyAudioTimeline(
        MediaCompositionExportRequest request,
        MediaMuxer muxer,
        int audioTrack,
        long totalDuration,
        AndroidExportProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        ByteBuffer buffer =
            ByteBuffer.AllocateDirect(64 * 1024);
        using var info = new MediaCodec.BufferInfo();
        long timelineOffset = 0;
        try
        {
            for (int index = 0;
                 index < request.Clips.Count;
                 index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MediaCompositionExportClip clip =
                    request.Clips[index];
                long sourceStart =
                    ToMicroseconds(clip.TrimTimeFromStart);
                long clipDuration =
                    ToMicroseconds(
                        clip.OriginalDuration -
                        clip.TrimTimeFromStart -
                        clip.TrimTimeFromEnd);
                long sourceEnd =
                    checked(sourceStart + clipDuration);
                var extractor = new MediaExtractor();
                try
                {
                    extractor.SetDataSource(
                        ToSource(clip.SourceUri!));
                    int track =
                        FindTrack(extractor, "audio/");
                    extractor.SelectTrack(track);
                    extractor.SeekTo(
                        sourceStart,
                        MediaExtractorSeekTo.PreviousSync);
                    while (extractor.SampleTime >= 0 &&
                           extractor.SampleTime < sourceEnd)
                    {
                        cancellationToken
                            .ThrowIfCancellationRequested();
                        long timestamp =
                            extractor.SampleTime;
                        if (timestamp < sourceStart)
                        {
                            extractor.Advance();
                            continue;
                        }

                        long sampleSize =
                            extractor.SampleSize;
                        if (sampleSize <= 0 ||
                            sampleSize >
                                MaximumCompressedAudioSample)
                        {
                            throw new InvalidDataException(
                                "Android AAC sample size is invalid.");
                        }
                        if (sampleSize > buffer.Capacity())
                        {
                            buffer.Dispose();
                            buffer =
                                ByteBuffer.AllocateDirect(
                                    checked((int)sampleSize));
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
                        long outputTimestamp =
                            checked(
                                timelineOffset +
                                timestamp -
                                sourceStart);
                        info.Set(
                            0,
                            size,
                            outputTimestamp,
                            ToCodecFlags(
                                extractor.SampleFlags));
                        buffer.Position(0);
                        buffer.Limit(size);
                        muxer.WriteSampleData(
                            audioTrack,
                            buffer,
                            info);
                        reporter.ReportTimeline(
                            outputTimestamp,
                            totalDuration,
                            offset: 90d,
                            scale: 0.1d);
                        extractor.Advance();
                    }
                }
                finally
                {
                    extractor.Release();
                    extractor.Dispose();
                }
                timelineOffset =
                    checked(timelineOffset + clipDuration);
            }

            info.Set(
                0,
                0,
                totalDuration,
                MediaCodecBufferFlags.EndOfStream);
            buffer.Clear();
            muxer.WriteSampleData(audioTrack, buffer, info);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    private static MediaFormat CreateVideoEncoderFormat(
        MediaCompositionEncodingProfile profile)
    {
        MediaFormat format =
            MediaFormat.CreateVideoFormat(
                VideoMime,
                checked((int)profile.Width),
                checked((int)profile.Height));
        format.SetInteger(
            "color-format",
            (int)MediaCodecCapabilities.Formatsurface);
        format.SetInteger(
            "bitrate",
            checked((int)profile.VideoBitrate));
        format.SetInteger(
            "frame-rate",
            checked(
                (int)Math.Round(
                    (double)profile.FrameRateNumerator /
                    profile.FrameRateDenominator)));
        format.SetInteger("i-frame-interval", 1);
        return format;
    }

    private static string? FindHardwareEncoder(
        MediaFormat format)
    {
        using var codecs =
            new MediaCodecList(
                MediaCodecListKind.AllCodecs);
        MediaCodecInfo[]? infos = codecs.GetCodecInfos();
        if (infos is null)
        {
            return null;
        }
        for (int index = 0; index < infos.Length; index++)
        {
            MediaCodecInfo? info = infos[index];
            string[]? supportedTypes =
                info?.GetSupportedTypes();
            if (info is null ||
                supportedTypes is null ||
                !info.IsEncoder ||
                !info.IsHardwareAccelerated ||
                !supportedTypes.Any(
                    static type =>
                        string.Equals(
                            type,
                            VideoMime,
                            StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            try
            {
                MediaCodecInfo.CodecCapabilities? capabilities =
                    info.GetCapabilitiesForType(VideoMime);
                if (capabilities?.VideoCapabilities is
                    { } video &&
                    video.AreSizeAndRateSupported(
                        format.GetInteger("width"),
                        format.GetInteger("height"),
                        format.GetInteger("frame-rate")))
                {
                    return info.Name;
                }
            }
            catch (Java.Lang.IllegalArgumentException)
            {
                // Malformed vendor capability entries are not selected.
            }
        }
        return null;
    }

    private static int FindTrack(
        MediaExtractor extractor,
        string prefix)
    {
        for (int index = 0;
             index < extractor.TrackCount;
             index++)
        {
            using MediaFormat format =
                extractor.GetTrackFormat(index);
            string? mime = format.GetString("mime");
            if (mime?.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase) ==
                true)
            {
                return index;
            }
        }
        return -1;
    }

    private static MediaCodecBufferFlags ToCodecFlags(
        MediaExtractorSampleFlags flags)
    {
        MediaCodecBufferFlags result =
            MediaCodecBufferFlags.None;
        if ((flags &
             MediaExtractorSampleFlags.Sync) != 0)
        {
            result |= MediaCodecBufferFlags.KeyFrame;
        }
        if ((flags &
             MediaExtractorSampleFlags.PartialFrame) != 0)
        {
            result |= MediaCodecBufferFlags.PartialFrame;
        }
        return result;
    }

    internal static bool TryGetBuiltInEffects(
        IReadOnlyDictionary<string, string> userData,
        out float saturation,
        out float grayscale)
    {
        saturation = 1f;
        grayscale = 0f;
        if (userData.TryGetValue(
                "progpu.saturation",
                out string? saturationText) &&
            (!float.TryParse(
                saturationText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out saturation) ||
             !float.IsFinite(saturation) ||
             saturation is < 0f or > 1f))
        {
            return false;
        }
        if (userData.TryGetValue(
                "progpu.grayscale",
                out string? grayscaleText) &&
            (!float.TryParse(
                grayscaleText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out grayscale) ||
             !float.IsFinite(grayscale) ||
             grayscale is < 0f or > 1f))
        {
            return false;
        }
        return true;
    }

    internal static bool TryGetVideoEffectPlan(
        MediaCompositionExportClip clip,
        MediaEffectRegistry effects,
        out MediaVideoEffectPlan plan)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(effects);
        plan = MediaVideoEffectPlan.Identity;
        if (!TryGetBuiltInEffects(
                clip.UserData,
                out float saturation,
                out float grayscale) ||
            !MediaCompositionVideoEffectResolver
                .TryCapturePlan(
                    effects,
                    clip.VideoEffectDefinitions,
                    out MediaVideoEffectPlan
                        declared))
        {
            return false;
        }

        MediaVideoColorTransform transform =
            MediaVideoColorEffectFactory
                .CreateTransform(
                    saturation: saturation,
                    grayscale: grayscale)
                .Then(declared.ColorTransform);
        plan = new MediaVideoEffectPlan(
            transform,
            declared.BlurStandardDeviation);
        return true;
    }

    private bool HasVideoEffects(
        MediaCompositionExportRequest request)
    {
        for (int index = 0;
             index < request.Clips.Count;
             index++)
        {
            if (TryGetVideoEffectPlan(
                    request.Clips[index],
                    _effects,
                    out MediaVideoEffectPlan plan) &&
                !plan.IsIdentity)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasSpatialVideoEffects(
        MediaCompositionExportRequest request,
        MediaEffectRegistry effects)
    {
        for (int index = 0;
             index < request.Clips.Count;
             index++)
        {
            if (TryGetVideoEffectPlan(
                    request.Clips[index],
                    effects,
                    out MediaVideoEffectPlan plan) &&
                plan.HasSpatialEffect)
            {
                return true;
            }
        }
        return false;
    }

    private static long GetTimelineDurationMicroseconds(
        MediaCompositionExportRequest request)
    {
        long duration = 0;
        for (int index = 0;
             index < request.Clips.Count;
             index++)
        {
            MediaCompositionExportClip clip =
                request.Clips[index];
            duration = checked(
                duration +
                ToMicroseconds(
                    clip.OriginalDuration -
                    clip.TrimTimeFromStart -
                    clip.TrimTimeFromEnd));
        }
        return Math.Max(1, duration);
    }

    private static long ToMicroseconds(TimeSpan time) =>
        time.Ticks / 10;

    private static string ToSource(Uri source) =>
        source.IsFile
            ? source.LocalPath
            : source.AbsoluteUri;

    private static void TryStop(MediaCodec? codec)
    {
        try
        {
            codec?.Stop();
        }
        catch
        {
            // Cleanup must not hide the export result.
        }
    }

    private static void TryStop(MediaMuxer? muxer)
    {
        try
        {
            muxer?.Stop();
        }
        catch
        {
            // Cleanup must not hide the export result.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup must not hide the export result.
        }
    }

    private sealed class AndroidExportProgressReporter
    {
        private const double MinimumDelta = 0.5d;
        private readonly IProgress<double>? _progress;
        private double _last = double.NegativeInfinity;

        public AndroidExportProgressReporter(
            IProgress<double>? progress)
        {
            _progress = progress;
        }

        public void ReportTimeline(
            long timestamp,
            long duration,
            double offset = 0d,
            double scale = 0.9d)
        {
            double fraction = Math.Clamp(
                (double)timestamp / Math.Max(1, duration),
                0d,
                1d);
            Report(offset + fraction * scale * 100d);
        }

        public void Report(double value)
        {
            value = Math.Clamp(value, 0d, 100d);
            if (value < 100d &&
                value - _last < MinimumDelta)
            {
                return;
            }
            _last = value;
            _progress?.Report(value);
        }
    }
}

/// <summary>
/// Retained decoder-surface to encoder-surface OpenGL ES bridge. One external
/// OES texture, program, quad buffer, EGL context, and EGL window surface are
/// created per export. Decoded and uniform-color draws are O(W*H) GPU fragment
/// work and O(1) managed work with no per-frame allocation or pixel transfer.
/// </summary>
internal sealed class AndroidEncoderSurfaceRenderer :
    IAndroidEncoderSurfaceRenderer
{
    private const long FrameWaitMilliseconds = 5_000;
    private static readonly string s_vertexShader =
        ShaderResource.Load(
            typeof(AndroidEncoderSurfaceRenderer),
            "AndroidMediaCompositionVertex.glsl");
    private static readonly string s_fragmentShader =
        ShaderResource.Load(
            typeof(AndroidEncoderSurfaceRenderer),
            "AndroidMediaCompositionFragment.glsl");
    private static readonly float[] s_quad =
    [
        -1f, -1f, 0f, 0f,
         1f, -1f, 1f, 0f,
        -1f,  1f, 0f, 1f,
         1f,  1f, 1f, 1f
    ];

    private readonly int _width;
    private readonly int _height;
    private readonly AutoResetEvent _frameAvailable = new(false);
    private readonly HandlerThread _callbackThread;
    private readonly Handler _callbackHandler;
    private readonly FrameListener _listener;
    private readonly float[] _textureTransform = new float[16];
    private readonly int[] _textureIds = new int[1];
    private readonly FloatBuffer _quad;
    private EGLDisplay? _display;
    private EGLContext? _context;
    private EGLSurface? _eglSurface;
    private SurfaceTexture? _surfaceTexture;
    private Surface? _decoderSurface;
    private int _program;
    private int _vertexShader;
    private int _fragmentShader;
    private int _positionLocation;
    private int _texCoordLocation;
    private int _transformLocation;
    private int _redTransformLocation;
    private int _greenTransformLocation;
    private int _blueTransformLocation;
    private int _useSolidColorLocation;
    private int _solidColorLocation;
    private int _disposed;

    public AndroidEncoderSurfaceRenderer(
        Surface encoderSurface,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(encoderSurface);
        _width = width;
        _height = height;
        _quad = ByteBuffer
            .AllocateDirect(s_quad.Length * sizeof(float))
            .Order(ByteOrder.NativeOrder()!)
            .AsFloatBuffer();
        _quad.Put(s_quad);
        _quad.Position(0);
        _callbackThread =
            new HandlerThread(
                "ProGPU Android Export Surface");
        _callbackThread.Start();
        _callbackHandler =
            new Handler(
                _callbackThread.Looper ??
                throw new InvalidOperationException(
                    "Android could not create the export callback looper."));
        _listener = new FrameListener(_frameAvailable);

        try
        {
            InitializeEgl(encoderSurface);
            InitializeGl();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Surface DecoderSurface =>
        _decoderSurface ??
        throw new ObjectDisposedException(
            nameof(AndroidEncoderSurfaceRenderer));

    public void DrawFrame(
        long presentationTimeMicroseconds,
        MediaVideoEffectPlan effectPlan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long deadline =
            Stopwatch.GetTimestamp() +
            FrameWaitMilliseconds *
            Stopwatch.Frequency /
            1_000;
        while (!_frameAvailable.WaitOne(50))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException(
                    "Android decoder did not deliver its output surface frame.");
            }
        }

        SurfaceTexture texture =
            _surfaceTexture ??
            throw new ObjectDisposedException(
                nameof(AndroidEncoderSurfaceRenderer));
        texture.UpdateTexImage();
        texture.GetTransformMatrix(_textureTransform);

        DrawGpuFrame(
            presentationTimeMicroseconds,
            RequireAffine(effectPlan),
            useSolidColor: false,
            argbColor: 0);
    }

    public void DrawColorFrame(
        long presentationTimeMicroseconds,
        uint argbColor,
        MediaVideoEffectPlan effectPlan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (presentationTimeMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(presentationTimeMicroseconds));
        }
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        DrawGpuFrame(
            presentationTimeMicroseconds,
            RequireAffine(effectPlan),
            useSolidColor: true,
            argbColor: argbColor);
    }

    private static MediaVideoColorTransform RequireAffine(
        MediaVideoEffectPlan effectPlan)
    {
        if (effectPlan.HasSpatialEffect)
        {
            throw new NotSupportedException(
                "The Android EGL fallback cannot execute spatial media effects.");
        }
        return effectPlan.ColorTransform;
    }

    private void DrawGpuFrame(
        long presentationTimeMicroseconds,
        MediaVideoColorTransform transform,
        bool useSolidColor,
        uint argbColor)
    {
        GLES20.GlViewport(0, 0, _width, _height);
        GLES20.GlUseProgram(_program);
        GLES20.GlActiveTexture(GLES20.GlTexture0);
        GLES20.GlBindTexture(
            GLES11Ext.GlTextureExternalOes,
            _textureIds[0]);

        _quad.Position(0);
        GLES20.GlEnableVertexAttribArray(
            _positionLocation);
        GLES20.GlVertexAttribPointer(
            _positionLocation,
            2,
            GLES20.GlFloat,
            false,
            4 * sizeof(float),
            _quad);
        _quad.Position(2);
        GLES20.GlEnableVertexAttribArray(
            _texCoordLocation);
        GLES20.GlVertexAttribPointer(
            _texCoordLocation,
            2,
            GLES20.GlFloat,
            false,
            4 * sizeof(float),
            _quad);
        GLES20.GlUniformMatrix4fv(
            _transformLocation,
            1,
            false,
            _textureTransform,
            0);
        GLES20.GlUniform4f(
            _redTransformLocation,
            transform.Red.X,
            transform.Red.Y,
            transform.Red.Z,
            transform.Red.W);
        GLES20.GlUniform4f(
            _greenTransformLocation,
            transform.Green.X,
            transform.Green.Y,
            transform.Green.Z,
            transform.Green.W);
        GLES20.GlUniform4f(
            _blueTransformLocation,
            transform.Blue.X,
            transform.Blue.Y,
            transform.Blue.Z,
            transform.Blue.W);
        GLES20.GlUniform1f(
            _useSolidColorLocation,
            useSolidColor ? 1f : 0f);
        const float colorScale =
            1f / byte.MaxValue;
        GLES20.GlUniform4f(
            _solidColorLocation,
            ((argbColor >> 16) & 0xff) * colorScale,
            ((argbColor >> 8) & 0xff) * colorScale,
            (argbColor & 0xff) * colorScale,
            ((argbColor >> 24) & 0xff) * colorScale);
        GLES20.GlDrawArrays(
            GLES20.GlTriangleStrip,
            0,
            4);
        GLES20.GlDisableVertexAttribArray(
            _positionLocation);
        GLES20.GlDisableVertexAttribArray(
            _texCoordLocation);

        EGLExt.EglPresentationTimeANDROID(
            _display!,
            _eglSurface!,
            checked(presentationTimeMicroseconds * 1_000));
        if (!EGL14.EglSwapBuffers(
                _display!,
                _eglSurface!))
        {
            throw new InvalidOperationException(
                $"Android EGL swap failed: 0x{EGL14.EglGetError():X}.");
        }
    }

    private void InitializeEgl(Surface encoderSurface)
    {
        _display =
            EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
        if (_display is null ||
            !EGL14.EglInitialize(
                _display,
                new int[1],
                0,
                new int[1],
                0))
        {
            throw new InvalidOperationException(
                "Android could not initialize EGL.");
        }

        int[] attributes =
        [
            EGL14.EglRedSize, 8,
            EGL14.EglGreenSize, 8,
            EGL14.EglBlueSize, 8,
            EGL14.EglAlphaSize, 8,
            EGL14.EglRenderableType,
            EGL14.EglOpenglEs2Bit,
            EGL14.EglSurfaceType,
            EGL14.EglWindowBit,
            EGL14.EglNone
        ];
        var configs = new EGLConfig[1];
        var count = new int[1];
        if (!EGL14.EglChooseConfig(
                _display,
                attributes,
                0,
                configs,
                0,
                1,
                count,
                0) ||
            count[0] != 1)
        {
            throw new InvalidOperationException(
                "Android could not choose an encoder EGL configuration.");
        }

        _context = EGL14.EglCreateContext(
            _display,
            configs[0],
            EGL14.EglNoContext,
            [
                EGL14.EglContextClientVersion,
                2,
                EGL14.EglNone
            ],
            0);
        _eglSurface = EGL14.EglCreateWindowSurface(
            _display,
            configs[0],
            encoderSurface,
            [EGL14.EglNone],
            0);
        if (_context is null ||
            _eglSurface is null ||
            !EGL14.EglMakeCurrent(
                _display,
                _eglSurface,
                _eglSurface,
                _context))
        {
            throw new InvalidOperationException(
                $"Android could not create the encoder EGL surface: 0x{EGL14.EglGetError():X}.");
        }
    }

    private void InitializeGl()
    {
        _vertexShader =
            CompileShader(
                GLES20.GlVertexShader,
                s_vertexShader);
        _fragmentShader =
            CompileShader(
                GLES20.GlFragmentShader,
                s_fragmentShader);
        _program = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(_program, _vertexShader);
        GLES20.GlAttachShader(_program, _fragmentShader);
        GLES20.GlLinkProgram(_program);
        var status = new int[1];
        GLES20.GlGetProgramiv(
            _program,
            GLES20.GlLinkStatus,
            status,
            0);
        if (status[0] == 0)
        {
            throw new InvalidOperationException(
                $"Android media effect program link failed: {GLES20.GlGetProgramInfoLog(_program)}");
        }

        _positionLocation =
            GLES20.GlGetAttribLocation(
                _program,
                "a_position");
        _texCoordLocation =
            GLES20.GlGetAttribLocation(
                _program,
                "a_tex_coord");
        _transformLocation =
            GLES20.GlGetUniformLocation(
                _program,
                "u_tex_transform");
        _redTransformLocation =
            GLES20.GlGetUniformLocation(
                _program,
                "u_red_transform");
        _greenTransformLocation =
            GLES20.GlGetUniformLocation(
                _program,
                "u_green_transform");
        _blueTransformLocation =
            GLES20.GlGetUniformLocation(
                _program,
                "u_blue_transform");
        _useSolidColorLocation =
            GLES20.GlGetUniformLocation(
                _program,
                "u_use_solid_color");
        _solidColorLocation =
            GLES20.GlGetUniformLocation(
                _program,
                "u_solid_color");

        GLES20.GlGenTextures(1, _textureIds, 0);
        GLES20.GlBindTexture(
            GLES11Ext.GlTextureExternalOes,
            _textureIds[0]);
        GLES20.GlTexParameteri(
            GLES11Ext.GlTextureExternalOes,
            GLES20.GlTextureMinFilter,
            GLES20.GlLinear);
        GLES20.GlTexParameteri(
            GLES11Ext.GlTextureExternalOes,
            GLES20.GlTextureMagFilter,
            GLES20.GlLinear);
        GLES20.GlTexParameteri(
            GLES11Ext.GlTextureExternalOes,
            GLES20.GlTextureWrapS,
            GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(
            GLES11Ext.GlTextureExternalOes,
            GLES20.GlTextureWrapT,
            GLES20.GlClampToEdge);

        _surfaceTexture =
            new SurfaceTexture(_textureIds[0]);
        _surfaceTexture.SetDefaultBufferSize(
            _width,
            _height);
        _surfaceTexture.SetOnFrameAvailableListener(
            _listener,
            _callbackHandler);
        _decoderSurface =
            new Surface(_surfaceTexture);
    }

    private static int CompileShader(
        int type,
        string source)
    {
        int shader = GLES20.GlCreateShader(type);
        GLES20.GlShaderSource(shader, source);
        GLES20.GlCompileShader(shader);
        var status = new int[1];
        GLES20.GlGetShaderiv(
            shader,
            GLES20.GlCompileStatus,
            status,
            0);
        if (status[0] == 0)
        {
            string? log =
                GLES20.GlGetShaderInfoLog(shader);
            GLES20.GlDeleteShader(shader);
            throw new InvalidOperationException(
                $"Android media effect shader compilation failed: {log}");
        }
        return shader;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _surfaceTexture?.SetOnFrameAvailableListener(null);
        _decoderSurface?.Release();
        _decoderSurface?.Dispose();
        _decoderSurface = null;
        _surfaceTexture?.Release();
        _surfaceTexture?.Dispose();
        _surfaceTexture = null;
        if (_textureIds[0] != 0)
        {
            GLES20.GlDeleteTextures(1, _textureIds, 0);
        }
        if (_program != 0)
        {
            GLES20.GlDeleteProgram(_program);
        }
        if (_vertexShader != 0)
        {
            GLES20.GlDeleteShader(_vertexShader);
        }
        if (_fragmentShader != 0)
        {
            GLES20.GlDeleteShader(_fragmentShader);
        }
        if (_display is not null)
        {
            EGL14.EglMakeCurrent(
                _display,
                EGL14.EglNoSurface,
                EGL14.EglNoSurface,
                EGL14.EglNoContext);
            if (_eglSurface is not null)
            {
                EGL14.EglDestroySurface(
                    _display,
                    _eglSurface);
            }
            if (_context is not null)
            {
                EGL14.EglDestroyContext(
                    _display,
                    _context);
            }
            EGL14.EglReleaseThread();
            EGL14.EglTerminate(_display);
        }
        _callbackThread.QuitSafely();
        _callbackThread.Join();
        _callbackHandler.Dispose();
        _callbackThread.Dispose();
        _listener.Dispose();
        _frameAvailable.Dispose();
        _quad.Dispose();
    }

    private sealed class FrameListener :
        Java.Lang.Object,
        SurfaceTexture.IOnFrameAvailableListener
    {
        private readonly AutoResetEvent _available;

        public FrameListener(AutoResetEvent available)
        {
            _available = available;
        }

        public void OnFrameAvailable(
            SurfaceTexture? surfaceTexture) =>
            _available.Set();
    }
}
