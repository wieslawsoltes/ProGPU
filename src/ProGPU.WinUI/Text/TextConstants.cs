
namespace Microsoft.UI.Text
{
    /// <summary>WinUI text-object-model sentinel and range-count constants.</summary>
    public static class TextConstants
    {
        public static Windows.UI.Color AutoColor { get; } = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        public static int MaxUnitCount => int.MaxValue;
        public static int MinUnitCount => int.MinValue;
        public static Windows.UI.Color UndefinedColor { get; } = Windows.UI.Color.FromArgb(0, 255, 255, 255);
        public static float UndefinedFloatValue => -9_999_999f;
        public static Windows.UI.Text.FontStretch UndefinedFontStretch => Windows.UI.Text.FontStretch.Undefined;
        public static Windows.UI.Text.FontStyle UndefinedFontStyle => (Windows.UI.Text.FontStyle)(-9_999_999);
        public static int UndefinedInt32Value => -9_999_999;
    }
}
