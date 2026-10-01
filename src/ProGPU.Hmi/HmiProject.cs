namespace ProGPU.Hmi;

public sealed class HmiProject
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "HMI project";
    public string StartScreenId { get; set; } = "overview";
    public List<HmiScreen> Screens { get; set; } = [];
    public List<HmiTagDefinition> Tags { get; set; } = [];
    public List<HmiAlarmDefinition> Alarms { get; set; } = [];
    public List<HmiRecipe> Recipes { get; set; } = [];
    public List<HmiConnectionProfile> Connections { get; set; } = [];
    public List<HmiFaceplateTemplate> Faceplates { get; set; } = [];
}

public sealed class HmiScreen
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Screen";
    public float Width { get; set; } = 1280;
    public float Height { get; set; } = 720;
    public List<HmiElement> Elements { get; set; } = [];
    public List<HmiDiagramLink> Links { get; set; } = [];
}

public enum HmiSymbol
{
    Label, NumericDisplay, NumericInput, Indicator, PushButton, ToggleSwitch,
    Tank, Pump, Valve, Motor, Pipe, Conveyor, Gauge, BarGraph, Trend,
    AlarmBanner, AlarmList, NavigationButton, RecipeButton, Rectangle,
    HeatExchanger, Filter, Compressor, Fan, Heater, Thermometer, Boiler, CoolingTower,
    ControlValve, CheckValve, ButterflyValve, Agitator, Silo, Hopper, Separator, Reactor, FlowMeter, Strainer, PressureTransmitter, LevelTransmitter,
    InstrumentBubble, ControlFunction, NormallyOpenContact, NormallyClosedContact,
    RelayCoil, CircuitBreaker, Transformer, ProtectiveEarth
}

public enum HmiActionKind { None, ToggleTag, WriteTag, Navigate, AcknowledgeAlarms, ApplyRecipe }

public sealed class HmiAction
{
    public HmiActionKind Kind { get; set; }
    public string Target { get; set; } = "";
    public HmiValue Value { get; set; } = HmiValue.From(false);
}

/// <summary>Persisted design state; transient samples and event handlers never enter a project.</summary>
public sealed class HmiElement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Component";
    public HmiSymbol Symbol { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; } = 160;
    public float Height { get; set; } = 100;
    public string Label { get; set; } = "Component";
    public string Unit { get; set; } = "";
    public string Tag { get; set; } = "";
    public string VisibilityTag { get; set; } = "";
    public string EnabledTag { get; set; } = "";
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100;
    public int Decimals { get; set; } = 1;
    public bool IsLocked { get; set; }
    public bool IsHidden { get; set; }
    public string Group { get; set; } = "";
    public HmiAction Action { get; set; } = new();
    public List<HmiStateRule> States { get; set; } = [];
    public HmiTrendOptions Trend { get; set; } = new();
    public HmiAppearance Appearance { get; set; } = new();
    public string FaceplateTemplateId { get; set; } = "";
    public string FaceplateInstanceId { get; set; } = "";
    public string FaceplateSourceId { get; set; } = "";
    public string FaceplatePrefix { get; set; } = "";
    public float FaceplateSourceX { get; set; }
    public float FaceplateSourceY { get; set; }

    public HmiElement Copy(bool newIdentity = false) => new()
    {
        Id = newIdentity ? Guid.NewGuid().ToString("N") : Id,
        Name = Name, Symbol = Symbol, X = X, Y = Y, Width = Width, Height = Height,
        Label = Label, Unit = Unit, Tag = Tag, VisibilityTag = VisibilityTag, EnabledTag = EnabledTag,
        Minimum = Minimum, Maximum = Maximum, Decimals = Decimals, IsLocked = IsLocked,
        IsHidden = IsHidden, Group = Group,
        Action = new HmiAction { Kind = Action.Kind, Target = Action.Target, Value = Action.Value },
        States = States.Select(s => s.Copy()).ToList(),
        Trend = Trend.Copy(), Appearance = Appearance.Copy(),
        FaceplateTemplateId = newIdentity ? "" : FaceplateTemplateId,
        FaceplateInstanceId = newIdentity ? "" : FaceplateInstanceId,
        FaceplateSourceId = newIdentity ? "" : FaceplateSourceId,
        FaceplatePrefix = newIdentity ? "" : FaceplatePrefix,
        FaceplateSourceX = newIdentity ? 0 : FaceplateSourceX,
        FaceplateSourceY = newIdentity ? 0 : FaceplateSourceY
    };
}
