using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using ProGPU.Media.Playback;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Windows.Media.Core;

/// <summary>
/// Specifies the type of error that occurred while resolving timed metadata.
/// </summary>
public enum TimedMetadataTrackErrorCode
{
    None = 0,
    DataFormatError = 1,
    NetworkError = 2,
    InternalError = 3
}

/// <summary>
/// Provides information about an error that occurred while resolving timed
/// text.
/// </summary>
public sealed class TimedMetadataTrackError
{
    internal TimedMetadataTrackError(
        TimedMetadataTrackErrorCode errorCode,
        Exception extendedError)
    {
        ErrorCode = errorCode;
        ExtendedError = extendedError ??
            throw new ArgumentNullException(
                nameof(extendedError));
    }

    public TimedMetadataTrackErrorCode ErrorCode { get; }

    public Exception ExtendedError { get; }
}

/// <summary>
/// Event data published after a timed-text source has resolved.
/// </summary>
public sealed class TimedTextSourceResolveResultEventArgs :
    EventArgs
{
    private static readonly ReadOnlyCollection<
        TimedMetadataTrack> s_emptyTracks =
            Array.AsReadOnly(
                Array.Empty<TimedMetadataTrack>());

    internal TimedTextSourceResolveResultEventArgs(
        IReadOnlyList<TimedMetadataTrack>? tracks,
        TimedMetadataTrackError? error)
    {
        if (tracks is null || tracks.Count == 0)
        {
            Tracks = s_emptyTracks;
        }
        else
        {
            var copy =
                new TimedMetadataTrack[tracks.Count];
            for (int index = 0;
                 index < copy.Length;
                 index++)
            {
                copy[index] = tracks[index] ??
                    throw new ArgumentException(
                        "Resolved track collections cannot contain null.",
                        nameof(tracks));
            }
            Tracks = Array.AsReadOnly(copy);
        }
        Error = error;
    }

    public TimedMetadataTrackError? Error { get; }

    public IReadOnlyList<TimedMetadataTrack> Tracks { get; }
}

/// <summary>
/// WinUI-aligned external timed-text source. Text WebVTT sources are resolved
/// with bounded built-in I/O and a clean-room provider-neutral parser.
/// </summary>
public sealed class TimedTextSource
{
    private const int MaximumSourceBytes =
        64 * 1024 * 1024;
    private const int CopyBufferSize = 64 * 1024;
    private static readonly HttpClient s_httpClient =
        new();
    private static readonly UTF8Encoding s_strictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
    private static long s_nextTrackId;

    private readonly byte[]? _streamBytes;
    private readonly Uri? _uri;
    private readonly bool _hasIndex;
    private readonly string _defaultLanguage;
    private readonly string _trackId;
    private readonly object _gate = new();
    private CancellationTokenSource? _resolutionCancellation;
    private MediaSource? _owner;
    private int _resolutionGeneration;

    private TimedTextSource(
        byte[]? streamBytes,
        Uri? uri,
        bool hasIndex,
        string? defaultLanguage)
    {
        _streamBytes = streamBytes;
        _uri = uri;
        _hasIndex = hasIndex;
        _defaultLanguage = defaultLanguage ?? string.Empty;
        _trackId = string.Concat(
            "external-timed-text-",
            Interlocked.Increment(
                    ref s_nextTrackId)
                .ToString(
                    global::System.Globalization.CultureInfo
                        .InvariantCulture));
    }

    public event TypedEventHandler<
        TimedTextSource,
        TimedTextSourceResolveResultEventArgs>? Resolved;

    public static TimedTextSource CreateFromStream(
        IRandomAccessStream stream) =>
        CreateFromStream(stream, string.Empty);

    public static TimedTextSource CreateFromStream(
        IRandomAccessStream stream,
        string defaultLanguage)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new TimedTextSource(
            Snapshot(stream),
            uri: null,
            hasIndex: false,
            defaultLanguage);
    }

    public static TimedTextSource CreateFromUri(Uri uri) =>
        CreateFromUri(uri, string.Empty);

    public static TimedTextSource CreateFromUri(
        Uri uri,
        string defaultLanguage)
    {
        ValidateUri(uri);
        return new TimedTextSource(
            streamBytes: null,
            uri,
            hasIndex: false,
            defaultLanguage);
    }

    public static TimedTextSource CreateFromStreamWithIndex(
        IRandomAccessStream stream,
        IRandomAccessStream indexStream) =>
        CreateFromStreamWithIndex(
            stream,
            indexStream,
            string.Empty);

    public static TimedTextSource CreateFromStreamWithIndex(
        IRandomAccessStream stream,
        IRandomAccessStream indexStream,
        string defaultLanguage)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(indexStream);
        return new TimedTextSource(
            Snapshot(stream),
            uri: null,
            hasIndex: true,
            defaultLanguage);
    }

    public static TimedTextSource CreateFromUriWithIndex(
        Uri uri,
        Uri indexUri) =>
        CreateFromUriWithIndex(
            uri,
            indexUri,
            string.Empty);

    public static TimedTextSource CreateFromUriWithIndex(
        Uri uri,
        Uri indexUri,
        string defaultLanguage)
    {
        ValidateUri(uri);
        ValidateUri(indexUri);
        return new TimedTextSource(
            streamBytes: null,
            uri,
            hasIndex: true,
            defaultLanguage);
    }

    internal void AttachToSource(MediaSource owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (_owner is not null)
            {
                throw new InvalidOperationException(
                    "A TimedTextSource can belong to only one MediaSource.");
            }
            _owner = owner;
        }
    }

    internal void BeginResolve(MediaSource owner)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner))
            {
                return;
            }
            _resolutionCancellation =
                new CancellationTokenSource();
            int generation =
                ++_resolutionGeneration;
            _ = ResolveAsync(
                owner,
                generation,
                _resolutionCancellation.Token);
        }
    }

    internal void DetachFromSource(MediaSource owner)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner))
            {
                return;
            }
            _owner = null;
            _resolutionGeneration++;
            CancellationTokenSource? cancellation =
                _resolutionCancellation;
            _resolutionCancellation = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
        }
    }

    private async Task ResolveAsync(
        MediaSource owner,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            TimedTextSourceResolveResultEventArgs? result;
            try
            {
                if (_hasIndex)
                {
                    throw new NotSupportedException(
                        "Image-based external subtitle sources require a native image-subtitle decoder and are not supported yet.");
                }

                byte[] bytes = _streamBytes ??
                    await LoadUriAsync(
                            _uri!,
                            cancellationToken)
                        .ConfigureAwait(false);
                cancellationToken
                    .ThrowIfCancellationRequested();
                string text =
                    s_strictUtf8.GetString(bytes);
                WebVttDocument document =
                    WebVttDocumentParser.Parse(text);
                TimedMetadataTrack track =
                    CreateTrack(document);
                TimedMetadataTrack[] tracks = [track];
                if (!IsCurrent(
                        owner,
                        generation,
                        cancellationToken) ||
                    !owner.PublishResolvedTimedTextTracks(
                        this,
                        tracks))
                {
                    return;
                }
                result =
                    new
                        TimedTextSourceResolveResultEventArgs(
                            tracks,
                            error: null);
            }
            catch (OperationCanceledException)
                when (cancellationToken
                    .IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                if (!IsCurrent(
                        owner,
                        generation,
                        cancellationToken))
                {
                    return;
                }
                result =
                    new
                        TimedTextSourceResolveResultEventArgs(
                            tracks: null,
                            new TimedMetadataTrackError(
                                Classify(error),
                                error));
            }

            RaiseResolved(result);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_owner, owner) &&
                    _resolutionGeneration == generation)
                {
                    _resolutionCancellation?.Dispose();
                    _resolutionCancellation = null;
                }
            }
        }
    }

    private bool IsCurrent(
        MediaSource owner,
        int generation,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        lock (_gate)
        {
            return ReferenceEquals(_owner, owner) &&
                   _resolutionGeneration == generation;
        }
    }

    private void RaiseResolved(
        TimedTextSourceResolveResultEventArgs args) =>
        Resolved?.Invoke(this, args);

    private TimedMetadataTrack CreateTrack(
        WebVttDocument document)
    {
        var track = new TimedMetadataTrack(
            _trackId,
            _defaultLanguage,
            TimedMetadataKind.Subtitle)
        {
            Label = GetLabel()
        };
        for (int index = 0;
             index < document.Cues.Count;
             index++)
        {
            WebVttDocumentCue source =
                document.Cues[index];
            string cueId =
                string.IsNullOrEmpty(source.Id)
                    ? string.Concat(
                        _trackId,
                        ":",
                        index.ToString(
                            global::System.Globalization
                                .CultureInfo
                                .InvariantCulture))
                    : source.Id;
            var descriptor =
                new
                    MediaPlaybackTimedMetadataCueDescriptor(
                        cueId,
                        source.StartTime,
                        source.Duration,
                        source.Text,
                        source.Presentation);
            var cue = new TimedTextCue
            {
                Id = cueId
            };
            cue.ApplyProviderState(in descriptor);
            track.AddCue(cue);
        }
        return track;
    }

    private string GetLabel()
    {
        if (_uri is null)
        {
            return string.Empty;
        }
        string name =
            Path.GetFileNameWithoutExtension(
                _uri.IsFile
                    ? _uri.LocalPath
                    : _uri.AbsolutePath);
        return Uri.UnescapeDataString(name);
    }

    private static TimedMetadataTrackErrorCode Classify(
        Exception error) =>
        error switch
        {
            FormatException or DecoderFallbackException =>
                TimedMetadataTrackErrorCode.DataFormatError,
            HttpRequestException =>
                TimedMetadataTrackErrorCode.NetworkError,
            IOException =>
                TimedMetadataTrackErrorCode.NetworkError,
            _ => TimedMetadataTrackErrorCode.InternalError
        };

    private static void ValidateUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "A timed-text URI must be absolute.",
                nameof(uri));
        }
    }

    private static byte[] Snapshot(
        IRandomAccessStream source)
    {
        Stream stream = source.AsStream();
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException(
                "A timed-text stream must be readable and seekable.",
                nameof(source));
        }
        lock (stream)
        {
            long originalPosition = stream.Position;
            try
            {
                stream.Position = 0;
                return ReadBounded(stream);
            }
            finally
            {
                stream.Position = originalPosition;
            }
        }
    }

    private static async Task<byte[]> LoadUriAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        if (uri.IsFile)
        {
            await using var fileStream =
                new FileStream(
                    uri.LocalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSize,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan);
            return await ReadBoundedAsync(
                    fileStream,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        using HttpResponseMessage response =
            await s_httpClient.GetAsync(
                    uri,
                    HttpCompletionOption
                        .ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength
                is long length &&
            length > MaximumSourceBytes)
        {
            throw new FormatException(
                $"A timed-text source cannot exceed {MaximumSourceBytes} bytes.");
        }
        await using Stream responseStream =
            await response.Content.ReadAsStreamAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        return await ReadBoundedAsync(
                responseStream,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static byte[] ReadBounded(Stream source)
    {
        if (source.CanSeek &&
            source.Length > MaximumSourceBytes)
        {
            throw new FormatException(
                $"A timed-text source cannot exceed {MaximumSourceBytes} bytes.");
        }
        using var destination =
            new MemoryStream(
                source.CanSeek
                    ? checked((int)source.Length)
                    : 0);
        byte[] buffer =
            ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (true)
            {
                int read = source.Read(
                    buffer,
                    0,
                    buffer.Length);
                if (read == 0)
                {
                    break;
                }
                if (destination.Length >
                    MaximumSourceBytes - read)
                {
                    throw new FormatException(
                        $"A timed-text source cannot exceed {MaximumSourceBytes} bytes.");
                }
                destination.Write(buffer, 0, read);
            }
            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        if (source.CanSeek &&
            source.Length > MaximumSourceBytes)
        {
            throw new FormatException(
                $"A timed-text source cannot exceed {MaximumSourceBytes} bytes.");
        }
        using var destination =
            new MemoryStream(
                source.CanSeek
                    ? checked((int)source.Length)
                    : 0);
        byte[] buffer =
            ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (true)
            {
                int read =
                    await source.ReadAsync(
                            buffer.AsMemory(
                                0,
                                buffer.Length),
                            cancellationToken)
                        .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                if (destination.Length >
                    MaximumSourceBytes - read)
                {
                    throw new FormatException(
                        $"A timed-text source cannot exceed {MaximumSourceBytes} bytes.");
                }
                await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
