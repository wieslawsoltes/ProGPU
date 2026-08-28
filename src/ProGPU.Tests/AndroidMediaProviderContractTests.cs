using Xunit;

namespace ProGPU.Tests;

public sealed class AndroidMediaProviderContractTests
{
    [Fact]
    public void AndroidProviderUsesPlatformDecodeAndGpuSampleableImages()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaPlaybackProvider.cs");

        Assert.Contains(
            "new global::Android.Media.MediaPlayer()",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ImageReader.NewInstance(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "UsageGpuSampledImage",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGpuExternalTextureHandleKind",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            ".AndroidHardwareBuffer",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaTransferMode.NativeZeroCopy",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TextureWrite",
            provider,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidFrameOwnerPreventsImageReaderReuseDuringDawnAccess()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaPlaybackProvider.cs");
        string dawn = ReadRepoFile(
            "src",
            "ProGPU.Backend.Dawn",
            "DawnGpuContext.cs");

        Assert.Contains(
            "AHardwareBuffer_acquire",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "AHardwareBuffer_release",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "private Image? _image;",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "image?.Close();",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "SharedTextureMemory.ImportAHardwareBuffer(",
            dawn,
            StringComparison.Ordinal);
        Assert.True(
            dawn.IndexOf(
                "sharedMemory?.EndAccess(_texture);",
                StringComparison.Ordinal) <
            dawn.IndexOf(
                "nativeOwner?.Dispose();",
                StringComparison.Ordinal));
    }

    [Fact]
    public void AndroidSampleRegistersProviderAndNetworkCapability()
    {
        string program = ReadRepoFile(
            "src",
            "ProGPU.Samples.Android",
            "Program.cs");
        string project = ReadRepoFile(
            "src",
            "ProGPU.Samples.Android",
            "ProGPU.Samples.Android.csproj");

        Assert.Contains(
            "AndroidMedia.Register()",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "UsesPermission(Android.Manifest.Permission.Internet)",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU.Android.Media.csproj",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<PackageReference Include=\"WebGPUSharp\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExcludeAssets=\"native\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "AdditionalProperties=\"ProGpuSamplesMobile=true\"",
            project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidDawnBuildPackagesOnePinnedVulkanAbiPerArchitecture()
    {
        string script = ReadRepoFile(
            "eng",
            "build-webgpu-dawn-android.sh");
        string cmake = ReadRepoFile(
            "eng",
            "dawn-android",
            "CMakeLists.txt");
        string exports = ReadRepoFile(
            "eng",
            "dawn-android",
            "WebGpuDawn.exports");
        string project = ReadRepoFile(
            "src",
            "ProGPU.Android",
            "ProGPU.Android.csproj");

        Assert.Contains(
            "01249a97332468dbdd6cf5edb8dd7bae77875de5",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "refs/heads/chromium/7871_124",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "dawn-webgpusharp-0.5.5-src",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "verify_webgpusharp_abi_header",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "WGPUSType_SurfaceSourceAndroidNativeWindow",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "webgpusharp-abi=0.5.5",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "--directory \"${source_dir}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"${expected_commit}^{commit}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "android.toolchain.cmake",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "-DANDROID_ABI=\"${android_abi}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "-DANDROID_STL=c++_static",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "DAWN_ENABLE_VULKAN ON",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "DAWN_ENABLE_OPENGLES OFF",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "TINT_BUILD_IR_BINARY OFF",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"-Wl,--whole-archive\"",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "WebGpuDawn.exports",
            cmake,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--exclude-libs",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "-Wl,-z,max-page-size=16384",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "wgpu*;",
            exports,
            StringComparison.Ordinal);
        Assert.Contains(
            "wgpuDeviceImportSharedTextureMemory",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "wgpuSharedTextureMemoryBeginAccess",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "libc\\+\\+_shared",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "libwebgpu_dawn.so",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGpuRequireZeroCopyMedia",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "build-webgpu-dawn-android.sh",
            project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidHostUsesOneDawnDeviceForPresentationAndMediaImport()
    {
        string host = ReadRepoFile(
            "src",
            "ProGPU.Android",
            "AndroidWindowHost.cs");
        string project = ReadRepoFile(
            "src",
            "ProGPU.Android",
            "ProGPU.Android.csproj");

        Assert.Contains(
            "DawnGpuContext.CreateNativePresentation(source)",
            host,
            StringComparison.Ordinal);
        Assert.Contains(
            "dawnContext.AttachNativePresentation(",
            host,
            StringComparison.Ordinal);
        Assert.Contains(
            "existingDawn.AttachNativePresentation(",
            host,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGpuDawnAndroidRoot",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "libwebgpu_dawn.so",
            project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidGainUsesPerSessionNativeAudioEffect()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaPlaybackProvider.cs");

        Assert.Contains(
            "effect is not IMediaAudioGraphEffect",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new LoudnessEnhancer(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "player.AudioSessionId",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "2000d *",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "enhancer.SetTargetGain(millibels);",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "Effect.StateChanged += _changed;",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaAudioGraphEffectKind\n                    .StereoBalance",
            provider.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "GetCombinedAudioLevels()",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "levels.Left /\n            nativeBoost",
            provider.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidPlaybackProjectsOnlyParsedNativeTimedTextAsCues()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaPlaybackProvider.cs");

        Assert.Contains(
            "IMediaPlaybackTimedMetadataProvider",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaTrackType.Timedtext or",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaTrackType.Subtitle",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "player.TimedText += OnTimedText;",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "args.Text?.Text",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaPlaybackTimedTextCueAccumulator",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "_sink.UpdateTimedMetadataCues(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaPlaybackTrackSupport\n                                .Unsupported",
            provider.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "mode ==\n                MediaPlaybackTimedMetadataPresentationMode\n                    .PlatformPresented",
            provider.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OnSubtitleData",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System.Reflection",
            provider,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidPreciseExporterUsesNativeSurfaceCodecAndMuxer()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaCodecCompositionExportProvider.cs");
        string audio = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaCodecCompositionAudio.cs");
        string timelineMixer = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaCodecAudioTimelineMixer.cs");
        string pcmMixer = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidPcm16Mixer.cs");
        string overlayPlanner = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaCodecOverlayPlanner.cs");
        string videoEffectPlanner = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaCodecVideoEffectPlanner.cs");
        string overlayComposer = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaCodecOverlayFrameComposer.cs");
        string gpuSink = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaCodecGpuEncoderFrameSink.cs");
        string registration = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaPlaybackProvider.cs");
        string vertex = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "Shaders",
            "AndroidMediaCompositionVertex.glsl");
        string fragment = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "Shaders",
            "AndroidMediaCompositionFragment.glsl");

        Assert.Contains(
            "AndroidMediaCodecCompositionExportProvider",
            registration,
            StringComparison.Ordinal);
        Assert.True(
            registration.IndexOf(
                "AndroidMediaCodecCompositionExportProvider",
                StringComparison.Ordinal) <
            registration.IndexOf(
                "IsoBmffFastMediaCompositionExportProvider",
                StringComparison.Ordinal));
        Assert.Contains(
            "MediaExtractor",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCodec.CreateDecoderByType(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "encoder.CreateInputSurface()",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new SurfaceTexture(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "EGLExt.EglPresentationTimeANDROID(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new MediaMuxer(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCompositionExportVideoPath.NativeGpuSurface",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCompositionExportVideoPath.GpuCopy",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "HasActiveVulkanDawnContext()",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AndroidMediaCodecGpuEncoderFrameSink(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "RenderColorClip(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "renderer.DrawColorFrame(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetNextColorFrameTimestampMicroseconds(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCompositionVideoEffectResolver",
            videoEffectPlanner,
            StringComparison.Ordinal);
        Assert.Contains(
            ".TryCapturePlan(",
            videoEffectPlanner,
            StringComparison.Ordinal);
        Assert.Contains(
            "u_red_transform",
            fragment,
            StringComparison.Ordinal);
        Assert.Contains(
            "u_green_transform",
            fragment,
            StringComparison.Ordinal);
        Assert.Contains(
            "u_blue_transform",
            fragment,
            StringComparison.Ordinal);
        Assert.Contains(
            "u_use_solid_color",
            fragment,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCompositionExportAudioPath" +
            ".CompressedSampleCopy",
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
            "MediaCompositionExportAudioPath" +
            ".NativeBuffer",
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
            "MediaCompositionExportAudioPath" +
            ".CpuBuffer",
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
            "BakeAudioTimeline(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "request.BackgroundAudioTracks.Count != 0)",
            audio,
            StringComparison.Ordinal);
        Assert.Contains(
            "request.BackgroundAudioTracks.Count;",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "request.OverlayLayers.Count != 0",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "AndroidMediaCodecOverlayPlanner.TryCapture(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AndroidMediaCodecOverlayFrameComposer(",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "request.Clips.Count == 0 ||\n" +
            "            request.OverlayLayers.Count != 0",
            provider.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            ".android.audio.tmp.mp4",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCodec.CreateEncoderByType(",
            audio,
            StringComparison.Ordinal);
        Assert.Contains(
            "decoder.GetOutputBuffer(",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "encoder.GetInputBuffer(",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "JNIEnv.GetDirectBufferAddress(",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "AndroidPcm16Mixer.FramesPerBlock",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "stackalloc long[",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "stackalloc float[",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaAudioEffectProcessorChain",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetWritableDirectPcm16Span(",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaAudioGraphEffectResolver" +
            "\n                .TryCaptureCombinedStereoLevels(",
            audio.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "AndroidPcm16Mixer.WriteSaturated(",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "AndroidPcm16Mixer.AddProcessed(",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaPcm16FloatConverter",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "overlay.AudioEnabled",
            audio,
            StringComparison.Ordinal);
        Assert.Contains(
            "request.OverlayLayers.Count;",
            audio,
            StringComparison.Ordinal);
        Assert.Contains(
            "AudioEnabled: true",
            ReadRepoFile(
                "src",
                "ProGPU.Tests",
                "AndroidMediaAudioPlannerTests.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaPcm16ProcessedAccumulator.AddMono(",
            pcmMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaPcm16ProcessedAccumulator.AddStereo(",
            pcmMixer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AddProcessedSample(",
            pcmMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "_extractor?.Release();",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "_decoder?.Release();",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Activator.",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Assembly.Load",
            timelineMixer,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureLayerPlacement",
            overlayPlanner,
            StringComparison.Ordinal);
        Assert.Contains(
            "layer.CustomCompositorDefinition is not null",
            overlayPlanner,
            StringComparison.Ordinal);
        Assert.Contains(
            "Array order is the declared layer/overlay back-to-front order",
            overlayPlanner,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCodec.CreateDecoderByType(",
            overlayComposer,
            StringComparison.Ordinal);
        Assert.Contains(
            "private int _heldOutputIndex = -1;",
            overlayComposer,
            StringComparison.Ordinal);
        Assert.Contains(
            "sink.UpdateOverlayDecoderInput(",
            overlayComposer,
            StringComparison.Ordinal);
        Assert.Contains(
            "Input.HasCurrentImage",
            overlayComposer,
            StringComparison.Ordinal);
        Assert.Contains(
            "sink.CompositeDecodedLayer(",
            overlayComposer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            overlayComposer,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureLayerCompositor",
            gpuSink,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateOverlayDecoderInput(",
            gpuSink,
            StringComparison.Ordinal);
        Assert.Contains(
            "updateFromProducer: false",
            gpuSink,
            StringComparison.Ordinal);
        Assert.Contains(
            "Android Media Overlay Effect Output",
            gpuSink,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShaderResource.Load(",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReadPixels",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TextureWrite",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WaitHandle.WaitAny",
            provider,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "// Algorithm:",
            vertex,
            StringComparison.Ordinal);
        Assert.Contains(
            "// Time complexity:",
            fragment,
            StringComparison.Ordinal);
        Assert.Contains(
            "// Space complexity:",
            fragment,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidPreciseExporterIsTransactionalAndBounded()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaCodecCompositionExportProvider.cs");

        Assert.Contains(
            ".android.tmp.mp4",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "File.Move(temporary, destination, overwrite: true)",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaximumCompressedAudioSample",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MinimumDelta = 0.5d",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "cancellationToken.ThrowIfCancellationRequested()",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "extractor.Release();",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "encoder?.Release();",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "muxer?.Release();",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ffmpeg",
            provider,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AndroidCompositionThumbnailsReuseNativeBatchStateAndGpuEffects()
    {
        string provider = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaCompositionThumbnailProvider.cs");
        string registration = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaPlaybackProvider.cs");
        string project = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "ProGPU.Android.Media.csproj");

        Assert.Contains(
            "IMediaCompositionThumbnailProvider",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AndroidMediaCompositionThumbnailProvider(",
            registration,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCompositionThumbnailRegistry.Default.Register(",
            registration,
            StringComparison.Ordinal);
        Assert.Contains(
            "new MediaMetadataRetriever?[",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetScaledFrameAtTime(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "Option.ClosestSync",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            ".CreateRenderer(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "renderer.DrawFrame(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "renderer.DrawColorFrame(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryGetVideoEffectPlan(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "HasSpatialEffects(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "AndroidMediaCodecOverlayPlanner.TryCapture(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AndroidMediaCodecOverlayFrameComposer(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "Array.Sort(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "orderedPositions",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "renderer.DrawFrame(\n" +
            "                        presentationTimeMicroseconds,\n" +
            "                        effectPlan,\n" +
            "                        overlays,",
            provider.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "composition.OverlayLayers.Count != 0 ||",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ImageReader.NewInstance(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "AcquireNextImage()",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "Bitmap.CompressFormat.Png",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU.Media.Editing.csproj",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ffmpeg",
            provider,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AndroidWebGpuEncoderSinkUsesBoundedBidirectionalFences()
    {
        string sink = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "AndroidMediaCodecGpuEncoderFrameSink.cs");
        string vertex = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "Shaders",
            "AndroidHardwareBufferBlitVertex.glsl");
        string fragment = ReadRepoFile(
            "src",
            "ProGPU.Android.Media",
            "Shaders",
            "AndroidHardwareBufferBlitFragment.glsl");

        Assert.Contains(
            "IMediaGpuEncoderFrameSink",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "IAndroidEncoderSurfaceRenderer",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int TargetCount = 3;",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "private readonly SourceSlot[] _sourceSlots;",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "HardwareBufferFormat.Rgba8888",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "UsageGpuColorOutput",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryImportAHardwareBufferRenderTarget",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "EndAccessAndExportSyncFd",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "BeginAccessAndConsumeSyncFd",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "StageDecoderFrame(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureBlitter.Blit(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureGaussianBlur.Blur(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "Android Media Gaussian Intermediate",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "GpuTextureClearer.Clear(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "DrawColorFrame(",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "eglWaitSyncKHR",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "eglDupNativeFenceFDANDROID",
            sink,
            StringComparison.Ordinal);
        Assert.Contains(
            "EGLExt.EglPresentationTimeANDROID",
            sink,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GlFinish",
            sink,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WaitIdle(",
            sink,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PollDevice(",
            sink,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReadPixels",
            sink,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.Copy",
            sink,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "// Algorithm:",
            vertex,
            StringComparison.Ordinal);
        Assert.Contains(
            "// Time complexity:",
            fragment,
            StringComparison.Ordinal);
        Assert.Contains(
            "// Space complexity:",
            fragment,
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
