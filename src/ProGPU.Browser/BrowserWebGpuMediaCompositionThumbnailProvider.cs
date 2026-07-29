using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Media.Editing;

namespace ProGPU.Browser;

/// <summary>
/// Browser WebGPU composition thumbnails. One browser GPU device, pipeline,
/// canvas, media-element set, and texture set serve the complete batch.
/// HTML media seeks select each requested source frame, the shared
/// composition shader applies transforms/overlays/effects, and
/// OffscreenCanvas encodes the completed GPU canvas as PNG.
/// </summary>
/// <remarks>
/// For T requested positions, C timeline entries, and P output pixels,
/// timeline selection is O(T * C), GPU composition and PNG encoding are
/// O(T * P), and retained browser GPU/native storage is O(C + P). Managed
/// storage is O(B) for the required encoded result bytes. Browser video
/// import is an explicit GPU copy and PNG encoding reads the canvas; this
/// provider does not claim zero-copy.
/// </remarks>
public sealed partial class
    BrowserWebGpuMediaCompositionThumbnailProvider :
        IMediaCompositionThumbnailProvider
{
    private const int MaximumDimension = 8_192;
    private static readonly string s_shaderSource =
        ShaderResource.Load(
            typeof(
                BrowserWebGpuMediaCompositionThumbnailProvider),
            "BrowserMediaComposition.wgsl");
    private static int s_nextOperationId;

    public BrowserWebGpuMediaCompositionThumbnailProvider(
        int priority = 100)
    {
        Priority = priority;
    }

    public string Id =>
        "progpu.browser.webgpu-thumbnails";

    public int Priority { get; }

    public bool CanRender(
        MediaCompositionThumbnailRequest request) =>
        IsRequestSupported(
            request,
            OperatingSystem.IsBrowser());

    internal static bool IsRequestSupported(
        MediaCompositionThumbnailRequest request,
        bool isBrowser)
    {
        ArgumentNullException.ThrowIfNull(request);
        MediaCompositionExportRequest composition =
            request.Composition;
        if (!isBrowser ||
            request.Positions.Count == 0 ||
            !Enum.IsDefined(request.Precision) ||
            request.PixelWidth is 0 or > MaximumDimension ||
            request.PixelHeight is 0 or > MaximumDimension ||
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

        long durationTicks = 0;
        for (int index = 0;
             index < composition.Clips.Count;
             index++)
        {
            MediaCompositionExportClip clip =
                composition.Clips[index];
            if (!IsSupportedVisualClip(
                    clip,
                    out long clipDurationTicks))
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
                if (!IsSupportedVisualClip(
                        overlay.Clip,
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

    public async ValueTask<IReadOnlyList<
        MediaCompositionThumbnail>> RenderAsync(
        MediaCompositionThumbnailRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRender(request))
        {
            throw new ArgumentException(
                "The thumbnail request is not supported by this provider.",
                nameof(request));
        }
        if (!TryCreateRequestJson(
                request,
                out string requestJson))
        {
            throw new ArgumentException(
                "The thumbnail request could not be serialized.",
                nameof(request));
        }

        int operationId =
            Interlocked.Increment(
                ref s_nextOperationId);
        Task<BrowserMediaThumbnailCompletion> completion =
            BrowserMediaThumbnailCallbacks.Register(
                operationId);
        try
        {
            using CancellationTokenRegistration cancellation =
                cancellationToken.Register(
                    static id =>
                        CancelCore((int)id!),
                    operationId);
            StartCore(
                operationId,
                requestJson,
                s_shaderSource);
            BrowserMediaThumbnailCompletion outcome =
                await completion
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (outcome.Result != 0)
            {
                throw outcome.Result == 3
                    ? new NotSupportedException(
                        "The browser cannot create WebGPU composition thumbnails in this environment.")
                    : new InvalidOperationException(
                        "The browser failed to render composition thumbnails.");
            }
            return CopyResults(
                operationId,
                request,
                outcome.MetadataJson);
        }
        finally
        {
            ClearCore(operationId);
            BrowserMediaThumbnailCallbacks.Unregister(
                operationId);
        }
    }

    private static IReadOnlyList<
        MediaCompositionThumbnail> CopyResults(
        int operationId,
        MediaCompositionThumbnailRequest request,
        string metadataJson)
    {
        using JsonDocument document =
            JsonDocument.Parse(metadataJson);
        JsonElement root =
            document.RootElement;
        if (!string.Equals(
                root.GetProperty("contentType")
                    .GetString(),
                "image/png",
                StringComparison.OrdinalIgnoreCase) ||
            root.GetProperty("width")
                .GetUInt32() != request.PixelWidth ||
            root.GetProperty("height")
                .GetUInt32() != request.PixelHeight)
        {
            throw new InvalidOperationException(
                "The browser returned invalid thumbnail metadata.");
        }

        JsonElement lengths =
            root.GetProperty("lengths");
        if (lengths.GetArrayLength() !=
            request.Positions.Count)
        {
            throw new InvalidOperationException(
                "The browser returned the wrong thumbnail count.");
        }

        long maximumLength =
            checked(
                (long)request.PixelWidth *
                request.PixelHeight *
                4L +
                1_048_576L);
        var results =
            new MediaCompositionThumbnail[
                request.Positions.Count];
        for (int index = 0;
             index < results.Length;
             index++)
        {
            int length =
                lengths[index].GetInt32();
            if (length <= 0 ||
                length > maximumLength)
            {
                throw new InvalidOperationException(
                    "The browser returned an invalid encoded thumbnail length.");
            }

            byte[] bytes =
                GC.AllocateUninitializedArray<byte>(
                    length);
            CopyResult(
                operationId,
                index,
                bytes);
            results[index] =
                new MediaCompositionThumbnail(
                    bytes,
                    "image/png",
                    request.PixelWidth,
                    request.PixelHeight);
        }
        return Array.AsReadOnly(results);
    }

    private static unsafe void CopyResult(
        int operationId,
        int index,
        byte[] destination)
    {
        fixed (byte* address = destination)
        {
            int copied =
                CopyCore(
                    operationId,
                    index,
                    (nint)address,
                    destination.Length);
            if (copied != destination.Length)
            {
                throw new InvalidOperationException(
                    $"The browser copied {copied} of {destination.Length} thumbnail bytes.");
            }
        }
    }

    private static bool TryCreateRequestJson(
        MediaCompositionThumbnailRequest request,
        out string json)
    {
        var buffer =
            new ArrayBufferWriter<byte>();
        using (var writer =
               new Utf8JsonWriter(buffer))
        {
            MediaCompositionExportRequest composition =
                request.Composition;
            writer.WriteStartObject();
            writer.WriteNumber(
                "width",
                request.PixelWidth);
            writer.WriteNumber(
                "height",
                request.PixelHeight);
            writer.WriteNumber(
                "frameRate",
                (double)composition.EncodingProfile
                    .FrameRateNumerator /
                composition.EncodingProfile
                    .FrameRateDenominator);
            writer.WriteBoolean(
                "includeAudio",
                false);
            writer.WritePropertyName("clips");
            writer.WriteStartArray();
            for (int index = 0;
                 index < composition.Clips.Count;
                 index++)
            {
                if (!WriteClip(
                        writer,
                        composition.Clips[index]))
                {
                    json = string.Empty;
                    return false;
                }
            }
            writer.WriteEndArray();
            writer.WriteStartArray(
                "backgroundAudio");
            writer.WriteEndArray();
            writer.WritePropertyName(
                "overlayLayers");
            writer.WriteStartArray();
            for (int layerIndex = 0;
                 layerIndex <
                    composition.OverlayLayers.Count;
                 layerIndex++)
            {
                MediaCompositionExportOverlayLayer layer =
                    composition.OverlayLayers[layerIndex];
                writer.WriteStartArray();
                for (int overlayIndex = 0;
                     overlayIndex < layer.Overlays.Count;
                     overlayIndex++)
                {
                    MediaCompositionExportOverlay overlay =
                        layer.Overlays[overlayIndex];
                    writer.WriteStartObject();
                    writer.WritePropertyName("clip");
                    if (!WriteClip(
                            writer,
                            overlay.Clip))
                    {
                        json = string.Empty;
                        return false;
                    }
                    WriteTime(
                        writer,
                        "delay",
                        overlay.Delay);
                    writer.WriteNumber(
                        "x",
                        overlay.PositionX);
                    writer.WriteNumber(
                        "y",
                        overlay.PositionY);
                    writer.WriteNumber(
                        "width",
                        overlay.PositionWidth);
                    writer.WriteNumber(
                        "height",
                        overlay.PositionHeight);
                    writer.WriteNumber(
                        "opacity",
                        overlay.Opacity);
                    writer.WriteBoolean(
                        "audioEnabled",
                        false);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WritePropertyName(
                "thumbnailPositions");
            writer.WriteStartArray();
            for (int index = 0;
                 index < request.Positions.Count;
                 index++)
            {
                writer.WriteNumberValue(
                    request.Positions[index]
                        .TotalSeconds);
            }
            writer.WriteEndArray();
            writer.WriteNumber(
                "thumbnailPrecision",
                (int)request.Precision);
            writer.WriteEndObject();
        }
        json =
            Encoding.UTF8.GetString(
                buffer.WrittenSpan);
        return true;
    }

    private static bool WriteClip(
        Utf8JsonWriter writer,
        MediaCompositionExportClip clip)
    {
        if (!TryGetBuiltInEffects(
                clip.UserData,
                out double saturation,
                out double grayscale))
        {
            return false;
        }
        writer.WriteStartObject();
        if (clip.SourceUri is { } source)
        {
            writer.WriteString(
                "uri",
                source.AbsoluteUri);
        }
        if (clip.ArgbColor is uint color)
        {
            writer.WriteNumber(
                "argb",
                color);
        }
        WriteTime(
            writer,
            "duration",
            clip.OriginalDuration -
            clip.TrimTimeFromStart -
            clip.TrimTimeFromEnd);
        WriteTime(
            writer,
            "trimStart",
            clip.TrimTimeFromStart);
        writer.WriteNumber("volume", 0d);
        writer.WriteNumber(
            "saturation",
            saturation);
        writer.WriteNumber(
            "grayscale",
            grayscale);
        writer.WriteEndObject();
        return true;
    }

    private static bool IsSupportedVisualClip(
        MediaCompositionExportClip clip,
        out long durationTicks)
    {
        durationTicks = 0;
        if (clip.OriginalDuration <= TimeSpan.Zero ||
            clip.TrimTimeFromStart < TimeSpan.Zero ||
            clip.TrimTimeFromEnd < TimeSpan.Zero ||
            clip.TrimTimeFromStart >=
                clip.OriginalDuration ||
            clip.TrimTimeFromEnd >=
                clip.OriginalDuration -
                clip.TrimTimeFromStart)
        {
            return false;
        }
        bool hasUri =
            clip.SourceUri is { } source &&
            IsBrowserMediaUri(source);
        bool hasColor =
            clip.ArgbColor.HasValue;
        durationTicks =
            clip.OriginalDuration.Ticks -
            clip.TrimTimeFromStart.Ticks -
            clip.TrimTimeFromEnd.Ticks;
        return hasUri != hasColor &&
               durationTicks > 0 &&
               clip.VideoEffectDefinitions.Count == 0 &&
               TryGetBuiltInEffects(
                   clip.UserData,
                   out _,
                   out _);
    }

    private static bool IsBrowserMediaUri(
        Uri source) =>
        source.IsAbsoluteUri &&
        (source.Scheme.Equals(
             Uri.UriSchemeHttp,
             StringComparison.OrdinalIgnoreCase) ||
         source.Scheme.Equals(
             Uri.UriSchemeHttps,
             StringComparison.OrdinalIgnoreCase) ||
         source.Scheme.Equals(
             "blob",
             StringComparison.OrdinalIgnoreCase));

    private static bool TryGetBuiltInEffects(
        IReadOnlyDictionary<string, string> userData,
        out double saturation,
        out double grayscale)
    {
        saturation = 1d;
        grayscale = 0d;
        if (userData.TryGetValue(
                "progpu.saturation",
                out string? saturationText) &&
            (!double.TryParse(
                saturationText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out saturation) ||
             !double.IsFinite(saturation) ||
             saturation is < 0d or > 2d))
        {
            return false;
        }
        if (userData.TryGetValue(
                "progpu.grayscale",
                out string? grayscaleText) &&
            (!double.TryParse(
                grayscaleText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out grayscale) ||
             !double.IsFinite(grayscale) ||
             grayscale is < 0d or > 1d))
        {
            return false;
        }
        return true;
    }

    private static void WriteTime(
        Utf8JsonWriter writer,
        string name,
        TimeSpan value) =>
        writer.WriteNumber(
            name,
            value.TotalSeconds);

    [JSImport(
        "startBrowserMediaCompositionThumbnails",
        "progpu-browser")]
    private static partial void StartCore(
        int operationId,
        string requestJson,
        string shaderSource);

    [JSImport(
        "copyBrowserMediaCompositionThumbnail",
        "progpu-browser")]
    private static partial int CopyCore(
        int operationId,
        int index,
        nint destination,
        int length);

    [JSImport(
        "clearBrowserMediaCompositionThumbnails",
        "progpu-browser")]
    private static partial void ClearCore(
        int operationId);

    [JSImport(
        "cancelBrowserMediaCompositionExport",
        "progpu-browser")]
    private static partial void CancelCore(
        int operationId);
}

public readonly record struct BrowserMediaThumbnailCompletion(
    int Result,
    string MetadataJson);

public static partial class BrowserMediaThumbnailCallbacks
{
    private static readonly ConcurrentDictionary<
        int,
        TaskCompletionSource<
            BrowserMediaThumbnailCompletion>>
        s_pending = new();

    internal static Task<
        BrowserMediaThumbnailCompletion> Register(
        int operationId)
    {
        var completion =
            new TaskCompletionSource<
                BrowserMediaThumbnailCompletion>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        if (!s_pending.TryAdd(
                operationId,
                completion))
        {
            throw new InvalidOperationException(
                $"Browser media thumbnail operation {operationId} is already registered.");
        }
        return completion.Task;
    }

    internal static void Unregister(
        int operationId) =>
        s_pending.TryRemove(
            operationId,
            out _);

    [JSExport]
    public static void DispatchCompletion(
        int operationId,
        int result,
        string metadataJson)
    {
        if (s_pending.TryGetValue(
                operationId,
                out TaskCompletionSource<
                    BrowserMediaThumbnailCompletion>?
                    completion))
        {
            completion.TrySetResult(
                new BrowserMediaThumbnailCompletion(
                    result,
                    metadataJson));
        }
    }
}

public static partial class BrowserMediaThumbnailSmokeTest
{
    [JSExport]
    public static async Task<int> RunAsync()
    {
        var profile =
            new MediaCompositionEncodingProfile(
                "PNG",
                "RGBA",
                null,
                160,
                90,
                0,
                30,
                1,
                0,
                0,
                0);
        MediaCompositionExportClip[] clips =
        [
            new MediaCompositionExportClip(
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                TimeSpan.Zero,
                0d,
                0xFFFF0000u,
                new Dictionary<string, string>()),
            new MediaCompositionExportClip(
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                TimeSpan.Zero,
                0d,
                0xFF0000FFu,
                new Dictionary<string, string>())
        ];
        var request =
            new MediaCompositionThumbnailRequest(
                new MediaCompositionExportRequest(
                    string.Empty,
                    clips,
                    MediaCompositionTrimmingMode.Precise,
                    profile,
                    new Dictionary<string, string>()),
                [
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2)
                ],
                160,
                90,
                MediaCompositionThumbnailPrecision
                    .NearestFrame);

        IReadOnlyList<MediaCompositionThumbnail> results =
            await new BrowserWebGpuMediaCompositionThumbnailProvider()
                .RenderAsync(
                    request,
                    CancellationToken.None)
                .ConfigureAwait(false);
        if (results.Count != request.Positions.Count)
        {
            return 1;
        }
        for (int index = 0;
             index < results.Count;
             index++)
        {
            MediaCompositionThumbnail thumbnail =
                results[index];
            byte[] bytes =
                thumbnail.EncodedBytes;
            if (thumbnail.PixelWidth != 160 ||
                thumbnail.PixelHeight != 90 ||
                !string.Equals(
                    thumbnail.ContentType,
                    "image/png",
                    StringComparison.OrdinalIgnoreCase) ||
                bytes.Length < 8 ||
                bytes[0] != 0x89 ||
                bytes[1] != 0x50 ||
                bytes[2] != 0x4E ||
                bytes[3] != 0x47 ||
                bytes[4] != 0x0D ||
                bytes[5] != 0x0A ||
                bytes[6] != 0x1A ||
                bytes[7] != 0x0A)
            {
                return 2;
            }
        }
        return 0;
    }
}
