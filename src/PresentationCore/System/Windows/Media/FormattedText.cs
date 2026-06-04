using ProGPU.Text;
using System.Globalization;
using System.Numerics;

namespace System.Windows.Media;

public class FormattedText
{
    public string Text { get; }
    public TtfFont Font { get; }
    public double FontSize { get; }
    public Brush Foreground { get; }

    public FormattedText(
        string textToFormat,
        CultureInfo culture,
        FlowDirection flowDirection,
        Typeface typeface,
        double emSize,
        Brush foreground)
    {
        Text = textToFormat;
        Font = typeface?.FontFamily?.NativeFont ?? new FontFamily("Arial").NativeFont!;
        FontSize = emSize;
        Foreground = foreground;
    }

    public FormattedText(
        string textToFormat,
        CultureInfo culture,
        FlowDirection flowDirection,
        Typeface typeface,
        double emSize,
        Brush foreground,
        double pixelsPerDip) : this(textToFormat, culture, flowDirection, typeface, emSize, foreground)
    {
    }

    public double Width
    {
        get
        {
            if (string.IsNullOrEmpty(Text) || Font == null) return 0;
            float w = 0f;
            for (int i = 0; i < Text.Length; i++)
            {
                ushort idx = Font.GetGlyphIndex(Text[i]);
                w += Font.GetAdvanceWidth(idx, (float)FontSize);
                if (i < Text.Length - 1)
                {
                    w += Font.GetKerning(Text[i], Text[i + 1], (float)FontSize);
                }
            }
            return w;
        }
    }

    public double Height
    {
        get
        {
            if (Font == null) return FontSize;
            double factor = (Font.Ascender - Font.Descender + Font.LineGap) / (double)Font.UnitsPerEm;
            return Math.Max(FontSize, factor * FontSize);
        }
    }

    public void Draw(DrawingContext dc, Point origin)
    {
        if (Font == null) return;
        var nativeBrush = Foreground?.ToNative() ?? new ProGPU.Vector.SolidColorBrush(Vector4.One);
        var pos = new Vector2((float)origin.X, (float)(origin.Y + Height * 0.8)); // Baseline adjustment
        dc.NativeContext.DrawText(Text, Font, (float)FontSize, nativeBrush, pos);
    }
}
