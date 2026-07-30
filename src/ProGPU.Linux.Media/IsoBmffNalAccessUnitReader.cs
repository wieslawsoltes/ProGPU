using System.Buffers;
using System.Buffers.Binary;
using ProGPU.Media.Containers;

namespace ProGPU.Linux.Media;

/// <summary>
/// Reuses two pooled buffers to turn ISO-BMFF length-prefixed H.264/H.265
/// samples into V4L2 stateful-decoder Annex-B access units. Per-sample work is
/// O(N) in compressed bytes, storage is bounded by four times the largest
/// indexed sample plus codec configuration, and steady playback allocates no
/// managed objects.
/// </summary>
internal sealed class IsoBmffNalAccessUnitReader : IDisposable
{
    private const int MaximumAccessUnitBytes = 256 * 1024 * 1024;
    private static ReadOnlySpan<byte> StartCode =>
        [0, 0, 0, 1];

    private readonly Stream _stream;
    private readonly IsoBmffTrack _track;
    private byte[]? _input;
    private byte[]? _output;
    private byte[] _annexBConfiguration;
    private int _length;

    public IsoBmffNalAccessUnitReader(
        Stream stream,
        IsoBmffTrack track)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(track);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException(
                "Access-unit reads require a readable, seekable stream.",
                nameof(stream));
        }
        if (track.Codec is not
            (IsoBmffCodec.H264 or IsoBmffCodec.H265) ||
            track.NalLengthSize is < 1 or > 4)
        {
            throw new NotSupportedException(
                "Only length-prefixed H.264 and H.265 tracks can be converted to Annex B.");
        }

        _stream = stream;
        _track = track;
        _annexBConfiguration =
            track.Codec == IsoBmffCodec.H264
                ? BuildAvcConfiguration(
                    track.CodecConfiguration)
                : BuildHevcConfiguration(
                    track.CodecConfiguration);

        int largestSample = 1;
        foreach (IsoBmffSample sample in track.Samples)
        {
            largestSample =
                Math.Max(largestSample, sample.Size);
        }
        if (largestSample > MaximumAccessUnitBytes / 4)
        {
            throw new InvalidDataException(
                "The indexed access unit exceeds the bounded decoder workspace.");
        }
        int outputCapacity = checked(
            largestSample * 4 +
            _annexBConfiguration.Length);
        if (outputCapacity > MaximumAccessUnitBytes)
        {
            throw new InvalidDataException(
                "The converted access unit exceeds the bounded decoder workspace.");
        }
        _input =
            ArrayPool<byte>.Shared.Rent(largestSample);
        _output =
            ArrayPool<byte>.Shared.Rent(
                Math.Max(1, outputCapacity));
    }

    public ReadOnlySpan<byte> Current =>
        (_output ??
         throw new ObjectDisposedException(
             nameof(IsoBmffNalAccessUnitReader)))
        .AsSpan(0, _length);

    public ReadOnlySpan<byte> Read(
        int sampleIndex,
        bool prependCodecConfiguration = true)
    {
        byte[] input =
            _input ??
            throw new ObjectDisposedException(
                nameof(IsoBmffNalAccessUnitReader));
        byte[] output =
            _output ??
            throw new ObjectDisposedException(
                nameof(IsoBmffNalAccessUnitReader));
        if ((uint)sampleIndex >=
            (uint)_track.Samples.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleIndex));
        }

        IsoBmffSample sample =
            _track.Samples[sampleIndex];
        _stream.Position = sample.Offset;
        _stream.ReadExactly(
            input.AsSpan(0, sample.Size));

        int destination = 0;
        if (prependCodecConfiguration &&
            sample.IsSync &&
            _annexBConfiguration.Length != 0)
        {
            _annexBConfiguration.CopyTo(
                output,
                destination);
            destination +=
                _annexBConfiguration.Length;
        }

        int source = 0;
        while (source < sample.Size)
        {
            if (sample.Size - source <
                _track.NalLengthSize)
            {
                throw new InvalidDataException(
                    "A length-prefixed NAL header is truncated.");
            }
            uint nalLength =
                ReadNalLength(
                    input.AsSpan(
                        source,
                        _track.NalLengthSize));
            source += _track.NalLengthSize;
            if (nalLength == 0 ||
                nalLength >
                sample.Size - source)
            {
                throw new InvalidDataException(
                    "A length-prefixed NAL exceeds its media sample.");
            }

            StartCode.CopyTo(
                output.AsSpan(destination));
            destination += StartCode.Length;
            input.AsSpan(
                    source,
                    checked((int)nalLength))
                .CopyTo(
                    output.AsSpan(destination));
            source += checked((int)nalLength);
            destination += checked((int)nalLength);
        }

        _length = destination;
        return output.AsSpan(0, destination);
    }

    public void Dispose()
    {
        byte[]? input =
            Interlocked.Exchange(ref _input, null);
        byte[]? output =
            Interlocked.Exchange(ref _output, null);
        _length = 0;
        _annexBConfiguration = [];
        if (input is not null)
        {
            ArrayPool<byte>.Shared.Return(input);
        }
        if (output is not null)
        {
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    private static uint ReadNalLength(
        ReadOnlySpan<byte> value) =>
        value.Length switch
        {
            1 => value[0],
            2 => BinaryPrimitives
                .ReadUInt16BigEndian(value),
            3 => (uint)(value[0] << 16 |
                        value[1] << 8 |
                        value[2]),
            4 => BinaryPrimitives
                .ReadUInt32BigEndian(value),
            _ => throw new ArgumentOutOfRangeException(
                nameof(value))
        };

    private static byte[] BuildAvcConfiguration(
        ReadOnlySpan<byte> configuration)
    {
        if (configuration.IsEmpty)
        {
            return [];
        }
        if (configuration.Length < 7 ||
            configuration[0] != 1)
        {
            throw new InvalidDataException(
                "The avcC decoder configuration is truncated or unsupported.");
        }

        var units = new List<byte[]>();
        int position = 5;
        int sequenceCount =
            configuration[position++] & 0x1F;
        ReadConfigurationUnits(
            configuration,
            ref position,
            sequenceCount,
            units);
        if (position >= configuration.Length)
        {
            throw new InvalidDataException(
                "The avcC picture-parameter count is missing.");
        }
        int pictureCount =
            configuration[position++];
        ReadConfigurationUnits(
            configuration,
            ref position,
            pictureCount,
            units);
        return JoinAnnexB(units);
    }

    private static byte[] BuildHevcConfiguration(
        ReadOnlySpan<byte> configuration)
    {
        if (configuration.IsEmpty)
        {
            return [];
        }
        if (configuration.Length < 23 ||
            configuration[0] != 1)
        {
            throw new InvalidDataException(
                "The hvcC decoder configuration is truncated or unsupported.");
        }

        var units = new List<byte[]>();
        int position = 22;
        int arrayCount = configuration[position++];
        for (int array = 0;
             array < arrayCount;
             array++)
        {
            if (configuration.Length - position < 3)
            {
                throw new InvalidDataException(
                    "An hvcC NAL array header is truncated.");
            }
            position++;
            int unitCount =
                BinaryPrimitives.ReadUInt16BigEndian(
                    configuration[position..]);
            position += 2;
            ReadConfigurationUnits(
                configuration,
                ref position,
                unitCount,
                units);
        }
        return JoinAnnexB(units);
    }

    private static void ReadConfigurationUnits(
        ReadOnlySpan<byte> configuration,
        ref int position,
        int count,
        List<byte[]> destination)
    {
        for (int index = 0; index < count; index++)
        {
            if (configuration.Length - position < 2)
            {
                throw new InvalidDataException(
                    "A codec-configuration NAL length is truncated.");
            }
            int length =
                BinaryPrimitives.ReadUInt16BigEndian(
                    configuration[position..]);
            position += 2;
            if (length == 0 ||
                configuration.Length - position < length)
            {
                throw new InvalidDataException(
                    "A codec-configuration NAL is empty or truncated.");
            }
            destination.Add(
                configuration
                    .Slice(position, length)
                    .ToArray());
            position += length;
        }
    }

    private static byte[] JoinAnnexB(
        IReadOnlyList<byte[]> units)
    {
        int length = 0;
        for (int index = 0;
             index < units.Count;
             index++)
        {
            length = checked(
                length +
                StartCode.Length +
                units[index].Length);
        }
        if (length > MaximumAccessUnitBytes)
        {
            throw new InvalidDataException(
                "Codec configuration exceeds the bounded decoder workspace.");
        }
        var result = new byte[length];
        int position = 0;
        for (int index = 0;
             index < units.Count;
             index++)
        {
            StartCode.CopyTo(
                result.AsSpan(position));
            position += StartCode.Length;
            units[index].CopyTo(result, position);
            position += units[index].Length;
        }
        return result;
    }
}
