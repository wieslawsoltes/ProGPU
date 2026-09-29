"""Exact-match integration of additive HMI modules. This development helper is removed after validation."""
from pathlib import Path


def replace(path, old, new, count=1):
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    actual = text.count(old)
    if actual != count:
        raise RuntimeError(f"{path}: expected {count} occurrences, found {actual}: {old[:100]!r}")
    p.write_text(text.replace(old, new), encoding="utf-8", newline="\n")


replace("Directory.Packages.props", '    <PackageVersion Include="Moq"', '    <PackageVersion Include="MQTTnet" Version="5.2.0.1603" />\n    <PackageVersion Include="MQTTnet.Server" Version="5.2.0.1603" />\n    <PackageVersion Include="Moq"')
model = "src/ProGPU.Hmi/HmiProject.cs"
replace(model, '    public List<HmiRecipe> Recipes { get; set; } = [];', '    public List<HmiRecipe> Recipes { get; set; } = [];\n    public List<HmiConnectionProfile> Connections { get; set; } = [];\n    public List<HmiFaceplateTemplate> Faceplates { get; set; } = [];')
replace(model, 'AlarmBanner, AlarmList, NavigationButton, RecipeButton, Rectangle', 'AlarmBanner, AlarmList, NavigationButton, RecipeButton, Rectangle,\n    HeatExchanger, Filter, Compressor, Fan, Heater, Thermometer, Boiler, CoolingTower')
replace(model, '    public HmiAction Action { get; set; } = new();', '    public HmiAction Action { get; set; } = new();\n    public List<HmiStateRule> States { get; set; } = [];\n    public string FaceplateTemplateId { get; set; } = "";\n    public string FaceplateInstanceId { get; set; } = "";\n    public string FaceplateSourceId { get; set; } = "";\n    public string FaceplatePrefix { get; set; } = "";')
replace(model, '        Action = new HmiAction { Kind = Action.Kind, Target = Action.Target, Value = Action.Value }', '''        Action = new HmiAction { Kind = Action.Kind, Target = Action.Target, Value = Action.Value },
        States = States.Select(s => s.Copy()).ToList(),
        FaceplateTemplateId = newIdentity ? "" : FaceplateTemplateId,
        FaceplateInstanceId = newIdentity ? "" : FaceplateInstanceId,
        FaceplateSourceId = newIdentity ? "" : FaceplateSourceId,
        FaceplatePrefix = newIdentity ? "" : FaceplatePrefix''')
replace("src/ProGPU.Hmi/HmiProjectSerializer.cs", '        var alarmIds = new HashSet<string>(StringComparer.Ordinal);', '        HmiProjectExtensions.Validate(project);\n        var alarmIds = new HashSet<string>(StringComparer.Ordinal);')
runtime = "src/ProGPU.Hmi/HmiRuntime.cs"
replace(runtime, 'int historyCapacity = 600)', 'int historyCapacity = 600, bool initializeGoodQuality = true)')
replace(runtime, 'var sample = new HmiTagSample(tag.InitialValue, HmiQuality.Good, Now);', 'var sample = new HmiTagSample(tag.InitialValue, initializeGoodQuality ? HmiQuality.Good : HmiQuality.Uncertain, initializeGoodQuality ? Now : DateTimeOffset.MinValue);')
replace(runtime, '_effectiveQuality.Add(tag.Name, HmiQuality.Good);', '_effectiveQuality.Add(tag.Name, sample.Quality);')
replace(runtime, '            history.Add(sample);', '            if (initializeGoodQuality) history.Add(sample);')
replace(runtime, '''    public void Acknowledge(string? alarmId = null)
    {
        RequireCommandPermission();''', '''    public void Acknowledge(string? alarmId = null)
    {
        if (!IsRunning) throw new InvalidOperationException("Run the local alarm session before acknowledging.");''')

view = "src/ProGPU.WinUI.Hmi/HmiScreenView.cs"
replace(view, '    public event Action<string>? Error;', '    public Action<HmiElement, HmiValue?>? CommandRequested { get; set; }\n    public event Action<string>? Error;')
replace(view, 'new[] { definition.Tag, definition.VisibilityTag, definition.EnabledTag }.Where(t => t.Length > 0).Distinct()', 'new[] { definition.Tag, definition.VisibilityTag, definition.EnabledTag }.Concat(definition.States.Select(s => s.Tag)).Where(t => t.Length > 0).Distinct()')
replace(view, '            control.Invoked += sender => Execute(() => _runtime.Execute(_definitions[sender].Action));', '''            control.Invoked += sender => Execute(() =>
            {
                var element = _definitions[sender];
                if (CommandRequested != null && element.Action.Kind is not (HmiActionKind.None or HmiActionKind.Navigate or HmiActionKind.AcknowledgeAlarms))
                    CommandRequested(element.Copy(), null);
                else _runtime.Execute(element.Action);
            });''')
replace(view, '            control.ValueSubmitted += (sender, value) => Execute(() => _runtime.Write(_definitions[sender].Tag, value));', '''            control.ValueSubmitted += (sender, value) => Execute(() =>
            {
                if (CommandRequested != null) CommandRequested(_definitions[sender].Copy(), value);
                else _runtime.Write(_definitions[sender].Tag, value);
            });''')
replace(view, '        control.CommandsEnabled = _runtime.IsRunning;', '        control.CommandsEnabled = _runtime.IsRunning;\n        control.UpdateState(HmiStateEvaluator.Evaluate(definition.States, tag => _runtime.TryRead(tag, out var sample) ? sample : null));')
control = "src/ProGPU.WinUI.Hmi/HmiControl.cs"
replace(control, '    private HmiElement _definition;', '''    public static readonly DependencyProperty VisualToneProperty = DependencyProperty.Register(nameof(VisualTone), typeof(HmiVisualTone), typeof(HmiControl), new PropertyMetadata(HmiVisualTone.Normal, OnDisplayChanged) { AffectsRender = true });
    public static readonly DependencyProperty StateTextProperty = DependencyProperty.Register(nameof(StateText), typeof(string), typeof(HmiControl), new PropertyMetadata("", OnDisplayChanged) { AffectsRender = true });
    public HmiVisualTone VisualTone { get => (HmiVisualTone)(GetValue(VisualToneProperty) ?? HmiVisualTone.Normal); set => SetValue(VisualToneProperty, value); }
    public string StateText { get => (string)(GetValue(StateTextProperty) ?? ""); set => SetValue(StateTextProperty, value ?? ""); }
    public void UpdateState(HmiVisualState state)
    {
        if (VisualTone == state.Tone && StateText == state.Text) return;
        _batching = true;
        try { VisualTone = state.Tone; StateText = state.Text; }
        finally { _batching = false; }
        UpdateDisplay();
    }
    private HmiElement _definition;''')
replace(control, '_quality.Text = Quality == HmiQuality.Good ? "" : Quality.ToString().ToUpperInvariant();', '_quality.Text = Quality == HmiQuality.Good ? StateText : Quality.ToString().ToUpperInvariant();\n        _quality.Foreground = HmiEquipmentDrawing.ToneBrush(VisualTone, HmiDrawing.Warning);')
replace(control, '_history, _hasAlarm, _phase);', '_history, _hasAlarm, _phase, VisualTone);')
replace(control, 'HmiSymbol.Pump or HmiSymbol.Motor or HmiSymbol.Pipe or HmiSymbol.Conveyor)', 'HmiSymbol.Pump or HmiSymbol.Motor or HmiSymbol.Pipe or HmiSymbol.Conveyor or HmiSymbol.Fan)')

catalog = "src/ProGPU.WinUI.Hmi/HmiControlCatalog.cs"
symbols = [("HeatExchanger", "Heat exchanger", 220, 170), ("Filter", "Process filter", 170, 170), ("Compressor", "Compressor", 200, 175), ("Fan", "Ventilation fan", 170, 175), ("Heater", "Process heater", 180, 150), ("Thermometer", "Thermometer", 150, 245), ("Boiler", "Steam boiler", 205, 245), ("CoolingTower", "Cooling tower", 215, 235)]
replace(catalog, '        new(HmiSymbol.Tank,', ''.join(f'        new(HmiSymbol.{name}, "{label}", "Equipment", {w}, {h}),\n' for name, label, w, h in symbols) + '        new(HmiSymbol.Tank,')
replace(catalog, '            HmiSymbol.Tank =>', ''.join(f'            HmiSymbol.{name} => (HmiControl)new Hmi{name}(),\n' for name, _, _, _ in symbols) + '            HmiSymbol.Tank =>')
registration = "src/ProGPU.WinUI.Hmi.Designer/HmiDesignerRegistration.cs"
replace(registration, '            _registered = true;', ''.join(f'            Register<Hmi{name}>(HmiSymbol.{name}, () => new Hmi{name}());\n' for name, _, _, _ in symbols) + '            _registered = true;')
drawing = "src/ProGPU.WinUI.Hmi/HmiDrawing.cs"
replace(drawing, 'IReadOnlyList<HmiTagSample> history, bool alarm, float phase)', 'IReadOnlyList<HmiTagSample> history, bool alarm, float phase, HmiVisualTone tone = HmiVisualTone.Normal)')
replace(drawing, '        Pen statusLine = active && quality == HmiQuality.Good ? ActiveLine : InactiveLine;', '''        status = HmiEquipmentDrawing.ToneBrush(quality == HmiQuality.Good ? tone : HmiVisualTone.Unknown, status);
        Pen statusLine = HmiEquipmentDrawing.TonePen(quality == HmiQuality.Good ? tone : HmiVisualTone.Unknown, active && quality == HmiQuality.Good ? ActiveLine : InactiveLine);''')
replace(drawing, '        if (quality != HmiQuality.Good) dc.FillRoundedRectangle', '        HmiEquipmentDrawing.Draw(dc, symbol, w, top, bottom, fraction, status, active && quality == HmiQuality.Good ? phase : 0);\n        if (quality != HmiQuality.Good) dc.FillRoundedRectangle')

host = "src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.Tables.cs"
replace(host, '        tabs.Items.Add(new PivotItem("Help",', '''        tabs.Items.Add(new PivotItem("Connections", BuildConnectionsPane()));
        tabs.Items.Add(new PivotItem("Faceplates", BuildFaceplatesPane()));
        tabs.Items.Add(new PivotItem("States", BuildStateRulesPane()));
        tabs.Items.Add(new PivotItem("Help",''')
replace(host, '        _tags.ClearItems(); _alarms.ClearItems(); _recipes.ClearItems();', '        RefreshConnectionTables(); RefreshFaceplateList(); RefreshStateTable();\n        _tags.ClearItems(); _alarms.ClearItems(); _recipes.ClearItems();')
replace(host, '"Stop before editing. No PLC, OPC UA or MQTT transport is connected. These samples are not a safety controller.', '"Stop before editing. Simulation has no equipment connection. Connections starts explicit Modbus TCP or MQTT acquisition. These samples are not a safety controller.')
replace(host, '        _monitor.Text = $"LOCAL SIMULATION', '        _monitor.Text = (_acquisition == null ? "LOCAL SIMULATION\\n" : "LIVE ACQUISITION\\n" + _acquisition.Diagnostics + "\\n") + $"RUNTIME')
replace("src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.Inspector.cs", '        _properties.ClearItems();', '        RefreshStateTable();\n        _properties.ClearItems();')
preview = "src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.Runtime.cs"
replace(preview, '    public void StopPreview()\n    {', '    public void StopPreview()\n    {\n        StopHardwareAcquisition();')
replace(preview, '        _runtime.AdvanceSimulation(elapsed);', '        if (_acquisition != null) throw new InvalidOperationException("Simulation ticks are disabled during live acquisition.");\n        _runtime.AdvanceSimulation(elapsed);')
session = "src/ProGPU.WinUI.Hmi.Designer/HmiDesignerSession.cs"
replace(session, '                if (element.Tag == oldName)', '                foreach (var rule in element.States) if (rule.Tag == oldName) rule.Tag = newName;\n                if (element.Tag == oldName)')
replace(session, '            foreach (var alarm in p.Alarms)', '''            foreach (var mapping in p.Connections.SelectMany(c => c.Mappings))
            {
                if (mapping.Tag == oldName) mapping.Tag = newName;
                if (mapping.InterlockTag == oldName) mapping.InterlockTag = newName;
            }
            foreach (var alarm in p.Alarms)''')

for project in ["samples/HmiDesigner/HmiDesigner.csproj", "tests/ProGPU.Hmi.Tests/ProGPU.Hmi.Tests.csproj"]:
    replace(project, '  <ItemGroup>', '  <ItemGroup>\n    <ProjectReference Include="../../src/ProGPU.Hmi.Modbus/ProGPU.Hmi.Modbus.csproj" />\n    <ProjectReference Include="../../src/ProGPU.Hmi.Mqtt/ProGPU.Hmi.Mqtt.csproj" />')
replace("tests/ProGPU.Hmi.Tests/ProGPU.Hmi.Tests.csproj", '    <PackageReference Include="xunit"', '    <PackageReference Include="MQTTnet.Server" />\n    <PackageReference Include="xunit"')
replace("src/ProGPU.Samples/ProGPU.Samples.csproj", '    <ProjectReference Include="..\\ProGPU.WinUI.Hmi.Designer', '''    <ProjectReference Include="..\\ProGPU.Hmi.Modbus\\ProGPU.Hmi.Modbus.csproj" Condition="'$(ProGpuSamplesMobile)' != 'true'" />
    <ProjectReference Include="..\\ProGPU.Hmi.Mqtt\\ProGPU.Hmi.Mqtt.csproj" Condition="'$(ProGpuSamplesMobile)' != 'true'" />
    <ProjectReference Include="..\\ProGPU.WinUI.Hmi.Designer''')
factory = '''profile => profile.Protocol switch
            {
                ProGPU.Hmi.HmiConnectionProtocol.ModbusTcp => new ProGPU.Hmi.Modbus.HmiModbusConnection(profile),
                ProGPU.Hmi.HmiConnectionProtocol.Mqtt => new ProGPU.Hmi.Mqtt.HmiMqttConnection(profile),
                _ => throw new NotSupportedException("Protocol not registered.")
            }'''
replace("samples/HmiDesigner/Program.cs", 'var designer = new HmiDesignerHost();', 'var designer = new HmiDesignerHost { ConnectionFactory = ' + factory + ' };')
replace("src/ProGPU.Samples/Pages/VisualDesignerPage.cs", '        var hmi = new HmiDesignerHost(null, AppState._font)\n        {', '        var hmi = new HmiDesignerHost(null, AppState._font)\n        {\n            ConnectionFactory = ' + factory + ',')

manifest = "eng/progpu-package-list.sh"
packages = ["ProGPU.Hmi", "ProGPU.WinUI.Hmi", "ProGPU.WinUI.Hmi.Designer", "ProGPU.Hmi.Modbus", "ProGPU.Hmi.Mqtt"]
replace(manifest, '  ProGPU.WinUI.Designer\n', '  ProGPU.WinUI.Designer\n' + ''.join(f'  {name}\n' for name in packages))
replace(manifest, '  src/ProGPU.WinUI.Designer/ProGPU.WinUI.Designer.csproj\n', '  src/ProGPU.WinUI.Designer/ProGPU.WinUI.Designer.csproj\n' + ''.join(f'  src/{name}/{name}.csproj\n' for name in packages))
replace(manifest, '  "Designer/editor controls and diagnostics for ProGPU WinUI surfaces."\n', '  "Designer/editor controls and diagnostics for ProGPU WinUI surfaces."\n' + ''.join(f'  "{description}"\n' for description in ["Typed HMI documents, equipment templates, alarms and guarded acquisition contracts.", "Reusable retained-vector HMI equipment controls and runtime screen views.", "Shared-canvas HMI authoring, state rules, equipment templates and commissioning UI.", "Strict Modbus TCP acquisition and single-attempt absolute commands.", "MQTT 5 typed telemetry and non-retained absolute commands with strict TLS."]))
readme = "README.md"
replace(readme, '## NuGet Packages', '''## HMI designer and control-system integrations

The [HMI workbench](docs/hmi-designer.md) reuses the existing designer canvas and ships
standalone controls, typed tags, alarms, recipes, linked equipment faceplates and
priority-based equipment states. Run `dotnet run --project samples/HmiDesigner -c Release`
or open **Visual Designer → HMI** in the sample gallery.

The [control integration guide](docs/hmi-control-integrations.md) covers real Modbus TCP
and MQTT 5/TLS adapters, editable connection/mapping profiles, explicit read-only
commissioning and single-use write confirmations. Opening a project never connects to
an endpoint. External writes additionally require a host-provided authorization policy;
broker/controller acknowledgements are distinguished from actual process feedback.

## NuGet Packages''')
print("HMI v2 modules integrated with existing models, views, designer, samples and release manifest.")
