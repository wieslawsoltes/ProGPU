using AVFoundation;
using CoreGraphics;
using CoreImage;
using CoreMedia;
using CoreVideo;
using Foundation;
using ImageIO;
using Metal;
using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;

namespace ProGPU.Apple.Media;

/// <summary>
/// Native AVFoundation timeline exporter. AVMutableComposition retains source
/// samples and AVAssetExportSession performs hardware-backed H.264/AAC encode
/// when the selected Apple device exposes the corresponding codec.
/// </summary>
public sealed class AppleMediaCompositionExportProvider :
    IMediaCompositionExportProvider,
    IMediaCompositionExportCapabilityProvider,
    IMediaCompositionThumbnailProvider
{
    private const int MediaTimeScale = 600;
    private const string ProviderId =
        "progpu.apple.avfoundation.export";
    private readonly MediaEffectRegistry _effects;

    public AppleMediaCompositionExportProvider(
        int priority = 100,
        MediaEffectRegistry? effects = null)
    {
        Priority = priority;
        _effects = effects ?? MediaEffectRegistry.Default;
    }

    public string Id => ProviderId;
    public int Priority { get; }

    public bool CanRender(MediaCompositionExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((!OperatingSystem.IsIOS() &&
             !OperatingSystem.IsMacOS()) ||
            request.Clips.Count == 0 ||
            !string.Equals(
                request.EncodingProfile.ContainerSubtype,
                "MPEG4",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.EncodingProfile.VideoSubtype,
                "H264",
                StringComparison.OrdinalIgnoreCase) ||
            request.EncodingProfile.AudioSubtype is not null &&
            !string.Equals(
                request.EncodingProfile.AudioSubtype,
                "AAC",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int index = 0; index < request.Clips.Count; index++)
        {
            MediaCompositionExportClip clip =
                request.Clips[index];
            if (!HasExactlyOneVideoSource(clip) ||
                !double.IsFinite(clip.Volume) ||
                clip.Volume is < 0d or > 1d ||
                !AreAudioEffectsRegistered(
                    clip.AudioEffectDefinitions) ||
                !TryGetClipEffects(
                    clip,
                    out _))
            {
                return false;
            }
        }
        for (int index = 0;
             index < request.BackgroundAudioTracks.Count;
             index++)
        {
            MediaCompositionExportAudioTrack track =
                request.BackgroundAudioTracks[index];
            if (!track.SourceUri.IsAbsoluteUri ||
                !double.IsFinite(track.Volume) ||
                track.Volume is < 0d or > 1d ||
                !AreAudioEffectsRegistered(
                    track.AudioEffectDefinitions))
            {
                return false;
            }
        }
        for (int layerIndex = 0;
             layerIndex < request.OverlayLayers.Count;
             layerIndex++)
        {
            MediaCompositionExportOverlayLayer layer =
                request.OverlayLayers[layerIndex];
            if (layer.CustomCompositorDefinition is not null)
            {
                return false;
            }
            for (int overlayIndex = 0;
                 overlayIndex < layer.Overlays.Count;
                 overlayIndex++)
            {
                MediaCompositionExportOverlay overlay =
                    layer.Overlays[overlayIndex];
                MediaCompositionExportClip clip =
                    overlay.Clip;
                if (!HasExactlyOneVideoSource(clip) ||
                    !double.IsFinite(clip.Volume) ||
                    clip.Volume is < 0d or > 1d ||
                    !AreAudioEffectsRegistered(
                        clip.AudioEffectDefinitions) ||
                    !TryGetClipEffects(
                        clip,
                        out _) ||
                    overlay.Delay < TimeSpan.Zero ||
                    !double.IsFinite(overlay.PositionX) ||
                    !double.IsFinite(overlay.PositionY) ||
                    !double.IsFinite(
                        overlay.PositionWidth) ||
                    !double.IsFinite(
                        overlay.PositionHeight) ||
                    overlay.PositionWidth <= 0d ||
                    overlay.PositionHeight <= 0d ||
                    !double.IsFinite(overlay.Opacity) ||
                    overlay.Opacity is < 0d or > 1d)
                {
                    return false;
                }
            }
        }
        return true;
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

        bool effectsBakedOnGpu =
            CountEffectClips(request) != 0;
        bool hasGpuPreparation =
            CountPreparedClips(request) != 0;
        return new MediaCompositionExportCapabilities(
            ProviderId,
            hasGpuPreparation
                ? MediaCompositionExportVideoPath
                    .NativeGpuSurface
                : MediaCompositionExportVideoPath.Unknown,
            request.EncodingProfile.AudioSubtype is null
                ? MediaCompositionExportAudioPath.None
                : MediaCompositionExportAudioPath.NativeBuffer,
            HardwareVideoEncoderRequested: false,
            HardwareVideoEncoderGuaranteed: false,
            EffectsBakedOnGpu: effectsBakedOnGpu,
            Limitation: hasGpuPreparation
                ? "Generated colors and registered affine color effects " +
                  "are rendered by Core Image on the native Metal device " +
                  "into the AVAssetWriter pixel-buffer pool. AVFoundation " +
                  "owns codec selection and does not guarantee hardware " +
                  "encode."
                : "AVAssetExportSession owns the native video path and " +
                  "codec selection; ProGPU does not observe enough of " +
                  "that path to label it GPU-surface or compressed-copy.");
    }

    bool IMediaCompositionThumbnailProvider.CanRender(
        MediaCompositionThumbnailRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        MediaCompositionExportRequest composition =
            request.Composition;
        if ((!OperatingSystem.IsIOS() &&
             !OperatingSystem.IsMacOS()) ||
            request.Positions.Count == 0 ||
            !Enum.IsDefined(request.Precision) ||
            request.PixelWidth == 0 ||
            request.PixelHeight == 0 ||
            composition.Clips.Count == 0 ||
            composition.EncodingProfile.Width !=
                request.PixelWidth ||
            composition.EncodingProfile.Height !=
                request.PixelHeight ||
            composition.EncodingProfile
                .FrameRateNumerator == 0 ||
            composition.EncodingProfile
                .FrameRateDenominator == 0)
        {
            return false;
        }

        TimeSpan duration = TimeSpan.Zero;
        for (int index = 0;
             index < composition.Clips.Count;
             index++)
        {
            MediaCompositionExportClip clip =
                composition.Clips[index];
            TimeSpan clipDuration =
                clip.OriginalDuration -
                clip.TrimTimeFromStart -
                clip.TrimTimeFromEnd;
            if (!HasExactlyOneVideoSource(clip) ||
                clipDuration <= TimeSpan.Zero ||
                !TryGetClipEffects(
                    clip,
                    out _))
            {
                return false;
            }
            duration += clipDuration;
        }

        for (int layerIndex = 0;
             layerIndex < composition.OverlayLayers.Count;
             layerIndex++)
        {
            MediaCompositionExportOverlayLayer layer =
                composition.OverlayLayers[layerIndex];
            if (layer.CustomCompositorDefinition is not null)
            {
                return false;
            }
            for (int overlayIndex = 0;
                 overlayIndex < layer.Overlays.Count;
                 overlayIndex++)
            {
                MediaCompositionExportOverlay overlay =
                    layer.Overlays[overlayIndex];
                MediaCompositionExportClip clip =
                    overlay.Clip;
                if (!HasExactlyOneVideoSource(clip) ||
                    !TryGetClipEffects(
                        clip,
                        out _) ||
                    overlay.Delay < TimeSpan.Zero ||
                    !double.IsFinite(overlay.PositionX) ||
                    !double.IsFinite(overlay.PositionY) ||
                    !double.IsFinite(
                        overlay.PositionWidth) ||
                    !double.IsFinite(
                        overlay.PositionHeight) ||
                    overlay.PositionWidth <= 0d ||
                    overlay.PositionHeight <= 0d ||
                    !double.IsFinite(overlay.Opacity) ||
                    overlay.Opacity is < 0d or > 1d)
                {
                    return false;
                }
            }
        }

        for (int index = 0;
             index < request.Positions.Count;
             index++)
        {
            TimeSpan position = request.Positions[index];
            if (position < TimeSpan.Zero ||
                position > duration)
            {
                return false;
            }
        }
        return true;
    }

    async ValueTask<IReadOnlyList<
        MediaCompositionThumbnail>>
        IMediaCompositionThumbnailProvider.RenderAsync(
        MediaCompositionThumbnailRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!((IMediaCompositionThumbnailProvider)this)
                .CanRender(request))
        {
            throw new ArgumentException(
                "The thumbnail request is not supported by this provider.",
                nameof(request));
        }

        MediaCompositionExportRequest composition =
            request.Composition;
        int preparedClipCount =
            CountPreparedClips(composition);
        string? temporaryDirectory = null;
        try
        {
            if (preparedClipCount != 0)
            {
                temporaryDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "ProGPU.Media.Thumbnails",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(
                    temporaryDirectory);
                composition =
                    await PrepareEffectRequestAsync(
                        composition,
                        temporaryDirectory,
                        new EffectPreparationProgress(
                            target: null,
                            preparedClipCount,
                            maximum: 100d),
                        cancellationToken)
                        .ConfigureAwait(false);
            }

            MediaCompositionExportRequest prepared =
                composition;
            return await Task.Run(
                () => RenderThumbnailsCore(
                    request with
                    {
                        Composition = prepared
                    },
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (temporaryDirectory is not null)
            {
                TryDeleteDirectory(
                    temporaryDirectory);
            }
        }
    }

    public async ValueTask<MediaCompositionExportFailure> RenderAsync(
        MediaCompositionExportRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRender(request))
        {
            return MediaCompositionExportFailure.InvalidProfile;
        }

        int preparedClipCount = CountPreparedClips(request);
        if (preparedClipCount == 0)
        {
            return await RenderCoreAsync(
                request,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "ProGPU.Media.Export",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var preparationProgress =
                new EffectPreparationProgress(
                    progress,
                    preparedClipCount,
                    40d);
            MediaCompositionExportRequest prepared =
                await PrepareEffectRequestAsync(
                    request,
                    temporaryDirectory,
                    preparationProgress,
                    cancellationToken)
                    .ConfigureAwait(false);
            return await RenderCoreAsync(
                prepared,
                progress is null
                    ? null
                    : new ScaledProgress(
                        progress,
                        offset: 40d,
                        scale: 0.6d),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable(
                        "PROGPU_MEDIA_EXPORT_THROW"),
                    "1",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Apple media preparation failed.",
                    exception);
            }
            return MediaCompositionExportFailure.Unknown;
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    private async ValueTask<MediaCompositionExportFailure>
        RenderCoreAsync(
        MediaCompositionExportRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRender(request) ||
            request.EncodingProfile.Width == 0 ||
            request.EncodingProfile.Height == 0 ||
            request.EncodingProfile.FrameRateNumerator == 0 ||
            request.EncodingProfile.FrameRateDenominator == 0)
        {
            return MediaCompositionExportFailure.InvalidProfile;
        }

        using var composition = new AVMutableComposition();
        var volumeSchedule =
            new List<AppleMainAudioSegment>(
                request.Clips.Count);
        CMTime insertionTime = CMTime.Zero;
        for (int index = 0; index < request.Clips.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaCompositionExportClip clip =
                request.Clips[index];
            using NSUrl sourceUrl =
                CreateUrl(clip.SourceUri!);
            using var asset = new AVUrlAsset(sourceUrl);

            TimeSpan trimmedDuration =
                clip.OriginalDuration -
                clip.TrimTimeFromStart -
                clip.TrimTimeFromEnd;
            if (trimmedDuration <= TimeSpan.Zero)
            {
                continue;
            }

            CMTime sourceStart = ToMediaTime(
                clip.TrimTimeFromStart);
            CMTime sourceDuration = ToMediaTime(
                trimmedDuration);
            volumeSchedule.Add(
                new AppleMainAudioSegment(
                    insertionTime,
                    sourceDuration,
                    (float)clip.Volume,
                    clip.AudioEffectDefinitions));
            var sourceRange = new CMTimeRange
            {
                Start = sourceStart,
                Duration = sourceDuration
            };
            if (!composition.Insert(
                    sourceRange,
                    asset,
                    insertionTime,
                    out NSError? error))
            {
                return error is null
                    ? MediaCompositionExportFailure.Unknown
                    : MediaCompositionExportFailure.CodecNotFound;
            }
            insertionTime =
                CMTime.Add(insertionTime, sourceDuration);
        }

        if (insertionTime == CMTime.Zero)
        {
            return MediaCompositionExportFailure.InvalidProfile;
        }

        AVAssetTrack[] mainAudioTracks =
            composition.GetTracks(AVMediaTypes.Audio);
        AVAssetTrack[] mainVideoTracks =
            composition.GetTracks(AVMediaTypes.Video);
        var backgroundAudioMix =
            new List<AppleAuxiliaryAudioTrack>(
                    request.BackgroundAudioTracks.Count);
        var overlayVideoTracks =
            new List<AppleOverlayVideoTrack>();
        if (request.EncodingProfile.AudioSubtype is null)
        {
            for (int index = 0;
                 index < mainAudioTracks.Length;
                 index++)
            {
                if (mainAudioTracks[index] is
                    AVCompositionTrack compositionTrack)
                {
                    composition.RemoveTrack(compositionTrack);
                }
            }
        }
        else
        {
            MediaCompositionExportFailure backgroundFailure =
                AddBackgroundAudioTracks(
                    composition,
                    request.BackgroundAudioTracks,
                    backgroundAudioMix,
                    cancellationToken);
            if (backgroundFailure !=
                MediaCompositionExportFailure.None)
            {
                return backgroundFailure;
            }
        }
        MediaCompositionExportFailure overlayFailure =
            AddOverlayTracks(
                composition,
                request.OverlayLayers,
                overlayVideoTracks,
                backgroundAudioMix,
                request.EncodingProfile.AudioSubtype is
                    not null,
                cancellationToken);
        if (overlayFailure !=
            MediaCompositionExportFailure.None)
        {
            return overlayFailure;
        }

        AVAssetExportSessionPreset preset =
            SelectPreset(
                request.EncodingProfile.Width,
                request.EncodingProfile.Height);
        using var export =
            new AVAssetExportSession(composition, preset);
        string mpeg4FileType =
            AVFileTypes.Mpeg4.GetConstant()?.ToString() ??
            "public.mpeg-4";
        if (!export.SupportedFileTypes.Contains(
                mpeg4FileType,
                StringComparer.Ordinal))
        {
            return MediaCompositionExportFailure.CodecNotFound;
        }

        string destinationPath =
            Path.GetFullPath(request.DestinationPath);
        string? destinationDirectory =
            Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(destinationDirectory))
        {
            return MediaCompositionExportFailure.InvalidProfile;
        }
        Directory.CreateDirectory(destinationDirectory);
        string temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}." +
            $"{Guid.NewGuid():N}.tmp");
        using NSUrl destinationUrl =
            NSUrl.FromFilename(temporaryPath);
        export.OutputUrl = destinationUrl;
        export.OutputFileType = mpeg4FileType;
        export.ShouldOptimizeForNetworkUse = true;

        AVMutableVideoComposition? videoComposition = null;
        AVMutableVideoCompositionInstruction?
            videoInstruction = null;
        AVMutableVideoCompositionLayerInstruction[]
            videoLayerInstructions = [];
        AVMutableAudioMix? audioMix = null;
        AVMutableAudioMixInputParameters[] audioParameters = [];
        AppleExportAudioEffectGraph? audioEffectGraph = null;
        try
        {
            if (overlayVideoTracks.Count != 0)
            {
                (videoComposition,
                 videoInstruction,
                 videoLayerInstructions) =
                    CreateVideoComposition(
                        composition,
                        mainVideoTracks,
                        overlayVideoTracks,
                        request.EncodingProfile);
                export.VideoComposition =
                    videoComposition;
            }

            int parameterCount =
                request.EncodingProfile.AudioSubtype is null
                    ? 0
                    : mainAudioTracks.Length +
                      backgroundAudioMix.Count;
            if (parameterCount != 0)
            {
                audioParameters =
                    new AVMutableAudioMixInputParameters[
                        parameterCount];
                for (int trackIndex = 0;
                     trackIndex < mainAudioTracks.Length;
                     trackIndex++)
                {
                    AVMutableAudioMixInputParameters parameters =
                        AVMutableAudioMixInputParameters.FromTrack(
                            mainAudioTracks[trackIndex]);
                    for (int volumeIndex = 0;
                         volumeIndex < volumeSchedule.Count;
                         volumeIndex++)
                    {
                        AppleMainAudioSegment segment =
                            volumeSchedule[volumeIndex];
                        parameters.SetVolume(
                            segment.Volume,
                            segment.Start);
                    }
                    audioParameters[trackIndex] = parameters;
                }
                for (int backgroundIndex = 0;
                     backgroundIndex < backgroundAudioMix.Count;
                     backgroundIndex++)
                {
                    AppleAuxiliaryAudioTrack track =
                        backgroundAudioMix[backgroundIndex];
                    AVMutableAudioMixInputParameters parameters =
                        AVMutableAudioMixInputParameters.FromTrack(
                            track.Track);
                    parameters.SetVolume(
                        track.Volume,
                        track.Start);
                    audioParameters[
                        mainAudioTracks.Length +
                        backgroundIndex] = parameters;
                }
                var effectTracks =
                    new AppleExportAudioEffectTrack[
                        parameterCount];
                var mainEffectSegments =
                    new AppleExportAudioEffectSegment[
                        volumeSchedule.Count];
                for (int segmentIndex = 0;
                     segmentIndex < volumeSchedule.Count;
                     segmentIndex++)
                {
                    AppleMainAudioSegment segment =
                        volumeSchedule[segmentIndex];
                    mainEffectSegments[segmentIndex] =
                        new AppleExportAudioEffectSegment(
                            ToTimeSpan(segment.Start),
                            ToTimeSpan(segment.Duration),
                            segment.EffectDefinitions);
                }
                for (int trackIndex = 0;
                     trackIndex < mainAudioTracks.Length;
                     trackIndex++)
                {
                    effectTracks[trackIndex] =
                        new AppleExportAudioEffectTrack(
                            audioParameters[trackIndex],
                            mainEffectSegments);
                }
                for (int backgroundIndex = 0;
                     backgroundIndex <
                        backgroundAudioMix.Count;
                     backgroundIndex++)
                {
                    AppleAuxiliaryAudioTrack track =
                        backgroundAudioMix[
                            backgroundIndex];
                    effectTracks[
                        mainAudioTracks.Length +
                        backgroundIndex] =
                        new AppleExportAudioEffectTrack(
                            audioParameters[
                                mainAudioTracks.Length +
                                backgroundIndex],
                            [
                                new AppleExportAudioEffectSegment(
                                    ToTimeSpan(track.Start),
                                    ToTimeSpan(track.Duration),
                                    track.EffectDefinitions)
                            ]);
                }
                audioEffectGraph =
                    new AppleExportAudioEffectGraph(
                        _effects,
                        effectTracks);
                audioMix = AVMutableAudioMix.Create();
                audioMix.InputParameters = audioParameters;
                export.AudioMix = audioMix;
            }

            using CancellationTokenRegistration registration =
                cancellationToken.Register(export.CancelExport);
            Task exportTask = export.ExportTaskAsync();
            while (!exportTask.IsCompleted)
            {
                progress?.Report(export.Progress * 100d);
                await Task.WhenAny(
                    exportTask,
                    Task.Delay(100))
                    .ConfigureAwait(false);
            }
            await exportTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100d);

            if (export.Status ==
                AVAssetExportSessionStatus.Completed)
            {
                if (audioEffectGraph?
                        .HasUnsupportedFormat == true)
                {
                    return MediaCompositionExportFailure
                        .InvalidProfile;
                }
                File.Move(
                    temporaryPath,
                    destinationPath,
                    overwrite: true);
                return MediaCompositionExportFailure.None;
            }
            if (export.Status ==
                AVAssetExportSessionStatus.Cancelled)
            {
                throw new OperationCanceledException(
                    cancellationToken);
            }
            return MediaCompositionExportFailure.Unknown;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return MediaCompositionExportFailure.Unknown;
        }
        finally
        {
            TryDelete(temporaryPath);
            videoComposition?.Dispose();
            videoInstruction?.Dispose();
            for (int index = 0;
                 index < videoLayerInstructions.Length;
                 index++)
            {
                videoLayerInstructions[index]?.Dispose();
            }
            audioEffectGraph?.Dispose();
            audioMix?.Dispose();
            for (int index = 0;
                 index < audioParameters.Length;
                 index++)
            {
                audioParameters[index]?.Dispose();
            }
        }
    }

    private static IReadOnlyList<
        MediaCompositionThumbnail> RenderThumbnailsCore(
        MediaCompositionThumbnailRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MediaCompositionExportRequest source =
            request.Composition;
        using var composition = new AVMutableComposition();
        CMTime insertionTime = CMTime.Zero;
        for (int index = 0;
             index < source.Clips.Count;
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaCompositionExportClip clip =
                source.Clips[index];
            TimeSpan duration =
                clip.OriginalDuration -
                clip.TrimTimeFromStart -
                clip.TrimTimeFromEnd;
            if (duration <= TimeSpan.Zero)
            {
                continue;
            }

            using NSUrl sourceUrl =
                CreateUrl(clip.SourceUri!);
            using var asset = new AVUrlAsset(sourceUrl);
            var range = new CMTimeRange
            {
                Start = ToMediaTime(
                    clip.TrimTimeFromStart),
                Duration = ToMediaTime(duration)
            };
            if (!composition.Insert(
                    range,
                    asset,
                    insertionTime,
                    out NSError? insertionError))
            {
                string message =
                    insertionError?
                        .LocalizedDescription ??
                    "AVFoundation could not insert a thumbnail source.";
                insertionError?.Dispose();
                throw new InvalidOperationException(message);
            }
            insertionError?.Dispose();
            insertionTime =
                CMTime.Add(
                    insertionTime,
                    range.Duration);
        }

        AVAssetTrack[] mainVideoTracks =
            composition.GetTracks(AVMediaTypes.Video);
        if (mainVideoTracks.Length == 0)
        {
            throw new InvalidOperationException(
                "The composition contains no native video track.");
        }

        var overlayVideoTracks =
            new List<AppleOverlayVideoTrack>();
        var unusedAudioTracks =
            new List<AppleAuxiliaryAudioTrack>();
        MediaCompositionExportFailure overlayFailure =
            AddOverlayTracks(
                composition,
                source.OverlayLayers,
                overlayVideoTracks,
                unusedAudioTracks,
                includeAudio: false,
                cancellationToken);
        if (overlayFailure !=
            MediaCompositionExportFailure.None)
        {
            throw new InvalidOperationException(
                "AVFoundation could not compose the thumbnail overlays.");
        }

        AVMutableVideoComposition? videoComposition = null;
        AVMutableVideoCompositionInstruction?
            videoInstruction = null;
        AVMutableVideoCompositionLayerInstruction[]
            videoLayerInstructions = [];
        try
        {
            (videoComposition,
             videoInstruction,
             videoLayerInstructions) =
                CreateVideoComposition(
                    composition,
                    mainVideoTracks,
                    overlayVideoTracks,
                    source.EncodingProfile);
            using var generator =
                new AVAssetImageGenerator(composition)
                {
                    AppliesPreferredTrackTransform = false,
                    MaximumSize = new CGSize(
                        request.PixelWidth,
                        request.PixelHeight),
                    VideoComposition = videoComposition
                };
            if (request.Precision ==
                MediaCompositionThumbnailPrecision
                    .NearestFrame)
            {
                generator.RequestedTimeToleranceBefore =
                    CMTime.Zero;
                generator.RequestedTimeToleranceAfter =
                    CMTime.Zero;
            }
            else
            {
                generator.RequestedTimeToleranceBefore =
                    CMTime.PositiveInfinity;
                generator.RequestedTimeToleranceAfter =
                    CMTime.PositiveInfinity;
            }

            using CancellationTokenRegistration registration =
                cancellationToken.Register(
                    generator.CancelAllCGImageGeneration);
            var results =
                new MediaCompositionThumbnail[
                    request.Positions.Count];
            TimeSpan duration =
                GetCompositionDuration(source.Clips);
            double frameSeconds =
                (double)source.EncodingProfile
                    .FrameRateDenominator /
                source.EncodingProfile
                    .FrameRateNumerator;
            TimeSpan finalFrameStart =
                duration -
                TimeSpan.FromSeconds(frameSeconds);
            if (finalFrameStart < TimeSpan.Zero)
            {
                finalFrameStart = TimeSpan.Zero;
            }

            for (int index = 0;
                 index < request.Positions.Count;
                 index++)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
                TimeSpan requested =
                    request.Positions[index];
                TimeSpan nativePosition =
                    requested >= duration
                        ? finalFrameStart
                        : requested;
                CGImage? image =
                    generator.CopyCGImageAtTime(
                        ToMediaTime(nativePosition),
                        out _,
                        out NSError? imageError);
                if (image is null)
                {
                    string message =
                        imageError?
                            .LocalizedDescription ??
                        "AVFoundation returned no thumbnail image.";
                    imageError?.Dispose();
                    throw new InvalidOperationException(message);
                }
                using (image)
                {
                    imageError?.Dispose();
                    cancellationToken
                        .ThrowIfCancellationRequested();
                    byte[] encoded = EncodePng(image);
                    results[index] =
                        new MediaCompositionThumbnail(
                            encoded,
                            "image/png",
                            checked((uint)image.Width),
                            checked((uint)image.Height));
                }
            }
            return Array.AsReadOnly(results);
        }
        finally
        {
            videoComposition?.Dispose();
            videoInstruction?.Dispose();
            for (int index = 0;
                 index < videoLayerInstructions.Length;
                 index++)
            {
                videoLayerInstructions[index]?.Dispose();
            }
        }
    }

    private static byte[] EncodePng(CGImage image)
    {
        using var data = new NSMutableData();
        var options =
            new CGImageDestinationOptions();
        using CGImageDestination destination =
            CGImageDestination.Create(
                data,
                "public.png",
                imageCount: 1,
                options) ??
            throw new InvalidOperationException(
                "ImageIO could not create a PNG destination.");
        destination.AddImage(image, options);
        if (!destination.Close())
        {
            throw new InvalidOperationException(
                "ImageIO could not encode the thumbnail.");
        }
        return data.ToArray();
    }

    private static TimeSpan GetCompositionDuration(
        IReadOnlyList<MediaCompositionExportClip> clips)
    {
        TimeSpan duration = TimeSpan.Zero;
        for (int index = 0; index < clips.Count; index++)
        {
            duration +=
                clips[index].OriginalDuration -
                clips[index].TrimTimeFromStart -
                clips[index].TrimTimeFromEnd;
        }
        return duration;
    }

    private int CountEffectClips(
        MediaCompositionExportRequest request)
    {
        int count = 0;
        for (int index = 0;
             index < request.Clips.Count;
             index++)
        {
            if (TryGetClipEffects(
                    request.Clips[index],
                    out AppleBuiltInClipEffects effects) &&
                !effects.IsIdentity)
            {
                count++;
            }
        }
        for (int layerIndex = 0;
             layerIndex < request.OverlayLayers.Count;
             layerIndex++)
        {
            MediaCompositionExportOverlayLayer layer =
                request.OverlayLayers[layerIndex];
            for (int overlayIndex = 0;
                 overlayIndex < layer.Overlays.Count;
                 overlayIndex++)
            {
                if (TryGetClipEffects(
                        layer.Overlays[overlayIndex]
                            .Clip,
                        out AppleBuiltInClipEffects effects) &&
                    !effects.IsIdentity)
                {
                    count++;
                }
            }
        }
        return count;
    }

    private int CountPreparedClips(
        MediaCompositionExportRequest request)
    {
        int count = 0;
        for (int index = 0;
             index < request.Clips.Count;
             index++)
        {
            MediaCompositionExportClip clip =
                request.Clips[index];
            if (clip.ArgbColor is not null ||
                TryGetClipEffects(
                    clip,
                    out AppleBuiltInClipEffects effects) &&
                !effects.IsIdentity)
            {
                count++;
            }
        }
        for (int layerIndex = 0;
             layerIndex < request.OverlayLayers.Count;
             layerIndex++)
        {
            MediaCompositionExportOverlayLayer layer =
                request.OverlayLayers[layerIndex];
            for (int overlayIndex = 0;
                 overlayIndex < layer.Overlays.Count;
                 overlayIndex++)
            {
                MediaCompositionExportClip clip =
                    layer.Overlays[overlayIndex].Clip;
                if (clip.ArgbColor is not null ||
                    TryGetClipEffects(
                        clip,
                        out AppleBuiltInClipEffects effects) &&
                    !effects.IsIdentity)
                {
                    count++;
                }
            }
        }
        return count;
    }

    private async Task<MediaCompositionExportRequest>
        PrepareEffectRequestAsync(
            MediaCompositionExportRequest request,
            string temporaryDirectory,
            EffectPreparationProgress progress,
            CancellationToken cancellationToken)
    {
        var clips =
            new MediaCompositionExportClip[
                request.Clips.Count];
        for (int index = 0;
             index < request.Clips.Count;
             index++)
        {
            clips[index] =
                await PrepareEffectClipAsync(
                    request.Clips[index],
                    request.EncodingProfile,
                    temporaryDirectory,
                    progress,
                    cancellationToken)
                    .ConfigureAwait(false);
        }

        var layers =
            new MediaCompositionExportOverlayLayer[
                request.OverlayLayers.Count];
        for (int layerIndex = 0;
             layerIndex < request.OverlayLayers.Count;
             layerIndex++)
        {
            MediaCompositionExportOverlayLayer layer =
                request.OverlayLayers[layerIndex];
            var overlays =
                new MediaCompositionExportOverlay[
                    layer.Overlays.Count];
            for (int overlayIndex = 0;
                 overlayIndex < layer.Overlays.Count;
                 overlayIndex++)
            {
                MediaCompositionExportOverlay overlay =
                    layer.Overlays[overlayIndex];
                MediaCompositionExportClip preparedClip =
                    await PrepareEffectClipAsync(
                        overlay.Clip,
                        request.EncodingProfile,
                        temporaryDirectory,
                        progress,
                        cancellationToken)
                        .ConfigureAwait(false);
                overlays[overlayIndex] =
                    overlay with
                    {
                        Clip = preparedClip
                    };
            }
            layers[layerIndex] =
                layer with
                {
                    Overlays =
                        Array.AsReadOnly(overlays)
                };
        }

        return request with
        {
            Clips = Array.AsReadOnly(clips),
            OverlayLayers = Array.AsReadOnly(layers)
        };
    }

    private async Task<MediaCompositionExportClip>
        PrepareEffectClipAsync(
            MediaCompositionExportClip clip,
            MediaCompositionEncodingProfile profile,
            string temporaryDirectory,
            EffectPreparationProgress progress,
            CancellationToken cancellationToken)
    {
        if (!TryGetClipEffects(
                clip,
                out AppleBuiltInClipEffects effects))
        {
            return clip;
        }
        if (clip.ArgbColor is null &&
            effects.IsIdentity)
        {
            return clip;
        }

        TimeSpan duration =
            clip.OriginalDuration -
            clip.TrimTimeFromStart -
            clip.TrimTimeFromEnd;
        if (duration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "A prepared clip requires a positive timeline range.");
        }

        string path = Path.Combine(
            temporaryDirectory,
            $"{Guid.NewGuid():N}.mp4");
        IProgress<double>? clipProgress =
            progress.CreateClipProgress();
        if (clip.ArgbColor is uint argbColor)
        {
            await RenderColorClipAsync(
                argbColor,
                duration,
                effects,
                profile,
                path,
                clipProgress,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RenderEffectClipAsync(
                clip.SourceUri ??
                    throw new InvalidOperationException(
                        "An effected clip requires a URI source."),
                clip.TrimTimeFromStart,
                duration,
                effects,
                path,
                clipProgress,
                cancellationToken).ConfigureAwait(false);
        }
        progress.CompleteClip();

        var userData =
            new Dictionary<string, string>(
                clip.UserData,
                StringComparer.Ordinal)
            {
                ["progpu.saturation"] = "1",
                ["progpu.grayscale"] = "0"
            };
        return clip with
        {
            SourceUri = new Uri(Path.GetFullPath(path)),
            ArgbColor = null,
            OriginalDuration = duration,
            TrimTimeFromStart = TimeSpan.Zero,
            TrimTimeFromEnd = TimeSpan.Zero,
            UserData =
                new ReadOnlyDictionary<string, string>(
                    userData),
            VideoEffectDefinitions = []
        };
    }

    private static Task RenderColorClipAsync(
        uint argbColor,
        TimeSpan duration,
        AppleBuiltInClipEffects effects,
        MediaCompositionEncodingProfile profile,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => RenderColorClipCoreAsync(
                argbColor,
                duration,
                effects,
                profile,
                destinationPath,
                progress,
                cancellationToken),
            cancellationToken);
    }

    private static async Task RenderColorClipCoreAsync(
        uint argbColor,
        TimeSpan duration,
        AppleBuiltInClipEffects effects,
        MediaCompositionEncodingProfile profile,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profile.Width == 0 ||
            profile.Height == 0 ||
            profile.Width > int.MaxValue ||
            profile.Height > int.MaxValue ||
            profile.FrameRateNumerator == 0 ||
            profile.FrameRateNumerator > int.MaxValue ||
            profile.FrameRateDenominator == 0)
        {
            throw new InvalidOperationException(
                "The Apple color generator requires finite dimensions " +
                "and a frame rate representable by Core Media.");
        }

        string mpeg4FileType =
            AVFileTypes.Mpeg4.GetConstant()?.ToString() ??
            "public.mpeg-4";
        using NSUrl destinationUrl =
            NSUrl.FromFilename(destinationPath);
        using var writer =
            new AVAssetWriter(
                destinationUrl,
                mpeg4FileType,
                out NSError? writerCreationError);
        using (writerCreationError)
        {
            if (writerCreationError is not null)
            {
                throw new InvalidOperationException(
                    writerCreationError.LocalizedDescription);
            }
        }

        var videoSettings =
            new AVVideoSettingsCompressed
            {
                CodecType = AVVideoCodecType.H264,
                Width = checked((int)profile.Width),
                Height = checked((int)profile.Height)
            };
        string videoMediaType =
            MediaTypeConstant(AVMediaTypes.Video, "vide");
        using var input =
            new AVAssetWriterInput(
                videoMediaType,
                videoSettings)
            {
                ExpectsMediaDataInRealTime = false,
                MediaTimeScale =
                    checked((int)profile.FrameRateNumerator)
            };
        if (!writer.CanApplyOutputSettings(
                videoSettings,
                videoMediaType) ||
            !writer.CanAddInput(input))
        {
            throw new NotSupportedException(
                "AVAssetWriter rejected the generated H.264 track.");
        }
        writer.AddInput(input);

        using var pixelFormat =
            NSNumber.FromUInt32(
                (uint)CVPixelFormatType.CV32BGRA);
        using var width =
            NSNumber.FromUInt32(profile.Width);
        using var height =
            NSNumber.FromUInt32(profile.Height);
        using var metalCompatible =
            NSNumber.FromBoolean(true);
        using var ioSurfaceProperties =
            new NSDictionary();
        using var attributeDictionary =
            NSDictionary.FromObjectsAndKeys(
                new NSObject[]
                {
                    pixelFormat,
                    width,
                    height,
                    metalCompatible,
                    ioSurfaceProperties
                },
                new NSObject[]
                {
                    CVPixelBuffer.PixelFormatTypeKey,
                    CVPixelBuffer.WidthKey,
                    CVPixelBuffer.HeightKey,
                    CVPixelBuffer.MetalCompatibilityKey,
                    CVPixelBuffer.IOSurfacePropertiesKey
                });
        var attributes =
            new CVPixelBufferAttributes(
                attributeDictionary);
        using var adaptor =
            new AVAssetWriterInputPixelBufferAdaptor(
                input,
                attributes);
        if (!writer.StartWriting())
        {
            throw new InvalidOperationException(
                writer.Error?.LocalizedDescription ??
                "AVAssetWriter could not start.");
        }
        writer.StartSessionAtSourceTime(CMTime.Zero);

        using IMTLDevice device =
            MTLDevice.SystemDefault ??
            throw new NotSupportedException(
                "A Metal device is required to render a color clip.");
        using CIContext context =
            CIContext.FromMetalDevice(device);
        using CGColorSpace colorSpace =
            CGColorSpace.CreateSrgb() ??
            throw new NotSupportedException(
                "The sRGB color space is unavailable.");
        (float red, float green, float blue, float alpha) =
            ApplyColorEffects(argbColor, effects);
        using CIColor color =
            CIColor.FromRgba(red, green, blue, alpha);
        using CIImage infiniteImage =
            CIImage.ImageWithColor(color);
        var bounds = new CGRect(
            0d,
            0d,
            profile.Width,
            profile.Height);
        using CIImage image =
            infiniteImage.ImageByCroppingToRect(bounds);
        using CVPixelBufferPool pool =
            adaptor.PixelBufferPool ??
            throw new InvalidOperationException(
                "AVAssetWriter created no pixel-buffer pool.");
        using CVPixelBuffer pixelBuffer =
            pool.CreatePixelBuffer() ??
            throw new InvalidOperationException(
                "The AVAssetWriter pixel-buffer pool is exhausted.");

        // The generated frame is immutable. Render it once on Metal and append
        // the same pooled buffer at each exact rational timestamp. AVFoundation
        // retains the buffer as required, so managed memory remains O(1).
        context.Render(
            image,
            pixelBuffer,
            bounds,
            colorSpace);

        long frameCount = GetFrameCount(
            duration,
            profile.FrameRateNumerator,
            profile.FrameRateDenominator);
        long reportInterval = Math.Max(1L, frameCount / 100L);
        using CancellationTokenRegistration registration =
            cancellationToken.Register(writer.CancelWriting);
        for (long frameIndex = 0;
             frameIndex < frameCount;
             frameIndex++)
        {
            while (!input.ReadyForMoreMediaData)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (writer.Status ==
                    AVAssetWriterStatus.Failed)
                {
                    throw new InvalidOperationException(
                        writer.Error?.LocalizedDescription ??
                        "AVAssetWriter rejected generated video data.");
                }
                Thread.Sleep(1);
            }

            var presentationTime = new CMTime(
                checked(
                    frameIndex *
                    (long)profile.FrameRateDenominator),
                checked((int)profile.FrameRateNumerator));
            if (!adaptor.AppendPixelBufferWithPresentationTime(
                    pixelBuffer,
                    presentationTime))
            {
                throw new InvalidOperationException(
                    writer.Error?.LocalizedDescription ??
                    "AVAssetWriter rejected a generated video frame.");
            }
            if (frameIndex % reportInterval == 0)
            {
                progress?.Report(
                    (double)(frameIndex + 1L) /
                    frameCount *
                    100d);
            }
        }

        input.MarkAsFinished();
        writer.EndSessionAtSourceTime(
            new CMTime(
                duration.Ticks,
                checked((int)TimeSpan.TicksPerSecond)));
        await writer.FinishWritingAsync()
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (writer.Status !=
            AVAssetWriterStatus.Completed)
        {
            throw new InvalidOperationException(
                writer.Error?.LocalizedDescription ??
                "Apple generated-color export failed.");
        }
        progress?.Report(100d);
    }

    private static long GetFrameCount(
        TimeSpan duration,
        uint frameRateNumerator,
        uint frameRateDenominator)
    {
        if (duration <= TimeSpan.Zero ||
            frameRateNumerator == 0 ||
            frameRateDenominator == 0)
        {
            return 0;
        }
        Int128 scaled =
            (Int128)duration.Ticks *
            frameRateNumerator;
        Int128 divisor =
            (Int128)TimeSpan.TicksPerSecond *
            frameRateDenominator;
        Int128 count =
            (scaled + divisor - 1) /
            divisor;
        if (count > long.MaxValue)
        {
            throw new OverflowException(
                "The generated clip contains too many frames.");
        }
        return (long)count;
    }

    private static (
        float Red,
        float Green,
        float Blue,
        float Alpha)
        ApplyColorEffects(
            uint argbColor,
            AppleBuiltInClipEffects effects)
    {
        float alpha =
            ((argbColor >> 24) & 0xff) / 255f;
        float red =
            ((argbColor >> 16) & 0xff) / 255f;
        float green =
            ((argbColor >> 8) & 0xff) / 255f;
        float blue =
            (argbColor & 0xff) / 255f;
        Vector3 transformed =
            effects.Transform.Transform(
                new Vector3(red, green, blue));
        return (
            transformed.X,
            transformed.Y,
            transformed.Z,
            alpha);
    }

    private static async Task RenderEffectClipAsync(
        Uri source,
        TimeSpan sourceStart,
        TimeSpan duration,
        AppleBuiltInClipEffects effects,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using NSUrl sourceUrl = CreateUrl(source);
        using var asset = new AVUrlAsset(sourceUrl);
        using var export =
            new AVAssetExportSession(
                asset,
                AVAssetExportSessionPreset.HighestQuality);
        using IMTLDevice device =
            MTLDevice.SystemDefault ??
            throw new NotSupportedException(
                "A Metal device is required to bake clip effects.");
        using CIContext context =
            CIContext.FromMetalDevice(device);

        MediaVideoColorTransform transform =
            effects.Transform;
        using var red = new CIVector(
            transform.Red.X,
            transform.Red.Y,
            transform.Red.Z,
            0f);
        using var green = new CIVector(
            transform.Green.X,
            transform.Green.Y,
            transform.Green.Z,
            0f);
        using var blue = new CIVector(
            transform.Blue.X,
            transform.Blue.Y,
            transform.Blue.Z,
            0f);
        using var alpha =
            new CIVector(0f, 0f, 0f, 1f);
        using var bias =
            new CIVector(
                transform.Red.W,
                transform.Green.W,
                transform.Blue.W,
                0f);
        using AVMutableVideoComposition videoComposition =
            AVMutableVideoComposition.GetVideoComposition(
                asset,
                request =>
                {
                    try
                    {
                        using var matrix =
                            new CIColorMatrix
                            {
                                InputImage =
                                    request.SourceImage,
                                RVector = red,
                                GVector = green,
                                BVector = blue,
                                AVector = alpha,
                                BiasVector = bias
                            };
                        using CIImage filtered =
                            matrix.OutputImage ??
                            throw new InvalidOperationException(
                                "Core Image produced no filtered frame.");
                        using CIImage output =
                            filtered.ImageByCroppingToRect(
                                request.SourceImage
                                    .Extent);
                        request.Finish(output, context);
                    }
                    catch (Exception exception)
                    {
                        using NSError error =
                            NSError.FromDomain(
                                new NSString(
                                    "org.progpu.media.export"),
                                -1,
                                new NSDictionary(
                                    NSError
                                        .LocalizedDescriptionKey,
                                    new NSString(
                                        exception.Message)));
                        request.Finish(error);
                    }
                });

        string mpeg4FileType =
            AVFileTypes.Mpeg4.GetConstant()?.ToString() ??
            "public.mpeg-4";
        if (!export.SupportedFileTypes.Contains(
                mpeg4FileType,
                StringComparer.Ordinal))
        {
            throw new NotSupportedException(
                "The Apple encoder cannot produce MPEG-4.");
        }
        using NSUrl destinationUrl =
            NSUrl.FromFilename(destinationPath);
        export.OutputUrl = destinationUrl;
        export.OutputFileType = mpeg4FileType;
        export.ShouldOptimizeForNetworkUse = false;
        export.TimeRange = new CMTimeRange
        {
            Start = ToMediaTime(sourceStart),
            Duration = ToMediaTime(duration)
        };
        export.VideoComposition = videoComposition;

        using CancellationTokenRegistration registration =
            cancellationToken.Register(export.CancelExport);
        Task task = export.ExportTaskAsync();
        while (!task.IsCompleted)
        {
            progress?.Report(export.Progress * 100d);
            await Task.WhenAny(
                task,
                Task.Delay(100, CancellationToken.None))
                .ConfigureAwait(false);
        }
        await task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(100d);
        if (export.Status ==
            AVAssetExportSessionStatus.Cancelled)
        {
            throw new OperationCanceledException(
                cancellationToken);
        }
        if (export.Status !=
            AVAssetExportSessionStatus.Completed)
        {
            throw new InvalidOperationException(
                export.Error?.LocalizedDescription ??
                "Apple clip-effect export failed.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup must not hide the export result.
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

    private static NSUrl CreateUrl(Uri source)
    {
        if (source.IsFile)
        {
            return NSUrl.FromFilename(source.LocalPath);
        }
        return NSUrl.FromString(source.AbsoluteUri) ??
            throw new NotSupportedException(
                $"AVFoundation cannot represent '{source}'.");
    }

    private static MediaCompositionExportFailure
        AddOverlayTracks(
            AVMutableComposition composition,
            IReadOnlyList<
                MediaCompositionExportOverlayLayer> layers,
            List<AppleOverlayVideoTrack> videoTracks,
            List<AppleAuxiliaryAudioTrack> audioMix,
            bool includeAudio,
            CancellationToken cancellationToken)
    {
        for (int layerIndex = 0;
             layerIndex < layers.Count;
             layerIndex++)
        {
            MediaCompositionExportOverlayLayer layer =
                layers[layerIndex];
            for (int overlayIndex = 0;
                 overlayIndex < layer.Overlays.Count;
                 overlayIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MediaCompositionExportOverlay overlay =
                    layer.Overlays[overlayIndex];
                MediaCompositionExportClip clip =
                    overlay.Clip;
                TimeSpan duration =
                    clip.OriginalDuration -
                    clip.TrimTimeFromStart -
                    clip.TrimTimeFromEnd;
                if (duration <= TimeSpan.Zero)
                {
                    continue;
                }

                using NSUrl sourceUrl =
                    CreateUrl(clip.SourceUri!);
                using var asset =
                    new AVUrlAsset(sourceUrl);
                AVAssetTrack? sourceVideo =
                    asset.GetTracks(AVMediaTypes.Video)
                        .FirstOrDefault();
                if (sourceVideo is null)
                {
                    return MediaCompositionExportFailure.CodecNotFound;
                }
                AVMutableCompositionTrack? destinationVideo =
                    composition.AddMutableTrack(
                        MediaTypeConstant(
                            AVMediaTypes.Video,
                            "vide"),
                        0);
                if (destinationVideo is null)
                {
                    return MediaCompositionExportFailure.Unknown;
                }
                CMTime start = ToMediaTime(overlay.Delay);
                CMTime mediaDuration = ToMediaTime(duration);
                var sourceRange = new CMTimeRange
                {
                    Start = ToMediaTime(
                        clip.TrimTimeFromStart),
                    Duration = mediaDuration
                };
                if (!destinationVideo.InsertTimeRange(
                        sourceRange,
                        sourceVideo,
                        start,
                        out NSError? videoError))
                {
                    return videoError is null
                        ? MediaCompositionExportFailure.Unknown
                        : MediaCompositionExportFailure.CodecNotFound;
                }
                destinationVideo.PreferredTransform =
                    sourceVideo.PreferredTransform;
                videoTracks.Add(
                    new AppleOverlayVideoTrack(
                        destinationVideo,
                        start,
                        mediaDuration,
                        overlay.PositionX,
                        overlay.PositionY,
                        overlay.PositionWidth,
                        overlay.PositionHeight,
                        (float)overlay.Opacity));

                if (!includeAudio ||
                    !overlay.AudioEnabled)
                {
                    continue;
                }
                AVAssetTrack? sourceAudio =
                    asset.GetTracks(AVMediaTypes.Audio)
                        .FirstOrDefault();
                if (sourceAudio is null)
                {
                    continue;
                }
                AVMutableCompositionTrack? destinationAudio =
                    composition.AddMutableTrack(
                        MediaTypeConstant(
                            AVMediaTypes.Audio,
                            "soun"),
                        0);
                if (destinationAudio is null)
                {
                    return MediaCompositionExportFailure.Unknown;
                }
                if (!destinationAudio.InsertTimeRange(
                        sourceRange,
                        sourceAudio,
                        start,
                        out NSError? audioError))
                {
                    return audioError is null
                        ? MediaCompositionExportFailure.Unknown
                        : MediaCompositionExportFailure.CodecNotFound;
                }
                audioMix.Add(
                    new AppleAuxiliaryAudioTrack(
                        destinationAudio,
                        start,
                        mediaDuration,
                        (float)clip.Volume,
                        clip.AudioEffectDefinitions));
            }
        }
        return MediaCompositionExportFailure.None;
    }

    private static (
        AVMutableVideoComposition Composition,
        AVMutableVideoCompositionInstruction Instruction,
        AVMutableVideoCompositionLayerInstruction[]
            LayerInstructions)
        CreateVideoComposition(
            AVMutableComposition composition,
            IReadOnlyList<AVAssetTrack> mainTracks,
            IReadOnlyList<AppleOverlayVideoTrack>
                overlayTracks,
            MediaCompositionEncodingProfile profile)
    {
        var videoComposition =
            new AVMutableVideoComposition
            {
                RenderSize = new CGSize(
                    profile.Width,
                    profile.Height),
                FrameDuration = CMTime.FromSeconds(
                    (double)profile.FrameRateDenominator /
                    profile.FrameRateNumerator,
                    60_000)
            };
        var instruction =
            new AVMutableVideoCompositionInstruction
            {
                TimeRange = new CMTimeRange
                {
                    Start = CMTime.Zero,
                    Duration = composition.Duration
                }
            };
        var layers =
            new AVMutableVideoCompositionLayerInstruction[
                overlayTracks.Count +
                mainTracks.Count];
        int destinationIndex = 0;

        // AVFoundation layer instructions are front-to-back. ProGPU stores
        // overlay layers and overlays back-to-front, so emit them in reverse.
        for (int index = overlayTracks.Count - 1;
             index >= 0;
             index--)
        {
            AppleOverlayVideoTrack overlay =
                overlayTracks[index];
            AVMutableVideoCompositionLayerInstruction layer =
                AVMutableVideoCompositionLayerInstruction
                    .FromAssetTrack(overlay.Track);
            var target = new CGRect(
                overlay.X,
                overlay.Y,
                overlay.Width,
                overlay.Height);
            layer.SetTransform(
                CreateTrackTransform(
                    overlay.Track,
                    target,
                    preserveAspect: false),
                overlay.Start);
            layer.SetOpacity(
                overlay.Opacity,
                overlay.Start);
            layers[destinationIndex++] = layer;
        }

        var renderBounds = new CGRect(
            0d,
            0d,
            profile.Width,
            profile.Height);
        for (int index = mainTracks.Count - 1;
             index >= 0;
             index--)
        {
            AVAssetTrack track = mainTracks[index];
            AVMutableVideoCompositionLayerInstruction layer =
                AVMutableVideoCompositionLayerInstruction
                    .FromAssetTrack(track);
            layer.SetTransform(
                CreateTrackTransform(
                    track,
                    renderBounds,
                    preserveAspect: true),
                CMTime.Zero);
            layers[destinationIndex++] = layer;
        }

        instruction.LayerInstructions = layers;
        videoComposition.Instructions =
            [instruction];
        return (videoComposition, instruction, layers);
    }

    private static CGAffineTransform CreateTrackTransform(
        AVAssetTrack track,
        CGRect target,
        bool preserveAspect)
    {
        CGAffineTransform preferred =
            track.PreferredTransform;
        CGRect sourceBounds = preferred.TransformRect(
            new CGRect(
                0d,
                0d,
                track.NaturalSize.Width,
                track.NaturalSize.Height));
        double sourceWidth =
            Math.Abs((double)sourceBounds.Width);
        double sourceHeight =
            Math.Abs((double)sourceBounds.Height);
        if (sourceWidth <= 0d || sourceHeight <= 0d)
        {
            return CGAffineTransform.MakeIdentity();
        }

        double scaleX =
            (double)target.Width / sourceWidth;
        double scaleY =
            (double)target.Height / sourceHeight;
        double x = target.X;
        double y = target.Y;
        if (preserveAspect)
        {
            double scale = Math.Min(scaleX, scaleY);
            scaleX = scale;
            scaleY = scale;
            x += ((double)target.Width -
                  sourceWidth * scale) * 0.5d;
            y += ((double)target.Height -
                  sourceHeight * scale) * 0.5d;
        }

        return new CGAffineTransform(
            (System.Runtime.InteropServices.NFloat)(
                (double)preferred.A * scaleX),
            (System.Runtime.InteropServices.NFloat)(
                (double)preferred.B * scaleY),
            (System.Runtime.InteropServices.NFloat)(
                (double)preferred.C * scaleX),
            (System.Runtime.InteropServices.NFloat)(
                (double)preferred.D * scaleY),
            (System.Runtime.InteropServices.NFloat)(
                x + ((double)preferred.Tx -
                     (double)sourceBounds.X) * scaleX),
            (System.Runtime.InteropServices.NFloat)(
                y + ((double)preferred.Ty -
                     (double)sourceBounds.Y) * scaleY));
    }

    private static string MediaTypeConstant(
        AVMediaTypes mediaType,
        string fallback) =>
        mediaType.GetConstant()?.ToString() ??
        fallback;

    private static MediaCompositionExportFailure
        AddBackgroundAudioTracks(
            AVMutableComposition composition,
            IReadOnlyList<MediaCompositionExportAudioTrack>
                sourceTracks,
            List<AppleAuxiliaryAudioTrack> audioMix,
            CancellationToken cancellationToken)
    {
        for (int index = 0;
             index < sourceTracks.Count;
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaCompositionExportAudioTrack track =
                sourceTracks[index];
            TimeSpan sourceSkip =
                track.Delay < TimeSpan.Zero
                    ? -track.Delay
                    : TimeSpan.Zero;
            TimeSpan duration =
                track.OriginalDuration -
                track.TrimTimeFromStart -
                track.TrimTimeFromEnd -
                sourceSkip;
            if (duration <= TimeSpan.Zero)
            {
                continue;
            }

            using NSUrl sourceUrl =
                CreateUrl(track.SourceUri);
            using var asset = new AVUrlAsset(sourceUrl);
            AVAssetTrack? sourceTrack =
                asset.GetTracks(AVMediaTypes.Audio)
                    .FirstOrDefault();
            if (sourceTrack is null)
            {
                return MediaCompositionExportFailure.CodecNotFound;
            }
            AVMutableCompositionTrack? destinationTrack =
                composition.AddMutableTrack(
                    MediaTypeConstant(
                        AVMediaTypes.Audio,
                        "soun"),
                    0);
            if (destinationTrack is null)
            {
                return MediaCompositionExportFailure.Unknown;
            }

            CMTime destinationStart =
                ToMediaTime(
                    track.Delay > TimeSpan.Zero
                        ? track.Delay
                        : TimeSpan.Zero);
            var sourceRange = new CMTimeRange
            {
                Start = ToMediaTime(
                    track.TrimTimeFromStart +
                    sourceSkip),
                Duration = ToMediaTime(duration)
            };
            if (!destinationTrack.InsertTimeRange(
                    sourceRange,
                    sourceTrack,
                    destinationStart,
                    out NSError? error))
            {
                return error is null
                    ? MediaCompositionExportFailure.Unknown
                    : MediaCompositionExportFailure.CodecNotFound;
            }
            audioMix.Add(
                new AppleAuxiliaryAudioTrack(
                    destinationTrack,
                    destinationStart,
                    sourceRange.Duration,
                    (float)track.Volume,
                    track.AudioEffectDefinitions));
        }
        return MediaCompositionExportFailure.None;
    }

    private sealed record AppleOverlayVideoTrack(
        AVMutableCompositionTrack Track,
        CMTime Start,
        CMTime Duration,
        double X,
        double Y,
        double Width,
        double Height,
        float Opacity);

    private sealed record AppleMainAudioSegment(
        CMTime Start,
        CMTime Duration,
        float Volume,
        IReadOnlyList<MediaCompositionEffectDefinition>
            EffectDefinitions);

    private sealed record AppleAuxiliaryAudioTrack(
        AVAssetTrack Track,
        CMTime Start,
        CMTime Duration,
        float Volume,
        IReadOnlyList<MediaCompositionEffectDefinition>
            EffectDefinitions);

    private static CMTime ToMediaTime(TimeSpan value) =>
        CMTime.FromSeconds(
            value.TotalSeconds,
            MediaTimeScale);

    private static TimeSpan ToTimeSpan(CMTime value) =>
        value.IsNumeric &&
        double.IsFinite(value.Seconds) &&
        value.Seconds > 0d
            ? TimeSpan.FromSeconds(value.Seconds)
            : TimeSpan.Zero;

    private bool AreAudioEffectsRegistered(
        IReadOnlyList<MediaCompositionEffectDefinition>
            definitions)
    {
        for (int index = 0;
             index < definitions.Count;
             index++)
        {
            string classId =
                definitions[index].ActivatableClassId;
            if (string.IsNullOrWhiteSpace(classId) ||
                !_effects.IsRegistered(classId))
            {
                return false;
            }
        }
        return true;
    }

    private static AVAssetExportSessionPreset SelectPreset(
        uint width,
        uint height)
    {
        ulong pixels = (ulong)width * height;
        if (pixels >= 3840UL * 2160UL)
        {
            return AVAssetExportSessionPreset.Preset3840x2160;
        }
        if (pixels >= 1920UL * 1080UL)
        {
            return AVAssetExportSessionPreset.Preset1920x1080;
        }
        if (pixels >= 1280UL * 720UL)
        {
            return AVAssetExportSessionPreset.Preset1280x720;
        }
        if (pixels >= 960UL * 540UL)
        {
            return AVAssetExportSessionPreset.Preset960x540;
        }
        return AVAssetExportSessionPreset.Preset640x480;
    }

    private static bool HasExactlyOneVideoSource(
        MediaCompositionExportClip clip) =>
        (clip.SourceUri is { IsAbsoluteUri: true }) ^
        (clip.ArgbColor is not null);

    private bool TryGetClipEffects(
        MediaCompositionExportClip clip,
        out AppleBuiltInClipEffects effects)
    {
        if (!TryGetBuiltInClipEffects(
                clip.UserData,
                out float saturation,
                out float grayscale) ||
            !MediaCompositionVideoEffectResolver
                .TryCaptureColorTransform(
                    _effects,
                    clip.VideoEffectDefinitions,
                    out MediaVideoColorTransform
                        declared))
        {
            effects = default;
            return false;
        }

        MediaVideoColorTransform combined =
            MediaVideoColorEffectFactory
                .CreateTransform(
                    saturation: saturation,
                    grayscale: grayscale)
                .Then(declared);
        effects = new AppleBuiltInClipEffects(
            combined);
        return true;
    }

    private static bool TryGetBuiltInClipEffects(
        IReadOnlyDictionary<string, string> userData,
        out float saturation,
        out float grayscale)
    {
        saturation = 1f;
        grayscale = 0f;
        if (!TryReadEffect(
                userData,
                "progpu.saturation",
                1f,
                0f,
                2f,
                out saturation) ||
            !TryReadEffect(
                userData,
                "progpu.grayscale",
                0f,
                0f,
                1f,
                out grayscale))
        {
            return false;
        }
        return true;
    }

    private static bool TryReadEffect(
        IReadOnlyDictionary<string, string> userData,
        string key,
        float defaultValue,
        float minimum,
        float maximum,
        out float value)
    {
        if (!userData.TryGetValue(
                key,
                out string? text))
        {
            value = defaultValue;
            return true;
        }
        return float.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value) &&
            float.IsFinite(value) &&
            value >= minimum &&
            value <= maximum;
    }

    private readonly record struct AppleBuiltInClipEffects(
        MediaVideoColorTransform Transform)
    {
        public bool IsIdentity =>
            Transform.IsIdentity;
    }

    private sealed class EffectPreparationProgress
    {
        private readonly IProgress<double>? _target;
        private readonly int _clipCount;
        private readonly double _maximum;
        private int _completed;

        public EffectPreparationProgress(
            IProgress<double>? target,
            int clipCount,
            double maximum)
        {
            _target = target;
            _clipCount = Math.Max(1, clipCount);
            _maximum = maximum;
        }

        public IProgress<double>? CreateClipProgress() =>
            _target is null
                ? null
                : new ClipProgress(this);

        public void CompleteClip()
        {
            _completed++;
            _target?.Report(
                Math.Min(
                    _maximum,
                    (double)_completed /
                    _clipCount *
                    _maximum));
        }

        private void ReportClip(double value)
        {
            double normalized =
                (_completed +
                 Math.Clamp(value, 0d, 100d) / 100d) /
                _clipCount;
            _target?.Report(
                Math.Min(
                    _maximum,
                    normalized * _maximum));
        }

        private sealed class ClipProgress :
            IProgress<double>
        {
            private readonly EffectPreparationProgress
                _owner;

            public ClipProgress(
                EffectPreparationProgress owner)
            {
                _owner = owner;
            }

            public void Report(double value) =>
                _owner.ReportClip(value);
        }
    }

    private sealed class ScaledProgress :
        IProgress<double>
    {
        private readonly IProgress<double> _target;
        private readonly double _offset;
        private readonly double _scale;

        public ScaledProgress(
            IProgress<double> target,
            double offset,
            double scale)
        {
            _target = target;
            _offset = offset;
            _scale = scale;
        }

        public void Report(double value) =>
            _target.Report(
                _offset +
                Math.Clamp(value, 0d, 100d) *
                _scale);
    }
}
