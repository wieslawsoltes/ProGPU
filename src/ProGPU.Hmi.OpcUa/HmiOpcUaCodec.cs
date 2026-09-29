using Opc.Ua;

namespace ProGPU.Hmi.OpcUa;

/// <summary>Strict scalar conversion. Arrays, structures, coercive Boolean conversion and lossy 64-bit integers are not admitted.</summary>
public static class HmiOpcUaCodec
{
    public const double MaximumExactInteger = 9007199254740991d;

    public static HmiTagSample Decode(HmiIoMapping mapping, DataValue value, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(value);
        mapping.OpcUa.Validate(mapping.Type);
        HmiValue decoded = Default(mapping.Type);
        var quality = StatusCode.IsBad(value.StatusCode) ? HmiQuality.Bad : StatusCode.IsUncertain(value.StatusCode) ? HmiQuality.Uncertain : HmiQuality.Good;
        try
        {
            decoded = DecodeScalar(mapping, value.Value);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            quality = HmiQuality.Bad;
        }
        // Receipt time cannot repair missing or future source timestamps.
        var source = value.SourceTimestamp;
        DateTimeOffset timestamp;
        if (source == DateTime.MinValue) { timestamp = DateTimeOffset.MinValue; if (quality == HmiQuality.Good) quality = HmiQuality.Uncertain; }
        else
        {
            timestamp = new DateTimeOffset(DateTime.SpecifyKind(source, DateTimeKind.Utc));
            if (timestamp > receivedAt) { timestamp = DateTimeOffset.MinValue; quality = HmiQuality.Bad; }
        }
        if (StatusCode.IsBad(value.StatusCode)) quality = HmiQuality.Bad;
        return new(decoded, quality, timestamp);
    }
    public static HmiValue DecodeScalar(HmiIoMapping mapping, object? value)
    {
        mapping.OpcUa.Validate(mapping.Type);
        if (value == null || value.GetType() != ClrType(mapping.OpcUa.DataType)) throw new InvalidDataException("OPC UA scalar type mismatch.");
        if (value is bool boolean) return HmiValue.From(boolean);
        if (value is string text)
        {
            if (text.Length > 4096) throw new InvalidDataException("OPC UA string exceeds 4096 characters.");
            return HmiValue.From(text);
        }
        double raw = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(raw) || (value is long or ulong) && Math.Abs(raw) > MaximumExactInteger)
            throw new InvalidDataException("OPC UA number cannot be represented exactly by the HMI value model.");
        double engineering = raw * mapping.Scale + mapping.Offset;
        if (!double.IsFinite(engineering)) throw new InvalidDataException("OPC UA engineering conversion overflow.");
        return HmiValue.From(engineering);
    }
    public static object Encode(HmiIoMapping mapping, HmiValue value)
    {
        mapping.OpcUa.Validate(mapping.Type);
        if (value.Type != mapping.Type || !double.IsFinite(mapping.Scale) || mapping.Scale == 0 || !double.IsFinite(mapping.Offset))
            throw new InvalidDataException("Invalid OPC UA write mapping/value.");
        if (value.Type == HmiTagType.Boolean) return value.Boolean;
        if (value.Type == HmiTagType.Text)
        {
            if (value.Text == null || value.Text.Length > 4096) throw new InvalidDataException("Invalid OPC UA string write.");
            return value.Text;
        }
        double raw = (value.Number - mapping.Offset) / mapping.Scale;
        if (!double.IsFinite(raw)) throw new InvalidDataException("OPC UA inverse scaling is nonfinite.");
        if (mapping.OpcUa.DataType is not (HmiOpcUaDataType.Float or HmiOpcUaDataType.Double) &&
            (raw != Math.Truncate(raw) || Math.Abs(raw) > MaximumExactInteger))
            throw new InvalidDataException("Integer OPC UA writes require an exactly represented integer.");
        checked
        {
            return mapping.OpcUa.DataType switch
            {
                HmiOpcUaDataType.SByte => (object)(sbyte)raw,
                HmiOpcUaDataType.Byte => (byte)raw,
                HmiOpcUaDataType.Int16 => (short)raw,
                HmiOpcUaDataType.UInt16 => (ushort)raw,
                HmiOpcUaDataType.Int32 => (int)raw,
                HmiOpcUaDataType.UInt32 => (uint)raw,
                HmiOpcUaDataType.Int64 => (long)raw,
                HmiOpcUaDataType.UInt64 => (ulong)raw,
                HmiOpcUaDataType.Float when float.IsFinite((float)raw) => (float)raw,
                HmiOpcUaDataType.Double => raw,
                _ => throw new InvalidDataException("Unsupported or overflowing OPC UA scalar write.")
            };
        }
    }
    public static Type ClrType(HmiOpcUaDataType type) => type switch
    {
        HmiOpcUaDataType.Boolean => typeof(bool), HmiOpcUaDataType.SByte => typeof(sbyte), HmiOpcUaDataType.Byte => typeof(byte),
        HmiOpcUaDataType.Int16 => typeof(short), HmiOpcUaDataType.UInt16 => typeof(ushort), HmiOpcUaDataType.Int32 => typeof(int),
        HmiOpcUaDataType.UInt32 => typeof(uint), HmiOpcUaDataType.Int64 => typeof(long), HmiOpcUaDataType.UInt64 => typeof(ulong),
        HmiOpcUaDataType.Float => typeof(float), HmiOpcUaDataType.Double => typeof(double), HmiOpcUaDataType.String => typeof(string),
        _ => throw new InvalidDataException("Unsupported OPC UA scalar type.")
    };
    internal static HmiValue Default(HmiTagType type) => type switch
    {
        HmiTagType.Boolean => HmiValue.From(false), HmiTagType.Text => HmiValue.From(""), _ => HmiValue.From(0d)
    };
    public static NodeId Resolve(HmiOpcUaAddress address, NamespaceTable namespaces)
    {
        // Syntax validation is independent of the configured scalar value type.
        address.Validate(address.DataType == HmiOpcUaDataType.Boolean ? HmiTagType.Boolean : address.DataType == HmiOpcUaDataType.String ? HmiTagType.Text : HmiTagType.Number);
        int index = address.NamespaceUri.Length == 0 ? 0 : namespaces.GetIndex(address.NamespaceUri);
        if (index is < 0 or > ushort.MaxValue) throw new InvalidDataException("Configured OPC UA namespace URI is not present on this server.");
        var local = NodeId.Parse(address.Identifier);
        if (local.NamespaceIndex != 0) throw new InvalidDataException("Persist namespace URIs, not volatile namespace indexes.");
        return new NodeId(local.Identifier, (ushort)index);
    }
}
