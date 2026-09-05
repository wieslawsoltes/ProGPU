using BenchmarkDotNet.Attributes;
using System.Drawing.Text;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class FontBenchmarks
{
    private PrivateFontCollection _collection = null!;
    private FontFamily _family = null!;

    [GlobalSetup]
    public void LoadFont()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fonts", "Inter-Regular.ttf");
        _collection = new PrivateFontCollection();
        _collection.AddFontFile(path);
        _family = _collection.Families[0];
        ReadTypefaceMetrics();
    }

    [Benchmark(OperationsPerInvoke = 4000)]
    public int ReadTypefaceMetrics()
    {
        int total = 0;
        for (int index = 0; index < 1000; index++)
        {
            total += _family.GetEmHeight(FontStyle.Regular);
            total += _family.GetCellAscent(FontStyle.Regular);
            total += _family.GetCellDescent(FontStyle.Regular);
            total += _family.GetLineSpacing(FontStyle.Regular);
        }

        return total;
    }

    [GlobalCleanup]
    public void DisposeFont()
    {
        _family.Dispose();
        _collection.Dispose();
    }
}
