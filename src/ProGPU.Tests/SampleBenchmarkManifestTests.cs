using System.Text.RegularExpressions;
using Xunit;

namespace ProGPU.Tests;

public sealed class SampleBenchmarkManifestTests
{
    [Fact]
    public void PerformanceManifestMatchesNavigationOrder()
    {
        var controller = Read("src", "ProGPU.Samples", "Windows", "MainWindowController.cs");
        var manifest = Read("eng", "performance", "sample-pages.txt");
        int declarationsStart = controller.IndexOf("// Page visual trees", StringComparison.Ordinal);
        int declarationsEnd = controller.IndexOf(
            "AppState._navigationView.SelectionChanged",
            declarationsStart,
            StringComparison.Ordinal);
        Assert.True(declarationsStart >= 0 && declarationsEnd > declarationsStart);

        var declarationRegion = controller[declarationsStart..declarationsEnd];
        var namesByVariable = Regex.Matches(
                declarationRegion,
                "var\\s+(?<variable>[A-Za-z0-9_]+)\\s*=\\s*PageItem\\(\"(?<name>[^\"]+)\"",
                RegexOptions.CultureInvariant)
            .ToDictionary(
                match => match.Groups["variable"].Value,
                match => match.Groups["name"].Value,
                StringComparer.Ordinal);
        var navigationOrder = Regex.Matches(
                declarationRegion,
                "MenuItems\\.Add\\((?<variable>[A-Za-z0-9_]+)\\)",
                RegexOptions.CultureInvariant)
            .Select(match => namesByVariable[match.Groups["variable"].Value])
            .ToArray();
        var manifestOrder = manifest
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .ToArray();

        Assert.NotEmpty(navigationOrder);
        Assert.Equal(navigationOrder, manifestOrder);
    }

    [Fact]
    public void BrowserCompletionFenceCoversWorkerAndMainThreadQueues()
    {
        var browserAsset = Read("src", "ProGPU.Browser", "BrowserAssets", "progpu-browser.js");
        var browserApi = Read("src", "ProGPU.Browser", "BrowserWebGpuApi.cs");

        Assert.Contains("async function waitForSubmittedWorkDone()", browserAsset, StringComparison.Ordinal);
        Assert.Contains("await workerRequest('submitted-work-done');", browserAsset, StringComparison.Ordinal);
        Assert.Contains("case 'submitted-work-done':", browserAsset, StringComparison.Ordinal);
        Assert.Contains("await state.device.queue.onSubmittedWorkDone();", browserAsset, StringComparison.Ordinal);
        Assert.Contains("waitForSubmittedWorkDone,", browserAsset, StringComparison.Ordinal);
        Assert.Contains("WaitForSubmittedWorkDoneCoreAsync", browserApi, StringComparison.Ordinal);
        Assert.Contains("? QueueWorkDoneStatus.Success", browserApi, StringComparison.Ordinal);
        Assert.Contains("supportsTimestampQuery", browserAsset, StringComparison.Ordinal);
        Assert.Contains("requiredFeatures.push('timestamp-query')", browserAsset, StringComparison.Ordinal);
        Assert.Contains("createQuerySet({ type: 'timestamp'", browserAsset, StringComparison.Ordinal);
        Assert.Contains(".resolveQuerySet(", browserAsset, StringComparison.Ordinal);
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
