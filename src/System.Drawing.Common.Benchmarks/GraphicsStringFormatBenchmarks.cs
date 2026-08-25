using BenchmarkDotNet.Attributes;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class GraphicsStringFormatBenchmarks
{
    private Bitmap _bitmap = null!;
    private Graphics _graphics = null!;
    private Font _font = null!;
    private StringFormat _format = null!;
    private char[] _text = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bitmap = new Bitmap(200, 80);
        _graphics = Graphics.FromImage(_bitmap);
        _font = new Font(FontFamily.GenericSansSerif, 16f);
        _format = StringFormat.GenericTypographic;
        _text = "LibreWinForms text".ToCharArray();
        MeasureSpan();
    }

    [Benchmark]
    public SizeF MeasureSpan() =>
        _graphics.MeasureString(_text.AsSpan(), _font, new SizeF(160f, 60f), _format);

    [GlobalCleanup]
    public void Dispose()
    {
        _format.Dispose();
        _font.Dispose();
        _graphics.Dispose();
        _bitmap.Dispose();
    }
}
