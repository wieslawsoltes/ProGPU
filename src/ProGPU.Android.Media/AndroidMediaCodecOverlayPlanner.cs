using ProGPU.Backend;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

namespace ProGPU.Android.Media;

/// <summary>
/// Immutable execution interval for one standard WinUI media overlay.
/// Array order is the declared layer/overlay back-to-front order.
/// </summary>
internal readonly record struct AndroidMediaCodecOverlayPlan(
    MediaCompositionExportClip Clip,
    long StartMicroseconds,
    long EndMicroseconds,
    long SourceStartMicroseconds,
    long SourceEndMicroseconds,
    GpuTextureLayerPlacement Placement,
    MediaVideoEffectPlan EffectPlan,
    bool AudioEnabled)
{
    internal bool TryResolve(
        long compositionMicroseconds,
        out long sourceMicroseconds)
    {
        if (compositionMicroseconds <
                StartMicroseconds ||
            compositionMicroseconds >=
                EndMicroseconds)
        {
            sourceMicroseconds = 0;
            return false;
        }

        sourceMicroseconds =
            checked(
                SourceStartMicroseconds +
                compositionMicroseconds -
                StartMicroseconds);
        return sourceMicroseconds <
            SourceEndMicroseconds;
    }
}

/// <summary>
/// Clean-room capture of standard WinUI overlay timing, placement, effects,
/// and declared z-order for the Android WebGPU export lane.
/// </summary>
/// <remarks>
/// Capture is O(O) time and storage for O overlays. Per-frame resolution is
/// O(O), allocation-free, and uses half-open composition/source intervals.
/// Custom compositor definitions are rejected rather than approximated.
/// </remarks>
internal static class AndroidMediaCodecOverlayPlanner
{
    internal static bool TryCapture(
        MediaCompositionExportRequest request,
        MediaEffectRegistry effects,
        out AndroidMediaCodecOverlayPlan[] plans)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(effects);
        var captured =
            new List<AndroidMediaCodecOverlayPlan>();
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
                    bool hasSource =
                        clip.SourceUri is
                        { IsAbsoluteUri: true };
                    bool hasColor =
                        clip.ArgbColor.HasValue;
                    if (hasSource == hasColor ||
                        overlay.Delay < TimeSpan.Zero ||
                        !TryCreatePlacement(
                            overlay,
                            request.EncodingProfile,
                            out GpuTextureLayerPlacement
                                placement) ||
                        clip.OriginalDuration <=
                            TimeSpan.Zero ||
                        clip.TrimTimeFromStart <
                            TimeSpan.Zero ||
                        clip.TrimTimeFromEnd <
                            TimeSpan.Zero ||
                        clip.TrimTimeFromStart +
                            clip.TrimTimeFromEnd >=
                            clip.OriginalDuration ||
                        !AndroidMediaCodecVideoEffectPlanner
                            .TryGetVideoEffectPlan(
                                clip,
                                effects,
                                out MediaVideoEffectPlan
                                    effectPlan))
                    {
                        plans = [];
                        return false;
                    }

                    long sourceStart =
                        ToMicroseconds(
                            clip.TrimTimeFromStart);
                    long duration =
                        ToMicroseconds(
                            clip.OriginalDuration -
                            clip.TrimTimeFromStart -
                            clip.TrimTimeFromEnd);
                    long start =
                        ToMicroseconds(overlay.Delay);
                    if (duration <= 0)
                    {
                        plans = [];
                        return false;
                    }
                    captured.Add(
                        new AndroidMediaCodecOverlayPlan(
                            clip,
                            start,
                            checked(start + duration),
                            sourceStart,
                            checked(
                                sourceStart + duration),
                            placement,
                            effectPlan,
                            overlay.AudioEnabled));
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

    internal static bool TryCreatePlacement(
        MediaCompositionExportOverlay overlay,
        MediaCompositionEncodingProfile profile,
        out GpuTextureLayerPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Width == 0 ||
            profile.Height == 0 ||
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
            placement = default;
            return false;
        }

        double x =
            overlay.PositionX /
            profile.Width;
        double y =
            overlay.PositionY /
            profile.Height;
        double width =
            overlay.PositionWidth /
            profile.Width;
        double height =
            overlay.PositionHeight /
            profile.Height;
        float normalizedX = (float)x;
        float normalizedY = (float)y;
        float normalizedWidth = (float)width;
        float normalizedHeight = (float)height;
        float opacity = (float)overlay.Opacity;
        if (!float.IsFinite(normalizedX) ||
            !float.IsFinite(normalizedY) ||
            !float.IsFinite(normalizedWidth) ||
            !float.IsFinite(normalizedHeight) ||
            !float.IsFinite(opacity) ||
            normalizedWidth <= 0f ||
            normalizedHeight <= 0f)
        {
            placement = default;
            return false;
        }

        placement =
            new GpuTextureLayerPlacement(
                normalizedX,
                normalizedY,
                normalizedWidth,
                normalizedHeight,
                opacity);
        return true;
    }

    private static long ToMicroseconds(
        TimeSpan time) =>
        time.Ticks /
        TimeSpan.TicksPerMicrosecond;
}
