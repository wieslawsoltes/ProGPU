using ProGPU.Backend;
using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

namespace ProGPU.Windows.Media;

/// <summary>
/// Immutable flattened execution plan for one WinUI-compatible media
/// overlay. Layer order and overlay order are preserved by array order.
/// </summary>
internal readonly record struct
    WindowsMediaFoundationOverlayPlan(
    MediaCompositionExportClip Clip,
    long StartTicks,
    long EndTicks,
    long SourceStartTicks,
    GpuTextureLayerPlacement Placement,
    WindowsGpuVideoEffectPlan EffectPlan)
{
    internal bool TryResolve(
        long compositionTicks,
        out long sourceTicks)
    {
        if (compositionTicks < StartTicks ||
            compositionTicks >= EndTicks)
        {
            sourceTicks = 0;
            return false;
        }
        sourceTicks =
            checked(
                SourceStartTicks +
                compositionTicks -
                StartTicks);
        return true;
    }
}

/// <summary>
/// Retained Media Foundation overlay scheduler and source-reader set.
/// </summary>
/// <remarks>
/// Setup flattens WinUI layer/overlay order in O(O) time and storage. Each
/// output frame performs O(O) allocation-free timeline checks and decodes
/// only active URI overlays. Export uses monotonic retained readers; thumbnail
/// batches use precise native seeks. No decoded pixels enter managed memory.
/// </remarks>
internal sealed class
    WindowsMediaFoundationOverlayFrameComposer :
    IDisposable
{
    private readonly WindowsMediaFoundationOverlayPlan[] _plans;
    private readonly WindowsMediaFoundationVideoFrameReader?[]
        _readers;
    private readonly bool _randomAccess;
    private readonly MediaCompositionThumbnailPrecision
        _randomAccessPrecision;

    internal WindowsMediaFoundationOverlayFrameComposer(
        IReadOnlyList<WindowsMediaFoundationOverlayPlan> plans,
        nint dxgiManager,
        MediaCompositionEncodingProfile profile,
        bool randomAccess,
        MediaCompositionThumbnailPrecision
            randomAccessPrecision =
                MediaCompositionThumbnailPrecision
                    .NearestFrame)
    {
        _plans = plans.ToArray();
        _readers =
            new WindowsMediaFoundationVideoFrameReader?[
                _plans.Length];
        _randomAccess = randomAccess;
        _randomAccessPrecision =
            randomAccessPrecision;
        try
        {
            for (int index = 0;
                 index < _plans.Length;
                 index++)
            {
                Uri? source =
                    _plans[index].Clip.SourceUri;
                if (source is null)
                {
                    continue;
                }
                _readers[index] =
                    new WindowsMediaFoundationVideoFrameReader(
                        source,
                        dxgiManager,
                        profile.Width,
                        profile.Height,
                        profile.FrameRateNumerator,
                        profile.FrameRateDenominator);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Composite(
        long compositionTicks,
        WindowsDxgiGpuEffectFrameSink sink,
        GpuTexture destination,
        CancellationToken cancellationToken)
    {
        for (int index = 0;
             index < _plans.Length;
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ref readonly WindowsMediaFoundationOverlayPlan plan =
                ref _plans[index];
            if (!plan.TryResolve(
                    compositionTicks,
                    out long sourceTicks) ||
                plan.Placement.Opacity == 0f)
            {
                continue;
            }

            if (plan.Clip.ArgbColor is uint color)
            {
                sink.CompositeColorLayer(
                    color,
                    destination,
                    plan.Placement,
                    plan.EffectPlan,
                    cancellationToken);
                continue;
            }

            nint sample = _randomAccess
                ? _readers[index]!.ReadFrame(
                    sourceTicks,
                    _randomAccessPrecision,
                    cancellationToken)
                : _readers[index]!.ReadFrameForward(
                    sourceTicks,
                    cancellationToken);
            try
            {
                sink.CompositeDecodedLayer(
                    sample,
                    destination,
                    plan.Placement,
                    plan.EffectPlan,
                    cancellationToken);
            }
            finally
            {
                WindowsMediaNative.Release(sample);
            }
        }
    }

    public void Dispose()
    {
        for (int index = 0;
             index < _readers.Length;
             index++)
        {
            _readers[index]?.Dispose();
            _readers[index] = null;
        }
    }

    internal static bool TryCapturePlans(
        MediaCompositionExportRequest request,
        MediaEffectRegistry effects,
        bool includeAudio,
        out WindowsMediaFoundationOverlayPlan[] plans)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(effects);
        var captured =
            new List<WindowsMediaFoundationOverlayPlan>();
        try
        {
            for (int layerIndex = 0;
                 layerIndex < request.OverlayLayers.Count;
                 layerIndex++)
            {
                MediaCompositionExportOverlayLayer layer =
                    request.OverlayLayers[layerIndex];
                if (layer.CustomCompositorDefinition is not null)
                {
                    plans = [];
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
                    bool hasSource =
                        clip.SourceUri is
                        { IsAbsoluteUri: true };
                    bool hasColor =
                        clip.ArgbColor.HasValue;
                    if (hasSource == hasColor ||
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
                        overlay.Opacity is < 0d or > 1d ||
                        !double.IsFinite(clip.Volume) ||
                        clip.Volume is < 0d or > 1d ||
                        clip.OriginalDuration <= TimeSpan.Zero ||
                        clip.TrimTimeFromStart < TimeSpan.Zero ||
                        clip.TrimTimeFromEnd < TimeSpan.Zero ||
                        clip.TrimTimeFromStart +
                            clip.TrimTimeFromEnd >=
                            clip.OriginalDuration ||
                        !WindowsMediaFoundationCompositionExportProvider
                            .TryGetEffectiveAudioLevels(
                                clip,
                                effects,
                                out MediaAudioStereoLevels
                                    audioLevels) ||
                        !WindowsMediaFoundationCompositionExportProvider
                            .TryGetVideoEffectPlan(
                                clip,
                                effects,
                                out WindowsGpuVideoEffectPlan
                                    effectPlan))
                    {
                        plans = [];
                        return false;
                    }

                    double normalizedX =
                        overlay.PositionX /
                        request.EncodingProfile.Width;
                    double normalizedY =
                        overlay.PositionY /
                        request.EncodingProfile.Height;
                    double normalizedWidth =
                        overlay.PositionWidth /
                        request.EncodingProfile.Width;
                    double normalizedHeight =
                        overlay.PositionHeight /
                        request.EncodingProfile.Height;
                    if (!TryCreatePlacement(
                            normalizedX,
                            normalizedY,
                            normalizedWidth,
                            normalizedHeight,
                            overlay.Opacity,
                            out GpuTextureLayerPlacement
                                placement))
                    {
                        plans = [];
                        return false;
                    }

                    long duration =
                        checked(
                            clip.OriginalDuration.Ticks -
                            clip.TrimTimeFromStart.Ticks -
                            clip.TrimTimeFromEnd.Ticks);
                    long start = overlay.Delay.Ticks;
                    captured.Add(
                        new WindowsMediaFoundationOverlayPlan(
                            clip,
                            start,
                            checked(start + duration),
                            clip.TrimTimeFromStart.Ticks,
                            placement,
                            effectPlan));
                }
            }
        }
        catch (OverflowException)
        {
            plans = [];
            return false;
        }
        plans = captured.ToArray();
        return true;
    }

    private static bool TryCreatePlacement(
        double xValue,
        double yValue,
        double widthValue,
        double heightValue,
        double opacityValue,
        out GpuTextureLayerPlacement placement)
    {
        float x = (float)xValue;
        float y = (float)yValue;
        float width = (float)widthValue;
        float height = (float)heightValue;
        float opacity = (float)opacityValue;
        if (!float.IsFinite(x) ||
            !float.IsFinite(y) ||
            !float.IsFinite(width) ||
            !float.IsFinite(height) ||
            !float.IsFinite(opacity) ||
            width <= 0f ||
            height <= 0f ||
            !float.IsFinite(x + width) ||
            !float.IsFinite(y + height) ||
            !float.IsFinite(x * 2f) ||
            !float.IsFinite(y * 2f) ||
            !float.IsFinite((x + width) * 2f) ||
            !float.IsFinite((y + height) * 2f))
        {
            placement = default;
            return false;
        }
        placement =
            new GpuTextureLayerPlacement(
                x,
                y,
                width,
                height,
                opacity);
        return true;
    }
}
