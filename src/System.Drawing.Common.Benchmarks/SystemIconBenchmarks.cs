using BenchmarkDotNet.Attributes;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class SystemIconBenchmarks
{
    [Benchmark]
    public int CreateAndDisposeFolderIcon32()
    {
        using Icon icon = SystemIcons.GetStockIcon(StockIconId.Folder, 32);
        return icon.Width;
    }

    [Benchmark]
    public int CreateAndDisposeSelectedLinkIcon32()
    {
        using Icon icon = SystemIcons.GetStockIcon(
            StockIconId.DocumentWithAssociation,
            StockIconOptions.LinkOverlay | StockIconOptions.Selected);
        return icon.Width;
    }
}
