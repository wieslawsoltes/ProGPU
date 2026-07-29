using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Samples;
using Silk.NET.Input.Glfw;
using Silk.NET.Windowing.Glfw;
#if MACOS
using AudioToolbox;
using AVFoundation;
using Foundation;
using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using System.Runtime.InteropServices;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;
#endif

namespace ProGPU.Samples.Desktop;

public static class Program
{
    public static void Main(string[] args)
    {
#if MACOS
        using IDisposable mediaRegistration =
            ProGPU.Apple.Media.AppleMedia.Register();
        if (TryGetMediaAudioEffectExportSmokePaths(
                args,
                out string? audioSourcePath,
                out string? audioOutputPath))
        {
            Environment.ExitCode =
                RunMediaAudioEffectExportSmokeAsync(
                    audioSourcePath,
                    audioOutputPath)
                    .GetAwaiter()
                    .GetResult();
            return;
        }
        if (TryGetMediaColorExportSmokePath(
                args,
                out string? smokePath))
        {
            Environment.ExitCode =
                RunMediaColorExportSmokeAsync(smokePath)
                    .GetAwaiter()
                    .GetResult();
            return;
        }
#else
        using IDisposable? mediaRegistration =
            OperatingSystem.IsWindows()
                ? ProGPU.Windows.Media.WindowsMedia.Register()
                : OperatingSystem.IsLinux()
                    ? ProGPU.Linux.Media.LinuxMedia.Register()
                    : null;
#endif
        GlfwWindowing.Use();
        GlfwInput.RegisterPlatform();
        AppBuilder<App>.Configure()
            .WithTitle("ProGPU Substrate - High-Performance WinUI Gallery Dashboard")
            .WithSize(1280, 800)
            .WithGpuContextFactory(CreateDesktopGpuContext)
            .Build()
            .Run(args);
    }

    private static WgpuContext CreateDesktopGpuContext(
        NativeWindowHandle handle,
        uint width,
        uint height)
    {
        if (!handle.IsValid)
        {
            throw new NotSupportedException(
                "The desktop sample requires a native presentation handle.");
        }

        using DawnNativeWindowSource source =
            handle.Kind switch
            {
                NativeWindowKind.Cocoa =>
                    DawnNativeWindowSource
                        .CreateCocoaWindow(
                            handle.Handle),
                NativeWindowKind.Win32 =>
                    DawnNativeWindowSource
                        .CreateWin32(
                            handle.Handle),
                NativeWindowKind.X11 =>
                    DawnNativeWindowSource
                        .CreateXlib(
                            handle.Handle),
                NativeWindowKind.Wayland =>
                    DawnNativeWindowSource
                        .CreateWayland(
                            handle.Display,
                            handle.Handle),
                _ => throw new NotSupportedException(
                    $"Dawn presentation does not support native handle kind {handle.Kind}.")
            };
        DawnGpuContext dawn =
            DawnGpuContext.CreateNativePresentation(source);
        try
        {
            dawn.AttachNativePresentation(
                source,
                width,
                height);
            return dawn.Context;
        }
        catch
        {
            dawn.Dispose();
            throw;
        }
    }

#if MACOS
    private static bool
        TryGetMediaAudioEffectExportSmokePaths(
            IReadOnlyList<string> args,
            out string sourcePath,
            out string outputPath)
    {
        const string option =
            "--media-audio-effect-export-smoke";
        for (int index = 0; index < args.Count; index++)
        {
            if (!string.Equals(
                    args[index],
                    option,
                    StringComparison.Ordinal))
            {
                continue;
            }
            if (index + 1 >= args.Count)
            {
                throw new ArgumentException(
                    $"{option} requires a source MP4 path.");
            }
            sourcePath =
                Path.GetFullPath(args[index + 1]);
            outputPath =
                index + 2 < args.Count
                    ? Path.GetFullPath(args[index + 2])
                    : Path.Combine(
                        Path.GetTempPath(),
                        "ProGPU-media-audio-effect-smoke.mp4");
            return true;
        }
        sourcePath = string.Empty;
        outputPath = string.Empty;
        return false;
    }

    private static async Task<int>
        RunMediaAudioEffectExportSmokeAsync(
            string sourcePath,
            string outputPath)
    {
        const string effectClassId =
            "ProGPU.Sample.ExportGain";
        try
        {
            MediaClip sourceMetadata =
                await MediaClip.CreateFromFileAsync(
                    new StorageFile(sourcePath));
            VideoEncodingProperties video =
                sourceMetadata.GetVideoEncodingProperties();
            TimeSpan duration =
                TimeSpan.FromSeconds(
                    Math.Min(
                        2d,
                        sourceMetadata
                            .OriginalDuration.TotalSeconds));
            var effectRegistry =
                new MediaEffectRegistry();
            var gainFactory =
                new MediaAudioGainEffectFactory(
                    effectClassId);
            using IDisposable effectRegistration =
                effectRegistry.Register(gainFactory);
            var clip =
                new MediaCompositionExportClip(
                    new Uri(sourcePath),
                    sourceMetadata.OriginalDuration,
                    TimeSpan.Zero,
                    sourceMetadata.OriginalDuration -
                        duration,
                    1d,
                    ArgbColor: null,
                    UserData:
                        new Dictionary<string, string>())
                {
                    AudioEffectDefinitions =
                    [
                        new MediaCompositionEffectDefinition(
                            effectClassId,
                            new Dictionary<string, object?>
                            {
                                [MediaAudioGainEffectFactory
                                    .GainPropertyName] = 0d
                            })
                    ]
                };
            var profile =
                new MediaCompositionEncodingProfile(
                    "MPEG4",
                    "H264",
                    "AAC",
                    video.Width == 0 ? 640u : video.Width,
                    video.Height == 0 ? 360u : video.Height,
                    video.Bitrate == 0
                        ? 2_000_000u
                        : video.Bitrate,
                    video.FrameRate.Numerator == 0
                        ? 30u
                        : video.FrameRate.Numerator,
                    video.FrameRate.Denominator == 0
                        ? 1u
                        : video.FrameRate.Denominator,
                    128_000,
                    48_000,
                    2);
            var request =
                new MediaCompositionExportRequest(
                    outputPath,
                    [clip],
                    MediaCompositionTrimmingMode.Precise,
                    profile,
                    new Dictionary<string, string>());
            var provider =
                new ProGPU.Apple.Media
                    .AppleMediaCompositionExportProvider(
                        effects: effectRegistry);
            MediaCompositionExportCapabilities capabilities =
                provider.GetCapabilities(request);
            MediaCompositionExportFailure failure =
                await provider.RenderAsync(
                    request,
                    progress: null,
                    CancellationToken.None);
            if (failure !=
                MediaCompositionExportFailure.None)
            {
                Console.Error.WriteLine(
                    "MEDIA_AUDIO_EFFECT_SMOKE " +
                    $"failure={failure}");
                return 5;
            }

            (double rootMeanSquare,
             long sampleCount,
             int audioTrackCount) =
                ReadAudioRootMeanSquare(outputPath);
            bool valid =
                audioTrackCount == 1 &&
                sampleCount > 0 &&
                rootMeanSquare < 0.001d &&
                capabilities.AudioPath ==
                    MediaCompositionExportAudioPath
                        .NativeBuffer;
            Console.WriteLine(
                "MEDIA_AUDIO_EFFECT_SMOKE " +
                $"valid={valid} " +
                $"path={outputPath} " +
                $"bytes={new FileInfo(outputPath).Length} " +
                $"audioTracks={audioTrackCount} " +
                $"samples={sampleCount} " +
                $"rms={rootMeanSquare:0.########} " +
                $"audioPath={capabilities.AudioPath}");
            return valid ? 0 : 6;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                "MEDIA_AUDIO_EFFECT_SMOKE exception=" +
                exception);
            return 7;
        }
    }

    private static (
        double RootMeanSquare,
        long SampleCount,
        int AudioTrackCount)
        ReadAudioRootMeanSquare(string path)
    {
        using NSUrl url =
            NSUrl.FromFilename(path);
        using var asset = new AVUrlAsset(url);
        AVAssetTrack[] tracks =
            asset.GetTracks(AVMediaTypes.Audio);
        if (tracks.Length == 0)
        {
            return (double.PositiveInfinity, 0, 0);
        }

        using var reader =
            new AVAssetReader(
                asset,
                out NSError? error);
        if (error is not null)
        {
            throw new InvalidOperationException(
                error.LocalizedDescription);
        }
        var settings = new AudioSettings
        {
            Format = AudioFormatType.LinearPCM,
            SampleRate = 48_000,
            NumberChannels = 2,
            LinearPcmBitDepth = 32,
            LinearPcmBigEndian = false,
            LinearPcmFloat = true,
            LinearPcmNonInterleaved = false
        };
        using var output =
            new AVAssetReaderTrackOutput(
                tracks[0],
                settings)
            {
                AlwaysCopiesSampleData = false
            };
        if (!reader.CanAddOutput(output))
        {
            throw new InvalidOperationException(
                "AVAssetReader rejected float PCM output.");
        }
        reader.AddOutput(output);
        if (!reader.StartReading())
        {
            throw new InvalidOperationException(
                reader.Error?.LocalizedDescription ??
                "AVAssetReader failed to start.");
        }

        double sumOfSquares = 0d;
        long sampleCount = 0;
        while (true)
        {
            using CoreMedia.CMSampleBuffer? sample =
                output.CopyNextSampleBuffer();
            if (sample is null)
            {
                break;
            }
            using CoreMedia.CMBlockBuffer? data =
                sample.GetDataBuffer();
            if (data is null ||
                data.DataLength == 0)
            {
                continue;
            }
            CoreMedia.CMBlockBufferError copyError =
                data.CopyDataBytes(
                    0,
                    data.DataLength,
                    out byte[]? bytes);
            if (copyError !=
                    CoreMedia.CMBlockBufferError.None ||
                bytes is null)
            {
                throw new InvalidOperationException(
                    $"PCM readback failed: {copyError}.");
            }
            ReadOnlySpan<float> values =
                MemoryMarshal.Cast<byte, float>(bytes);
            for (int index = 0;
                 index < values.Length;
                 index++)
            {
                double value = values[index];
                sumOfSquares += value * value;
            }
            sampleCount += values.Length;
        }
        if (reader.Status !=
                AVAssetReaderStatus.Completed)
        {
            throw new InvalidOperationException(
                reader.Error?.LocalizedDescription ??
                $"AVAssetReader ended as {reader.Status}.");
        }
        return (
            sampleCount == 0
                ? double.PositiveInfinity
                : Math.Sqrt(sumOfSquares / sampleCount),
            sampleCount,
            tracks.Length);
    }

    private static bool TryGetMediaColorExportSmokePath(
        IReadOnlyList<string> args,
        out string path)
    {
        string? environmentPath =
            Environment.GetEnvironmentVariable(
                "PROGPU_MEDIA_COLOR_EXPORT_SMOKE_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            path = Path.GetFullPath(environmentPath);
            return true;
        }
        const string option =
            "--media-color-export-smoke";
        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            if (string.Equals(
                    argument,
                    option,
                    StringComparison.Ordinal))
            {
                path =
                    index + 1 < args.Count
                        ? Path.GetFullPath(args[index + 1])
                        : Path.Combine(
                            Path.GetTempPath(),
                            "ProGPU-media-color-smoke.mp4");
                return true;
            }
            if (argument.StartsWith(
                    option + "=",
                    StringComparison.Ordinal))
            {
                path = Path.GetFullPath(
                    argument[(option.Length + 1)..]);
                return true;
            }
        }
        path = string.Empty;
        return false;
    }

    private static async Task<int>
        RunMediaColorExportSmokeAsync(string path)
    {
        try
        {
            var effects =
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["progpu.saturation"] = "0.8",
                    ["progpu.grayscale"] = "0.15"
                };
            var mainClip =
                new MediaCompositionExportClip(
                    SourceUri: null,
                    OriginalDuration:
                        TimeSpan.FromSeconds(2),
                    TrimTimeFromStart: TimeSpan.Zero,
                    TrimTimeFromEnd: TimeSpan.Zero,
                    Volume: 1d,
                    ArgbColor: 0xffd02f78,
                    UserData: effects);
            var overlayClip =
                new MediaCompositionExportClip(
                    SourceUri: null,
                    OriginalDuration:
                        TimeSpan.FromSeconds(1),
                    TrimTimeFromStart: TimeSpan.Zero,
                    TrimTimeFromEnd: TimeSpan.Zero,
                    Volume: 0d,
                    ArgbColor: 0xff36d399,
                    UserData:
                        new Dictionary<string, string>());
            var profile =
                new MediaCompositionEncodingProfile(
                    ContainerSubtype: "MPEG4",
                    VideoSubtype: "H264",
                    AudioSubtype: null,
                    Width: 320,
                    Height: 180,
                    VideoBitrate: 1_000_000,
                    FrameRateNumerator: 30_000,
                    FrameRateDenominator: 1_001,
                    AudioBitrate: 0,
                    AudioSampleRate: 0,
                    AudioChannelCount: 0);
            var request =
                new MediaCompositionExportRequest(
                    DestinationPath: path,
                    Clips:
                        Array.AsReadOnly(
                            new[] { mainClip }),
                    TrimmingMode:
                        MediaCompositionTrimmingMode.Precise,
                    EncodingProfile: profile,
                    UserData:
                        new Dictionary<string, string>())
                {
                    OverlayLayers =
                        Array.AsReadOnly(
                            new[]
                            {
                                new MediaCompositionExportOverlayLayer(
                                    Array.AsReadOnly(
                                        new[]
                                        {
                                            new MediaCompositionExportOverlay(
                                                overlayClip,
                                                TimeSpan.FromMilliseconds(500),
                                                200d,
                                                20d,
                                                96d,
                                                96d,
                                                0.8d,
                                                AudioEnabled: false)
                                        }))
                            })
                };
            var provider =
                new ProGPU.Apple.Media
                    .AppleMediaCompositionExportProvider();
            MediaCompositionExportCapabilities capabilities =
                provider.GetCapabilities(request);
            MediaCompositionExportFailure failure =
                await provider.RenderAsync(
                    request,
                    progress: null,
                    CancellationToken.None);
            if (failure !=
                MediaCompositionExportFailure.None)
            {
                Console.Error.WriteLine(
                    $"MEDIA_COLOR_SMOKE failure={failure}");
                return 2;
            }

            using NSUrl url =
                NSUrl.FromFilename(path);
            using var asset = new AVUrlAsset(url);
            int videoTrackCount =
                asset.GetTracks(AVMediaTypes.Video).Length;
            double seconds = asset.Duration.Seconds;
            long bytes = new FileInfo(path).Length;
            MediaClip metadataClip =
                await MediaClip.CreateFromFileAsync(
                    new StorageFile(path));
            VideoEncodingProperties metadata =
                metadataClip.GetVideoEncodingProperties();
            bool valid =
                videoTrackCount == 1 &&
                double.IsFinite(seconds) &&
                seconds >= 1.9d &&
                bytes > 0 &&
                metadataClip.OriginalDuration >=
                    TimeSpan.FromSeconds(1.9) &&
                metadata.Width == 320 &&
                metadata.Height == 180 &&
                string.Equals(
                    metadata.Subtype,
                    "H264",
                    StringComparison.Ordinal) &&
                capabilities.VideoPath ==
                    MediaCompositionExportVideoPath
                        .NativeGpuSurface;
            Console.WriteLine(
                "MEDIA_COLOR_SMOKE " +
                $"valid={valid} " +
                $"path={path} " +
                $"bytes={bytes} " +
                $"duration={seconds:0.######} " +
                $"videoTracks={videoTrackCount} " +
                $"metadata={metadata.Width}x" +
                    $"{metadata.Height}/" +
                    $"{metadata.Subtype} " +
                $"videoPath={capabilities.VideoPath} " +
                $"effectsGpu={capabilities.EffectsBakedOnGpu}");
            return valid ? 0 : 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                "MEDIA_COLOR_SMOKE exception=" +
                exception);
            return 4;
        }
    }
#endif
}
