namespace System.Drawing;

public static class SystemBrushes
{
    public static Brush FromSystemColor(Color c)
    {
        if (!c.IsSystemColor)
            throw new ArgumentException("Color must be a system color.", nameof(c));
        return KnownColorResources.GetBrush(c.ToKnownColor());
    }

    public static Brush ActiveBorder => KnownColorResources.GetBrush(KnownColor.ActiveBorder);
    public static Brush ActiveCaption => KnownColorResources.GetBrush(KnownColor.ActiveCaption);
    public static Brush ActiveCaptionText => KnownColorResources.GetBrush(KnownColor.ActiveCaptionText);
    public static Brush AppWorkspace => KnownColorResources.GetBrush(KnownColor.AppWorkspace);
    public static Brush ButtonFace => KnownColorResources.GetBrush(KnownColor.ButtonFace);
    public static Brush ButtonHighlight => KnownColorResources.GetBrush(KnownColor.ButtonHighlight);
    public static Brush ButtonShadow => KnownColorResources.GetBrush(KnownColor.ButtonShadow);
    public static Brush Control => KnownColorResources.GetBrush(KnownColor.Control);
    public static Brush ControlDark => KnownColorResources.GetBrush(KnownColor.ControlDark);
    public static Brush ControlDarkDark => KnownColorResources.GetBrush(KnownColor.ControlDarkDark);
    public static Brush ControlLight => KnownColorResources.GetBrush(KnownColor.ControlLight);
    public static Brush ControlLightLight => KnownColorResources.GetBrush(KnownColor.ControlLightLight);
    public static Brush ControlText => KnownColorResources.GetBrush(KnownColor.ControlText);
    public static Brush Desktop => KnownColorResources.GetBrush(KnownColor.Desktop);
    public static Brush GradientActiveCaption => KnownColorResources.GetBrush(KnownColor.GradientActiveCaption);
    public static Brush GradientInactiveCaption => KnownColorResources.GetBrush(KnownColor.GradientInactiveCaption);
    public static Brush GrayText => KnownColorResources.GetBrush(KnownColor.GrayText);
    public static Brush Highlight => KnownColorResources.GetBrush(KnownColor.Highlight);
    public static Brush HighlightText => KnownColorResources.GetBrush(KnownColor.HighlightText);
    public static Brush HotTrack => KnownColorResources.GetBrush(KnownColor.HotTrack);
    public static Brush InactiveBorder => KnownColorResources.GetBrush(KnownColor.InactiveBorder);
    public static Brush InactiveCaption => KnownColorResources.GetBrush(KnownColor.InactiveCaption);
    public static Brush InactiveCaptionText => KnownColorResources.GetBrush(KnownColor.InactiveCaptionText);
    public static Brush Info => KnownColorResources.GetBrush(KnownColor.Info);
    public static Brush InfoText => KnownColorResources.GetBrush(KnownColor.InfoText);
    public static Brush Menu => KnownColorResources.GetBrush(KnownColor.Menu);
    public static Brush MenuBar => KnownColorResources.GetBrush(KnownColor.MenuBar);
    public static Brush MenuHighlight => KnownColorResources.GetBrush(KnownColor.MenuHighlight);
    public static Brush MenuText => KnownColorResources.GetBrush(KnownColor.MenuText);
    public static Brush ScrollBar => KnownColorResources.GetBrush(KnownColor.ScrollBar);
    public static Brush Window => KnownColorResources.GetBrush(KnownColor.Window);
    public static Brush WindowFrame => KnownColorResources.GetBrush(KnownColor.WindowFrame);
    public static Brush WindowText => KnownColorResources.GetBrush(KnownColor.WindowText);
}
