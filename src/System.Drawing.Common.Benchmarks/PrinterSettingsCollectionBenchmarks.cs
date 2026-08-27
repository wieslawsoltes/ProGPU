using BenchmarkDotNet.Attributes;
using System.Drawing.Printing;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class PrinterSettingsCollectionBenchmarks
{
    private readonly PrinterSettings.PaperSizeCollection _paperSizes = new(
        [new PaperSize("Letter", 850, 1100)]);
    private readonly PageSettings[] _pageSettings =
    [
        new()
        {
            PaperSource = new PaperSource { RawKind = 300, SourceName = "Managed tray" },
            PrinterResolution = new PrinterResolution { Kind = PrinterResolutionKind.High }
        },
        new()
        {
            PaperSource = new PaperSource { RawKind = 301, SourceName = "Alternate tray" },
            PrinterResolution = new PrinterResolution { Kind = PrinterResolutionKind.Medium }
        }
    ];

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

    [Benchmark(OperationsPerInvoke = 1_000)]
    public int ReadPageDeviceSelectionBatch()
    {
        int value = 0;
        for (int index = 0; index < 1_000; index++)
        {
            PageSettings settings = _pageSettings[index & 1];
            value += settings.PaperSource.RawKind +
                (int)settings.PrinterResolution.Kind;
        }

        return value;
    }
}
