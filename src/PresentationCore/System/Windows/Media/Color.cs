namespace System.Windows.Media;

public struct Color
{
    public byte A { get; set; }
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }

    public static Color FromRgb(byte r, byte g, byte b) => new Color { A = 255, R = r, G = g, B = b };
    public static Color FromArgb(byte a, byte r, byte g, byte b) => new Color { A = a, R = r, G = g, B = b };

    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}
