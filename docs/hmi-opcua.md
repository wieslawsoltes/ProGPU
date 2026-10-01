# OPC UA commissioning and session-bound commands

`ProGPU.Hmi.OpcUa` is an optional native .NET OPC UA client built on the OPC Foundation .NET Standard stack. It implements typed scalar acquisition, bounded hierarchical node browsing and reviewed absolute-value writes. It does not require the ProGPU UI or designer. The default configuration uses certificate-validated, signed and encrypted OPC UA SecureChannels; opening a project never connects to a server.

## Package and host structure

The dependency direction is `ProGPU.Hmi.OpcUa → ProGPU.Hmi` plus the pinned `OPCFoundation.NetStandard.Opc.Ua.Client` package. No WinUI, rendering or designer assembly is referenced by the adapter. The separate OPC UA test project hosts the official stack's real `StandardServer`; the server package is not an application dependency.

The standalone `samples/HmiDesigner` application and shared sample gallery register OPC UA alongside Modbus TCP and MQTT. Embedders may register only the protocols they need:

```csharp
using ProGPU.Hmi;
using ProGPU.Hmi.OpcUa;
using ProGPU.WinUI.Hmi.Designer;

var designer = new HmiDesignerHost
{
    ConnectionFactory = profile => profile.Protocol switch
    {
        HmiConnectionProtocol.OpcUa => new HmiOpcUaConnection(profile),
        _ => throw new NotSupportedException("This host enables OPC UA only.")
    }
};
window.Content = designer;
// External writes remain denied until the host installs an authenticated WriteAuthorizer.
```

## Commissioning workflow

In **Connections**, choose **+ OPC UA**. Configure the host, port (default 4840), endpoint path, security mode and security policy. OPC UA SecureChannel security is separate from the MQTT TLS checkbox. The adapter requires an exact advertised endpoint URL and exact configured security mode/policy. It does not follow a different host, silently rewrite a path, choose a weaker endpoint, or accept an untrusted server certificate to make a connection succeed.

Select **Connect read-only** explicitly. An OPC UA profile can connect with zero mappings solely for browsing. Real acquisition starts with uncertain samples rather than training values. A successful connection does not manufacture process measurements.

The **OPC UA Node Browser** is in the Connections pane. Its default root `i=85` is the standard Objects folder in namespace zero. Enter a namespace URI and a namespace-local node identifier, browse children, and use a result row as the next root. Each operation is bounded to 256 visible results in the designer; the adapter admits at most 1024 results and 32 server pages. Truncated results are labelled, and unused server continuation points are released. References to other servers are not followed.

To map a result, disconnect first, select the result row and enter an existing project tag in the mapping toolbar, then choose **Map variable**. The recorded mapping is read-only. Set the exact server scalar type in **UA scalar**, and reconnect to validate actual telemetry. Numeric mappings initially select Double; the adapter rejects a different server type instead of coercing it. Browsing itself does not change the document, grant writes or execute node methods.

## Stable node identities

Persist the namespace URI and namespace-local identifier separately:

```json
{
  "tag": "Pump.Speed",
  "type": "Number",
  "writable": false,
  "opcUa": {
    "namespaceUri": "urn:plant:controller",
    "identifier": "s=Pump.Speed",
    "dataType": "Float"
  }
}
```

Do not store `ns=2;s=Pump.Speed` as the identifier. Namespace indexes can differ between sessions and servers. The adapter resolves each URI against the current session's NamespaceArray. Missing namespaces fail connection setup rather than reading a node with the same numeric index from another namespace. Numeric, string, GUID and opaque identifiers are supported. Canonically equivalent writable node destinations are rejected after SDK resolution.

## Certificates, credentials and endpoint security

The default client identity is stored under the current user's local application-data directory, in `ProGPU/Hmi/OpcUa/pki`. Subdirectories separate the application's own certificate/key, trusted peers, trusted issuers and rejected certificates. A local application certificate may be created when connecting. Remote certificates are never automatically trusted. Review fingerprints and establish both client and server trust through your approved PKI/commissioning process; do not copy rejected certificates into trusted storage without verification.

Default security is **SignAndEncrypt / Basic256Sha256**. Explicit policies also include Aes128Sha256RsaOaep and Aes256Sha256RsaPss. Application certificate, trust-list and hostname checks remain active. SHA-1-signed certificates and undersized keys are rejected by the default configuration.

Hosts can supply an asynchronous application-configuration factory to use centrally provisioned certificates and an asynchronous `IUserIdentity` factory for authenticated identities. Neither factory nor any credential is serialized into the HMI project. A custom configuration is trusted host code; its certificate validators and store permissions remain the host's responsibility. The adapter rejects configurations that advertise automatic untrusted-certificate acceptance.

Security mode None is only admitted with a separate `AllowUnsecuredTransport` opt-in and anonymous identity. It is used by the isolated loopback regression server, not by default production configuration. Credentials are not sent over an unencrypted channel. These native .NET TCP adapters are not browser transports; a browser deployment requires an appropriately authenticated gateway.

## Acquisition semantics

Read batches default to 128 nodes, are configurable from 1 to 512, and are reduced to the server's advertised read-operation limit. There is one serialized service operation per adapter. Each service response must contain the expected number of values before a batch can be published. SDK/service, cancellation and framing failures retire the current session. The acquisition owner handles bounded read reconnects; writes never reconnect or retry implicitly.

Supported scalar types are Boolean, SByte, Byte, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double and String. Arrays, matrices, ExtensionObjects and coercive Boolean/numeric conversions are rejected. Strings are bounded to 4096 characters. Engineering scaling is `engineering = raw × scale + offset`; inverse integer writes must be integral and in range. Nonfinite values, overflows and 64-bit integers outside the HMI model's exact integer range (±(2^53−1), or the unsigned subset) are rejected instead of rounded.

OPC UA source timestamps and quality are preserved. A missing source timestamp cannot be made fresh by receipt time. Future timestamps, time reversal and conflicting equal-time values are quarantined. Stale feedback therefore cannot be rehabilitated by reconnecting or by a fast polling rate. Individual bad values remain visible as bad quality; they are not shown as stopped equipment or valid zero readings.

This is **polled latest-value telemetry**, not an event-complete historian. The read service can miss intermediate transitions between polls; no alarm-event completeness or hard-real-time timing is asserted. The HMI alarm engine evaluates the admitted sampled values and preserves unknown-quality semantics.

## Reviewed writes and transport generations

`IHmiConnectionGeneration` identifies a concrete transport-session generation. `IHmiConditionalWriteConnection` adds a write overload that checks the reviewed generation under the same serialization gate that admits the actual request. OPC UA, Modbus TCP and MQTT implement this contract. A review prepared before reconnect cannot be sent on a replacement connection, including a reconnect that occurs while a command waits behind a read operation.

The coordinator consumes a single-use, expiring review, awaits authenticated host authorization, and then rechecks range, current feedback, quality, permission tag and session identity. The server/controller must still enforce authoritative permissions and process interlocks: a client-side check is not a compare-and-swap or a safety function.

A successful OPC UA Write result is a **server acknowledgement**, not evidence that machinery moved. Bad per-node results are rejected; uncertain results or lost/malformed replies after a request attempt are indeterminate. The coordinator never optimistically writes the requested value into telemetry and never automatically retries an indeterminate operation. Await independent process readback. A shutdown-time audit publication failure is reported without erasing the already observed transport outcome.

The built-in sample supplies no permissive external-write authorizer. Typing an operator name supplies audit context only; the host must verify the authenticated session and endpoint/tag access policy. Multi-tag hardware recipes, remote method invocation, PLC programs and safety interlocks are not executed by this adapter.

## Tests and boundaries

```sh
dotnet test tests/ProGPU.Hmi.OpcUa.Tests/ProGPU.Hmi.OpcUa.Tests.csproj -c Release
dotnet test tests/ProGPU.Hmi.Tests/ProGPU.Hmi.Tests.csproj -c Release
dotnet build samples/HmiDesigner/HmiDesigner.csproj -c Release
```

The OPC UA suite covers exact scalar types, namespace-index reordering, strict endpoint selection, invalid values/timestamps and a real local server read/browse/write/reconnect exchange. The shared suite covers reviewed-command expiry, authorization, generation replacement, cancellation and audit failures, plus the Modbus/MQTT and designer regressions. Inspect actual CI results separately from the presence of these tests.

Vendor PLC interoperability, deployed PKI/credential provisioning, physical equipment operation and native GPU/pointer fidelity require target-specific qualification. OPC UA subscriptions, historical reads, Alarms & Conditions, PubSub, arrays/custom structures, method calls, Siemens S7, EtherNet/IP, IEC protocols and redundant SCADA operation are not implemented here.

## Primary references

- [OPC Foundation .NET Standard stack, pinned 1.5.378.176](https://github.com/OPCFoundation/UA-.NETStandard/tree/1.5.378.176).
- [OPC UA Part 4: Read service](https://reference.opcfoundation.org/Core/Part4/v105/docs/5.11.2).
- [OPC UA Part 4: Write service](https://reference.opcfoundation.org/Core/Part4/v105/docs/5.11.4).
- [OPC UA Part 4: Browse service](https://reference.opcfoundation.org/Core/Part4/v105/docs/5.9.2).

The implementation and test results are not an OPC Foundation certification claim.
