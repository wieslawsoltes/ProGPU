using BenchmarkDotNet.Attributes;
using System.Drawing.Printing;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class PrinterSettingsCollectionBenchmarks
{
    private readonly PrinterSettings.PaperSizeCollection _paperSizes = new(
        [new PaperSize("Letter", 850, 1100)]);

    [Benchmark(OperationsPerInvoke = 1_000)]
    public int ReadPaperSizeWidthBatch()
    {
        int width = 0;
        for (int index = 0; index < 1_000; index++)
        {
            width = _paperSizes[0].Width;
        }

        return width;
    }
}
