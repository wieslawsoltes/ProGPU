using System.Globalization;

namespace ProGPU.WinUI.Platform;

/// <summary>
/// Supplies localized display names for platform colors.
/// </summary>
public interface IColorDisplayNameProvider
{
    bool TryGetColorDisplayName(
        Windows.UI.Color color,
        CultureInfo culture,
        out string displayName);
}
