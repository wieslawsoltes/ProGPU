using System.Runtime.InteropServices;

namespace ProGPU.Linux.Media;

internal static class V4l2Constants
{
    internal const uint VideoCaptureMultiPlanar = 9;
    internal const uint VideoOutputMultiPlanar = 10;
    internal const uint MemoryMap = 1;
    internal const uint MemoryDmaBuf = 4;
    internal const uint FieldNone = 1;
    internal const uint EventSourceChange = 5;
    internal const uint EventSourceChangeResolution = 1;
    internal const uint BufferFlagError = 0x0000_0040;
    internal const uint BufferFlagKeyFrame = 0x0000_0008;
    internal const uint BufferFlagTimestampCopy = 0x0000_4000;
    internal const uint BufferFlagLast = 0x0010_0000;

    internal const int OpenReadWrite = 0x0002;
    internal const int OpenNonBlocking = 0x0800;
    internal const int OpenCloseOnExec = 0x0008_0000;
    internal const int MapShared = 0x01;
    internal const int ProtectRead = 0x01;
    internal const int ProtectWrite = 0x02;
    internal const int PollInput = 0x0001;
    internal const int PollPriority = 0x0002;
    internal const int PollOutput = 0x0004;
    internal const int ErrorAgain = 11;
    internal const int ErrorInterrupted = 4;

    internal const int MaximumPlanes = 8;
    internal const int MaximumQueueBuffers = 64;

    internal static readonly uint H264 = FourCc("H264");
    internal static readonly uint H265 = FourCc("HEVC");
    internal static readonly uint Nv12 = FourCc("NV12");
    internal static readonly uint Nv12MultiPlanar = FourCc("NM12");
    internal static readonly uint Xbgr32 = FourCc("XR24");
    internal static readonly uint Abgr32 = FourCc("AR24");
    internal static readonly uint Xrgb32 = FourCc("BX24");
    internal static readonly uint Argb32 = FourCc("BA24");

    // DRM fourcc values describe the byte layout of the exported allocation.
    internal static readonly uint DrmXrgb8888 = FourCc("XR24");
    internal static readonly uint DrmArgb8888 = FourCc("AR24");
    internal static readonly uint DrmXbgr8888 = FourCc("XB24");
    internal static readonly uint DrmAbgr8888 = FourCc("AB24");
    internal static readonly uint DrmNv12 = FourCc("NV12");
    internal static readonly uint DrmR8 = FourCc("R8  ");
    internal static readonly uint DrmGr88 = FourCc("GR88");

    // Linux UAPI ioctl encoding from asm-generic/ioctl.h. The structures below
    // use the fixed 64-bit ABI shared by supported Linux x64 and arm64 builds.
    internal static readonly nuint EnumerateFormat =
        IoctlReadWrite(2, 64);
    internal static readonly nuint GetFormat = IoctlReadWrite(4, 204);
    internal static readonly nuint SetFormat = IoctlReadWrite(5, 204);
    internal static readonly nuint RequestBuffers = IoctlReadWrite(8, 20);
    internal static readonly nuint QueryBuffer = IoctlReadWrite(9, 88);
    internal static readonly nuint QueueBuffer = IoctlReadWrite(15, 88);
    internal static readonly nuint ExportBuffer = IoctlReadWrite(16, 64);
    internal static readonly nuint DequeueBuffer = IoctlReadWrite(17, 88);
    internal static readonly nuint StreamOn = IoctlWrite(18, 4);
    internal static readonly nuint StreamOff = IoctlWrite(19, 4);
    internal static readonly nuint SetStreamParameters =
        IoctlReadWrite(22, 204);
    internal static readonly nuint SetControl =
        IoctlReadWrite(28, 8);
    internal static readonly nuint EncoderCommand =
        IoctlReadWrite(77, 40);
    internal static readonly nuint DequeueEvent = IoctlRead(89, 136);
    internal static readonly nuint SubscribeEvent = IoctlWrite(90, 32);
    internal static readonly nuint DecoderCommand =
        IoctlReadWrite(96, 72);

    internal const uint CodecControlBase = 0x0099_0900;
    internal const uint VideoBitrateControl =
        CodecControlBase + 207;

    internal static uint FourCc(string value)
    {
        if (value.Length != 4)
        {
            throw new ArgumentException(
                "A fourcc must contain four ASCII characters.",
                nameof(value));
        }
        return (uint)value[0] |
               ((uint)value[1] << 8) |
               ((uint)value[2] << 16) |
               ((uint)value[3] << 24);
    }

    private static nuint IoctlReadWrite(int number, int size) =>
        Ioctl(direction: 3, number, size);

    private static nuint IoctlRead(int number, int size) =>
        Ioctl(direction: 2, number, size);

    private static nuint IoctlWrite(int number, int size) =>
        Ioctl(direction: 1, number, size);

    private static nuint Ioctl(int direction, int number, int size) =>
        (nuint)((uint)direction << 30 |
                (uint)size << 16 |
                (uint)'V' << 8 |
                (uint)number);
}

[StructLayout(LayoutKind.Explicit, Size = 204)]
internal unsafe struct V4l2Format
{
    [FieldOffset(0)]
    internal uint Type;

    [FieldOffset(4)]
    internal V4l2PixelFormatMultiPlanar Pixel;
}

[StructLayout(LayoutKind.Explicit, Size = 192, Pack = 1)]
internal unsafe struct V4l2PixelFormatMultiPlanar
{
    [FieldOffset(0)]
    internal uint Width;

    [FieldOffset(4)]
    internal uint Height;

    [FieldOffset(8)]
    internal uint PixelFormat;

    [FieldOffset(12)]
    internal uint Field;

    [FieldOffset(16)]
    internal uint ColorSpace;

    [FieldOffset(20)]
    private fixed byte _planeFormats[160];

    [FieldOffset(180)]
    internal byte PlaneCount;

    [FieldOffset(181)]
    internal byte Flags;

    [FieldOffset(182)]
    internal byte YCbCrEncoding;

    [FieldOffset(183)]
    internal byte Quantization;

    [FieldOffset(184)]
    internal byte TransferFunction;

    internal readonly V4l2PlanePixelFormat GetPlane(int index)
    {
        if ((uint)index >= V4l2Constants.MaximumPlanes)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        fixed (byte* value = _planeFormats)
        {
            return ((V4l2PlanePixelFormat*)value)[index];
        }
    }

    internal void SetPlane(
        int index,
        uint sizeImage,
        uint bytesPerLine = 0)
    {
        if ((uint)index >= V4l2Constants.MaximumPlanes)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        fixed (byte* value = _planeFormats)
        {
            ((V4l2PlanePixelFormat*)value)[index] =
                new V4l2PlanePixelFormat
                {
                    SizeImage = sizeImage,
                    BytesPerLine = bytesPerLine
                };
        }
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 20)]
internal unsafe struct V4l2PlanePixelFormat
{
    internal uint SizeImage;
    internal uint BytesPerLine;
    private fixed ushort _reserved[6];
}

[StructLayout(LayoutKind.Sequential, Size = 20)]
internal unsafe struct V4l2RequestBuffers
{
    internal uint Count;
    internal uint Type;
    internal uint Memory;
    internal uint Capabilities;
    internal byte Flags;
    private fixed byte _reserved[3];
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
internal unsafe struct V4l2Plane
{
    [FieldOffset(0)]
    internal uint BytesUsed;

    [FieldOffset(4)]
    internal uint Length;

    [FieldOffset(8)]
    internal ulong Memory;

    [FieldOffset(16)]
    internal uint DataOffset;
}

[StructLayout(LayoutKind.Explicit, Size = 88)]
internal unsafe struct V4l2Buffer
{
    [FieldOffset(0)]
    internal uint Index;

    [FieldOffset(4)]
    internal uint Type;

    [FieldOffset(8)]
    internal uint BytesUsed;

    [FieldOffset(12)]
    internal uint Flags;

    [FieldOffset(16)]
    internal uint Field;

    [FieldOffset(24)]
    internal long TimestampSeconds;

    [FieldOffset(32)]
    internal long TimestampMicroseconds;

    [FieldOffset(56)]
    internal uint Sequence;

    [FieldOffset(60)]
    internal uint MemoryType;

    [FieldOffset(64)]
    internal nint Planes;

    [FieldOffset(72)]
    internal uint Length;

    [FieldOffset(76)]
    internal uint Reserved2;

    [FieldOffset(80)]
    internal int RequestFileDescriptor;
}

[StructLayout(LayoutKind.Sequential, Size = 64)]
internal unsafe struct V4l2ExportBuffer
{
    internal uint Type;
    internal uint Index;
    internal uint Plane;
    internal uint Flags;
    internal int FileDescriptor;
    private fixed uint _reserved[11];
}

[StructLayout(LayoutKind.Sequential, Size = 32)]
internal unsafe struct V4l2EventSubscription
{
    internal uint Type;
    internal uint Id;
    internal uint Flags;
    private fixed uint _reserved[5];
}

[StructLayout(LayoutKind.Explicit, Size = 136)]
internal unsafe struct V4l2Event
{
    [FieldOffset(0)]
    internal uint Type;

    [FieldOffset(4)]
    internal uint SourceChangeFlags;
}

[StructLayout(LayoutKind.Explicit, Size = 72)]
internal unsafe struct V4l2DecoderCommand
{
    [FieldOffset(0)]
    internal uint Command;

    [FieldOffset(4)]
    internal uint Flags;
}

[StructLayout(LayoutKind.Explicit, Size = 40)]
internal unsafe struct V4l2EncoderCommand
{
    [FieldOffset(0)]
    internal uint Command;

    [FieldOffset(4)]
    internal uint Flags;
}

[StructLayout(LayoutKind.Sequential, Size = 8)]
internal struct V4l2Control
{
    internal uint Id;
    internal int Value;
}

[StructLayout(LayoutKind.Explicit, Size = 204)]
internal unsafe struct V4l2StreamParameters
{
    [FieldOffset(0)]
    internal uint Type;

    [FieldOffset(12)]
    internal uint TimePerFrameNumerator;

    [FieldOffset(16)]
    internal uint TimePerFrameDenominator;
}

[StructLayout(LayoutKind.Sequential, Size = 8)]
internal struct LinuxPollDescriptor
{
    internal int FileDescriptor;
    internal short Events;
    internal short ReturnedEvents;
}

internal static unsafe partial class LinuxMediaNative
{
    [LibraryImport(
        "libc",
        EntryPoint = "mmap",
        SetLastError = true)]
    internal static partial void* MapMemory(
        void* address,
        nuint length,
        int protection,
        int flags,
        int fileDescriptor,
        nint offset);

    [LibraryImport(
        "libc",
        EntryPoint = "munmap",
        SetLastError = true)]
    internal static partial int UnmapMemory(
        void* address,
        nuint length);

    [LibraryImport(
        "libc",
        EntryPoint = "poll",
        SetLastError = true)]
    internal static partial int Poll(
        LinuxPollDescriptor* descriptors,
        nuint count,
        int timeoutMilliseconds);

    [LibraryImport(
        "libc",
        EntryPoint = "dup",
        SetLastError = true)]
    internal static partial int Duplicate(
        int fileDescriptor);
}
