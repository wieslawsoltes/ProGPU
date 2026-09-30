namespace ProGPU.Hmi;

/// <summary>A document connection, not an equipment command or a network connection profile.</summary>
public sealed class HmiDiagramLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Process connection";
    public HmiLinkEndpoint Source { get; set; } = new();
    public HmiLinkEndpoint Target { get; set; } = new();
    public HmiLinkKind Kind { get; set; }
    public string ActivityTag { get; set; } = "";
    public float Thickness { get; set; } = 3;
    public float Clearance { get; set; } = 12;
    public bool ShowDirection { get; set; } = true;
    public bool IsHidden { get; set; }
    public bool IsLocked { get; set; }

    public HmiDiagramLink Copy(bool newIdentity = false) => new()
    {
        Id = newIdentity ? Guid.NewGuid().ToString("N") : Id, Name = Name,
        Source = Source.Copy(), Target = Target.Copy(), Kind = Kind, ActivityTag = ActivityTag,
        Thickness = Thickness, Clearance = Clearance, ShowDirection = ShowDirection,
        IsHidden = IsHidden, IsLocked = IsLocked
    };
}

public enum HmiLinkKind { Process, Signal, Electrical }

public sealed class HmiLinkEndpoint
{
    public string ElementId { get; set; } = "";
    public string PortId { get; set; } = "";
    public HmiLinkEndpoint Copy() => new() { ElementId = ElementId, PortId = PortId };
}

public enum HmiPortDirection { Left, Top, Right, Bottom }
public readonly record struct HmiPoint(float X, float Y);

/// <summary>Port positions in the original ProGPU symbol's 0..100 geometry frame.</summary>
public sealed record HmiSymbolPort(string Id, string Name, HmiPoint Position, HmiPortDirection Direction);
