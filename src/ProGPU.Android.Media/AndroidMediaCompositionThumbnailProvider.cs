using System.Diagnostics;
using Android.Graphics;
using Android.Media;
using Android.Views;
using Java.Nio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using AndroidUri = Android.Net.Uri;

namespace ProGPU.Android.Media;

/// <summary>
/// Android-native composition thumbnails. One ImageReader and one retained
/// encoder-surface renderer serve the complete batch. URI clips reuse one
/// MediaMetadataRetriever each; decoded native Bitmaps are posted into the
/// renderer's SurfaceTexture. Standard overlays reuse the precise-export
/// SurfaceTexture/WebGPU compositor and are evaluated in ascending timeline
/// order even when callers request thumbnails out of order.
/// </summary>
/// <remarks>
/// For T requested thumbnails, C clips, O overlays, and P output pixels,
/// provider-side selection is O(T log T + T * (C + O)), native
/// decode/composition is O(T * P), and managed storage is O(T * B) for the
/// required encoded PNG results plus O(T + C + O) schedule/handle state.
/// Android's public thumbnail decoder returns Bitmap objects and the official
/// API returns encoded bytes, so this path intentionally does not claim
/// zero-copy. Overlay video remains native through the shared bounded GPU
/// path; no decoded full-frame managed array is created.
/// </remarks>
public sealed class AndroidMediaCompositionThumbnailProvider :
    IMediaCompositionThumbnailProvider
{
    private const string ProviderId =
        "progpu.android.media.thumbnails";
    private const long ImageWaitMilliseconds = 5_000;
    private static readonly IDictionary<string, string>
        s_emptyHeaders =
            new Dictionary<string, string>(
                StringComparer.Ordinal);
    private readonly MediaEffectRegistry _effects;

    public AndroidMediaCompositionThumbnailProvider(
        int priority = 100,
        MediaEffectRegistry? effects = null)
    {
        Priority = priority;
        _effects = effects ?? MediaEffectRegistry.Default;
    }

    public string Id => ProviderId;

    public int Priority { get; }

    public bool CanRender(
        MediaCompositionThumbnailRequest request) =>
        IsRequestSupported(
            request,
            OperatingSystem.IsAndroid(),
            _effects) &&
        (request.Composition.OverlayLayers.Count == 0 &&
         !HasSpatialEffects(
             request,
             _effects) ||
         AndroidMediaCodecCompositionExportProvider
             .HasActiveVulkanDawnContext());

    internal static bool IsRequestSupported(
        MediaCompositionThumbnailRequest request,
        bool isAndroid,
        MediaEffectRegistry? effects = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        MediaCompositionExportRequest composition =
            request.Composition;
        if (!isAndroid ||
            request.Positions.Count == 0 ||
            !Enum.IsDefined(request.Precision) ||
            request.PixelWidth == 0 ||
            request.PixelHeight == 0 ||
            request.PixelWidth >
                AndroidMediaCodecCompositionExportProvider
                    .MaximumDimension ||
            request.PixelHeight >
                AndroidMediaCodecCompositionExportProvider
                    .MaximumDimension ||
            composition.Clips.Count == 0 ||
            composition.EncodingProfile.Width !=
                request.PixelWidth ||
            composition.EncodingProfile.Height !=
                request.PixelHeight)
        {
            return false;
        }

        long durationTicks = 0;
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
            long clipDurationTicks =
                clip.OriginalDuration.Ticks -
                clip.TrimTimeFromStart.Ticks -
                clip.TrimTimeFromEnd.Ticks;
            if (hasUri == hasColor ||
                clipDurationTicks <= 0 ||
                !AndroidMediaCodecCompositionExportProvider
                    .TryGetVideoEffectPlan(
                        clip,
                        effects ?? MediaEffectRegistry.Default,
                        out _))
            {
                return false;
            }
            try
            {
                durationTicks = checked(
                    durationTicks +
                    clipDurationTicks);
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        if (!AndroidMediaCodecOverlayPlanner.TryCapture(
                composition,
                effects ?? MediaEffectRegistry.Default,
                out _))
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
                ticks > durationTicks)
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
            return ValueTask.FromException<
                IReadOnlyList<MediaCompositionThumbnail>>(
                new ArgumentException(
                    "The thumbnail request is not supported by this provider.",
                    nameof(request)));
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
        int width =
            checked((int)request.PixelWidth);
        int height =
            checked((int)request.PixelHeight);
        if (!AndroidMediaCodecOverlayPlanner.TryCapture(
                request.Composition,
                effects,
                out AndroidMediaCodecOverlayPlan[]
                    overlayPlans))
        {
            throw new InvalidOperationException(
                "The composition contains an unsupported overlay.");
        }
        using ImageReader reader =
            ImageReader.NewInstance(
                width,
                height,
                (ImageFormatType)Format.Rgba8888,
                maxImages: 2);
        using IAndroidEncoderSurfaceRenderer renderer =
            AndroidMediaCodecCompositionExportProvider
                .CreateRenderer(
                    reader.Surface ??
                    throw new InvalidOperationException(
                        "Android did not create a thumbnail output surface."),
                    width,
                    height,
                    overlayPlans.Length != 0 ||
                    HasSpatialEffects(
                        request,
                        effects));
        using AndroidMediaCodecOverlayFrameComposer?
            overlays =
                overlayPlans.Length == 0
                    ? null
                    : renderer is
                        AndroidMediaCodecGpuEncoderFrameSink
                            gpuRenderer
                        ? new AndroidMediaCodecOverlayFrameComposer(
                            overlayPlans,
                            gpuRenderer)
                        : throw new InvalidOperationException(
                            "Android standard overlays require the Vulkan Dawn renderer.");
        using var paint =
            new Paint(PaintFlags.FilterBitmap);
        var sourceRect = new Rect();
        var destinationRect =
            new Rect(0, 0, width, height);
        var retrievers =
            new MediaMetadataRetriever?[
                request.Composition.Clips.Count];
        var results =
            new MediaCompositionThumbnail[
                request.Positions.Count];
        var orderedPositions =
            new ThumbnailPosition[
                request.Positions.Count];
        for (int index = 0;
             index < orderedPositions.Length;
             index++)
        {
            orderedPositions[index] =
                new ThumbnailPosition(
                    index,
                    request.Positions[index]);
        }
        Array.Sort(
            orderedPositions,
            static (left, right) =>
                left.Position.CompareTo(
                    right.Position));

        try
        {
            for (int orderedIndex = 0;
                 orderedIndex <
                    orderedPositions.Length;
                 orderedIndex++)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
                ThumbnailPosition requested =
                    orderedPositions[orderedIndex];
                TimelineFrame frame =
                    ResolveTimelineFrame(
                        request.Composition.Clips,
                        requested.Position);
                MediaCompositionExportClip clip =
                    request.Composition.Clips[
                        frame.ClipIndex];
                if (!AndroidMediaCodecCompositionExportProvider
                        .TryGetVideoEffectPlan(
                            clip,
                            effects,
                            out MediaVideoEffectPlan
                                effectPlan))
                {
                    throw new InvalidOperationException(
                        "The clip contains an unsupported video effect.");
                }
                long presentationTimeMicroseconds =
                    Math.Max(
                        0,
                        requested.Position.Ticks /
                        TimeSpan.TicksPerMicrosecond);

                if (clip.ArgbColor is uint color)
                {
                    renderer.DrawColorFrame(
                        presentationTimeMicroseconds,
                        color,
                        effectPlan,
                        overlays,
                        cancellationToken);
                }
                else
                {
                    MediaMetadataRetriever retriever =
                        retrievers[frame.ClipIndex] ??=
                            CreateRetriever(
                                clip.SourceUri!);
                    using Bitmap bitmap =
                        retriever.GetScaledFrameAtTime(
                            frame.SourceTimeMicroseconds,
                            request.Precision ==
                                MediaCompositionThumbnailPrecision
                                    .NearestFrame
                                ? Option.Closest
                                : Option.ClosestSync,
                            width,
                            height) ??
                        throw new InvalidOperationException(
                            $"Android returned no frame for clip {frame.ClipIndex}.");
                    PostBitmap(
                        renderer.DecoderSurface,
                        bitmap,
                        sourceRect,
                        destinationRect,
                        paint);
                    renderer.DrawFrame(
                        presentationTimeMicroseconds,
                        effectPlan,
                        overlays,
                        cancellationToken);
                }

                using Image image =
                    AcquireImage(
                        reader,
                        cancellationToken);
                results[requested.Index] =
                    EncodeImage(
                        image,
                        width,
                        height);
            }
        }
        finally
        {
            for (int index = 0;
                 index < retrievers.Length;
                 index++)
            {
                retrievers[index]?.Dispose();
            }
        }

        return Array.AsReadOnly(results);
    }

    private static bool HasSpatialEffects(
        MediaCompositionThumbnailRequest request,
        MediaEffectRegistry effects)
    {
        IReadOnlyList<MediaCompositionExportClip> clips =
            request.Composition.Clips;
        for (int index = 0;
             index < clips.Count;
             index++)
        {
            if (AndroidMediaCodecCompositionExportProvider
                    .TryGetVideoEffectPlan(
                        clips[index],
                        effects,
                        out MediaVideoEffectPlan plan) &&
                plan.HasSpatialEffect)
            {
                return true;
            }
        }
        return false;
    }

    private static MediaMetadataRetriever CreateRetriever(
        Uri source)
    {
        var retriever =
            new MediaMetadataRetriever();
        try
        {
            if (source.IsFile)
            {
                retriever.SetDataSource(
                    source.LocalPath);
            }
            else if (string.Equals(
                         source.Scheme,
                         "content",
                         StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(
                         source.Scheme,
                         "android.resource",
                         StringComparison.OrdinalIgnoreCase))
            {
                using AndroidUri androidUri =
                    AndroidUri.Parse(
                        source.AbsoluteUri) ??
                    throw new InvalidOperationException(
                        "Android could not parse the thumbnail content URI.");
                retriever.SetDataSource(
                    global::Android.App.Application.Context ??
                    throw new InvalidOperationException(
                        "Android application context is unavailable."),
                    androidUri);
            }
            else
            {
                retriever.SetDataSource(
                    source.AbsoluteUri,
                    s_emptyHeaders);
            }
            return retriever;
        }
        catch
        {
            retriever.Dispose();
            throw;
        }
    }

    private static void PostBitmap(
        Surface surface,
        Bitmap bitmap,
        Rect sourceRect,
        Rect destinationRect,
        Paint paint)
    {
        sourceRect.Set(
            0,
            0,
            bitmap.Width,
            bitmap.Height);
        Canvas? canvas = null;
        try
        {
            canvas = surface.LockCanvas(null) ??
                throw new InvalidOperationException(
                    "Android did not lock the thumbnail decoder surface.");
            canvas.DrawBitmap(
                bitmap,
                sourceRect,
                destinationRect,
                paint);
        }
        finally
        {
            if (canvas is not null)
            {
                surface.UnlockCanvasAndPost(canvas);
                canvas.Dispose();
            }
        }
    }

    private static Image AcquireImage(
        ImageReader reader,
        CancellationToken cancellationToken)
    {
        long deadline =
            Stopwatch.GetTimestamp() +
            ImageWaitMilliseconds *
            Stopwatch.Frequency /
            1_000;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Image? image =
                reader.AcquireNextImage();
            if (image is not null)
            {
                return image;
            }
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException(
                    "Android did not publish the rendered thumbnail image.");
            }
            Thread.Sleep(1);
        }
    }

    private static MediaCompositionThumbnail EncodeImage(
        Image image,
        int width,
        int height)
    {
        Image.Plane[] planes =
            image.GetPlanes() ??
            throw new InvalidOperationException(
                "Android returned no thumbnail image planes.");
        if (planes.Length != 1)
        {
            throw new InvalidOperationException(
                "Android returned a non-RGBA thumbnail buffer.");
        }

        Image.Plane plane = planes[0];
        if (plane.PixelStride != 4 ||
            plane.RowStride < checked(width * 4) ||
            plane.RowStride % plane.PixelStride != 0)
        {
            throw new InvalidOperationException(
                "Android returned an unsupported RGBA thumbnail layout.");
        }

        int paddedWidth =
            plane.RowStride /
            plane.PixelStride;
        using Bitmap padded =
            Bitmap.CreateBitmap(
                paddedWidth,
                height,
                Bitmap.Config.Argb8888!) ??
            throw new InvalidOperationException(
                "Android could not allocate the thumbnail bitmap.");
        ByteBuffer buffer =
            plane.Buffer ??
            throw new InvalidOperationException(
                "Android returned no RGBA thumbnail buffer.");
        buffer.Rewind();
        padded.CopyPixelsFromBuffer(buffer);

        Bitmap? cropped = null;
        Bitmap encodedBitmap = padded;
        if (paddedWidth != width)
        {
            cropped =
                Bitmap.CreateBitmap(
                    padded,
                    0,
                    0,
                    width,
                    height) ??
                throw new InvalidOperationException(
                    "Android could not crop the thumbnail row padding.");
            encodedBitmap = cropped;
        }

        try
        {
            using var output =
                new MemoryStream(
                    checked(width * height / 2));
            if (!encodedBitmap.Compress(
                    Bitmap.CompressFormat.Png!,
                    quality: 100,
                    output))
            {
                throw new InvalidOperationException(
                    "Android could not encode the thumbnail as PNG.");
            }
            return new MediaCompositionThumbnail(
                output.ToArray(),
                "image/png",
                checked((uint)width),
                checked((uint)height));
        }
        finally
        {
            cropped?.Dispose();
        }
    }

    private static TimelineFrame ResolveTimelineFrame(
        IReadOnlyList<MediaCompositionExportClip> clips,
        TimeSpan position)
    {
        long remainingTicks = position.Ticks;
        for (int index = 0;
             index < clips.Count;
             index++)
        {
            MediaCompositionExportClip clip =
                clips[index];
            long durationTicks =
                clip.OriginalDuration.Ticks -
                clip.TrimTimeFromStart.Ticks -
                clip.TrimTimeFromEnd.Ticks;
            bool isLast =
                index == clips.Count - 1;
            if (remainingTicks < durationTicks ||
                isLast)
            {
                long localTicks =
                    Math.Min(
                        remainingTicks,
                        Math.Max(0, durationTicks - 1));
                long sourceTicks =
                    checked(
                        clip.TrimTimeFromStart.Ticks +
                        localTicks);
                return new TimelineFrame(
                    index,
                    Math.Max(
                        0,
                        sourceTicks /
                        TimeSpan.TicksPerMicrosecond));
            }
            remainingTicks -= durationTicks;
        }

        throw new ArgumentOutOfRangeException(
            nameof(position));
    }

    private readonly record struct TimelineFrame(
        int ClipIndex,
        long SourceTimeMicroseconds);

    private readonly record struct ThumbnailPosition(
        int Index,
        TimeSpan Position);
}
