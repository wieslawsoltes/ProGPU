using Xunit;

namespace ProGPU.Tests;

public sealed class MemoryProfilerToolTests
{
    [Fact]
    public void VmRegionReportSeparatesResidentAndDirtyGrowth()
    {
        string source = File.ReadAllText(
            FindRepoFile(
                "tools",
                "ProGPU.SampleMemoryProfiler",
                "LiveMemoryCapture.cs"));

        Assert.Contains(
            "$\"region-resident:{regionName}\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"residentBytes\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$\"region-dirty:{regionName}\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"dirtyBytes\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "successfulVmmapSamples",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsSuccessfulMacVmmapSample",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"failedSamples\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InstrumentsCleanupRequiresEverySupportedExportToSucceed()
    {
        string source = File.ReadAllText(
            FindRepoFile(
                "tools",
                "ProGPU.SampleMemoryProfiler",
                "MacInstrumentsCapture.cs"));

        Assert.Contains(
            "if (!ContainsSchema(tocXml, schema))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "The raw trace has been retained.",
            source,
            StringComparison.Ordinal);
        Assert.True(
            source.IndexOf(
                "Failed to export supported",
                StringComparison.Ordinal) <
            source.LastIndexOf(
                "DeleteTraceBundle(tracePath)",
                StringComparison.Ordinal));
        Assert.Contains(
            "\"potential-hangs\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"metal-command-buffer-error\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"graphics-compiler-spill-events\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AllocationsStatisticsXPath",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"allocations-statistics.xml\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Failed to export the Allocations statistics table.",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AllocationsListXPath",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"allocations-list.xml\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (options.AllocationDetails)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Failed to export the Allocations list.",
            source,
            StringComparison.Ordinal);
        Assert.True(
            source.IndexOf(
                "MacInstrumentsSummary.Write(options.OutputDirectory)",
                StringComparison.Ordinal) <
            source.IndexOf(
                "DeleteCaptureExports(results[index])",
                StringComparison.Ordinal));
        Assert.Contains(
            "ExportsRetained = false",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "DeletedExportBytes = bytes",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InstrumentsCaptureBoundsFinalizationAndRemovesIncompleteTrace()
    {
        string source = File.ReadAllText(
            FindRepoFile(
                "tools",
                "ProGPU.SampleMemoryProfiler",
                "MacInstrumentsCapture.cs"));

        Assert.Contains(
            "options.DurationSeconds + 120",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "process.Kill(entireProcessTree: true)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (record.TimedOut)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "DeleteTraceBundle(tracePath)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "of incomplete trace data was removed",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "traceCreated && options.CleanupTraces",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Removed incomplete trace bytes:",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InstrumentsCaptureOwnsAndDeletesXcodeTemporaryStorage()
    {
        string source = File.ReadAllText(
            FindRepoFile(
                "tools",
                "ProGPU.SampleMemoryProfiler",
                "MacInstrumentsCapture.cs"));

        Assert.Contains(
            "using var captureScratch = new CaptureScratchDirectory(slug);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "startInfo.Environment[\"TMPDIR\"] = temporaryDirectory;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Directory.Delete(Path, recursive: true);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "using var xcodeTemporaryFiles = new XcodeTemporaryFileTracker();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"instruments*.ktrace\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (_preexistingFiles.Contains(path))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "DeletedTemporaryBytes",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InstrumentsSummaryAttributesMetalResourcesAndResolvesReferences()
    {
        string source = File.ReadAllText(
            FindRepoFile(
                "tools",
                "ProGPU.SampleMemoryProfiler",
                "MacInstrumentsSummary.cs"));

        Assert.Contains(
            "public XElement Resolve(XElement element)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "return \"wgpu-native\";",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "LiveAtCaptureEndBytes",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Deallocation\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Allocation\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "bool liveAtEnd = !deallocations.ContainsKey(resourceId);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompilerSpills",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommandBufferErrorCount",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SummarizeNativeAllocations(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"All Heap & Anonymous VM\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "LargestPersistentCategories",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Managed-object attribution",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SummarizeNativeAllocationDetails(detailsPath)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Largest attributed live native/VM groups",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "group.Add(size, timestamp);",
            source,
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
            $"Could not find repository file {Path.Combine(pathParts)}.");
    }
}
