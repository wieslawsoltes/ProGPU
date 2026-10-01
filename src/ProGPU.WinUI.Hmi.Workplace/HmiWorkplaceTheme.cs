using ProGPU.Hmi;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Vector;

namespace ProGPU.WinUI.Hmi.Workplace;

/// <summary>Control-room chrome resources, scoped to their owning workplace. No global theme mutation.</summary>
public static class HmiWorkplaceTheme
{
    public static void Apply(FrameworkElement owner, HmiColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!Enum.IsDefined(scheme)) throw new ArgumentOutOfRangeException(nameof(scheme));
        uint[] rgb = scheme switch
        {
            HmiColorScheme.Light => [0xE8EBEF, 0xF7F8FA, 0xFFFFFF, 0xDCE1E7, 0x202B38, 0x586778, 0x175B96, 0xBFC8D1, 0xB52E3B, 0x906000],
            HmiColorScheme.Dark => [0x1D232B, 0x262E38, 0x2B3541, 0x171E27, 0xEDF2F8, 0xAFBDCD, 0x8EC7FF, 0x48596B, 0xFF8B99, 0xFFCA72],
            _ => [0x000000, 0x000000, 0x080808, 0x000000, 0xFFFFFF, 0xFFFFFF, 0x00FFFF, 0xFFFFFF, 0xFFA0AE, 0xFFFF00]
        };
        string[] keys = ["Dcs.Background", "Dcs.Surface", "Dcs.Panel", "Dcs.Header", "Dcs.Text", "Dcs.Muted", "Dcs.Accent", "Dcs.Border", "Dcs.Alarm", "Dcs.Warning"];
        for (int i = 0; i < keys.Length; i++)
        {
            uint c = rgb[i]; owner.Resources[keys[i]] = new SolidColorBrush(new Vector4(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f, 1));
        }
        foreach (var (key, role) in new (string, string)[] {
            ("TextPrimary", "Text"), ("TextSecondary", "Muted"), ("PageBackground", "Background"),
            ("CardBackground", "Surface"), ("ControlBackground", "Panel"), ("HeaderBackground", "Header"),
            ("ControlBackgroundHover", "Header"), ("ControlBorder", "Border"), ("SystemAccentColor", "Accent"), ("SelectionHighlight", "Header") })
            owner.Resources[key] = owner.Resources["Dcs." + role];
        owner.RequestedTheme = scheme == HmiColorScheme.Light ? ElementTheme.Light : ElementTheme.Dark;
    }
    public static ThemeResourceBrush Reference(FrameworkElement owner, string role) => new(owner.Resources, "Dcs." + role);
}
