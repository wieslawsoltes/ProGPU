using System.Diagnostics.CodeAnalysis;

namespace ProGPU.Hmi;

public enum HmiConnectionProtocol { ModbusTcp, Mqtt }
public enum HmiModbusArea { Coil, DiscreteInput, HoldingRegister, InputRegister }
public enum HmiRegisterEncoding { Boolean, UInt16, Int16, UInt32, Int32, Float32, Float64 }
public enum HmiRegisterOrder { BigEndian, SwapBytes, SwapWords, SwapBytesAndWords }

/// <summary>Serializable, inert endpoint configuration. Credentials and write authorization are never persisted here.</summary>
public sealed class HmiConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Controller";
    public HmiConnectionProtocol Protocol { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 502;
    public bool UseTls { get; set; }
    public bool AllowUnsecuredTransport { get; set; }
    public int TimeoutMilliseconds { get; set; } = 3000;
    public int PollMilliseconds { get; set; } = 500;
    public byte UnitId { get; set; } = 1;
    public List<HmiIoMapping> Mappings { get; set; } = [];

    public HmiConnectionProfile Copy() => new()
    {
        Id = Id, Name = Name, Protocol = Protocol, Host = Host, Port = Port, UseTls = UseTls,
        AllowUnsecuredTransport = AllowUnsecuredTransport, TimeoutMilliseconds = TimeoutMilliseconds,
        PollMilliseconds = PollMilliseconds, UnitId = UnitId, Mappings = Mappings.Select(m => m.Copy()).ToList()
    };

    public void Validate(IReadOnlyList<HmiTagDefinition>? tags = null)
    {
        Require(!string.IsNullOrWhiteSpace(Id) && Id.Length <= 128 && !string.IsNullOrWhiteSpace(Name) && Name.Length <= 256, "Invalid connection identity.");
        Require(Enum.IsDefined(Protocol), "Unknown connection protocol.");
        Require(!string.IsNullOrWhiteSpace(Host) && Host.Length <= 253 && Uri.CheckHostName(Host) != UriHostNameType.Unknown, "Supply a DNS name or IP literal, not a URL.");
        Require(Port is >= 1 and <= 65535, "Invalid TCP port.");
        Require(TimeoutMilliseconds is >= 100 and <= 60000 && PollMilliseconds is >= 50 and <= 60000, "Invalid timeout or polling interval.");
        Require(UnitId != 0, "Broadcast unit zero is not admitted by this client.");
        Require(Mappings is { Count: <= 4096 }, "A connection supports at most 4096 mappings.");
        Require(Protocol != HmiConnectionProtocol.ModbusTcp || !UseTls, "This Modbus adapter is TCP, not Modbus Security/TLS.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in Mappings)
        {
            Require(mapping != null && !string.IsNullOrWhiteSpace(mapping.Tag) && mapping.Tag.Length <= 4096 && names.Add(mapping.Tag), "Null, duplicate or invalid tag mapping.");
            Require(Enum.IsDefined(mapping.Type) && Enum.IsDefined(mapping.Area) && Enum.IsDefined(mapping.Encoding) && Enum.IsDefined(mapping.Order), "Unknown mapping type.");
            Require(double.IsFinite(mapping.Scale) && mapping.Scale != 0 && double.IsFinite(mapping.Offset), "Mapping scale must be finite and nonzero; offset must be finite.");
            Require(mapping.InterlockTag != null && mapping.InterlockTag.Length <= 4096, "Invalid write interlock tag.");
            if (tags != null)
            {
                var tag = tags.SingleOrDefault(t => t.Name == mapping.Tag);
                Require(tag != null && tag.Type == mapping.Type, $"Mapping {mapping.Tag} must match an existing tag type.");
                Require(!mapping.Writable || tag.Writable, $"Mapping {mapping.Tag} cannot write a read-only tag.");
                if (mapping.InterlockTag.Length > 0)
                    Require(tags.Any(t => t.Name == mapping.InterlockTag && t.Type == HmiTagType.Boolean), "Write interlocks require existing Boolean tags.");
            }
            if (Protocol == HmiConnectionProtocol.ModbusTcp)
            {
                bool bits = mapping.Area is HmiModbusArea.Coil or HmiModbusArea.DiscreteInput;
                Require(bits ? mapping.Type == HmiTagType.Boolean && mapping.Encoding == HmiRegisterEncoding.Boolean : mapping.Type == HmiTagType.Number && mapping.Encoding != HmiRegisterEncoding.Boolean, "Modbus bit areas require Boolean mappings; register areas require numeric mappings.");
                Require(mapping.Address is >= 0 and <= 65535 && mapping.Address + mapping.RegisterCount <= 65536, "Modbus addresses are zero-based 16-bit PDU addresses.");
                Require(!mapping.Writable || mapping.Area is HmiModbusArea.Coil or HmiModbusArea.HoldingRegister, "Input areas are read-only.");
            }
            else
            {
                Require(IsExactTopic(mapping.Topic), "MQTT telemetry needs an exact, non-wildcard topic.");
                Require(!mapping.Writable || IsExactTopic(mapping.CommandTopic), "Writable MQTT mappings need a separate command topic.");
                Require(!mapping.Writable || mapping.Topic != mapping.CommandTopic, "Command and telemetry topics must differ.");
            }
        }
        // Reject overlapping write addresses: one physical destination must have one command owner.
        if (Protocol == HmiConnectionProtocol.ModbusTcp)
        {
            var writes = Mappings.Where(m => m.Writable).OrderBy(m => m.Area).ThenBy(m => m.Address).ToArray();
            for (int i = 1; i < writes.Length; i++)
                Require(writes[i].Area != writes[i - 1].Area || writes[i].Address >= writes[i - 1].Address + writes[i - 1].RegisterCount, "Writable Modbus mappings overlap.");
        }
    }
    public void RequireTransportPermission()
    {
        Validate();
        if (!UseTls && !AllowUnsecuredTransport)
            throw new InvalidOperationException("Unencrypted transport is disabled. Explicitly permit it only for a trusted, isolated control network.");
    }
    private static bool IsExactTopic(string topic) => !string.IsNullOrWhiteSpace(topic) && topic.Length <= 1024 && topic.IndexOfAny(['#', '+', '\0']) < 0;
    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

public sealed class HmiIoMapping
{
    public string Tag { get; set; } = "";
    public HmiTagType Type { get; set; }
    public bool Writable { get; set; }
    public string InterlockTag { get; set; } = "";
    public HmiModbusArea Area { get; set; } = HmiModbusArea.HoldingRegister;
    public int Address { get; set; }
    public HmiRegisterEncoding Encoding { get; set; } = HmiRegisterEncoding.UInt16;
    public HmiRegisterOrder Order { get; set; }
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }
    public string Topic { get; set; } = "";
    public string CommandTopic { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public int RegisterCount => Encoding switch { HmiRegisterEncoding.Float64 => 4, HmiRegisterEncoding.UInt32 or HmiRegisterEncoding.Int32 or HmiRegisterEncoding.Float32 => 2, _ => 1 };
    public HmiIoMapping Copy() => (HmiIoMapping)MemberwiseClone();
}
