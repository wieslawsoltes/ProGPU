using System;

namespace System.Drawing.Drawing2D;

/// <summary>
/// Encapsulates the portable serialized form of a <see cref="Region"/>.
/// </summary>
public sealed class RegionData
{
    private byte[] _data;

    internal RegionData(byte[] data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public byte[] Data
    {
        get => (byte[])_data.Clone();
        set => _data = value is null
            ? throw new ArgumentNullException(nameof(value))
            : (byte[])value.Clone();
    }

    internal ReadOnlySpan<byte> AsSpan() => _data;
}
