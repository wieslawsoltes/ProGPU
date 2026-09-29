"""Guarded HMI integration corrections. Temporary development helper; not a build-time dependency."""
from pathlib import Path


def replace(path, old, new, count=1):
    file = Path(path)
    source = file.read_text(encoding="utf-8")
    if source.count(old) != count:
        raise RuntimeError(f"{path}: expected {count} matches for {old[:120]!r}, found {source.count(old)}")
    file.write_text(source.replace(old, new), encoding="utf-8", newline="\n")


replace("src/ProGPU.Hmi.Mqtt/HmiMqttConnection.cs", '.WithAtLeastOnceQoS().WithRetainFlag(false)', '.WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(false)')
replace("tests/ProGPU.Hmi.Tests/HmiMqttTests.cs", '.WithAtLeastOnceQoS()', '.WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)', 2)
replace("src/ProGPU.Hmi/Connectivity/HmiAcquisitionSession.cs", 'error is IOException or System.Net.Sockets.SocketException', 'error is IOException or InvalidDataException or System.Net.Sockets.SocketException')
replace("src/ProGPU.Hmi.Modbus/HmiModbusConnection.cs", 'error is IOException or SocketException or OperationCanceledException or ObjectDisposedException', 'error is IOException or InvalidDataException or SocketException or OperationCanceledException or ObjectDisposedException')
replace("src/ProGPU.Hmi/Connectivity/HmiWriteCoordinator.cs", 'await _authorize(request, token).ConfigureAwait(false)', 'await _authorize(request, token).AsTask().WaitAsync(token).ConfigureAwait(false)')

model = 'src/ProGPU.Hmi/HmiProject.cs'
replace(model, '    public string FaceplatePrefix { get; set; } = "";', '    public string FaceplatePrefix { get; set; } = "";\n    public float FaceplateSourceX { get; set; }\n    public float FaceplateSourceY { get; set; }')
replace(model, '        FaceplatePrefix = newIdentity ? "" : FaceplatePrefix', '        FaceplatePrefix = newIdentity ? "" : FaceplatePrefix,\n        FaceplateSourceX = newIdentity ? 0 : FaceplateSourceX,\n        FaceplateSourceY = newIdentity ? 0 : FaceplateSourceY')
faceplates = 'src/ProGPU.Hmi/HmiFaceplates.cs'
replace(faceplates, '''                var source = template.Elements.SingleOrDefault(e => e.Id == first.FaceplateSourceId);
                float x = first.X - (source?.X ?? 0), y = first.Y - (source?.Y ?? 0);''', '''                // Original master-local coordinates retain the instance origin across master geometry edits.
                float x = first.X - first.FaceplateSourceX, y = first.Y - first.FaceplateSourceY;''')
replace(faceplates, '        element.FaceplateSourceId = ""; element.FaceplatePrefix = "";', '        element.FaceplateSourceId = ""; element.FaceplatePrefix = "";\n        element.FaceplateSourceX = 0; element.FaceplateSourceY = 0;')
replace(faceplates, '            element.FaceplateSourceId = source.Id; element.FaceplatePrefix = prefix;', '            element.FaceplateSourceId = source.Id; element.FaceplatePrefix = prefix;\n            element.FaceplateSourceX = source.X; element.FaceplateSourceY = source.Y;')
replace(faceplates, '            // Reuse normal graph validation, replacing slot tokens with a concrete isolated tag namespace.', '''            if (template.Slots.Any(t => t == null) || template.Elements.Any(e => e == null || e.Action == null || e.Action.Target == null || e.States == null ||
                e.Tag == null || e.VisibilityTag == null || e.EnabledTag == null || e.FaceplateTemplateId == null || e.FaceplateInstanceId == null ||
                e.States.Any(s => s == null || s.Tag == null)))
                throw new InvalidDataException("Faceplate contains null elements, slots, state rules, actions or bindings.");
            // Reuse normal graph validation, replacing slot tokens with a concrete isolated tag namespace.''')
replace(faceplates, '            validation.Screens.AddRange(project.Screens.Where(s => s.Id != "template").Select(s => new HmiScreen { Id = s.Id, Name = s.Name }));', '''            // External navigation is checked against the real project below; do not inflate the synthetic screen budget.''')
replace(faceplates, '                ReplaceBindings(element, token => token.StartsWith', '''                if (element.Action.Kind == HmiActionKind.Navigate)
                {
                    if (!project.Screens.Any(s => s.Id == element.Action.Target)) throw new InvalidDataException("Faceplate navigation target does not exist.");
                    element.Action = new HmiAction(); // Validation-only clone; the real template keeps its action.
                }
                ReplaceBindings(element, token => token.StartsWith''')
replace('src/ProGPU.Hmi/HmiProjectExtensions.cs', '            if (element.States is not', '''            if (!float.IsFinite(element.FaceplateSourceX) || !float.IsFinite(element.FaceplateSourceY) ||
                Math.Abs(element.FaceplateSourceX) > 32768 || Math.Abs(element.FaceplateSourceY) > 32768)
                throw new InvalidDataException("Invalid faceplate master-local coordinates.");
            if (element.States is not''')
replace('src/ProGPU.WinUI.Hmi/HmiEquipmentDrawing.cs', '        float left = width * 0.15f, right = width * 0.85f;', '        if (width < 48 || bottom - top < 32) return;\n        float left = width * 0.15f, right = width * 0.85f;')
replace('src/ProGPU.WinUI.Hmi/HmiEquipmentDrawing.cs', 'params Vector2[] points', 'params ReadOnlySpan<Vector2> points')

# Keep existing overview documentation consistent with the implemented optional transport packages.
doc = Path('docs/hmi-designer.md')
text = doc.read_text(encoding='utf-8').replace('20 symbol types', '28 symbol types').replace('all 20 insertable symbols', 'all 28 insertable symbols')
text = text.replace('`IHmiTagSource` is a read-adapter seam, not a built-in industrial protocol driver.', '`IHmiTagSource` is the read-adapter seam. Optional `ProGPU.Hmi.Modbus` and `ProGPU.Hmi.Mqtt` packages provide real transports; see [control integrations](hmi-control-integrations.md).')
text = text.replace('Groups are flat authoring groups, not nested reusable symbol definitions.', 'Groups are flat authoring groups. The Faceplates tab provides typed reusable equipment compositions with explicit master synchronization; recursive nested templates remain outside this implementation.')
text = text.replace('No write is forwarded to a transport.', 'Local simulation writes are never forwarded to a transport. Live acquisition uses a separate, explicitly authorized and reviewed external-command coordinator.')
text = text.replace('It does not include built-in OPC UA/DA, Modbus or MQTT drivers, PLC downloads,', 'Optional adapters support Modbus TCP and MQTT 5/TLS with the boundaries in [control integrations](hmi-control-integrations.md). It does not include OPC UA/DA, PLC downloads,')
text = text.replace('nested symbol templates, advanced industrial connector routing,', 'recursive nested symbol templates, advanced industrial connector routing,')
text = text.replace('The three new libraries are packable', 'The core, controls, designer and two optional transport libraries are packable')
text += '\n\n## Equipment and commissioning extension\n\nConnections edits endpoint profiles and typed I/O mappings, starts explicit read-only acquisition, and reviews single-use external write requests. Faceplates captures and instantiates equipment masters with typed slots. States edits priority-based equipment conditions. New symbols include heat exchangers, filters, compressors, fans, heaters, thermometers, boilers and cooling towers. See [the integration guide](hmi-control-integrations.md) for protocol details, security boundaries and tests.\n'
doc.write_text(text, encoding='utf-8', newline='\n')
print('Applied MQTT API correction, command cancellation, framing and equipment-template hardening.')
