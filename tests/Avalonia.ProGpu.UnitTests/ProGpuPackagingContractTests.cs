using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Avalonia.ProGpu.UnitTests
{
    public class ProGpuPackagingContractTests
    {
        [Fact]
        public void IntegrationProjectsUseProGpuPackageIdsAndExactVersionPins()
        {
            var properties = ReadRepoFile("Directory.Build.props");
            var renderer = ReadRepoFile("src", "ProGPU.Avalonia.Rendering", "Avalonia.ProGpu.csproj");
            var windowing = ReadRepoFile("src", "ProGPU.Avalonia.SilkNet", "Avalonia.SilkNet.csproj");
            var rendererV11 = ReadRepoFile("src", "ProGPU.Avalonia.Rendering.V11", "Avalonia.ProGpu.csproj");
            var windowingV11 = ReadRepoFile("src", "ProGPU.Avalonia.SilkNet.V11", "Avalonia.SilkNet.csproj");
            var packageVersions = ReadRepoFile("Directory.Packages.props");

            Assert.Contains("'$(MSBuildProjectName)' == 'Avalonia.ProGpu'", properties, StringComparison.Ordinal);
            Assert.Contains("'$(MSBuildProjectName)' == 'Avalonia.SilkNet'", properties, StringComparison.Ordinal);
            Assert.Contains("<PackageId>ProGPU.Avalonia.Rendering</PackageId>", renderer, StringComparison.Ordinal);
            Assert.Contains("<PackageId>ProGPU.Avalonia.SilkNet</PackageId>", windowing, StringComparison.Ordinal);
            Assert.Contains("<Version>12.0.5-preview.27</Version>", renderer, StringComparison.Ordinal);
            Assert.Contains("<Version>12.0.5-preview.27</Version>", windowing, StringComparison.Ordinal);
            Assert.Contains("<Version>11.3.18-preview.27</Version>", rendererV11, StringComparison.Ordinal);
            Assert.Contains("<Version>11.3.18-preview.27</Version>", windowingV11, StringComparison.Ordinal);
            Assert.Contains("<DefineConstants>$(DefineConstants);AVALONIA11</DefineConstants>", rendererV11, StringComparison.Ordinal);
            Assert.Contains(@"..\ProGPU.Avalonia.Rendering\**\*.cs", rendererV11, StringComparison.Ordinal);
            Assert.Contains(@"..\ProGPU.Avalonia.SilkNet\**\*.cs", windowingV11, StringComparison.Ordinal);
            Assert.Contains("VersionOverride=\"11.3.18\"", rendererV11, StringComparison.Ordinal);
            Assert.Contains("VersionOverride=\"11.3.18\"", windowingV11, StringComparison.Ordinal);
            Assert.Contains("<PackageReference Include=\"OpenFontSharp\" />", renderer, StringComparison.Ordinal);
            Assert.Contains("<PackageReference Include=\"StbImageSharp\" />", renderer, StringComparison.Ordinal);
            Assert.Contains("<PackageVersion Include=\"Avalonia\" Version=\"12.0.5\" />", packageVersions, StringComparison.Ordinal);
            Assert.Contains("<PackageVersion Include=\"OpenFontSharp\" Version=\"1.0.0\" />", packageVersions, StringComparison.Ordinal);
            Assert.Contains("<PackageVersion Include=\"StbImageSharp\" Version=\"2.30.15\" />", packageVersions, StringComparison.Ordinal);
        }

        [Fact]
        public void ControlCatalogDefaultsToProGpuOnSilkNet()
        {
            var program = ReadRepoFile("samples", "ControlCatalog.Desktop", "Program.cs");

            Assert.Contains(".UseSilkNet()", program, StringComparison.Ordinal);
            Assert.Contains(".UseProGpu()", program, StringComparison.Ordinal);
            Assert.DoesNotContain(".UsePlatformDetect()", program, StringComparison.Ordinal);

            var project = ReadRepoFile("samples", "ControlCatalog.Desktop", "ControlCatalog.Desktop.csproj");
            Assert.Contains(@"src\ProGPU.Avalonia.Rendering\Avalonia.ProGpu.csproj", project, StringComparison.Ordinal);
            Assert.Contains(@"src\ProGPU.Avalonia.SilkNet\Avalonia.SilkNet.csproj", project, StringComparison.Ordinal);
            Assert.DoesNotContain("Avalonia.Desktop", project, StringComparison.Ordinal);
        }

        [Fact]
        public void SourceControlCatalogFailsClosedOnCompositionFallback()
        {
            var program = ReadRepoFile(
                "integration",
                "AvaloniaSourceControlCatalog",
                "Program.cs");
            var options = ReadRepoFile(
                "src",
                "ProGPU.Avalonia.Rendering",
                "ProGpuOptions.cs");

            Assert.Contains(
                "RequireNativeCompositionScene =",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "requireNativeCompositionScene: true",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "--allow-composition-fallback",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "public bool RequireNativeCompositionScene",
                options,
                StringComparison.Ordinal);

            var profiler = ReadRepoFile(
                "tools",
                "profile-avalonia-controlcatalog.sh");
            Assert.Contains(
                "\"RetainedCompositionFallbackNodes\"",
                profiler,
                StringComparison.Ordinal);
            Assert.Contains(
                "missing or nonzero retained composition fallback telemetry",
                profiler,
                StringComparison.Ordinal);
        }

        [Fact]
        public void SourceControlCatalogHasStrictNonSilkDawnPresentationGate()
        {
            var renderer = ReadRepoFile(
                "src",
                "ProGPU.Avalonia.Rendering",
                "Avalonia.ProGpu.csproj");
            var options = ReadRepoFile(
                "src",
                "ProGPU.Avalonia.Rendering",
                "ProGpuOptions.cs");
            var backend = ReadRepoFile(
                "src",
                "ProGPU.Avalonia.Rendering",
                "SkiaBackendContext.cs");
            var target = ReadRepoFile(
                "src",
                "ProGPU.Avalonia.Rendering",
                "DawnMetalRenderTarget.cs");
            var nativeWindowTarget = ReadRepoFile(
                "src",
                "ProGPU.Avalonia.Rendering",
                "DawnNativeWindowRenderTarget.cs");
            var nativePresentation = ReadRepoFile(
                "src",
                "ProGPU.Backend.Dawn",
                "DawnNativePresentation.cs");
            var program = ReadRepoFile(
                "integration",
                "AvaloniaSourceControlCatalog",
                "Program.cs");
            var project = ReadRepoFile(
                "integration",
                "AvaloniaSourceControlCatalog",
                "AvaloniaSourceControlCatalog.csproj");
            var profiler = ReadRepoFile(
                "tools",
                "profile-avalonia-controlcatalog.sh");
            var benchmark = ReadRepoFile(
                "samples",
                "ControlCatalog.Desktop",
                "ControlCatalogBenchmark.cs");
            var nativeBuilder = ReadRepoFile(
                "tools",
                "build-avalonia-native-dawn.sh");
            var nativePatch = ReadRepoFile(
                "eng",
                "avalonia",
                "12.0.5",
                "progpu-native-dawn.patch");
            var preparation = ReadRepoFile(
                "tools",
                "prepare-avalonia-12.0.5-source.sh");

            Assert.Contains(
                @"..\ProGPU.Backend.Dawn\ProGPU.Backend.Dawn.csproj",
                renderer,
                StringComparison.Ordinal);
            Assert.Contains(
                "<PackageReference Include=\"WebGPUSharp\" />",
                renderer,
                StringComparison.Ordinal);
            Assert.Contains(
                "public bool UseDawnMetalPresentation",
                options,
                StringComparison.Ordinal);
            Assert.Contains(
                "public bool RequireDawnMetalPresentation",
                options,
                StringComparison.Ordinal);
            Assert.Contains(
                "public bool UseDawnNativePresentation",
                options,
                StringComparison.Ordinal);
            Assert.Contains(
                "public bool RequireDawnNativePresentation",
                options,
                StringComparison.Ordinal);
            Assert.Contains(
                "IMetalPlatformSurface",
                backend,
                StringComparison.Ordinal);
            Assert.Contains(
                "DawnMetalRenderTarget",
                backend,
                StringComparison.Ordinal);
            Assert.Contains(
                "ImportIOSurface",
                target,
                StringComparison.Ordinal);
            Assert.Contains(
                "SubmitWait",
                target,
                StringComparison.Ordinal);
            Assert.Contains(
                "SubmitSignal",
                target,
                StringComparison.Ordinal);
            Assert.Contains(
                "\"DawnMetalIOSurface\"",
                target,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "ReadPixels",
                target,
                StringComparison.Ordinal);
            Assert.Contains(
                "INativePlatformHandleSurface",
                backend,
                StringComparison.Ordinal);
            Assert.Contains(
                "DawnNativeWindowRenderTarget",
                backend,
                StringComparison.Ordinal);
            Assert.Contains(
                "\"DawnD3D12HWND\"",
                nativeWindowTarget,
                StringComparison.Ordinal);
            Assert.Contains(
                "\"DawnVulkanXlib\"",
                nativeWindowTarget,
                StringComparison.Ordinal);
            Assert.Contains(
                "GetCurrentTexture",
                nativePresentation,
                StringComparison.Ordinal);
            Assert.Contains(
                "Present()",
                nativePresentation,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "ReadPixels",
                nativeWindowTarget,
                StringComparison.Ordinal);
            Assert.Contains(
                "--native-windowing",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "--allow-dawn-presentation-fallback",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                @"src\Avalonia.Native\Avalonia.Native.csproj",
                project,
                StringComparison.Ordinal);
            Assert.Contains(
                @"src\Avalonia.X11\Avalonia.X11.csproj",
                project,
                StringComparison.Ordinal);
            Assert.Contains(
                @"src\Windows\Avalonia.Win32\Avalonia.Win32.csproj",
                project,
                StringComparison.Ordinal);
            Assert.Contains(
                "source-progpu-native",
                profiler,
                StringComparison.Ordinal);
            Assert.Contains(
                "strict native lane did not present through $expected_native_presentation",
                profiler,
                StringComparison.Ordinal);
            Assert.Contains(
                "MedianFrameMs",
                benchmark,
                StringComparison.Ordinal);
            Assert.Contains(
                "P95FrameMs",
                benchmark,
                StringComparison.Ordinal);
            Assert.Contains(
                "P99FrameMs",
                benchmark,
                StringComparison.Ordinal);
            Assert.Contains(
                "missing or incomplete frame-time distribution telemetry",
                profiler,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_AVALONIA_REPEATS",
                profiler,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_AVALONIA_BENCHMARK_RUN",
                profiler,
                StringComparison.Ordinal);
            Assert.Contains(
                "build-avalonia-native-dawn.sh",
                profiler,
                StringComparison.Ordinal);
            Assert.Contains(
                "xcodebuild",
                nativeBuilder,
                StringComparison.Ordinal);
            Assert.Contains(
                "derivedDataPath",
                nativeBuilder,
                StringComparison.Ordinal);
            Assert.Contains(
                "_layer.framebufferOnly = false;",
                nativePatch,
                StringComparison.Ordinal);
            Assert.Contains(
                "|| !_renderTarget.Properties.IsSuitableForDirectRendering;",
                nativePatch,
                StringComparison.Ordinal);
            Assert.Contains(
                "progpu-native-dawn.patch",
                preparation,
                StringComparison.Ordinal);
        }

        [Fact]
        public void PackageOnlyReplacementSmokeRequiresRetainedComposition()
        {
            var program = ReadRepoFile(
                "integration",
                "ProGpuPackageApp",
                "Program.cs");
            var app = ReadRepoFile(
                "integration",
                "ProGpuPackageApp",
                "App.cs");
            var project = ReadRepoFile(
                "integration",
                "ProGpuPackageApp",
                "ProGpuPackageApp.csproj");
            var runner = ReadRepoFile(
                "integration",
                "ProGpuPackageApp",
                "run.sh");

            Assert.Contains(
                "PROGPU_REPLACEMENT_PACKAGE",
                project,
                StringComparison.Ordinal);
            Assert.Contains(
                "RequireNativeCompositionScene = true",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "metrics.RetainedCompositionSceneCount",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "metrics.RetainedCompositionFallbackNodeCount",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "The packaged renderer does not contain",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "ProGpuRequireReplacement=",
                runner,
                StringComparison.Ordinal);
            Assert.Contains(
                "progpu_avalonia_runtime_package_ids",
                runner,
                StringComparison.Ordinal);
            Assert.Contains(
                ".nupkg.sha512",
                runner,
                StringComparison.Ordinal);
            Assert.Contains(
                "openssl dgst -sha512",
                runner,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_INTEGRATION_NATIVE_AOT",
                runner,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_INTEGRATION_MULTI_WINDOW_SMOKE",
                app,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_INTEGRATION_PROFILE_HOLD_SECONDS",
                app,
                StringComparison.Ordinal);
            Assert.Contains(
                "seconds is >= 1 and <= 120",
                app,
                StringComparison.Ordinal);
            Assert.Contains(
                "SharesWebGpuDeviceWith",
                app,
                StringComparison.Ordinal);
            Assert.Contains(
                "ownerDisposedFrames",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "borrowerDisposedFrames",
                program,
                StringComparison.Ordinal);
            Assert.Contains(
                "\"-p:PublishAot=true\"",
                runner,
                StringComparison.Ordinal);
            Assert.Contains(
                "\"-p:TrimMode=full\"",
                runner,
                StringComparison.Ordinal);
            Assert.Contains(
                "\"${published_executable}\" \"$@\"",
                runner,
                StringComparison.Ordinal);
            Assert.Contains(
                "trap cleanup EXIT",
                runner,
                StringComparison.Ordinal);
            Assert.Contains(
                "package_source=\"${PWD}/${package_source}\"",
                runner,
                StringComparison.Ordinal);

            var popup = ReadRepoFile(
                "src",
                "ProGPU.Avalonia.SilkNet",
                "SilkNetPopupImpl.cs");
            Assert.Contains(
                "SetSystemDecorations(SystemDecorations.None)",
                popup,
                StringComparison.Ordinal);
            Assert.Contains(
                "SetWindowDecorations(WindowDecorations.None)",
                popup,
                StringComparison.Ordinal);
        }

        [Fact]
        public void WebGpuPresentationAvoidsReadbackAndKeepsAvaloniaSkiaSourceUnmodified()
        {
            var directDrawingContext = ReadRepoFile(
                "src", "ProGPU.Avalonia.Rendering", "DrawingContextImpl.cs");
            var surfacePresenter = ReadRepoFile(
                "src", "ProGPU.Backend", "GpuTextureSurfacePresenter.cs");
            var skSurface = ReadRepoFile("src", "SkiaSharp", "SKSurface.cs");
            var lockedFramebuffer = ReadRepoFile(
                "src", "ProGPU.Avalonia.SilkNet", "SilkNetLockedFramebuffer.cs");
            var provenance = ReadRepoFile(
                "src", "ProGPU.Avalonia.SkiaShim", "AVALONIA-SOURCE.md");
            var program = ReadRepoFile("samples", "ControlCatalog.Desktop", "Program.cs");

            Assert.Contains("GpuTextureBlitter.Blit", surfacePresenter, StringComparison.Ordinal);
            Assert.Contains("GpuTextureBlitter.Blit", directDrawingContext, StringComparison.Ordinal);
            Assert.DoesNotContain("ReadPixels", surfacePresenter, StringComparison.Ordinal);
            Assert.Contains("TextureRelease(surfaceTexture.Texture)", surfacePresenter, StringComparison.Ordinal);
            Assert.Contains("TextureRelease(surfaceTexture.Texture)", directDrawingContext, StringComparison.Ordinal);
            Assert.Contains("FlushCore(copyToCpu: false)", skSurface, StringComparison.Ordinal);
            Assert.Contains("GpuTextureSurfacePresenter.Present", skSurface, StringComparison.Ordinal);
            Assert.Contains("GpuFramebufferPresentationRegistry.TryPresent", lockedFramebuffer, StringComparison.Ordinal);
            Assert.Contains("external/Avalonia", provenance, StringComparison.Ordinal);
            Assert.Contains("5378af03f17a4d9d2845882229ffed7f67350037", provenance, StringComparison.Ordinal);
            Assert.Contains("fee9c561ce036e8a3e8cee2397c75ca599b4790d", provenance, StringComparison.Ordinal);
            Assert.Contains("effective 54-file source set therefore remains unmodified", provenance, StringComparison.Ordinal);
            Assert.Contains("private static AppBuilder BuildSkiaShimApp(bool useHarfBuzz)", program, StringComparison.Ordinal);
            Assert.Contains(".UseSilkNet()", program, StringComparison.Ordinal);
        }

        [Fact]
        public void AvaloniaDerivedProjectsLinkPinnedForkSourcesAndKeepOnlyOverrides()
        {
            var modules = ReadRepoFile(".gitmodules");
            var skiaProject = ReadRepoFile(
                "src", "ProGPU.Avalonia.SkiaShim", "Avalonia.Skia.csproj");
            var skiaNotice = ReadRepoFile(
                "src", "ProGPU.Avalonia.SkiaShim", "PORTING-NOTICE.txt");
            var catalogProject = ReadRepoFile(
                "samples", "ControlCatalog", "ControlCatalog.csproj");
            var renderDemoProject = ReadRepoFile(
                "samples", "RenderDemo", "RenderDemo.csproj");

            Assert.Contains("path = external/Avalonia", modules, StringComparison.Ordinal);
            Assert.Contains(
                "url = https://github.com/wieslawsoltes/Avalonia.git",
                modules,
                StringComparison.Ordinal);
            Assert.Contains(@"external\Avalonia", skiaProject, StringComparison.Ordinal);
            Assert.Contains(@"external\Avalonia", catalogProject, StringComparison.Ordinal);
            Assert.Contains(@"external\Avalonia", renderDemoProject, StringComparison.Ordinal);
            Assert.Contains(
                "progpu-avalonia-v12.0.5-preview.19",
                skiaNotice,
                StringComparison.Ordinal);
            Assert.Contains(
                "5378af03f17a4d9d2845882229ffed7f67350037",
                skiaNotice,
                StringComparison.Ordinal);

            AssertLocalSourceOverrides(
                Path.Combine("src", "ProGPU.Avalonia.SkiaShim"),
                "GlyphRunImpl.cs");
            AssertLocalSourceOverrides(
                Path.Combine("samples", "ControlCatalog"),
                Path.Combine("Pages", "DialogsPage.xaml.cs"),
                Path.Combine("ViewModels", "PlatformInformationViewModel.cs"));
            AssertLocalSourceOverrides(
                Path.Combine("samples", "RenderDemo"),
                "App.xaml.cs",
                Path.Combine("Controls", "LineBoundsDemoControl.cs"),
                "MainWindow.xaml.cs",
                Path.Combine("Pages", "AnimationsPage.xaml.cs"),
                Path.Combine("Pages", "BrushesPage.axaml.cs"),
                Path.Combine("Pages", "ClippingPage.xaml.cs"),
                Path.Combine("Pages", "CustomAnimatorPage.xaml.cs"),
                Path.Combine("Pages", "DrawingPage.xaml.cs"),
                Path.Combine("Pages", "FormattedTextPage.axaml.cs"),
                Path.Combine("Pages", "GlyphRunPage.xaml.cs"),
                Path.Combine("Pages", "LineBoundsPage.xaml.cs"),
                Path.Combine("Pages", "SpringAnimationsPage.xaml.cs"),
                Path.Combine("Pages", "TextFormatterPage.axaml.cs"),
                Path.Combine("Pages", "Transform3DPage.axaml.cs"),
                Path.Combine("Pages", "TransitionsPage.xaml.cs"));
        }

        [Fact]
        public void PackagingScriptsAndDocumentationCoverBothArtifacts()
        {
            var packageList = ReadRepoFile("scripts", "progpu-package-list.sh");
            var pack = ReadRepoFile("scripts", "progpu-pack.sh");
            var publish = ReadRepoFile("scripts", "progpu-publish.sh");
            var documentation = ReadRepoFile("docs", "progpu-packaging.md");
            var packageReadme = ReadRepoFile("docs", "progpu-package-readme.md");
            var releaseWorkflow = ReadRepoFile(".github", "workflows", "release.yml");

            foreach (var packageId in new[] { "ProGPU.Avalonia.Rendering", "ProGPU.Avalonia.SilkNet" })
            {
                Assert.Contains(packageId, packageList, StringComparison.Ordinal);
                Assert.Contains(packageId, documentation, StringComparison.Ordinal);
            }

            Assert.Contains("ProGPU.Avalonia.Rendering.V11", packageList, StringComparison.Ordinal);
            Assert.Contains("ProGPU.Avalonia.SilkNet.V11", packageList, StringComparison.Ordinal);
            Assert.Contains("11.3.18-preview.27", packageList, StringComparison.Ordinal);
            Assert.Contains("12.0.5-preview.27", packageList, StringComparison.Ordinal);
            Assert.Contains("dotnet", pack, StringComparison.Ordinal);
            Assert.Contains("--output", pack, StringComparison.Ordinal);
            Assert.Contains("NUGET_API_KEY", publish, StringComparison.Ordinal);
            Assert.Contains("--skip-duplicate", publish, StringComparison.Ordinal);
            Assert.DoesNotContain(".snupkg", publish, StringComparison.Ordinal);
            Assert.Contains("./scripts/progpu-pack.sh", releaseWorkflow, StringComparison.Ordinal);
            Assert.Contains(
                "progpu-avalonia-${{ env.PROGPU_PACKAGE_VERSION }}",
                releaseWorkflow,
                StringComparison.Ordinal);
            Assert.True(
                releaseWorkflow.IndexOf(
                    "Publish runtime packages to NuGet.org",
                    StringComparison.Ordinal) <
                releaseWorkflow.IndexOf(
                    "Publish Avalonia integration packages to NuGet.org",
                    StringComparison.Ordinal));
            Assert.Contains(".WithInterFont()", documentation, StringComparison.Ordinal);
            Assert.Contains("IProGpuApiLeaseFeature", packageReadme, StringComparison.Ordinal);
            Assert.Contains("lease.CurrentTransform", packageReadme, StringComparison.Ordinal);
            Assert.Contains("ShaderToyParams", packageReadme, StringComparison.Ordinal);
            Assert.Contains("ShaderResource.Load", packageReadme, StringComparison.Ordinal);
            Assert.Contains("ApiLeaseWave.wgsl", packageReadme, StringComparison.Ordinal);
        }

        [Fact]
        public void SourceBuiltReplacementHasFailClosedRevisionAndAbiGate()
        {
            var preparation = ReadRepoFile("tools", "prepare-avalonia-12.0.5-source.sh");
            var replacementPack = ReadRepoFile("tools", "pack-avalonia-progpu-replacement.sh");
            var validator = ReadRepoFile("tools", "validate-avalonia-source-abi.sh");
            var textTestRunner = ReadRepoFile("tools", "test-avalonia-progpu-text.sh");
            var compositorTestRunner = ReadRepoFile(
                "tools", "test-avalonia-progpu-compositor.sh");
            var retainedPixelTestRunner = ReadRepoFile(
                "tools", "test-avalonia-progpu-retained-pixels.sh");
            var retainedScene = ReadRepoFile(
                "src", "ProGPU.Avalonia.Rendering", "AvaloniaCompositionScene.cs");
            var drawingContext = ReadRepoFile(
                "src", "ProGPU.Avalonia.Rendering", "DrawingContextImpl.cs");
            var incrementalPages = ReadRepoFile(
                "src", "ProGPU.Scene", "Compositor.IncrementalPages.cs");
            var incrementalUploads = ReadRepoFile(
                "src", "ProGPU.Scene", "Compositor.IncrementalUploads.cs");
            var compositorOptions = ReadRepoFile(
                "src", "ProGPU.Scene", "CompositorOptions.cs");
            var sceneVisual = ReadRepoFile(
                "src", "ProGPU.Scene", "Visual.cs");
            var controlCatalogBenchmark = ReadRepoFile(
                "samples", "ControlCatalog.Desktop", "ControlCatalogBenchmark.cs");
            var apiCompat = ReadRepoFile("tools", "validate-avalonia-source-abi.proj");
            var buildWorkflow = ReadRepoFile(".github", "workflows", "build.yml");
            var compositorPatch = ReadRepoFile(
                "eng", "avalonia", "12.0.5", "progpu-compositor.patch");
            var textTestPatch = ReadRepoFile(
                "eng", "avalonia", "12.0.5", "progpu-text-tests.patch");
            var controlCatalogPatch = ReadRepoFile(
                "eng", "avalonia", "12.0.5", "progpu-controlcatalog.patch");
            var packagePatch = ReadRepoFile(
                "eng", "avalonia", "12.0.5", "progpu-package.patch");
            var replacementStack = ReadRepoFile(
                "tools", "pack-avalonia-progpu-stack.sh");
            var reflectionGate = ReadRepoFile(
                "tools", "validate-avalonia-progpu-no-reflection.sh");
            var imageBrushSource = ReadRepoFile(
                "src", "ProGPU.Avalonia.Rendering", "ProGpuImageBrushSource.cs");
            var rendererProject = ReadRepoFile(
                "src", "ProGPU.Avalonia.Rendering", "Avalonia.ProGpu.csproj");
            var textTestBackend = ReadRepoFile(
                "eng", "avalonia", "12.0.5", "TextShapingTestBackend.cs");
            var documentation = ReadRepoFile(
                "docs", "AVALONIA_COMPOSITOR_BACKEND_ARCHITECTURE.md");

            Assert.Contains(
                "fee9c561ce036e8a3e8cee2397c75ca599b4790d",
                preparation,
                StringComparison.Ordinal);
            Assert.Contains("worktree add --detach", preparation, StringComparison.Ordinal);
            Assert.Contains("apply --check --reverse", preparation, StringComparison.Ordinal);
            Assert.Contains("progpu-compositor.patch", preparation, StringComparison.Ordinal);
            Assert.Contains("progpu-text-tests.patch", preparation, StringComparison.Ordinal);
            Assert.Contains("progpu-controlcatalog.patch", preparation, StringComparison.Ordinal);
            Assert.Contains("progpu-package.patch", preparation, StringComparison.Ordinal);
            Assert.Contains("ICompositionRenderDataDrawingContextFeature", compositorPatch, StringComparison.Ordinal);
            Assert.Contains("ICompositionVisualTreeDrawingContextFeature", compositorPatch, StringComparison.Ordinal);
            Assert.Contains("ServerCompositionVisualCollection", compositorPatch, StringComparison.Ordinal);
            Assert.Contains("RetainedChangedVisuals", compositorPatch, StringComparison.Ordinal);
            Assert.Contains("CompleteRetainedSceneSynchronization", compositorPatch, StringComparison.Ordinal);
            Assert.Contains("RetainedContentRevision", compositorPatch, StringComparison.Ordinal);
            Assert.Contains("RetainedCompositionRevisionTests", compositorPatch, StringComparison.Ordinal);
            Assert.Contains(
                "_delayPropagateNeedsBoundsUpdate =",
                compositorPatch,
                StringComparison.Ordinal);
            Assert.Contains(
                "ParentChangeDelayedFlagsAreConsumedByOneRecompute",
                compositorPatch,
                StringComparison.Ordinal);
            Assert.Contains("ProGpuTextShaperTests", textTestPatch, StringComparison.Ordinal);
            Assert.Contains("Media\\GlyphRunTests.cs", textTestPatch, StringComparison.Ordinal);
            Assert.Contains("Media\\TextFormatting\\**\\*.cs", textTestPatch, StringComparison.Ordinal);
            Assert.Contains("InitialPage", controlCatalogPatch, StringComparison.Ordinal);
            Assert.Contains("ProGpuReplacementPackage", packagePatch, StringComparison.Ordinal);
            Assert.Contains("PROGPU_TEXT_SHAPER_TESTS", textTestBackend, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Reflection", textTestBackend, StringComparison.Ordinal);
            Assert.Contains("prepare-avalonia-12.0.5-source.sh", textTestRunner, StringComparison.Ordinal);
            Assert.Contains("--filter-namespace", textTestRunner, StringComparison.Ordinal);
            Assert.Contains("--filter-class", textTestRunner, StringComparison.Ordinal);
            Assert.Contains(
                "prepare-avalonia-12.0.5-source.sh",
                compositorTestRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "RetainedCompositionRevisionTests",
                compositorTestRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_AVALONIA_RETAINED_SCENE=0",
                retainedPixelTestRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_AVALONIA_BENCHMARK_ROOT_GEOMETRY_CLIP=1",
                retainedPixelTestRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_AVALONIA_BENCHMARK_ROOT_ALIASED_TEXT=1",
                retainedPixelTestRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_AVALONIA_BENCHMARK_TEXT_BLUR_EFFECT=1",
                retainedPixelTestRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_AVALONIA_BENCHMARK_TEXT_DROP_SHADOW_EFFECT=1",
                retainedPixelTestRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_AVALONIA_BENCHMARK_ROOT_CONIC_OPACITY_MASK=1",
                retainedPixelTestRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_SCALE=2",
                retainedPixelTestRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_SNAP=1",
                retainedPixelTestRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_CLEARTYPE=1",
                retainedPixelTestRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "Buttons|Composition|Acrylic|BitmapCache|Canvas|AdornerLayer|Clipboard|HeaderedContentControl|Notifications",
                retainedPixelTestRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "native linear/conic/picture opacity masks",
                retainedPixelTestRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "transformed adorner clip chains",
                retainedPixelTestRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "blur/drop-shadow effects",
                retainedPixelTestRunner,
                StringComparison.Ordinal);
            Assert.Contains("cmp -s", retainedPixelTestRunner, StringComparison.Ordinal);
            Assert.Contains(
                "source.Clip is not null and not GeometryImpl",
                retainedScene,
                StringComparison.Ordinal);
            Assert.Contains(
                "TryGetAxisAlignedRectangleBounds",
                retainedScene,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_AVALONIA_BENCHMARK_ROOT_GEOMETRY_CLIP",
                controlCatalogBenchmark,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_AVALONIA_BENCHMARK_ROOT_ALIASED_TEXT",
                controlCatalogBenchmark,
                StringComparison.Ordinal);
            Assert.Contains(
                "GetEffectiveTextOptions",
                retainedScene,
                StringComparison.Ordinal);
            Assert.Contains(
                "GetEffectiveRenderOptions",
                retainedScene,
                StringComparison.Ordinal);
            Assert.Contains(
                "CacheAsLayer = hasBitmapCache",
                retainedScene,
                StringComparison.Ordinal);
            Assert.Contains(
                "LayerCacheRenderScale = cacheRenderScale",
                retainedScene,
                StringComparison.Ordinal);
            Assert.Contains(
                "disableSubpixelText",
                retainedScene,
                StringComparison.Ordinal);
            Assert.Contains(
                "SupportsRetainedCompositionOpacityMask",
                retainedScene,
                StringComparison.Ordinal);
            Assert.Contains(
                "CanRepresentAdornerClip(source)",
                retainedScene,
                StringComparison.Ordinal);
            Assert.Contains(
                "RecordRetainedCompositionOpacityMask",
                retainedScene,
                StringComparison.Ordinal);
            Assert.Contains(
                "SupportsRetainedEffect(effect)",
                retainedScene,
                StringComparison.Ordinal);
            Assert.Contains(
                "SynchronizeEffect(source)",
                retainedScene,
                StringComparison.Ordinal);
            Assert.Contains(
                "EffectContentBounds",
                retainedScene,
                StringComparison.Ordinal);
            Assert.Contains(
                "IIncrementalRenderCommandCache",
                retainedScene,
                StringComparison.Ordinal);
            Assert.Contains(
                "public interface IIncrementalRenderCommandCache",
                sceneVisual,
                StringComparison.Ordinal);
            Assert.Contains(
                "MaximumIncrementalScenePages",
                compositorOptions,
                StringComparison.Ordinal);
            Assert.Contains(
                "RemoveObsoleteIncrementalScenePageRevisions",
                incrementalPages,
                StringComparison.Ordinal);
            Assert.Contains(
                "IncrementalUploadPageBytes = 4096",
                incrementalUploads,
                StringComparison.Ordinal);
            Assert.Contains(
                "WriteAlignedBytes",
                incrementalUploads,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_AVALONIA_INCREMENTAL_SCENE_PAGES",
                drawingContext,
                StringComparison.Ordinal);
            Assert.Contains(
                "./tools/test-avalonia-progpu-text.sh",
                buildWorkflow,
                StringComparison.Ordinal);
            Assert.Contains(
                "./tools/test-avalonia-progpu-compositor.sh",
                buildWorkflow,
                StringComparison.Ordinal);
            Assert.Contains("ProGpuReplacementPackage=true", replacementPack, StringComparison.Ordinal);
            Assert.Contains("PROGPU-REPLACEMENT.md", replacementPack, StringComparison.Ordinal);
            Assert.Contains("validate-avalonia-source-abi.proj", replacementPack, StringComparison.Ordinal);
            Assert.Contains(
                "validate-avalonia-progpu-no-reflection.sh",
                replacementStack,
                StringComparison.Ordinal);
            Assert.Contains(
                "PROGPU_PACKAGE_GROUP=avalonia-runtime",
                replacementStack,
                StringComparison.Ordinal);
            Assert.Contains(
                "progpu_avalonia_runtime_package_ids",
                replacementStack,
                StringComparison.Ordinal);
            Assert.Contains("Avalonia.ProGpu.dll", reflectionGate, StringComparison.Ordinal);
            Assert.Contains("Avalonia.SilkNet.dll", reflectionGate, StringComparison.Ordinal);
            Assert.Contains(
                "#if PROGPU_AVALONIA_SOURCE_COMPOSITOR",
                imageBrushSource,
                StringComparison.Ordinal);
            Assert.Contains("source?.Bitmap?.Item", imageBrushSource, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Reflection", imageBrushSource, StringComparison.Ordinal);
            Assert.DoesNotContain("BindingFlags", imageBrushSource, StringComparison.Ordinal);
            Assert.DoesNotContain("PropertyInfo", imageBrushSource, StringComparison.Ordinal);
            Assert.Contains(
                "UseExactSourceAvaloniaPrivateAssemblies",
                rendererProject,
                StringComparison.Ordinal);
            Assert.Contains(
                "packages\\Avalonia\\bin\\$(Configuration)\\$(TargetFramework)",
                rendererProject,
                StringComparison.Ordinal);
            Assert.Contains("lib ref", replacementPack, StringComparison.Ordinal);
            Assert.Contains("net10.0 net8.0", replacementPack, StringComparison.Ordinal);
            Assert.Contains(
                "fee9c561ce036e8a3e8cee2397c75ca599b4790d",
                validator,
                StringComparison.Ordinal);
            Assert.Contains("git -C", validator, StringComparison.Ordinal);
            Assert.Contains("rev-parse HEAD", validator, StringComparison.Ordinal);
            Assert.Contains("lib/${target_framework}/Avalonia.Base.dll", validator, StringComparison.Ordinal);
            Assert.DoesNotContain("ref/${target_framework}/Avalonia.Base.dll", validator, StringComparison.Ordinal);
            Assert.Contains("ValidateAssembliesTask", apiCompat, StringComparison.Ordinal);
            Assert.Contains("EnableStrictMode=\"true\"", apiCompat, StringComparison.Ordinal);
            Assert.DoesNotContain("ApiCompatSuppressionFile", apiCompat, StringComparison.Ordinal);
            Assert.Contains("EnableRuleAttributesMustMatch=\"true\"", apiCompat, StringComparison.Ordinal);
            Assert.Contains("EnableRuleCannotChangeParameterName=\"true\"", apiCompat, StringComparison.Ordinal);
            Assert.Contains("GetAssemblyIdentity", apiCompat, StringComparison.Ordinal);
            Assert.Contains("PublicKeyToken", apiCompat, StringComparison.Ordinal);
            Assert.Contains("tools/validate-avalonia-source-abi.sh", documentation, StringComparison.Ordinal);
        }

        [Fact]
        public void IntegrationAppConsumesOnlyLocalOrNuGetPackages()
        {
            var project = ReadRepoFile("integration", "ProGpuPackageApp", "ProGpuPackageApp.csproj");
            var program = ReadRepoFile("integration", "ProGpuPackageApp", "Program.cs");
            var leaseView = ReadRepoFile("integration", "ProGpuPackageApp", "ProGpuLeaseView.cs");
            var shader = ReadRepoFile(
                "integration", "ProGpuPackageApp", "Shaders", "ApiLeaseWave.wgsl");
            var runScript = ReadRepoFile("integration", "ProGpuPackageApp", "run.sh");
            var drawingContext = ReadRepoFile(
                "src", "ProGPU.Avalonia.Rendering", "DrawingContextImpl.cs");
            var lockedFramebuffer = ReadRepoFile(
                "src", "ProGPU.Avalonia.SilkNet", "SilkNetLockedFramebuffer.cs");

            Assert.Contains("ProGPU.Avalonia.Rendering", project, StringComparison.Ordinal);
            Assert.Contains("ProGPU.Avalonia.SilkNet", project, StringComparison.Ordinal);
            Assert.Contains("$(ProGpuAvaloniaPackageVersion)", project, StringComparison.Ordinal);
            Assert.DoesNotContain("Avalonia.HarfBuzz", project, StringComparison.Ordinal);
            Assert.Contains("Avalonia.Fonts.Inter", project, StringComparison.Ordinal);
            Assert.Contains("EmbeddedResource Update=\"Shaders/*.wgsl\"", project, StringComparison.Ordinal);
            Assert.Contains("$(AssemblyName).Shaders.%(Filename)%(Extension)", project, StringComparison.Ordinal);
            Assert.DoesNotContain("ProjectReference", project, StringComparison.Ordinal);
            Assert.Contains(".UseSilkNet()", program, StringComparison.Ordinal);
            Assert.Contains(".UseProGpu()", program, StringComparison.Ordinal);
            Assert.Contains("UseRegionDirtyRectClipping = false", program, StringComparison.Ordinal);
            Assert.Contains(".UseProGpuTextShaping()", program, StringComparison.Ordinal);
            Assert.Contains(".WithInterFont()", program, StringComparison.Ordinal);
            Assert.Contains("IProGpuApiLeaseFeature", leaseView, StringComparison.Ordinal);
            Assert.Contains("lease.CurrentTransform", leaseView, StringComparison.Ordinal);
            Assert.Contains("ShaderToyParams", leaseView, StringComparison.Ordinal);
            Assert.Contains("ShaderResource.Load<ProGpuDrawOperation>(\"ApiLeaseWave.wgsl\")", leaseView, StringComparison.Ordinal);
            Assert.DoesNotContain("fn mainImage", leaseView, StringComparison.Ordinal);
            Assert.Contains("// Algorithm:", shader, StringComparison.Ordinal);
            Assert.Contains("// Time complexity:", shader, StringComparison.Ordinal);
            Assert.Contains("// Space complexity:", shader, StringComparison.Ordinal);
            Assert.Contains("fn mainImage", shader, StringComparison.Ordinal);
            Assert.Contains("IPlatformHandle", lockedFramebuffer, StringComparison.Ordinal);
            Assert.Contains("WGPU_SURFACE", lockedFramebuffer, StringComparison.Ordinal);
            Assert.Contains("WGPU_SURFACE", drawingContext, StringComparison.Ordinal);
            Assert.DoesNotContain("IProGpuSurfaceFramebuffer", drawingContext, StringComparison.Ordinal);
            Assert.Contains("local)", runScript, StringComparison.Ordinal);
            Assert.Contains("nuget)", runScript, StringComparison.Ordinal);
            Assert.Contains("--configfile", runScript, StringComparison.Ordinal);
            Assert.Contains("--artifacts-path", runScript, StringComparison.Ordinal);
            Assert.Contains("PROGPU_AVALONIA_PACKAGE_VERSION", runScript, StringComparison.Ordinal);
        }

        private static string ReadRepoFile(params string[] path)
        {
            var candidate = Path.Combine(FindRepositoryRoot().FullName, Path.Combine(path));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            throw new FileNotFoundException($"Could not locate repository file '{Path.Combine(path)}'.");
        }

        private static void AssertLocalSourceOverrides(
            string projectPath,
            params string[] expectedFiles)
        {
            var projectRoot = Path.Combine(FindRepositoryRoot().FullName, projectPath);
            var actualFiles = Directory
                .EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path =>
                    !HasPathSegment(path, "bin") &&
                    !HasPathSegment(path, "obj"))
                .Select(path => Path.GetRelativePath(projectRoot, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var expected = expectedFiles
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expected, actualFiles);
        }

        private static bool HasPathSegment(string path, string segment) =>
            path.Split(Path.DirectorySeparatorChar)
                .Contains(segment, StringComparer.Ordinal);

        private static DirectoryInfo FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
                    return directory;

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the ProGPU repository root.");
        }
    }
}
