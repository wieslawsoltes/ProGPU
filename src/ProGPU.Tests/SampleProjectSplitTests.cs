using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Samples;
using Xunit;

namespace ProGPU.Tests;

public sealed class SampleProjectSplitTests
{
    [Fact]
    public void SharedGalleryIsLibraryAndThinHostsReferenceIt()
    {
        var shared = Read("src", "ProGPU.Samples", "ProGPU.Samples.csproj");
        var desktop = Read("src", "ProGPU.Samples.Desktop", "ProGPU.Samples.Desktop.csproj");
        var browser = Read("src", "ProGPU.Samples.Browser", "ProGPU.Samples.Browser.csproj");
        var winUi = Read("src", "ProGPU.WinUI", "ProGPU.WinUI.csproj");

        Assert.DoesNotContain("<OutputType>Exe</OutputType>", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("<ApplicationManifest>", shared, StringComparison.Ordinal);
        Assert.Contains("ProGPU.Media.Editing.csproj", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("ProGPU.Media.Editing.csproj", winUi, StringComparison.Ordinal);
        Assert.Contains("ProGPU.Samples.csproj", desktop, StringComparison.Ordinal);
        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", desktop, StringComparison.Ordinal);
        Assert.Contains("Microsoft.NET.Sdk.WebAssembly", browser, StringComparison.Ordinal);
        Assert.Contains("ProGPU.Media.Editing.csproj", browser, StringComparison.Ordinal);
        Assert.Contains("ProGPU.WinRT.csproj", browser, StringComparison.Ordinal);
        Assert.Contains("ProGPU.Samples.csproj", browser, StringComparison.Ordinal);
        Assert.Contains("ProGPU.Browser.csproj", browser, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerCanvasResizeNeverMutatesTransferredHtmlCanvas()
    {
        var browserAsset = Read("src", "ProGPU.Browser", "BrowserAssets", "progpu-browser.js");

        Assert.Contains(
            "if (state.worker) {\n    if (changed) state.worker.postMessage({ type: 'resize', width, height });\n    return;\n  }",
            browserAsset.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserDiagnosticsAreHiddenByDefaultAndExposedInSampleSettings()
    {
        var html = Read("src", "ProGPU.Samples.Browser", "wwwroot", "index.html");
        var css = Read("src", "ProGPU.Samples.Browser", "wwwroot", "styles.css");
        var browserAsset = Read("src", "ProGPU.Browser", "BrowserAssets", "progpu-browser.js");
        var settings = Read("src", "ProGPU.Samples", "Pages", "SettingsPage.cs");

        Assert.Contains("id=\"diagnostics\" aria-live=\"polite\" hidden", html, StringComparison.Ordinal);
        Assert.Contains("#diagnostics[hidden]", css, StringComparison.Ordinal);
        Assert.Contains("function initializeDiagnosticsVisibility()", browserAsset, StringComparison.Ordinal);
        Assert.Contains("function setDiagnosticsVisible(visible)", browserAsset, StringComparison.Ordinal);
        Assert.Contains("if (isError) applyDiagnosticsVisibility(true, false);", browserAsset, StringComparison.Ordinal);
        Assert.Contains("Show Browser WebGPU Diagnostics", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserDiagnosticsToggleUpdatesHostPreference()
    {
        var diagnosticsVisible = false;
        SamplePlatformServices.GetBrowserDiagnosticsVisible = () => diagnosticsVisible;
        SamplePlatformServices.SetBrowserDiagnosticsVisible = value => diagnosticsVisible = value;
        try
        {
            var page = SettingsPage.Create();
            var toggle = Assert.IsType<ToggleSwitch>(FindByName(page, "BrowserDiagnosticsToggle"));

            Assert.False(toggle.IsOn);
            toggle.IsOn = true;
            Assert.True(diagnosticsVisible);
        }
        finally
        {
            SamplePlatformServices.GetBrowserDiagnosticsVisible = null;
            SamplePlatformServices.SetBrowserDiagnosticsVisible = null;
        }
    }

    [Fact]
    public void BrowserKeepsLiveStatusBarWithoutRebuildingItsInlineTree()
    {
        var browserProgram = Read("src", "ProGPU.Samples.Browser", "Program.cs");
        var mainWindow = Read("src", "ProGPU.Samples", "Windows", "MainWindowController.cs");

        Assert.DoesNotContain("EnableLivePerformanceStatus", browserProgram, StringComparison.Ordinal);
        Assert.DoesNotContain("_statsText.Inlines.Clear()", mainWindow, StringComparison.Ordinal);
        Assert.Contains("AppState._statsFpsRun!.Text", mainWindow, StringComparison.Ordinal);
        Assert.Contains("new ThemeResourceBrush(\"SystemAccentColor\")", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserTextInputSinkCanReceiveFocusWithoutBeingAriaHidden()
    {
        var browserAsset = Read("src", "ProGPU.Browser", "BrowserAssets", "progpu-browser.js");

        Assert.DoesNotContain("textSink.setAttribute('aria-hidden'", browserAsset, StringComparison.Ordinal);
        Assert.Contains("textSink.setAttribute('aria-label', 'ProGPU canvas keyboard input');", browserAsset, StringComparison.Ordinal);
        Assert.Contains("textSink.tabIndex = -1;", browserAsset, StringComparison.Ordinal);
        Assert.Contains("textSink.focus({ preventScroll: true });", browserAsset, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserTerminalPointersFlushQueuedMovesBeforeImmediateDispatch()
    {
        var browserAsset = Read("src", "ProGPU.Browser", "BrowserAssets", "progpu-browser.js");
        var dispatcher = Read("src", "ProGPU.Browser", "BrowserInputDispatcher.cs");

        Assert.Contains("function flushQueuedPointerMoves(pointerId)", browserAsset, StringComparison.Ordinal);
        Assert.Contains("state.dispatchImmediatePointer(queued.kind, queued.x, queued.y, queued.button", browserAsset, StringComparison.Ordinal);
        Assert.Contains("function dispatchTerminalPointerEvent(kind, event, point)", browserAsset, StringComparison.Ordinal);
        Assert.Contains("dispatchTerminalPointerEvent(3, event, point);", browserAsset, StringComparison.Ordinal);
        Assert.Contains("dispatchTerminalPointerEvent(9, event, point);", browserAsset, StringComparison.Ordinal);
        Assert.Contains("kind != (int)BrowserInputKind.PointerMove", dispatcher, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserTextSinkIncludesCanvasOriginBeforeViewportClamping()
    {
        var browserAsset = Read("src", "ProGPU.Browser", "BrowserAssets", "progpu-browser.js");

        Assert.Contains("const canvasBounds = state.canvas?.getBoundingClientRect();", browserAsset, StringComparison.Ordinal);
        Assert.Contains("const sinkX = (canvasBounds?.left || 0) + bounds.x;", browserAsset, StringComparison.Ordinal);
        Assert.Contains("const sinkY = (canvasBounds?.top || 0) + bounds.y + bounds.height;", browserAsset, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserTextSinkRepositionsWithVisualViewport()
    {
        var browserAsset = Read("src", "ProGPU.Browser", "BrowserAssets", "progpu-browser.js");

        Assert.Contains("textInputBounds: null", browserAsset, StringComparison.Ordinal);
        Assert.Contains("function positionTextInput()", browserAsset, StringComparison.Ordinal);
        Assert.Contains("resizeCanvas();\n  positionTextInput();", browserAsset, StringComparison.Ordinal);
        Assert.Contains("state.textInputBounds = { x, y, width, height };", browserAsset, StringComparison.Ordinal);
        Assert.Contains("state.textInputBounds = null;", browserAsset, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserHostRegistersBundledInterForSkiaSharpDefaults()
    {
        var browserHost = Read("src", "ProGPU.Browser", "BrowserWindowHost.cs");
        var typeface = Read("src", "SkiaSharp", "SKTypeface.cs");

        Assert.Contains("InterFontFamily.RegisterFonts();", browserHost, StringComparison.Ordinal);
        Assert.Contains("NotoFontFamily.RegisterFallbacks();", browserHost, StringComparison.Ordinal);
        Assert.Contains("var fallbackFont = InterFontFamily.Regular;", browserHost, StringComparison.Ordinal);
        Assert.Contains("FontApi.RegisterPlatformFallbackFont(fallbackFont);", browserHost, StringComparison.Ordinal);
        Assert.Contains("ResolveDefaultTypeface(FontApi.GetSystemFonts(), FontApi.PlatformFallbackFont)", typeface, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserFrameSchedulerHonorsVSyncAndUsesRollingGpuCompletionWindow()
    {
        var browserAsset = Read("src", "ProGPU.Browser", "BrowserAssets", "progpu-browser.js");
        var browserHost = Read("src", "ProGPU.Browser", "BrowserWindowHost.cs");

        Assert.Contains("function nextAnimationFrame(vsync)", browserAsset, StringComparison.Ordinal);
        Assert.Contains("if (vsync) return new Promise(resolve => requestAnimationFrame(resolve));", browserAsset, StringComparison.Ordinal);
        Assert.Contains("const uncappedFrameChannel = new MessageChannel();", browserAsset, StringComparison.Ordinal);
        Assert.Contains("uncappedFrameChannel.port2.postMessage(0);", browserAsset, StringComparison.Ordinal);
        Assert.Contains("const UNCAPPED_FRAMES_PER_COMPLETION = 3;", browserAsset, StringComparison.Ordinal);
        Assert.Contains("const MAX_UNCAPPED_COMPLETION_GROUPS = 2;", browserAsset, StringComparison.Ordinal);
        Assert.Contains("const uncappedGpuFenceResolvers = new Map();", browserAsset, StringComparison.Ordinal);
        Assert.Contains("uncappedGpuCompletions.push(captureUncappedGpuCompletion());", browserAsset, StringComparison.Ordinal);
        Assert.Contains("await uncappedGpuCompletions.shift();", browserAsset, StringComparison.Ordinal);
        Assert.Contains("state.device.queue.onSubmittedWorkDone()", browserAsset, StringComparison.Ordinal);
        Assert.Contains("type: 'uncapped-frame-fence', id", browserAsset, StringComparison.Ordinal);
        Assert.DoesNotContain("uncappedFramesSinceFence", browserAsset, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "function nextAnimationFrame(vsync) {\n  queueMicrotask",
            browserAsset.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains("hosted.Gpu.Context.VSync", browserHost, StringComparison.Ordinal);
        Assert.Contains("NextAnimationFrameAsync(vsync)", browserHost, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserBenchmarkQueryUsesExplicitEnvironmentAllowList()
    {
        var browserAsset = Read("src", "ProGPU.Browser", "BrowserAssets", "progpu-browser.js");

        Assert.Contains("const BENCHMARK_QUERY_VARIABLES = Object.freeze({", browserAsset, StringComparison.Ordinal);
        Assert.Contains("benchmarkPage: 'PROGPU_SAMPLE_BENCHMARK_PAGE'", browserAsset, StringComparison.Ordinal);
        Assert.Contains("benchmarkMeasureFrames: 'PROGPU_SAMPLE_BENCHMARK_MEASURE_FRAMES'", browserAsset, StringComparison.Ordinal);
        Assert.Contains("benchmarkScrollStep: 'PROGPU_SAMPLE_BENCHMARK_SCROLL_STEP'", browserAsset, StringComparison.Ordinal);
        Assert.Contains("benchmarkPreconditionPages: 'PROGPU_SAMPLE_BENCHMARK_PRECONDITION_PAGES'", browserAsset, StringComparison.Ordinal);
        Assert.Contains("benchmarkPreconditionFrames: 'PROGPU_SAMPLE_BENCHMARK_PRECONDITION_FRAMES'", browserAsset, StringComparison.Ordinal);
        Assert.Contains("function readBenchmarkEnvironment()", browserAsset, StringComparison.Ordinal);
        Assert.Contains("dotnet.withEnvironmentVariables(readBenchmarkEnvironment()).create()", browserAsset, StringComparison.Ordinal);
        Assert.DoesNotContain("Object.fromEntries(query", browserAsset, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserFilePickerUsesCancellationSafeDirectByteTransfer()
    {
        var browserAsset = Read("src", "ProGPU.Browser", "BrowserAssets", "progpu-browser.js");
        var storageServices = Read("src", "ProGPU.Browser", "BrowserStorageServices.cs");
        var browserInput = Read("src", "ProGPU.Browser", "BrowserInputDispatcher.cs");

        Assert.Contains("input.addEventListener('cancel'", browserAsset, StringComparison.Ordinal);
        Assert.DoesNotContain("globalThis.addEventListener('focus'", browserAsset, StringComparison.Ordinal);
        Assert.Contains("runtime.getAssemblyExports('ProGPU.Browser.dll')", browserAsset, StringComparison.Ordinal);
        Assert.Contains("dispatchTerminalPointerEvent(3, event, point)", browserAsset, StringComparison.Ordinal);
        Assert.Contains("DispatchImmediatePointer", browserInput, StringComparison.Ordinal);
        Assert.Contains("heap.set(bytes, destination);", browserAsset, StringComparison.Ordinal);
        Assert.DoesNotContain("bytesToBase64", browserAsset, StringComparison.Ordinal);
        Assert.Contains("globalThis.showOpenFilePicker", browserAsset, StringComparison.Ordinal);
        Assert.Contains("globalThis.showSaveFilePicker", browserAsset, StringComparison.Ordinal);
        Assert.Contains("globalThis.showDirectoryPicker", browserAsset, StringComparison.Ordinal);
        Assert.Contains("input.webkitdirectory = true;", browserAsset, StringComparison.Ordinal);
        Assert.Contains("handle.createWritable()", browserAsset, StringComparison.Ordinal);
        Assert.Contains("const bytes = heap.slice(source, source + length);", browserAsset, StringComparison.Ordinal);
        Assert.Contains("CopyPickedStorage((nint)destination, length)", storageServices, StringComparison.Ordinal);
        Assert.Contains("ClearPickedStorage();", storageServices, StringComparison.Ordinal);
        Assert.Contains("WritePickedStorageText(token, text)", storageServices, StringComparison.Ordinal);
        Assert.Contains("WritePickedStorageBytes(token, (nint)source, bytes.Length)", storageServices, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserMediaUsesTypedNativeWebAudioEffectGraph()
    {
        var browserAsset = Read(
            "src",
            "ProGPU.Browser",
            "BrowserAssets",
            "progpu-browser.js");
        var browserProvider = Read(
            "src",
            "ProGPU.Browser",
            "BrowserMediaPlaybackProvider.cs");
        var mediaPlayerSample = Read(
            "src",
            "ProGPU.Samples",
            "Pages",
            "MediaPlayerPage.cs");

        Assert.Contains(
            "createMediaElementSource(entry.video)",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "entry.audioContext.createGain()",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "entry.audioContext.createStereoPanner()",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "effect.node.pan.setValueAtTime(",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "configureBrowserMediaAudioEffect",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "removeAllBrowserMediaAudioEffects",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "effect is not IMediaAudioGraphEffect",
            browserProvider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ConfigureAudioEffectCore(",
            browserProvider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new MediaAudioGainEffectFactory(",
            mediaPlayerSample,
            StringComparison.Ordinal);
        Assert.Contains(
            "new MediaAudioStereoBalanceEffectFactory(",
            mediaPlayerSample,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Audio balance effect\"",
            mediaPlayerSample,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SampleAudioGainEffect",
            mediaPlayerSample,
            StringComparison.Ordinal);
    }

    [Fact]
    public void
        MediaPlayerSampleExposesWinUiSphericalProjectionWithRetainedEffects()
    {
        string mediaPlayer = Read(
            "src",
            "ProGPU.Samples",
            "Pages",
            "MediaPlayerPage.cs");

        Assert.Contains(
            "MediaPlaybackSphericalVideoProjection",
            mediaPlayer,
            StringComparison.Ordinal);
        Assert.Contains(
            ".SphericalVideoProjection;",
            mediaPlayer,
            StringComparison.Ordinal);
        Assert.Contains(
            ".SphericalVideoFrameFormat",
            mediaPlayer,
            StringComparison.Ordinal);
        Assert.Contains(
            "SphericalVideoProjectionMode.Spherical",
            mediaPlayer,
            StringComparison.Ordinal);
        Assert.Contains(
            "projection.ViewOrientation",
            mediaPlayer,
            StringComparison.Ordinal);
        Assert.Contains(
            "projection.HorizontalFieldOfViewInDegrees",
            mediaPlayer,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddEffect(effects, \"Blur\", blur);",
            mediaPlayer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EditorSampleExposesColorClipPreviewPlaybackAndOverlay()
    {
        string editor = Read(
            "src",
            "ProGPU.Samples",
            "Pages",
            "NonLinearVideoEditorPage.cs");

        Assert.Contains(
            "Content = \"Add color clip\"",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaClip.CreateFromColor(",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "private sealed class EditorRoot",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "IAnimatedElement",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "_colorSourcePosition +=",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplyColorPreview(clip)",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateColorBrush(",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "else if (overlay.Clip.ProGpuColor is",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"ProGPU.Sample.Editing.AudioGain\"",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"ProGPU.Sample.Editing.VideoColor\"",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"ProGPU.Sample.Editing.VideoGaussianBlur\"",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaEffectRegistry.Default.Register(",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AudioEffectDefinition(",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "new VideoEffectDefinition(",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "clip.VideoEffectDefinitions",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "new MediaVideoColorEffectFactory(",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "new MediaVideoGaussianBlurEffectFactory(",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text(\"GPU brightness\")",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text(\"GPU contrast\")",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text(\"GPU sepia\")",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text(\"GPU invert\")",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "GPU Gaussian blur",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "blurSigma: BlurOf(",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnClipAudioGainChanged",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnClipAudioBalanceChanged",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnBackgroundAudioGainChanged",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnBackgroundAudioBalanceChanged",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplyAudioEffects(",
            editor,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Select a URI-backed clip before adding an overlay.",
            editor,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserEditorUsesNativeFastAndWebGpuExportLanes()
    {
        string browserAsset = Read(
            "src",
            "ProGPU.Browser",
            "BrowserAssets",
            "progpu-browser.js");
        string provider = Read(
            "src",
            "ProGPU.Browser",
            "BrowserWebGpuMediaCompositionExportProvider.cs");
        string audioGraphResolver = Read(
            "src",
            "ProGPU.Media",
            "Audio",
            "MediaAudioGraphEffectResolver.cs");
        string fastProvider = Read(
            "src",
            "ProGPU.Browser",
            "BrowserFastMediaCompositionExportProvider.cs");
        string registration = Read(
            "src",
            "ProGPU.Browser",
            "BrowserMediaPlaybackProvider.cs");
        string shader = Read(
            "src",
            "ProGPU.Browser",
            "Shaders",
            "BrowserMediaComposition.wgsl");

        Assert.Contains(
            "new OffscreenCanvas(",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "copyExternalImageToTexture(",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "transferToImageBitmap()",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "createMediaStreamDestination()",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "before the first asynchronous GPU or",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu-media-export-audio-smoke",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "volumeNode.gain.value = entry.volume",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "audioContext.createStereoPanner()",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "node.pan.value = parameter0",
            browserAsset,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Math.min(1, Number(clip.volume)",
            browserAsset,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Math.min(1, Number(track.volume)",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "new MediaRecorder(",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "GPUTextureUsage.RENDER_ATTACHMENT",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "handle.createWritable()",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "startBrowserMediaCompositionExport(",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpuMediaExportSource",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "startStageBrowserMediaSource(",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "dispatchMediaExportCompletion",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "dispatchMediaStageCompletion",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShaderResource.Load(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "BrowserMediaComposition.wgsl",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "TextureGaussianBlur.wgsl",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "s_gaussianBlurRegistration",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "StandardDeviationPropertyName",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCompositionVideoEffectResolver",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"redTransform\"",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "redTransform:",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "size: 80",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "size: 912",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "const values = new Float32Array(228)",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "const uniformIndex = 36 + pair * 4",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "binding: 3",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "createCompositionGaussianUniform(",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "request.width *",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "Math.abs(destination.width)",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "visual.blurHorizontalBindGroup",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "const commandBufferSubmission = [null]",
            browserAsset,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "overlayEntries.filter(",
            browserAsset,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "...(activeBase ?",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "red_transform: vec4<f32>",
            shader,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsoBmffFastMediaCompositionExportProvider",
            fastProvider,
            StringComparison.Ordinal);
        Assert.Contains(
            "IMediaCompositionExportCapabilityProvider",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaCompositionExportVideoPath.GpuCopy",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryGetAudioEffectGraph(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "effect is not IMediaAudioGraphEffect",
            audioGraphResolver,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaAudioGraphEffectKind.Gain",
            audioGraphResolver,
            StringComparison.Ordinal);
        Assert.Contains(
            "MediaAudioGraphEffectKind.StereoBalance",
            audioGraphResolver,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryCaptureBuiltInGraph(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "WriteAudioGraph(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"audioGraph\"",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "track.Volume * effectGain",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "AudioEffectDefinitions =",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "IMediaCompositionExportCapabilityProvider",
            fastProvider,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompressedSampleCopy",
            fastProvider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new BrowserWebGpuMediaCompositionExportProvider(",
            registration,
            StringComparison.Ordinal);
        Assert.Contains(
            "new BrowserFastMediaCompositionExportProvider(",
            registration,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "// Algorithm:",
            shader,
            StringComparison.Ordinal);
        Assert.Contains(
            "// Time complexity:",
            shader,
            StringComparison.Ordinal);
        Assert.Contains(
            "// Space complexity:",
            shader,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FFmpeg",
            browserAsset,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "FFmpeg",
            provider,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowserCompositionThumbnailsReuseTheWebGpuCompositionLane()
    {
        string browserAsset = Read(
            "src",
            "ProGPU.Browser",
            "BrowserAssets",
            "progpu-browser.js");
        string provider = Read(
            "src",
            "ProGPU.Browser",
            "BrowserWebGpuMediaCompositionThumbnailProvider.cs");
        string registration = Read(
            "src",
            "ProGPU.Browser",
            "BrowserMediaPlaybackProvider.cs");

        Assert.Contains(
            "IMediaCompositionThumbnailProvider",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShaderResource.Load(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "BrowserMediaComposition.wgsl",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryGetVideoEffectPlan(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "TextureGaussianBlur.wgsl",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "visual.blurVerticalBindGroup",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "thumbnailSmoke === 'effect'",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "RunEffectAsync(",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "s_gaussianBlurRegistration",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "new BrowserWebGpuMediaCompositionThumbnailProvider(",
            registration,
            StringComparison.Ordinal);
        Assert.Contains(
            "const renderActiveVisuals = async () =>",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "copyExternalImageToTexture(",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "convertToBlob({",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "startBrowserMediaCompositionThumbnails(",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "copyBrowserMediaCompositionThumbnail(",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "heap.set(bytes, destination)",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "dispatchMediaThumbnailCompletion",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpuMediaThumbnailSmokeResult",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not claim zero-copy",
            provider,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FFmpeg",
            provider,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowserWorkerClosesEveryTransferredMediaFrameOwnershipPath()
    {
        var browserAsset = Read(
            "src",
            "ProGPU.Browser",
            "BrowserAssets",
            "progpu-browser.js")
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "if (!transferred) frame.close();",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "state.worker.postMessage({ type: 'media-disposed', mediaId: id });",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "case 'media-disposed': {\n" +
            "        const pending = state.pendingMediaFrames.get(message.mediaId);\n" +
            "        pending?.close();\n" +
            "        state.pendingMediaFrames.delete(message.mediaId);",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "const previous = state.pendingMediaFrames.get(message.mediaId);\n" +
            "        previous?.close();",
            browserAsset,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (isDispatcherWorker) source.close();",
            browserAsset,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseBrowserPublishUsesManagedWebAssemblyAot()
    {
        var project = Read("src", "ProGPU.Samples.Browser", "ProGPU.Samples.Browser.csproj");

        Assert.Contains("<WasmEnableHotReload Condition=\"'$(WasmEnableHotReload)' == '' And '$(Configuration)' == 'Debug'\">true</WasmEnableHotReload>", project, StringComparison.Ordinal);
        Assert.Contains("<RunAOTCompilation Condition=\"'$(RunAOTCompilation)' == '' And '$(Configuration)' == 'Release'\">true</RunAOTCompilation>", project, StringComparison.Ordinal);
        Assert.Contains("<PublishTrimmed Condition=\"'$(RunAOTCompilation)' == 'true'\">true</PublishTrimmed>", project, StringComparison.Ordinal);
        Assert.Contains("<_AOT_InternalForceInterpretAssemblies Include=\"netDxf.netstandard.dll\" />", project, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubPagesPublishesAotBrowserArtifactBelowRepositoryPath()
    {
        var workflow = Read(".github", "workflows", "browser-pages.yml");
        var html = Read("src", "ProGPU.Samples.Browser", "wwwroot", "index.html");
        var noJekyll = Read("src", "ProGPU.Samples.Browser", "wwwroot", ".nojekyll");

        Assert.Contains("dotnet publish src/ProGPU.Samples.Browser/ProGPU.Samples.Browser.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("--configuration Release", workflow, StringComparison.Ordinal);
        Assert.Contains("path: artifacts/browser-aot/wwwroot", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/configure-pages@v5", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/upload-pages-artifact@v4", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/deploy-pages@v4", workflow, StringComparison.Ordinal);
        Assert.Contains("pages: write", workflow, StringComparison.Ordinal);
        Assert.Contains("id-token: write", workflow, StringComparison.Ordinal);
        Assert.Contains("<base href=\"./\">", html, StringComparison.Ordinal);
        Assert.Contains("_framework", noJekyll, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserPathOperationsUseNonBlockingAotSafeReadback()
    {
        var page = Read("src", "ProGPU.Samples", "Pages", "PathOpsPage.cs");
        var api = Read("src", "ProGPU.Backend", "IWebGpuApi.cs");
        var browserApi = Read("src", "ProGPU.Browser", "BrowserWebGpuApi.cs");

        Assert.Contains("await PathOpGeometrySolver.CombineAsync", page, StringComparison.Ordinal);
        Assert.DoesNotContain("PathOpGeometrySolver.Combine(", page, StringComparison.Ordinal);
        Assert.Contains("Task<BufferMapAsyncStatus> BufferMapAsyncTask", api, StringComparison.Ordinal);
        Assert.Contains("MapBufferTaskCoreAsync", browserApi, StringComparison.Ordinal);
        Assert.DoesNotContain("await MapBufferCoreAsync", browserApi, StringComparison.Ordinal);
        Assert.Contains("MapBufferCoreAsync(double handle", browserApi, StringComparison.Ordinal);
        Assert.DoesNotContain("checked((int)handle.Value)", browserApi, StringComparison.Ordinal);
    }

    private static FrameworkElement? FindByName(FrameworkElement element, string name)
    {
        if (element.Name == name) return element;
        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement frameworkElement && FindByName(frameworkElement, name) is { } match)
                    return match;
            }
        }
        if (element is ContentControl { Content: FrameworkElement content })
            return FindByName(content, name);
        return null;
    }

    private static string Read(params string[] parts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
