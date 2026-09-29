using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using ProGPU.Hmi;
using ProGPU.Vector;

namespace ProGPU.WinUI.Hmi;

public enum HmiBrushRole { Surface, Workspace, Body, Border, Text, Muted, Accent, Running, Warning, Fault, Outline, Maintenance }

/// <summary>Semantic palette materials and dynamic WinUI resource references. Do not mutate shared drawing materials.</summary>
public static class HmiThemeResources
{
    private sealed class References
    {
        internal readonly ResourceDictionary Resources = new();
        internal readonly Dictionary<HmiBrushRole, ThemeResourceBrush> Brushes = [];
        internal References(HmiColorScheme scheme)
        {
            foreach (var role in Enum.GetValues<HmiBrushRole>())
            {
                string key = "Hmi." + role;
                Resources[key] = GetBrush(scheme, role);
                Brushes.Add(role, new ThemeResourceBrush(Resources, key));
            }
        }
    }
    private static readonly ConditionalWeakTable<HmiPalette, References> ReferenceCache = new();

    /// <summary>Use for WinUI dependency properties so resource provenance is retained by the framework theme map.</summary>
    public static ThemeResourceBrush GetReference(HmiColorScheme scheme, HmiBrushRole role)
    {
        var palette = HmiPalette.Get(scheme);
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        if (!ReferenceCache.TryGetValue(palette, out var references))
            references = ReferenceCache.GetValue(palette, _ => new References(scheme));
        return references.Brushes[role];
    }

    /// <summary>Use only for retained drawing and palette resource definitions, not to replace WinUI theme bindings.</summary>
    public static Brush GetBrush(HmiColorScheme scheme, HmiBrushRole role)
    {
        var p = HmiPalette.Get(scheme);
        return role switch
        {
            HmiBrushRole.Surface => p.Surface, HmiBrushRole.Workspace => p.Workspace,
            HmiBrushRole.Body => p.Body, HmiBrushRole.Border => p.Track,
            HmiBrushRole.Text => p.Text, HmiBrushRole.Muted => p.Muted,
            HmiBrushRole.Accent => p.Accent, HmiBrushRole.Running => p.Running,
            HmiBrushRole.Warning => p.Warning, HmiBrushRole.Fault => p.Fault,
            HmiBrushRole.Outline => p.Edge, HmiBrushRole.Maintenance => p.Maintenance,
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
    }
    internal static ThemeResourceBrush StatusReference(HmiColorScheme scheme, HmiVisualTone tone, bool active, bool unknown) =>
        GetReference(scheme, unknown ? HmiBrushRole.Warning : tone switch
        {
            HmiVisualTone.Fault => HmiBrushRole.Fault,
            HmiVisualTone.Warning => HmiBrushRole.Warning,
            HmiVisualTone.Maintenance => HmiBrushRole.Maintenance,
            HmiVisualTone.Running => HmiBrushRole.Running,
            _ => active ? HmiBrushRole.Running : HmiBrushRole.Muted
        });
}
