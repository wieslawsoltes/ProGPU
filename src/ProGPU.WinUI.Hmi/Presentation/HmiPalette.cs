using System.Numerics;
using ProGPU.Hmi;
using ProGPU.Vector;

namespace ProGPU.WinUI.Hmi;

/// <summary>Immutable application palettes. Normal equipment is neutral; status colour is a separate signal.</summary>
internal sealed class HmiPalette
{
    private static Brush B(uint rgb) => new SolidColorBrush(new Vector4(
        ((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1));

    internal static readonly HmiPalette Light = new(false, 0xF8FAFC, 0xE9EEF3, 0xD9E2EB, 0xFFFFFF,
        0x60758A, 0x162B40, 0x52677B, 0xC5D2DF, 0x146F86, 0x177553, 0x966000, 0xB22D43, 0x7049A4);
    internal static readonly HmiPalette Dark = new(false, 0x172331, 0x101923, 0x2C3C4D, 0x3F5367,
        0x93A8BC, 0xF0F5FA, 0xA8BDCF, 0x35495C, 0x72D3E4, 0x7DD5B0, 0xFFD17D, 0xFF8FA1, 0xC4A4F1);
    internal static readonly HmiPalette Contrast = new(true, 0x000000, 0x000000, 0x151515, 0x000000,
        0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0x00FFFF, 0x75FFB0, 0xFFFF00, 0xFF91A4, 0xDFC0FF);

    private static readonly HmiPalette[] Neutral = [new(Light, false), new(Dark, false), new(Contrast, false)];
    private static readonly HmiPalette[] LineArt = [new(Light, true), new(Dark, true), new(Contrast, true)];
    internal HmiGraphicStyle GraphicStyle { get; }
    internal bool HighContrast { get; }
    internal Brush Surface { get; }
    internal Brush Workspace { get; }
    internal Brush Body { get; }
    internal Brush Highlight { get; }
    internal Brush Edge { get; }
    internal Brush Text { get; }
    internal Brush Muted { get; }
    internal Brush Track { get; }
    internal Brush Accent { get; }
    internal Brush Running { get; }
    internal Brush Warning { get; }
    internal Brush Fault { get; }
    internal Brush Maintenance { get; }
    internal Pen Outline { get; }
    internal Pen Fine { get; }
    internal Pen Grid { get; }
    internal Pen AccentLine { get; }
    internal Pen RunningLine { get; }
    internal Pen WarningLine { get; }
    internal Pen FaultLine { get; }
    internal Pen MaintenanceLine { get; }
    internal Pen Strong { get; }

    private HmiPalette(bool contrast, uint surface, uint workspace, uint body, uint highlight, uint edge,
        uint text, uint muted, uint track, uint accent, uint running, uint warning, uint fault, uint maintenance)
    {
        HighContrast = contrast;
        Surface = B(surface); Workspace = B(workspace); Body = B(body); Highlight = B(highlight);
        Edge = B(edge); Text = B(text); Muted = B(muted); Track = B(track); Accent = B(accent);
        Running = B(running); Warning = B(warning); Fault = B(fault); Maintenance = B(maintenance);
        Outline = new Pen(Edge, contrast ? 2 : 1.6f); Fine = new Pen(Edge, 1);
        Grid = new Pen(Track, 1); Strong = new Pen(Text, 2);
        AccentLine = new Pen(Accent, 2.2f); RunningLine = new Pen(Running, 2.2f);
        WarningLine = new Pen(Warning, 2.2f); FaultLine = new Pen(Fault, 2.2f);
        MaintenanceLine = new Pen(Maintenance, 2.2f);
    }

    private HmiPalette(HmiPalette source, bool schematic)
    {
        GraphicStyle = schematic ? HmiGraphicStyle.Schematic : HmiGraphicStyle.HighPerformance;
        HighContrast = source.HighContrast;
        Surface = source.Surface; Workspace = source.Workspace;
        Body = schematic ? source.Surface : source.Body;
        Highlight = schematic ? source.Surface : source.Highlight;
        Edge = source.Edge; Text = source.Text; Muted = source.Muted; Track = source.Track;
        Accent = source.Muted; Running = source.Text;
        Warning = source.Warning; Fault = source.Fault; Maintenance = source.Maintenance;
        Outline = source.Outline; Fine = source.Fine; Grid = source.Grid; Strong = source.Strong;
        AccentLine = source.Outline; RunningLine = source.Strong;
        WarningLine = source.WarningLine; FaultLine = source.FaultLine; MaintenanceLine = source.MaintenanceLine;
    }

    internal static HmiPalette Get(HmiColorScheme scheme, HmiGraphicStyle style)
    {
        if (!Enum.IsDefined(scheme) || !Enum.IsDefined(style)) throw new ArgumentOutOfRangeException(nameof(style));
        return style switch { HmiGraphicStyle.HighPerformance => Neutral[(int)scheme], HmiGraphicStyle.Schematic => LineArt[(int)scheme], _ => Get(scheme) };
    }

    internal static HmiPalette Get(HmiColorScheme scheme) => scheme switch
    {
        HmiColorScheme.Light => Light,
        HmiColorScheme.Dark => Dark,
        HmiColorScheme.HighContrast => Contrast,
        _ => throw new ArgumentOutOfRangeException(nameof(scheme))
    };

    internal Brush Status(HmiVisualTone tone, bool active, bool unknown) => unknown ? Warning : tone switch
    {
        HmiVisualTone.Fault => Fault,
        HmiVisualTone.Warning => Warning,
        HmiVisualTone.Maintenance => Maintenance,
        HmiVisualTone.Running => Running,
        _ => active ? Running : Muted
    };

    internal Pen StatusPen(HmiVisualTone tone, bool active, bool unknown) => unknown ? WarningLine : tone switch
    {
        HmiVisualTone.Fault => FaultLine,
        HmiVisualTone.Warning => WarningLine,
        HmiVisualTone.Maintenance => MaintenanceLine,
        HmiVisualTone.Running => RunningLine,
        _ => active ? RunningLine : Outline
    };
}
