using System.Buffers.Binary;

namespace System.Drawing;

public sealed partial class Icon
{
    private const int PortableSmallIconSize = 16;
    private const int PortableLargeIconSize = 32;

    public static Icon? ExtractAssociatedIcon(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (filePath.Length == 0)
        {
            throw new ArgumentException("The file path cannot be empty.", nameof(filePath));
        }

        byte[] source = File.ReadAllBytes(filePath);
        if (PortableIconExtractor.TryExtract(source, id: 0, PortableLargeIconSize, out byte[]? encoded)
            && encoded is not null)
        {
            return TryCreateExtractedIcon(encoded, PortableLargeIconSize)
                ?? throw new ArgumentException(
                    "The file does not contain an associated image.",
                    nameof(filePath));
        }

        try
        {
            using var bitmap = new Bitmap(filePath);
            return CreateOwned(new Bitmap(bitmap, PortableLargeIconSize, PortableLargeIconSize));
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "The file does not contain an associated image.",
                nameof(filePath),
                exception);
        }
    }

    public static Icon? ExtractIcon(string filePath, int id, bool smallIcon = false) =>
        ExtractIcon(filePath, id, smallIcon ? PortableSmallIconSize : PortableLargeIconSize);

    public static Icon? ExtractIcon(string filePath, int id, int size)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if ((uint)(size - 1) >= ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        if (filePath.Length == 0)
        {
            throw new IOException("The icon path cannot be empty.");
        }

        byte[] source = File.ReadAllBytes(filePath);
        return PortableIconExtractor.TryExtract(source, id, size, out byte[]? encoded) && encoded is not null
            ? TryCreateExtractedIcon(encoded, size)
            : null;
    }

    private static Icon? TryCreateExtractedIcon(byte[] encoded, int size)
    {
        try
        {
            using var stream = new MemoryStream(encoded, writable: false);
            return new Icon(stream, size, size);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

internal static class PortableIconExtractor
{
    private const int IconDirectorySize = 6;
    private const int IconEntrySize = 16;
    private const int GroupIconEntrySize = 14;
    private const int PeSignatureSize = 4;
    private const int CoffHeaderSize = 20;
    private const int SectionHeaderSize = 40;
    private const int ResourceDirectorySize = 16;
    private const int ResourceDirectoryEntrySize = 8;
    private const uint DirectoryFlag = 0x80000000u;
    private const int ResourceTypeIcon = 3;
    private const int ResourceTypeGroupIcon = 14;

    internal static bool TryExtract(byte[] source, int id, int requestedSize, out byte[]? encoded)
    {
        encoded = null;
        ReadOnlySpan<byte> bytes = source;
        if (IsIcon(bytes))
        {
            if (id != 0)
            {
                return false;
            }

            return TryExtractIconContainer(bytes, requestedSize, out encoded);
        }

        return TryExtractPortableExecutable(bytes, id, requestedSize, out encoded);
    }

    private static bool IsIcon(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= IconDirectorySize
        && ReadUInt16(bytes, 0) == 0
        && ReadUInt16(bytes, 2) == 1;

    private static bool TryExtractIconContainer(
        ReadOnlySpan<byte> bytes,
        int requestedSize,
        out byte[]? encoded)
    {
        encoded = null;
        int count = ReadUInt16(bytes, 4);
        if (count == 0 || count > (bytes.Length - IconDirectorySize) / IconEntrySize)
        {
            return false;
        }

        int selected = SelectIconEntry(bytes, IconDirectorySize, IconEntrySize, count, requestedSize);
        if (selected < 0)
        {
            return false;
        }

        int entryOffset = IconDirectorySize + selected * IconEntrySize;
        uint length = ReadUInt32(bytes, entryOffset + 8);
        uint offset = ReadUInt32(bytes, entryOffset + 12);
        if (!TrySlice(bytes, offset, length, out ReadOnlySpan<byte> payload))
        {
            return false;
        }

        encoded = BuildSingleIcon(bytes.Slice(entryOffset, IconEntrySize), payload);
        return true;
    }

    private static bool TryExtractPortableExecutable(
        ReadOnlySpan<byte> bytes,
        int id,
        int requestedSize,
        out byte[]? encoded)
    {
        encoded = null;
        if (bytes.Length < 0x40 || ReadUInt16(bytes, 0) != 0x5A4D)
        {
            return false;
        }

        int peOffset = ReadInt32(bytes, 0x3C);
        if (peOffset < 0 || !Contains(bytes, peOffset, PeSignatureSize + CoffHeaderSize)
            || ReadUInt32(bytes, peOffset) != 0x00004550)
        {
            return false;
        }

        int coffOffset = peOffset + PeSignatureSize;
        int sectionCount = ReadUInt16(bytes, coffOffset + 2);
        int optionalHeaderSize = ReadUInt16(bytes, coffOffset + 16);
        int optionalOffset = coffOffset + CoffHeaderSize;
        int sectionOffset = optionalOffset + optionalHeaderSize;
        if (sectionCount == 0 || sectionCount > 96 || optionalHeaderSize < 120
            || !Contains(bytes, optionalOffset, optionalHeaderSize)
            || !Contains(bytes, sectionOffset, checked(sectionCount * SectionHeaderSize)))
        {
            return false;
        }

        ushort magic = ReadUInt16(bytes, optionalOffset);
        int directoryCountOffset;
        int directoryOffset;
        if (magic == 0x10B)
        {
            directoryCountOffset = optionalOffset + 92;
            directoryOffset = optionalOffset + 96;
        }
        else if (magic == 0x20B)
        {
            if (optionalHeaderSize < 136)
            {
                return false;
            }

            directoryCountOffset = optionalOffset + 108;
            directoryOffset = optionalOffset + 112;
        }
        else
        {
            return false;
        }

        if (!Contains(bytes, directoryCountOffset, 4) || ReadUInt32(bytes, directoryCountOffset) <= 2
            || !Contains(bytes, directoryOffset, 24))
        {
            return false;
        }

        uint resourceRva = ReadUInt32(bytes, directoryOffset + 16);
        uint resourceSize = ReadUInt32(bytes, directoryOffset + 20);
        if (resourceRva == 0 || resourceSize < ResourceDirectorySize || resourceSize > int.MaxValue
            || !TryMapRva(bytes, sectionOffset, sectionCount, resourceRva, resourceSize, out int resourceOffset))
        {
            return false;
        }

        int resourceLength = checked((int)resourceSize);
        if (!Contains(bytes, resourceOffset, resourceLength))
        {
            return false;
        }

        var resources = new ResourceView(bytes, resourceOffset, resourceLength, sectionOffset, sectionCount);
        if (!resources.TryFindIdDirectory(0, ResourceTypeGroupIcon, out int groupTypeDirectory)
            || !resources.TrySelectResource(groupTypeDirectory, id, out int groupResourceDirectory)
            || !resources.TryResolveData(groupResourceDirectory, out ReadOnlySpan<byte> group))
        {
            return false;
        }

        if (group.Length < IconDirectorySize || ReadUInt16(group, 0) != 0 || ReadUInt16(group, 2) != 1)
        {
            return false;
        }

        int count = ReadUInt16(group, 4);
        if (count == 0 || count > (group.Length - IconDirectorySize) / GroupIconEntrySize)
        {
            return false;
        }

        int selected = SelectIconEntry(group, IconDirectorySize, GroupIconEntrySize, count, requestedSize);
        if (selected < 0)
        {
            return false;
        }

        int groupEntryOffset = IconDirectorySize + selected * GroupIconEntrySize;
        int iconResourceId = ReadUInt16(group, groupEntryOffset + 12);
        if (!resources.TryFindIdDirectory(0, ResourceTypeIcon, out int iconTypeDirectory)
            || !resources.TryFindIdDirectory(iconTypeDirectory, iconResourceId, out int iconResourceDirectory)
            || !resources.TryResolveData(iconResourceDirectory, out ReadOnlySpan<byte> payload))
        {
            return false;
        }

        Span<byte> iconEntry = stackalloc byte[IconEntrySize];
        group.Slice(groupEntryOffset, 12).CopyTo(iconEntry);
        BinaryPrimitives.WriteUInt32LittleEndian(iconEntry.Slice(8, 4), checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(iconEntry.Slice(12, 4), IconDirectorySize + IconEntrySize);
        encoded = BuildSingleIcon(iconEntry, payload);
        return true;
    }

    private static int SelectIconEntry(
        ReadOnlySpan<byte> entries,
        int start,
        int stride,
        int count,
        int requestedSize)
    {
        int selected = -1;
        int bestDistance = int.MaxValue;
        int bestArea = -1;
        for (int index = 0; index < count; index++)
        {
            int offset = start + index * stride;
            int width = entries[offset] == 0 ? 256 : entries[offset];
            int height = entries[offset + 1] == 0 ? 256 : entries[offset + 1];
            int distance = Math.Abs(width - requestedSize) + Math.Abs(height - requestedSize);
            int area = width * height;
            if (distance < bestDistance || (distance == bestDistance && area > bestArea))
            {
                selected = index;
                bestDistance = distance;
                bestArea = area;
            }
        }

        return selected;
    }

    private static byte[] BuildSingleIcon(ReadOnlySpan<byte> entry, ReadOnlySpan<byte> payload)
    {
        var result = new byte[checked(IconDirectorySize + IconEntrySize + payload.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4, 2), 1);
        entry.CopyTo(result.AsSpan(IconDirectorySize, IconEntrySize));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(IconDirectorySize + 8, 4), checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(IconDirectorySize + 12, 4), IconDirectorySize + IconEntrySize);
        payload.CopyTo(result.AsSpan(IconDirectorySize + IconEntrySize));
        return result;
    }

    private static bool TryMapRva(
        ReadOnlySpan<byte> bytes,
        int sectionOffset,
        int sectionCount,
        uint rva,
        uint length,
        out int offset)
    {
        offset = 0;
        for (int index = 0; index < sectionCount; index++)
        {
            int header = sectionOffset + index * SectionHeaderSize;
            uint virtualSize = ReadUInt32(bytes, header + 8);
            uint virtualAddress = ReadUInt32(bytes, header + 12);
            uint rawSize = ReadUInt32(bytes, header + 16);
            uint rawOffset = ReadUInt32(bytes, header + 20);
            uint span = Math.Max(virtualSize, rawSize);
            if (rva < virtualAddress || (ulong)rva + length > (ulong)virtualAddress + span)
            {
                continue;
            }

            ulong relative = rva - virtualAddress;
            if (relative + length > rawSize || (ulong)rawOffset + relative + length > (ulong)bytes.Length)
            {
                return false;
            }

            offset = checked((int)(rawOffset + relative));
            return true;
        }

        return false;
    }

    private static bool TrySlice(ReadOnlySpan<byte> bytes, uint offset, uint length, out ReadOnlySpan<byte> value)
    {
        if ((ulong)offset + length > (ulong)bytes.Length)
        {
            value = default;
            return false;
        }

        value = bytes.Slice(checked((int)offset), checked((int)length));
        return true;
    }

    private static bool Contains(ReadOnlySpan<byte> bytes, int offset, int length) =>
        offset >= 0 && length >= 0 && (ulong)(uint)offset + (uint)length <= (ulong)bytes.Length;

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));

    private readonly ref struct ResourceView
    {
        private readonly ReadOnlySpan<byte> _file;
        private readonly int _rootOffset;
        private readonly int _length;
        private readonly int _sectionOffset;
        private readonly int _sectionCount;

        internal ResourceView(
            ReadOnlySpan<byte> file,
            int rootOffset,
            int length,
            int sectionOffset,
            int sectionCount)
        {
            _file = file;
            _rootOffset = rootOffset;
            _length = length;
            _sectionOffset = sectionOffset;
            _sectionCount = sectionCount;
        }

        internal bool TryFindIdDirectory(int directory, int id, out int childDirectory)
        {
            childDirectory = 0;
            if (!TryGetEntries(directory, out int entries, out int count))
            {
                return false;
            }

            for (int index = 0; index < count; index++)
            {
                int entry = entries + index * ResourceDirectoryEntrySize;
                uint name = ReadUInt32(_file, _rootOffset + entry);
                uint target = ReadUInt32(_file, _rootOffset + entry + 4);
                if ((name & DirectoryFlag) == 0 && (name & 0xFFFFu) == (uint)id
                    && (target & DirectoryFlag) != 0)
                {
                    int candidate = checked((int)(target & ~DirectoryFlag));
                    if (ContainsResource(candidate, ResourceDirectorySize))
                    {
                        childDirectory = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        internal bool TrySelectResource(int directory, int id, out int childDirectory)
        {
            childDirectory = 0;
            if (!TryGetEntries(directory, out int entries, out int count))
            {
                return false;
            }

            int selected = id >= 0 ? id : -1;
            int resourceId = id < 0 && id != int.MinValue ? -id : -1;
            if (selected >= count || (selected < 0 && resourceId < 0))
            {
                return false;
            }

            for (int index = 0; index < count; index++)
            {
                int entry = entries + index * ResourceDirectoryEntrySize;
                uint name = ReadUInt32(_file, _rootOffset + entry);
                uint target = ReadUInt32(_file, _rootOffset + entry + 4);
                bool match = selected >= 0
                    ? index == selected
                    : (name & DirectoryFlag) == 0 && (name & 0xFFFFu) == (uint)resourceId;
                if (match && (target & DirectoryFlag) != 0)
                {
                    int candidate = checked((int)(target & ~DirectoryFlag));
                    if (ContainsResource(candidate, ResourceDirectorySize))
                    {
                        childDirectory = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        internal bool TryResolveData(int directory, out ReadOnlySpan<byte> data)
        {
            data = default;
            if (!TryGetEntries(directory, out int entries, out int count) || count == 0)
            {
                return false;
            }

            uint target = ReadUInt32(_file, _rootOffset + entries + 4);
            if ((target & DirectoryFlag) != 0)
            {
                int languageDirectory = checked((int)(target & ~DirectoryFlag));
                if (!TryGetEntries(languageDirectory, out int languageEntries, out int languageCount)
                    || languageCount == 0)
                {
                    return false;
                }

                target = ReadUInt32(_file, _rootOffset + languageEntries + 4);
            }

            if ((target & DirectoryFlag) != 0)
            {
                return false;
            }

            int dataEntry = checked((int)target);
            if (!ContainsResource(dataEntry, 16))
            {
                return false;
            }

            uint dataRva = ReadUInt32(_file, _rootOffset + dataEntry);
            uint dataLength = ReadUInt32(_file, _rootOffset + dataEntry + 4);
            if (!TryMapRva(_file, _sectionOffset, _sectionCount, dataRva, dataLength, out int dataOffset))
            {
                return false;
            }

            data = _file.Slice(dataOffset, checked((int)dataLength));
            return true;
        }

        private bool TryGetEntries(int directory, out int entries, out int count)
        {
            entries = 0;
            count = 0;
            if (!ContainsResource(directory, ResourceDirectorySize))
            {
                return false;
            }

            int named = ReadUInt16(_file, _rootOffset + directory + 12);
            int ids = ReadUInt16(_file, _rootOffset + directory + 14);
            count = named + ids;
            entries = directory + ResourceDirectorySize;
            return count <= (_length - entries) / ResourceDirectoryEntrySize;
        }

        private bool ContainsResource(int offset, int length) =>
            offset >= 0 && length >= 0 && (ulong)(uint)offset + (uint)length <= (ulong)_length;
    }
}
