using System.Text.RegularExpressions;
using Xunit;

namespace ProGPU.Tests;

public sealed class AvaloniaControlCatalogPatchTests
{
    [Fact]
    public void ControlCatalogPagesAreDeferredThroughTypedFactories()
    {
        string patch = File.ReadAllText(
            FindRepoFile(
                "eng",
                "avalonia",
                "12.0.5",
                "progpu-controlcatalog.patch"));
        int deferredFileStart = patch.IndexOf(
            "diff --git a/samples/ControlCatalog/DeferredCatalogPage.cs",
            StringComparison.Ordinal);

        Assert.True(deferredFileStart >= 0);
        string deferredFile = patch[deferredFileStart..];
        Assert.Contains(
            "+internal sealed class DeferredCatalogPage : ContentControl",
            deferredFile,
            StringComparison.Ordinal);
        Assert.Contains(
            "+    protected override void OnAttachedToVisualTree(",
            deferredFile,
            StringComparison.Ordinal);
        Assert.Contains(
            "+            Content = CreatePage(PageKind);",
            deferredFile,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Activator.CreateInstance", deferredFile, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", deferredFile, StringComparison.Ordinal);

        int typedFactories = Regex.Matches(
            deferredFile,
            @"^\+\s+CatalogPageKind\.\w+\s+=>\s+new\s+\w+\(\),$",
            RegexOptions.Multiline).Count;
        int deferredHosts = Regex.Matches(
            patch,
            @"^\+\s+<local:DeferredCatalogPage PageKind=""\w+"" />$",
            RegexOptions.Multiline).Count;

        Assert.Equal(69, typedFactories);
        Assert.Equal(70, deferredHosts);
    }

    [Fact]
    public void BenchmarkCapturesTheSelectedPageInsteadOfTheClippedWindowTree()
    {
        string benchmark = File.ReadAllText(
            FindRepoFile(
                "samples",
                "ControlCatalog.Desktop",
                "ControlCatalogBenchmark.cs"));

        Assert.Contains(
            "Avalonia.Visual renderRoot = _benchmarkScreenshotRoot ??",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "FindScreenshotRoot(window.Content as Avalonia.Visual ?? window)",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            ".OfType<TabControl>()",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content: Avalonia.Visual deferredContent",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "bitmap.Render(renderRoot);",
            benchmark,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "bitmap.Render(window);",
            benchmark,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedAvaloniaFontsUseTypedAssemblyResourceSlices()
    {
        string patch = File.ReadAllText(
            FindRepoFile(
                "eng",
                "avalonia",
                "12.0.5",
                "progpu-compositor.patch"));
        string fontManager = File.ReadAllText(
            FindRepoFile(
                "src",
                "ProGPU.Avalonia.Rendering",
                "FontManagerImpl.cs"));
        string ttfFont = File.ReadAllText(
            FindRepoFile(
                "src",
                "ProGPU.Text",
                "TtfFont.cs"));

        Assert.Contains(
            "+internal sealed class AssemblyResourceSliceStream : SlicedStream",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+        return new AssemblyResourceSliceStream(",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "stream is AssemblyResourceSliceStream resourceSlice",
            fontManager,
            StringComparison.Ordinal);
        Assert.Contains(
            "TtfFont.LoadEmbeddedResourceSlice(",
            fontManager,
            StringComparison.Ordinal);
        Assert.Contains(
            "resourceSlice.OpenResourceStream()",
            fontManager,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "resourceSlice.Assembly",
            fontManager,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateEmbeddedSliceStorage(",
            ttfFont,
            StringComparison.Ordinal);
        Assert.Contains(
            "embeddedData.DataAddress + offset",
            ttfFont,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System.Reflection",
            fontManager,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SourceBackendManagerRecreatesAReportedLostRendererContext()
    {
        string patch = File.ReadAllText(
            FindRepoFile(
                "eng",
                "avalonia",
                "12.0.5",
                "progpu-compositor.patch"));

        Assert.Contains(
            "diff --git a/src/Avalonia.Base/Rendering/PlatformRenderInterfaceContextManager.cs",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "+            _backend.IsLost ||",
            patch,
            StringComparison.Ordinal);
        Assert.Contains(
            "Lost_Backend_Context_Is_Disposed_And_Recreated_Without_Platform_Graphics",
            patch,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompositionProfileRequiresNativeCustomVisualExecution()
    {
        string profiler = File.ReadAllText(
            FindRepoFile(
                "tools",
                "profile-avalonia-controlcatalog.sh"));
        string benchmark = File.ReadAllText(
            FindRepoFile(
                "samples",
                "ControlCatalog.Desktop",
                "ControlCatalogBenchmark.cs"));
        string scene = File.ReadAllText(
            FindRepoFile(
                "src",
                "ProGPU.Avalonia.Rendering",
                "AvaloniaCompositionScene.cs"));

        Assert.Contains(
            "\"$page\" == \"Composition\"",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RetainedCompositionCustomVisualNodes\"",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"RetainedCompositionCustomVisualCompilations\"",
            profiler,
            StringComparison.Ordinal);
        Assert.Contains(
            "RetainedCompositionCustomVisualCompilations =",
            benchmark,
            StringComparison.Ordinal);
        Assert.Contains(
            "source is ServerCompositionCustomVisual",
            scene,
            StringComparison.Ordinal);
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. pathParts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            string.Join(Path.DirectorySeparatorChar, pathParts));
    }
}
