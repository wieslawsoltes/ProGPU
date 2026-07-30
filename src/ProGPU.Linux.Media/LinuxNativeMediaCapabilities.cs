using System.Runtime.InteropServices;
using System.Text;

namespace ProGPU.Linux.Media;

[Flags]
public enum LinuxHardwareVideoCodec
{
    None = 0,
    H264 = 1 << 0,
    H265 = 1 << 1,
    Vp8 = 1 << 2,
    Vp9 = 1 << 3,
    Av1 = 1 << 4,
    Mpeg2 = 1 << 5
}

public readonly record struct LinuxVideoDecoderDevice(
    string Path,
    string Driver,
    string Card,
    LinuxHardwareVideoCodec Codecs,
    bool UsesMultiPlanarQueues,
    bool SupportsStreaming);

[Flags]
public enum LinuxRawVideoFormat
{
    None = 0,
    Nv12 = 1 << 0,
    Nv12MultiPlanar = 1 << 1,
    Xrgb8888 = 1 << 2,
    Argb8888 = 1 << 3,
    Xbgr8888 = 1 << 4,
    Abgr8888 = 1 << 5
}

public readonly record struct LinuxVideoEncoderDevice(
    string Path,
    string Driver,
    string Card,
    LinuxHardwareVideoCodec Codecs,
    LinuxRawVideoFormat InputFormats,
    bool UsesMultiPlanarQueues,
    bool SupportsStreaming,
    bool SupportsDmaBufInput);

public readonly record struct LinuxNativeMediaCapabilitySnapshot(
    IReadOnlyList<LinuxVideoDecoderDevice> VideoDecoders,
    bool PipeWireAvailable)
{
    public IReadOnlyList<LinuxVideoEncoderDevice>
        VideoEncoders { get; init; } =
            Array.Empty<LinuxVideoEncoderDevice>();

    public bool HasHardwareVideoDecoder =>
        VideoDecoders.Count != 0;

    public bool HasHardwareVideoEncoder =>
        VideoEncoders.Count != 0;
}

/// <summary>
/// Probes Linux kernel decode nodes and the native PipeWire client ABI without
/// loading a codec framework. Probe work is bounded to 64 V4L2 nodes and 256
/// coded formats per queue; it runs only during provider registration.
/// </summary>
public static unsafe class LinuxNativeMediaCapabilities
{
    private const int MaximumVideoNodes = 64;
    private const int MaximumFormatsPerQueue = 256;
    private const int OpenReadWrite = 0x0002;
    private const int OpenNonBlocking = 0x0800;

    private const uint VideoOutput = 2;
    private const uint VideoCapture = 1;
    private const uint VideoCaptureMultiPlanar = 9;
    private const uint VideoOutputMultiPlanar = 10;
    private const uint CapabilityVideoMemoryToMemory = 0x0000_4000;
    private const uint CapabilityVideoMemoryToMemoryMultiPlanar = 0x0000_8000;
    private const uint CapabilityStreaming = 0x0400_0000;
    private const uint CapabilityDeviceCaps = 0x8000_0000;

    // Linux UAPI _IOR/_IOWR values for v4l2_capability (104 bytes) and
    // v4l2_fmtdesc (64 bytes). Both structures use fixed-width UAPI fields.
    private const nuint VideoQueryCapabilities = 0x8068_5600;
    private const nuint VideoEnumerateFormat = 0xC040_5602;
    private const nuint VideoRequestBuffers = 0xC014_5608;
    private const uint MemoryDmaBuf = 4;

    private static readonly uint s_h264 = FourCc("H264");
    private static readonly uint s_h265 = FourCc("HEVC");
    private static readonly uint s_vp8 = FourCc("VP80");
    private static readonly uint s_vp9 = FourCc("VP90");
    private static readonly uint s_av1 = FourCc("AV01");
    private static readonly uint s_mpeg2 = FourCc("MPG2");
    private static readonly uint s_nv12 = FourCc("NV12");
    private static readonly uint s_nv12MultiPlanar = FourCc("NM12");
    private static readonly uint s_xrgb8888 = FourCc("XR24");
    private static readonly uint s_argb8888 = FourCc("AR24");
    private static readonly uint s_xbgr8888 = FourCc("XB24");
    private static readonly uint s_abgr8888 = FourCc("AB24");

    public static LinuxNativeMediaCapabilitySnapshot Probe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return new LinuxNativeMediaCapabilitySnapshot(
                Array.Empty<LinuxVideoDecoderDevice>(),
                PipeWireAvailable: false)
            {
                VideoEncoders =
                    Array.Empty<LinuxVideoEncoderDevice>()
            };
        }

        var decoders = new List<LinuxVideoDecoderDevice>();
        var encoders = new List<LinuxVideoEncoderDevice>();
        for (int index = 0;
             index < MaximumVideoNodes;
             index++)
        {
            string path =
                $"/dev/video{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (TryProbeDecoder(path, out LinuxVideoDecoderDevice device))
            {
                decoders.Add(device);
            }
            if (TryProbeEncoder(
                    path,
                    out LinuxVideoEncoderDevice encoder))
            {
                encoders.Add(encoder);
            }
        }

        return new LinuxNativeMediaCapabilitySnapshot(
            decoders.ToArray(),
            ProbeNativeLibrary(
                "libpipewire-0.3.so.0"))
        {
            VideoEncoders = encoders.ToArray()
        };
    }

    private static bool TryProbeDecoder(
        string path,
        out LinuxVideoDecoderDevice device)
    {
        int fileDescriptor =
            LinuxMediaNative.Open(
                path,
                OpenReadWrite | OpenNonBlocking,
                0);
        if (fileDescriptor < 0)
        {
            device = default;
            return false;
        }

        try
        {
            var capability = new V4l2Capability();
            if (LinuxMediaNative.Ioctl(
                    fileDescriptor,
                    VideoQueryCapabilities,
                    &capability) < 0)
            {
                device = default;
                return false;
            }

            uint effectiveCapabilities =
                (capability.Capabilities &
                 CapabilityDeviceCaps) != 0
                    ? capability.DeviceCapabilities
                    : capability.Capabilities;
            bool multiPlanar =
                (effectiveCapabilities &
                 CapabilityVideoMemoryToMemoryMultiPlanar) != 0;
            if (!multiPlanar &&
                (effectiveCapabilities &
                 CapabilityVideoMemoryToMemory) == 0)
            {
                device = default;
                return false;
            }

            LinuxHardwareVideoCodec codecs =
                EnumerateCodedFormats(
                    fileDescriptor,
                    multiPlanar
                        ? VideoOutputMultiPlanar
                        : VideoOutput);
            if (codecs == LinuxHardwareVideoCodec.None)
            {
                device = default;
                return false;
            }

            device = new LinuxVideoDecoderDevice(
                path,
                Utf8(capability.Driver, 16),
                Utf8(capability.Card, 32),
                codecs,
                multiPlanar,
                (effectiveCapabilities &
                 CapabilityStreaming) != 0);
            return true;
        }
        finally
        {
            LinuxMediaNative.Close(fileDescriptor);
        }
    }

    private static LinuxHardwareVideoCodec EnumerateCodedFormats(
        int fileDescriptor,
        uint queueType)
    {
        LinuxHardwareVideoCodec codecs =
            LinuxHardwareVideoCodec.None;
        for (uint index = 0;
             index < MaximumFormatsPerQueue;
             index++)
        {
            var format = new V4l2FormatDescription
            {
                Index = index,
                Type = queueType
            };
            if (LinuxMediaNative.Ioctl(
                    fileDescriptor,
                    VideoEnumerateFormat,
                    &format) < 0)
            {
                break;
            }

            codecs |= format.PixelFormat switch
            {
                var value when value == s_h264 =>
                    LinuxHardwareVideoCodec.H264,
                var value when value == s_h265 =>
                    LinuxHardwareVideoCodec.H265,
                var value when value == s_vp8 =>
                    LinuxHardwareVideoCodec.Vp8,
                var value when value == s_vp9 =>
                    LinuxHardwareVideoCodec.Vp9,
                var value when value == s_av1 =>
                    LinuxHardwareVideoCodec.Av1,
                var value when value == s_mpeg2 =>
                    LinuxHardwareVideoCodec.Mpeg2,
                _ => LinuxHardwareVideoCodec.None
            };
        }
        return codecs;
    }

    private static bool TryProbeEncoder(
        string path,
        out LinuxVideoEncoderDevice device)
    {
        int fileDescriptor =
            LinuxMediaNative.Open(
                path,
                OpenReadWrite | OpenNonBlocking,
                0);
        if (fileDescriptor < 0)
        {
            device = default;
            return false;
        }

        try
        {
            var capability = new V4l2Capability();
            if (LinuxMediaNative.Ioctl(
                    fileDescriptor,
                    VideoQueryCapabilities,
                    &capability) < 0)
            {
                device = default;
                return false;
            }

            uint effectiveCapabilities =
                (capability.Capabilities &
                 CapabilityDeviceCaps) != 0
                    ? capability.DeviceCapabilities
                    : capability.Capabilities;
            bool multiPlanar =
                (effectiveCapabilities &
                 CapabilityVideoMemoryToMemoryMultiPlanar) != 0;
            if (!multiPlanar &&
                (effectiveCapabilities &
                 CapabilityVideoMemoryToMemory) == 0)
            {
                device = default;
                return false;
            }

            uint codedQueue = multiPlanar
                ? VideoCaptureMultiPlanar
                : VideoCapture;
            uint rawQueue = multiPlanar
                ? VideoOutputMultiPlanar
                : VideoOutput;
            LinuxHardwareVideoCodec codecs =
                EnumerateCodedFormats(
                    fileDescriptor,
                    codedQueue);
            LinuxRawVideoFormat inputFormats =
                EnumerateRawFormats(
                    fileDescriptor,
                    rawQueue);
            if (codecs == LinuxHardwareVideoCodec.None ||
                inputFormats == LinuxRawVideoFormat.None)
            {
                device = default;
                return false;
            }

            device = new LinuxVideoEncoderDevice(
                path,
                Utf8(capability.Driver, 16),
                Utf8(capability.Card, 32),
                codecs,
                inputFormats,
                multiPlanar,
                (effectiveCapabilities &
                 CapabilityStreaming) != 0,
                ProbeDmaBufInput(
                    fileDescriptor,
                    rawQueue));
            return true;
        }
        finally
        {
            LinuxMediaNative.Close(fileDescriptor);
        }
    }

    private static LinuxRawVideoFormat EnumerateRawFormats(
        int fileDescriptor,
        uint queueType)
    {
        LinuxRawVideoFormat formats =
            LinuxRawVideoFormat.None;
        for (uint index = 0;
             index < MaximumFormatsPerQueue;
             index++)
        {
            var format = new V4l2FormatDescription
            {
                Index = index,
                Type = queueType
            };
            if (LinuxMediaNative.Ioctl(
                    fileDescriptor,
                    VideoEnumerateFormat,
                    &format) < 0)
            {
                break;
            }

            formats |= format.PixelFormat switch
            {
                var value when value == s_nv12 =>
                    LinuxRawVideoFormat.Nv12,
                var value when value == s_nv12MultiPlanar =>
                    LinuxRawVideoFormat.Nv12MultiPlanar,
                var value when value == s_xrgb8888 =>
                    LinuxRawVideoFormat.Xrgb8888,
                var value when value == s_argb8888 =>
                    LinuxRawVideoFormat.Argb8888,
                var value when value == s_xbgr8888 =>
                    LinuxRawVideoFormat.Xbgr8888,
                var value when value == s_abgr8888 =>
                    LinuxRawVideoFormat.Abgr8888,
                _ => LinuxRawVideoFormat.None
            };
        }
        return formats;
    }

    private static bool ProbeDmaBufInput(
        int fileDescriptor,
        uint queueType)
    {
        var request = new V4l2RequestBuffers
        {
            Count = 0,
            Type = queueType,
            Memory = MemoryDmaBuf
        };
        return LinuxMediaNative.Ioctl(
            fileDescriptor,
            VideoRequestBuffers,
            &request) >= 0;
    }

    private static bool ProbeNativeLibrary(string name)
    {
        if (!NativeLibrary.TryLoad(name, out nint handle))
        {
            return false;
        }
        NativeLibrary.Free(handle);
        return true;
    }

    private static uint FourCc(string value)
    {
        if (value.Length != 4)
        {
            throw new ArgumentException(
                "A V4L2 FourCC must contain four ASCII characters.",
                nameof(value));
        }
        return (uint)value[0] |
               ((uint)value[1] << 8) |
               ((uint)value[2] << 16) |
               ((uint)value[3] << 24);
    }

    private static string Utf8(byte* value, int length)
    {
        int count = 0;
        while (count < length && value[count] != 0)
        {
            count++;
        }
        return Encoding.UTF8.GetString(
            new ReadOnlySpan<byte>(value, count));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct V4l2Capability
    {
        public fixed byte Driver[16];
        public fixed byte Card[32];
        public fixed byte BusInformation[32];
        public uint Version;
        public uint Capabilities;
        public uint DeviceCapabilities;
        public fixed uint Reserved[3];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct V4l2FormatDescription
    {
        public uint Index;
        public uint Type;
        public uint Flags;
        public fixed byte Description[32];
        public uint PixelFormat;
        public uint MediaBusCode;
        public fixed uint Reserved[3];
    }
}

internal static unsafe partial class LinuxMediaNative
{
    [LibraryImport(
        "libc",
        EntryPoint = "open",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(
        string path,
        int flags,
        int mode);

    [LibraryImport(
        "libc",
        EntryPoint = "close",
        SetLastError = true)]
    internal static partial int Close(int fileDescriptor);

    [LibraryImport(
        "libc",
        EntryPoint = "ioctl",
        SetLastError = true)]
    internal static partial int Ioctl(
        int fileDescriptor,
        nuint request,
        void* argument);
}
