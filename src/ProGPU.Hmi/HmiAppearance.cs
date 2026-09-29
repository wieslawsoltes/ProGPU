namespace ProGPU.Hmi;

/// <summary>Application-local presentation theme. It never changes process state or tag quality.</summary>
public enum HmiColorScheme { Light, Dark, HighContrast }

/// <summary>Equipment may be composed directly on a process drawing or enclosed in an instrument card.</summary>
public enum HmiPresentation { Automatic, Process, Card }

/// <summary>Persisted, transport-independent visual configuration. Alarm/quality indication cannot be disabled.</summary>
public sealed class HmiAppearance
{
    public HmiPresentation Presentation { get; set; }
    public bool ShowTagName { get; set; } = true;
    public bool ShowEngineeringRange { get; set; } = true;
    public bool ShowConnectionPorts { get; set; } = true;
    public bool AnimateFlow { get; set; }
    public bool ShowValue { get; set; } = true;
    public int QuarterTurns { get; set; }
    public bool MirrorHorizontal { get; set; }
    public bool MirrorVertical { get; set; }

    public HmiAppearance Copy() => (HmiAppearance)MemberwiseClone();

    public void Validate()
    {
        if (QuarterTurns is < 0 or > 3) throw new InvalidDataException("Symbol rotation must be 0–3 clockwise quarter turns.");
        if (!Enum.IsDefined(Presentation))
            throw new InvalidDataException("Unknown HMI presentation. Choose Automatic, Process or Card.");
    }
}
