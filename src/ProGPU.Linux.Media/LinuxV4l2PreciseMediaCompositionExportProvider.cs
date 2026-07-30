using System.Globalization;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Media.Containers;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using Silk.NET.WebGPU;

namespace ProGPU.Linux.Media;

/// <summary>
/// Dependency-free Linux precise exporter for ordered local H.264 and
/// generated-color clips. A single identity URI clip transfers directly
/// between V4L2 queues. Scaled output, multi-clip timelines, generated frames,
/// and built-in effects use bounded GBM NV12 targets shared with WebGPU and
/// the encoder. Compatible selected AAC tracks remain compressed and use
/// ISO-BMFF edit lists for exact trims and silent clip spans.
/// </summary>
/// <remarks>
/// Decode and encode are O(F + B) for F frames and B compressed bytes.
/// Effect rendering is O(P) for P output pixels per frame. Decoded-pixel
/// copies and CPU mappings are zero. GPU and native queue residency is
/// bounded independently of frame count.
/// </remarks>
public sealed class
    LinuxV4l2PreciseMediaCompositionExportProvider :
    IMediaCompositionExportProvider,
    IMediaCompositionExportCapabilityProvider
{
    private const string ProviderId =
        "progpu.linux.v4l2.precise-export";
    private readonly LinuxNativeMediaCapabilitySnapshot
        _capabilities;
    private readonly MediaEffectRegistry _effects;

    public LinuxV4l2PreciseMediaCompositionExportProvider(
        LinuxNativeMediaCapabilitySnapshot capabilities,
        int priority = 100,
        MediaEffectRegistry? effects = null)
    {
        _capabilities = capabilities;
        Priority = priority;
        _effects = effects ?? MediaEffectRegistry.Default;
    }

    public string Id => ProviderId;

    public int Priority { get; }

    public bool CanRender(
        MediaCompositionExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool requiresGpu =
            RequiresGpuComposition(
                request,
                _effects);
        bool gpuAvailable =
            !requiresGpu ||
            LinuxGbmNative.IsAvailable() &&
            TryGetActiveVulkanDawnContext(out _);
        return CanRenderRequest(
            request,
            OperatingSystem.IsLinux(),
            HasNativeH264Path(),
            HasNativeH264EncoderPath(),
            gpuAvailable,
            _effects);
    }

    internal static bool CanRenderRequest(
        MediaCompositionExportRequest request,
        bool isLinux,
        bool hasNativeH264Path,
        bool hasNativeTwoPlaneH264EncoderPath,
        bool gpuAvailable,
        MediaEffectRegistry? effectRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool audioRequested =
            request.EncodingProfile.AudioSubtype is
                not null;
        if (!isLinux ||
            request.TrimmingMode !=
                MediaCompositionTrimmingMode.Precise ||
            request.Clips.Count == 0 ||
            request.BackgroundAudioTracks.Count != 0 ||
            audioRequested &&
            !string.Equals(
                request.EncodingProfile.AudioSubtype,
                "AAC",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.EncodingProfile.ContainerSubtype,
                "MPEG4",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.EncodingProfile.VideoSubtype,
                "H264",
                StringComparison.OrdinalIgnoreCase) ||
            request.EncodingProfile.Width == 0 ||
            request.EncodingProfile.Height == 0 ||
            request.EncodingProfile.VideoBitrate == 0 ||
            request.EncodingProfile.FrameRateNumerator == 0 ||
            request.EncodingProfile.FrameRateDenominator == 0 ||
            audioRequested &&
            (request.EncodingProfile.AudioBitrate == 0 ||
             request.EncodingProfile.AudioSampleRate == 0 ||
             request.EncodingProfile.AudioChannelCount == 0))
        {
            return false;
        }

        bool hasUriClip = false;
        bool hasDeclaredAac = false;
        bool hasUnknownAudioMetadata = false;
        bool requiresGpu =
            request.Clips.Count > 1;
        for (int index = 0;
             index < request.Clips.Count;
             index++)
        {
            MediaCompositionExportClip clip =
                request.Clips[index];
            if (!TryGetVideoEffectPlan(
                    clip,
                    effectRegistry ??
                        MediaEffectRegistry.Default,
                    out LinuxGpuVideoEffectPlan
                        effectPlan))
            {
                return false;
            }
            bool hasEffects =
                !effectPlan.IsIdentity;
            bool hasSource =
                clip.SourceUri is
                { IsFile: true };
            bool hasColor =
                clip.ArgbColor.HasValue;
            bool scaling =
                hasSource &&
                clip.SourceVideoWidth != 0 &&
                clip.SourceVideoHeight != 0 &&
                (clip.SourceVideoWidth !=
                     request.EncodingProfile.Width ||
                 clip.SourceVideoHeight !=
                     request.EncodingProfile.Height);
            bool sourceMetadataKnown =
                clip.SourceVideoWidth != 0 &&
                clip.SourceVideoHeight != 0;
            if (audioRequested &&
                hasSource)
            {
                if (clip.SourceAudioSubtype is
                        null)
                {
                    hasUnknownAudioMetadata |=
                        !sourceMetadataKnown;
                }
                else
                {
                    if (!string.Equals(
                            clip.SourceAudioSubtype,
                            "AAC",
                            StringComparison
                                .OrdinalIgnoreCase) ||
                        clip.SourceAudioBitrate !=
                            request.EncodingProfile
                                .AudioBitrate ||
                        clip.SourceAudioSampleRate !=
                            request.EncodingProfile
                                .AudioSampleRate ||
                        clip.SourceAudioChannelCount !=
                            request.EncodingProfile
                                .AudioChannelCount)
                    {
                        return false;
                    }
                    hasDeclaredAac = true;
                }
            }
            if (hasSource == hasColor ||
                (clip.SourceVideoWidth == 0) !=
                    (clip.SourceVideoHeight == 0) ||
                clip.OriginalDuration <=
                    TimeSpan.Zero ||
                clip.TrimTimeFromStart <
                    TimeSpan.Zero ||
                clip.TrimTimeFromEnd <
                    TimeSpan.Zero ||
                clip.TrimTimeFromStart +
                    clip.TrimTimeFromEnd >=
                    clip.OriginalDuration ||
                clip.Volume != 1d ||
                clip.AudioEffectDefinitions.Count !=
                    0)
            {
                return false;
            }

            hasUriClip |= hasSource;
            requiresGpu |=
                hasEffects ||
                hasColor ||
                scaling;
        }

        if (!LinuxMediaColorOverlayPlanner.TryCapture(
                request,
                effectRegistry ??
                    MediaEffectRegistry.Default,
                out LinuxMediaColorOverlayPlan[]
                    overlays))
        {
            return false;
        }
        requiresGpu |= overlays.Length != 0;

        if (audioRequested &&
            !hasDeclaredAac &&
            !hasUnknownAudioMetadata)
        {
            return false;
        }

        if (requiresGpu)
        {
            return
                hasNativeTwoPlaneH264EncoderPath &&
                (!hasUriClip ||
                 hasNativeH264Path) &&
                gpuAvailable;
        }

        return hasNativeH264Path;
    }

    public MediaCompositionExportCapabilities
        GetCapabilities(
            MediaCompositionExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanRender(request))
        {
            throw new ArgumentException(
                "The request is not supported by this provider.",
                nameof(request));
        }
        return GetCapabilitiesForRequest(
            request,
            _effects);
    }

    internal static MediaCompositionExportCapabilities
        GetCapabilitiesForRequest(
            MediaCompositionExportRequest request,
            MediaEffectRegistry? effectRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool effects = false;
        for (int index = 0;
             index < request.Clips.Count;
             index++)
        {
            MediaCompositionExportClip clip =
                request.Clips[index];
            effects |=
                TryGetVideoEffectPlan(
                    clip,
                    effectRegistry ??
                        MediaEffectRegistry.Default,
                    out LinuxGpuVideoEffectPlan
                        effectPlan) &&
                !effectPlan.IsIdentity;
        }
        if (LinuxMediaColorOverlayPlanner.TryCapture(
                request,
                effectRegistry ??
                    MediaEffectRegistry.Default,
                out LinuxMediaColorOverlayPlan[]
                    overlays))
        {
            for (int index = 0;
                 index < overlays.Length;
                 index++)
            {
                effects |=
                    !overlays[index]
                        .EffectPlan.IsIdentity;
            }
        }
        bool gpuComposition =
            RequiresGpuComposition(
                request,
                effectRegistry ??
                    MediaEffectRegistry.Default);
        return new MediaCompositionExportCapabilities(
            ProviderId,
            gpuComposition
                ? MediaCompositionExportVideoPath
                    .GpuCopy
                : MediaCompositionExportVideoPath
                    .NativeGpuSurface,
            request.EncodingProfile.AudioSubtype is
                null
                ? MediaCompositionExportAudioPath.None
                : MediaCompositionExportAudioPath
                    .CompressedSampleCopy,
            HardwareVideoEncoderRequested: true,
            HardwareVideoEncoderGuaranteed: false,
            EffectsBakedOnGpu: effects,
            Limitation:
                "The current Linux precise lane accepts ordered local " +
                "H.264 and generated-color clips and requires " +
                "compatible NV12 DMA-BUF sharing " +
                "between V4L2, GBM, and Dawn/Vulkan. Registered affine " +
                "color effects plus output scaling are fused into a bounded " +
                "zero-copy WebGPU lane. A single native-size identity URI " +
                "clip retains direct " +
                "decoder-to-encoder DMA-BUF transfer; ordered timelines " +
                "and standard positioned/opacity solid-color overlays use " +
                "retained WebGPU passes in declared order. Matching selected AAC " +
                "tracks remain compressed; edit lists retain exact trims " +
                "and silent source/color spans. Audio gain/effects, " +
                "background audio, URI overlays, and custom compositors are " +
                "not yet accepted.");
    }

    public async ValueTask<MediaCompositionExportFailure>
        RenderAsync(
            MediaCompositionExportRequest request,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRender(request))
        {
            return MediaCompositionExportFailure
                .InvalidProfile;
        }

        string destination = Path.GetFullPath(
            request.DestinationPath);
        string? directory =
            Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory))
        {
            return MediaCompositionExportFailure
                .InvalidProfile;
        }
        Directory.CreateDirectory(directory);
        string token = Guid.NewGuid().ToString("N");
        string spool = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{token}.h264-spool");
        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{token}.tmp");

        try
        {
            await Task.Run(
                    () => Transcode(
                        request,
                        spool,
                        temporary,
                        progress,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(
                temporary,
                destination,
                overwrite: true);
            progress?.Report(100d);
            return MediaCompositionExportFailure.None;
        }
        catch (OperationCanceledException)
            when (cancellationToken
                .IsCancellationRequested)
        {
            TryDelete(temporary);
            throw;
        }
        catch (InvalidDataException)
        {
            TryDelete(temporary);
            return MediaCompositionExportFailure
                .InvalidProfile;
        }
        catch (NotSupportedException)
        {
            TryDelete(temporary);
            return MediaCompositionExportFailure
                .CodecNotFound;
        }
        catch
        {
            TryDelete(temporary);
            return MediaCompositionExportFailure.Unknown;
        }
        finally
        {
            TryDelete(spool);
        }
    }

    private void Transcode(
        MediaCompositionExportRequest request,
        string spoolPath,
        string temporaryPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!LinuxMediaColorOverlayPlanner.TryCapture(
                request,
                _effects,
                out LinuxMediaColorOverlayPlan[]
                    overlays))
        {
            throw new InvalidDataException(
                "The Linux overlay plan is invalid.");
        }
        IsoBmffCompositionTrack? audioTrack =
            request.EncodingProfile.AudioSubtype is
                null
                ? null
                : IsoBmffPreciseAacTimelinePlanner
                    .Create(
                        request,
                        cancellationToken);
        if (request.Clips.Count > 1 ||
            overlays.Length != 0)
        {
            if (!TryGetActiveVulkanDawnContext(
                    out DawnGpuContext? timelineDawn) ||
                timelineDawn is null)
            {
                throw new NotSupportedException(
                    "The Linux multi-clip composition lane requires an active Vulkan Dawn context.");
            }
            TranscodeTimeline(
                request,
                timelineDawn,
                overlays,
                audioTrack,
                spoolPath,
                temporaryPath,
                progress,
                cancellationToken);
            return;
        }

        MediaCompositionExportClip clip =
            request.Clips[0];
        if (!TryGetVideoEffectPlan(
                clip,
                _effects,
                out LinuxGpuVideoEffectPlan
                    effectPlan))
        {
            throw new InvalidDataException(
                "The WebGPU effect graph is invalid.");
        }
        bool effects =
            !effectPlan.IsIdentity;
        bool gpuComposition =
            effects ||
            clip.ArgbColor.HasValue;
        DawnGpuContext? dawn = null;
        if (gpuComposition &&
            !TryGetActiveVulkanDawnContext(
                out dawn))
        {
            throw new NotSupportedException(
                "The Linux WebGPU composition export lane requires an active Vulkan Dawn context.");
        }
        if (clip.ArgbColor is uint argbColor)
        {
            TranscodeColor(
                request,
                argbColor,
                effectPlan,
                dawn!,
                audioTrack,
                spoolPath,
                temporaryPath,
                progress,
                cancellationToken);
            return;
        }
        string sourcePath = Path.GetFullPath(
            clip.SourceUri!.LocalPath);
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.RandomAccess);
        IsoBmffTrack track =
            SelectTrack(
                new IsoBmffDemuxer(source)
                    .Parse());
        bool scaling =
            RequiresScaling(request, track);
        gpuComposition |= scaling;
        if (gpuComposition &&
            dawn is null &&
            !TryGetActiveVulkanDawnContext(
                out dawn))
        {
            throw new NotSupportedException(
                "Scaled Linux composition export requires an active Vulkan Dawn context.");
        }

        TimeSpan sourceDuration =
            FromTrackTime(
                track.Duration,
                track.Timescale);
        TimeSpan trimStart =
            clip.TrimTimeFromStart;
        TimeSpan trimEnd = sourceDuration -
            clip.TrimTimeFromEnd;
        if (trimStart < TimeSpan.Zero ||
            trimEnd <= trimStart ||
            trimEnd > sourceDuration)
        {
            throw new InvalidDataException(
                "The precise trim interval is empty or outside the source duration.");
        }

        LinuxVideoDecoderDevice decoderDevice =
            SelectDecoder(track);
        using var reader =
            new IsoBmffNalAccessUnitReader(
                source,
                track);
        using var decoder =
            new V4l2StatefulVideoDecoder(
                decoderDevice.Path,
                track,
                preferNv12Capture: true);
        using var encodedSpool =
            new IsoBmffH264AccessUnitSpool(
                spoolPath,
                request.EncodingProfile.Width,
                request.EncodingProfile.Height,
                request.EncodingProfile
                    .FrameRateNumerator,
                request.EncodingProfile
                    .FrameRateDenominator);

        V4l2StatefulVideoEncoder? encoder = null;
        LinuxWebGpuNv12EncoderFrameSink? effectSink =
            null;
        V4l2DecodedFrame pendingFrame = default;
        bool hasPendingFrame = false;
        try
        {
            decoder.Open();
            int sampleIndex =
                FindDecodeStart(
                    track,
                    trimStart);
            bool decoderDraining = false;
            bool encoderDraining = false;

            while (encoder is null ||
                   !encoder.EndOfStreamReached)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                while (sampleIndex <
                       track.Samples.Length)
                {
                    ReadOnlySpan<byte> accessUnit =
                        reader.Read(sampleIndex);
                    if (!decoder.TryQueueAccessUnit(
                            accessUnit,
                            PresentationTime(
                                track,
                                sampleIndex)))
                    {
                        break;
                    }
                    sampleIndex++;
                }

                V4l2DecoderPumpResult pump =
                    decoder.Pump(
                        timeoutMilliseconds: 4);
                if (pump ==
                    V4l2DecoderPumpResult
                        .SourceChanged)
                {
                    if (decoder.IsCaptureConfigured)
                    {
                        throw new NotSupportedException(
                            "Dynamic source-size changes are not supported by the current precise exporter.");
                    }
                    decoder.ConfigureCapture();
                    if (decoder.DecodedPixelFormat !=
                            V4l2DecodedPixelFormat.Nv12 ||
                        decoder.CaptureWidth !=
                            track.Width ||
                        decoder.CaptureHeight !=
                            track.Height)
                    {
                        throw new NotSupportedException(
                            "The V4L2 decoder did not expose source-size linear NV12 DMA-BUF output.");
                    }
                }

                if (!hasPendingFrame &&
                    decoder.TryDequeueFrame(
                        out pendingFrame))
                {
                    hasPendingFrame = true;
                }

                if (hasPendingFrame &&
                    (pendingFrame.PresentationTime <
                         trimStart ||
                     pendingFrame.PresentationTime >=
                         trimEnd))
                {
                    pendingFrame.Owner.Dispose();
                    pendingFrame = default;
                    hasPendingFrame = false;
                }

                if (hasPendingFrame)
                {
                    TimeSpan outputTime =
                        pendingFrame
                            .PresentationTime -
                        trimStart;
                    if (gpuComposition)
                    {
                        encoder ??=
                            CreateEncoder(
                                planeCount: 2,
                                request);
                        if (effectSink is null &&
                            !LinuxWebGpuNv12EncoderFrameSink
                                .TryCreate(
                                    dawn!,
                                    request.EncodingProfile
                                        .Width,
                                    request.EncodingProfile
                                        .Height,
                                    out effectSink))
                        {
                            throw new NotSupportedException(
                                "No GBM allocation is renderable by the active Dawn/Vulkan adapter and importable by V4L2.");
                        }
                        try
                        {
                            if (effectSink.TryProcessFrame(
                                    in pendingFrame,
                                    outputTime,
                                    effectPlan,
                                    encoder))
                            {
                                pendingFrame = default;
                                hasPendingFrame = false;
                            }
                        }
                        catch
                        {
                            // The effect sink takes ownership once it
                            // dequeues a target slot. Avoid a second release
                            // while unwinding the export.
                            pendingFrame = default;
                            hasPendingFrame = false;
                            throw;
                        }
                    }
                    else
                    {
                        encoder ??=
                            CreateEncoder(
                                pendingFrame.DmaBuf
                                    .PlaneCount,
                                request);
                        ProGpuDmaBufDescriptor dmaBuf =
                            pendingFrame.DmaBuf;
                        if (encoder.TryQueueFrame(
                                in dmaBuf,
                                outputTime,
                                pendingFrame.Owner))
                        {
                            pendingFrame = default;
                            hasPendingFrame = false;
                        }
                    }
                }

                if (encoder is not null)
                {
                    encoder.Pump();
                    while (encoder
                        .TryDequeueAccessUnit(
                            out V4l2EncodedAccessUnit
                                accessUnit))
                    {
                        using (accessUnit)
                        {
                            encodedSpool.Append(
                                accessUnit.Data,
                                accessUnit
                                    .PresentationTime,
                                accessUnit.IsKeyFrame);
                        }
                    }
                }

                if (sampleIndex ==
                        track.Samples.Length &&
                    decoder.IsCaptureConfigured &&
                    !decoder.HasQueuedOutput &&
                    !decoderDraining)
                {
                    decoder.BeginDrain();
                    decoderDraining = true;
                }

                if (decoderDraining &&
                    decoder.EndOfStreamReached &&
                    !hasPendingFrame)
                {
                    if (encoder is null)
                    {
                        throw new InvalidDataException(
                            "The exact trim interval contains no decoded video frame.");
                    }
                    if (!encoder.HasQueuedInput &&
                        !encoderDraining)
                    {
                        encoder.BeginDrain();
                        encoderDraining = true;
                    }
                }

                progress?.Report(
                    track.Samples.Length == 0
                        ? 0d
                        : Math.Min(
                            85d,
                            sampleIndex * 85d /
                            track.Samples.Length));
            }

            IsoBmffCompositionPlan plan =
                CreateCompositionPlan(
                    encodedSpool,
                    audioTrack);
            IsoBmffCompositionWriter.WriteAsync(
                    plan,
                    temporaryPath,
                    progress: null,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
            progress?.Report(99d);
        }
        finally
        {
            if (hasPendingFrame)
            {
                pendingFrame.Owner.Dispose();
            }
            encoder?.Dispose();
            effectSink?.Dispose();
        }
    }

    private void TranscodeColor(
        MediaCompositionExportRequest request,
        uint argbColor,
        LinuxGpuVideoEffectPlan effectPlan,
        DawnGpuContext dawn,
        IsoBmffCompositionTrack? audioTrack,
        string spoolPath,
        string temporaryPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        MediaCompositionExportClip clip =
            request.Clips[0];
        TimeSpan duration =
            clip.OriginalDuration -
            clip.TrimTimeFromStart -
            clip.TrimTimeFromEnd;
        using var encodedSpool =
            new IsoBmffH264AccessUnitSpool(
                spoolPath,
                request.EncodingProfile.Width,
                request.EncodingProfile.Height,
                request.EncodingProfile
                    .FrameRateNumerator,
                request.EncodingProfile
                    .FrameRateDenominator);
        using V4l2StatefulVideoEncoder encoder =
            CreateEncoder(
                planeCount: 2,
                request);
        if (!LinuxWebGpuNv12EncoderFrameSink
                .TryCreate(
                    dawn,
                    request.EncodingProfile.Width,
                    request.EncodingProfile.Height,
                    out LinuxWebGpuNv12EncoderFrameSink
                        effectSink))
        {
            throw new NotSupportedException(
                "No GBM allocation is renderable by the active Dawn/Vulkan adapter and importable by V4L2.");
        }
        using (effectSink)
        {
            long frameOffset = 0;
            ulong frameRemainder = 0;
            bool encoderDraining = false;
            while (!encoder.EndOfStreamReached)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
                if (frameOffset < duration.Ticks &&
                    effectSink.TryProcessColorFrame(
                        argbColor,
                        TimeSpan.FromTicks(frameOffset),
                        effectPlan,
                        encoder))
                {
                    progress?.Report(
                        Math.Min(
                            85d,
                            frameOffset * 85d /
                            Math.Max(1, duration.Ticks)));
                    frameOffset =
                        GetNextColorFrameTimestamp(
                            frameOffset,
                            ref frameRemainder,
                            request.EncodingProfile
                                .FrameRateNumerator,
                            request.EncodingProfile
                                .FrameRateDenominator);
                }

                encoder.Pump(
                    timeoutMilliseconds: 4);
                while (encoder.TryDequeueAccessUnit(
                           out V4l2EncodedAccessUnit
                               accessUnit))
                {
                    using (accessUnit)
                    {
                        encodedSpool.Append(
                            accessUnit.Data,
                            accessUnit.PresentationTime,
                            accessUnit.IsKeyFrame);
                    }
                }

                if (frameOffset >= duration.Ticks &&
                    !encoder.HasQueuedInput &&
                    !encoderDraining)
                {
                    encoder.BeginDrain();
                    encoderDraining = true;
                }
            }
        }

        IsoBmffCompositionPlan plan =
            CreateCompositionPlan(
                encodedSpool,
                audioTrack);
        IsoBmffCompositionWriter.WriteAsync(
                plan,
                temporaryPath,
                progress: null,
                cancellationToken)
            .GetAwaiter()
            .GetResult();
        progress?.Report(99d);
    }

    private void TranscodeTimeline(
        MediaCompositionExportRequest request,
        DawnGpuContext dawn,
        IReadOnlyList<LinuxMediaColorOverlayPlan>
            overlays,
        IsoBmffCompositionTrack? audioTrack,
        string spoolPath,
        string temporaryPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var encodedSpool =
            new IsoBmffH264AccessUnitSpool(
                spoolPath,
                request.EncodingProfile.Width,
                request.EncodingProfile.Height,
                request.EncodingProfile
                    .FrameRateNumerator,
                request.EncodingProfile
                    .FrameRateDenominator);
        using V4l2StatefulVideoEncoder encoder =
            CreateEncoder(
                planeCount: 2,
                request);
        if (!LinuxWebGpuNv12EncoderFrameSink
                .TryCreate(
                    dawn,
                    request.EncodingProfile.Width,
                    request.EncodingProfile.Height,
                    out LinuxWebGpuNv12EncoderFrameSink
                        frameSink))
        {
            throw new NotSupportedException(
                "No GBM allocation is renderable by the active Dawn/Vulkan adapter and importable by V4L2.");
        }

        using (frameSink)
        {
            TimeSpan timelineOffset =
                TimeSpan.Zero;
            for (int clipIndex = 0;
                 clipIndex < request.Clips.Count;
                 clipIndex++)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
                MediaCompositionExportClip clip =
                    request.Clips[clipIndex];
                if (!TryGetVideoEffectPlan(
                        clip,
                        _effects,
                        out LinuxGpuVideoEffectPlan
                            effectPlan))
                {
                    throw new InvalidDataException(
                        "The WebGPU effect graph is invalid.");
                }

                TimeSpan clipDuration =
                    GetTrimmedDuration(clip);
                if (clip.ArgbColor is
                        uint argbColor)
                {
                    ProcessTimelineColorClip(
                        request,
                        argbColor,
                        effectPlan,
                        timelineOffset,
                        clipDuration,
                        clipIndex,
                        encoder,
                        frameSink,
                        overlays,
                        encodedSpool,
                        progress,
                        cancellationToken);
                }
                else
                {
                    ProcessTimelineUriClip(
                        request,
                        clip,
                        effectPlan,
                        timelineOffset,
                        clipIndex,
                        encoder,
                        frameSink,
                        overlays,
                        encodedSpool,
                        progress,
                        cancellationToken);
                }

                timelineOffset =
                    timelineOffset +
                    clipDuration;
            }

            DrainTimelineEncoder(
                encoder,
                encodedSpool,
                cancellationToken);
        }

        IsoBmffCompositionPlan plan =
            CreateCompositionPlan(
                encodedSpool,
                audioTrack);
        IsoBmffCompositionWriter.WriteAsync(
                plan,
                temporaryPath,
                progress: null,
                cancellationToken)
            .GetAwaiter()
            .GetResult();
        progress?.Report(99d);
    }

    private static void ProcessTimelineColorClip(
        MediaCompositionExportRequest request,
        uint argbColor,
        LinuxGpuVideoEffectPlan effectPlan,
        TimeSpan timelineOffset,
        TimeSpan duration,
        int clipIndex,
        V4l2StatefulVideoEncoder encoder,
        LinuxWebGpuNv12EncoderFrameSink frameSink,
        IReadOnlyList<LinuxMediaColorOverlayPlan>
            overlays,
        IsoBmffH264AccessUnitSpool encodedSpool,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        long frameOffset = 0;
        ulong frameRemainder = 0;
        while (frameOffset < duration.Ticks)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            TimeSpan presentationTime =
                TimeSpan.FromTicks(
                    checked(
                        timelineOffset.Ticks +
                        frameOffset));
            if (frameSink.TryProcessColorFrame(
                    argbColor,
                    presentationTime,
                    effectPlan,
                    overlays,
                    encoder))
            {
                ReportTimelineProgress(
                    progress,
                    clipIndex,
                    request.Clips.Count,
                    frameOffset /
                    (double)Math.Max(
                        1,
                        duration.Ticks));
                frameOffset =
                    GetNextColorFrameTimestamp(
                        frameOffset,
                        ref frameRemainder,
                        request.EncodingProfile
                            .FrameRateNumerator,
                        request.EncodingProfile
                            .FrameRateDenominator);
            }

            PumpTimelineEncoder(
                encoder,
                encodedSpool,
                timeoutMilliseconds: 4);
        }
    }

    private void ProcessTimelineUriClip(
        MediaCompositionExportRequest request,
        MediaCompositionExportClip clip,
        LinuxGpuVideoEffectPlan effectPlan,
        TimeSpan timelineOffset,
        int clipIndex,
        V4l2StatefulVideoEncoder encoder,
        LinuxWebGpuNv12EncoderFrameSink frameSink,
        IReadOnlyList<LinuxMediaColorOverlayPlan>
            overlays,
        IsoBmffH264AccessUnitSpool encodedSpool,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        string sourcePath = Path.GetFullPath(
            clip.SourceUri!.LocalPath);
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.RandomAccess);
        IsoBmffTrack track =
            SelectTrack(
                new IsoBmffDemuxer(source)
                    .Parse());

        TimeSpan sourceDuration =
            FromTrackTime(
                track.Duration,
                track.Timescale);
        TimeSpan trimStart =
            clip.TrimTimeFromStart;
        TimeSpan trimEnd =
            sourceDuration -
            clip.TrimTimeFromEnd;
        if (trimStart < TimeSpan.Zero ||
            trimEnd <= trimStart ||
            trimEnd > sourceDuration)
        {
            throw new InvalidDataException(
                "The precise trim interval is empty or outside the source duration.");
        }

        LinuxVideoDecoderDevice decoderDevice =
            SelectDecoder(track);
        using var reader =
            new IsoBmffNalAccessUnitReader(
                source,
                track);
        using var decoder =
            new V4l2StatefulVideoDecoder(
                decoderDevice.Path,
                track,
                preferNv12Capture: true);
        V4l2DecodedFrame pendingFrame =
            default;
        bool hasPendingFrame = false;
        int queuedFrameCount = 0;
        try
        {
            decoder.Open();
            int sampleIndex =
                FindDecodeStart(
                    track,
                    trimStart);
            bool decoderDraining = false;

            while (!decoder.EndOfStreamReached ||
                   hasPendingFrame)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                while (sampleIndex <
                       track.Samples.Length)
                {
                    ReadOnlySpan<byte> accessUnit =
                        reader.Read(sampleIndex);
                    if (!decoder.TryQueueAccessUnit(
                            accessUnit,
                            PresentationTime(
                                track,
                                sampleIndex)))
                    {
                        break;
                    }
                    sampleIndex++;
                }

                V4l2DecoderPumpResult pump =
                    decoder.Pump(
                        timeoutMilliseconds: 4);
                if (pump ==
                    V4l2DecoderPumpResult
                        .SourceChanged)
                {
                    if (decoder.IsCaptureConfigured)
                    {
                        throw new NotSupportedException(
                            "Dynamic source-size changes are not supported by the current precise exporter.");
                    }
                    decoder.ConfigureCapture();
                    if (decoder.DecodedPixelFormat !=
                            V4l2DecodedPixelFormat.Nv12 ||
                        decoder.CaptureWidth !=
                            track.Width ||
                        decoder.CaptureHeight !=
                            track.Height)
                    {
                        throw new NotSupportedException(
                            "The V4L2 decoder did not expose source-size linear NV12 DMA-BUF output.");
                    }
                }

                if (!hasPendingFrame &&
                    decoder.TryDequeueFrame(
                        out pendingFrame))
                {
                    hasPendingFrame = true;
                }

                if (hasPendingFrame &&
                    (pendingFrame.PresentationTime <
                         trimStart ||
                     pendingFrame.PresentationTime >=
                         trimEnd))
                {
                    pendingFrame.Owner.Dispose();
                    pendingFrame = default;
                    hasPendingFrame = false;
                }

                if (hasPendingFrame)
                {
                    TimeSpan outputTime =
                        GetTimelinePresentationTime(
                            timelineOffset,
                            pendingFrame
                                .PresentationTime,
                            trimStart);
                    try
                    {
                        if (frameSink.TryProcessFrame(
                                in pendingFrame,
                                outputTime,
                                effectPlan,
                                overlays,
                                encoder))
                        {
                            pendingFrame = default;
                            hasPendingFrame = false;
                            queuedFrameCount++;
                        }
                    }
                    catch
                    {
                        // The sink takes ownership once it dequeues a
                        // target slot.
                        pendingFrame = default;
                        hasPendingFrame = false;
                        throw;
                    }
                }

                PumpTimelineEncoder(
                    encoder,
                    encodedSpool,
                    timeoutMilliseconds: 0);

                if (sampleIndex ==
                        track.Samples.Length &&
                    decoder.IsCaptureConfigured &&
                    !decoder.HasQueuedOutput &&
                    !decoderDraining)
                {
                    decoder.BeginDrain();
                    decoderDraining = true;
                }

                ReportTimelineProgress(
                    progress,
                    clipIndex,
                    request.Clips.Count,
                    track.Samples.Length == 0
                        ? 0d
                        : sampleIndex /
                          (double)track
                              .Samples.Length);
            }
        }
        finally
        {
            if (hasPendingFrame)
            {
                pendingFrame.Owner.Dispose();
            }
        }

        if (queuedFrameCount == 0)
        {
            throw new InvalidDataException(
                "The exact trim interval contains no decoded video frame.");
        }
    }

    private static void DrainTimelineEncoder(
        V4l2StatefulVideoEncoder encoder,
        IsoBmffH264AccessUnitSpool encodedSpool,
        CancellationToken cancellationToken)
    {
        while (encoder.HasQueuedInput)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            PumpTimelineEncoder(
                encoder,
                encodedSpool,
                timeoutMilliseconds: 4);
        }
        encoder.BeginDrain();
        while (!encoder.EndOfStreamReached)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            PumpTimelineEncoder(
                encoder,
                encodedSpool,
                timeoutMilliseconds: 4);
        }
    }

    private static void PumpTimelineEncoder(
        V4l2StatefulVideoEncoder encoder,
        IsoBmffH264AccessUnitSpool encodedSpool,
        int timeoutMilliseconds)
    {
        encoder.Pump(timeoutMilliseconds);
        while (encoder.TryDequeueAccessUnit(
                   out V4l2EncodedAccessUnit
                       accessUnit))
        {
            using (accessUnit)
            {
                encodedSpool.Append(
                    accessUnit.Data,
                    accessUnit.PresentationTime,
                    accessUnit.IsKeyFrame);
            }
        }
    }

    internal static TimeSpan
        GetTimelinePresentationTime(
            TimeSpan timelineOffset,
            TimeSpan sourcePresentationTime,
            TimeSpan trimStart)
    {
        if (timelineOffset < TimeSpan.Zero ||
            sourcePresentationTime <
                TimeSpan.Zero ||
            trimStart < TimeSpan.Zero ||
            sourcePresentationTime <
                trimStart)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourcePresentationTime));
        }
        return TimeSpan.FromTicks(
            checked(
                timelineOffset.Ticks +
                sourcePresentationTime.Ticks -
                trimStart.Ticks));
    }

    private static TimeSpan GetTrimmedDuration(
        MediaCompositionExportClip clip) =>
        TimeSpan.FromTicks(
            checked(
                clip.OriginalDuration.Ticks -
                clip.TrimTimeFromStart.Ticks -
                clip.TrimTimeFromEnd.Ticks));

    private static void ReportTimelineProgress(
        IProgress<double>? progress,
        int clipIndex,
        int clipCount,
        double clipProgress)
    {
        progress?.Report(
            Math.Min(
                85d,
                Math.Max(
                    0d,
                    (clipIndex +
                     Math.Clamp(
                         clipProgress,
                         0d,
                         1d)) *
                    85d /
                    clipCount)));
    }

    internal static long GetNextColorFrameTimestamp(
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
            (ulong)TimeSpan.TicksPerSecond *
            frameRateDenominator;
        ulong wholeTicks =
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
            checked(wholeTicks + carry);
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

    private V4l2StatefulVideoEncoder
        CreateEncoder(
            uint planeCount,
            MediaCompositionExportRequest request)
    {
        LinuxRawVideoFormat required =
            planeCount switch
            {
                1 => LinuxRawVideoFormat.Nv12,
                2 => LinuxRawVideoFormat
                    .Nv12MultiPlanar,
                _ => throw new NotSupportedException(
                    "The V4L2 encoder supports only one- or two-plane NV12 DMA-BUF input.")
            };
        foreach (LinuxVideoEncoderDevice device in
                 _capabilities.VideoEncoders)
        {
            if (device.UsesMultiPlanarQueues &&
                device.SupportsStreaming &&
                device.SupportsDmaBufInput &&
                (device.Codecs &
                 LinuxHardwareVideoCodec.H264) != 0 &&
                (device.InputFormats &
                 required) != 0)
            {
                var encoder =
                    new V4l2StatefulVideoEncoder(
                        device.Path,
                        request.EncodingProfile.Width,
                        request.EncodingProfile.Height,
                        request.EncodingProfile
                            .VideoBitrate,
                        request.EncodingProfile
                            .FrameRateNumerator,
                        request.EncodingProfile
                            .FrameRateDenominator,
                        multiPlaneInput:
                            planeCount == 2);
                try
                {
                    encoder.Open();
                    return encoder;
                }
                catch
                {
                    encoder.Dispose();
                    throw;
                }
            }
        }
        throw new NotSupportedException(
            $"No streaming V4L2 H.264 encoder imports {required} DMA-BUF frames.");
    }

    internal LinuxVideoDecoderDevice SelectDecoder(
        IsoBmffTrack track)
    {
        LinuxHardwareVideoCodec required =
            track.Codec == IsoBmffCodec.H264
                ? LinuxHardwareVideoCodec.H264
                : LinuxHardwareVideoCodec.H265;
        foreach (LinuxVideoDecoderDevice device in
                 _capabilities.VideoDecoders)
        {
            if (device.UsesMultiPlanarQueues &&
                device.SupportsStreaming &&
                (device.Codecs & required) != 0)
            {
                return device;
            }
        }
        throw new NotSupportedException(
            $"No streaming multi-planar V4L2 decoder exposes {required}.");
    }

    private bool HasNativeH264Path()
    {
        bool decoder = false;
        foreach (LinuxVideoDecoderDevice device in
                 _capabilities.VideoDecoders)
        {
            if (device.UsesMultiPlanarQueues &&
                device.SupportsStreaming &&
                (device.Codecs &
                 LinuxHardwareVideoCodec.H264) != 0)
            {
                decoder = true;
                break;
            }
        }
        if (!decoder)
        {
            return false;
        }

        foreach (LinuxVideoEncoderDevice device in
                 _capabilities.VideoEncoders)
        {
            if (device.UsesMultiPlanarQueues &&
                device.SupportsStreaming &&
                device.SupportsDmaBufInput &&
                (device.Codecs &
                 LinuxHardwareVideoCodec.H264) != 0 &&
                (device.InputFormats &
                 (LinuxRawVideoFormat.Nv12 |
                  LinuxRawVideoFormat
                      .Nv12MultiPlanar)) != 0)
            {
                return true;
            }
        }
        return false;
    }

    private bool HasNativeH264EncoderPath()
    {
        foreach (LinuxVideoEncoderDevice device in
                 _capabilities.VideoEncoders)
        {
            if (device.UsesMultiPlanarQueues &&
                device.SupportsStreaming &&
                device.SupportsDmaBufInput &&
                (device.Codecs &
                 LinuxHardwareVideoCodec.H264) != 0 &&
                (device.InputFormats &
                 LinuxRawVideoFormat
                     .Nv12MultiPlanar) != 0)
            {
                return true;
            }
        }
        return false;
    }

    internal static IsoBmffTrack SelectTrack(
        IsoBmffMovie movie) =>
        movie.Tracks.FirstOrDefault(
            static track =>
                track.Kind ==
                    IsoBmffTrackKind.Video &&
                track.Codec ==
                    IsoBmffCodec.H264 &&
                track.Samples.Length != 0) ??
        throw new NotSupportedException(
            "The precise Linux exporter requires an H.264 ISO-BMFF video track.");

    private static bool RequiresScaling(
        MediaCompositionExportRequest request,
        IsoBmffTrack track) =>
        track.Width !=
            request.EncodingProfile.Width ||
        track.Height !=
            request.EncodingProfile.Height;

    private static IsoBmffCompositionPlan
        CreateCompositionPlan(
            IsoBmffH264AccessUnitSpool videoSpool,
            IsoBmffCompositionTrack? audioTrack)
    {
        IsoBmffCompositionPlan plan =
            videoSpool.CreatePlan();
        if (audioTrack is null)
        {
            return plan;
        }
        return plan with
        {
            Audio = audioTrack
        };
    }

    internal static int FindDecodeStart(
        IsoBmffTrack track,
        TimeSpan trimStart)
    {
        long target = ToTrackTime(
            trimStart,
            track.Timescale);
        int selected = -1;
        long selectedPresentation = long.MinValue;
        for (int index = 0;
             index < track.Samples.Length;
             index++)
        {
            IsoBmffSample sample =
                track.Samples[index];
            if (sample.IsSync &&
                sample.PresentationTime <= target &&
                sample.PresentationTime >=
                    selectedPresentation)
            {
                selected = index;
                selectedPresentation =
                    sample.PresentationTime;
            }
        }
        if (selected < 0)
        {
            throw new InvalidDataException(
                "No sync sample precedes the exact trim start.");
        }
        return selected;
    }

    internal static TimeSpan PresentationTime(
        IsoBmffTrack track,
        int sampleIndex) =>
        FromTrackTime(
            track.Samples[sampleIndex]
                .PresentationTime,
            track.Timescale);

    private static TimeSpan FromTrackTime(
        long value,
        uint timescale) =>
        TimeSpan.FromTicks(
            checked(
                (long)Math.Round(
                    value *
                    ((double)TimeSpan
                        .TicksPerSecond /
                     timescale),
                    MidpointRounding
                        .AwayFromZero)));

    private static long ToTrackTime(
        TimeSpan value,
        uint timescale) =>
        checked(
            (long)Math.Round(
                value.Ticks *
                (double)timescale /
                TimeSpan.TicksPerSecond,
                MidpointRounding.AwayFromZero));

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
        out LinuxGpuVideoEffectPlan plan)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(effects);
        plan = LinuxGpuVideoEffectPlan.Identity;
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

        MediaVideoColorTransform combined =
            MediaVideoColorEffectFactory
                .CreateTransform(
                    saturation: saturation,
                    grayscale: grayscale)
                .Then(declared.ColorTransform);
        plan = new LinuxGpuVideoEffectPlan(
            new GpuTextureColorTransform(
                combined.Red,
                combined.Green,
                combined.Blue),
            declared.BlurStandardDeviation);
        return true;
    }

    private static bool RequiresGpuComposition(
        MediaCompositionExportRequest request,
        MediaEffectRegistry effects)
    {
        if (request.Clips.Count > 1 ||
            request.OverlayLayers.Count != 0)
        {
            return true;
        }
        for (int index = 0;
             index < request.Clips.Count;
             index++)
        {
            MediaCompositionExportClip clip =
                request.Clips[index];
            if (clip.ArgbColor.HasValue)
            {
                return true;
            }
            if (clip.SourceUri is not null &&
                clip.SourceVideoWidth != 0 &&
                clip.SourceVideoHeight != 0 &&
                (clip.SourceVideoWidth !=
                     request.EncodingProfile.Width ||
                 clip.SourceVideoHeight !=
                     request.EncodingProfile.Height))
            {
                return true;
            }
            if (TryGetVideoEffectPlan(
                    clip,
                    effects,
                    out LinuxGpuVideoEffectPlan
                        plan) &&
                !plan.IsIdentity)
            {
                return true;
            }
        }
        return false;
    }

    internal static bool TryGetActiveVulkanDawnContext(
        out DawnGpuContext? dawn)
    {
        IReadOnlyList<WgpuContext> contexts =
            WgpuContext.ActiveContexts;
        for (int index = 0;
             index < contexts.Count;
             index++)
        {
            if (contexts[index].ExternalTextureImporter is
                    DawnGpuContext candidate &&
                contexts[index].AdapterBackendType ==
                    BackendType.Vulkan)
            {
                dawn = candidate;
                return true;
            }
        }
        dawn = null;
        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Cleanup is best-effort and must not hide the export result.
        }
    }
}
