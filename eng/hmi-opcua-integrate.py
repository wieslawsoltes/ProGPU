"""Guarded one-time edits connecting the OPC UA package to the existing HMI host. Not a build dependency."""
from pathlib import Path


def replace(path, old, new, count=1):
    file = Path(path)
    text = file.read_text(encoding='utf-8')
    if text.count(old) != count:
        raise RuntimeError(f'{path}: expected {count} matches for {old[:120]!r}, found {text.count(old)}')
    file.write_text(text.replace(old, new), encoding='utf-8', newline='\n')


replace('Directory.Packages.props', '    <PackageVersion Include="MQTTnet"', '''    <PackageVersion Include="OPCFoundation.NetStandard.Opc.Ua.Client" Version="1.5.378.176" />
    <PackageVersion Include="OPCFoundation.NetStandard.Opc.Ua.Server" Version="1.5.378.176" />
    <PackageVersion Include="MQTTnet"''')
replace('samples/HmiDesigner/HmiDesigner.csproj', '    <ProjectReference Include="../../src/ProGPU.Hmi.Mqtt/ProGPU.Hmi.Mqtt.csproj" />', '''    <ProjectReference Include="../../src/ProGPU.Hmi.Mqtt/ProGPU.Hmi.Mqtt.csproj" />
    <ProjectReference Include="../../src/ProGPU.Hmi.OpcUa/ProGPU.Hmi.OpcUa.csproj" />''')
# Insert beside the existing transport reference, retaining its mobile exclusion policy.
file = Path('src/ProGPU.Samples/ProGPU.Samples.csproj')
text = file.read_text(encoding='utf-8')
import re
pattern = r'<ProjectReference\s+Include="[^"\n]*ProGPU\.Hmi\.Mqtt[^"\n]*"[^>]*?/>'
matches = list(re.finditer(pattern, text))
if len(matches) != 1:
    raise RuntimeError('Expected exactly one gallery MQTT project reference')
reference = matches[0].group()
file.write_text(text.replace(reference, reference + '\n    ' + reference.replace('ProGPU.Hmi.Mqtt', 'ProGPU.Hmi.OpcUa')), encoding='utf-8', newline='\n')
for path in ['samples/HmiDesigner/Program.cs', 'src/ProGPU.Samples/Pages/VisualDesignerPage.cs']:
    replace(path, '                ProGPU.Hmi.HmiConnectionProtocol.Mqtt => new ProGPU.Hmi.Mqtt.HmiMqttConnection(profile),', '''                ProGPU.Hmi.HmiConnectionProtocol.Mqtt => new ProGPU.Hmi.Mqtt.HmiMqttConnection(profile),
                ProGPU.Hmi.HmiConnectionProtocol.OpcUa => new ProGPU.Hmi.OpcUa.HmiOpcUaConnection(profile),''')

connections = 'src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.Connections.cs'
replace(connections, '        tools.AddChild(_connectionId);', '''        tools.AddChild(Command("+ OPC UA", () => AddConnection(HmiConnectionProtocol.OpcUa)));
        tools.AddChild(_connectionId);''')
replace(connections, '("Timeout ms", "90", "Timeout"));', '("Timeout ms", "90", "Timeout"), ("UA path", "160", "UaPath"), ("UA security", "125", "UaSecurity"), ("UA policy", "180", "UaPolicy"));')
replace(connections, '("Command topic", "200", "Command"));', '("Command topic", "200", "Command"), ("UA namespace", "240", "UaNamespace"), ("UA identifier", "200", "UaIdentifier"), ("UA scalar", "95", "UaType"));')
replace(connections, '        panel.AddChild(commandTools); panel.AddChild(_pendingCommand);', '        panel.AddChild(commandTools); panel.AddChild(_pendingCommand);\n        panel.AddChild(BuildNodeBrowserPane());')
replace(connections, 'Name = protocol == HmiConnectionProtocol.Mqtt ? "MQTT broker" : "Modbus controller"', 'Name = protocol == HmiConnectionProtocol.OpcUa ? "OPC UA server" : protocol == HmiConnectionProtocol.Mqtt ? "MQTT broker" : "Modbus controller"')
replace(connections, 'Port = protocol == HmiConnectionProtocol.Mqtt ? 8883 : 502', 'Port = protocol == HmiConnectionProtocol.OpcUa ? 4840 : protocol == HmiConnectionProtocol.Mqtt ? 8883 : 502')
replace(connections, '            Topic = "plant/" + tag.Name, CommandTopic = "plant/" + tag.Name + "/set"', '''            Topic = "plant/" + tag.Name, CommandTopic = "plant/" + tag.Name + "/set",
            OpcUa = new HmiOpcUaAddress { Identifier = "s=" + tag.Name, DataType = tag.Type == HmiTagType.Boolean ? HmiOpcUaDataType.Boolean : tag.Type == HmiTagType.Text ? HmiOpcUaDataType.String : HmiOpcUaDataType.Double }''')
replace(connections, '["Timeout"] = profile.TimeoutMilliseconds.ToString(Invariant)', '["Timeout"] = profile.TimeoutMilliseconds.ToString(Invariant), ["UaPath"] = profile.OpcUa.EndpointPath, ["UaSecurity"] = profile.OpcUa.SecurityMode.ToString(), ["UaPolicy"] = profile.OpcUa.SecurityPolicy.ToString()')
replace(connections, '                    case "Timeout": target.TimeoutMilliseconds = Integer(value); break;', '''                    case "Timeout": target.TimeoutMilliseconds = Integer(value); break;
                    case "UaPath": target.OpcUa.EndpointPath = value; break;
                    case "UaSecurity": target.OpcUa.SecurityMode = Choice<HmiOpcUaSecurityMode>(value); break;
                    case "UaPolicy": target.OpcUa.SecurityPolicy = Choice<HmiOpcUaSecurityPolicy>(value); break;''')
replace(connections, '["Command"] = mapping.CommandTopic', '["Command"] = mapping.CommandTopic, ["UaNamespace"] = mapping.OpcUa.NamespaceUri, ["UaIdentifier"] = mapping.OpcUa.Identifier, ["UaType"] = mapping.OpcUa.DataType.ToString()')
replace(connections, '                    case "Command": target.CommandTopic = value; break;', '''                    case "Command": target.CommandTopic = value; break;
                    case "UaNamespace": target.OpcUa.NamespaceUri = value; break;
                    case "UaIdentifier": target.OpcUa.Identifier = value; break;
                    case "UaType": target.OpcUa.DataType = Choice<HmiOpcUaDataType>(value); break;''')

codec = 'src/ProGPU.Hmi.OpcUa/HmiOpcUaCodec.cs'
replace(codec, 'timestamp = DateTimeOffset.MinValue; quality = HmiQuality.Uncertain;', 'timestamp = DateTimeOffset.MinValue; if (quality == HmiQuality.Good) quality = HmiQuality.Uncertain;')
# Every concrete transport checks session identity while holding its existing serialization gate.
for name in ['OpcUa', 'Modbus', 'Mqtt']:
    path = f'src/ProGPU.Hmi.{name}/Hmi{name}Connection.cs'
    replace(path, f'public sealed class Hmi{name}Connection : IHmiConnection', f'public sealed class Hmi{name}Connection : IHmiConditionalWriteConnection')
    replace(path, '    public async ValueTask<HmiWriteResult> WriteAsync(string tag, HmiValue value, CancellationToken cancellationToken)', '''    public ValueTask<HmiWriteResult> WriteAsync(string tag, HmiValue value, CancellationToken cancellationToken)
        => WriteCoreAsync(tag, value, null, cancellationToken);
    public ValueTask<HmiWriteResult> WriteAsync(string tag, HmiValue value, long expectedConnectionGeneration, CancellationToken cancellationToken)
        => WriteCoreAsync(tag, value, expectedConnectionGeneration, cancellationToken);
    private async ValueTask<HmiWriteResult> WriteCoreAsync(string tag, HmiValue value, long? expectedConnectionGeneration, CancellationToken cancellationToken)''')
    if name == 'OpcUa':
        replace(path, '            var node = HmiOpcUaCodec.Resolve(mapping.OpcUa, session.NamespaceUris);', '''            if (expectedConnectionGeneration.HasValue && expectedConnectionGeneration.Value != ConnectionGeneration)
                return new(HmiWriteDisposition.NotSent, "OPC UA session changed after command review.");
            var node = HmiOpcUaCodec.Resolve(mapping.OpcUa, session.NamespaceUris);''')
    elif name == 'Modbus':
        replace(path, '    private ushort _transaction;', '    private ushort _transaction;\n    private long _generation;\n    public long ConnectionGeneration => Interlocked.Read(ref _generation);')
        replace(path, '                _client = client; _stream = client.GetStream();', '                _client = client; _stream = client.GetStream();\n                Interlocked.Increment(ref _generation);')
        replace(path, '            if (_disposed || _stream == null) return new(HmiWriteDisposition.NotSent, "Not connected.");', '''            if (_disposed || _stream == null) return new(HmiWriteDisposition.NotSent, "Not connected.");
            if (expectedConnectionGeneration.HasValue && expectedConnectionGeneration.Value != ConnectionGeneration)
                return new(HmiWriteDisposition.NotSent, "Modbus session changed after command review.");''')
        replace(path, 'private void CloseStream() { _stream?.Dispose();', 'private void CloseStream() { if (_stream != null) Interlocked.Increment(ref _generation); _stream?.Dispose();')
    else:
        replace(path, '    private long _rejected, _coalesced;', '    private long _rejected, _coalesced, _generation;\n    public long ConnectionGeneration => Interlocked.Read(ref _generation);')
        replace(path, '            _client = client;', '            _client = client;\n            Interlocked.Increment(ref _generation);')
        replace(path, '            if (!IsConnected) return new(HmiWriteDisposition.NotSent, "MQTT is disconnected; commands are not queued.");', '''            if (!IsConnected) return new(HmiWriteDisposition.NotSent, "MQTT is disconnected; commands are not queued.");
            if (expectedConnectionGeneration.HasValue && expectedConnectionGeneration.Value != ConnectionGeneration)
                return new(HmiWriteDisposition.NotSent, "MQTT session changed after command review.");''')
        replace(path, '        client?.Dispose();', '        if (client != null) Interlocked.Increment(ref _generation);\n        client?.Dispose();')
print('Wired OPC UA adapter, node browser and generation-checked commands into existing HMI components.')
