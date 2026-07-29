using Xunit;

namespace ProGPU.Tests;

public sealed class IosMediaProviderContractTests
{
    [Fact]
    public void IosHostPrefersSameDeviceDawnMetalPresentation()
    {
        string host = ReadRepoFile(
            "src",
            "ProGPU.iOS",
            "IosWindowHost.cs");
        string source = ReadRepoFile(
            "src",
            "ProGPU.Backend.Dawn",
            "DawnNativeWindowSource.cs");
        string presentation = ReadRepoFile(
            "src",
            "ProGPU.Backend.Dawn",
            "DawnNativePresentation.cs");

        Assert.Contains(
            "DawnNativeWindowSource.CreateMetalLayer(",
            host,
            StringComparison.Ordinal);
        Assert.Contains(
            "DawnGpuContext.CreateNativePresentation(source)",
            host,
            StringComparison.Ordinal);
        Assert.Contains(
            "dawnContext.AttachNativePresentation(",
            host,
            StringComparison.Ordinal);
        Assert.Contains(
            "SurfaceSourceMetalLayerFFI",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SharedTextureMemoryIOSurface",
            presentation,
            StringComparison.Ordinal);
        Assert.Contains(
            "SharedFenceMTLSharedEvent",
            presentation,
            StringComparison.Ordinal);
        Assert.Contains(
            "@rpath/webgpu_dawn.framework/webgpu_dawn",
            ReadRepoFile(
                "src",
                "ProGPU.Backend.Dawn",
                "DawnGpuContext.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeLibrary.SetDllImportResolver(",
            ReadRepoFile(
                "src",
                "ProGPU.Backend.Dawn",
                "DawnGpuContext.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void IosPackagingDistinguishesDawnFromUiOnlyFallback()
    {
        string project = ReadRepoFile(
            "src",
            "ProGPU.iOS",
            "ProGPU.iOS.csproj");
        string targets = ReadRepoFile(
            "src",
            "ProGPU.iOS",
            "buildTransitive",
            "ProGPU.iOS.targets");
        string dawnBuild = ReadRepoFile(
            "eng",
            "build-webgpu-dawn-ios.sh");

        Assert.Contains(
            "ProGpuDawnIOSXCFramework",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "webgpu_dawn.xcframework",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGpuDawnIOSXCFramework",
            targets,
            StringComparison.Ordinal);
        Assert.Contains(
            "zero-copy media",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "--directory \"${source_dir}\"",
            dawnBuild,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"${expected_commit}^{commit}\"",
            dawnBuild,
            StringComparison.Ordinal);
        Assert.Contains(
            "01249a97332468dbdd6cf5edb8dd7bae77875de5",
            dawnBuild,
            StringComparison.Ordinal);
        Assert.Contains(
            "refs/heads/chromium/7871_124",
            dawnBuild,
            StringComparison.Ordinal);
        Assert.Contains(
            "dawn-webgpusharp-0.5.5-src",
            dawnBuild,
            StringComparison.Ordinal);
        Assert.Contains(
            "verify_webgpusharp_abi_header",
            dawnBuild,
            StringComparison.Ordinal);
        Assert.Contains(
            "WGPUSType_SurfaceSourceMetalLayer",
            dawnBuild,
            StringComparison.Ordinal);
        Assert.Contains(
            "-DTINT_BUILD_IR_BINARY=OFF",
            dawnBuild,
            StringComparison.Ordinal);
        Assert.Contains(
            "-DDAWN_BUILD_MONOLITHIC_LIBRARY=STATIC",
            dawnBuild,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "find \"${build_dir}\"",
            dawnBuild,
            StringComparison.Ordinal);
        Assert.Contains(
            "create_dynamic_framework",
            dawnBuild,
            StringComparison.Ordinal);
        Assert.Contains(
            "-install_name \"@rpath/webgpu_dawn.framework/webgpu_dawn\"",
            dawnBuild,
            StringComparison.Ordinal);
        Assert.Contains(
            "_wgpuDeviceImportSharedTextureMemory",
            dawnBuild,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGpuRequireZeroCopyMedia",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<SmartLink>false</SmartLink>",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<IsCxx>true</IsCxx>",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<SmartLink>false</SmartLink>",
            targets,
            StringComparison.Ordinal);
        Assert.Contains(
            "<IsCxx>true</IsCxx>",
            targets,
            StringComparison.Ordinal);

        string sampleProject = ReadRepoFile(
            "src",
            "ProGPU.Samples.iOS",
            "ProGPU.Samples.iOS.csproj");
        Assert.Contains(
            "AdditionalProperties=\"ProGpuSamplesMobile=true\"",
            sampleProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "<PackageReference Include=\"WebGPUSharp\"",
            sampleProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExcludeAssets=\"native\"",
            sampleProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "'$(RuntimeIdentifier)' == 'iossimulator-arm64'",
            sampleProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "<UseInterpreter Condition=",
            sampleProject,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MacDesktopHostUsesSameDeviceDawnMetalPresentation()
    {
        string project = ReadRepoFile(
            "src",
            "ProGPU.Samples.Desktop",
            "ProGPU.Samples.Desktop.csproj");
        string program = ReadRepoFile(
            "src",
            "ProGPU.Samples.Desktop",
            "Program.cs");
        string source = ReadRepoFile(
            "src",
            "ProGPU.Backend.Dawn",
            "DawnNativeWindowSource.cs");

        Assert.Contains(
            "net10.0-macos",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU.Backend.Dawn.csproj",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU.Apple.Media.csproj",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<PackageReference Include=\"WebGPUSharp\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            ".WithGpuContextFactory(CreateDesktopGpuContext)",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            ".CreateCocoaWindow(",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "DawnGpuContext.CreateNativePresentation(source)",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "dawn.AttachNativePresentation(",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateCocoaWindow(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppleCompositionExportUsesNativeAvFoundationOverlaysAndBakesBuiltInEffectsOnGpu()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Apple.Media",
            "AppleMediaCompositionExportProvider.cs");
        string registration = ReadRepoFile(
            "src",
            "ProGPU.Apple.Media",
            "AppleMediaPlaybackProvider.cs");

        Assert.Contains(
            "new AVMutableComposition()",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "composition.Insert(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AVAssetExportSession(composition, preset)",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "AVMutableVideoComposition",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "AVMutableVideoCompositionLayerInstruction",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddOverlayTracks(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "AVMutableVideoComposition.GetVideoComposition(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new CIColorMatrix",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MTLDevice.SystemDefault",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "PrepareEffectRequestAsync(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "IMediaCompositionExportCapabilityProvider",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCompositionExportVideoPath" +
            ".NativeGpuSurface",
            provider.Replace(
                "\r",
                string.Empty,
                StringComparison.Ordinal)
                .Replace(
                    "\n",
                    string.Empty,
                    StringComparison.Ordinal)
                .Replace(
                    " ",
                    string.Empty,
                    StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "EffectsBakedOnGpu: effectsBakedOnGpu",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "effects.Saturation *",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "(1f - effects.Grayscale)",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "clip.VideoEffectDefinitions.Count != 0",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "layer.CustomCompositorDefinition is not null",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HasNonIdentityWebGpuEffect",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCompositionExportRegistry.Default.Register(",
            registration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ffmpeg",
            provider,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplePlaybackProcessesTypedAudioEffectsInsideAvFoundation()
    {
        string graph = ReadRepoFile(
            "src",
            "ProGPU.Apple.Media",
            "AppleAudioEffectGraph.cs");
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Apple.Media",
            "AppleMediaPlaybackProvider.cs");

        Assert.Contains(
            "MTAudioProcessingTap",
            graph,
            StringComparison.Ordinal);
        Assert.Contains(
            "AppleAudioTapNative.Create(this)",
            graph,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetAudioTapProcessor(",
            graph,
            StringComparison.Ordinal);
        Assert.Contains(
            "MTAudioProcessingTapGetSourceAudio(",
            graph,
            StringComparison.Ordinal);
        Assert.Contains(
            "Finalize = &Finalize",
            graph,
            StringComparison.Ordinal);
        Assert.Contains(
            "GCHandle.FromIntPtr(storage)",
            graph,
            StringComparison.Ordinal);
        Assert.Contains(
            "processors[index].Process(",
            graph,
            StringComparison.Ordinal);
        Assert.Contains(
            "effect is IMediaAudioEffect",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AVAudioEngine",
            graph,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new MTAudioProcessingTap(",
            graph,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ffmpeg",
            graph,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplePlaybackLoopsThroughSharedEngineWithoutClearingLastFrame()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Apple.Media",
            "AppleMediaPlaybackProvider.cs");

        Assert.Contains(
            "AVPlayerActionAtItemEnd.None",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "SignalEndedOnce();",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "Interlocked.Exchange(ref _endSignaled, 1)",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ToTimeSpan(player.CurrentTime)",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "_seekInProgress = player is not null;",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!seekInProgress)",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "generation != _seekGeneration",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "_sink.Ended();",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "VideoSurface.Clear()",
            provider,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppleCompositionExportProcessesRegisteredAudioEffectsInNativeMixTap()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Apple.Media",
            "AppleMediaCompositionExportProvider.cs");
        string graph = ReadRepoFile(
            "src",
            "ProGPU.Apple.Media",
            "AppleExportAudioEffectGraph.cs");
        string desktop = ReadRepoFile(
            "src",
            "ProGPU.Samples.Desktop",
            "Program.cs");

        Assert.Contains(
            "AreAudioEffectsRegistered(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AppleExportAudioEffectGraph(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "clip.AudioEffectDefinitions",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "clip.AudioEffectDefinitions.Count != 0",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new MediaAudioTimelineProcessor(",
            graph,
            StringComparison.Ordinal);
        Assert.Contains(
            "tap.AttachTo(track.Parameters)",
            graph,
            StringComparison.Ordinal);
        Assert.Contains(
            ".HasUnsupportedFormat == true",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaEffectKind.Audio",
            graph,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ffmpeg",
            graph,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "--media-audio-effect-export-smoke",
            desktop,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReadAudioRootMeanSquare(",
            desktop,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppleGeneratedColorsUseMetalWriterPoolAndExactClock()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Apple.Media",
            "AppleMediaCompositionExportProvider.cs");
        string desktop = ReadRepoFile(
            "src",
            "ProGPU.Samples.Desktop",
            "Program.cs");

        Assert.Contains(
            "HasExactlyOneVideoSource(clip)",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AVAssetWriter(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AVAssetWriterInputPixelBufferAdaptor(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "adaptor.PixelBufferPool",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "pool.CreatePixelBuffer()",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "CIContext.FromMetalDevice(device)",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "context.Render(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "AppendPixelBufferWithPresentationTime(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "frameIndex *" +
            Environment.NewLine +
            "                    (long)profile." +
            "FrameRateDenominator",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "checked((int)profile.FrameRateNumerator)",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "Int128 scaled",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "writer.EndSessionAtSourceTime(",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LockBaseAddress",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GetBaseAddress",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            provider,
            StringComparison.Ordinal);

        Assert.Contains(
            "PROGPU_MEDIA_COLOR_EXPORT_SMOKE_PATH",
            desktop,
            StringComparison.Ordinal);
        Assert.Contains(
            "MEDIA_COLOR_SMOKE",
            desktop,
            StringComparison.Ordinal);
        Assert.Contains(
            "videoTrackCount == 1",
            desktop,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCompositionExportVideoPath" +
            Environment.NewLine +
            "                        .NativeGpuSurface",
            desktop,
            StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] pathParts)
    {
        for (DirectoryInfo? directory =
                 new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string candidate =
                Path.Combine(
                    [directory.FullName, .. pathParts]);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{Path.Combine(pathParts)}'.");
    }
}
