using BenchmarkDotNet.Attributes;
using ProGPU.Scene;
using System.Drawing.Text;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class GraphicsStringFormatBenchmarks
{
    private Bitmap _bitmap = null!;
    private Graphics _graphics = null!;
    private Font _font = null!;
    private Font _decoratedFont = null!;
    private StringFormat _format = null!;
    private StringFormat _advancedFormat = null!;
    private StringFormat _pathFormat = null!;
    private DrawingContext _recordingContext = null!;
    private Graphics _recordingGraphics = null!;
    private SolidBrush _brush = null!;
    private StringFormat _mnemonicFormat = null!;
    private char[] _text = null!;
    private char[] _advancedText = null!;
    private char[] _pathText = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bitmap = new Bitmap(200, 80);
        _graphics = Graphics.FromImage(_bitmap);
        _font = new Font(FontFamily.GenericSansSerif, 16f);
        _decoratedFont = new Font(
            FontFamily.GenericSansSerif,
            16f,
            FontStyle.Underline | FontStyle.Strikeout);
        _format = StringFormat.GenericTypographic;
        _advancedFormat = new StringFormat(
            StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces);
        _advancedFormat.SetTabStops(8f, [40f, 40f]);
        _advancedFormat.SetDigitSubstitution(0x0C01, StringDigitSubstitute.National);
        _pathFormat = new StringFormat(StringFormatFlags.NoWrap)
        {
            Trimming = StringTrimming.EllipsisPath
        };
        _recordingContext = new DrawingContext();
        _recordingGraphics = Graphics.FromProGpuDrawingContext(_recordingContext);
        _brush = new SolidBrush(Color.Black);
        _mnemonicFormat = new StringFormat(StringFormatFlags.NoWrap)
        {
            HotkeyPrefix = HotkeyPrefix.Show
        };
        _text = "LibreWinForms text".ToCharArray();
        _advancedText = "A\t123  ".ToCharArray();
        _pathText = "C:/very/long/project/folder/report.txt".ToCharArray();
        MeasureSpan();
        MeasureAdvancedFormatSpan();
        MeasureEllipsisPathSpan();
        RecordMnemonicString();
        RecordDecoratedString();
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

    [Benchmark]
    public SizeF MeasureEllipsisPathSpan() =>
        _graphics.MeasureString(
            _pathText.AsSpan(),
            _font,
            new SizeF(160f, 60f),
            _pathFormat);

    [Benchmark]
    public int RecordMnemonicString()
    {
        _recordingContext.Commands.Clear();
        _recordingGraphics.DrawString("Sa&ve", _font, _brush, PointF.Empty, _mnemonicFormat);
        return _recordingContext.Commands.Count;
    }

    [Benchmark]
    public int RecordDecoratedString()
    {
        _recordingContext.Commands.Clear();
        _recordingGraphics.DrawString("LibreWinForms", _decoratedFont, _brush, PointF.Empty);
        return _recordingContext.Commands.Count;
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _format.Dispose();
        _advancedFormat.Dispose();
        _pathFormat.Dispose();
        _mnemonicFormat.Dispose();
        _brush.Dispose();
        _recordingGraphics.Dispose();
        _decoratedFont.Dispose();
        _font.Dispose();
        _graphics.Dispose();
        _bitmap.Dispose();
    }
}
