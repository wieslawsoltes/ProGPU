using System.Runtime.InteropServices;
using ProGPU.Tests;

if (args.Length != 1 || !string.Equals(args[0], RuntimeInformation.ProcessArchitecture.ToString(), StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Specify the required actual process architecture (x64 or arm64).");
Console.WriteLine($"Path rasterization source conformance: {RuntimeInformation.OSDescription}; process={RuntimeInformation.ProcessArchitecture}");
var tests = new SinglePathRasterPipelineTests();
tests.SinglePathEntryMatchesEveryOrdinaryCoverageWordAndUntouchedSlot();
tests.ActualAtlasRetainsSeparateLazyPipelinesAndMixedBatchCoverage(false);
tests.ActualAtlasRetainsSeparateLazyPipelinesAndMixedBatchCoverage(true);
Console.WriteLine("Path rasterization conformance passed: 3 executed, 0 skipped.");
