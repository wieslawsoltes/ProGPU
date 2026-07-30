using Windows.Foundation.Metadata;
using Windows.UI;

namespace Microsoft.UI;

[ContractVersion(WindowsAppSdkContract.Name, WindowsAppSdkContract.Version1)]
public sealed class Colors
{
    private Colors()
    {
    }

    public static Color AliceBlue => FromArgb(0xFFF0F8FFu);
    public static Color AntiqueWhite => FromArgb(0xFFFAEBD7u);
    public static Color Aqua => FromArgb(0xFF00FFFFu);
    public static Color Aquamarine => FromArgb(0xFF7FFFD4u);
    public static Color Azure => FromArgb(0xFFF0FFFFu);
    public static Color Beige => FromArgb(0xFFF5F5DCu);
    public static Color Bisque => FromArgb(0xFFFFE4C4u);
    public static Color Black => FromArgb(0xFF000000u);
    public static Color BlanchedAlmond => FromArgb(0xFFFFEBCDu);
    public static Color Blue => FromArgb(0xFF0000FFu);
    public static Color BlueViolet => FromArgb(0xFF8A2BE2u);
    public static Color Brown => FromArgb(0xFFA52A2Au);
    public static Color BurlyWood => FromArgb(0xFFDEB887u);
    public static Color CadetBlue => FromArgb(0xFF5F9EA0u);
    public static Color Chartreuse => FromArgb(0xFF7FFF00u);
    public static Color Chocolate => FromArgb(0xFFD2691Eu);
    public static Color Coral => FromArgb(0xFFFF7F50u);
    public static Color CornflowerBlue => FromArgb(0xFF6495EDu);
    public static Color Cornsilk => FromArgb(0xFFFFF8DCu);
    public static Color Crimson => FromArgb(0xFFDC143Cu);
    public static Color Cyan => FromArgb(0xFF00FFFFu);
    public static Color DarkBlue => FromArgb(0xFF00008Bu);
    public static Color DarkCyan => FromArgb(0xFF008B8Bu);
    public static Color DarkGoldenrod => FromArgb(0xFFB8860Bu);
    public static Color DarkGray => FromArgb(0xFFA9A9A9u);
    public static Color DarkGreen => FromArgb(0xFF006400u);
    public static Color DarkKhaki => FromArgb(0xFFBDB76Bu);
    public static Color DarkMagenta => FromArgb(0xFF8B008Bu);
    public static Color DarkOliveGreen => FromArgb(0xFF556B2Fu);
    public static Color DarkOrange => FromArgb(0xFFFF8C00u);
    public static Color DarkOrchid => FromArgb(0xFF9932CCu);
    public static Color DarkRed => FromArgb(0xFF8B0000u);
    public static Color DarkSalmon => FromArgb(0xFFE9967Au);
    public static Color DarkSeaGreen => FromArgb(0xFF8FBC8Fu);
    public static Color DarkSlateBlue => FromArgb(0xFF483D8Bu);
    public static Color DarkSlateGray => FromArgb(0xFF2F4F4Fu);
    public static Color DarkTurquoise => FromArgb(0xFF00CED1u);
    public static Color DarkViolet => FromArgb(0xFF9400D3u);
    public static Color DeepPink => FromArgb(0xFFFF1493u);
    public static Color DeepSkyBlue => FromArgb(0xFF00BFFFu);
    public static Color DimGray => FromArgb(0xFF696969u);
    public static Color DodgerBlue => FromArgb(0xFF1E90FFu);
    public static Color Firebrick => FromArgb(0xFFB22222u);
    public static Color FloralWhite => FromArgb(0xFFFFFAF0u);
    public static Color ForestGreen => FromArgb(0xFF228B22u);
    public static Color Fuchsia => FromArgb(0xFFFF00FFu);
    public static Color Gainsboro => FromArgb(0xFFDCDCDCu);
    public static Color GhostWhite => FromArgb(0xFFF8F8FFu);
    public static Color Gold => FromArgb(0xFFFFD700u);
    public static Color Goldenrod => FromArgb(0xFFDAA520u);
    public static Color Gray => FromArgb(0xFF808080u);
    public static Color Green => FromArgb(0xFF008000u);
    public static Color GreenYellow => FromArgb(0xFFADFF2Fu);
    public static Color Honeydew => FromArgb(0xFFF0FFF0u);
    public static Color HotPink => FromArgb(0xFFFF69B4u);
    public static Color IndianRed => FromArgb(0xFFCD5C5Cu);
    public static Color Indigo => FromArgb(0xFF4B0082u);
    public static Color Ivory => FromArgb(0xFFFFFFF0u);
    public static Color Khaki => FromArgb(0xFFF0E68Cu);
    public static Color Lavender => FromArgb(0xFFE6E6FAu);
    public static Color LavenderBlush => FromArgb(0xFFFFF0F5u);
    public static Color LawnGreen => FromArgb(0xFF7CFC00u);
    public static Color LemonChiffon => FromArgb(0xFFFFFACDu);
    public static Color LightBlue => FromArgb(0xFFADD8E6u);
    public static Color LightCoral => FromArgb(0xFFF08080u);
    public static Color LightCyan => FromArgb(0xFFE0FFFFu);
    public static Color LightGoldenrodYellow => FromArgb(0xFFFAFAD2u);
    public static Color LightGray => FromArgb(0xFFD3D3D3u);
    public static Color LightGreen => FromArgb(0xFF90EE90u);
    public static Color LightPink => FromArgb(0xFFFFB6C1u);
    public static Color LightSalmon => FromArgb(0xFFFFA07Au);
    public static Color LightSeaGreen => FromArgb(0xFF20B2AAu);
    public static Color LightSkyBlue => FromArgb(0xFF87CEFAu);
    public static Color LightSlateGray => FromArgb(0xFF778899u);
    public static Color LightSteelBlue => FromArgb(0xFFB0C4DEu);
    public static Color LightYellow => FromArgb(0xFFFFFFE0u);
    public static Color Lime => FromArgb(0xFF00FF00u);
    public static Color LimeGreen => FromArgb(0xFF32CD32u);
    public static Color Linen => FromArgb(0xFFFAF0E6u);
    public static Color Magenta => FromArgb(0xFFFF00FFu);
    public static Color Maroon => FromArgb(0xFF800000u);
    public static Color MediumAquamarine => FromArgb(0xFF66CDAAu);
    public static Color MediumBlue => FromArgb(0xFF0000CDu);
    public static Color MediumOrchid => FromArgb(0xFFBA55D3u);
    public static Color MediumPurple => FromArgb(0xFF9370DBu);
    public static Color MediumSeaGreen => FromArgb(0xFF3CB371u);
    public static Color MediumSlateBlue => FromArgb(0xFF7B68EEu);
    public static Color MediumSpringGreen => FromArgb(0xFF00FA9Au);
    public static Color MediumTurquoise => FromArgb(0xFF48D1CCu);
    public static Color MediumVioletRed => FromArgb(0xFFC71585u);
    public static Color MidnightBlue => FromArgb(0xFF191970u);
    public static Color MintCream => FromArgb(0xFFF5FFFAu);
    public static Color MistyRose => FromArgb(0xFFFFE4E1u);
    public static Color Moccasin => FromArgb(0xFFFFE4B5u);
    public static Color NavajoWhite => FromArgb(0xFFFFDEADu);
    public static Color Navy => FromArgb(0xFF000080u);
    public static Color OldLace => FromArgb(0xFFFDF5E6u);
    public static Color Olive => FromArgb(0xFF808000u);
    public static Color OliveDrab => FromArgb(0xFF6B8E23u);
    public static Color Orange => FromArgb(0xFFFFA500u);
    public static Color OrangeRed => FromArgb(0xFFFF4500u);
    public static Color Orchid => FromArgb(0xFFDA70D6u);
    public static Color PaleGoldenrod => FromArgb(0xFFEEE8AAu);
    public static Color PaleGreen => FromArgb(0xFF98FB98u);
    public static Color PaleTurquoise => FromArgb(0xFFAFEEEEu);
    public static Color PaleVioletRed => FromArgb(0xFFDB7093u);
    public static Color PapayaWhip => FromArgb(0xFFFFEFD5u);
    public static Color PeachPuff => FromArgb(0xFFFFDAB9u);
    public static Color Peru => FromArgb(0xFFCD853Fu);
    public static Color Pink => FromArgb(0xFFFFC0CBu);
    public static Color Plum => FromArgb(0xFFDDA0DDu);
    public static Color PowderBlue => FromArgb(0xFFB0E0E6u);
    public static Color Purple => FromArgb(0xFF800080u);
    public static Color Red => FromArgb(0xFFFF0000u);
    public static Color RosyBrown => FromArgb(0xFFBC8F8Fu);
    public static Color RoyalBlue => FromArgb(0xFF4169E1u);
    public static Color SaddleBrown => FromArgb(0xFF8B4513u);
    public static Color Salmon => FromArgb(0xFFFA8072u);
    public static Color SandyBrown => FromArgb(0xFFF4A460u);
    public static Color SeaGreen => FromArgb(0xFF2E8B57u);
    public static Color SeaShell => FromArgb(0xFFFFF5EEu);
    public static Color Sienna => FromArgb(0xFFA0522Du);
    public static Color Silver => FromArgb(0xFFC0C0C0u);
    public static Color SkyBlue => FromArgb(0xFF87CEEBu);
    public static Color SlateBlue => FromArgb(0xFF6A5ACDu);
    public static Color SlateGray => FromArgb(0xFF708090u);
    public static Color Snow => FromArgb(0xFFFFFAFAu);
    public static Color SpringGreen => FromArgb(0xFF00FF7Fu);
    public static Color SteelBlue => FromArgb(0xFF4682B4u);
    public static Color Tan => FromArgb(0xFFD2B48Cu);
    public static Color Teal => FromArgb(0xFF008080u);
    public static Color Thistle => FromArgb(0xFFD8BFD8u);
    public static Color Tomato => FromArgb(0xFFFF6347u);
    public static Color Transparent => FromArgb(0x00FFFFFFu);
    public static Color Turquoise => FromArgb(0xFF40E0D0u);
    public static Color Violet => FromArgb(0xFFEE82EEu);
    public static Color Wheat => FromArgb(0xFFF5DEB3u);
    public static Color White => FromArgb(0xFFFFFFFFu);
    public static Color WhiteSmoke => FromArgb(0xFFF5F5F5u);
    public static Color Yellow => FromArgb(0xFFFFFF00u);
    public static Color YellowGreen => FromArgb(0xFF9ACD32u);

    private static Color FromArgb(uint argb) =>
        Color.FromArgb(
            (byte)(argb >> 24),
            (byte)(argb >> 16),
            (byte)(argb >> 8),
            (byte)argb);
}
