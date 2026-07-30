using System.Buffers;
using System.Buffers.Binary;
using ProGPU.Media.Containers;
using System.Diagnostics;

namespace ProGPU.Linux.Media;

/// <summary>
/// Converts supported ISO-BMFF signed integer PCM samples to interleaved
/// float32 for the native PipeWire ring. Work is O(S) for S scalar samples,
/// pooled storage is bounded by the largest indexed media sample, and steady
/// reads allocate no managed objects.
/// </summary>
internal sealed class IsoBmffPcmSampleReader :
    IDisposable
{
    private const int MaximumSampleBytes =
        256 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly IsoBmffTrack _track;
    private byte[]? _input;
    private float[]? _output;
    private int _length;

    internal IsoBmffPcmSampleReader(
        Stream stream,
        IsoBmffTrack track)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(track);
        if (!stream.CanRead ||
            !stream.CanSeek)
        {
            throw new ArgumentException(
                "PCM reads require a readable, seekable stream.",
                nameof(stream));
        }
        if (track.Codec !=
                IsoBmffCodec.Pcm ||
            track.PcmEncoding ==
                IsoBmffPcmEncoding.Unknown ||
            track.AudioChannelCount is 0 or > 8 ||
            track.AudioSampleRate is < 8_000 or >
                384_000 ||
            track.AudioBitsPerSample is not
                (16 or 24 or 32))
        {
            throw new NotSupportedException(
                "The built-in PCM reader accepts version-zero sowt/twos signed 16-, 24-, or 32-bit audio with one to eight channels.");
        }

        _stream = stream;
        _track = track;
        int largest = 1;
        foreach (IsoBmffSample sample in
                 track.Samples)
        {
            largest =
                Math.Max(
                    largest,
                    sample.Size);
        }
        if (largest >
            MaximumSampleBytes)
        {
            throw new InvalidDataException(
                "An indexed PCM sample exceeds the bounded audio workspace.");
        }
        int bytesPerScalar =
            track.AudioBitsPerSample / 8;
        if (largest % bytesPerScalar != 0)
        {
            throw new InvalidDataException(
                "PCM sample byte sizes must align to the declared scalar width.");
        }
        _input =
            ArrayPool<byte>.Shared.Rent(
                largest);
        _output =
            ArrayPool<float>.Shared.Rent(
                Math.Max(
                    1,
                    largest /
                    bytesPerScalar));
    }

    internal ReadOnlySpan<float> Current =>
        (_output ??
         throw new ObjectDisposedException(
             nameof(IsoBmffPcmSampleReader)))
        .AsSpan(0, _length);

    internal ReadOnlySpan<float> Read(
        int sampleIndex)
    {
        byte[] input =
            _input ??
            throw new ObjectDisposedException(
                nameof(IsoBmffPcmSampleReader));
        float[] output =
            _output ??
            throw new ObjectDisposedException(
                nameof(IsoBmffPcmSampleReader));
        if ((uint)sampleIndex >=
            _track.Samples.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleIndex));
        }

        IsoBmffSample sample =
            _track.Samples[sampleIndex];
        int scalarBytes =
            _track.AudioBitsPerSample / 8;
        int frameBytes = checked(
            scalarBytes *
            _track.AudioChannelCount);
        if (sample.Size % frameBytes != 0)
        {
            throw new InvalidDataException(
                "An ISO-BMFF PCM sample does not contain complete interleaved frames.");
        }

        _stream.Position = sample.Offset;
        _stream.ReadExactly(
            input.AsSpan(0, sample.Size));
        int scalars =
            sample.Size /
            scalarBytes;
        bool littleEndian =
            _track.PcmEncoding ==
            IsoBmffPcmEncoding
                .SignedLittleEndian;
        for (int index = 0;
             index < scalars;
             index++)
        {
            ReadOnlySpan<byte> source =
                input.AsSpan(
                    index * scalarBytes,
                    scalarBytes);
            output[index] =
                scalarBytes switch
                {
                    2 => ReadInt16(
                             source,
                             littleEndian) /
                         32768f,
                    3 => ReadInt24(
                             source,
                             littleEndian) /
                         8_388_608f,
                    4 => ReadInt32(
                             source,
                             littleEndian) /
                         2_147_483_648f,
                    _ => throw new
                        UnreachableException()
                };
        }
        _length = scalars;
        return output.AsSpan(
            0,
            scalars);
    }

    public void Dispose()
    {
        byte[]? input =
            Interlocked.Exchange(
                ref _input,
                null);
        float[]? output =
            Interlocked.Exchange(
                ref _output,
                null);
        _length = 0;
        if (input is not null)
        {
            ArrayPool<byte>.Shared.Return(
                input);
        }
        if (output is not null)
        {
            ArrayPool<float>.Shared.Return(
                output);
        }
    }

    private static short ReadInt16(
        ReadOnlySpan<byte> value,
        bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives
                .ReadInt16LittleEndian(value)
            : BinaryPrimitives
                .ReadInt16BigEndian(value);

    private static int ReadInt24(
        ReadOnlySpan<byte> value,
        bool littleEndian)
    {
        int result =
            littleEndian
                ? value[0] |
                  value[1] << 8 |
                  value[2] << 16
                : value[2] |
                  value[1] << 8 |
                  value[0] << 16;
        return (result & 0x0080_0000) != 0
            ? result |
              unchecked((int)0xFF00_0000)
            : result;
    }

    private static int ReadInt32(
        ReadOnlySpan<byte> value,
        bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives
                .ReadInt32LittleEndian(value)
            : BinaryPrimitives
                .ReadInt32BigEndian(value);
}
