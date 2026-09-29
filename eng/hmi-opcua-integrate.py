"""Guarded integration corrections; this temporary helper is removed after validation."""
from pathlib import Path


def replace(path, old, new, count=1):
    file = Path(path)
    text = file.read_text(encoding='utf-8')
    if text.count(old) != count:
        raise RuntimeError(f'{path}: expected {count} matches for {old[:100]!r}, found {text.count(old)}')
    file.write_text(text.replace(old, new), encoding='utf-8', newline='\n')


replace('src/ProGPU.Hmi.OpcUa/HmiOpcUaConnection.cs', 'new NodeId(local.Identifier).ToString()', 'new NodeId(local.Identifier, 0).ToString()')
replace('tests/ProGPU.Hmi.Tests/HmiMqttTests.cs', '''        await using var connection = new HmiMqttConnection(Profile(1), _ => ValueTask.FromResult<HmiMqttCredentials?>(new("user", "secret")));
        var error = await Assert.ThrowsAsync<IOException>(async () => await connection.ConnectAsync(default));
        Assert.DoesNotContain("secret", error.Message);''', '''        const string user = "credential-canary-user-731db5";
        const string password = "credential-canary-password-64a9fc";
        await using var connection = new HmiMqttConnection(Profile(1), _ => ValueTask.FromResult<HmiMqttCredentials?>(new(user, password)));
        var error = await Assert.ThrowsAsync<IOException>(async () => await connection.ConnectAsync(default));
        // Check actual credential canaries, including inner exceptions, not a common English word.
        Assert.DoesNotContain(user, error.ToString());
        Assert.DoesNotContain(password, error.ToString());''')
replace('src/ProGPU.Hmi/Connectivity/HmiAcquisitionSession.cs',
    '        catch (Exception error) { SetStatus(HmiConnectionState.Faulted, error.Message); }',
    '''        catch (Exception error)
        {
            Interlocked.Increment(ref _failures);
            try { await PublishQualityAsync(HmiQuality.Bad, generation, token).ConfigureAwait(false); }
            catch (Exception qualityError) { SetStatus(HmiConnectionState.Faulted, error.Message + " Quality publication failed: " + qualityError.Message); }
            if (Diagnostics.State != HmiConnectionState.Faulted) SetStatus(HmiConnectionState.Faulted, error.Message);
        }''')
replace('src/ProGPU.Hmi.OpcUa/HmiOpcUaConnection.cs',
    '        catch (OperationCanceledException) { DropSession(); throw; }',
    '        catch (OperationCanceledException) { DropSession(); throw; }\n        catch (InvalidDataException) { DropSession(); throw; }')
replace('src/ProGPU.Hmi.OpcUa/HmiOpcUaCodec.cs',
    '        if (value == null || value.GetType() != ClrType(mapping.OpcUa.DataType))',
    '''        if (!double.IsFinite(mapping.Scale) || mapping.Scale == 0 || !double.IsFinite(mapping.Offset))
            throw new InvalidDataException("Invalid OPC UA engineering scale or offset.");
        if (value == null || value.GetType() != ClrType(mapping.OpcUa.DataType))''')
replace('eng/progpu-package-list.sh', '  ProGPU.Hmi.Mqtt\n', '  ProGPU.Hmi.Mqtt\n  ProGPU.Hmi.OpcUa\n')
replace('eng/progpu-package-list.sh', '  src/ProGPU.Hmi.Mqtt/ProGPU.Hmi.Mqtt.csproj\n', '  src/ProGPU.Hmi.Mqtt/ProGPU.Hmi.Mqtt.csproj\n  src/ProGPU.Hmi.OpcUa/ProGPU.Hmi.OpcUa.csproj\n')
replace('eng/progpu-package-list.sh', '  "MQTT 5 typed telemetry and non-retained absolute commands with strict TLS."', '  "MQTT 5 typed telemetry and non-retained absolute commands with strict TLS."\n  "Certificate-validated OPC UA scalar acquisition, bounded node browsing and session-bound absolute writes."')
replace('docs/hmi-control-integrations.md', 'No OPC UA, Siemens S7,', 'The optional OPC UA adapter adds certificate-validated scalar reads/writes and node browsing; see [OPC UA commissioning](hmi-opcua.md). No Siemens S7,')
replace('docs/hmi-control-integrations.md', 'All five HMI packages', 'All six HMI packages')
replace('docs/hmi-designer.md', 'It does not include OPC UA/DA, PLC downloads,', 'The optional OPC UA package supplies typed scalar acquisition, bounded browsing and reviewed writes; see [OPC UA commissioning](hmi-opcua.md). It does not include OPC DA, PLC downloads,')
replace('docs/hmi-designer.md', 'two optional transport libraries', 'three optional transport libraries')
file = Path('docs/hmi-control-integrations.md')
text = file.read_text(encoding='utf-8')
text += '\n\n## OPC UA and command-session extension\n\nThe Connections pane now supports OPC UA profiles and a bounded namespace-URI-based node browser. All three adapters check reviewed connection generations under their transport gates. See [OPC UA commissioning and session-bound commands](hmi-opcua.md) for security, scalar formats, test commands and deployment boundaries.\n'
file.write_text(text, encoding='utf-8', newline='\n')
file = Path('README.md')
text = file.read_text(encoding='utf-8')
text += '\n\n### OPC UA HMI commissioning\n\nThe optional `ProGPU.Hmi.OpcUa` package adds signed/encrypted OPC UA sessions, typed scalar acquisition, bounded node browsing and session-bound reviewed writes. The shared HMI designer exposes endpoint and mapping editors plus the node browser; opening a project never connects automatically. See [OPC UA commissioning](docs/hmi-opcua.md) and [HMI control integrations](docs/hmi-control-integrations.md).\n'
file.write_text(text, encoding='utf-8', newline='\n')
print('Corrected OPC UA node formatting, credential-canary test, failure quality, release manifest and integration docs.')
