using System;
using System.IO;
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
            Assert.Contains("<Version>12.0.5-preview.19</Version>", renderer, StringComparison.Ordinal);
            Assert.Contains("<Version>12.0.5-preview.19</Version>", windowing, StringComparison.Ordinal);
            Assert.Contains("<Version>11.3.18-preview.1</Version>", rendererV11, StringComparison.Ordinal);
            Assert.Contains("<Version>11.3.18-preview.1</Version>", windowingV11, StringComparison.Ordinal);
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
            Assert.Contains("fee9c561ce036e8a3e8cee2397c75ca599b4790d", provenance, StringComparison.Ordinal);
            Assert.Contains("without modification", provenance, StringComparison.Ordinal);
            Assert.Contains("private static AppBuilder BuildSkiaShimApp()", program, StringComparison.Ordinal);
            Assert.Contains(".UseSilkNet()", program, StringComparison.Ordinal);
        }

        [Fact]
        public void PackagingScriptsAndDocumentationCoverBothArtifacts()
        {
            var packageList = ReadRepoFile("scripts", "progpu-package-list.sh");
            var pack = ReadRepoFile("scripts", "progpu-pack.sh");
            var publish = ReadRepoFile("scripts", "progpu-publish.sh");
            var documentation = ReadRepoFile("docs", "progpu-packaging.md");
            var packageReadme = ReadRepoFile("docs", "progpu-package-readme.md");

            foreach (var packageId in new[] { "ProGPU.Avalonia.Rendering", "ProGPU.Avalonia.SilkNet" })
            {
                Assert.Contains(packageId, packageList, StringComparison.Ordinal);
                Assert.Contains(packageId, documentation, StringComparison.Ordinal);
            }

            Assert.Contains("ProGPU.Avalonia.Rendering.V11", packageList, StringComparison.Ordinal);
            Assert.Contains("ProGPU.Avalonia.SilkNet.V11", packageList, StringComparison.Ordinal);
            Assert.Contains("11.3.18-preview.1", packageList, StringComparison.Ordinal);
            Assert.Contains("12.0.5-preview.19", packageList, StringComparison.Ordinal);
            Assert.Contains("dotnet", pack, StringComparison.Ordinal);
            Assert.Contains("--output", pack, StringComparison.Ordinal);
            Assert.Contains("NUGET_API_KEY", publish, StringComparison.Ordinal);
            Assert.Contains("--skip-duplicate", publish, StringComparison.Ordinal);
            Assert.DoesNotContain(".snupkg", publish, StringComparison.Ordinal);
            Assert.Contains(".WithInterFont()", documentation, StringComparison.Ordinal);
            Assert.Contains("IProGpuApiLeaseFeature", packageReadme, StringComparison.Ordinal);
            Assert.Contains("lease.CurrentTransform", packageReadme, StringComparison.Ordinal);
            Assert.Contains("ShaderToyParams", packageReadme, StringComparison.Ordinal);
            Assert.Contains("ShaderResource.Load", packageReadme, StringComparison.Ordinal);
            Assert.Contains("ApiLeaseWave.wgsl", packageReadme, StringComparison.Ordinal);
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
            Assert.Contains("Avalonia.HarfBuzz", project, StringComparison.Ordinal);
            Assert.Contains("Avalonia.Fonts.Inter", project, StringComparison.Ordinal);
            Assert.Contains("EmbeddedResource Update=\"Shaders/*.wgsl\"", project, StringComparison.Ordinal);
            Assert.Contains("$(AssemblyName).Shaders.%(Filename)%(Extension)", project, StringComparison.Ordinal);
            Assert.DoesNotContain("ProjectReference", project, StringComparison.Ordinal);
            Assert.Contains(".UseSilkNet()", program, StringComparison.Ordinal);
            Assert.Contains(".UseProGpu()", program, StringComparison.Ordinal);
            Assert.Contains("UseRegionDirtyRectClipping = false", program, StringComparison.Ordinal);
            Assert.Contains(".UseHarfBuzz()", program, StringComparison.Ordinal);
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
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, Path.Combine(path));
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);

                directory = directory.Parent;
            }

            throw new FileNotFoundException($"Could not locate repository file '{Path.Combine(path)}'.");
        }
    }
}
