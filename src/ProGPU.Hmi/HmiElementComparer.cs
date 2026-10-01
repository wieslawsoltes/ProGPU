namespace ProGPU.Hmi;

/// <summary>Explicit design-state equality; runtime samples and event subscriptions are never compared.</summary>
public static class HmiElementComparer
{
    public static bool Equals(HmiElement left, HmiElement right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        if (ReferenceEquals(left, right)) return true;
        if (left.Id != right.Id || left.Name != right.Name || left.Symbol != right.Symbol ||
            left.X != right.X || left.Y != right.Y || left.Width != right.Width || left.Height != right.Height ||
            left.Label != right.Label || left.Unit != right.Unit || left.Tag != right.Tag ||
            left.VisibilityTag != right.VisibilityTag || left.EnabledTag != right.EnabledTag ||
            left.Minimum != right.Minimum || left.Maximum != right.Maximum || left.Decimals != right.Decimals ||
            left.IsLocked != right.IsLocked || left.IsHidden != right.IsHidden || left.Group != right.Group ||
            left.FaceplateTemplateId != right.FaceplateTemplateId || left.FaceplateInstanceId != right.FaceplateInstanceId ||
            left.FaceplateSourceId != right.FaceplateSourceId || left.FaceplatePrefix != right.FaceplatePrefix ||
            left.FaceplateSourceX != right.FaceplateSourceX || left.FaceplateSourceY != right.FaceplateSourceY ||
            left.Action.Kind != right.Action.Kind || left.Action.Target != right.Action.Target || left.Action.Value != right.Action.Value ||
            left.Trend.WindowSeconds != right.Trend.WindowSeconds || left.Trend.MaximumGapSeconds != right.Trend.MaximumGapSeconds ||
            left.Appearance.Presentation != right.Appearance.Presentation ||
            left.Appearance.GraphicStyle != right.Appearance.GraphicStyle ||
            left.Appearance.CaptionFontSize != right.Appearance.CaptionFontSize ||
            left.Appearance.CaptionAlignment != right.Appearance.CaptionAlignment ||
            left.Appearance.InstrumentCode != right.Appearance.InstrumentCode ||
            left.Appearance.InstrumentLoop != right.Appearance.InstrumentLoop ||
            left.Appearance.InstrumentLocation != right.Appearance.InstrumentLocation ||
            left.Appearance.NormalMinimum != right.Appearance.NormalMinimum ||
            left.Appearance.NormalMaximum != right.Appearance.NormalMaximum ||
            left.Appearance.ShowTagName != right.Appearance.ShowTagName ||
            left.Appearance.ShowEngineeringRange != right.Appearance.ShowEngineeringRange ||
            left.Appearance.ShowConnectionPorts != right.Appearance.ShowConnectionPorts ||
            left.Appearance.AnimateFlow != right.Appearance.AnimateFlow ||
            left.Appearance.ShowValue != right.Appearance.ShowValue ||
            left.Appearance.QuarterTurns != right.Appearance.QuarterTurns ||
            left.Appearance.MirrorHorizontal != right.Appearance.MirrorHorizontal ||
            left.Appearance.MirrorVertical != right.Appearance.MirrorVertical ||
            left.States.Count != right.States.Count) return false;
        for (int i = 0; i < left.States.Count; i++)
        {
            var a = left.States[i]; var b = right.States[i];
            if (a.Tag != b.Tag || a.Condition != b.Condition || a.Threshold != b.Threshold || a.Tone != b.Tone || a.Text != b.Text || a.Priority != b.Priority) return false;
        }
        return true;
    }
}
