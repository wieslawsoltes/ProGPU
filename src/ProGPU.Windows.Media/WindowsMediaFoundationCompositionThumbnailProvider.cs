using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

namespace ProGPU.Windows.Media;

/// <summary>
/// Media Foundation/DXGI/WebGPU composition thumbnails. One D3D11 device,
/// DXGI manager, WebGPU effect renderer, retained staging texture, and source
/// reader per URI clip serve the complete batch. Decoded and effect pixels
/// remain GPU-resident until the required final PNG readback.
/// </summary>
/// <remarks>
/// For T positions, C clips, D decoded frames after native seeks, and P output
/// pixels, selection is O(T * C), decode is O(D), GPU work and readback are
/// O(T * P), and PNG encoding is O(T * P). Native residency is bounded by the
/// source-reader queues plus six shared GPU textures and one staging texture.
/// Managed storage is O(P + B), excluding the required accumulated encoded
/// results B. The final staging readback means this provider does not claim
/// zero-copy thumbnails.
/// </remarks>
public sealed class
    WindowsMediaFoundationCompositionThumbnailProvider :
        IMediaCompositionThumbnailProvider
{
    private const uint MaximumDimension = 8_192;
    private readonly MediaEffectRegistry _effects;

    public WindowsMediaFoundationCompositionThumbnailProvider(
        int priority = 100,
        MediaEffectRegistry? effects = null)
    {
        Priority = priority;
        _effects = effects ?? MediaEffectRegistry.Default;
    }

    public string Id =>
        "progpu.windows.mediafoundation.thumbnails";

    public int Priority { get; }

    public bool CanRender(
        MediaCompositionThumbnailRequest request) =>
        IsRequestSupported(
            request,
            OperatingSystem.IsWindows(),
            _effects) &&
        WindowsMediaFoundationCompositionExportProvider
            .TryGetActiveD3D12DawnContext(out _);

    internal static bool IsRequestSupported(
        MediaCompositionThumbnailRequest request,
        bool isWindows) =>
        IsRequestSupported(
            request,
            isWindows,
            MediaEffectRegistry.Default);

    internal static bool IsRequestSupported(
        MediaCompositionThumbnailRequest request,
        bool isWindows,
        MediaEffectRegistry effects)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(effects);
        MediaCompositionExportRequest composition =
            request.Composition;
        MediaCompositionEncodingProfile profile =
            composition.EncodingProfile;
        if (!isWindows ||
            request.Positions.Count == 0 ||
            !Enum.IsDefined(request.Precision) ||
            request.PixelWidth is 0 or > MaximumDimension ||
            request.PixelHeight is 0 or > MaximumDimension ||
            profile.Width != request.PixelWidth ||
            profile.Height != request.PixelHeight ||
            profile.FrameRateNumerator == 0 ||
            profile.FrameRateDenominator == 0 ||
            composition.Clips.Count == 0 ||
            composition.OverlayLayers.Count != 0)
        {
            return false;
        }

        long duration = 0;
        for (int index = 0;
             index < composition.Clips.Count;
             index++)
        {
            MediaCompositionExportClip clip =
                composition.Clips[index];
            bool hasUri =
                clip.SourceUri is
                    { IsAbsoluteUri: true };
            bool hasColor =
                clip.ArgbColor.HasValue;
            if (hasUri == hasColor ||
                clip.OriginalDuration <= TimeSpan.Zero ||
                clip.TrimTimeFromStart < TimeSpan.Zero ||
                clip.TrimTimeFromEnd < TimeSpan.Zero ||
                clip.TrimTimeFromStart >=
                    clip.OriginalDuration ||
                clip.TrimTimeFromEnd >=
                    clip.OriginalDuration -
                    clip.TrimTimeFromStart ||
                !WindowsMediaFoundationCompositionExportProvider
                    .TryGetVideoColorTransform(
                        clip,
                        effects,
                        out _))
            {
                return false;
            }
            try
            {
                duration = checked(
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

        for (int index = 0;
             index < request.Positions.Count;
             index++)
        {
            long position =
                request.Positions[index].Ticks;
            if (position < 0 ||
                position > duration)
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
                "The Windows composition thumbnail request is not supported.",
                nameof(request));
        }
        return new ValueTask<IReadOnlyList<
            MediaCompositionThumbnail>>(
            Task.Run(
                () => RenderCore(
                    request,
                    _effects,
                    cancellationToken),
                CancellationToken.None));
    }

    private static IReadOnlyList<
        MediaCompositionThumbnail> RenderCore(
        MediaCompositionThumbnailRequest request,
        MediaEffectRegistry effects,
        CancellationToken cancellationToken)
    {
        bool comInitialized = false;
        bool mediaFoundationStarted = false;
        nint d3dDevice = 0;
        nint d3dContext = 0;
        nint dxgiManager = 0;
        WindowsDxgiGpuEffectFrameSink? renderer = null;
        ClipReader?[] readers =
            new ClipReader?[request.Composition.Clips.Count];
        var colorTransforms =
            new GpuTextureColorTransform[
                request.Composition.Clips.Count];
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int index = 0;
                 index < colorTransforms.Length;
                 index++)
            {
                if (!WindowsMediaFoundationCompositionExportProvider
                        .TryGetVideoColorTransform(
                            request.Composition.Clips[index],
                            effects,
                            out colorTransforms[index]))
                {
                    throw new InvalidOperationException(
                        "Validated Windows thumbnail effects became invalid.");
                }
            }
            WindowsMediaNative.InitializeCom();
            comInitialized = true;
            WindowsMediaNative.StartupMediaFoundation();
            mediaFoundationStarted = true;
            d3dDevice =
                WindowsMediaNative.CreateD3D11Device(
                    out d3dContext);
            dxgiManager =
                WindowsMediaNative.CreateDxgiDeviceManager(
                    d3dDevice);
            if (!WindowsMediaFoundationCompositionExportProvider
                    .TryGetActiveD3D12DawnContext(
                        out DawnGpuContext? dawn))
            {
                throw new NotSupportedException(
                    "Windows WebGPU thumbnails require an active Dawn D3D12 context.");
            }
            renderer =
                new WindowsDxgiGpuEffectFrameSink(
                    dawn!,
                    d3dDevice,
                    d3dContext,
                    request.PixelWidth,
                    request.PixelHeight);
            CreateReaders(
                request,
                dxgiManager,
                readers);

            TimelineIndex timeline =
                TimelineIndex.Create(
                    request.Composition.Clips);
            var results =
                new MediaCompositionThumbnail[
                    request.Positions.Count];
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
            for (int index = 0;
                 index < results.Length;
                 index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimelinePosition position =
                    timeline.Resolve(
                        request.Positions[index].Ticks,
                        frameDuration);
                MediaCompositionExportClip clip =
                    request.Composition.Clips[
                        position.ClipIndex];
                GpuTextureColorTransform colorTransform =
                    colorTransforms[position.ClipIndex];

                byte[] pixels;
                if (clip.ArgbColor is uint color)
                {
                    pixels =
                        renderer.ProcessColorAndReadback(
                            color,
                            colorTransform,
                            cancellationToken);
                }
                else
                {
                    nint sample =
                        readers[position.ClipIndex]!
                            .ReadFrame(
                                position.SourceTicks,
                                request.Precision,
                                cancellationToken);
                    try
                    {
                        pixels =
                            renderer.ProcessAndReadback(
                                sample,
                                colorTransform,
                                cancellationToken);
                    }
                    finally
                    {
                        WindowsMediaNative.Release(sample);
                    }
                }
                byte[] encoded =
                    MediaPngEncoder.Encode(
                        pixels,
                        request.PixelWidth,
                        request.PixelHeight,
                        checked(request.PixelWidth * 4),
                        MediaPngPixelOrder.Bgra);
                results[index] =
                    new MediaCompositionThumbnail(
                        encoded,
                        "image/png",
                        request.PixelWidth,
                        request.PixelHeight);
            }
            return Array.AsReadOnly(results);
        }
        finally
        {
            for (int index = 0;
                 index < readers.Length;
                 index++)
            {
                readers[index]?.Dispose();
            }
            renderer?.Dispose();
            WindowsMediaNative.Release(dxgiManager);
            WindowsMediaNative.Release(d3dContext);
            WindowsMediaNative.Release(d3dDevice);
            if (mediaFoundationStarted)
            {
                WindowsMediaNative.ShutdownMediaFoundation();
            }
            if (comInitialized)
            {
                WindowsMediaNative.UninitializeCom();
            }
        }
    }

    private static void CreateReaders(
        MediaCompositionThumbnailRequest request,
        nint dxgiManager,
        ClipReader?[] readers)
    {
        MediaCompositionEncodingProfile profile =
            request.Composition.EncodingProfile;
        for (int index = 0;
             index < request.Composition.Clips.Count;
             index++)
        {
            MediaCompositionExportClip clip =
                request.Composition.Clips[index];
            if (clip.SourceUri is not null)
            {
                readers[index] =
                    new ClipReader(
                        clip.SourceUri,
                        dxgiManager,
                        profile.Width,
                        profile.Height,
                        profile.FrameRateNumerator,
                        profile.FrameRateDenominator);
            }
        }
    }

    private sealed class ClipReader :
        IDisposable
    {
        private nint _reader;
        private nint _mediaType;

        internal ClipReader(
            Uri source,
            nint dxgiManager,
            uint width,
            uint height,
            uint frameRateNumerator,
            uint frameRateDenominator)
        {
            try
            {
                _reader =
                    WindowsMediaNative
                        .CreateTranscodeSourceReader(
                            WindowsMediaFoundationCompositionExportProvider
                                .ToSourceUrl(source),
                            dxgiManager);
                _mediaType =
                    WindowsMediaNative.CreateArgb32VideoType(
                        width,
                        height,
                        frameRateNumerator,
                        frameRateDenominator);
                WindowsMediaNative.ConfigureSourceReaderStream(
                    _reader,
                    WindowsMediaNative.FirstVideoStream,
                    _mediaType);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal nint ReadFrame(
            long sourceTicks,
            MediaCompositionThumbnailPrecision precision,
            CancellationToken cancellationToken)
        {
            WindowsMediaNative.SetSourceReaderPosition(
                _reader,
                sourceTicks);
            nint candidate = 0;
            long candidateTimestamp = long.MinValue;
            try
            {
                while (true)
                {
                    cancellationToken
                        .ThrowIfCancellationRequested();
                    nint sample =
                        WindowsMediaNative.ReadSourceSample(
                            _reader,
                            WindowsMediaNative.FirstVideoStream,
                            out uint flags,
                            out long timestamp);
                    if ((flags &
                         WindowsMediaNative
                             .SourceReaderEndOfStream) != 0)
                    {
                        WindowsMediaNative.Release(sample);
                        break;
                    }
                    if (sample == 0)
                    {
                        continue;
                    }
                    if (precision ==
                        MediaCompositionThumbnailPrecision
                            .NearestKeyFrame)
                    {
                        WindowsMediaNative.Release(candidate);
                        candidate = sample;
                        break;
                    }
                    if (timestamp >= sourceTicks)
                    {
                        if (candidate == 0 ||
                            timestamp - sourceTicks <
                            sourceTicks -
                            candidateTimestamp)
                        {
                            WindowsMediaNative.Release(candidate);
                            candidate = sample;
                        }
                        else
                        {
                            WindowsMediaNative.Release(sample);
                        }
                        break;
                    }
                    WindowsMediaNative.Release(candidate);
                    candidate = sample;
                    candidateTimestamp = timestamp;
                }
                if (candidate == 0)
                {
                    throw new InvalidDataException(
                        "Media Foundation returned no frame for the requested thumbnail position.");
                }
                nint result = candidate;
                candidate = 0;
                return result;
            }
            finally
            {
                WindowsMediaNative.Release(candidate);
            }
        }

        public void Dispose()
        {
            WindowsMediaNative.Release(
                Interlocked.Exchange(
                    ref _mediaType,
                    0));
            WindowsMediaNative.Release(
                Interlocked.Exchange(
                    ref _reader,
                    0));
        }
    }

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
            IReadOnlyList<MediaCompositionExportClip> clips)
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
