using System.Drawing;
using System.Drawing.Imaging;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

#pragma warning disable SYSLIB0050
public sealed class ManagedSerializationShapeQualityTests
{
    [Fact]
    public void GraphicsAndIconHaveCanonicalManagedBaseShapes()
    {
        Assert.Equal(typeof(MarshalByRefObject), typeof(Graphics).BaseType);
        Assert.Equal(typeof(MarshalByRefObject), typeof(Icon).BaseType);
        Assert.Contains(typeof(ISerializable), typeof(Icon).GetInterfaces());
        Assert.Contains(typeof(ISerializable), typeof(Image).GetInterfaces());
        Assert.True(typeof(Icon).IsSerializable);
        Assert.True(typeof(Image).IsSerializable);
    }

    [Fact]
    public void IconSerializationUsesOwnedCanonicalDataAndSize()
    {
        using var bitmap = new Bitmap(11, 13);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.CornflowerBlue);
        }

        using Icon original = Icon.CreateOwned(new Bitmap(bitmap));
        SerializationInfo info = CreateInfo(typeof(Icon));
        ((ISerializable)original).GetObjectData(info, default);

        byte[] data = (byte[])info.GetValue("IconData", typeof(byte[]))!;
        Size size = (Size)info.GetValue("IconSize", typeof(Size))!;
        Assert.Equal(new Size(11, 13), size);
        Assert.Equal([0, 0, 1, 0], data.AsSpan(0, 4).ToArray());

        ConstructorInfo? constructor = typeof(Icon).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(SerializationInfo), typeof(StreamingContext)],
            modifiers: null);
        Assert.NotNull(constructor);
        using Icon restored = Assert.IsType<Icon>(constructor.Invoke([info, default(StreamingContext)]));
        Array.Fill(data, (byte)0);
        original.Dispose();

        using Bitmap restoredBitmap = restored.ToBitmap();
        Assert.Equal(new Size(11, 13), restored.Size);
        Assert.Equal(Color.CornflowerBlue.ToArgb(), restoredBitmap.GetPixel(5, 6).ToArgb());
    }

    [Fact]
    public void ImageSerializationUsesAnOwnedDecodableSnapshot()
    {
        using var bitmap = new Bitmap(7, 5);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.SeaGreen);
        }

        SerializationInfo info = CreateInfo(typeof(Bitmap));
        ((ISerializable)bitmap).GetObjectData(info, default);
        byte[] data = (byte[])info.GetValue("Data", typeof(byte[]))!;
        Assert.Equal([137, 80, 78, 71], data.AsSpan(0, 4).ToArray());

        ConstructorInfo? constructor = typeof(Bitmap).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(SerializationInfo), typeof(StreamingContext)],
            modifiers: null);
        Assert.NotNull(constructor);
        using Bitmap restored = Assert.IsType<Bitmap>(constructor.Invoke([info, default(StreamingContext)]));
        Array.Fill(data, (byte)0);
        bitmap.Dispose();
        Assert.Equal(new Size(7, 5), restored.Size);
        Assert.Equal(Color.SeaGreen.ToArgb(), restored.GetPixel(3, 2).ToArgb());
    }

    [Fact]
    public void MetafileSerializationRetainsAnOwnedValidatedSourceSnapshot()
    {
        byte[] source = CreatePlaceableWmf();
        using var original = new Metafile(new MemoryStream(source, writable: false));
        SerializationInfo info = CreateInfo(typeof(Metafile));
        ((ISerializable)original).GetObjectData(info, default);

        byte[] data = (byte[])info.GetValue("Data", typeof(byte[]))!;
        Assert.Equal(source, data);

        ConstructorInfo? constructor = typeof(Metafile).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(SerializationInfo), typeof(StreamingContext)],
            modifiers: null);
        Assert.NotNull(constructor);
        using Metafile restored = Assert.IsType<Metafile>(constructor.Invoke([info, default(StreamingContext)]));
        Array.Fill(source, (byte)0);
        Array.Fill(data, (byte)0);
        original.Dispose();

        MetafileHeader header = restored.GetMetafileHeader();
        Assert.Equal(MetafileType.WmfPlaceable, header.Type);
        Assert.Equal(new Rectangle(10, 20, 100, 200), header.Bounds);
        Assert.Equal(46, header.MetafileSize);
    }

    [Fact]
    public void SerializationRejectsNullInformation()
    {
        using var bitmap = new Bitmap(1, 1);
        using Icon icon = Icon.CreateOwned(new Bitmap(1, 1));
        Assert.Throws<ArgumentNullException>(() => ((ISerializable)bitmap).GetObjectData(null!, default));
        Assert.Throws<ArgumentNullException>(() => ((ISerializable)icon).GetObjectData(null!, default));
    }

    private static SerializationInfo CreateInfo(Type type) =>
        new(type, new FormatterConverter());

    private static byte[] CreatePlaceableWmf()
    {
        byte[] bytes = new byte[46];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x9AC6_CDD7);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6, 2), 10);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8, 2), 20);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(10, 2), 110);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(12, 2), 220);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), 1440);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), checksum);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(24, 2), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), 0x0300);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(34, 4), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(38, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(44, 2), 0);
        return bytes;
    }
}
#pragma warning restore SYSLIB0050
