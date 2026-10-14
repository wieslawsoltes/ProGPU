using System.Runtime.InteropServices;
using ProGPU.Tests;

if (args.Length is < 1 or > 3 || !string.Equals(args[0], RuntimeInformation.ProcessArchitecture.ToString(), StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Specify the actual process architecture and optional case; linear-winding also accepts fallback or reference.");
string selected = args.Length >= 2 ? args[1] : "all";
string? mode = args.Length == 3 ? args[2] : null;
if (mode is not null && (mode is not ("fallback" or "reference") || selected != "linear-winding"))
    throw new ArgumentException("Explicit fallback or general-reference execution requires the linear-winding case.");
if (selected is not ("all" or "coverage" or "simple-first" or "boolean-first" or "linear-coverage" or "linear-first" or "curve-first" or "linear-winding" or "original-path" or "original-glyph" or "vertical-crossings" or "cubic-coverage" or "cubic-lazy"))
    throw new ArgumentException($"Unknown regression case: {selected}.");
Console.WriteLine($"Path rasterization source conformance: {RuntimeInformation.OSDescription}; process={RuntimeInformation.ProcessArchitecture}");
var tests = new SinglePathRasterPipelineTests();
int executed = 0;
Run("coverage", tests.SinglePathEntryMatchesEveryOrdinaryCoverageWordAndUntouchedSlot);
Run("original-path", tests.JoinedCrossingsMatchOriginalPathWordsAndUntouchedSlots);
Run("original-glyph", new RasterCrossingGlyphTests().JoinedCrossingsMatchOriginalGlyphWordsAndUntouchedSlots);
Run("vertical-crossings", new VerticalPathCrossingTests().ExactVerticalCrossingsMatchOriginalSignedLanesAndUntouchedTails);
Run("cubic-coverage", new CubicPathRasterPipelineTests().CubicEntryMatchesOriginalCoverageAndRejectsEveryOtherCurve);
Run("cubic-lazy", new CubicPathRasterPipelineTests().ActualAtlasKeepsCubicLinearMixedAndBooleanPipelinesLazy);
Run("simple-first", () => tests.ActualAtlasRetainsSeparateLazyPipelinesAndMixedBatchCoverage(false));
Run("boolean-first", () => tests.ActualAtlasRetainsSeparateLazyPipelinesAndMixedBatchCoverage(true));
Run("linear-coverage", new LinearPathRasterPipelineTests().LinearEntryMatchesEveryGeneralCoverageWordAndUntouchedSlot);
Run("linear-first", () => new LinearPathRasterPipelineTests().ActualAtlasKeepsLinearCurvedAndBooleanPipelinesLazyAndDistinct(false));
Run("curve-first", () => new LinearPathRasterPipelineTests().ActualAtlasKeepsLinearCurvedAndBooleanPipelinesLazyAndDistinct(true));
Run("linear-winding", new LinearWindingRasterPipelineTests
    { RequireFallbackAdapter = mode == "fallback", CompareGeneralEntry = mode == "reference" }
    .SignedLinearCoverageMatchesIndependentRegionsAndRejectsLaterCurves);
if (executed != (selected == "all" ? 12 : 1))
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
