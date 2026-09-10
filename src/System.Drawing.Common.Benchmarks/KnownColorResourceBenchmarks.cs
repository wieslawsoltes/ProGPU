using BenchmarkDotNet.Attributes;
using System.Runtime.CompilerServices;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class KnownColorResourceBenchmarks
{
    [GlobalSetup]
    public void WarmCache()
    {
        _ = Brushes.CornflowerBlue;
        _ = Pens.CornflowerBlue;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = 1024)]
    public int GetCachedBrushBatch()
    {
        int checksum = 0;
        for (int index = 0; index < 1024; index++)
        {
            Brush brush = (index & 3) switch
            {
                0 => Brushes.CornflowerBlue,
                1 => Brushes.DarkGoldenrod,
                2 => Brushes.MediumVioletRed,
                _ => Brushes.YellowGreen
            };
            checksum ^= RuntimeHelpers.GetHashCode(brush);
        }

        return checksum;
    }

    [Benchmark(OperationsPerInvoke = 1024)]
    public int GetCachedPenBatch()
    {
        int checksum = 0;
        for (int index = 0; index < 1024; index++)
        {
            Pen pen = (index & 3) switch
            {
                0 => Pens.CornflowerBlue,
                1 => Pens.DarkGoldenrod,
                2 => Pens.MediumVioletRed,
                _ => Pens.YellowGreen
            };
            checksum ^= RuntimeHelpers.GetHashCode(pen);
        }

        return checksum;
    }

    [Benchmark(OperationsPerInvoke = 1024)]
    public int ReadCachedPenStateBatch()
    {
        Pen pen = Pens.CornflowerBlue;
        int checksum = 0;
        for (int index = 0; index < 1024; index++)
        {
            checksum ^= pen.Color.ToArgb();
            checksum ^= (int)pen.PenType;
            checksum ^= BitConverter.SingleToInt32Bits(pen.Width);
        }

        return checksum;
    }

    [Benchmark]
    public Brush CreateSolidBrush() => new SolidBrush(Color.CornflowerBlue);

    [Benchmark]
    public Pen CreatePen() => new Pen(Color.CornflowerBlue);
}
