using ProGPU.Hmi;
using ProGPU.Scene;

namespace ProGPU.WinUI.Hmi;

/// <summary>Shared finite layout contract for symbols, live readouts and captions.</summary>
public readonly record struct HmiVisualLayout(Rect Header, Rect Tag, Rect Glyph, Rect Value, Rect Range, Rect Quality)
{
    public static HmiVisualLayout Calculate(HmiSymbol symbol, float width, float height, HmiAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        if (!float.IsFinite(width) || !float.IsFinite(height) || width < 0 || height < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        Rect R(float x, float y, float w, float h)
        {
            float px = Math.Clamp(x, 0, width), py = Math.Clamp(y, 0, height);
            return new(px, py, Math.Clamp(w, 0, width - px), Math.Clamp(h, 0, height - py));
        }
        if (symbol == HmiSymbol.Label)
            return new(R(8, 3, width - 16, Math.Min(27, height - 6)), default, default,
                R(8, 34, width - 16, height - 39), default, R(width - 16, 4, 10, 10));
        if (symbol == HmiSymbol.Pipe)
            return new(height >= 40 ? R(8, 0, width - 25, 16) : default, default,
                R(1, height >= 40 ? 14 : 1, width - 2, height >= 40 ? height - 15 : height - 2),
                default, default, R(width - 14, 2, 10, 10));
        if (width < 72 || height < 52)
            return new(default, default, R(3, 3, width - 6, height - 6), default, default, R(Math.Max(0, width - 12), 2, 10, 10));
        float inset = Math.Min(12, width * 0.06f);
        float content = Math.Max(0, width - 2 * inset);
        if (HmiSymbolTraits.IsCommand(symbol))
            return new(R(inset, (height - 22) / 2, content - 32, 22), default,
                R(width - 34, (height - 26) / 2, 24, 26), default, default, R(width - 14, 3, 10, 10));
        if (symbol == HmiSymbol.ToggleSwitch)
            return new(R(inset, 7, content - 67, 18), default, R(width - 77, 10, 65, height - 20),
                R(inset, height - 25, content - 67, 17), default, R(width - 14, 2, 10, 10));
        bool tag = appearance.ShowTagName && height >= 125;
        bool range = appearance.ShowValue && appearance.ShowEngineeringRange && !HmiSymbolTraits.IsBinary(symbol) && height >= 140 && symbol != HmiSymbol.Rectangle;
        float top = tag ? 46 : 31;
        float rangeHeight = range ? 18 : 0;
        float valueHeight = symbol == HmiSymbol.NumericDisplay ? Math.Min(42, height * 0.34f) : 22;
        float valueY = height - 9 - rangeHeight - valueHeight;
        var header = R(inset, 8, content - 16,  19);
        var tagRect = tag ? R(inset, 29, content - 10, 14) : default;
        var quality = R(width - 20, 10, 10, 10);
        var glyph = R(inset + 4, top, content - 8, Math.Max(0, valueY - top - 6));
        var value = R(inset, valueY, content, valueHeight);
        var rangeRect = range ? R(inset, height - 22, content, 14) : default;
        if (symbol is HmiSymbol.AlarmBanner or HmiSymbol.AlarmList)
        {
            header = R(40, 9, width - 52, 20);
            glyph = R(10, 10, 22, 25);
            value = R(40, 33, width - 52, height - 43);
            tagRect = default; rangeRect = default;
        }
        else if (symbol == HmiSymbol.NumericDisplay)
        {
            value = R(inset, top + 4, content, Math.Min(valueHeight, height - top - 14));
            glyph = default;
        }
        else if (symbol == HmiSymbol.NumericInput)
        {
            glyph = R(inset, top + 2, content, Math.Max(0, height - top - 12));
            value = default; rangeRect = default;
        }
        else if (symbol == HmiSymbol.Trend)
        {
            tagRect = default;
            glyph = R(inset, 36, content, Math.Max(0, height - 72));
            value = R(width - Math.Min(160, content / 2) - inset, height - 26, Math.Min(160, content / 2), 18);
            rangeRect = default;
        }
        return new(header, tagRect, glyph, value, rangeRect, quality);
    }
}
