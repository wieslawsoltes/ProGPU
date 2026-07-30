using System.Buffers.Binary;
using ProGPU.Media.Containers;

namespace ProGPU.Media.Editing;

/// <summary>
/// Bounded-metadata spool for H.264 Annex-B access units produced by a native
/// hardware encoder. Encoded bytes are converted directly to ISO-BMFF
/// length-prefixed samples; decoded pixels never pass through this type.
/// </summary>
/// <remarks>
/// Appending is O(B + N) time for B encoded bytes and N NAL units, with O(1)
/// working storage. Retained metadata is O(F) for F frames plus one SPS and
/// PPS copy. Final planning is O(F) time and storage.
/// </remarks>
internal sealed class IsoBmffH264AccessUnitSpool :
    IDisposable
{
    private const uint VideoTimescale = 90_000;
    private const int VisualSampleEntryHeaderSize = 78;
    private const int MaximumParameterSetBytes = ushort.MaxValue;

    private readonly string _path;
    private readonly ushort _width;
    private readonly ushort _height;
    private readonly int _sampleDuration;
    private readonly FileStream _stream;
    private readonly List<PendingSample> _samples = [];
    private byte[]? _sequenceParameterSet;
    private byte[]? _pictureParameterSet;
    private bool _disposed;

    internal IsoBmffH264AccessUnitSpool(
        string path,
        uint width,
        uint height,
        uint frameRateNumerator,
        uint frameRateDenominator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (width is 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        if (height is 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }
        ArgumentOutOfRangeException.ThrowIfZero(
            frameRateNumerator);
        ArgumentOutOfRangeException.ThrowIfZero(
            frameRateDenominator);

        _path = Path.GetFullPath(path);
        _width = checked((ushort)width);
        _height = checked((ushort)height);
        _sampleDuration = checked(
            (int)Math.Round(
                VideoTimescale *
                ((double)frameRateDenominator /
                 frameRateNumerator),
                MidpointRounding.AwayFromZero));
        if (_sampleDuration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameRateNumerator),
                "The frame rate exceeds the ISO-BMFF video timescale.");
        }
        _stream = new FileStream(
            _path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
    }

    internal int SampleCount => _samples.Count;

    internal void Append(
        ReadOnlySpan<byte> annexBAccessUnit,
        TimeSpan presentationTime,
        bool isKeyFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (presentationTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(presentationTime));
        }

        long sourceOffset = _stream.Position;
        int nalCount = 0;
        bool containsIdr = false;
        int searchOffset = 0;
        while (TryFindStartCode(
                   annexBAccessUnit,
                   searchOffset,
                   out int startCodeOffset,
                   out int startCodeLength))
        {
            int nalStart = checked(
                startCodeOffset + startCodeLength);
            if (!TryFindStartCode(
                    annexBAccessUnit,
                    nalStart,
                    out int nextStartCodeOffset,
                    out _))
            {
                nextStartCodeOffset =
                    annexBAccessUnit.Length;
            }

            int nalEnd = nextStartCodeOffset;
            while (nalEnd > nalStart &&
                   annexBAccessUnit[nalEnd - 1] == 0)
            {
                nalEnd--;
            }
            if (nalEnd > nalStart)
            {
                ReadOnlySpan<byte> nal =
                    annexBAccessUnit[
                        nalStart..nalEnd];
                int nalType = nal[0] & 0x1F;
                containsIdr |= nalType == 5;
                CaptureParameterSet(nalType, nal);
                WriteLengthPrefixedNal(nal);
                nalCount++;
            }

            if (nextStartCodeOffset ==
                annexBAccessUnit.Length)
            {
                break;
            }
            searchOffset = nextStartCodeOffset;
        }

        if (nalCount == 0)
        {
            throw new InvalidDataException(
                "The V4L2 H.264 access unit contains no Annex-B NAL unit.");
        }

        long length = checked(
            _stream.Position - sourceOffset);
        if (length > int.MaxValue)
        {
            throw new InvalidDataException(
                "An encoded access unit exceeds the ISO-BMFF sample limit.");
        }
        _samples.Add(
            new PendingSample(
                sourceOffset,
                checked((int)length),
                ToTrackTime(presentationTime),
                isKeyFrame || containsIdr));
    }

    internal IsoBmffCompositionPlan CreatePlan()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Flush(flushToDisk: false);
        if (_samples.Count == 0)
        {
            throw new InvalidDataException(
                "The hardware encoder produced no H.264 samples.");
        }
        if (_sequenceParameterSet is null ||
            _pictureParameterSet is null)
        {
            throw new InvalidDataException(
                "The H.264 encoder output did not expose SPS and PPS parameter sets.");
        }

        long firstPresentation =
            _samples.Min(
                static sample =>
                    sample.PresentationTime);
        var samples =
            new IsoBmffCompositionSample[
                _samples.Count];
        for (int index = 0;
             index < samples.Length;
             index++)
        {
            PendingSample pending =
                _samples[index];
            long decodeTime = checked(
                (long)index * _sampleDuration);
            long presentationTime = checked(
                pending.PresentationTime -
                firstPresentation);
            long compositionOffset = checked(
                presentationTime - decodeTime);
            if (compositionOffset is
                < int.MinValue or
                > int.MaxValue)
            {
                throw new InvalidDataException(
                    "An encoder timestamp exceeds the signed ISO-BMFF composition-offset range.");
            }
            samples[index] =
                new IsoBmffCompositionSample(
                    _path,
                    pending.SourceOffset,
                    pending.Size,
                    _sampleDuration,
                    checked((int)compositionOffset),
                    pending.IsSync);
        }

        var video =
            new IsoBmffCompositionTrack(
                IsoBmffTrackKind.Video,
                VideoTimescale,
                _width,
                _height,
                SampleEntryType: 0x6176_6331,
                BuildVisualSampleEntryPayload(
                    _width,
                    _height,
                    _sequenceParameterSet,
                    _pictureParameterSet),
                samples);
        return new IsoBmffCompositionPlan(
            video,
            Audio: null);
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

    private void CaptureParameterSet(
        int nalType,
        ReadOnlySpan<byte> nal)
    {
        if (nalType is not (7 or 8))
        {
            return;
        }
        if (nal.Length > MaximumParameterSetBytes)
        {
            throw new InvalidDataException(
                "An H.264 parameter set exceeds the AVC configuration-record limit.");
        }
        if (nalType == 7 &&
            _sequenceParameterSet is null)
        {
            _sequenceParameterSet = nal.ToArray();
        }
        else if (nalType == 8 &&
                 _pictureParameterSet is null)
        {
            _pictureParameterSet = nal.ToArray();
        }
    }

    private void WriteLengthPrefixedNal(
        ReadOnlySpan<byte> nal)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(
            length,
            checked((uint)nal.Length));
        _stream.Write(length);
        _stream.Write(nal);
    }

    private static byte[] BuildVisualSampleEntryPayload(
        ushort width,
        ushort height,
        byte[] sequenceParameterSet,
        byte[] pictureParameterSet)
    {
        if (sequenceParameterSet.Length < 4)
        {
            throw new InvalidDataException(
                "The H.264 SPS is too short to create an AVC configuration record.");
        }

        int configurationLength = checked(
            11 +
            sequenceParameterSet.Length +
            pictureParameterSet.Length);
        int avcBoxLength = checked(
            configurationLength + 8);
        var payload = new byte[
            checked(
                VisualSampleEntryHeaderSize +
                avcBoxLength)];
        Span<byte> header =
            payload.AsSpan(
                0,
                VisualSampleEntryHeaderSize);
        BinaryPrimitives.WriteUInt16BigEndian(
            header[6..],
            1);
        BinaryPrimitives.WriteUInt16BigEndian(
            header[24..],
            width);
        BinaryPrimitives.WriteUInt16BigEndian(
            header[26..],
            height);
        BinaryPrimitives.WriteUInt32BigEndian(
            header[28..],
            0x0048_0000);
        BinaryPrimitives.WriteUInt32BigEndian(
            header[32..],
            0x0048_0000);
        BinaryPrimitives.WriteUInt16BigEndian(
            header[40..],
            1);
        BinaryPrimitives.WriteUInt16BigEndian(
            header[74..],
            0x0018);
        BinaryPrimitives.WriteUInt16BigEndian(
            header[76..],
            ushort.MaxValue);

        Span<byte> avcBox =
            payload.AsSpan(
                VisualSampleEntryHeaderSize,
                avcBoxLength);
        BinaryPrimitives.WriteUInt32BigEndian(
            avcBox,
            checked((uint)avcBoxLength));
        avcBox[4] = (byte)'a';
        avcBox[5] = (byte)'v';
        avcBox[6] = (byte)'c';
        avcBox[7] = (byte)'C';
        Span<byte> configuration = avcBox[8..];
        configuration[0] = 1;
        configuration[1] = sequenceParameterSet[1];
        configuration[2] = sequenceParameterSet[2];
        configuration[3] = sequenceParameterSet[3];
        configuration[4] = 0xFF;
        configuration[5] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(
            configuration[6..],
            checked((ushort)sequenceParameterSet.Length));
        sequenceParameterSet.CopyTo(
            configuration[8..]);
        int ppsOffset = checked(
            8 + sequenceParameterSet.Length);
        configuration[ppsOffset] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(
            configuration[(ppsOffset + 1)..],
            checked((ushort)pictureParameterSet.Length));
        pictureParameterSet.CopyTo(
            configuration[(ppsOffset + 3)..]);
        return payload;
    }

    private static bool TryFindStartCode(
        ReadOnlySpan<byte> source,
        int start,
        out int offset,
        out int length)
    {
        for (int index = Math.Max(0, start);
             index + 2 < source.Length;
             index++)
        {
            if (source[index] != 0 ||
                source[index + 1] != 0)
            {
                continue;
            }
            if (source[index + 2] == 1)
            {
                offset = index;
                length = 3;
                return true;
            }
            if (index + 3 < source.Length &&
                source[index + 2] == 0 &&
                source[index + 3] == 1)
            {
                offset = index;
                length = 4;
                return true;
            }
        }
        offset = 0;
        length = 0;
        return false;
    }

    private static long ToTrackTime(
        TimeSpan time) =>
        checked(
            (long)Math.Round(
                time.TotalSeconds *
                VideoTimescale,
                MidpointRounding.AwayFromZero));

    private readonly record struct PendingSample(
        long SourceOffset,
        int Size,
        long PresentationTime,
        bool IsSync);
}
