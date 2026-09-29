# HMI equipment and control-system integrations

This extension adds real Modbus TCP and MQTT 5 adapters, explicit commissioning controls, typed reusable equipment faceplates, state rules and eight additional GPU-rendered equipment components. It is not a PLC runtime, safety controller or certification claim.

## Libraries and dependency direction

`ProGPU.Hmi` owns inert connection profiles, tag mappings, the acquisition-session contract, write review/authorization, faceplates and state rules. It has no UI or protocol-package dependency. `ProGPU.Hmi.Modbus` implements its TCP transport with .NET networking and binary primitives. `ProGPU.Hmi.Mqtt` uses MIT-licensed MQTTnet 5.2.0.1603. Neither adapter depends on the designer.

`ProGPU.WinUI.Hmi.Designer` exposes `ConnectionFactory` and `WriteAuthorizer` extension points. The standalone HMI app and shared gallery register both concrete adapters; the reusable designer does not force transport dependencies on runtime-only component consumers. All six HMI packages are registered in the normal portable release manifest, but a source change does not itself publish a NuGet release.

## Commissioning in the designer

Open **Connections**, add a Modbus TCP or MQTT profile, and select its ID. Edit the exact endpoint, polling interval and timeout. Add mappings to existing project tags. A Modbus address is the zero-based PDU address, not a 40001-style documentation number. Choose bit/register area, encoding, word/byte ordering, engineering scale and offset. MQTT mappings have exact telemetry topics and separate command topics; wildcard mappings are intentionally rejected.

Projects never connect automatically on load. **Connect read-only** explicitly constructs a transport and starts acquisition. All tags start with uncertain quality, not good-quality training defaults. Connection state, failure count, poll count and elapsed polling time appear through **Refresh** and runtime diagnostics. Retry delays are bounded, reads are serialized, and invalid or disconnected telemetry turns bad instead of being replaced by simulated data. Simulation stepping is rejected during live acquisition.

MQTT defaults to TLS. Native certificate and hostname verification remain enabled; there is no accept-all certificate callback. A hosting application can supply a credential callback to `HmiMqttConnection`; credentials are not part of JSON and cannot be sent by this adapter without TLS. The Modbus adapter is plain Modbus TCP, not Modbus Security/TLS. Unencrypted transport requires the separate `AllowUnsecuredTransport` opt-in and an isolated, appropriately protected control network. Do not expose raw Modbus endpoints to the public Internet.

## External commands

The standalone sample deliberately has no permissive external-write authorizer. A writable tag or mapping is only configuration, not authorization. The embedding application must assign `WriteAuthorizer` and check its authenticated user/session, endpoint and tag ACLs. The operator name entered in the sample is audit context, not authentication.

Operator controls prepare an immutable command review rather than sending immediately. **Connections** shows the tag, observed value, absolute requested value, reason and a 20-second expiration. **Confirm pending write** consumes a single-use request. Authorization is awaited, then connection state, generation, expiry, current feedback, tag range and the optional Boolean permissive are checked again. Revocation invalidates an in-flight authorization. The runtime is never optimistically changed to the requested value.

The transport attempts an absolute write once. A lost or malformed write reply is **Indeterminate**, not success and not a reason for automatic retry. Modbus acknowledgement means the request was accepted at protocol level; MQTT PUBACK means the broker accepted the publish. Neither proves that machinery reached the requested state. Await independent readback and enforce safety/interlocks at the controller. Modbus does not provide a compare-and-swap operation for this workflow; concurrent process changes remain possible after the client's final check.

Recipe operations remain local simulation operations. Multi-tag hardware recipes are not sent implicitly or advertised as atomic transactions. Commands are not retained, queued offline, automatically replayed after reconnect, or derived from arbitrary scripts. Local alarm acknowledgement is distinct from PLC alarm acknowledgement.

```csharp
using ProGPU.Hmi;
using ProGPU.Hmi.Modbus;
using ProGPU.Hmi.Mqtt;
using ProGPU.WinUI.Hmi.Designer;

var designer = new HmiDesignerHost
{
    ConnectionFactory = profile => profile.Protocol switch
    {
        HmiConnectionProtocol.ModbusTcp => new HmiModbusConnection(profile),
        HmiConnectionProtocol.Mqtt => new HmiMqttConnection(profile),
        _ => throw new NotSupportedException()
    }
};
// Set designer.WriteAuthorizer to your authenticated authorization service.
// No default "return true" policy is installed.
window.Content = designer;
```

## Modbus TCP support

Implemented reads: coils (FC01), discrete inputs (FC02), holding registers (FC03) and input registers (FC04). Implemented absolute writes: one coil (FC05), one register (FC06), and one mapped multi-register numeric value using FC16 (0x10). This is not an implementation of every Modbus function, serial RTU/ASCII, discovery or device programming.

The client validates transaction ID, protocol ID, unit ID, bounded MBAP length, response function, byte count and write echo. It accepts fragmented TCP delivery with exact-length reads. Cancellation or malformed framing closes the stream, so a late response cannot be mistaken for another transaction. One request is in flight per connection. Contiguous configured reads are combined within protocol limits; unconfigured address gaps are not read. Transport replies are parsed before publication of an acquisition batch.

Numeric encodings are UInt16, Int16, UInt32, Int32, Float32 and Float64. Byte swap and word swap are explicit and independent of host byte order. Engineering conversion is `value = raw * scale + offset`; inverse writes reject overflow, nonfinite values and fractional integer encodings rather than silently rounding. Register reads containing nonfinite floating-point data receive bad quality. Unit-zero broadcast and overlapping writable register mappings are rejected.

## MQTT payload contract

The adapter uses MQTT 5, exact subscriptions, clean sessions and bounded packets. Scalar JSON is accepted for live non-retained values. The envelope form carries original source time and quality:

```json
{"value":4.25,"timestamp":"2026-09-29T16:30:00Z","quality":"Good"}
```

`value` must match the configured Boolean, Number or Text tag. Duplicate/unknown envelope members, nonfinite numbers, excessive payloads, invalid quality and future timestamps are rejected. Retained messages require source timestamps and are never made fresh merely by reconnecting. The payload budget is 16 KiB, string values are bounded, and older/duplicate source samples cannot overwrite newer accepted values.

The adapter stores bounded latest-value telemetry per mapped tag. When intermediate changed values are coalesced before owner-thread publication, it increments `CoalescedSamples` and marks the delivered sample uncertain. It is **not an event-complete alarm feed or historian**. Applications needing every transition must use a durable, ordered event integration rather than infer transitions from latest-value telemetry. Bad or missing telemetry is visible; it is not converted into normal equipment state.

Commands are non-retained QoS1 JSON envelopes with a generated `id`, creation `timestamp` and typed absolute `value`. A controller-side consumer must implement this contract, authenticate/authorize commands, enforce expiry/deduplication and publish separate process feedback. MQTT QoS1 permits duplicate delivery; do not attach non-idempotent process actions to an absolute-value command topic.

## Equipment components, state rules and faceplates

The catalog now contains **28** components. Heat exchanger, filter, compressor, fan, heater, thermometer, boiler and cooling tower have distinct retained-vector drawings and standalone typed controls. Common equipment visuals expose `VisualTone` and `StateText` dependency properties in addition to value, quality and activity.

The **States** table edits component rules: typed tag, condition, threshold, tone, text and priority. The highest matching priority wins; missing/bad/stale state telemetry overrides ordinary status with UNKNOWN QUALITY. Rule evaluation is declarative and does not execute scripts. The runtime's tag index includes all state dependencies.

The **Faceplates** panel captures a selection into a reusable master with typed `$slot` bindings, inserts prefix-qualified grouped instances, updates instances explicitly, or detaches an instance. The built-in variable-speed pump station combines status, trip/permissive states, current, speed input and absolute start/stop controls. Its Boolean permissive is a UI condition, not a substitute for controller safety logic.

Templates persist in the project and can be edited in Project JSON. Explicit synchronization retains matching child IDs and each instance's current origin, while replacing local overrides from the master. New slots create or reuse type-compatible tags. Copy/paste detaches master links and retains concrete tag references. This release supports flat equipment compositions, not recursive/nested templates or vendor faceplate interchange.

## Validation and deployment boundaries

The HMI test project exercises binary codecs, real loopback Modbus TCP exchanges (fragmented replies, malformed MBAP, cancellation, lost write acknowledgements), a real in-process MQTT broker, payload quarantine, command-review/authorization behavior, faceplate identity and state priority. The normal Windows/Linux matrix builds the sample/gallery and packs the HMI libraries. Inspect actual CI results; protocol tests do not qualify a particular PLC, TLS installation or physical GPU.

The optional OPC UA adapter adds certificate-validated scalar reads/writes and node browsing; see [OPC UA commissioning](hmi-opcua.md). No Siemens S7, EtherNet/IP/CIP, IEC 61850/104, DNP3, industrial historian, redundancy, or PLC-program download is claimed here. `IHmiConnection` is the extension seam for additional reviewed adapters. Browser TCP/TLS access is not supplied by these native .NET adapters; use an authenticated gateway for browser deployments. Full production authorization, secure credential provisioning, network design, commissioning and independent safety are application responsibilities.

## Primary protocol references

- Modbus Organization, [Application Protocol V1.1b3](https://www.modbus.org/file/secure/modbusprotocolspecification.pdf).
- Modbus Organization, [Messaging on TCP/IP Implementation Guide V1.0b](https://www.modbus.org/file/secure/messagingimplementationguide.pdf).
- OASIS, [MQTT 5.0 standard](https://docs.oasis-open.org/mqtt/mqtt/v5.0/os/mqtt-v5.0-os.html).
- .NET Foundation, [MQTTnet source and MIT license](https://github.com/dotnet/MQTTnet).


## OPC UA and command-session extension

The Connections pane now supports OPC UA profiles and a bounded namespace-URI-based node browser. All three adapters check reviewed connection generations under their transport gates. See [OPC UA commissioning and session-bound commands](hmi-opcua.md) for security, scalar formats, test commands and deployment boundaries.
