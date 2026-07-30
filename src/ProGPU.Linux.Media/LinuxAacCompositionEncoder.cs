using System.Buffers.Binary;
using ProGPU.Media.Containers;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;

namespace ProGPU.Linux.Media;

/// <summary>
/// Describes the bounded PCM16 stream supplied to a pluggable Linux AAC
/// encoder.
/// </summary>
public readonly record struct LinuxAacEncoderConfiguration(
    uint SampleRate,
    uint ChannelCount,
    uint Bitrate,
    long TotalFrameCount)
{
    public int PreferredInputFrameCount =>
        LinuxPcm16Mixer.FramesPerBlock;
}

/// <summary>
/// Receives raw MPEG-4 AAC access units from a pluggable encoder.
/// </summary>
/// <remarks>
/// Calls are synchronous and must occur on the thread which created the
/// encoder. The sink owns and copies every access-unit payload before the
/// call returns. Encoders configure the stream exactly once before emitting
/// access units. The encoder-delay frame count describes decoded priming
/// frames before composition frame zero.
/// </remarks>
public interface ILinuxAacAccessUnitSink
{
    void Configure(
        ReadOnlySpan<byte> audioSpecificConfig,
        uint encoderDelayFrameCount);

    void WriteAccessUnit(
        ReadOnlySpan<byte> accessUnit,
        uint decodedFrameCount);
}

/// <summary>
/// Synchronous, typed PCM16-to-AAC codec boundary for Linux composition
/// export.
/// </summary>
public interface ILinuxAacEncoder : IDisposable
{
    void EncodePcm16(
        long firstFrame,
        ReadOnlySpan<short> interleavedSamples);

    void Complete();
}

/// <summary>
/// Optional application-supplied AAC encoder factory. ProGPU does not load,
/// scan for, or depend on a codec package.
/// </summary>
public interface ILinuxAacEncoderFactory
{
    bool CanEncode(
        in LinuxAacEncoderConfiguration configuration);

    ILinuxAacEncoder Create(
        in LinuxAacEncoderConfiguration configuration,
        ILinuxAacAccessUnitSink sink);
}

/// <summary>
/// Connects the bounded composition mixer to an explicitly supplied AAC
/// encoder while retaining ISO-BMFF ownership in ProGPU.
/// </summary>
/// <remarks>
/// Planning is O(C + B + O + E). Mixing is O(F * A) for F PCM frames and A
/// scheduled sources. Managed PCM workspace is fixed at 1,024 frames per
/// channel. Encoded payload is streamed to disk; retained managed metadata is
/// O(U) for U emitted access units.
/// </remarks>
internal static class LinuxAacCompositionEncoder
{
    internal static bool TryPrepare(
        MediaCompositionExportRequest request,
        MediaEffectRegistry effects,
        ILinuxAacEncoderFactory? factory,
        out LinuxCompositionAudioSourcePlan[] plans,
        out LinuxAacEncoderConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(effects);

        if (factory is null ||
            !string.Equals(
                request.EncodingProfile.AudioSubtype,
                "AAC",
                StringComparison.OrdinalIgnoreCase) ||
            request.EncodingProfile.AudioBitrate == 0 ||
            request.EncodingProfile.AudioSampleRate is
                < 8_000 or > 65_535 ||
            request.EncodingProfile.AudioChannelCount is
                not (1u or 2u) ||
            !LinuxCompositionAudioPlanner.TryCapture(
                request,
                effects,
                out plans,
                out long compositionFrameCount))
        {
            plans = [];
            configuration = default;
            return false;
        }

        configuration =
            new LinuxAacEncoderConfiguration(
                request.EncodingProfile.AudioSampleRate,
                request.EncodingProfile.AudioChannelCount,
                request.EncodingProfile.AudioBitrate,
                compositionFrameCount);
        try
        {
            if (factory.CanEncode(in configuration))
            {
                return true;
            }
        }
        catch
        {
            // Capability selection is non-throwing. Creation/encode failures
            // remain visible through RenderAsync.
        }

        plans = [];
        configuration = default;
        return false;
    }

    internal static IsoBmffCompositionTrack Encode(
        MediaCompositionExportRequest request,
        MediaEffectRegistry effects,
        ILinuxAacEncoderFactory factory,
        string spoolPath,
        CancellationToken cancellationToken)
    {
        if (!TryPrepare(
                request,
                effects,
                factory,
                out LinuxCompositionAudioSourcePlan[] plans,
                out LinuxAacEncoderConfiguration configuration))
        {
            throw new NotSupportedException(
                "No registered Linux AAC encoder accepts the requested PCM16 composition timeline.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var sink =
            new LinuxAacAccessUnitSpool(
                spoolPath,
                configuration);
        using ILinuxAacEncoder encoder =
            factory.Create(
                in configuration,
                sink) ??
            throw new InvalidOperationException(
                "The Linux AAC encoder factory returned no encoder.");

        LinuxPcm16TimelineMixer.Mix(
            plans,
            configuration.TotalFrameCount,
            configuration.SampleRate,
            configuration.ChannelCount,
            effects,
            EncodeBlock,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        encoder.Complete();
        cancellationToken.ThrowIfCancellationRequested();
        return sink.CompleteTrack();

        void EncodeBlock(
            long firstFrame,
            ReadOnlySpan<short> samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            encoder.EncodePcm16(
                firstFrame,
                samples);
        }
    }
}

/// <summary>
/// Validates and spools a sequential raw-AAC stream without retaining encoded
/// payload in managed memory.
/// </summary>
internal sealed class LinuxAacAccessUnitSpool :
    ILinuxAacAccessUnitSink,
    IDisposable
{
    private const int MaximumDecoderConfigurationBytes =
        256;
    private const int MaximumAccessUnitBytes =
        1 * 1024 * 1024;
    private const uint Mp4aSampleEntryType =
        0x6D703461;
    private readonly FileStream _stream;
    private readonly LinuxAacEncoderConfiguration
        _configuration;
    private readonly int _ownerThreadId =
        Environment.CurrentManagedThreadId;
    private readonly List<IsoBmffCompositionSample>
        _samples = [];
    private byte[]? _audioSpecificConfig;
    private uint _encoderDelayFrameCount;
    private long _decodedFrameCount;
    private bool _completed;
    private bool _disposed;

    internal LinuxAacAccessUnitSpool(
        string path,
        in LinuxAacEncoderConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (configuration.SampleRate is
                < 8_000 or > 65_535 ||
            configuration.ChannelCount is
                not (1u or 2u) ||
            configuration.Bitrate == 0 ||
            configuration.TotalFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration));
        }
        _configuration = configuration;
        _stream =
            new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
    }

    public void Configure(
        ReadOnlySpan<byte> audioSpecificConfig,
        uint encoderDelayFrameCount)
    {
        VerifyWritable();
        if (_audioSpecificConfig is not null)
        {
            throw new InvalidOperationException(
                "The AAC access-unit sink is already configured.");
        }
        if (audioSpecificConfig.Length is
                < 2 or
                > MaximumDecoderConfigurationBytes ||
            !MatchesConfiguration(
                audioSpecificConfig,
                _configuration.SampleRate,
                _configuration.ChannelCount) ||
            encoderDelayFrameCount >
                checked(
                    _configuration.SampleRate *
                    2))
        {
            throw new ArgumentException(
                "The AAC AudioSpecificConfig or encoder delay is incompatible with the requested output profile.",
                nameof(audioSpecificConfig));
        }

        _audioSpecificConfig =
            audioSpecificConfig.ToArray();
        _encoderDelayFrameCount =
            encoderDelayFrameCount;
    }

    public void WriteAccessUnit(
        ReadOnlySpan<byte> accessUnit,
        uint decodedFrameCount)
    {
        VerifyWritable();
        if (_audioSpecificConfig is null)
        {
            throw new InvalidOperationException(
                "Configure must precede the first AAC access unit.");
        }
        if (accessUnit.IsEmpty ||
            accessUnit.Length >
                MaximumAccessUnitBytes ||
            decodedFrameCount == 0 ||
            decodedFrameCount >
                _configuration.SampleRate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(accessUnit));
        }

        long nextFrameCount = checked(
            _decodedFrameCount +
            decodedFrameCount);
        long maximumFrameCount = checked(
            _configuration.TotalFrameCount +
            _encoderDelayFrameCount +
            _configuration.SampleRate * 2L);
        if (nextFrameCount > maximumFrameCount)
        {
            throw new InvalidDataException(
                "The AAC encoder emitted more than two seconds of trailing padding.");
        }

        long offset = _stream.Position;
        _stream.Write(accessUnit);
        _samples.Add(
            new IsoBmffCompositionSample(
                _stream.Name,
                offset,
                accessUnit.Length,
                checked((int)decodedFrameCount),
                CompositionOffset: 0,
                IsSync: true));
        _decodedFrameCount = nextFrameCount;
    }

    internal IsoBmffCompositionTrack CompleteTrack()
    {
        VerifyWritable();
        if (_audioSpecificConfig is null ||
            _samples.Count == 0 ||
            _decodedFrameCount <
                checked(
                    _encoderDelayFrameCount +
                    _configuration.TotalFrameCount))
        {
            throw new InvalidDataException(
                "The AAC encoder did not emit enough configured access units to cover the composition.");
        }

        _stream.Flush(flushToDisk: true);
        _completed = true;
        ulong presentationDuration =
            ScaleFramesToMovieTime(
                _configuration.TotalFrameCount,
                _configuration.SampleRate);
        if (presentationDuration == 0)
        {
            throw new InvalidDataException(
                "The encoded audio timeline is shorter than one movie-timescale unit.");
        }

        return new IsoBmffCompositionTrack(
            IsoBmffTrackKind.Audio,
            _configuration.SampleRate,
            Width: 0,
            Height: 0,
            Mp4aSampleEntryType,
            BuildMp4aSampleEntryPayload(
                _configuration,
                _audioSpecificConfig),
            _samples.ToArray())
        {
            Edits =
            [
                new IsoBmffCompositionEdit(
                    presentationDuration,
                    _encoderDelayFrameCount)
            ]
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stream.Dispose();
    }

    private void VerifyWritable()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
        if (_completed)
        {
            throw new InvalidOperationException(
                "The AAC access-unit spool is complete.");
        }
        if (Environment.CurrentManagedThreadId !=
            _ownerThreadId)
        {
            throw new InvalidOperationException(
                "The AAC access-unit sink is single-threaded.");
        }
    }

    private static bool MatchesConfiguration(
        ReadOnlySpan<byte> value,
        uint sampleRate,
        uint channelCount)
    {
        var reader = new BitReader(value);
        if (!reader.TryRead(5, out uint objectType) ||
            objectType != 2 ||
            !reader.TryRead(
                4,
                out uint frequencyIndex))
        {
            return false;
        }

        uint declaredSampleRate;
        if (frequencyIndex == 15)
        {
            if (!reader.TryRead(
                    24,
                    out declaredSampleRate))
            {
                return false;
            }
        }
        else
        {
            declaredSampleRate =
                frequencyIndex <
                    s_aacSampleRates.Length
                    ? s_aacSampleRates[
                        frequencyIndex]
                    : 0;
        }

        return reader.TryRead(
                   4,
                   out uint declaredChannels) &&
               declaredSampleRate == sampleRate &&
               declaredChannels == channelCount;
    }

    private static byte[] BuildMp4aSampleEntryPayload(
        in LinuxAacEncoderConfiguration configuration,
        ReadOnlySpan<byte> audioSpecificConfig)
    {
        using var decoderSpecific =
            new MemoryStream();
        WriteDescriptor(
            decoderSpecific,
            0x05,
            audioSpecificConfig);

        using var decoderConfiguration =
            new MemoryStream();
        decoderConfiguration.WriteByte(0x40);
        decoderConfiguration.WriteByte(0x15);
        decoderConfiguration.Write(
            [0, 0, 0]);
        WriteUInt32(
            decoderConfiguration,
            configuration.Bitrate);
        WriteUInt32(
            decoderConfiguration,
            configuration.Bitrate);
        decoderSpecific.Position = 0;
        decoderSpecific.CopyTo(
            decoderConfiguration);

        using var elementaryStream =
            new MemoryStream();
        WriteUInt16(
            elementaryStream,
            1);
        elementaryStream.WriteByte(0);
        WriteDescriptor(
            elementaryStream,
            0x04,
            decoderConfiguration.GetBuffer()
                .AsSpan(
                    0,
                    checked(
                        (int)decoderConfiguration
                            .Length)));
        WriteDescriptor(
            elementaryStream,
            0x06,
            [0x02]);

        using var esdsPayload =
            new MemoryStream();
        esdsPayload.Write(
            [0, 0, 0, 0]);
        WriteDescriptor(
            esdsPayload,
            0x03,
            elementaryStream.GetBuffer()
                .AsSpan(
                    0,
                    checked(
                        (int)elementaryStream
                            .Length)));

        byte[] esds =
            BuildBox(
                "esds",
                esdsPayload.GetBuffer()
                    .AsSpan(
                        0,
                        checked(
                            (int)esdsPayload
                                .Length)));
        var result =
            new byte[
                checked(
                    28 +
                    esds.Length)];
        BinaryPrimitives
            .WriteUInt16BigEndian(
                result.AsSpan(6),
                1);
        BinaryPrimitives
            .WriteUInt16BigEndian(
                result.AsSpan(16),
                checked(
                    (ushort)configuration
                        .ChannelCount));
        BinaryPrimitives
            .WriteUInt16BigEndian(
                result.AsSpan(18),
                16);
        BinaryPrimitives
            .WriteUInt32BigEndian(
                result.AsSpan(24),
                checked(
                    configuration.SampleRate
                    << 16));
        esds.CopyTo(
            result,
            28);
        return result;
    }

    private static byte[] BuildBox(
        string type,
        ReadOnlySpan<byte> payload)
    {
        var result =
            new byte[
                checked(
                    payload.Length +
                    8)];
        BinaryPrimitives
            .WriteUInt32BigEndian(
                result,
                checked((uint)result.Length));
        result[4] = checked((byte)type[0]);
        result[5] = checked((byte)type[1]);
        result[6] = checked((byte)type[2]);
        result[7] = checked((byte)type[3]);
        payload.CopyTo(
            result.AsSpan(8));
        return result;
    }

    private static void WriteDescriptor(
        Stream destination,
        byte tag,
        ReadOnlySpan<byte> payload)
    {
        destination.WriteByte(tag);
        Span<byte> length = stackalloc byte[4];
        int count = 1;
        uint remaining =
            checked((uint)payload.Length);
        length[3] =
            checked(
                (byte)(remaining & 0x7F));
        while ((remaining >>= 7) != 0)
        {
            count++;
            length[4 - count] =
                checked(
                    (byte)(
                        0x80 |
                        remaining &
                            0x7F));
        }
        destination.Write(
            length[(4 - count)..]);
        destination.Write(payload);
    }

    private static void WriteUInt16(
        Stream destination,
        ushort value)
    {
        Span<byte> bytes =
            stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(
            bytes,
            value);
        destination.Write(bytes);
    }

    private static void WriteUInt32(
        Stream destination,
        uint value)
    {
        Span<byte> bytes =
            stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(
            bytes,
            value);
        destination.Write(bytes);
    }

    private static ulong ScaleFramesToMovieTime(
        long frameCount,
        uint sampleRate)
    {
        Int128 numerator =
            (Int128)frameCount *
            IsoBmffCompositionWriter
                .MovieTimescale;
        return checked(
            (ulong)(
                (numerator +
                 sampleRate / 2) /
                sampleRate));
    }

    private static readonly uint[]
        s_aacSampleRates =
        [
            96_000,
            88_200,
            64_000,
            48_000,
            44_100,
            32_000,
            24_000,
            22_050,
            16_000,
            12_000,
            11_025,
            8_000,
            7_350
        ];

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _bitOffset;

        internal BitReader(
            ReadOnlySpan<byte> bytes)
        {
            _bytes = bytes;
        }

        internal bool TryRead(
            int bitCount,
            out uint result)
        {
            if (bitCount is < 0 or > 32 ||
                _bitOffset >
                    _bytes.Length * 8 -
                    bitCount)
            {
                result = 0;
                return false;
            }

            result = 0;
            for (int index = 0;
                 index < bitCount;
                 index++)
            {
                int absolute =
                    _bitOffset + index;
                result =
                    result << 1 |
                    (uint)(
                        _bytes[absolute / 8] >>
                        (7 - absolute % 8) &
                        1);
            }
            _bitOffset += bitCount;
            return true;
        }
    }
}
