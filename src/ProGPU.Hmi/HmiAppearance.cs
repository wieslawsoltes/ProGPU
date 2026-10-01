namespace ProGPU.Hmi;

/// <summary>Application-local presentation theme. It never changes process state or tag quality.</summary>
public enum HmiColorScheme { Light, Dark, HighContrast }

/// <summary>Equipment may be composed directly on a process drawing or enclosed in an instrument card.</summary>
public enum HmiPresentation { Automatic, Process, Card }

/// <summary>Persisted, transport-independent visual configuration. Alarm/quality indication cannot be disabled.</summary>
public sealed class HmiAppearance
{
    public HmiPresentation Presentation { get; set; }
    public HmiGraphicStyle GraphicStyle { get; set; }
    public float CaptionFontSize { get; set; }
    public HmiCaptionAlignment CaptionAlignment { get; set; }
    public string InstrumentCode { get; set; } = "";
    public string InstrumentLoop { get; set; } = "";
    public HmiInstrumentLocation InstrumentLocation { get; set; }
    public double? NormalMinimum { get; set; }
    public double? NormalMaximum { get; set; }
    public bool ShowTagName { get; set; } = true;
    public bool ShowEngineeringRange { get; set; } = true;
    public bool ShowConnectionPorts { get; set; } = true;
    public bool AnimateFlow { get; set; }
    public bool ShowValue { get; set; } = true;
    public int QuarterTurns { get; set; }
    public bool MirrorHorizontal { get; set; }
    public bool MirrorVertical { get; set; }

    public void ValidateForRange(double minimum, double maximum)
    {
        Validate();
        if (NormalMinimum is { } low && (low < minimum || NormalMaximum!.Value > maximum))
            throw new InvalidDataException("The display operating band must lie within the component engineering range.");
    }

    private static void ValidateIdentifier(string value, int limit, string name)
    {
        if (value is null || value.Length > limit || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.' and not '/'))
            throw new InvalidDataException($"Invalid {name}: use at most {limit} ASCII letters, digits, '-', '.' or '/'.");
    }

    public HmiAppearance Copy() => (HmiAppearance)MemberwiseClone();

    public void Validate()
    {
        if (!Enum.IsDefined(GraphicStyle) || !Enum.IsDefined(CaptionAlignment) || !Enum.IsDefined(InstrumentLocation))
            throw new InvalidDataException("Unknown graphic style, caption alignment or instrument location.");
        if (!float.IsFinite(CaptionFontSize) || CaptionFontSize != 0 && (CaptionFontSize is < 8 or > 72))
            throw new InvalidDataException("Caption size must be automatic (0), or 8–72 document units.");
        ValidateIdentifier(InstrumentCode, 8, "instrument function");
        ValidateIdentifier(InstrumentLoop, 16, "instrument loop");
        if (NormalMinimum.HasValue != NormalMaximum.HasValue ||
            NormalMinimum is { } low && (!double.IsFinite(low) || !double.IsFinite(NormalMaximum!.Value) || low >= NormalMaximum.Value))
            throw new InvalidDataException("A normal operating band requires two finite, increasing engineering values.");
        if (QuarterTurns is < 0 or > 3) throw new InvalidDataException("Symbol rotation must be 0–3 clockwise quarter turns.");
        if (!Enum.IsDefined(Presentation))
            throw new InvalidDataException("Unknown HMI presentation. Choose Automatic, Process or Card.");
    }
}
