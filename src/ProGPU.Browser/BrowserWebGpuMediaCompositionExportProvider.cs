using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Numerics;
using System.Text;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

namespace ProGPU.Browser;

/// <summary>
/// Browser-native WebGPU composition and H.264/AAC recording provider.
/// Rendering remains entirely on the browser GPU; MediaRecorder owns the
/// platform encoder and muxer. Export is intentionally real-time because the
/// MediaRecorder clock is the portable browser A/V synchronization contract.
/// </summary>
public sealed partial class
    BrowserWebGpuMediaCompositionExportProvider :
        IMediaCompositionExportProvider,
        IMediaCompositionExportCapabilityProvider
{
    private static readonly string s_shaderSource =
        ShaderResource.Load(
            typeof(
                BrowserWebGpuMediaCompositionExportProvider),
            "BrowserMediaComposition.wgsl");
    private static readonly string
        s_gaussianShaderSource =
            ShaderResource.Load(
                typeof(GpuTextureGaussianBlur),
                "TextureGaussianBlur.wgsl");
    private static int s_nextOperationId;
    private readonly MediaEffectRegistry _effects;

    public BrowserWebGpuMediaCompositionExportProvider(
        int priority = 100,
        MediaEffectRegistry? effects = null)
    {
        Priority = priority;
        _effects = effects ?? MediaEffectRegistry.Default;
    }

    public string Id =>
        "progpu.browser.webgpu-native-export";

    public int Priority { get; }

    public bool CanRender(
        MediaCompositionExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsBrowser() ||
            request.Clips.Count == 0 ||
            request.EncodingProfile.Width is 0 or > 8_192 ||
            request.EncodingProfile.Height is 0 or > 8_192 ||
            request.EncodingProfile.FrameRateNumerator == 0 ||
            request.EncodingProfile.FrameRateDenominator == 0 ||
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
                StringComparison.OrdinalIgnoreCase) ||
            !BrowserStorageServices.TryGetSaveSelection(
                request.DestinationPath,
                out _,
                out _))
        {
            return false;
        }

        bool requiresBaking =
            request.TrimmingMode ==
                MediaCompositionTrimmingMode.Precise ||
            request.BackgroundAudioTracks.Count != 0 ||
            request.OverlayLayers.Count != 0;
        for (int index = 0;
             index < request.Clips.Count;
             index++)
        {
            MediaCompositionExportClip clip =
                request.Clips[index];
            if (!IsSupportedClip(clip))
            {
                return false;
            }
            requiresBaking |=
                clip.ArgbColor is not null ||
                clip.Volume != 1d ||
                clip.AudioEffectDefinitions.Count != 0 ||
                HasNonIdentityVideoEffect(clip);
        }
        for (int index = 0;
             index <
                request.BackgroundAudioTracks.Count;
             index++)
        {
            MediaCompositionExportAudioTrack track =
                request.BackgroundAudioTracks[index];
            if (!IsBrowserMediaUri(track.SourceUri) ||
                !double.IsFinite(track.Volume) ||
                track.Volume is < 0d or > 1d ||
                !TryGetAudioEffectGraph(
                    track.AudioEffectDefinitions,
                    out _))
            {
                return false;
            }
        }
        for (int layerIndex = 0;
             layerIndex <
                request.OverlayLayers.Count;
             layerIndex++)
        {
            MediaCompositionExportOverlayLayer layer =
                request.OverlayLayers[layerIndex];
            if (layer.CustomCompositorDefinition is not null)
            {
                return false;
            }
            for (int overlayIndex = 0;
                 overlayIndex <
                    layer.Overlays.Count;
                 overlayIndex++)
            {
                MediaCompositionExportOverlay overlay =
                    layer.Overlays[overlayIndex];
                if (!IsSupportedClip(overlay.Clip) ||
                    overlay.PositionWidth <= 0d ||
                    overlay.PositionHeight <= 0d ||
                    overlay.Opacity is < 0d or > 1d)
                {
                    return false;
                }
            }
        }
        return requiresBaking;
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

        return new MediaCompositionExportCapabilities(
            Id,
            MediaCompositionExportVideoPath.GpuCopy,
            request.EncodingProfile.AudioSubtype is null
                ? MediaCompositionExportAudioPath.None
                : MediaCompositionExportAudioPath.NativeBuffer,
            HardwareVideoEncoderRequested: false,
            HardwareVideoEncoderGuaranteed: false,
            EffectsBakedOnGpu: true,
            Limitation:
                "WebGPU renders into the browser capture canvas through " +
                "an explicit GPU image transfer. MediaRecorder owns " +
                "real-time codec selection and does not expose a hardware " +
                "encoder guarantee.");
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
            return MediaCompositionExportFailure.InvalidProfile;
        }

        int operationId = Interlocked.Increment(
            ref s_nextOperationId);
        Task<int> completion =
            BrowserMediaExportCallbacks.Register(
            operationId,
            progress);
        try
        {
            if (!TryCreateRequestJson(
                    request,
                    out string requestJson))
            {
                return MediaCompositionExportFailure.InvalidProfile;
            }
            using CancellationTokenRegistration cancellation =
                cancellationToken.Register(
                    static id =>
                        CancelCore((int)id!),
                    operationId);
            StartCore(
                operationId,
                requestJson,
                s_shaderSource,
                s_gaussianShaderSource);
            int result = await completion
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result switch
            {
                0 => MediaCompositionExportFailure.None,
                2 => MediaCompositionExportFailure.InvalidProfile,
                3 => MediaCompositionExportFailure.CodecNotFound,
                _ => MediaCompositionExportFailure.Unknown
            };
        }
        finally
        {
            BrowserMediaExportCallbacks.Unregister(
                operationId);
        }
    }

    private bool IsSupportedClip(
        MediaCompositionExportClip clip)
    {
        if (!double.IsFinite(clip.Volume) ||
            clip.Volume is < 0d or > 1d ||
            !TryGetAudioEffectGraph(
                clip.AudioEffectDefinitions,
                out _) ||
            !TryGetVideoEffectPlan(
                clip,
                _effects,
                out _))
        {
            return false;
        }
        return clip.ArgbColor is not null
            ? clip.SourceUri is null
            : clip.SourceUri is { } source &&
              IsBrowserMediaUri(source);
    }

    private static bool IsBrowserMediaUri(Uri source) =>
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

    private bool HasNonIdentityVideoEffect(
        MediaCompositionExportClip clip)
    {
        return TryGetVideoEffectPlan(
                   clip,
                   _effects,
                   out MediaVideoEffectPlan plan) &&
               !plan.IsIdentity;
    }

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
                out double saturation,
                out double grayscale) ||
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
                    saturation:
                        (float)saturation,
                    grayscale:
                        (float)grayscale)
                .Then(declared.ColorTransform);
        plan = new MediaVideoEffectPlan(
            transform,
            declared.BlurStandardDeviation);
        return true;
    }

    private bool TryCreateRequestJson(
        MediaCompositionExportRequest request,
        out string json)
    {
        BrowserStorageServices.TryGetSaveSelection(
            request.DestinationPath,
            out string token,
            out string name);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("token", token);
            writer.WriteString("name", name);
            writer.WriteNumber(
                "width",
                request.EncodingProfile.Width);
            writer.WriteNumber(
                "height",
                request.EncodingProfile.Height);
            writer.WriteNumber(
                "frameRate",
                (double)request.EncodingProfile
                    .FrameRateNumerator /
                request.EncodingProfile
                    .FrameRateDenominator);
            writer.WriteNumber(
                "videoBitrate",
                request.EncodingProfile.VideoBitrate);
            writer.WriteNumber(
                "audioBitrate",
                request.EncodingProfile.AudioBitrate);
            writer.WriteBoolean(
                "includeAudio",
                request.EncodingProfile.AudioSubtype is
                    not null);
            writer.WritePropertyName("clips");
            writer.WriteStartArray();
            foreach (MediaCompositionExportClip clip in
                     request.Clips)
            {
                if (!WriteClip(writer, clip))
                {
                    json = string.Empty;
                    return false;
                }
            }
            writer.WriteEndArray();
            writer.WritePropertyName("backgroundAudio");
            writer.WriteStartArray();
            foreach (MediaCompositionExportAudioTrack track
                     in request.BackgroundAudioTracks)
            {
                if (!TryGetAudioEffectGraph(
                        track.AudioEffectDefinitions,
                        out MediaAudioGraphEffectState[]
                            audioGraph))
                {
                    json = string.Empty;
                    return false;
                }
                writer.WriteStartObject();
                writer.WriteString(
                    "uri",
                    track.SourceUri.AbsoluteUri);
                WriteTime(
                    writer,
                    "duration",
                    track.OriginalDuration -
                    track.TrimTimeFromStart -
                    track.TrimTimeFromEnd);
                WriteTime(
                    writer,
                    "trimStart",
                    track.TrimTimeFromStart);
                WriteTime(
                    writer,
                    "delay",
                    track.Delay);
                writer.WriteNumber(
                    "volume",
                    track.Volume);
                WriteAudioGraph(
                    writer,
                    audioGraph);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("overlayLayers");
            writer.WriteStartArray();
            foreach (MediaCompositionExportOverlayLayer layer
                     in request.OverlayLayers)
            {
                writer.WriteStartArray();
                foreach (MediaCompositionExportOverlay overlay
                         in layer.Overlays)
                {
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
                    writer.WriteNumber("x", overlay.PositionX);
                    writer.WriteNumber("y", overlay.PositionY);
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
                        overlay.AudioEnabled);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        json = Encoding.UTF8.GetString(
            buffer.WrittenSpan);
        return true;
    }

    private bool WriteClip(
        Utf8JsonWriter writer,
        MediaCompositionExportClip clip)
    {
        if (!TryGetAudioEffectGraph(
                clip.AudioEffectDefinitions,
                out MediaAudioGraphEffectState[]
                    audioGraph))
        {
            return false;
        }
        if (!TryGetVideoEffectPlan(
                clip,
                _effects,
                out MediaVideoEffectPlan plan))
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
            writer.WriteNumber("argb", color);
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
        writer.WriteNumber(
            "volume",
            clip.Volume);
        WriteAudioGraph(
            writer,
            audioGraph);
        WriteTransformRow(
            writer,
            "redTransform",
            plan.ColorTransform.Red);
        WriteTransformRow(
            writer,
            "greenTransform",
            plan.ColorTransform.Green);
        WriteTransformRow(
            writer,
            "blueTransform",
            plan.ColorTransform.Blue);
        writer.WriteNumber(
            "blurStandardDeviation",
            plan.BlurStandardDeviation);
        writer.WriteEndObject();
        return true;
    }

    private static void WriteTransformRow(
        Utf8JsonWriter writer,
        string name,
        Vector4 row)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        writer.WriteNumberValue(row.X);
        writer.WriteNumberValue(row.Y);
        writer.WriteNumberValue(row.Z);
        writer.WriteNumberValue(row.W);
        writer.WriteEndArray();
    }

    private bool TryGetAudioEffectGraph(
        IReadOnlyList<MediaCompositionEffectDefinition>
            definitions,
        out MediaAudioGraphEffectState[] states) =>
        MediaAudioGraphEffectResolver
            .TryCaptureBuiltInGraph(
                _effects,
                definitions,
                out states);

    private static void WriteAudioGraph(
        Utf8JsonWriter writer,
        IReadOnlyList<MediaAudioGraphEffectState>
            states)
    {
        writer.WritePropertyName("audioGraph");
        writer.WriteStartArray();
        for (int index = 0;
             index < states.Count;
             index++)
        {
            MediaAudioGraphEffectState state =
                states[index];
            writer.WriteStartArray();
            writer.WriteNumberValue((int)state.Kind);
            writer.WriteNumberValue(state.Parameter0);
            writer.WriteNumberValue(state.Parameter1);
            writer.WriteNumberValue(state.Parameter2);
            writer.WriteNumberValue(state.Parameter3);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    private static void WriteTime(
        Utf8JsonWriter writer,
        string name,
        TimeSpan value) =>
        writer.WriteNumber(
            name,
            value.TotalSeconds);

    [JSImport(
        "startBrowserMediaCompositionExport",
        "progpu-browser")]
    private static partial void StartCore(
        int operationId,
        string requestJson,
        string shaderSource,
        string gaussianShaderSource);

    [JSImport(
        "cancelBrowserMediaCompositionExport",
        "progpu-browser")]
    private static partial void CancelCore(
        int operationId);
}

public static partial class BrowserMediaExportCallbacks
{
    private static readonly ConcurrentDictionary<
        int,
        PendingExport> s_pending = new();

    internal static Task<int> Register(
        int operationId,
        IProgress<double>? progress)
    {
        var pending =
            new PendingExport(progress);
        if (!s_pending.TryAdd(
                operationId,
                pending))
        {
            throw new InvalidOperationException(
                $"Browser media export operation {operationId} is already registered.");
        }
        return pending.Completion.Task;
    }

    internal static void Unregister(
        int operationId) =>
        s_pending.TryRemove(operationId, out _);

    [JSExport]
    public static void DispatchProgress(
        int operationId,
        double progress)
    {
        if (s_pending.TryGetValue(
                operationId,
                out PendingExport? pending))
        {
            pending.Progress?.Report(
                Math.Clamp(progress, 0d, 100d));
        }
    }

    [JSExport]
    public static void DispatchCompletion(
        int operationId,
        int result)
    {
        if (s_pending.TryGetValue(
                operationId,
                out PendingExport? pending))
        {
            pending.Completion.TrySetResult(result);
        }
    }

    private sealed class PendingExport(
        IProgress<double>? progress)
    {
        public IProgress<double>? Progress { get; } =
            progress;

        public TaskCompletionSource<int> Completion
        { get; } = new();
    }
}

public static partial class BrowserMediaExportSmokeTest
{
    private const string AudioGainEffectId =
        "ProGPU.Browser.Smoke.AudioGain";
    private const string AudioBalanceEffectId =
        "ProGPU.Browser.Smoke.AudioBalance";
    private const string GaussianBlurEffectId =
        "ProGPU.Browser.Smoke.GaussianBlur";
    private static readonly Lazy<IDisposable>
        s_audioGainRegistration = new(
            static () =>
                MediaEffectRegistry.Default.Register(
                    new MediaAudioGainEffectFactory(
                        AudioGainEffectId)));
    private static readonly Lazy<IDisposable>
        s_audioBalanceRegistration = new(
            static () =>
                MediaEffectRegistry.Default.Register(
                    new MediaAudioStereoBalanceEffectFactory(
                        AudioBalanceEffectId)));
    private static readonly Lazy<IDisposable>
        s_gaussianBlurRegistration = new(
            static () =>
                MediaEffectRegistry.Default.Register(
                    new MediaVideoGaussianBlurEffectFactory(
                        GaussianBlurEffectId)));

    [JSExport]
    public static async Task<int> RunAsync(
        string sourceUri,
        bool applyEffect,
        bool includeAudio)
    {
        var userData =
            new Dictionary<string, string>
            {
                ["progpu.saturation"] = "1",
                ["progpu.grayscale"] =
                    applyEffect ? "0.5" : "0"
            };
        MediaCompositionExportClip clip =
            new MediaCompositionExportClip(
            new Uri(sourceUri),
            TimeSpan.FromSeconds(5.055),
            TimeSpan.Zero,
            TimeSpan.Zero,
            1d,
            null,
            userData);
        if (applyEffect)
        {
            _ = s_gaussianBlurRegistration.Value;
            clip = clip with
            {
                VideoEffectDefinitions =
                [
                    new MediaCompositionEffectDefinition(
                        GaussianBlurEffectId,
                        new Dictionary<string, object?>
                        {
                            [MediaVideoGaussianBlurEffectFactory
                                .StandardDeviationPropertyName] = 4f
                        })
                ]
            };
        }
        if (includeAudio)
        {
            _ = s_audioGainRegistration.Value;
            _ = s_audioBalanceRegistration.Value;
            clip = clip with
            {
                AudioEffectDefinitions =
                [
                    new MediaCompositionEffectDefinition(
                        AudioGainEffectId,
                        new Dictionary<string, object?>
                        {
                            [MediaAudioGainEffectFactory
                                .GainPropertyName] = 0.5f
                        }),
                    new MediaCompositionEffectDefinition(
                        AudioBalanceEffectId,
                        new Dictionary<string, object?>
                        {
                            [MediaAudioStereoBalanceEffectFactory
                                .BalancePropertyName] = -0.25f
                        })
                ]
            };
        }
        var request =
            new MediaCompositionExportRequest(
                "/tmp/progpu-browser-save/download/ProGPU export.mp4",
                new[] { clip },
                MediaCompositionTrimmingMode.Fast,
                new MediaCompositionEncodingProfile(
                    "MPEG4",
                    "H264",
                    includeAudio ? "AAC" : null,
                    1_280,
                    720,
                    8_000_000,
                    30,
                    1,
                    includeAudio ? 192_000u : 0u,
                    includeAudio ? 48_000u : 0u,
                    includeAudio ? 2u : 0u),
                new Dictionary<string, string>());
        MediaCompositionExportFailure result =
            await MediaCompositionExportRegistry.Default
                .RenderAsync(request)
                .ConfigureAwait(false);
        return (int)result;
    }
}
