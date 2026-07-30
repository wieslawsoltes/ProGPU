using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Media.Containers;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

namespace ProGPU.Linux.Media;

/// <summary>
/// Batched Linux V4L2/DMA-BUF/WebGPU composition thumbnails. Each URI clip
/// is demuxed and decoded once in ascending presentation order; decoded NV12
/// surfaces remain device-owned through scaling, effects, and RGBA rendering.
/// </summary>
/// <remarks>
/// For T requested positions, C clips, D decoded frames, P output pixels, and
/// B encoded PNG bytes, timeline selection is O(T log C), decoding is O(D),
/// and rendering/encoding is O(T * P + B). Decoder queues, one RGBA target,
/// one WebGPU staging buffer, and at most two decoded candidates are retained.
/// The final staging map and PNG encode are explicit non-zero-copy boundaries.
/// </remarks>
public sealed class
    LinuxV4l2MediaCompositionThumbnailProvider :
        IMediaCompositionThumbnailProvider
{
    private const uint MaximumDimension = 8_192;
    private readonly LinuxNativeMediaCapabilitySnapshot
        _capabilities;
    private readonly
        LinuxV4l2PreciseMediaCompositionExportProvider
        _decoderSelector;
    private readonly MediaEffectRegistry _effects;

    public LinuxV4l2MediaCompositionThumbnailProvider(
        LinuxNativeMediaCapabilitySnapshot capabilities,
        int priority = 100,
        MediaEffectRegistry? effects = null)
    {
        _capabilities = capabilities;
        _effects = effects ?? MediaEffectRegistry.Default;
        _decoderSelector =
            new LinuxV4l2PreciseMediaCompositionExportProvider(
                capabilities,
                priority,
                _effects);
        Priority = priority;
    }

    public string Id =>
        "progpu.linux.v4l2.thumbnails";

    public int Priority { get; }

    public bool CanRender(
        MediaCompositionThumbnailRequest request)
    {
        bool hasDecoder =
            HasH264Decoder();
        bool hasGpu =
            LinuxV4l2PreciseMediaCompositionExportProvider
                .TryGetActiveVulkanDawnContext(
                    out _);
        return CanRenderRequest(
            request,
            OperatingSystem.IsLinux(),
            hasDecoder,
            hasGpu,
            _effects);
    }

    internal static bool CanRenderRequest(
        MediaCompositionThumbnailRequest request,
        bool isLinux,
        bool hasH264Decoder,
        bool hasVulkanWebGpu,
        MediaEffectRegistry? effects = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        MediaCompositionExportRequest composition =
            request.Composition;
        MediaCompositionEncodingProfile profile =
            composition.EncodingProfile;
        if (!isLinux ||
            !hasVulkanWebGpu ||
            request.Positions.Count == 0 ||
            !Enum.IsDefined(request.Precision) ||
            request.PixelWidth is 0 or > MaximumDimension ||
            request.PixelHeight is 0 or > MaximumDimension ||
            profile.Width != request.PixelWidth ||
            profile.Height != request.PixelHeight ||
            profile.FrameRateNumerator == 0 ||
            profile.FrameRateDenominator == 0 ||
            composition.Clips.Count == 0 ||
            composition.OverlayLayers.Count != 0 ||
            composition.BackgroundAudioTracks.Count != 0)
        {
            return false;
        }

        long duration = 0;
        bool hasUri = false;
        for (int index = 0;
             index < composition.Clips.Count;
             index++)
        {
            MediaCompositionExportClip clip =
                composition.Clips[index];
            bool uri =
                clip.SourceUri is
                    { IsFile: true };
            bool color =
                clip.ArgbColor.HasValue;
            if (uri == color ||
                clip.OriginalDuration <=
                    TimeSpan.Zero ||
                clip.TrimTimeFromStart <
                    TimeSpan.Zero ||
                clip.TrimTimeFromEnd <
                    TimeSpan.Zero ||
                clip.TrimTimeFromStart +
                    clip.TrimTimeFromEnd >=
                    clip.OriginalDuration ||
                !LinuxV4l2PreciseMediaCompositionExportProvider
                    .TryGetVideoEffectPlan(
                        clip,
                        effects ?? MediaEffectRegistry.Default,
                        out _))
            {
                return false;
            }
            hasUri |= uri;
            try
            {
                duration =
                    checked(
                        duration +
                        clip.OriginalDuration.Ticks -
                        clip.TrimTimeFromStart.Ticks -
                        clip.TrimTimeFromEnd.Ticks);
            }
            catch (OverflowException)
            {
                return false;
            }
        }
        if (hasUri &&
            !hasH264Decoder)
        {
            return false;
        }

        for (int index = 0;
             index < request.Positions.Count;
             index++)
        {
            long ticks =
                request.Positions[index].Ticks;
            if (ticks < 0 ||
                ticks > duration)
            {
                return false;
            }
        }
        return true;
    }

    public ValueTask<IReadOnlyList<
        MediaCompositionThumbnail>> RenderAsync(
        MediaCompositionThumbnailRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRender(request))
        {
            throw new ArgumentException(
                "The Linux V4L2 composition thumbnail request is not supported.",
                nameof(request));
        }
        return new ValueTask<IReadOnlyList<
            MediaCompositionThumbnail>>(
            Task.Run(
                () => RenderCore(
                    request,
                    cancellationToken),
                CancellationToken.None));
    }

    private IReadOnlyList<
        MediaCompositionThumbnail> RenderCore(
        MediaCompositionThumbnailRequest request,
        CancellationToken cancellationToken)
    {
        if (!LinuxV4l2PreciseMediaCompositionExportProvider
                .TryGetActiveVulkanDawnContext(
                    out DawnGpuContext? dawn) ||
            !LinuxWebGpuCompositionThumbnailRenderer
                .TryCreate(
                    dawn!,
                    request.PixelWidth,
                    request.PixelHeight,
                    out LinuxWebGpuCompositionThumbnailRenderer
                        renderer))
        {
            throw new NotSupportedException(
                "No active Vulkan Dawn device can render Linux media thumbnails.");
        }

        using (renderer)
        {
            var results =
                new MediaCompositionThumbnail?[
                    request.Positions.Count];
            TimelineIndex timeline =
                TimelineIndex.Create(
                    request.Composition.Clips);
            long frameDuration =
                Math.Max(
                    1,
                    checked(
                        TimeSpan.TicksPerSecond *
                        (long)request.Composition
                            .EncodingProfile
                            .FrameRateDenominator /
                        request.Composition
                            .EncodingProfile
                            .FrameRateNumerator));
            var workByClip =
                new List<ThumbnailWorkItem>?[
                    request.Composition.Clips.Count];
            for (int index = 0;
                 index < request.Positions.Count;
                 index++)
            {
                TimelinePosition position =
                    timeline.Resolve(
                        request.Positions[index].Ticks,
                        frameDuration);
                MediaCompositionExportClip clip =
                    request.Composition.Clips[
                        position.ClipIndex];
                if (!LinuxV4l2PreciseMediaCompositionExportProvider
                        .TryGetVideoEffectPlan(
                            clip,
                            _effects,
                            out LinuxGpuVideoEffectPlan
                                effectPlan))
                {
                    throw new InvalidDataException(
                        "The clip contains an unsupported video effect.");
                }
                if (clip.ArgbColor is uint color)
                {
                    byte[] pixels =
                        renderer.RenderColor(
                            color,
                            effectPlan);
                    results[index] =
                        Encode(
                            request,
                            pixels);
                    continue;
                }

                (workByClip[position.ClipIndex] ??=
                    []).Add(
                    new ThumbnailWorkItem(
                        index,
                        position.SourceTicks));
            }

            for (int clipIndex = 0;
                 clipIndex < workByClip.Length;
                 clipIndex++)
            {
                List<ThumbnailWorkItem>? work =
                    workByClip[clipIndex];
                if (work is null)
                {
                    continue;
                }
                work.Sort(
                    static (left, right) =>
                        left.SourceTicks.CompareTo(
                            right.SourceTicks));
                RenderUriClip(
                    request,
                    clipIndex,
                    work,
                    renderer,
                    results,
                    cancellationToken);
            }

            var completed =
                new MediaCompositionThumbnail[
                    results.Length];
            for (int index = 0;
                 index < results.Length;
                 index++)
            {
                completed[index] =
                    results[index] ??
                    throw new InvalidDataException(
                        "Linux thumbnail decoding did not resolve every requested position.");
            }
            return Array.AsReadOnly(completed);
        }
    }

    private void RenderUriClip(
        MediaCompositionThumbnailRequest request,
        int clipIndex,
        List<ThumbnailWorkItem> work,
        LinuxWebGpuCompositionThumbnailRenderer renderer,
        MediaCompositionThumbnail?[] results,
        CancellationToken cancellationToken)
    {
        MediaCompositionExportClip clip =
            request.Composition.Clips[clipIndex];
        string sourcePath =
            Path.GetFullPath(
                clip.SourceUri!.LocalPath);
        using var source =
            new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.RandomAccess);
        IsoBmffTrack track =
            LinuxV4l2PreciseMediaCompositionExportProvider
                .SelectTrack(
                    new IsoBmffDemuxer(source)
                        .Parse());
        NormalizeKeyFrameTargets(
            track,
            request.Precision,
            work);
        LinuxVideoDecoderDevice decoderDevice =
            _decoderSelector.SelectDecoder(track);
        using var reader =
            new IsoBmffNalAccessUnitReader(
                source,
                track);
        using var decoder =
            new V4l2StatefulVideoDecoder(
                decoderDevice.Path,
                track,
                preferNv12Capture: true);
        decoder.Open();

        int sampleIndex =
            LinuxV4l2PreciseMediaCompositionExportProvider
                .FindDecodeStart(
                    track,
                    TimeSpan.FromTicks(
                        work[0].SourceTicks));
        int workIndex = 0;
        bool draining = false;
        FrameCandidate? previous = null;
        try
        {
            while (workIndex < work.Count)
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
                            LinuxV4l2PreciseMediaCompositionExportProvider
                                .PresentationTime(
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
                    V4l2DecoderPumpResult.SourceChanged)
                {
                    if (decoder.IsCaptureConfigured)
                    {
                        throw new NotSupportedException(
                            "Dynamic source-size changes are not supported by Linux thumbnails.");
                    }
                    decoder.ConfigureCapture();
                    if (decoder.DecodedPixelFormat !=
                        V4l2DecodedPixelFormat.Nv12)
                    {
                        throw new NotSupportedException(
                            "The V4L2 decoder did not expose NV12 DMA-BUF output.");
                    }
                }

                while (decoder.TryDequeueFrame(
                           out V4l2DecodedFrame frame))
                {
                    var current =
                        new FrameCandidate(frame);
                    while (workIndex < work.Count &&
                           work[workIndex].SourceTicks <=
                               current.Timestamp)
                    {
                        FrameCandidate selected =
                            previous is not null &&
                            work[workIndex].SourceTicks -
                                previous.Timestamp <=
                            current.Timestamp -
                                work[workIndex].SourceTicks
                                ? previous
                                : current;
                        results[work[workIndex].ResultIndex] =
                            selected.Render(
                                request,
                                renderer,
                                clip,
                                _effects);
                        workIndex++;
                    }
                    previous?.Dispose();
                    previous = current;
                    if (workIndex == work.Count)
                    {
                        break;
                    }
                }

                if (workIndex == work.Count)
                {
                    break;
                }
                if (sampleIndex ==
                        track.Samples.Length &&
                    decoder.IsCaptureConfigured &&
                    !decoder.HasQueuedOutput &&
                    !draining)
                {
                    decoder.BeginDrain();
                    draining = true;
                }
                if (draining &&
                    decoder.EndOfStreamReached)
                {
                    if (previous is null)
                    {
                        throw new InvalidDataException(
                            "V4L2 produced no frame for the thumbnail requests.");
                    }
                    while (workIndex < work.Count)
                    {
                        results[work[workIndex].ResultIndex] =
                            previous.Render(
                                request,
                                renderer,
                                clip,
                                _effects);
                        workIndex++;
                    }
                }
            }
        }
        finally
        {
            previous?.Dispose();
        }
    }

    private static void NormalizeKeyFrameTargets(
        IsoBmffTrack track,
        MediaCompositionThumbnailPrecision precision,
        List<ThumbnailWorkItem> work)
    {
        if (precision !=
            MediaCompositionThumbnailPrecision
                .NearestKeyFrame)
        {
            return;
        }
        for (int workIndex = 0;
             workIndex < work.Count;
             workIndex++)
        {
            long target =
                ToTrackTime(
                    work[workIndex].SourceTicks,
                    track.Timescale);
            long selected = long.MinValue;
            for (int sampleIndex = 0;
                 sampleIndex < track.Samples.Length;
                 sampleIndex++)
            {
                IsoBmffSample sample =
                    track.Samples[sampleIndex];
                if (sample.IsSync &&
                    sample.PresentationTime <= target &&
                    sample.PresentationTime > selected)
                {
                    selected =
                        sample.PresentationTime;
                }
            }
            if (selected == long.MinValue)
            {
                throw new InvalidDataException(
                    "No sync sample precedes the requested Linux thumbnail.");
            }
            work[workIndex] =
                work[workIndex] with
                {
                    SourceTicks =
                        FromTrackTime(
                            selected,
                            track.Timescale)
                };
        }
        work.Sort(
            static (left, right) =>
                left.SourceTicks.CompareTo(
                    right.SourceTicks));
    }

    private static MediaCompositionThumbnail Encode(
        MediaCompositionThumbnailRequest request,
        byte[] pixels) =>
        new(
            MediaPngEncoder.Encode(
                pixels,
                request.PixelWidth,
                request.PixelHeight,
                checked(request.PixelWidth * 4),
                MediaPngPixelOrder.Rgba),
            "image/png",
            request.PixelWidth,
            request.PixelHeight);

    private bool HasH264Decoder()
    {
        for (int index = 0;
             index < _capabilities.VideoDecoders.Count;
             index++)
        {
            LinuxVideoDecoderDevice decoder =
                _capabilities.VideoDecoders[index];
            if (decoder.UsesMultiPlanarQueues &&
                decoder.SupportsStreaming &&
                (decoder.Codecs &
                 LinuxHardwareVideoCodec.H264) != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static long ToTrackTime(
        long ticks,
        uint timescale) =>
        checked(
            (long)Math.Round(
                ticks *
                (double)timescale /
                TimeSpan.TicksPerSecond,
                MidpointRounding.AwayFromZero));

    private static long FromTrackTime(
        long value,
        uint timescale) =>
        checked(
            (long)Math.Round(
                value *
                ((double)TimeSpan.TicksPerSecond /
                 timescale),
                MidpointRounding.AwayFromZero));

    private sealed class FrameCandidate :
        IDisposable
    {
        private V4l2DecodedFrame? _frame;
        private MediaCompositionThumbnail? _thumbnail;

        internal FrameCandidate(
            V4l2DecodedFrame frame)
        {
            _frame = frame;
            Timestamp =
                frame.PresentationTime.Ticks;
        }

        internal long Timestamp { get; }

        internal MediaCompositionThumbnail Render(
            MediaCompositionThumbnailRequest request,
            LinuxWebGpuCompositionThumbnailRenderer renderer,
            MediaCompositionExportClip clip,
            MediaEffectRegistry effects)
        {
            if (_thumbnail is not null)
            {
                return _thumbnail;
            }
            V4l2DecodedFrame frame =
                _frame ??
                throw new ObjectDisposedException(
                    nameof(FrameCandidate));
            _frame = null;
            if (!LinuxV4l2PreciseMediaCompositionExportProvider
                    .TryGetVideoEffectPlan(
                        clip,
                        effects,
                        out LinuxGpuVideoEffectPlan
                            effectPlan))
            {
                throw new InvalidDataException(
                    "The clip contains an unsupported video effect.");
            }
            byte[] pixels =
                renderer.RenderFrame(
                    in frame,
                    effectPlan);
            _thumbnail =
                Encode(
                    request,
                    pixels);
            return _thumbnail;
        }

        public void Dispose()
        {
            V4l2DecodedFrame? frame =
                _frame;
            _frame = null;
            frame?.Owner.Dispose();
        }
    }

    private readonly record struct ThumbnailWorkItem(
        int ResultIndex,
        long SourceTicks);

    private sealed class TimelineIndex
    {
        private readonly long[] _starts;
        private readonly long[] _durations;
        private readonly IReadOnlyList<
            MediaCompositionExportClip> _clips;

        private TimelineIndex(
            IReadOnlyList<MediaCompositionExportClip> clips,
            long[] starts,
            long[] durations)
        {
            _clips = clips;
            _starts = starts;
            _durations = durations;
        }

        internal static TimelineIndex Create(
            IReadOnlyList<
                MediaCompositionExportClip> clips)
        {
            var starts =
                new long[clips.Count];
            var durations =
                new long[clips.Count];
            long start = 0;
            for (int index = 0;
                 index < clips.Count;
                 index++)
            {
                MediaCompositionExportClip clip =
                    clips[index];
                starts[index] = start;
                durations[index] =
                    clip.OriginalDuration.Ticks -
                    clip.TrimTimeFromStart.Ticks -
                    clip.TrimTimeFromEnd.Ticks;
                start =
                    checked(
                        start +
                        durations[index]);
            }
            return new TimelineIndex(
                clips,
                starts,
                durations);
        }

        internal TimelinePosition Resolve(
            long requestedTicks,
            long frameDuration)
        {
            long total =
                checked(
                    _starts[^1] +
                    _durations[^1]);
            long effective =
                requestedTicks >= total
                    ? Math.Max(
                        _starts[^1],
                        total - frameDuration)
                    : requestedTicks;
            int clipIndex =
                Array.BinarySearch(
                    _starts,
                    effective);
            if (clipIndex < 0)
            {
                clipIndex = ~clipIndex - 1;
            }
            clipIndex =
                Math.Clamp(
                    clipIndex,
                    0,
                    _clips.Count - 1);
            long local =
                Math.Min(
                    _durations[clipIndex] - 1,
                    Math.Max(
                        0,
                        effective -
                        _starts[clipIndex]));
            return new TimelinePosition(
                clipIndex,
                checked(
                    _clips[clipIndex]
                        .TrimTimeFromStart.Ticks +
                    local));
        }
    }

    private readonly record struct TimelinePosition(
        int ClipIndex,
        long SourceTicks);
}
