using System;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using Xunit;

namespace Avalonia.ProGpu.RenderTests;

public sealed class BaselineAssetTests
{
    [Fact]
    public void MigratedProGpuBaselinesAreCompleteAndDecodable()
    {
        var root = FindRepositoryRoot();
        var baselineRoot = Path.Combine(root, "tests", "TestFiles", "ProGpu");
        var baselines = Directory
            .EnumerateFiles(baselineRoot, "*.expected.png", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(274, baselines.Length);
        Assert.Equal(
            284,
            Directory.EnumerateFiles(baselineRoot, "*", SearchOption.AllDirectories).Count());
        Assert.True(File.Exists(Path.Combine(
            baselineRoot,
            "Controls",
            "Image",
            "blend",
            "Cat.jpg")));

        foreach (var baseline in baselines)
        {
            var info = Image.Identify(baseline);
            Assert.NotNull(info);
            Assert.True(info.Width > 0, baseline);
            Assert.True(info.Height > 0, baseline);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src", "ProGPU.Avalonia.Rendering")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ProGPU repository root.");
    }
}
