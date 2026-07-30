using System.Buffers.Binary;

namespace ProGPU.Media.Containers;

internal enum IsoBmffTrackKind
{
    Unknown,
    Video,
    Audio
}

internal enum IsoBmffCodec
{
    Unknown,
    H264,
    H265,
    Aac,
    Pcm
}

internal enum IsoBmffPcmEncoding
{
    Unknown,
    SignedLittleEndian,
    SignedBigEndian
}

internal readonly record struct IsoBmffSample(
    long Offset,
    int Size,
    long DecodeTime,
    long PresentationTime,
    int Duration,
    bool IsSync);

internal readonly record struct IsoBmffEdit(
    ulong SegmentDuration,
    long MediaTime,
    short MediaRateInteger,
    short MediaRateFraction);

internal sealed record IsoBmffTrack(
    IsoBmffTrackKind Kind,
    IsoBmffCodec Codec,
    uint Timescale,
    long Duration,
    ushort Width,
    ushort Height,
    int NalLengthSize,
    byte[] CodecConfiguration,
    IsoBmffSample[] Samples)
{
    internal uint SampleEntryType { get; init; }
    internal byte[] SampleEntryPayload { get; init; } = [];
    internal ushort AudioChannelCount { get; init; }
    internal ushort AudioBitsPerSample { get; init; }
    internal uint AudioSampleRate { get; init; }
    internal IsoBmffPcmEncoding PcmEncoding { get; init; }
    internal uint MovieTimescale { get; init; }
    internal IsoBmffEdit[] EditList { get; init; } = [];
}

internal sealed record IsoBmffMovie(
    IsoBmffTrack[] Tracks);

/// <summary>
/// Clean-room ISO Base Media File Format sample-table reader used by the
/// dependency-free Linux provider. Parsing is O(B + S + C) time for B boxes,
/// S samples, and C chunks, with O(S + C) retained index storage. Media payload
/// bytes are never copied during parsing.
/// </summary>
internal sealed class IsoBmffDemuxer
{
    private const int MaximumTracks = 64;
    private const int MaximumSamples = 16_777_216;
    private const int MaximumChunks = 4_194_304;
    private const int MaximumTableEntries = 16_777_216;
    private const int MaximumCodecConfigurationBytes = 1_048_576;

    private readonly Stream _stream;
    private readonly byte[] _scratch = new byte[32];

    public IsoBmffDemuxer(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException(
                "ISO-BMFF parsing requires a readable, seekable stream.",
                nameof(stream));
        }
        _stream = stream;
    }

    public IsoBmffMovie Parse()
    {
        var tracks = new List<IsoBmffTrack>();
        long end = _stream.Length;
        long position = 0;
        while (TryReadBox(position, end, out Box box))
        {
            if (box.Type == BoxType.Moov)
            {
                ParseMovie(box, tracks);
            }
            position = box.End;
        }

        if (tracks.Count == 0)
        {
            throw new InvalidDataException(
                "The ISO-BMFF source contains no supported audio or video sample table.");
        }
        return new IsoBmffMovie(tracks.ToArray());
    }

    private void ParseMovie(
        in Box parent,
        List<IsoBmffTrack> destination)
    {
        uint movieTimescale = 0;
        long position = parent.PayloadStart;
        while (TryReadBox(position, parent.End, out Box child))
        {
            if (child.Type == BoxType.Mvhd)
            {
                movieTimescale =
                    ParseMovieTimescale(child);
            }
            position = child.End;
        }

        position = parent.PayloadStart;
        while (TryReadBox(position, parent.End, out Box child))
        {
            if (child.Type == BoxType.Trak)
            {
                if (destination.Count >= MaximumTracks)
                {
                    throw new InvalidDataException(
                        $"ISO-BMFF track count exceeds {MaximumTracks}.");
                }
                var builder = new TrackBuilder
                {
                    MovieTimescale = movieTimescale
                };
                ParseTrack(child, builder);
                IsoBmffTrack? track = builder.Build();
                if (track is not null)
                {
                    destination.Add(track);
                }
            }
            position = child.End;
        }
    }

    private uint ParseMovieTimescale(in Box box)
    {
        EnsurePayload(box, 4);
        Span<byte> fullHeader = stackalloc byte[4];
        ReadAt(box.PayloadStart, fullHeader);
        byte version = fullHeader[0];
        if (version == 0)
        {
            EnsurePayload(box, 20);
            Span<byte> value = stackalloc byte[12];
            ReadAt(box.PayloadStart + 4, value);
            return BinaryPrimitives.ReadUInt32BigEndian(
                value[8..]);
        }
        if (version == 1)
        {
            EnsurePayload(box, 32);
            Span<byte> value = stackalloc byte[20];
            ReadAt(box.PayloadStart + 4, value);
            return BinaryPrimitives.ReadUInt32BigEndian(
                value[16..]);
        }
        throw new InvalidDataException(
            $"Unsupported mvhd version {version}.");
    }

    private void ParseTrack(in Box parent, TrackBuilder builder)
    {
        long position = parent.PayloadStart;
        while (TryReadBox(position, parent.End, out Box child))
        {
            switch (child.Type)
            {
                case BoxType.Edts:
                    ParseEditContainer(child, builder);
                    break;
                case BoxType.Mdia:
                    ParseMedia(child, builder);
                    break;
            }
            position = child.End;
        }
    }

    private void ParseEditContainer(
        in Box parent,
        TrackBuilder builder)
    {
        long position = parent.PayloadStart;
        while (TryReadBox(position, parent.End, out Box child))
        {
            if (child.Type == BoxType.Elst)
            {
                builder.EditList =
                    ReadEditList(child);
            }
            position = child.End;
        }
    }

    private IsoBmffEdit[] ReadEditList(in Box box)
    {
        EnsurePayload(box, 8);
        Span<byte> header = stackalloc byte[8];
        ReadAt(box.PayloadStart, header);
        byte version = header[0];
        if (version is not 0 and not 1)
        {
            throw new InvalidDataException(
                $"Unsupported elst version {version}.");
        }

        uint count =
            BinaryPrimitives.ReadUInt32BigEndian(
                header[4..]);
        ValidateCount(
            count,
            MaximumTableEntries,
            "edit-list");
        int entrySize = version == 1 ? 20 : 12;
        EnsurePayload(
            box,
            checked(8L + (long)count * entrySize));

        var result =
            new IsoBmffEdit[checked((int)count)];
        long position = box.PayloadStart + 8;
        Span<byte> value = stackalloc byte[20];
        for (int index = 0;
             index < result.Length;
             index++)
        {
            ReadAt(
                position,
                value[..entrySize]);
            ulong segmentDuration;
            long mediaTime;
            int rateOffset;
            if (version == 1)
            {
                segmentDuration =
                    BinaryPrimitives
                        .ReadUInt64BigEndian(value);
                mediaTime =
                    BinaryPrimitives
                        .ReadInt64BigEndian(value[8..]);
                rateOffset = 16;
            }
            else
            {
                segmentDuration =
                    BinaryPrimitives
                        .ReadUInt32BigEndian(value);
                mediaTime =
                    BinaryPrimitives
                        .ReadInt32BigEndian(value[4..]);
                rateOffset = 8;
            }
            result[index] =
                new IsoBmffEdit(
                    segmentDuration,
                    mediaTime,
                    BinaryPrimitives
                        .ReadInt16BigEndian(
                            value[rateOffset..]),
                    BinaryPrimitives
                        .ReadInt16BigEndian(
                            value[(rateOffset + 2)..]));
            position += entrySize;
        }
        return result;
    }

    private void ParseMedia(in Box parent, TrackBuilder builder)
    {
        long position = parent.PayloadStart;
        while (TryReadBox(position, parent.End, out Box child))
        {
            switch (child.Type)
            {
                case BoxType.Mdhd:
                    ParseMediaHeader(child, builder);
                    break;
                case BoxType.Hdlr:
                    ParseHandler(child, builder);
                    break;
                case BoxType.Minf:
                    ParseMediaInformation(child, builder);
                    break;
            }
            position = child.End;
        }
    }

    private void ParseMediaInformation(
        in Box parent,
        TrackBuilder builder)
    {
        long position = parent.PayloadStart;
        while (TryReadBox(position, parent.End, out Box child))
        {
            if (child.Type == BoxType.Stbl)
            {
                ParseSampleTable(child, builder);
            }
            position = child.End;
        }
    }

    private void ParseSampleTable(
        in Box parent,
        TrackBuilder builder)
    {
        long position = parent.PayloadStart;
        while (TryReadBox(position, parent.End, out Box child))
        {
            switch (child.Type)
            {
                case BoxType.Stsd:
                    ParseSampleDescription(child, builder);
                    break;
                case BoxType.Stts:
                    builder.DecodeTimeEntries =
                        ReadTimeEntries(child, signedOffsets: false);
                    break;
                case BoxType.Ctts:
                    builder.CompositionTimeEntries =
                        ReadTimeEntries(child, signedOffsets: true);
                    break;
                case BoxType.Stsc:
                    builder.SampleToChunkEntries =
                        ReadSampleToChunk(child);
                    break;
                case BoxType.Stsz:
                    builder.SampleSizes =
                        ReadSampleSizes(child);
                    break;
                case BoxType.Stco:
                    builder.ChunkOffsets =
                        ReadChunkOffsets(child, uses64BitOffsets: false);
                    break;
                case BoxType.Co64:
                    builder.ChunkOffsets =
                        ReadChunkOffsets(child, uses64BitOffsets: true);
                    break;
                case BoxType.Stss:
                    builder.SyncSamples =
                        ReadSyncSamples(child);
                    break;
            }
            position = child.End;
        }
    }

    private void ParseMediaHeader(
        in Box box,
        TrackBuilder builder)
    {
        EnsurePayload(box, 4);
        Span<byte> fullHeader = stackalloc byte[4];
        ReadAt(box.PayloadStart, fullHeader);
        byte version = fullHeader[0];
        if (version == 0)
        {
            EnsurePayload(box, 20);
            Span<byte> value = stackalloc byte[16];
            ReadAt(box.PayloadStart + 4, value);
            builder.Timescale =
                BinaryPrimitives.ReadUInt32BigEndian(value[8..]);
            builder.Duration =
                BinaryPrimitives.ReadUInt32BigEndian(value[12..]);
        }
        else if (version == 1)
        {
            EnsurePayload(box, 32);
            Span<byte> value = stackalloc byte[28];
            ReadAt(box.PayloadStart + 4, value);
            builder.Timescale =
                BinaryPrimitives.ReadUInt32BigEndian(value[16..]);
            ulong duration =
                BinaryPrimitives.ReadUInt64BigEndian(value[20..]);
            builder.Duration =
                duration > long.MaxValue
                    ? long.MaxValue
                    : (long)duration;
        }
        else
        {
            throw new InvalidDataException(
                $"Unsupported mdhd version {version}.");
        }
    }

    private void ParseHandler(
        in Box box,
        TrackBuilder builder)
    {
        Span<byte> value = stackalloc byte[12];
        ReadAt(box.PayloadStart, value);
        uint handler =
            BinaryPrimitives.ReadUInt32BigEndian(value[8..]);
        builder.Kind = handler switch
        {
            BoxType.Vide => IsoBmffTrackKind.Video,
            BoxType.Soun => IsoBmffTrackKind.Audio,
            _ => IsoBmffTrackKind.Unknown
        };
    }

    private void ParseSampleDescription(
        in Box box,
        TrackBuilder builder)
    {
        Span<byte> header = stackalloc byte[8];
        ReadAt(box.PayloadStart, header);
        uint count =
            BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        ValidateCount(count, MaximumTracks, "sample description");

        long position = box.PayloadStart + 8;
        for (uint index = 0; index < count; index++)
        {
            if (!TryReadBox(position, box.End, out Box entry))
            {
                throw new InvalidDataException(
                    "The stsd sample entry is truncated.");
            }
            ParseSampleEntry(entry, builder);
            position = entry.End;
        }
    }

    private void ParseSampleEntry(
        in Box entry,
        TrackBuilder builder)
    {
        bool supported = true;
        switch (entry.Type)
        {
            case BoxType.Avc1:
            case BoxType.Avc3:
                builder.Codec = IsoBmffCodec.H264;
                ParseVisualSampleEntry(entry, builder);
                break;
            case BoxType.Hvc1:
            case BoxType.Hev1:
                builder.Codec = IsoBmffCodec.H265;
                ParseVisualSampleEntry(entry, builder);
                break;
            case BoxType.Mp4a:
                builder.Codec = IsoBmffCodec.Aac;
                ParseAudioSampleEntry(
                    entry,
                    builder,
                    IsoBmffPcmEncoding.Unknown);
                break;
            case BoxType.Lpcm:
                builder.Codec = IsoBmffCodec.Pcm;
                ParseAudioSampleEntry(
                    entry,
                    builder,
                    IsoBmffPcmEncoding.Unknown);
                break;
            case BoxType.Sowt:
                builder.Codec = IsoBmffCodec.Pcm;
                ParseAudioSampleEntry(
                    entry,
                    builder,
                    IsoBmffPcmEncoding
                        .SignedLittleEndian);
                break;
            case BoxType.Twos:
                builder.Codec = IsoBmffCodec.Pcm;
                ParseAudioSampleEntry(
                    entry,
                    builder,
                    IsoBmffPcmEncoding
                        .SignedBigEndian);
                break;
            default:
                supported = false;
                break;
        }

        if (supported)
        {
            long payloadLength = entry.End - entry.PayloadStart;
            if (payloadLength is < 0 or > MaximumCodecConfigurationBytes)
            {
                throw new InvalidDataException(
                    "The sample entry is unreasonably large.");
            }
            var payload = new byte[checked((int)payloadLength)];
            ReadAt(entry.PayloadStart, payload);
            builder.SampleEntryType = entry.Type;
            builder.SampleEntryPayload = payload;
        }
    }

    private void ParseVisualSampleEntry(
        in Box entry,
        TrackBuilder builder)
    {
        const int visualHeaderSize = 78;
        EnsurePayload(entry, visualHeaderSize);
        Span<byte> dimensions = stackalloc byte[4];
        ReadAt(entry.PayloadStart + 24, dimensions);
        builder.Width =
            BinaryPrimitives.ReadUInt16BigEndian(dimensions);
        builder.Height =
            BinaryPrimitives.ReadUInt16BigEndian(dimensions[2..]);

        ParseCodecChildren(
            entry.PayloadStart + visualHeaderSize,
            entry.End,
            builder);
    }

    private void ParseAudioSampleEntry(
        in Box entry,
        TrackBuilder builder,
        IsoBmffPcmEncoding pcmEncoding)
    {
        const int audioHeaderSize = 28;
        EnsurePayload(entry, audioHeaderSize);
        Span<byte> header =
            stackalloc byte[audioHeaderSize];
        ReadAt(entry.PayloadStart, header);
        ushort version =
            BinaryPrimitives
                .ReadUInt16BigEndian(
                    header[8..]);
        if (version == 0)
        {
            builder.AudioChannelCount =
                BinaryPrimitives
                    .ReadUInt16BigEndian(
                        header[16..]);
            builder.AudioBitsPerSample =
                BinaryPrimitives
                    .ReadUInt16BigEndian(
                        header[18..]);
            builder.AudioSampleRate =
                BinaryPrimitives
                    .ReadUInt32BigEndian(
                        header[24..]) >>
                16;
            builder.PcmEncoding =
                pcmEncoding;
        }
        ParseCodecChildren(
            entry.PayloadStart + audioHeaderSize,
            entry.End,
            builder);
    }

    private void ParseCodecChildren(
        long position,
        long end,
        TrackBuilder builder)
    {
        while (TryReadBox(position, end, out Box child))
        {
            if (child.Type is BoxType.AvcC or BoxType.HvcC)
            {
                long length = child.End - child.PayloadStart;
                if (length is <= 0 or > MaximumCodecConfigurationBytes)
                {
                    throw new InvalidDataException(
                        "Codec configuration is empty or unreasonably large.");
                }
                byte[] configuration =
                    new byte[checked((int)length)];
                ReadAt(child.PayloadStart, configuration);
                builder.CodecConfiguration = configuration;
                builder.NalLengthSize =
                    child.Type == BoxType.AvcC
                        ? configuration.Length >= 5
                            ? (configuration[4] & 0x03) + 1
                            : 0
                        : configuration.Length >= 22
                            ? (configuration[21] & 0x03) + 1
                            : 0;
            }
            position = child.End;
        }
    }

    private TimeEntry[] ReadTimeEntries(
        in Box box,
        bool signedOffsets)
    {
        Span<byte> header = stackalloc byte[8];
        ReadAt(box.PayloadStart, header);
        byte version = header[0];
        uint count =
            BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        ValidateCount(count, MaximumTableEntries, "time-to-sample");
        EnsurePayload(box, checked(8L + count * 8L));

        var result = new TimeEntry[checked((int)count)];
        long position = box.PayloadStart + 8;
        Span<byte> value = stackalloc byte[8];
        for (int index = 0; index < result.Length; index++)
        {
            ReadAt(position, value);
            uint sampleCount =
                BinaryPrimitives.ReadUInt32BigEndian(value);
            uint raw =
                BinaryPrimitives.ReadUInt32BigEndian(value[4..]);
            long sampleDelta =
                signedOffsets && version == 1
                    ? BinaryPrimitives.ReadInt32BigEndian(value[4..])
                    : raw;
            result[index] =
                new TimeEntry(sampleCount, sampleDelta);
            position += 8;
        }
        return result;
    }

    private SampleToChunkEntry[] ReadSampleToChunk(in Box box)
    {
        uint count = ReadFullBoxEntryCount(box);
        ValidateCount(count, MaximumTableEntries, "sample-to-chunk");
        EnsurePayload(box, checked(8L + count * 12L));

        var result =
            new SampleToChunkEntry[checked((int)count)];
        long position = box.PayloadStart + 8;
        Span<byte> value = stackalloc byte[12];
        uint previousFirstChunk = 0;
        for (int index = 0; index < result.Length; index++)
        {
            ReadAt(position, value);
            uint firstChunk =
                BinaryPrimitives.ReadUInt32BigEndian(value);
            uint samplesPerChunk =
                BinaryPrimitives.ReadUInt32BigEndian(value[4..]);
            if (firstChunk == 0 ||
                firstChunk <= previousFirstChunk ||
                samplesPerChunk == 0)
            {
                throw new InvalidDataException(
                    "The stsc table is not strictly ordered or contains an empty chunk.");
            }
            result[index] =
                new SampleToChunkEntry(
                    firstChunk,
                    samplesPerChunk);
            previousFirstChunk = firstChunk;
            position += 12;
        }
        return result;
    }

    private int[] ReadSampleSizes(in Box box)
    {
        Span<byte> header = stackalloc byte[12];
        ReadAt(box.PayloadStart, header);
        uint uniformSize =
            BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        uint count =
            BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        ValidateCount(count, MaximumSamples, "sample size");
        var result = new int[checked((int)count)];
        if (uniformSize != 0)
        {
            if (uniformSize > int.MaxValue)
            {
                throw new InvalidDataException(
                    "A media sample is larger than the supported addressable buffer.");
            }
            Array.Fill(result, (int)uniformSize);
            return result;
        }

        EnsurePayload(box, checked(12L + count * 4L));
        long position = box.PayloadStart + 12;
        Span<byte> value = stackalloc byte[4];
        for (int index = 0; index < result.Length; index++)
        {
            ReadAt(position, value);
            uint size =
                BinaryPrimitives.ReadUInt32BigEndian(value);
            if (size > int.MaxValue)
            {
                throw new InvalidDataException(
                    "A media sample is larger than the supported addressable buffer.");
            }
            result[index] = (int)size;
            position += 4;
        }
        return result;
    }

    private long[] ReadChunkOffsets(
        in Box box,
        bool uses64BitOffsets)
    {
        uint count = ReadFullBoxEntryCount(box);
        ValidateCount(count, MaximumChunks, "chunk offset");
        int width = uses64BitOffsets ? 8 : 4;
        EnsurePayload(box, checked(8L + count * width));
        var result = new long[checked((int)count)];
        long position = box.PayloadStart + 8;
        Span<byte> value = stackalloc byte[8];
        for (int index = 0; index < result.Length; index++)
        {
            ReadAt(position, value[..width]);
            ulong offset = uses64BitOffsets
                ? BinaryPrimitives.ReadUInt64BigEndian(value)
                : BinaryPrimitives.ReadUInt32BigEndian(value);
            if (offset > long.MaxValue)
            {
                throw new InvalidDataException(
                    "A chunk offset exceeds the supported stream range.");
            }
            result[index] = (long)offset;
            position += width;
        }
        return result;
    }

    private HashSet<int> ReadSyncSamples(in Box box)
    {
        uint count = ReadFullBoxEntryCount(box);
        ValidateCount(count, MaximumSamples, "sync sample");
        EnsurePayload(box, checked(8L + count * 4L));
        var result = new HashSet<int>();
        long position = box.PayloadStart + 8;
        Span<byte> value = stackalloc byte[4];
        for (uint index = 0; index < count; index++)
        {
            ReadAt(position, value);
            uint oneBased =
                BinaryPrimitives.ReadUInt32BigEndian(value);
            if (oneBased == 0 || oneBased > int.MaxValue)
            {
                throw new InvalidDataException(
                    "The stss table contains an invalid sample index.");
            }
            result.Add(checked((int)oneBased - 1));
            position += 4;
        }
        return result;
    }

    private uint ReadFullBoxEntryCount(in Box box)
    {
        Span<byte> value = stackalloc byte[8];
        ReadAt(box.PayloadStart, value);
        return BinaryPrimitives.ReadUInt32BigEndian(value[4..]);
    }

    private bool TryReadBox(
        long position,
        long parentEnd,
        out Box box)
    {
        if (position == parentEnd)
        {
            box = default;
            return false;
        }
        if (position < 0 || parentEnd - position < 8)
        {
            throw new InvalidDataException(
                "An ISO-BMFF box header is truncated.");
        }

        Span<byte> header = _scratch.AsSpan(0, 16);
        ReadAt(position, header[..8]);
        uint size32 =
            BinaryPrimitives.ReadUInt32BigEndian(header);
        uint type =
            BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        long headerSize = 8;
        ulong size;
        if (size32 == 1)
        {
            ReadAt(position + 8, header[..8]);
            size =
                BinaryPrimitives.ReadUInt64BigEndian(header);
            headerSize = 16;
        }
        else if (size32 == 0)
        {
            size = checked((ulong)(parentEnd - position));
        }
        else
        {
            size = size32;
        }

        if (size < (ulong)headerSize ||
            size > (ulong)(parentEnd - position))
        {
            throw new InvalidDataException(
                $"ISO-BMFF box '{FourCc(type)}' exceeds its parent.");
        }
        long end = checked(position + (long)size);
        box = new Box(
            type,
            position + headerSize,
            end);
        return true;
    }

    private void ReadAt(long position, Span<byte> destination)
    {
        _stream.Position = position;
        _stream.ReadExactly(destination);
    }

    private static void EnsurePayload(in Box box, long bytes)
    {
        if (bytes < 0 || box.End - box.PayloadStart < bytes)
        {
            throw new InvalidDataException(
                $"ISO-BMFF box '{FourCc(box.Type)}' is truncated.");
        }
    }

    private static void ValidateCount(
        uint count,
        int maximum,
        string table)
    {
        if (count > maximum)
        {
            throw new InvalidDataException(
                $"ISO-BMFF {table} count {count} exceeds the bounded limit {maximum}.");
        }
    }

    private static string FourCc(uint value) =>
        string.Create(
            4,
            value,
            static (destination, code) =>
            {
                destination[0] = (char)(code >> 24);
                destination[1] = (char)(code >> 16);
                destination[2] = (char)(code >> 8);
                destination[3] = (char)code;
            });

    private readonly record struct Box(
        uint Type,
        long PayloadStart,
        long End);

    private readonly record struct TimeEntry(
        uint SampleCount,
        long Value);

    private readonly record struct SampleToChunkEntry(
        uint FirstChunk,
        uint SamplesPerChunk);

    private sealed class TrackBuilder
    {
        public IsoBmffTrackKind Kind;
        public IsoBmffCodec Codec;
        public uint Timescale;
        public long Duration;
        public ushort Width;
        public ushort Height;
        public int NalLengthSize;
        public byte[] CodecConfiguration = [];
        public uint SampleEntryType;
        public byte[] SampleEntryPayload = [];
        public ushort AudioChannelCount;
        public ushort AudioBitsPerSample;
        public uint AudioSampleRate;
        public IsoBmffPcmEncoding PcmEncoding;
        public uint MovieTimescale;
        public IsoBmffEdit[] EditList = [];
        public int[] SampleSizes = [];
        public long[] ChunkOffsets = [];
        public TimeEntry[] DecodeTimeEntries = [];
        public TimeEntry[] CompositionTimeEntries = [];
        public SampleToChunkEntry[] SampleToChunkEntries = [];
        public HashSet<int>? SyncSamples;

        public IsoBmffTrack? Build()
        {
            if (Kind == IsoBmffTrackKind.Unknown ||
                Codec == IsoBmffCodec.Unknown ||
                Timescale == 0 ||
                SampleSizes.Length == 0)
            {
                return null;
            }
            if (ChunkOffsets.Length == 0 ||
                SampleToChunkEntries.Length == 0 ||
                DecodeTimeEntries.Length == 0)
            {
                throw new InvalidDataException(
                    "A supported ISO-BMFF track is missing required sample tables.");
            }

            long[] offsets = BuildSampleOffsets();
            (long[] decodeTimes, int[] durations) =
                BuildDecodeTimes();
            long[] compositionOffsets =
                BuildCompositionOffsets();
            var samples =
                new IsoBmffSample[SampleSizes.Length];
            for (int index = 0;
                 index < samples.Length;
                 index++)
            {
                samples[index] = new IsoBmffSample(
                    offsets[index],
                    SampleSizes[index],
                    decodeTimes[index],
                    checked(
                        decodeTimes[index] +
                        compositionOffsets[index]),
                    durations[index],
                    SyncSamples is null ||
                    SyncSamples.Contains(index));
            }

            return new IsoBmffTrack(
                Kind,
                Codec,
                Timescale,
                Duration,
                Width,
                Height,
                NalLengthSize,
                CodecConfiguration,
                samples)
            {
                SampleEntryType =
                    SampleEntryType,
                SampleEntryPayload =
                    SampleEntryPayload,
                AudioChannelCount =
                    AudioChannelCount,
                AudioBitsPerSample =
                    AudioBitsPerSample,
                AudioSampleRate =
                    AudioSampleRate,
                PcmEncoding =
                    PcmEncoding,
                MovieTimescale =
                    MovieTimescale,
                EditList =
                    EditList
            };
        }

        private long[] BuildSampleOffsets()
        {
            var result = new long[SampleSizes.Length];
            int sample = 0;
            int mapping = 0;
            for (int chunk = 0;
                 chunk < ChunkOffsets.Length;
                 chunk++)
            {
                uint oneBasedChunk = checked((uint)chunk + 1);
                while (mapping + 1 <
                           SampleToChunkEntries.Length &&
                       SampleToChunkEntries[mapping + 1]
                           .FirstChunk <= oneBasedChunk)
                {
                    mapping++;
                }
                uint samplesInChunk =
                    SampleToChunkEntries[mapping]
                        .SamplesPerChunk;
                long offset = ChunkOffsets[chunk];
                for (uint index = 0;
                     index < samplesInChunk;
                     index++)
                {
                    if (sample >= SampleSizes.Length)
                    {
                        throw new InvalidDataException(
                            "The stsc table maps more samples than stsz declares.");
                    }
                    result[sample] = offset;
                    offset = checked(
                        offset +
                        SampleSizes[sample]);
                    sample++;
                }
            }
            if (sample != SampleSizes.Length)
            {
                throw new InvalidDataException(
                    "The stsc/stco tables do not map every declared sample.");
            }
            return result;
        }

        private (long[] Times, int[] Durations)
            BuildDecodeTimes()
        {
            var times = new long[SampleSizes.Length];
            var durations = new int[SampleSizes.Length];
            int sample = 0;
            long time = 0;
            foreach (TimeEntry entry in DecodeTimeEntries)
            {
                if (entry.Value is < 0 or > int.MaxValue)
                {
                    throw new InvalidDataException(
                        "The stts table contains an unsupported sample duration.");
                }
                for (uint index = 0;
                     index < entry.SampleCount;
                     index++)
                {
                    if (sample >= times.Length)
                    {
                        throw new InvalidDataException(
                            "The stts table describes too many samples.");
                    }
                    times[sample] = time;
                    durations[sample] = (int)entry.Value;
                    time = checked(time + entry.Value);
                    sample++;
                }
            }
            if (sample != times.Length)
            {
                throw new InvalidDataException(
                    "The stts table does not describe every sample.");
            }
            return (times, durations);
        }

        private long[] BuildCompositionOffsets()
        {
            var result = new long[SampleSizes.Length];
            if (CompositionTimeEntries.Length == 0)
            {
                return result;
            }

            int sample = 0;
            foreach (TimeEntry entry in CompositionTimeEntries)
            {
                for (uint index = 0;
                     index < entry.SampleCount;
                     index++)
                {
                    if (sample >= result.Length)
                    {
                        throw new InvalidDataException(
                            "The ctts table describes too many samples.");
                    }
                    result[sample++] = entry.Value;
                }
            }
            if (sample != result.Length)
            {
                throw new InvalidDataException(
                    "The ctts table does not describe every sample.");
            }
            return result;
        }
    }

    private static class BoxType
    {
        public const uint Moov = 0x6D6F_6F76;
        public const uint Mvhd = 0x6D76_6864;
        public const uint Trak = 0x7472_616B;
        public const uint Edts = 0x6564_7473;
        public const uint Elst = 0x656C_7374;
        public const uint Mdia = 0x6D64_6961;
        public const uint Minf = 0x6D69_6E66;
        public const uint Stbl = 0x7374_626C;
        public const uint Mdhd = 0x6D64_6864;
        public const uint Hdlr = 0x6864_6C72;
        public const uint Stsd = 0x7374_7364;
        public const uint Stts = 0x7374_7473;
        public const uint Ctts = 0x6374_7473;
        public const uint Stsc = 0x7374_7363;
        public const uint Stsz = 0x7374_737A;
        public const uint Stco = 0x7374_636F;
        public const uint Co64 = 0x636F_3634;
        public const uint Stss = 0x7374_7373;
        public const uint Vide = 0x7669_6465;
        public const uint Soun = 0x736F_756E;
        public const uint Avc1 = 0x6176_6331;
        public const uint Avc3 = 0x6176_6333;
        public const uint Hvc1 = 0x6876_6331;
        public const uint Hev1 = 0x6865_7631;
        public const uint Mp4a = 0x6D70_3461;
        public const uint Lpcm = 0x6C70_636D;
        public const uint Sowt = 0x736F_7774;
        public const uint Twos = 0x7477_6F73;
        public const uint AvcC = 0x6176_6343;
        public const uint HvcC = 0x6876_6343;
    }
}
