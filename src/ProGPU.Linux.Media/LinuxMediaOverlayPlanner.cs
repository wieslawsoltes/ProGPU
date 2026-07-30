using ProGPU.Backend;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

namespace ProGPU.Linux.Media;

/// <summary>
/// Immutable execution interval for one standard WinUI URI or solid-color
/// overlay. Array order is the declared layer/overlay back-to-front order.
/// </summary>
internal readonly record struct LinuxMediaOverlayPlan(
    Uri? SourceUri,
    uint? ArgbColor,
    long SourceStartTicks,
    long SourceEndTrimTicks,
    long StartTicks,
    long EndTicks,
    GpuTextureLayerPlacement Placement,
    LinuxGpuVideoEffectPlan EffectPlan)
{
    internal bool IsUri => SourceUri is not null;

    internal bool IsActive(long compositionTicks) =>
        compositionTicks >= StartTicks &&
        compositionTicks < EndTicks &&
        Placement.Opacity > 0f;

    internal long GetSourceTicks(
        long compositionTicks)
    {
        if (!IsActive(compositionTicks))
        {
            throw new ArgumentOutOfRangeException(
                nameof(compositionTicks));
        }
        return checked(
            SourceStartTicks +
            compositionTicks -
            StartTicks);
    }
}

/// <summary>
/// Clean-room capture of the standard WinUI delay, position, opacity, effect,
/// source, trim, and declared z-order contracts for Linux overlays.
/// </summary>
/// <remarks>
/// Capture is O(O) time and storage for O overlays. Per-frame resolution is
/// O(O), allocation-free, and uses half-open composition intervals. Only
/// local URI sources are accepted. Custom compositor definitions are rejected.
/// </remarks>
internal static class LinuxMediaOverlayPlanner
{
    internal static bool TryCapture(
        MediaCompositionExportRequest request,
        MediaEffectRegistry effects,
        out LinuxMediaOverlayPlan[] plans)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(effects);
        var captured =
            new List<LinuxMediaOverlayPlan>();
        try
        {
            for (int layerIndex = 0;
                 layerIndex <
                    request.OverlayLayers.Count;
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
                     overlayIndex <
                        layer.Overlays.Count;
                     overlayIndex++)
                {
                    MediaCompositionExportOverlay overlay =
                        layer.Overlays[overlayIndex];
                    MediaCompositionExportClip clip =
                        overlay.Clip;
                    Uri? sourceUri =
                        clip.SourceUri;
                    uint? color =
                        clip.ArgbColor;
                    bool hasUri =
                        sourceUri is { IsFile: true };
                    bool hasColor =
                        color.HasValue;
                    if (hasUri == hasColor ||
                        sourceUri is not null &&
                            !hasUri ||
                        hasUri &&
                            (overlay.AudioEnabled ||
                             clip.AudioEffectDefinitions
                                 .Count != 0) ||
                        overlay.Delay < TimeSpan.Zero ||
                        clip.OriginalDuration <=
                            TimeSpan.Zero ||
                        clip.TrimTimeFromStart <
                            TimeSpan.Zero ||
                        clip.TrimTimeFromEnd <
                            TimeSpan.Zero ||
                        clip.TrimTimeFromStart +
                            clip.TrimTimeFromEnd >=
                            clip.OriginalDuration ||
                        !TryCreatePlacement(
                            overlay,
                            request.EncodingProfile,
                            out GpuTextureLayerPlacement
                                placement) ||
                        !LinuxV4l2PreciseMediaCompositionExportProvider
                            .TryGetVideoEffectPlan(
                                clip,
                                effects,
                                out LinuxGpuVideoEffectPlan
                                    effectPlan))
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
                        new LinuxMediaOverlayPlan(
                            sourceUri,
                            color,
                            clip.TrimTimeFromStart
                                .Ticks,
                            clip.TrimTimeFromEnd
                                .Ticks,
                            start,
                            checked(start + duration),
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
        MediaCompositionExportOverlay overlay,
        MediaCompositionEncodingProfile profile,
        out GpuTextureLayerPlacement placement)
    {
        if (profile.Width == 0 ||
            profile.Height == 0 ||
            !double.IsFinite(overlay.PositionX) ||
            !double.IsFinite(overlay.PositionY) ||
            !double.IsFinite(overlay.PositionWidth) ||
            !double.IsFinite(overlay.PositionHeight) ||
            overlay.PositionWidth <= 0d ||
            overlay.PositionHeight <= 0d ||
            !double.IsFinite(overlay.Opacity) ||
            overlay.Opacity is < 0d or > 1d)
        {
            placement = default;
            return false;
        }

        float x =
            (float)(overlay.PositionX /
                profile.Width);
        float y =
            (float)(overlay.PositionY /
                profile.Height);
        float width =
            (float)(overlay.PositionWidth /
                profile.Width);
        float height =
            (float)(overlay.PositionHeight /
                profile.Height);
        float opacity = (float)overlay.Opacity;
        if (!float.IsFinite(x) ||
            !float.IsFinite(y) ||
            !float.IsFinite(width) ||
            !float.IsFinite(height) ||
            !float.IsFinite(opacity) ||
            width <= 0f ||
            height <= 0f)
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
