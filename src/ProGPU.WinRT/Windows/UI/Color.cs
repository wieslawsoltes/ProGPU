namespace Windows.UI
{
    /// <summary>WinRT-compatible ARGB color value used by the text object model.</summary>
    public readonly record struct Color(byte A, byte R, byte G, byte B)
    {
        public static Color FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);
    }
}
