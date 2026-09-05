namespace System.Drawing;

public static class SystemPens
{
    public static Pen FromSystemColor(Color c)
    {
        if (!c.IsSystemColor)
            throw new ArgumentException("Color must be a system color.", nameof(c));
        return KnownColorResources.GetPen(c.ToKnownColor());
    }

    public static Pen ActiveBorder => KnownColorResources.GetPen(KnownColor.ActiveBorder);
    public static Pen ActiveCaption => KnownColorResources.GetPen(KnownColor.ActiveCaption);
    public static Pen ActiveCaptionText => KnownColorResources.GetPen(KnownColor.ActiveCaptionText);
    public static Pen AppWorkspace => KnownColorResources.GetPen(KnownColor.AppWorkspace);
    public static Pen ButtonFace => KnownColorResources.GetPen(KnownColor.ButtonFace);
    public static Pen ButtonHighlight => KnownColorResources.GetPen(KnownColor.ButtonHighlight);
    public static Pen ButtonShadow => KnownColorResources.GetPen(KnownColor.ButtonShadow);
    public static Pen Control => KnownColorResources.GetPen(KnownColor.Control);
    public static Pen ControlDark => KnownColorResources.GetPen(KnownColor.ControlDark);
    public static Pen ControlDarkDark => KnownColorResources.GetPen(KnownColor.ControlDarkDark);
    public static Pen ControlLight => KnownColorResources.GetPen(KnownColor.ControlLight);
    public static Pen ControlLightLight => KnownColorResources.GetPen(KnownColor.ControlLightLight);
    public static Pen ControlText => KnownColorResources.GetPen(KnownColor.ControlText);
    public static Pen Desktop => KnownColorResources.GetPen(KnownColor.Desktop);
    public static Pen GradientActiveCaption => KnownColorResources.GetPen(KnownColor.GradientActiveCaption);
    public static Pen GradientInactiveCaption => KnownColorResources.GetPen(KnownColor.GradientInactiveCaption);
    public static Pen GrayText => KnownColorResources.GetPen(KnownColor.GrayText);
    public static Pen Highlight => KnownColorResources.GetPen(KnownColor.Highlight);
    public static Pen HighlightText => KnownColorResources.GetPen(KnownColor.HighlightText);
    public static Pen HotTrack => KnownColorResources.GetPen(KnownColor.HotTrack);
    public static Pen InactiveBorder => KnownColorResources.GetPen(KnownColor.InactiveBorder);
    public static Pen InactiveCaption => KnownColorResources.GetPen(KnownColor.InactiveCaption);
    public static Pen InactiveCaptionText => KnownColorResources.GetPen(KnownColor.InactiveCaptionText);
    public static Pen Info => KnownColorResources.GetPen(KnownColor.Info);
    public static Pen InfoText => KnownColorResources.GetPen(KnownColor.InfoText);
    public static Pen Menu => KnownColorResources.GetPen(KnownColor.Menu);
    public static Pen MenuBar => KnownColorResources.GetPen(KnownColor.MenuBar);
    public static Pen MenuHighlight => KnownColorResources.GetPen(KnownColor.MenuHighlight);
    public static Pen MenuText => KnownColorResources.GetPen(KnownColor.MenuText);
    public static Pen ScrollBar => KnownColorResources.GetPen(KnownColor.ScrollBar);
    public static Pen Window => KnownColorResources.GetPen(KnownColor.Window);
    public static Pen WindowFrame => KnownColorResources.GetPen(KnownColor.WindowFrame);
    public static Pen WindowText => KnownColorResources.GetPen(KnownColor.WindowText);
}
