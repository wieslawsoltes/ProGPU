using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Media.Audio;

namespace ProGPU.Windows.Media;

internal static unsafe partial class WindowsMediaNative
{
    internal const uint FirstAudioStream = 0xffff_fffd;
    internal const uint FirstVideoStream = 0xffff_fffc;
    internal const uint SourceReaderEndOfStream = 0x2;
    internal const uint SourceReaderStreamTick = 0x100;

    private const uint AllStreams = 0xffff_fffe;
    private const uint VideoInterlaceProgressive = 2;
    private const ushort VariantTypeInt64 = 20;
    private const uint AacLcProfileLevel2 = 0x29;

    private static readonly Guid s_emptyGuid = Guid.Empty;
    private static readonly Guid s_sourceReaderD3dManager =
        new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    private static readonly Guid s_sinkWriterD3dManager =
        new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    private static readonly Guid s_enableHardwareTransforms =
        new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid s_enableAdvancedVideoProcessing =
        new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    private static readonly Guid s_enableTranscodeOnlyTransforms =
        new("dfd4f008-b5fd-4e78-ae44-62a1e67bbe27");
    private static readonly Guid s_disableSinkThrottling =
        new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    private static readonly Guid s_sampleDiscontinuity =
        new("9cdf01d9-a0f0-43ba-b077-eaa06cbd728a");

    private static readonly Guid s_majorType =
        new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid s_subtype =
        new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid s_mediaTypeVideo =
        new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid s_mediaTypeAudio =
        new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid s_videoFormatNv12 =
        new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid s_videoFormatArgb32 =
        new("00000015-0000-0010-8000-00aa00389b71");
    private static readonly Guid s_videoFormatH264 =
        new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid s_mediaSample =
        new("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4");
    private static readonly Guid s_dxgiBuffer =
        new("e7174cfa-1c9e-48b1-8866-626226bfc258");
    private static readonly Guid s_d3d11Texture2D =
        new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid s_audioFormatPcm =
        new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid s_audioFormatAac =
        new("00001610-0000-0010-8000-00aa00389b71");

    private static readonly Guid s_frameSize =
        new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid s_frameRate =
        new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid s_pixelAspectRatio =
        new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid s_interlaceMode =
        new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid s_averageBitrate =
        new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid s_audioChannels =
        new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid s_audioSamplesPerSecond =
        new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid s_audioAverageBytesPerSecond =
        new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid s_audioBlockAlignment =
        new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid s_audioBitsPerSample =
        new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid s_aacProfileLevel =
        new("7632f0e6-9538-4d61-acda-ea29c8c14456");

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        internal ushort VariantType;

        [FieldOffset(8)]
        internal long Int64;

        [FieldOffset(8)]
        internal nint Pointer;
    }

    internal static nint CreateTranscodeSourceReader(
        string sourceUrl,
        nint dxgiManager)
    {
        nint attributes = CreateAttributes(4);
        try
        {
            SetAttributeUnknown(
                attributes,
                in s_sourceReaderD3dManager,
                dxgiManager);
            SetAttributeUInt32(
                attributes,
                in s_enableHardwareTransforms,
                1);
            SetAttributeUInt32(
                attributes,
                in s_enableAdvancedVideoProcessing,
                1);
            SetAttributeUInt32(
                attributes,
                in s_enableTranscodeOnlyTransforms,
                1);

            nint reader = 0;
            ThrowIfFailed(
                MFCreateSourceReaderFromURL(
                    sourceUrl,
                    attributes,
                    &reader),
                "create the Media Foundation source reader");
            SetSourceReaderStreamSelection(
                reader,
                AllStreams,
                selected: false);
            return reader;
        }
        finally
        {
            Release(attributes);
        }
    }

    internal static nint CreateTranscodeSinkWriter(
        string destinationPath,
        nint dxgiManager)
    {
        nint attributes = CreateAttributes(3);
        try
        {
            SetAttributeUnknown(
                attributes,
                in s_sinkWriterD3dManager,
                dxgiManager);
            SetAttributeUInt32(
                attributes,
                in s_enableHardwareTransforms,
                1);
            SetAttributeUInt32(
                attributes,
                in s_disableSinkThrottling,
                1);

            nint writer = 0;
            ThrowIfFailed(
                MFCreateSinkWriterFromURL(
                    destinationPath,
                    0,
                    attributes,
                    &writer),
                "create the Media Foundation sink writer");
            return writer;
        }
        finally
        {
            Release(attributes);
        }
    }

    internal static nint CreateH264VideoType(
        uint width,
        uint height,
        uint bitrate,
        uint frameRateNumerator,
        uint frameRateDenominator)
    {
        nint mediaType = CreateVideoType(
            in s_videoFormatH264,
            width,
            height,
            frameRateNumerator,
            frameRateDenominator);
        try
        {
            SetAttributeUInt32(
                mediaType,
                in s_averageBitrate,
                bitrate);
            return mediaType;
        }
        catch
        {
            Release(mediaType);
            throw;
        }
    }

    internal static nint CreateNv12VideoType(
        uint width,
        uint height,
        uint frameRateNumerator,
        uint frameRateDenominator) =>
        CreateVideoType(
            in s_videoFormatNv12,
            width,
            height,
            frameRateNumerator,
            frameRateDenominator);

    internal static nint CreateArgb32VideoType(
        uint width,
        uint height,
        uint frameRateNumerator,
        uint frameRateDenominator) =>
        CreateVideoType(
            in s_videoFormatArgb32,
            width,
            height,
            frameRateNumerator,
            frameRateDenominator);

    internal static nint CreateAacAudioType(
        uint channelCount,
        uint sampleRate,
        uint bitrate)
    {
        nint mediaType = CreateMediaType();
        try
        {
            SetAttributeGuid(
                mediaType,
                in s_majorType,
                in s_mediaTypeAudio);
            SetAttributeGuid(
                mediaType,
                in s_subtype,
                in s_audioFormatAac);
            SetAttributeUInt32(
                mediaType,
                in s_audioChannels,
                channelCount);
            SetAttributeUInt32(
                mediaType,
                in s_audioSamplesPerSecond,
                sampleRate);
            SetAttributeUInt32(
                mediaType,
                in s_audioAverageBytesPerSecond,
                Math.Max(1, bitrate / 8));
            SetAttributeUInt32(
                mediaType,
                in s_audioBitsPerSample,
                16);
            SetAttributeUInt32(
                mediaType,
                in s_aacProfileLevel,
                AacLcProfileLevel2);
            return mediaType;
        }
        catch
        {
            Release(mediaType);
            throw;
        }
    }

    internal static nint CreatePcmAudioType(
        uint channelCount,
        uint sampleRate)
    {
        nint mediaType = CreateMediaType();
        try
        {
            uint blockAlignment =
                checked(channelCount * 2);
            SetAttributeGuid(
                mediaType,
                in s_majorType,
                in s_mediaTypeAudio);
            SetAttributeGuid(
                mediaType,
                in s_subtype,
                in s_audioFormatPcm);
            SetAttributeUInt32(
                mediaType,
                in s_audioChannels,
                channelCount);
            SetAttributeUInt32(
                mediaType,
                in s_audioSamplesPerSecond,
                sampleRate);
            SetAttributeUInt32(
                mediaType,
                in s_audioBitsPerSample,
                16);
            SetAttributeUInt32(
                mediaType,
                in s_audioBlockAlignment,
                blockAlignment);
            SetAttributeUInt32(
                mediaType,
                in s_audioAverageBytesPerSecond,
                checked(sampleRate * blockAlignment));
            return mediaType;
        }
        catch
        {
            Release(mediaType);
            throw;
        }
    }

    /// <summary>
    /// Creates one MF-owned aligned PCM16 sample from a wide mix accumulator.
    /// Saturation is applied exactly once while writing the native buffer.
    /// </summary>
    internal static nint CreatePcm16Sample(
        ReadOnlySpan<long> accumulator)
    {
        if (accumulator.IsEmpty)
        {
            throw new ArgumentException(
                "A PCM16 sample must contain at least one value.",
                nameof(accumulator));
        }
        uint byteLength =
            checked((uint)accumulator.Length * 2);
        nint sample = 0;
        nint buffer = 0;
        byte* bytes = null;
        bool locked = false;
        try
        {
            ThrowIfFailed(
                MFCreateSample(&sample),
                "create a PCM16 Media Foundation sample");
            ThrowIfFailed(
                MFCreateAlignedMemoryBuffer(
                    byteLength,
                    31,
                    &buffer),
                "create a 32-byte-aligned PCM16 media buffer");

            delegate* unmanaged[Stdcall]<
                nint,
                byte**,
                uint*,
                uint*,
                int> lockBuffer =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    byte**,
                    uint*,
                    uint*,
                    int>)VTable(buffer)[3];
            uint maximumLength = 0;
            ThrowIfFailed(
                lockBuffer(
                    buffer,
                    &bytes,
                    &maximumLength,
                    null),
                "lock an output PCM16 media buffer");
            locked = true;
            if (maximumLength < byteLength)
            {
                throw new InvalidDataException(
                    "Media Foundation returned an undersized PCM16 buffer.");
            }
            WindowsPcm16Mixer.WriteSaturated(
                accumulator,
                new Span<short>(
                    bytes,
                    accumulator.Length));

            delegate* unmanaged[Stdcall]<
                nint,
                uint,
                int> setCurrentLength =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    uint,
                    int>)VTable(buffer)[6];
            ThrowIfFailed(
                setCurrentLength(
                    buffer,
                    byteLength),
                "set the output PCM16 media buffer length");

            delegate* unmanaged[Stdcall]<
                nint,
                nint,
                int> addBuffer =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    nint,
                    int>)VTable(sample)[42];
            ThrowIfFailed(
                addBuffer(sample, buffer),
                "add an output PCM16 buffer to its sample");
            nint result = sample;
            sample = 0;
            return result;
        }
        finally
        {
            if (locked)
            {
                delegate* unmanaged[Stdcall]<
                    nint,
                    int> unlockBuffer =
                    (delegate* unmanaged[Stdcall]<
                        nint,
                        int>)VTable(buffer)[4];
                ThrowIfFailed(
                    unlockBuffer(buffer),
                    "unlock an output PCM16 media buffer");
            }
            Release(buffer);
            Release(sample);
        }
    }

    internal static int GetPcm16SampleFrameCount(
        nint sample,
        uint channelCount)
    {
        if (channelCount is not (1u or 2u))
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelCount));
        }
        uint bufferCount = 0;
        delegate* unmanaged[Stdcall]<
            nint,
            uint*,
            int> getBufferCount =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint*,
                int>)VTable(sample)[39];
        ThrowIfFailed(
            getBufferCount(sample, &bufferCount),
            "get the PCM16 sample buffer count");
        delegate* unmanaged[Stdcall]<
            nint,
            uint,
            nint*,
            int> getBuffer =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                nint*,
                int>)VTable(sample)[40];
        ulong totalBytes = 0;
        for (uint index = 0;
             index < bufferCount;
             index++)
        {
            nint buffer = 0;
            try
            {
                ThrowIfFailed(
                    getBuffer(
                        sample,
                        index,
                        &buffer),
                    "get a PCM16 media buffer");
                uint currentLength = 0;
                delegate* unmanaged[Stdcall]<
                    nint,
                    uint*,
                    int> getCurrentLength =
                    (delegate* unmanaged[Stdcall]<
                        nint,
                        uint*,
                        int>)VTable(buffer)[5];
                ThrowIfFailed(
                    getCurrentLength(
                        buffer,
                        &currentLength),
                    "get a PCM16 media buffer length");
                totalBytes =
                    checked(
                        totalBytes +
                        currentLength);
            }
            finally
            {
                Release(buffer);
            }
        }
        ulong bytesPerFrame =
            checked(channelCount * 2u);
        if (totalBytes == 0 ||
            totalBytes % bytesPerFrame != 0 ||
            totalBytes / bytesPerFrame >
                int.MaxValue)
        {
            throw new InvalidDataException(
                "A PCM16 sample must contain complete interleaved frames.");
        }
        return checked(
            (int)(totalBytes / bytesPerFrame));
    }

    internal static void MixPcm16Sample(
        nint sample,
        int sourceFrameOffset,
        int frameCount,
        uint channelCount,
        in WindowsPcm16MixLevels levels,
        Span<long> destination,
        int destinationFrameOffset)
    {
        if (sourceFrameOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceFrameOffset));
        }
        if (frameCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameCount));
        }
        if (channelCount is not (1u or 2u))
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelCount));
        }
        int channels = checked((int)channelCount);
        int sourceSampleOffset =
            checked(sourceFrameOffset * channels);
        int requestedSamples =
            checked(frameCount * channels);
        int destinationSampleOffset =
            checked(destinationFrameOffset * channels);
        if (destinationSampleOffset < 0 ||
            destinationSampleOffset >
                destination.Length ||
            requestedSamples >
                destination.Length -
                destinationSampleOffset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destinationFrameOffset));
        }
        if (requestedSamples == 0 ||
            levels.Left == 0 &&
            levels.Right == 0)
        {
            return;
        }

        uint bufferCount = 0;
        delegate* unmanaged[Stdcall]<
            nint,
            uint*,
            int> getBufferCount =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint*,
                int>)VTable(sample)[39];
        ThrowIfFailed(
            getBufferCount(sample, &bufferCount),
            "get the PCM16 sample buffer count");
        delegate* unmanaged[Stdcall]<
            nint,
            uint,
            nint*,
            int> getBuffer =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                nint*,
                int>)VTable(sample)[40];
        int globalSampleOffset = 0;
        int copiedSamples = 0;
        for (uint index = 0;
             index < bufferCount &&
             copiedSamples < requestedSamples;
             index++)
        {
            nint buffer = 0;
            byte* bytes = null;
            bool locked = false;
            try
            {
                ThrowIfFailed(
                    getBuffer(
                        sample,
                        index,
                        &buffer),
                    "get a PCM16 media buffer");
                uint currentLength = 0;
                delegate* unmanaged[Stdcall]<
                    nint,
                    byte**,
                    uint*,
                    uint*,
                    int> lockBuffer =
                    (delegate* unmanaged[Stdcall]<
                        nint,
                        byte**,
                        uint*,
                        uint*,
                        int>)VTable(buffer)[3];
                ThrowIfFailed(
                    lockBuffer(
                        buffer,
                        &bytes,
                        null,
                        &currentLength),
                    "lock a PCM16 media buffer for mixing");
                locked = true;
                if ((currentLength & 1) != 0)
                {
                    throw new InvalidDataException(
                        "A PCM16 media buffer has an odd byte length.");
                }
                int bufferSamples =
                    checked(
                        (int)(currentLength / 2));
                int localStart =
                    Math.Max(
                        0,
                        sourceSampleOffset -
                        globalSampleOffset);
                int available =
                    Math.Max(
                        0,
                        bufferSamples -
                        localStart);
                int take =
                    Math.Min(
                        available,
                        requestedSamples -
                        copiedSamples);
                var sourceValues =
                    new ReadOnlySpan<short>(
                        bytes,
                        bufferSamples);
                for (int sampleIndex = 0;
                     sampleIndex < take;
                     sampleIndex++)
                {
                    int sourceIndex =
                        localStart + sampleIndex;
                    int absoluteIndex =
                        sourceSampleOffset +
                        copiedSamples +
                        sampleIndex;
                    int fixedLevel =
                        channels == 1
                            ? Math.Max(
                                levels.Left,
                                levels.Right)
                            : (absoluteIndex & 1) == 0
                                ? levels.Left
                                : levels.Right;
                    destination[
                        destinationSampleOffset +
                        copiedSamples +
                        sampleIndex] +=
                        (long)sourceValues[sourceIndex] *
                        fixedLevel /
                        32_768;
                }
                copiedSamples += take;
                globalSampleOffset =
                    checked(
                        globalSampleOffset +
                        bufferSamples);
            }
            finally
            {
                if (locked)
                {
                    delegate* unmanaged[Stdcall]<
                        nint,
                        int> unlockBuffer =
                        (delegate* unmanaged[Stdcall]<
                            nint,
                            int>)VTable(buffer)[4];
                    ThrowIfFailed(
                        unlockBuffer(buffer),
                        "unlock a PCM16 media buffer after mixing");
                }
                Release(buffer);
            }
        }
        if (copiedSamples != requestedSamples)
        {
            throw new InvalidDataException(
                "A PCM16 sample ended before the requested frame range.");
        }
    }

    /// <summary>
    /// Copies one frame interval from MF-owned PCM16 buffers into normalized
    /// caller-owned float storage for an explicitly registered typed effect.
    /// Buffers are borrowed only for the duration of each direct span copy.
    /// </summary>
    internal static void CopyPcm16SampleToFloat(
        nint sample,
        int sourceFrameOffset,
        int frameCount,
        uint channelCount,
        Span<float> destination)
    {
        if (sourceFrameOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceFrameOffset));
        }
        if (frameCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameCount));
        }
        if (channelCount is not (1u or 2u))
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelCount));
        }
        int channels = checked((int)channelCount);
        int sourceSampleOffset =
            checked(sourceFrameOffset * channels);
        int requestedSamples =
            checked(frameCount * channels);
        if (destination.Length < requestedSamples)
        {
            throw new ArgumentException(
                "The float destination is smaller than the requested PCM interval.",
                nameof(destination));
        }
        if (requestedSamples == 0)
        {
            return;
        }

        uint bufferCount = 0;
        delegate* unmanaged[Stdcall]<
            nint,
            uint*,
            int> getBufferCount =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint*,
                int>)VTable(sample)[39];
        ThrowIfFailed(
            getBufferCount(sample, &bufferCount),
            "get the PCM16 sample buffer count");
        delegate* unmanaged[Stdcall]<
            nint,
            uint,
            nint*,
            int> getBuffer =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                nint*,
                int>)VTable(sample)[40];
        int globalSampleOffset = 0;
        int copiedSamples = 0;
        for (uint index = 0;
             index < bufferCount &&
             copiedSamples < requestedSamples;
             index++)
        {
            nint buffer = 0;
            byte* bytes = null;
            bool locked = false;
            try
            {
                ThrowIfFailed(
                    getBuffer(
                        sample,
                        index,
                        &buffer),
                    "get a PCM16 media buffer");
                uint currentLength = 0;
                delegate* unmanaged[Stdcall]<
                    nint,
                    byte**,
                    uint*,
                    uint*,
                    int> lockBuffer =
                    (delegate* unmanaged[Stdcall]<
                        nint,
                        byte**,
                        uint*,
                        uint*,
                        int>)VTable(buffer)[3];
                ThrowIfFailed(
                    lockBuffer(
                        buffer,
                        &bytes,
                        null,
                        &currentLength),
                    "lock a PCM16 media buffer for typed processing");
                locked = true;
                if ((currentLength & 1) != 0)
                {
                    throw new InvalidDataException(
                        "A PCM16 media buffer has an odd byte length.");
                }
                int bufferSamples =
                    checked(
                        (int)(currentLength / 2));
                int localStart =
                    Math.Max(
                        0,
                        sourceSampleOffset -
                        globalSampleOffset);
                int available =
                    Math.Max(
                        0,
                        bufferSamples -
                        localStart);
                int take =
                    Math.Min(
                        available,
                        requestedSamples -
                        copiedSamples);
                var sourceValues =
                    new ReadOnlySpan<short>(
                        bytes,
                        bufferSamples);
                MediaPcm16FloatConverter
                    .ConvertToNormalizedFloat(
                        sourceValues.Slice(
                            localStart,
                            take),
                        destination.Slice(
                            copiedSamples,
                            take));
                copiedSamples += take;
                globalSampleOffset =
                    checked(
                        globalSampleOffset +
                        bufferSamples);
            }
            finally
            {
                if (locked)
                {
                    delegate* unmanaged[Stdcall]<
                        nint,
                        int> unlockBuffer =
                        (delegate* unmanaged[Stdcall]<
                            nint,
                            int>)VTable(buffer)[4];
                    ThrowIfFailed(
                        unlockBuffer(buffer),
                        "unlock a PCM16 media buffer after typed processing");
                }
                Release(buffer);
            }
        }
        if (copiedSamples != requestedSamples)
        {
            throw new InvalidDataException(
                "A PCM16 sample ended before the requested frame range.");
        }
    }

    internal static void ConfigureSourceReaderStream(
        nint reader,
        uint stream,
        nint mediaType)
    {
        SetSourceReaderStreamSelection(
            reader,
            stream,
            selected: true);
        delegate* unmanaged[Stdcall]<
            nint,
            uint,
            uint*,
            nint,
            int> setCurrentType =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                uint*,
                nint,
                int>)VTable(reader)[7];
        ThrowIfFailed(
            setCurrentType(
                reader,
                stream,
                null,
                mediaType),
            "set the source-reader output media type");
    }

    internal static void SetSourceReaderPosition(
        nint reader,
        long position)
    {
        var value = new PropVariant
        {
            VariantType = VariantTypeInt64,
            Int64 = position
        };
        fixed (Guid* timeFormat = &s_emptyGuid)
        {
            delegate* unmanaged[Stdcall]<
                nint,
                Guid*,
                PropVariant*,
                int> setPosition =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    Guid*,
                    PropVariant*,
                    int>)VTable(reader)[8];
            ThrowIfFailed(
                setPosition(
                    reader,
                    timeFormat,
                    &value),
                "seek the Media Foundation source reader");
        }
    }

    internal static nint ReadSourceSample(
        nint reader,
        uint stream,
        out uint flags,
        out long timestamp)
    {
        uint actualStream = 0;
        uint nativeFlags = 0;
        long nativeTimestamp = 0;
        nint sample = 0;
        delegate* unmanaged[Stdcall]<
            nint,
            uint,
            uint,
            uint*,
            uint*,
            long*,
            nint*,
            int> read =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                uint,
                uint*,
                uint*,
                long*,
                nint*,
                int>)VTable(reader)[9];
        ThrowIfFailed(
            read(
                reader,
                stream,
                0,
                &actualStream,
                &nativeFlags,
                &nativeTimestamp,
                &sample),
            "read a Media Foundation source sample");
        flags = nativeFlags;
        timestamp = nativeTimestamp;
        return sample;
    }

    internal static uint AddSinkWriterStream(
        nint writer,
        nint outputType)
    {
        uint stream = 0;
        delegate* unmanaged[Stdcall]<
            nint,
            nint,
            uint*,
            int> addStream =
            (delegate* unmanaged[Stdcall]<
                nint,
                nint,
                uint*,
                int>)VTable(writer)[3];
        ThrowIfFailed(
            addStream(writer, outputType, &stream),
            "add a Media Foundation sink-writer stream");
        return stream;
    }

    internal static void SetSinkWriterInputType(
        nint writer,
        uint stream,
        nint inputType)
    {
        delegate* unmanaged[Stdcall]<
            nint,
            uint,
            nint,
            nint,
            int> setInput =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                nint,
                nint,
                int>)VTable(writer)[4];
        ThrowIfFailed(
            setInput(
                writer,
                stream,
                inputType,
                0),
            "set a Media Foundation sink-writer input type");
    }

    internal static void BeginSinkWriter(nint writer) =>
        CallResult(
            writer,
            5,
            "begin Media Foundation sink writing");

    internal static void WriteSinkSample(
        nint writer,
        uint stream,
        nint sample)
    {
        delegate* unmanaged[Stdcall]<
            nint,
            uint,
            nint,
            int> write =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                nint,
                int>)VTable(writer)[6];
        ThrowIfFailed(
            write(writer, stream, sample),
            "write a Media Foundation sink sample");
    }

    internal static void SendSinkStreamTick(
        nint writer,
        uint stream,
        long timestamp)
    {
        delegate* unmanaged[Stdcall]<
            nint,
            uint,
            long,
            int> send =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                long,
                int>)VTable(writer)[7];
        ThrowIfFailed(
            send(writer, stream, timestamp),
            "write a Media Foundation stream tick");
    }

    internal static void FinalizeSinkWriter(nint writer) =>
        CallResult(
            writer,
            11,
            "finalize Media Foundation sink writing");

    internal static void SetSampleTime(
        nint sample,
        long timestamp)
    {
        delegate* unmanaged[Stdcall]<
            nint,
            long,
            int> set =
            (delegate* unmanaged[Stdcall]<
                nint,
                long,
                int>)VTable(sample)[36];
        ThrowIfFailed(
            set(sample, timestamp),
            "set a Media Foundation sample timestamp");
    }

    internal static void SetSampleDiscontinuity(
        nint sample) =>
        SetAttributeUInt32(
            sample,
            in s_sampleDiscontinuity,
            1);

    internal static bool TryGetSampleDuration(
        nint sample,
        out long duration)
    {
        long nativeDuration = 0;
        delegate* unmanaged[Stdcall]<
            nint,
            long*,
            int> get =
            (delegate* unmanaged[Stdcall]<
                nint,
                long*,
                int>)VTable(sample)[37];
        int result = get(sample, &nativeDuration);
        duration = nativeDuration;
        return result >= 0;
    }

    internal static void SetSampleDuration(
        nint sample,
        long duration)
    {
        delegate* unmanaged[Stdcall]<
            nint,
            long,
            int> set =
            (delegate* unmanaged[Stdcall]<
                nint,
                long,
                int>)VTable(sample)[38];
        ThrowIfFailed(
            set(sample, duration),
            "set a Media Foundation sample duration");
    }

    /// <summary>
    /// Applies in-place PCM16 gain to every native buffer in an audio
    /// sample without joining buffers or allocating managed scratch storage.
    /// </summary>
    internal static void ApplyPcm16Gain(
        nint sample,
        double gain)
    {
        float value = checked((float)gain);
        ApplyPcm16StereoLevels(
            sample,
            channelCount: 1,
            new MediaAudioStereoLevels(
                value,
                value));
    }

    /// <summary>
    /// Applies in-place PCM16 gain and stereo balance to every native buffer
    /// in an audio sample. Channel phase is carried across buffer boundaries,
    /// and no managed scratch storage is allocated.
    /// </summary>
    internal static void ApplyPcm16StereoLevels(
        nint sample,
        uint channelCount,
        in MediaAudioStereoLevels levels)
    {
        if (levels == MediaAudioStereoLevels.Identity)
        {
            return;
        }

        uint bufferCount = 0;
        delegate* unmanaged[Stdcall]<
            nint,
            uint*,
            int> getBufferCount =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint*,
                int>)VTable(sample)[39];
        ThrowIfFailed(
            getBufferCount(
                sample,
                &bufferCount),
            "get the PCM sample buffer count");

        delegate* unmanaged[Stdcall]<
            nint,
            uint,
            nint*,
            int> getBuffer =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                nint*,
                int>)VTable(sample)[40];
        int channelOffset = 0;
        for (uint index = 0;
             index < bufferCount;
             index++)
        {
            nint buffer = 0;
            byte* bytes = null;
            bool locked = false;
            try
            {
                ThrowIfFailed(
                    getBuffer(
                        sample,
                        index,
                        &buffer),
                    "get a PCM media buffer");
                uint currentLength = 0;
                delegate* unmanaged[Stdcall]<
                    nint,
                    byte**,
                    uint*,
                    uint*,
                    int> lockBuffer =
                    (delegate* unmanaged[Stdcall]<
                        nint,
                        byte**,
                        uint*,
                        uint*,
                        int>)VTable(buffer)[3];
                ThrowIfFailed(
                    lockBuffer(
                        buffer,
                        &bytes,
                        null,
                        &currentLength),
                    "lock a PCM media buffer");
                locked = true;
                if ((currentLength & 1) != 0)
                {
                    throw new InvalidDataException(
                        "A PCM16 media buffer has an odd byte length.");
                }
                WindowsPcm16GainProcessor.ApplyStereo(
                    new Span<short>(
                        bytes,
                        checked(
                            (int)(currentLength / 2))),
                    channelCount,
                    levels,
                    ref channelOffset);
            }
            finally
            {
                if (locked)
                {
                    delegate* unmanaged[Stdcall]<
                        nint,
                        int> unlockBuffer =
                        (delegate* unmanaged[Stdcall]<
                            nint,
                            int>)VTable(buffer)[4];
                    ThrowIfFailed(
                        unlockBuffer(buffer),
                        "unlock a PCM media buffer");
                }
                Release(buffer);
            }
        }
        if (channelOffset != 0)
        {
            throw new InvalidDataException(
                "A PCM16 media sample ended inside an interleaved audio frame.");
        }
    }

    /// <summary>
    /// Borrows the D3D11 texture carried by one DXGI-backed media sample.
    /// The returned COM reference is caller-owned.
    /// </summary>
    internal static nint GetSampleD3D11Texture(
        nint sample)
    {
        nint buffer = 0;
        nint dxgiBuffer = 0;
        try
        {
            delegate* unmanaged[Stdcall]<
                nint,
                uint,
                nint*,
                int> getBuffer =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    uint,
                    nint*,
                    int>)VTable(sample)[40];
            ThrowIfFailed(
                getBuffer(sample, 0, &buffer),
                "get the DXGI media-sample buffer");
            dxgiBuffer = QueryInterface(
                buffer,
                in s_dxgiBuffer);

            nint texture = 0;
            fixed (Guid* textureId = &s_d3d11Texture2D)
            {
                delegate* unmanaged[Stdcall]<
                    nint,
                    Guid*,
                    nint*,
                    int> getResource =
                    (delegate* unmanaged[Stdcall]<
                        nint,
                        Guid*,
                        nint*,
                        int>)VTable(dxgiBuffer)[3];
                ThrowIfFailed(
                    getResource(
                        dxgiBuffer,
                        textureId,
                        &texture),
                    "get the D3D11 texture from the DXGI buffer");
            }
            return texture;
        }
        finally
        {
            Release(dxgiBuffer);
            Release(buffer);
        }
    }

    internal static void CopyD3D11Texture(
        nint immediateContext,
        nint destination,
        nint source)
    {
        delegate* unmanaged[Stdcall]<
            nint,
            nint,
            nint,
            void> copy =
            (delegate* unmanaged[Stdcall]<
                nint,
                nint,
                nint,
                void>)VTable(immediateContext)[47];
        copy(
            immediateContext,
            destination,
            source);
    }

    /// <summary>
    /// Creates a tracked Media Foundation sample around one caller-owned
    /// D3D11 texture. The sink-writer callback is invoked only after all
    /// downstream references have been released.
    /// </summary>
    internal static nint CreateTrackedDxgiSample(
        nint texture,
        long timestamp,
        long duration,
        nint callback)
    {
        nint tracked = 0;
        nint sample = 0;
        nint buffer = 0;
        try
        {
            ThrowIfFailed(
                MFCreateTrackedSample(&tracked),
                "create a tracked Media Foundation sample");
            sample = QueryInterface(
                tracked,
                in s_mediaSample);
            ThrowIfFailed(
                MFCreateDXGISurfaceBuffer(
                    in s_d3d11Texture2D,
                    texture,
                    0,
                    false,
                    &buffer),
                "wrap the encoder D3D11 texture");
            delegate* unmanaged[Stdcall]<
                nint,
                nint,
                int> addBuffer =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    nint,
                    int>)VTable(sample)[42];
            ThrowIfFailed(
                addBuffer(sample, buffer),
                "attach the encoder DXGI buffer");
            SetSampleTime(sample, timestamp);
            SetSampleDuration(sample, duration);

            delegate* unmanaged[Stdcall]<
                nint,
                nint,
                nint,
                int> setAllocator =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    nint,
                    nint,
                    int>)VTable(tracked)[3];
            ThrowIfFailed(
                setAllocator(
                    tracked,
                    callback,
                    0),
                "track sink-writer ownership of the DXGI sample");
            return sample;
        }
        catch
        {
            Release(sample);
            throw;
        }
        finally
        {
            Release(buffer);
            Release(tracked);
        }
    }

    private static nint CreateVideoType(
        in Guid subtype,
        uint width,
        uint height,
        uint frameRateNumerator,
        uint frameRateDenominator)
    {
        nint mediaType = CreateMediaType();
        try
        {
            SetAttributeGuid(
                mediaType,
                in s_majorType,
                in s_mediaTypeVideo);
            SetAttributeGuid(
                mediaType,
                in s_subtype,
                in subtype);
            SetAttributeUInt64(
                mediaType,
                in s_frameSize,
                PackPair(width, height));
            SetAttributeUInt64(
                mediaType,
                in s_frameRate,
                PackPair(
                    frameRateNumerator,
                    frameRateDenominator));
            SetAttributeUInt64(
                mediaType,
                in s_pixelAspectRatio,
                PackPair(1, 1));
            SetAttributeUInt32(
                mediaType,
                in s_interlaceMode,
                VideoInterlaceProgressive);
            return mediaType;
        }
        catch
        {
            Release(mediaType);
            throw;
        }
    }

    private static nint CreateMediaType()
    {
        nint mediaType = 0;
        ThrowIfFailed(
            MFCreateMediaType(&mediaType),
            "create a Media Foundation media type");
        return mediaType;
    }

    private static void SetAttributeGuid(
        nint attributes,
        in Guid key,
        in Guid value)
    {
        fixed (Guid* keyPointer = &key)
        fixed (Guid* valuePointer = &value)
        {
            delegate* unmanaged[Stdcall]<
                nint,
                Guid*,
                Guid*,
                int> set =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    Guid*,
                    Guid*,
                    int>)VTable(attributes)[24];
            ThrowIfFailed(
                set(
                    attributes,
                    keyPointer,
                    valuePointer),
                "set a Media Foundation GUID attribute");
        }
    }

    private static void SetAttributeUInt64(
        nint attributes,
        in Guid key,
        ulong value)
    {
        fixed (Guid* keyPointer = &key)
        {
            delegate* unmanaged[Stdcall]<
                nint,
                Guid*,
                ulong,
                int> set =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    Guid*,
                    ulong,
                    int>)VTable(attributes)[22];
            ThrowIfFailed(
                set(attributes, keyPointer, value),
                "set a Media Foundation 64-bit attribute");
        }
    }

    private static void SetSourceReaderStreamSelection(
        nint reader,
        uint stream,
        bool selected)
    {
        delegate* unmanaged[Stdcall]<
            nint,
            uint,
            int,
            int> setSelection =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                int,
                int>)VTable(reader)[4];
        ThrowIfFailed(
            setSelection(
                reader,
                stream,
                selected ? 1 : 0),
            "select a Media Foundation source stream");
    }

    private static ulong PackPair(
        uint first,
        uint second) =>
        ((ulong)first << 32) | second;

    [LibraryImport(
        "mfreadwrite.dll",
        StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(
        CallConvs = [typeof(CallConvStdcall)])]
    private static partial int
        MFCreateSourceReaderFromURL(
        string sourceUrl,
        nint attributes,
        nint* reader);

    [LibraryImport(
        "mfreadwrite.dll",
        StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(
        CallConvs = [typeof(CallConvStdcall)])]
    private static partial int
        MFCreateSinkWriterFromURL(
        string destinationUrl,
        nint byteStream,
        nint attributes,
        nint* writer);

    [LibraryImport("mfplat.dll")]
    [UnmanagedCallConv(
        CallConvs = [typeof(CallConvStdcall)])]
    private static partial int MFCreateMediaType(
        nint* mediaType);

    [LibraryImport("mfplat.dll")]
    [UnmanagedCallConv(
        CallConvs = [typeof(CallConvStdcall)])]
    private static partial int MFCreateDXGISurfaceBuffer(
        in Guid interfaceId,
        nint surface,
        uint subresourceIndex,
        [MarshalAs(UnmanagedType.Bool)] bool bottomUpWhenLinear,
        nint* buffer);

    [LibraryImport("mfplat.dll")]
    [UnmanagedCallConv(
        CallConvs = [typeof(CallConvStdcall)])]
    private static partial int MFCreateSample(
        nint* sample);

    [LibraryImport("mfplat.dll")]
    [UnmanagedCallConv(
        CallConvs = [typeof(CallConvStdcall)])]
    private static partial int MFCreateAlignedMemoryBuffer(
        uint maximumLength,
        uint alignment,
        nint* buffer);

    [LibraryImport("mfplat.dll")]
    [UnmanagedCallConv(
        CallConvs = [typeof(CallConvStdcall)])]
    private static partial int MFCreateTrackedSample(
        nint* trackedSample);
}
