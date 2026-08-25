using BenchmarkDotNet.Attributes;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class GraphicsStringFormatBenchmarks
{
    private Bitmap _bitmap = null!;
    private Graphics _graphics = null!;
    private Font _font = null!;
    private StringFormat _format = null!;
    private StringFormat _advancedFormat = null!;
    private char[] _text = null!;
    private char[] _advancedText = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bitmap = new Bitmap(200, 80);
        _graphics = Graphics.FromImage(_bitmap);
        _font = new Font(FontFamily.GenericSansSerif, 16f);
        _format = StringFormat.GenericTypographic;
        _advancedFormat = new StringFormat(
            StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces);
        _advancedFormat.SetTabStops(8f, [40f, 40f]);
        _advancedFormat.SetDigitSubstitution(0x0C01, StringDigitSubstitute.National);
        _text = "LibreWinForms text".ToCharArray();
        _advancedText = "A\t123  ".ToCharArray();
        MeasureSpan();
        MeasureAdvancedFormatSpan();
    }

    [Benchmark]
    public SizeF MeasureSpan() =>
        _graphics.MeasureString(_text.AsSpan(), _font, new SizeF(160f, 60f), _format);

    [Benchmark]
    public SizeF MeasureAdvancedFormatSpan() =>
        _graphics.MeasureString(
            _advancedText.AsSpan(),
            _font,
            new SizeF(160f, 60f),
            _advancedFormat);

    [GlobalCleanup]
    public void Dispose()
    {
        _format.Dispose();
        _advancedFormat.Dispose();
        _font.Dispose();
        _graphics.Dispose();
        _bitmap.Dispose();
    }
}
