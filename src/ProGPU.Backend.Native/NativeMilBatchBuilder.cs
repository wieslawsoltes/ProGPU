using System.Buffers;
using System.Buffers.Binary;

namespace ProGPU.Backend.Native;

/// <summary>
/// Writes canonical, DWORD-aligned WPF DUCE/MIL channel batches.
/// </summary>
public sealed class NativeMilBatchBuilder
{
    private readonly ArrayBufferWriter<byte> _writer;

    public NativeMilBatchBuilder(int initialCapacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);
        _writer = new ArrayBufferWriter<byte>(initialCapacity);
    }

    public int Length => _writer.WrittenCount;

    public ReadOnlySpan<byte> WrittenSpan => _writer.WrittenSpan;

    public void Clear() => _writer.Clear();

    public byte[] ToArray() => _writer.WrittenSpan.ToArray();

    public void CreateResource(uint handle, NativeMilResourceType resourceType)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.CreateResource, 12);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, (uint)resourceType);
    }

    public void DeleteResource(uint handle, NativeMilResourceType resourceType)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.DeleteResource, 12);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, (uint)resourceType);
    }

    public void CreateVisual(uint handle)
    {
        WriteHandleCommand(NativeMilCommand.VisualCreate, handle);
    }

    public void SetVisualOffset(uint handle, double x, double y)
    {
        ValidateHandle(handle);
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualSetOffset, 24);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, x);
        WriteDouble(packet, 16, y);
    }

    public void SetVisualOpacity(uint handle, double opacity)
    {
        ValidateHandle(handle);
        if (!double.IsFinite(opacity) || opacity < 0.0 || opacity > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualSetAlpha, 16);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, opacity);
    }

    public void SetVisualContent(uint handle, uint contentHandle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualSetContent, 12);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, contentHandle);
    }

    public void InsertVisualChild(uint handle, uint childHandle, uint index)
    {
        ValidateHandle(handle);
        ValidateHandle(childHandle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.VisualInsertChildAt, 16);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, childHandle);
        WriteUInt32(packet, 12, index);
    }

    public void CreateGenericTarget(
        uint handle,
        uint pixelWidth,
        uint pixelHeight,
        uint flags = 0,
        ulong platformRenderTarget = 0,
        ulong section = 0)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.GenericTargetCreate, 36);
        WriteUInt32(packet, 4, handle);
        WriteUInt64(packet, 8, platformRenderTarget);
        WriteUInt64(packet, 16, section);
        WriteUInt32(packet, 24, pixelWidth);
        WriteUInt32(packet, 28, pixelHeight);
        WriteUInt32(packet, 32, flags);
    }

    public void SetTargetRoot(uint handle, uint rootHandle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.TargetSetRoot, 12);
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, rootHandle);
    }

    public void SetTargetClearColor(uint handle, NativeMilColor color)
    {
        ValidateHandle(handle);
        ValidateColor(color);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.TargetSetClearColor, 24);
        WriteUInt32(packet, 4, handle);
        WriteSingle(packet, 8, color.Red);
        WriteSingle(packet, 12, color.Green);
        WriteSingle(packet, 16, color.Blue);
        WriteSingle(packet, 20, color.Alpha);
    }

    public void SetSolidColorBrush(
        uint handle,
        NativeMilColor color,
        double opacity = 1.0)
    {
        ValidateHandle(handle);
        ValidateColor(color);
        if (!double.IsFinite(opacity) || opacity < 0.0 || opacity > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.SolidColorBrush, 48);
        WriteUInt32(packet, 4, handle);
        WriteDouble(packet, 8, opacity);
        WriteSingle(packet, 16, color.Red);
        WriteSingle(packet, 20, color.Green);
        WriteSingle(packet, 24, color.Blue);
        WriteSingle(packet, 28, color.Alpha);
    }

    public void SetRenderData(uint handle, NativeMilRenderDataBuilder renderData)
    {
        ValidateHandle(handle);
        ArgumentNullException.ThrowIfNull(renderData);
        ReadOnlySpan<byte> nested = renderData.WrittenSpan;
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer,
            NativeMilCommand.RenderData,
            checked(12 + nested.Length));
        WriteUInt32(packet, 4, handle);
        WriteUInt32(packet, 8, checked((uint)nested.Length));
        nested.CopyTo(packet[12..]);
    }

    private void WriteHandleCommand(uint command, uint handle)
    {
        ValidateHandle(handle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, command, 8);
        WriteUInt32(packet, 4, handle);
    }

    private static void ValidateHandle(uint handle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(handle);
    }

    internal static void ValidateColor(NativeMilColor color)
    {
        if (!float.IsFinite(color.Red) || !float.IsFinite(color.Green) ||
            !float.IsFinite(color.Blue) || !float.IsFinite(color.Alpha))
        {
            throw new ArgumentOutOfRangeException(nameof(color));
        }
    }

    internal static void WriteUInt32(Span<byte> packet, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(packet[offset..], value);

    internal static void WriteUInt64(Span<byte> packet, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(packet[offset..], value);

    internal static void WriteDouble(Span<byte> packet, int offset, double value) =>
        WriteUInt64(packet, offset, BitConverter.DoubleToUInt64Bits(value));

    internal static void WriteSingle(Span<byte> packet, int offset, float value) =>
        WriteUInt32(packet, offset, BitConverter.SingleToUInt32Bits(value));
}

/// <summary>
/// Writes the nested instruction stream carried by a MIL render-data resource.
/// </summary>
public sealed class NativeMilRenderDataBuilder
{
    private readonly ArrayBufferWriter<byte> _writer;

    public NativeMilRenderDataBuilder(int initialCapacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);
        _writer = new ArrayBufferWriter<byte>(initialCapacity);
    }

    public int Length => _writer.WrittenCount;

    public ReadOnlySpan<byte> WrittenSpan => _writer.WrittenSpan;

    public void Clear() => _writer.Clear();

    public void PushOpacity(double opacity)
    {
        if (!double.IsFinite(opacity) || opacity < 0.0 || opacity > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.PushOpacity, 12);
        NativeMilBatchBuilder.WriteDouble(packet, 4, opacity);
    }

    public void Pop()
    {
        _ = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.Pop, 4);
    }

    public void DrawRectangle(
        double x,
        double y,
        double width,
        double height,
        uint brushHandle,
        uint penHandle = 0)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || !double.IsFinite(height) ||
            width < 0.0 || height < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        ArgumentOutOfRangeException.ThrowIfZero(brushHandle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.DrawRectangle, 44);
        NativeMilBatchBuilder.WriteDouble(packet, 4, x);
        NativeMilBatchBuilder.WriteDouble(packet, 12, y);
        NativeMilBatchBuilder.WriteDouble(packet, 20, width);
        NativeMilBatchBuilder.WriteDouble(packet, 28, height);
        NativeMilBatchBuilder.WriteUInt32(packet, 36, brushHandle);
        NativeMilBatchBuilder.WriteUInt32(packet, 40, penHandle);
    }

    public void DrawEllipse(
        double centerX,
        double centerY,
        double radiusX,
        double radiusY,
        uint brushHandle,
        uint penHandle = 0)
    {
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY) ||
            !double.IsFinite(radiusX) || !double.IsFinite(radiusY) ||
            radiusX < 0.0 || radiusY < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusX));
        }
        ArgumentOutOfRangeException.ThrowIfZero(brushHandle);
        Span<byte> packet = NativeMilBatchEncoding.Allocate(
            _writer, NativeMilCommand.DrawEllipse, 44);
        NativeMilBatchBuilder.WriteDouble(packet, 4, centerX);
        NativeMilBatchBuilder.WriteDouble(packet, 12, centerY);
        NativeMilBatchBuilder.WriteDouble(packet, 20, radiusX);
        NativeMilBatchBuilder.WriteDouble(packet, 28, radiusY);
        NativeMilBatchBuilder.WriteUInt32(packet, 36, brushHandle);
        NativeMilBatchBuilder.WriteUInt32(packet, 40, penHandle);
    }
}

internal static class NativeMilBatchEncoding
{
    internal static Span<byte> Allocate(
        ArrayBufferWriter<byte> writer,
        uint command,
        int packetSize)
    {
        int itemSize = checked((packetSize + 4 + 3) & ~3);
        Span<byte> item = writer.GetSpan(itemSize)[..itemSize];
        item.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(item, (uint)itemSize);
        BinaryPrimitives.WriteUInt32LittleEndian(item[4..], command);
        writer.Advance(itemSize);
        return item.Slice(4, packetSize);
    }
}

internal static class NativeMilCommand
{
    internal const uint CreateResource = 0x07;
    internal const uint DeleteResource = 0x08;
    internal const uint RenderData = 0x18;
    internal const uint VisualCreate = 0x1a;
    internal const uint VisualSetOffset = 0x1b;
    internal const uint VisualSetAlpha = 0x20;
    internal const uint VisualSetContent = 0x22;
    internal const uint VisualInsertChildAt = 0x26;
    internal const uint GenericTargetCreate = 0x34;
    internal const uint TargetSetRoot = 0x35;
    internal const uint TargetSetClearColor = 0x36;
    internal const uint DrawRectangle = 0x40;
    internal const uint DrawEllipse = 0x44;
    internal const uint PushOpacity = 0x4f;
    internal const uint Pop = 0x56;
    internal const uint SolidColorBrush = 0x7e;
}
