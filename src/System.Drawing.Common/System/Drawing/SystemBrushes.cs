namespace System.Drawing;

public static class SystemBrushes
{
    public static Brush FromSystemColor(Color c)
    {
        if (!c.IsSystemColor)
            throw new ArgumentException("Color must be a system color.");
        return KnownColorResources.GetSystemBrush(c.ToKnownColor());
    }

    public static Brush ActiveBorder => KnownColorResources.GetSystemBrush(KnownColor.ActiveBorder);
    public static Brush ActiveCaption => KnownColorResources.GetSystemBrush(KnownColor.ActiveCaption);
    public static Brush ActiveCaptionText => KnownColorResources.GetSystemBrush(KnownColor.ActiveCaptionText);
    public static Brush AppWorkspace => KnownColorResources.GetSystemBrush(KnownColor.AppWorkspace);
    public static Brush ButtonFace => KnownColorResources.GetSystemBrush(KnownColor.ButtonFace);
    public static Brush ButtonHighlight => KnownColorResources.GetSystemBrush(KnownColor.ButtonHighlight);
    public static Brush ButtonShadow => KnownColorResources.GetSystemBrush(KnownColor.ButtonShadow);
    public static Brush Control => KnownColorResources.GetSystemBrush(KnownColor.Control);
    public static Brush ControlDark => KnownColorResources.GetSystemBrush(KnownColor.ControlDark);
    public static Brush ControlDarkDark => KnownColorResources.GetSystemBrush(KnownColor.ControlDarkDark);
    public static Brush ControlLight => KnownColorResources.GetSystemBrush(KnownColor.ControlLight);
    public static Brush ControlLightLight => KnownColorResources.GetSystemBrush(KnownColor.ControlLightLight);
    public static Brush ControlText => KnownColorResources.GetSystemBrush(KnownColor.ControlText);
    public static Brush Desktop => KnownColorResources.GetSystemBrush(KnownColor.Desktop);
    public static Brush GradientActiveCaption => KnownColorResources.GetSystemBrush(KnownColor.GradientActiveCaption);
    public static Brush GradientInactiveCaption => KnownColorResources.GetSystemBrush(KnownColor.GradientInactiveCaption);
    public static Brush GrayText => KnownColorResources.GetSystemBrush(KnownColor.GrayText);
    public static Brush Highlight => KnownColorResources.GetSystemBrush(KnownColor.Highlight);
    public static Brush HighlightText => KnownColorResources.GetSystemBrush(KnownColor.HighlightText);
    public static Brush HotTrack => KnownColorResources.GetSystemBrush(KnownColor.HotTrack);
    public static Brush InactiveBorder => KnownColorResources.GetSystemBrush(KnownColor.InactiveBorder);
    public static Brush InactiveCaption => KnownColorResources.GetSystemBrush(KnownColor.InactiveCaption);
    public static Brush InactiveCaptionText => KnownColorResources.GetSystemBrush(KnownColor.InactiveCaptionText);
    public static Brush Info => KnownColorResources.GetSystemBrush(KnownColor.Info);
    public static Brush InfoText => KnownColorResources.GetSystemBrush(KnownColor.InfoText);
    public static Brush Menu => KnownColorResources.GetSystemBrush(KnownColor.Menu);
    public static Brush MenuBar => KnownColorResources.GetSystemBrush(KnownColor.MenuBar);
    public static Brush MenuHighlight => KnownColorResources.GetSystemBrush(KnownColor.MenuHighlight);
    public static Brush MenuText => KnownColorResources.GetSystemBrush(KnownColor.MenuText);
    public static Brush ScrollBar => KnownColorResources.GetSystemBrush(KnownColor.ScrollBar);
    public static Brush Window => KnownColorResources.GetSystemBrush(KnownColor.Window);
    public static Brush WindowFrame => KnownColorResources.GetSystemBrush(KnownColor.WindowFrame);
    public static Brush WindowText => KnownColorResources.GetSystemBrush(KnownColor.WindowText);
}
