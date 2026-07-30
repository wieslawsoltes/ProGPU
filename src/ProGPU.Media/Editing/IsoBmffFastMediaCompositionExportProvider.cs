using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using ProGPU.Media.Containers;

namespace ProGPU.Media.Editing;

/// <summary>
/// Dependency-free ISO-BMFF fast editor for compatible H.264/AAC sources.
/// Compressed access units are copied without decode or re-encode. Planning is
/// O(C + S) time and O(S) storage for C clips and S selected samples; writing
/// is O(B + S) time for B compressed bytes with one bounded pooled buffer.
/// </summary>
public sealed class IsoBmffFastMediaCompositionExportProvider :
    IMediaCompositionExportProvider,
    IMediaCompositionExportCapabilityProvider
{
    private const long MaximumRemoteSourceBytes =
        64L * 1024 * 1024 * 1024;
    private static readonly HttpClient s_httpClient = new();

    public IsoBmffFastMediaCompositionExportProvider(
        int priority = 50)
    {
        Priority = priority;
    }

    public string Id => "progpu.isobmff.fast-export";
    public int Priority { get; }

    public bool CanRender(MediaCompositionExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TrimmingMode !=
                MediaCompositionTrimmingMode.Fast ||
            request.Clips.Count == 0 ||
            request.BackgroundAudioTracks.Count != 0 ||
            request.OverlayLayers.Count != 0 ||
            !string.Equals(
                request.EncodingProfile.ContainerSubtype,
                "MPEG4",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.EncodingProfile.VideoSubtype,
                "H264",
                StringComparison.OrdinalIgnoreCase) ||
            request.EncodingProfile.AudioSubtype is not null &&
            !string.Equals(
                request.EncodingProfile.AudioSubtype,
                "AAC",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int index = 0; index < request.Clips.Count; index++)
        {
            MediaCompositionExportClip clip = request.Clips[index];
            if (clip.SourceUri is not { } source ||
                !IsSeekableSource(source) ||
                clip.ArgbColor is not null ||
                clip.Volume != 1d ||
                clip.AudioEffectDefinitions.Count != 0 ||
                clip.VideoEffectDefinitions.Count != 0 ||
                HasNonIdentityWebGpuEffect(clip.UserData))
            {
                return false;
            }
        }
        return true;
    }

    public MediaCompositionExportCapabilities GetCapabilities(
        MediaCompositionExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanRender(request))
        {
            throw new ArgumentException(
                "The request is not supported by this provider.",
                nameof(request));
        }

        return new MediaCompositionExportCapabilities(
            Id,
            MediaCompositionExportVideoPath.CompressedSampleCopy,
            request.EncodingProfile.AudioSubtype is null
                ? MediaCompositionExportAudioPath.None
                : MediaCompositionExportAudioPath
                    .CompressedSampleCopy,
            HardwareVideoEncoderRequested: false,
            HardwareVideoEncoderGuaranteed: false,
            EffectsBakedOnGpu: false,
            Limitation:
                "Fast export copies compatible compressed H.264/AAC " +
                "samples and cannot bake effects, overlays, mixing, " +
                "gain, or frame-exact edits.");
    }

    public async ValueTask<MediaCompositionExportFailure> RenderAsync(
        MediaCompositionExportRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRender(request))
        {
            return MediaCompositionExportFailure.InvalidProfile;
        }

        string destination = Path.GetFullPath(
            request.DestinationPath);
        string? directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory))
        {
            return MediaCompositionExportFailure.InvalidProfile;
        }
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        DirectoryInfo? stagingDirectory = null;

        try
        {
            MediaCompositionExportRequest seekableRequest =
                request;
            if (request.Clips.Any(
                    static clip =>
                        clip.SourceUri is
                            { IsFile: false }))
            {
                stagingDirectory =
                    Directory.CreateTempSubdirectory(
                        "progpu-media-export-");
                seekableRequest =
                    await StageRemoteSourcesAsync(
                            request,
                            stagingDirectory.FullName,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            IsoBmffCompositionPlan plan =
                IsoBmffCompositionPlanner.Create(
                    seekableRequest);
            await IsoBmffCompositionWriter.WriteAsync(
                    plan,
                    temporary,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
            progress?.Report(100d);
            return MediaCompositionExportFailure.None;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(temporary);
            throw;
        }
        catch (InvalidDataException)
        {
            TryDelete(temporary);
            return MediaCompositionExportFailure.InvalidProfile;
        }
        catch (NotSupportedException)
        {
            TryDelete(temporary);
            return MediaCompositionExportFailure.CodecNotFound;
        }
        catch
        {
            TryDelete(temporary);
            return MediaCompositionExportFailure.Unknown;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static bool HasNonIdentityWebGpuEffect(
        IReadOnlyDictionary<string, string> userData) =>
        userData.TryGetValue(
            "progpu.saturation",
            out string? saturation) &&
        !string.Equals(saturation, "1", StringComparison.Ordinal) ||
        userData.TryGetValue(
            "progpu.grayscale",
            out string? grayscale) &&
        !string.Equals(grayscale, "0", StringComparison.Ordinal);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup must not hide the export result.
        }
    }

    private static bool IsSeekableSource(Uri source) =>
        source.IsFile ||
        source.Scheme.Equals(
            Uri.UriSchemeHttp,
            StringComparison.OrdinalIgnoreCase) ||
        source.Scheme.Equals(
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);

    private static async Task<MediaCompositionExportRequest>
        StageRemoteSourcesAsync(
            MediaCompositionExportRequest request,
            string stagingDirectory,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
    {
        var stagedUris =
            new Dictionary<string, Uri>(
                StringComparer.Ordinal);
        var clips =
            new MediaCompositionExportClip[
                request.Clips.Count];
        int remoteCount = request.Clips.Count(
            static clip =>
                clip.SourceUri is { IsFile: false });
        int completed = 0;
        for (int index = 0;
             index < request.Clips.Count;
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaCompositionExportClip clip =
                request.Clips[index];
            Uri source = clip.SourceUri!;
            if (source.IsFile)
            {
                clips[index] = clip;
                continue;
            }

            if (!stagedUris.TryGetValue(
                    source.AbsoluteUri,
                    out Uri? staged))
            {
                string path = Path.Combine(
                    stagingDirectory,
                    $"{stagedUris.Count:D4}.mp4");
                await DownloadSourceAsync(
                        source,
                        path,
                        cancellationToken)
                    .ConfigureAwait(false);
                staged = new Uri(path);
                stagedUris.Add(
                    source.AbsoluteUri,
                    staged);
                completed++;
                progress?.Report(
                    remoteCount == 0
                        ? 0d
                        : completed * 5d /
                          remoteCount);
            }
            clips[index] = clip with
            {
                SourceUri = staged
            };
        }

        return request with
        {
            Clips = Array.AsReadOnly(clips)
        };
    }

    private static async Task DownloadSourceAsync(
        Uri source,
        string destination,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response =
            await s_httpClient.GetAsync(
                    source,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is
                long declaredLength &&
            declaredLength > MaximumRemoteSourceBytes)
        {
            throw new InvalidDataException(
                "A remote media source exceeds the bounded staging limit.");
        }

        await using Stream input =
            await response.Content.ReadAsStreamAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            256 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        byte[] buffer =
            ArrayPool<byte>.Shared.Rent(256 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(
                        buffer,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                total = checked(total + read);
                if (total > MaximumRemoteSourceBytes)
                {
                    throw new InvalidDataException(
                        "A remote media source exceeds the bounded staging limit.");
                }
                await output.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDeleteDirectory(
        DirectoryInfo? directory)
    {
        if (directory is null)
        {
            return;
        }
        try
        {
            directory.Delete(recursive: true);
        }
        catch
        {
            // Best-effort cleanup must not hide the export result.
        }
    }
}

internal readonly record struct IsoBmffCompositionSample(
    string SourcePath,
    long SourceOffset,
    int Size,
    int Duration,
    int CompositionOffset,
    bool IsSync);

/// <summary>
/// Maps one movie-timeline segment to media time. A media time of -1 is an
/// ISO-BMFF empty edit and advances presentation without presenting a sample.
/// Segment duration uses <see cref="IsoBmffCompositionWriter.MovieTimescale"/>.
/// </summary>
internal readonly record struct IsoBmffCompositionEdit(
    ulong SegmentDuration,
    long MediaTime);

internal sealed record IsoBmffCompositionTrack(
    IsoBmffTrackKind Kind,
    uint Timescale,
    ushort Width,
    ushort Height,
    uint SampleEntryType,
    byte[] SampleEntryPayload,
    IsoBmffCompositionSample[] Samples)
{
    public long Duration { get; } =
        Samples.Aggregate(
            0L,
            static (duration, sample) =>
                checked(duration + sample.Duration));

    public IsoBmffCompositionEdit[] Edits
    {
        get;
        init;
    } = [];
}

internal sealed record IsoBmffCompositionPlan(
    IsoBmffCompositionTrack Video,
    IsoBmffCompositionTrack? Audio);

internal static class IsoBmffCompositionPlanner
{
    public static IsoBmffCompositionPlan Create(
        MediaCompositionExportRequest request)
    {
        var videoSamples =
            new List<IsoBmffCompositionSample>();
        var audioSamples =
            new List<IsoBmffCompositionSample>();
        IsoBmffTrack? videoTemplate = null;
        IsoBmffTrack? audioTemplate = null;
        bool sawAudio = false;
        bool sawMissingAudio = false;

        for (int clipIndex = 0;
             clipIndex < request.Clips.Count;
             clipIndex++)
        {
            MediaCompositionExportClip clip =
                request.Clips[clipIndex];
            string path = Path.GetFullPath(
                clip.SourceUri!.LocalPath);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.RandomAccess);
            IsoBmffMovie movie =
                new IsoBmffDemuxer(stream).Parse();
            IsoBmffTrack video =
                movie.Tracks.FirstOrDefault(
                    static track =>
                        track.Kind == IsoBmffTrackKind.Video &&
                        track.Codec == IsoBmffCodec.H264) ??
                throw new NotSupportedException(
                    "Fast export requires an H.264 video track.");
            IsoBmffTrack? audio =
                request.EncodingProfile.AudioSubtype is null
                    ? null
                    : movie.Tracks.FirstOrDefault(
                        static track =>
                            track.Kind ==
                                IsoBmffTrackKind.Audio &&
                            track.Codec ==
                                IsoBmffCodec.Aac);

            ValidateTemplate(videoTemplate, video);
            videoTemplate ??= video;

            (int firstVideo, int lastVideo) =
                SelectVideoRange(video, clip);
            IsoBmffSample first =
                video.Samples[firstVideo];
            IsoBmffSample last =
                video.Samples[lastVideo];
            long firstPresentation =
                first.PresentationTime;
            long segmentEnd =
                checked(
                    last.PresentationTime +
                    last.Duration);
            AppendSamples(
                path,
                video.Samples,
                firstVideo,
                lastVideo,
                firstPresentation,
                videoSamples);

            if (request.EncodingProfile.AudioSubtype is null)
            {
                continue;
            }
            if (audio is null)
            {
                sawMissingAudio = true;
                continue;
            }

            sawAudio = true;
            ValidateTemplate(audioTemplate, audio);
            audioTemplate ??= audio;
            long audioStart = ScaleTime(
                firstPresentation,
                video.Timescale,
                audio.Timescale);
            long audioEnd = ScaleTime(
                segmentEnd,
                video.Timescale,
                audio.Timescale);
            (int firstAudio, int lastAudio) =
                SelectAudioRange(
                    audio,
                    audioStart,
                    audioEnd);
            if (firstAudio >= 0)
            {
                AppendSamples(
                    path,
                    audio.Samples,
                    firstAudio,
                    lastAudio,
                    audio.Samples[firstAudio]
                        .PresentationTime,
                    audioSamples);
            }
        }

        if (videoTemplate is null || videoSamples.Count == 0)
        {
            throw new InvalidDataException(
                "The composition contains no exportable video samples.");
        }
        if (sawAudio && sawMissingAudio)
        {
            throw new NotSupportedException(
                "Fast export cannot synthesize a missing audio track between AAC clips.");
        }

        var videoTrack = CreateTrack(
            videoTemplate,
            videoSamples);
        IsoBmffCompositionTrack? audioTrack =
            sawAudio && audioTemplate is not null
                ? CreateTrack(audioTemplate, audioSamples)
                : null;
        return new IsoBmffCompositionPlan(
            videoTrack,
            audioTrack);
    }

    private static (int First, int Last) SelectVideoRange(
        IsoBmffTrack track,
        MediaCompositionExportClip clip)
    {
        long start = ToTrackTime(
            clip.TrimTimeFromStart,
            track.Timescale);
        long sourceDuration =
            track.Duration > 0
                ? track.Duration
                : checked(
                    track.Samples[^1].PresentationTime +
                    track.Samples[^1].Duration);
        long end = checked(
            sourceDuration -
            ToTrackTime(
                clip.TrimTimeFromEnd,
                track.Timescale));
        if (end <= start)
        {
            throw new InvalidDataException(
                "A trimmed clip has no remaining duration.");
        }

        int first = -1;
        for (int index = 0;
             index < track.Samples.Length;
             index++)
        {
            IsoBmffSample sample = track.Samples[index];
            if (sample.IsSync &&
                sample.PresentationTime >= start)
            {
                first = index;
                break;
            }
        }
        if (first < 0)
        {
            throw new InvalidDataException(
                "No key frame exists at or after the requested fast-trim start.");
        }

        int last = first - 1;
        for (int index = first;
             index < track.Samples.Length;
             index++)
        {
            if (track.Samples[index].PresentationTime >= end)
            {
                break;
            }
            last = index;
        }
        if (last < first)
        {
            throw new InvalidDataException(
                "The key-frame-aligned trim range is empty.");
        }
        return (first, last);
    }

    private static (int First, int Last) SelectAudioRange(
        IsoBmffTrack track,
        long start,
        long end)
    {
        int first = -1;
        int last = -1;
        for (int index = 0;
             index < track.Samples.Length;
             index++)
        {
            IsoBmffSample sample = track.Samples[index];
            if (sample.PresentationTime < start)
            {
                continue;
            }
            if (sample.PresentationTime >= end)
            {
                break;
            }
            first = first < 0 ? index : first;
            last = index;
        }
        return (first, last);
    }

    private static void AppendSamples(
        string sourcePath,
        IsoBmffSample[] source,
        int first,
        int last,
        long firstPresentation,
        List<IsoBmffCompositionSample> destination)
    {
        long firstDecode = source[first].DecodeTime;
        for (int index = first; index <= last; index++)
        {
            IsoBmffSample sample = source[index];
            long relativeDecode =
                checked(sample.DecodeTime - firstDecode);
            long relativePresentation =
                checked(
                    sample.PresentationTime -
                    firstPresentation);
            long compositionOffset =
                checked(
                    relativePresentation -
                    relativeDecode);
            if (compositionOffset is < int.MinValue or > int.MaxValue)
            {
                throw new InvalidDataException(
                    "A composition offset exceeds ISO-BMFF version-1 ctts range.");
            }
            destination.Add(
                new IsoBmffCompositionSample(
                    sourcePath,
                    sample.Offset,
                    sample.Size,
                    sample.Duration,
                    (int)compositionOffset,
                    sample.IsSync));
        }
    }

    private static IsoBmffCompositionTrack CreateTrack(
        IsoBmffTrack template,
        List<IsoBmffCompositionSample> samples)
    {
        if (template.SampleEntryType == 0 ||
            template.SampleEntryPayload.Length == 0)
        {
            throw new InvalidDataException(
                "The source sample entry is unavailable.");
        }
        return new IsoBmffCompositionTrack(
            template.Kind,
            template.Timescale,
            template.Width,
            template.Height,
            template.SampleEntryType,
            template.SampleEntryPayload,
            samples.ToArray());
    }

    private static void ValidateTemplate(
        IsoBmffTrack? expected,
        IsoBmffTrack actual)
    {
        if (expected is null)
        {
            return;
        }
        if (expected.Codec != actual.Codec ||
            expected.Timescale != actual.Timescale ||
            expected.Width != actual.Width ||
            expected.Height != actual.Height ||
            expected.SampleEntryType !=
                actual.SampleEntryType ||
            !expected.SampleEntryPayload.AsSpan()
                .SequenceEqual(actual.SampleEntryPayload))
        {
            throw new NotSupportedException(
                "Fast export requires identical codec sample entries, dimensions, and timescales.");
        }
    }

    private static long ToTrackTime(
        TimeSpan time,
        uint timescale) =>
        checked(
            (long)Math.Round(
                time.TotalSeconds * timescale,
                MidpointRounding.AwayFromZero));

    private static long ScaleTime(
        long value,
        uint sourceTimescale,
        uint destinationTimescale) =>
        checked(
            (long)Math.Round(
                value *
                ((double)destinationTimescale /
                 sourceTimescale),
                MidpointRounding.AwayFromZero));
}

internal static class IsoBmffCompositionWriter
{
    internal const uint MovieTimescale = 1_000;
    private const int CopyBufferSize = 256 * 1024;

    public static async Task WriteAsync(
        IsoBmffCompositionPlan plan,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var tracks = plan.Audio is null
            ? new[] { plan.Video }
            : new[] { plan.Video, plan.Audio };
        long payloadBytes = 0;
        for (int trackIndex = 0;
             trackIndex < tracks.Length;
             trackIndex++)
        {
            IsoBmffCompositionTrack track = tracks[trackIndex]!;
            for (int sampleIndex = 0;
                 sampleIndex < track.Samples.Length;
                 sampleIndex++)
            {
                payloadBytes = checked(
                    payloadBytes +
                    track.Samples[sampleIndex].Size);
            }
        }

        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        WriteFileType(output);
        WriteUInt32(output, 1);
        WriteFourCc(output, "mdat");
        WriteUInt64(
            output,
            checked((ulong)payloadBytes + 16));

        var offsets =
            new Dictionary<IsoBmffCompositionTrack, long[]>(
                ReferenceEqualityComparer.Instance);
        var handles =
            new Dictionary<string, SafeFileHandle>(
                StringComparer.Ordinal);
        byte[] buffer =
            ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long copied = 0;
        try
        {
            for (int trackIndex = 0;
                 trackIndex < tracks.Length;
                 trackIndex++)
            {
                IsoBmffCompositionTrack track =
                    tracks[trackIndex]!;
                var trackOffsets =
                    new long[track.Samples.Length];
                offsets.Add(track, trackOffsets);
                for (int sampleIndex = 0;
                     sampleIndex < track.Samples.Length;
                     sampleIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IsoBmffCompositionSample sample =
                        track.Samples[sampleIndex];
                    trackOffsets[sampleIndex] = output.Position;
                    if (!handles.TryGetValue(
                            sample.SourcePath,
                            out SafeFileHandle? handle))
                    {
                        handle = File.OpenHandle(
                            sample.SourcePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            FileOptions.Asynchronous |
                            FileOptions.RandomAccess);
                        handles.Add(sample.SourcePath, handle);
                    }
                    await CopySampleAsync(
                            handle,
                            sample.SourceOffset,
                            sample.Size,
                            output,
                            buffer,
                            cancellationToken)
                        .ConfigureAwait(false);
                    copied = checked(copied + sample.Size);
                    progress?.Report(
                        payloadBytes == 0
                            ? 0d
                            : copied * 95d / payloadBytes);
                }
            }
        }
        finally
        {
            foreach (SafeFileHandle handle in handles.Values)
            {
                handle.Dispose();
            }
            ArrayPool<byte>.Shared.Return(buffer);
        }

        using MemoryStream movie =
            BuildMovie(plan, offsets);
        movie.Position = 0;
        await movie.CopyToAsync(
                output,
                CopyBufferSize,
                cancellationToken)
            .ConfigureAwait(false);
        await output.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(99d);
    }

    private static async Task CopySampleAsync(
        SafeFileHandle source,
        long sourceOffset,
        int length,
        FileStream destination,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int remaining = length;
        long position = sourceOffset;
        while (remaining > 0)
        {
            int count = Math.Min(remaining, buffer.Length);
            int read = await RandomAccess.ReadAsync(
                    source,
                    buffer.AsMemory(0, count),
                    position,
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "A source media sample is truncated.");
            }
            await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken)
                .ConfigureAwait(false);
            position = checked(position + read);
            remaining -= read;
        }
    }

    private static MemoryStream BuildMovie(
        IsoBmffCompositionPlan plan,
        Dictionary<IsoBmffCompositionTrack, long[]> offsets)
    {
        var stream = new MemoryStream();
        long movie = BeginBox(stream, "moov");
        WriteMovieHeader(
            stream,
            Math.Max(
                ToMovieDuration(plan.Video),
                plan.Audio is null
                    ? 0
                    : ToMovieDuration(plan.Audio)),
            plan.Audio is null ? 2u : 3u);
        WriteTrack(
            stream,
            plan.Video,
            offsets[plan.Video],
            trackId: 1);
        if (plan.Audio is not null)
        {
            WriteTrack(
                stream,
                plan.Audio,
                offsets[plan.Audio],
                trackId: 2);
        }
        EndBox(stream, movie);
        return stream;
    }

    private static void WriteFileType(Stream stream)
    {
        long box = BeginBox(stream, "ftyp");
        WriteFourCc(stream, "isom");
        WriteUInt32(stream, 512);
        WriteFourCc(stream, "isom");
        WriteFourCc(stream, "iso2");
        WriteFourCc(stream, "mp41");
        WriteFourCc(stream, "avc1");
        EndBox(stream, box);
    }

    private static void WriteMovieHeader(
        Stream stream,
        ulong duration,
        uint nextTrackId)
    {
        long box = BeginFullBox(stream, "mvhd", version: 1);
        WriteUInt64(stream, 0);
        WriteUInt64(stream, 0);
        WriteUInt32(stream, MovieTimescale);
        WriteUInt64(stream, duration);
        WriteUInt32(stream, 0x0001_0000);
        WriteUInt16(stream, 0x0100);
        WriteZeros(stream, 10);
        WriteIdentityMatrix(stream);
        WriteZeros(stream, 24);
        WriteUInt32(stream, nextTrackId);
        EndBox(stream, box);
    }

    private static void WriteTrack(
        Stream stream,
        IsoBmffCompositionTrack track,
        long[] offsets,
        uint trackId)
    {
        long trak = BeginBox(stream, "trak");
        WriteTrackHeader(stream, track, trackId);
        WriteEditList(stream, track.Edits);
        long mdia = BeginBox(stream, "mdia");
        WriteMediaHeader(stream, track);
        WriteHandler(stream, track.Kind);
        WriteMediaInformation(stream, track, offsets);
        EndBox(stream, mdia);
        EndBox(stream, trak);
    }

    private static void WriteEditList(
        Stream stream,
        IsoBmffCompositionEdit[] edits)
    {
        if (edits.Length == 0)
        {
            return;
        }
        if (edits[^1].MediaTime == -1)
        {
            throw new InvalidDataException(
                "The final ISO-BMFF edit cannot be empty.");
        }

        long edts = BeginBox(stream, "edts");
        long elst = BeginFullBox(
            stream,
            "elst",
            version: 1);
        WriteUInt32(
            stream,
            checked((uint)edits.Length));
        for (int index = 0;
             index < edits.Length;
             index++)
        {
            IsoBmffCompositionEdit edit =
                edits[index];
            if (edit.SegmentDuration == 0 ||
                edit.MediaTime < -1)
            {
                throw new InvalidDataException(
                    "An ISO-BMFF composition edit has an invalid duration or media time.");
            }
            WriteUInt64(
                stream,
                edit.SegmentDuration);
            WriteInt64(
                stream,
                edit.MediaTime);
            WriteUInt16(stream, 1);
            WriteUInt16(stream, 0);
        }
        EndBox(stream, elst);
        EndBox(stream, edts);
    }

    private static void WriteTrackHeader(
        Stream stream,
        IsoBmffCompositionTrack track,
        uint trackId)
    {
        long box = BeginFullBox(
            stream,
            "tkhd",
            version: 1,
            flags: 7);
        WriteUInt64(stream, 0);
        WriteUInt64(stream, 0);
        WriteUInt32(stream, trackId);
        WriteUInt32(stream, 0);
        WriteUInt64(stream, ToMovieDuration(track));
        WriteZeros(stream, 8);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(
            stream,
            track.Kind == IsoBmffTrackKind.Audio
                ? (ushort)0x0100
                : (ushort)0);
        WriteUInt16(stream, 0);
        WriteIdentityMatrix(stream);
        WriteUInt32(
            stream,
            checked((uint)track.Width << 16));
        WriteUInt32(
            stream,
            checked((uint)track.Height << 16));
        EndBox(stream, box);
    }

    private static void WriteMediaHeader(
        Stream stream,
        IsoBmffCompositionTrack track)
    {
        long box = BeginFullBox(stream, "mdhd", version: 1);
        WriteUInt64(stream, 0);
        WriteUInt64(stream, 0);
        WriteUInt32(stream, track.Timescale);
        WriteUInt64(stream, checked((ulong)track.Duration));
        WriteUInt16(stream, 0x55C4);
        WriteUInt16(stream, 0);
        EndBox(stream, box);
    }

    private static void WriteHandler(
        Stream stream,
        IsoBmffTrackKind kind)
    {
        long box = BeginFullBox(stream, "hdlr");
        WriteUInt32(stream, 0);
        WriteFourCc(
            stream,
            kind == IsoBmffTrackKind.Video
                ? "vide"
                : "soun");
        WriteZeros(stream, 12);
        byte[] name =
            System.Text.Encoding.ASCII.GetBytes(
                kind == IsoBmffTrackKind.Video
                    ? "ProGPU Video\0"
                    : "ProGPU Audio\0");
        stream.Write(name);
        EndBox(stream, box);
    }

    private static void WriteMediaInformation(
        Stream stream,
        IsoBmffCompositionTrack track,
        long[] offsets)
    {
        long minf = BeginBox(stream, "minf");
        if (track.Kind == IsoBmffTrackKind.Video)
        {
            long vmhd = BeginFullBox(
                stream,
                "vmhd",
                flags: 1);
            WriteZeros(stream, 8);
            EndBox(stream, vmhd);
        }
        else
        {
            long smhd = BeginFullBox(stream, "smhd");
            WriteZeros(stream, 4);
            EndBox(stream, smhd);
        }
        WriteDataInformation(stream);
        WriteSampleTable(stream, track, offsets);
        EndBox(stream, minf);
    }

    private static void WriteDataInformation(Stream stream)
    {
        long dinf = BeginBox(stream, "dinf");
        long dref = BeginFullBox(stream, "dref");
        WriteUInt32(stream, 1);
        long url = BeginFullBox(
            stream,
            "url ",
            flags: 1);
        EndBox(stream, url);
        EndBox(stream, dref);
        EndBox(stream, dinf);
    }

    private static void WriteSampleTable(
        Stream stream,
        IsoBmffCompositionTrack track,
        long[] offsets)
    {
        long stbl = BeginBox(stream, "stbl");
        WriteSampleDescription(stream, track);
        WriteTimeToSample(stream, track.Samples);
        WriteCompositionTime(stream, track.Samples);
        WriteSampleToChunk(stream);
        WriteSampleSizes(stream, track.Samples);
        WriteChunkOffsets(stream, offsets);
        if (track.Kind == IsoBmffTrackKind.Video)
        {
            WriteSyncSamples(stream, track.Samples);
        }
        EndBox(stream, stbl);
    }

    private static void WriteSampleDescription(
        Stream stream,
        IsoBmffCompositionTrack track)
    {
        long stsd = BeginFullBox(stream, "stsd");
        WriteUInt32(stream, 1);
        long entry = BeginBox(
            stream,
            track.SampleEntryType);
        stream.Write(track.SampleEntryPayload);
        EndBox(stream, entry);
        EndBox(stream, stsd);
    }

    private static void WriteTimeToSample(
        Stream stream,
        IsoBmffCompositionSample[] samples)
    {
        long stts = BeginFullBox(stream, "stts");
        WriteRuns(
            stream,
            samples,
            static sample => sample.Duration,
            signed: false);
        EndBox(stream, stts);
    }

    private static void WriteCompositionTime(
        Stream stream,
        IsoBmffCompositionSample[] samples)
    {
        if (!samples.Any(
                static sample =>
                    sample.CompositionOffset != 0))
        {
            return;
        }
        long ctts = BeginFullBox(
            stream,
            "ctts",
            version: 1);
        WriteRuns(
            stream,
            samples,
            static sample =>
                sample.CompositionOffset,
            signed: true);
        EndBox(stream, ctts);
    }

    private static void WriteRuns(
        Stream stream,
        IsoBmffCompositionSample[] samples,
        Func<IsoBmffCompositionSample, int> selector,
        bool signed)
    {
        int runs = 0;
        int previous = 0;
        for (int index = 0; index < samples.Length; index++)
        {
            int value = selector(samples[index]);
            if (index == 0 || value != previous)
            {
                runs++;
                previous = value;
            }
        }
        WriteUInt32(stream, checked((uint)runs));

        int start = 0;
        while (start < samples.Length)
        {
            int value = selector(samples[start]);
            int end = start + 1;
            while (end < samples.Length &&
                   selector(samples[end]) == value)
            {
                end++;
            }
            WriteUInt32(
                stream,
                checked((uint)(end - start)));
            if (signed)
            {
                WriteInt32(stream, value);
            }
            else
            {
                WriteUInt32(
                    stream,
                    checked((uint)value));
            }
            start = end;
        }
    }

    private static void WriteSampleToChunk(Stream stream)
    {
        long stsc = BeginFullBox(stream, "stsc");
        WriteUInt32(stream, 1);
        WriteUInt32(stream, 1);
        WriteUInt32(stream, 1);
        WriteUInt32(stream, 1);
        EndBox(stream, stsc);
    }

    private static void WriteSampleSizes(
        Stream stream,
        IsoBmffCompositionSample[] samples)
    {
        long stsz = BeginFullBox(stream, "stsz");
        WriteUInt32(stream, 0);
        WriteUInt32(
            stream,
            checked((uint)samples.Length));
        for (int index = 0; index < samples.Length; index++)
        {
            WriteUInt32(
                stream,
                checked((uint)samples[index].Size));
        }
        EndBox(stream, stsz);
    }

    private static void WriteChunkOffsets(
        Stream stream,
        long[] offsets)
    {
        long co64 = BeginFullBox(stream, "co64");
        WriteUInt32(
            stream,
            checked((uint)offsets.Length));
        for (int index = 0; index < offsets.Length; index++)
        {
            WriteUInt64(
                stream,
                checked((ulong)offsets[index]));
        }
        EndBox(stream, co64);
    }

    private static void WriteSyncSamples(
        Stream stream,
        IsoBmffCompositionSample[] samples)
    {
        int count = samples.Count(
            static sample => sample.IsSync);
        long stss = BeginFullBox(stream, "stss");
        WriteUInt32(stream, checked((uint)count));
        for (int index = 0; index < samples.Length; index++)
        {
            if (samples[index].IsSync)
            {
                WriteUInt32(
                    stream,
                    checked((uint)index + 1));
            }
        }
        EndBox(stream, stss);
    }

    private static ulong ToMovieDuration(
        IsoBmffCompositionTrack track)
    {
        if (track.Edits.Length != 0)
        {
            ulong duration = 0;
            for (int index = 0;
                 index < track.Edits.Length;
                 index++)
            {
                duration = checked(
                    duration +
                    track.Edits[index]
                        .SegmentDuration);
            }
            return duration;
        }

        return checked(
            (ulong)Math.Round(
                track.Duration *
                ((double)MovieTimescale /
                 track.Timescale),
                MidpointRounding.AwayFromZero));
    }

    private static long BeginFullBox(
        Stream stream,
        string type,
        byte version = 0,
        int flags = 0)
    {
        long start = BeginBox(stream, type);
        stream.WriteByte(version);
        stream.WriteByte((byte)(flags >> 16));
        stream.WriteByte((byte)(flags >> 8));
        stream.WriteByte((byte)flags);
        return start;
    }

    private static long BeginBox(
        Stream stream,
        string type) =>
        BeginBox(stream, FourCc(type));

    private static long BeginBox(
        Stream stream,
        uint type)
    {
        long start = stream.Position;
        WriteUInt32(stream, 0);
        WriteUInt32(stream, type);
        return start;
    }

    private static void EndBox(Stream stream, long start)
    {
        long end = stream.Position;
        long size = checked(end - start);
        if (size > uint.MaxValue)
        {
            throw new InvalidDataException(
                "An ISO-BMFF metadata box exceeds 32-bit size.");
        }
        stream.Position = start;
        WriteUInt32(stream, (uint)size);
        stream.Position = end;
    }

    private static void WriteIdentityMatrix(Stream stream)
    {
        WriteUInt32(stream, 0x0001_0000);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0x0001_0000);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0x4000_0000);
    }

    private static void WriteZeros(Stream stream, int count)
    {
        Span<byte> zeros = stackalloc byte[32];
        while (count > 0)
        {
            int write = Math.Min(count, zeros.Length);
            stream.Write(zeros[..write]);
            count -= write;
        }
    }

    private static void WriteUInt16(
        Stream stream,
        ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(
        Stream stream,
        uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt32(
        Stream stream,
        int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64(
        Stream stream,
        ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(
        Stream stream,
        long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteFourCc(
        Stream stream,
        string value) =>
        WriteUInt32(stream, FourCc(value));

    private static uint FourCc(string value)
    {
        if (value.Length != 4)
        {
            throw new ArgumentException(
                "A four-character code must contain exactly four characters.",
                nameof(value));
        }
        return (uint)value[0] << 24 |
            (uint)value[1] << 16 |
            (uint)value[2] << 8 |
            value[3];
    }
}
