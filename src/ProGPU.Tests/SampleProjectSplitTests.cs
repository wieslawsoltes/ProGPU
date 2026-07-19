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

        Assert.DoesNotContain("<OutputType>Exe</OutputType>", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("<ApplicationManifest>", shared, StringComparison.Ordinal);
        Assert.Contains("ProGPU.Samples.csproj", desktop, StringComparison.Ordinal);
        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", desktop, StringComparison.Ordinal);
        Assert.Contains("Microsoft.NET.Sdk.WebAssembly", browser, StringComparison.Ordinal);
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
        Assert.Contains("dispatchPointerEvent(3, event, point)", browserAsset, StringComparison.Ordinal);
        Assert.Contains("DispatchImmediatePointer", browserInput, StringComparison.Ordinal);
        Assert.Contains("heap.set(bytes, destination);", browserAsset, StringComparison.Ordinal);
        Assert.DoesNotContain("bytesToBase64", browserAsset, StringComparison.Ordinal);
        Assert.Contains("CopyPickedStorage((nint)destination, length)", storageServices, StringComparison.Ordinal);
        Assert.Contains("ClearPickedStorage();", storageServices, StringComparison.Ordinal);
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
