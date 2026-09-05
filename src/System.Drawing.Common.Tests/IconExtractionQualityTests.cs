using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

public sealed class IconExtractionQualityTests
{
    [Fact]
    public void IcoExtractionSelectsRequestedFrameAndResamplesClosestFrame()
    {
        byte[] encoded = CreateIcon(
            (16, CreatePng(16, Color.Red)),
            (32, CreatePng(32, Color.Blue)));
        using var file = new TemporaryFile(encoded, ".ico");

        using Icon? small = Icon.ExtractIcon(file.Path, 0, smallIcon: true);
        using Icon? large = Icon.ExtractIcon(file.Path, 0, smallIcon: false);
        using Icon? resized = Icon.ExtractIcon(file.Path, 0, 24);
        using Icon? associated = Icon.ExtractAssociatedIcon(file.Path);
        using Bitmap smallBitmap = Assert.IsType<Icon>(small).ToBitmap();
        using Bitmap largeBitmap = Assert.IsType<Icon>(large).ToBitmap();
        using Bitmap resizedBitmap = Assert.IsType<Icon>(resized).ToBitmap();
        using Bitmap associatedBitmap = Assert.IsType<Icon>(associated).ToBitmap();

        Assert.Equal(new Size(16, 16), small.Size);
        Assert.Equal(Color.Red.ToArgb(), smallBitmap.GetPixel(8, 8).ToArgb());
        Assert.Equal(new Size(32, 32), large.Size);
        Assert.Equal(Color.Blue.ToArgb(), largeBitmap.GetPixel(16, 16).ToArgb());
        Assert.Equal(new Size(24, 24), resized.Size);
        Assert.Equal(Color.Blue.ToArgb(), resizedBitmap.GetPixel(12, 12).ToArgb());
        Assert.Equal(new Size(32, 32), associated.Size);
        Assert.Equal(Color.Blue.ToArgb(), associatedBitmap.GetPixel(16, 16).ToArgb());
        Assert.Null(Icon.ExtractIcon(file.Path, 1, 16));
        Assert.Null(Icon.ExtractIcon(file.Path, -1, 16));
    }

    [Fact]
    public void PortableExecutableExtractionSupportsIndexAndResourceId()
    {
        byte[] executable = CreatePortableExecutable(
            (16, CreatePng(16, Color.OrangeRed), 1),
            (32, CreatePng(32, Color.MediumPurple), 2));
        using var file = new TemporaryFile(executable, ".exe");

        using Icon? byIndex = Icon.ExtractIcon(file.Path, 0, 16);
        using Icon? byResource = Icon.ExtractIcon(file.Path, -100, 32);
        using Icon? associated = Icon.ExtractAssociatedIcon(file.Path);
        using Bitmap indexedBitmap = Assert.IsType<Icon>(byIndex).ToBitmap();
        using Bitmap resourceBitmap = Assert.IsType<Icon>(byResource).ToBitmap();
        using Bitmap associatedBitmap = Assert.IsType<Icon>(associated).ToBitmap();

        Assert.Equal(Color.OrangeRed.ToArgb(), indexedBitmap.GetPixel(8, 8).ToArgb());
        Assert.Equal(Color.MediumPurple.ToArgb(), resourceBitmap.GetPixel(16, 16).ToArgb());
        Assert.Equal(Color.MediumPurple.ToArgb(), associatedBitmap.GetPixel(16, 16).ToArgb());
        Assert.Null(Icon.ExtractIcon(file.Path, 1, 16));
        Assert.Null(Icon.ExtractIcon(file.Path, -101, 16));
    }

    [Fact]
    public void ExtractedIconOwnsPixelsAfterSourceFileIsRemoved()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"progpu-icon-{Guid.NewGuid():N}.ico");
        File.WriteAllBytes(path, CreateIcon((16, CreatePng(16, Color.SeaGreen))));

        try
        {
            using Icon icon = Assert.IsType<Icon>(Icon.ExtractIcon(path, 0, 16));
            File.Delete(path);
            using Bitmap bitmap = icon.ToBitmap();
            Assert.Equal(Color.SeaGreen.ToArgb(), bitmap.GetPixel(8, 8).ToArgb());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InvalidInputsPreserveFileAndSizeContracts()
    {
        Assert.Throws<ArgumentNullException>(() => Icon.ExtractIcon(null!, 0, 16));
        Assert.Throws<ArgumentNullException>(() => Icon.ExtractAssociatedIcon(null!));
        Assert.Throws<IOException>(() => Icon.ExtractIcon(string.Empty, 0, 16));
        Assert.Throws<ArgumentException>(() => Icon.ExtractAssociatedIcon(string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => Icon.ExtractIcon("missing.ico", 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Icon.ExtractIcon("missing.ico", 0, ushort.MaxValue + 1));
        Assert.Throws<FileNotFoundException>(() => Icon.ExtractIcon("missing.ico", 0, 16));
        Assert.Throws<FileNotFoundException>(() => Icon.ExtractAssociatedIcon("missing.ico"));

        using var invalid = new TemporaryFile([0x4D, 0x5A, 0, 0], ".exe");
        Assert.Null(Icon.ExtractIcon(invalid.Path, 0, 16));
        Assert.Throws<ArgumentException>(() => Icon.ExtractAssociatedIcon(invalid.Path));
    }

    [Fact]
    public void TruncatedAndOutOfRangeResourcesAreRejectedWithoutPartialIcons()
    {
        byte[] executable = CreatePortableExecutable((16, CreatePng(16, Color.Gold), 1));
        // One-image fixture layout: the RT_ICON data entry starts at resource offset 128.
        BinaryPrimitives.WriteUInt32LittleEndian(executable.AsSpan(512 + 128, 4), 0x7FFF_FFF0u);
        using var invalid = new TemporaryFile(executable, ".exe");

        Assert.Null(Icon.ExtractIcon(invalid.Path, 0, 16));
        Assert.Null(Icon.ExtractIcon(invalid.Path, -100, 16));
    }

    private static byte[] CreatePng(int size, Color color)
    {
        using var bitmap = new Bitmap(size, size);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(color);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] CreateIcon(params (int Size, byte[] Payload)[] images)
    {
        int payloadOffset = checked(6 + images.Length * 16);
        int length = checked(payloadOffset + images.Sum(image => image.Payload.Length));
        var result = new byte[length];
        WriteUInt16(result, 2, 1);
        WriteUInt16(result, 4, checked((ushort)images.Length));

        int cursor = payloadOffset;
        for (int index = 0; index < images.Length; index++)
        {
            (int size, byte[] payload) = images[index];
            int entry = 6 + index * 16;
            result[entry] = checked((byte)(size == 256 ? 0 : size));
            result[entry + 1] = checked((byte)(size == 256 ? 0 : size));
            WriteUInt16(result, entry + 4, 1);
            WriteUInt16(result, entry + 6, 32);
            WriteUInt32(result, entry + 8, checked((uint)payload.Length));
            WriteUInt32(result, entry + 12, checked((uint)cursor));
            payload.CopyTo(result, cursor);
            cursor += payload.Length;
        }

        return result;
    }

    private static byte[] CreatePortableExecutable(
        params (int Size, byte[] Payload, ushort ResourceId)[] images)
    {
        const int headersSize = 512;
        const int rootRva = 0x1000;
        const int root = 0;
        const int iconTypeDirectory = 32;
        int iconLanguageDirectories = iconTypeDirectory + 16 + images.Length * 8;
        int groupTypeDirectory = iconLanguageDirectories + images.Length * 24;
        int groupLanguageDirectory = groupTypeDirectory + 24;
        int dataEntries = groupLanguageDirectory + 24;
        int groupDataEntry = dataEntries + images.Length * 16;
        int payloadOffset = Align4(groupDataEntry + 16);

        int[] iconPayloadOffsets = new int[images.Length];
        int cursor = payloadOffset;
        for (int index = 0; index < images.Length; index++)
        {
            iconPayloadOffsets[index] = cursor;
            cursor = Align4(checked(cursor + images[index].Payload.Length));
        }

        int groupOffset = cursor;
        int groupLength = checked(6 + images.Length * 14);
        int resourceLength = checked(groupOffset + groupLength);
        int rawSize = Align512(resourceLength);
        var result = new byte[headersSize + rawSize];

        WriteUInt16(result, 0, 0x5A4D);
        WriteUInt32(result, 0x3C, 0x80);
        WriteUInt32(result, 0x80, 0x00004550);
        int coff = 0x84;
        WriteUInt16(result, coff, 0x8664);
        WriteUInt16(result, coff + 2, 1);
        WriteUInt16(result, coff + 16, 0xF0);
        WriteUInt16(result, coff + 18, 0x0022);
        int optional = coff + 20;
        WriteUInt16(result, optional, 0x20B);
        WriteUInt32(result, optional + 32, 0x1000);
        WriteUInt32(result, optional + 36, 0x200);
        WriteUInt32(result, optional + 56, 0x2000);
        WriteUInt32(result, optional + 60, headersSize);
        WriteUInt32(result, optional + 108, 16);
        WriteUInt32(result, optional + 128, rootRva);
        WriteUInt32(result, optional + 132, checked((uint)resourceLength));
        int section = optional + 0xF0;
        ".rsrc"u8.CopyTo(result.AsSpan(section, 5));
        WriteUInt32(result, section + 8, checked((uint)resourceLength));
        WriteUInt32(result, section + 12, rootRva);
        WriteUInt32(result, section + 16, checked((uint)rawSize));
        WriteUInt32(result, section + 20, headersSize);
        WriteUInt32(result, section + 36, 0x40000040);

        Span<byte> resources = result.AsSpan(headersSize, rawSize);
        WriteDirectory(resources, root, 2);
        WriteDirectoryEntry(resources, root + 16, 3, iconTypeDirectory);
        WriteDirectoryEntry(resources, root + 24, 14, groupTypeDirectory);
        WriteDirectory(resources, iconTypeDirectory, images.Length);
        for (int index = 0; index < images.Length; index++)
        {
            int languageDirectory = iconLanguageDirectories + index * 24;
            WriteDirectoryEntry(resources, iconTypeDirectory + 16 + index * 8, images[index].ResourceId, languageDirectory);
            WriteDirectory(resources, languageDirectory, 1);
            WriteDataEntryReference(resources, languageDirectory + 16, dataEntries + index * 16);
            WriteUInt32(resources, dataEntries + index * 16, checked((uint)(rootRva + iconPayloadOffsets[index])));
            WriteUInt32(resources, dataEntries + index * 16 + 4, checked((uint)images[index].Payload.Length));
            images[index].Payload.CopyTo(resources.Slice(iconPayloadOffsets[index]));
        }

        WriteDirectory(resources, groupTypeDirectory, 1);
        WriteDirectoryEntry(resources, groupTypeDirectory + 16, 100, groupLanguageDirectory);
        WriteDirectory(resources, groupLanguageDirectory, 1);
        WriteDataEntryReference(resources, groupLanguageDirectory + 16, groupDataEntry);
        WriteUInt32(resources, groupDataEntry, checked((uint)(rootRva + groupOffset)));
        WriteUInt32(resources, groupDataEntry + 4, checked((uint)groupLength));
        WriteUInt16(resources, groupOffset + 2, 1);
        WriteUInt16(resources, groupOffset + 4, checked((ushort)images.Length));
        for (int index = 0; index < images.Length; index++)
        {
            int entry = groupOffset + 6 + index * 14;
            int size = images[index].Size;
            resources[entry] = checked((byte)(size == 256 ? 0 : size));
            resources[entry + 1] = checked((byte)(size == 256 ? 0 : size));
            WriteUInt16(resources, entry + 4, 1);
            WriteUInt16(resources, entry + 6, 32);
            WriteUInt32(resources, entry + 8, checked((uint)images[index].Payload.Length));
            WriteUInt16(resources, entry + 12, images[index].ResourceId);
        }

        return result;
    }

    private static void WriteDirectory(Span<byte> bytes, int offset, int idCount) =>
        WriteUInt16(bytes, offset + 14, checked((ushort)idCount));

    private static void WriteDirectoryEntry(Span<byte> bytes, int offset, int id, int directory)
    {
        WriteUInt32(bytes, offset, checked((uint)id));
        WriteUInt32(bytes, offset + 4, checked((uint)directory) | 0x80000000u);
    }

    private static void WriteDataEntryReference(Span<byte> bytes, int offset, int dataEntry)
    {
        WriteUInt32(bytes, offset, 1033);
        WriteUInt32(bytes, offset + 4, checked((uint)dataEntry));
    }

    private static int Align4(int value) => checked((value + 3) & ~3);
    private static int Align512(int value) => checked((value + 511) & ~511);

    private static void WriteUInt16(Span<byte> bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(offset, 2), value);

    private static void WriteUInt32(Span<byte> bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(offset, 4), value);

    private sealed class TemporaryFile : IDisposable
    {
        internal TemporaryFile(byte[] contents, string extension)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"progpu-icon-{Guid.NewGuid():N}{extension}");
            File.WriteAllBytes(Path, contents);
        }

        internal string Path { get; }

        public void Dispose() => File.Delete(Path);
    }
}
