using System.Buffers.Binary;

namespace ProGPU.Hmi.Modbus;

/// <summary>Explicit register representation, word/byte order and engineering-unit conversion; never host-endian dependent.</summary>
public static class HmiRegisterCodec
{
    public static double Decode(ReadOnlySpan<byte> networkBytes, HmiIoMapping mapping)
    {
        if (networkBytes.Length != mapping.RegisterCount * 2) throw new InvalidDataException("Wrong register byte count.");
        Span<byte> bytes = stackalloc byte[8];
        networkBytes.CopyTo(bytes);
        bytes = bytes[..networkBytes.Length];
        Reorder(bytes, mapping.Order);
        double raw = mapping.Encoding switch
        {
            HmiRegisterEncoding.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(bytes),
            HmiRegisterEncoding.Int16 => BinaryPrimitives.ReadInt16BigEndian(bytes),
            HmiRegisterEncoding.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(bytes),
            HmiRegisterEncoding.Int32 => BinaryPrimitives.ReadInt32BigEndian(bytes),
            HmiRegisterEncoding.Float32 => BinaryPrimitives.ReadSingleBigEndian(bytes),
            HmiRegisterEncoding.Float64 => BinaryPrimitives.ReadDoubleBigEndian(bytes),
            _ => throw new InvalidDataException("Boolean values must use bit-area decoding.")
        };
        double value = raw * mapping.Scale + mapping.Offset;
        if (!double.IsFinite(raw) || !double.IsFinite(value)) throw new InvalidDataException("Controller returned a nonfinite numeric value.");
        return value;
    }
    public static byte[] Encode(double value, HmiIoMapping mapping)
    {
        double raw = (value - mapping.Offset) / mapping.Scale;
        if (!double.IsFinite(raw)) throw new InvalidDataException("The engineering value cannot be encoded.");
        byte[] bytes = new byte[mapping.RegisterCount * 2];
        bool integer = mapping.Encoding is HmiRegisterEncoding.UInt16 or HmiRegisterEncoding.Int16 or HmiRegisterEncoding.UInt32 or HmiRegisterEncoding.Int32;
        if (integer && raw != Math.Truncate(raw)) throw new InvalidDataException("The setpoint does not map exactly to an integer register; no silent rounding is permitted.");
        try
        {
            switch (mapping.Encoding)
            {
                case HmiRegisterEncoding.UInt16: BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)raw)); break;
                case HmiRegisterEncoding.Int16: BinaryPrimitives.WriteInt16BigEndian(bytes, checked((short)raw)); break;
                case HmiRegisterEncoding.UInt32: BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)raw)); break;
                case HmiRegisterEncoding.Int32: BinaryPrimitives.WriteInt32BigEndian(bytes, checked((int)raw)); break;
                case HmiRegisterEncoding.Float32:
                    if (!float.IsFinite((float)raw)) throw new InvalidDataException("Float32 overflow.");
                    BinaryPrimitives.WriteSingleBigEndian(bytes, (float)raw); break;
                case HmiRegisterEncoding.Float64: BinaryPrimitives.WriteDoubleBigEndian(bytes, raw); break;
                default: throw new InvalidDataException("Unsupported register encoding.");
            }
        }
        catch (OverflowException error) { throw new InvalidDataException("Setpoint overflows the controller representation.", error); }
        Reorder(bytes, mapping.Order);
        return bytes;
    }
    private static void Reorder(Span<byte> bytes, HmiRegisterOrder order)
    {
        if (!Enum.IsDefined(order)) throw new InvalidDataException("Unknown register byte order.");
        if (order is HmiRegisterOrder.SwapBytes or HmiRegisterOrder.SwapBytesAndWords)
            for (int i = 0; i < bytes.Length; i += 2) (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
        if (order is HmiRegisterOrder.SwapWords or HmiRegisterOrder.SwapBytesAndWords)
            for (int i = 0; i < bytes.Length / 2; i += 2)
            {
                int opposite = bytes.Length - 2 - i;
                (bytes[i], bytes[opposite]) = (bytes[opposite], bytes[i]);
                (bytes[i + 1], bytes[opposite + 1]) = (bytes[opposite + 1], bytes[i + 1]);
            }
    }
}
