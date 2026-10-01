using System.Buffers;
using System.Text.Json;

namespace ProGPU.Hmi.Mqtt;

/// <summary>Typed JSON telemetry. Retained data requires an original source timestamp; receipt time is not freshness.</summary>
public static class HmiMqttPayloadCodec
{
    public const int MaximumPayloadBytes = 16384;
    public static HmiTagSample Decode(ReadOnlyMemory<byte> payload, HmiTagType type, bool retained, DateTimeOffset receivedAt)
    {
        if (payload.Length is 0 or > MaximumPayloadBytes) throw new InvalidDataException("MQTT payload is empty or exceeds 16 KiB.");
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        var valueElement = root;
        HmiQuality quality = HmiQuality.Good;
        DateTimeOffset timestamp = receivedAt;
        bool hasTimestamp = false;
        if (root.ValueKind == JsonValueKind.Object)
        {
            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in root.EnumerateObject())
            {
                if (!members.Add(member.Name)) throw new InvalidDataException("Duplicate telemetry member.");
                switch (member.Name)
                {
                    case "value": valueElement = member.Value; break;
                    case "timestamp":
                        if (member.Value.ValueKind != JsonValueKind.String || !member.Value.TryGetDateTimeOffset(out timestamp))
                            throw new InvalidDataException("Telemetry timestamp must be ISO-8601.");
                        hasTimestamp = true; break;
                    case "quality":
                        if (member.Value.ValueKind != JsonValueKind.String || !Enum.GetNames<HmiQuality>().Contains(member.Value.GetString()))
                            throw new InvalidDataException("Unknown telemetry quality.");
                        quality = Enum.Parse<HmiQuality>(member.Value.GetString()!); break;
                    default: throw new InvalidDataException("Unknown telemetry envelope member: " + member.Name);
                }
            }
            if (!members.Contains("value")) throw new InvalidDataException("Telemetry requires a value.");
        }
        if (retained && !hasTimestamp) throw new InvalidDataException("Retained telemetry without source time is not trusted as current state.");
        if (timestamp > receivedAt || timestamp < DateTimeOffset.UnixEpoch) throw new InvalidDataException("Telemetry source time is in the future or before the Unix epoch.");
        HmiValue value = type switch
        {
            HmiTagType.Number when valueElement.ValueKind == JsonValueKind.Number && valueElement.TryGetDouble(out double number) && double.IsFinite(number) => HmiValue.From(number),
            HmiTagType.Boolean when valueElement.ValueKind is JsonValueKind.True or JsonValueKind.False => HmiValue.From(valueElement.GetBoolean()),
            HmiTagType.Text when valueElement.ValueKind == JsonValueKind.String && valueElement.GetString() is { Length: <= 4096 } text => HmiValue.From(text),
            _ => throw new InvalidDataException("MQTT telemetry type does not match its mapped tag.")
        };
        return new(value, quality, timestamp);
    }

    /// <summary>Writes the fixed command envelope directly; no anonymous types, object boxing or reflection metadata.</summary>
    public static byte[] EncodeCommand(HmiValue value, DateTimeOffset createdAt)
    {
        if (!Enum.IsDefined(value.Type) || !double.IsFinite(value.Number) || value.Text == null || value.Text.Length > 4096)
            throw new InvalidDataException("Invalid command value.");
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", Guid.NewGuid().ToString("N"));
            writer.WriteString("timestamp", createdAt);
            switch (value.Type)
            {
                case HmiTagType.Number: writer.WriteNumber("value", value.Number); break;
                case HmiTagType.Boolean: writer.WriteBoolean("value", value.Boolean); break;
                case HmiTagType.Text: writer.WriteString("value", value.Text); break;
            }
            writer.WriteEndObject();
        }
        if (buffer.WrittenCount > MaximumPayloadBytes)
            throw new InvalidDataException("Encoded MQTT command exceeds the 16 KiB payload budget.");
        return buffer.WrittenSpan.ToArray();
    }
}
