namespace ProGPU.Hmi;

/// <summary>Presentation conventions, not declarations of ISA/ISO/IEC certification.</summary>
public enum HmiGraphicStyle { Process, HighPerformance, Schematic }

/// <summary>Explicit instrument mounting annotation. It is not inferred from a tag or network address.</summary>
public enum HmiInstrumentLocation { Field, PanelFront, PanelRear }

public enum HmiCaptionAlignment { Start, Center, End }
