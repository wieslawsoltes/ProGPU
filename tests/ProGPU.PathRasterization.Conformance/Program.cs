using System.Runtime.InteropServices;
using ProGPU.Tests;

if (args.Length is < 1 or > 2 || !string.Equals(args[0], RuntimeInformation.ProcessArchitecture.ToString(), StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Specify the required actual process architecture (x64 or arm64) and optional regression case.");
string selected = args.Length == 2 ? args[1] : "all";
if (selected is not ("all" or "coverage" or "simple-first" or "boolean-first" or "linear-coverage" or "linear-first" or "curve-first"))
    throw new ArgumentException($"Unknown regression case: {selected}.");
Console.WriteLine($"Path rasterization source conformance: {RuntimeInformation.OSDescription}; process={RuntimeInformation.ProcessArchitecture}");
var tests = new SinglePathRasterPipelineTests();
int executed = 0;
Run("coverage", tests.SinglePathEntryMatchesEveryOrdinaryCoverageWordAndUntouchedSlot);
Run("simple-first", () => tests.ActualAtlasRetainsSeparateLazyPipelinesAndMixedBatchCoverage(false));
Run("boolean-first", () => tests.ActualAtlasRetainsSeparateLazyPipelinesAndMixedBatchCoverage(true));
Run("linear-coverage", new LinearPathRasterPipelineTests().LinearEntryMatchesEveryGeneralCoverageWordAndUntouchedSlot);
Run("linear-first", () => new LinearPathRasterPipelineTests().ActualAtlasKeepsLinearCurvedAndBooleanPipelinesLazyAndDistinct(false));
Run("curve-first", () => new LinearPathRasterPipelineTests().ActualAtlasKeepsLinearCurvedAndBooleanPipelinesLazyAndDistinct(true));
if (executed != (selected == "all" ? 6 : 1))
    throw new InvalidOperationException("Every selected original regression body must execute.");
Console.WriteLine($"Path rasterization conformance passed: {executed} executed, 0 skipped; case={selected}.");

void Run(string name, Action body)
{
    if (selected != "all" && selected != name) return;
    Console.WriteLine($"Path rasterization regression entering: {name}.");
    body();
    executed++;
    Console.WriteLine($"Path rasterization regression passed: {name}.");
}
