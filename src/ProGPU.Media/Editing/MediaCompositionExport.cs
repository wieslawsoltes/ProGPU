namespace ProGPU.Media.Editing;

/// <summary>
/// Controls whether an exporter may align edits to codec sync samples or must
/// decode and re-encode the exact requested range.
/// </summary>
public enum MediaCompositionTrimmingMode
{
    Fast,
    Precise
}

public enum MediaCompositionExportFailure
{
    None,
    Unknown,
    InvalidProfile,
    CodecNotFound
}

/// <summary>
/// Describes how video reaches an exporter's output pipeline. This is an
/// export-specific diagnostic contract and is not part of the WinUI API.
/// </summary>
public enum MediaCompositionExportVideoPath
{
    Unknown,
    None,
    CompressedSampleCopy,
    NativeGpuSurface,
    GpuCopy,
    CpuBuffer
}

/// <summary>
/// Describes how audio reaches an exporter's output pipeline. NativeBuffer
/// means samples remain owned by the platform media stack rather than being
/// copied into managed memory.
/// </summary>
public enum MediaCompositionExportAudioPath
{
    Unknown,
    None,
    CompressedSampleCopy,
    NativeBuffer,
    CpuBuffer
}

/// <summary>
/// Typed, reflection-free diagnostics for the provider selected for an export
/// request. Hardware encoder selection is reported separately as requested
/// and guaranteed because native codec stacks may negotiate a software
/// transform at runtime.
/// </summary>
public readonly record struct MediaCompositionExportCapabilities(
    string ProviderId,
    MediaCompositionExportVideoPath VideoPath,
    MediaCompositionExportAudioPath AudioPath,
    bool HardwareVideoEncoderRequested,
    bool HardwareVideoEncoderGuaranteed,
    bool EffectsBakedOnGpu,
    string? Limitation);

/// <summary>
/// Framework-neutral encoding settings passed to a native export provider.
/// Subtype values use the same public codec/container names exposed by
/// WinUI's MediaEncodingProfile (for example MPEG4, H264, and AAC).
/// </summary>
public sealed record MediaCompositionEncodingProfile(
    string ContainerSubtype,
    string? VideoSubtype,
    string? AudioSubtype,
    uint Width,
    uint Height,
    uint VideoBitrate,
    uint FrameRateNumerator,
    uint FrameRateDenominator,
    uint AudioBitrate,
    uint AudioSampleRate,
    uint AudioChannelCount);

/// <summary>
/// Immutable clip snapshot. Providers never retain or inspect framework
/// objects while encoding.
/// </summary>
public sealed record MediaCompositionExportClip(
    Uri? SourceUri,
    TimeSpan OriginalDuration,
    TimeSpan TrimTimeFromStart,
    TimeSpan TrimTimeFromEnd,
    double Volume,
    uint? ArgbColor,
    IReadOnlyDictionary<string, string> UserData)
{
    /// <summary>
    /// Gets the source video's encoded display width when it is known.
    /// Zero means that the provider must discover it from the source.
    /// </summary>
    public uint SourceVideoWidth { get; init; }

    /// <summary>
    /// Gets the source video's encoded display height when it is known.
    /// Zero means that the provider must discover it from the source.
    /// </summary>
    public uint SourceVideoHeight { get; init; }

    /// <summary>
    /// Gets the selected source audio subtype when it is known.
    /// Null means that the provider must inspect the source.
    /// </summary>
    public string? SourceAudioSubtype { get; init; }

    /// <summary>
    /// Gets the zero-based embedded audio-track selection.
    /// </summary>
    public uint SourceAudioTrackIndex { get; init; }

    /// <summary>
    /// Gets the selected source audio bitrate when it is known.
    /// </summary>
    public uint SourceAudioBitrate { get; init; }

    /// <summary>
    /// Gets the selected source audio sample rate when it is known.
    /// </summary>
    public uint SourceAudioSampleRate { get; init; }

    /// <summary>
    /// Gets the selected source audio channel count when it is known.
    /// </summary>
    public uint SourceAudioChannelCount { get; init; }

    public IReadOnlyList<MediaCompositionEffectDefinition>
        AudioEffectDefinitions { get; init; } = [];

    public IReadOnlyList<MediaCompositionEffectDefinition>
        VideoEffectDefinitions { get; init; } = [];
}

/// <summary>
/// Framework-neutral snapshot of a WinUI media effect definition. Property
/// values are restricted by the project serializer to null and primitive
/// JSON-compatible value types so providers do not need reflection.
/// </summary>
public sealed record MediaCompositionEffectDefinition(
    string ActivatableClassId,
    IReadOnlyDictionary<string, object?> Properties);

/// <summary>
/// Immutable background-audio snapshot aligned with WinUI's
/// BackgroundAudioTrack timeline model.
/// </summary>
public sealed record MediaCompositionExportAudioTrack(
    Uri SourceUri,
    TimeSpan OriginalDuration,
    TimeSpan TrimTimeFromStart,
    TimeSpan TrimTimeFromEnd,
    TimeSpan Delay,
    double Volume,
    IReadOnlyDictionary<string, string> UserData)
{
    public IReadOnlyList<MediaCompositionEffectDefinition>
        AudioEffectDefinitions { get; init; } = [];
}

public sealed record MediaCompositionExportOverlay(
    MediaCompositionExportClip Clip,
    TimeSpan Delay,
    double PositionX,
    double PositionY,
    double PositionWidth,
    double PositionHeight,
    double Opacity,
    bool AudioEnabled);

public sealed record MediaCompositionExportOverlayLayer(
    IReadOnlyList<MediaCompositionExportOverlay> Overlays)
{
    public MediaCompositionEffectDefinition?
        CustomCompositorDefinition { get; init; }
}

public sealed record MediaCompositionExportRequest(
    string DestinationPath,
    IReadOnlyList<MediaCompositionExportClip> Clips,
    MediaCompositionTrimmingMode TrimmingMode,
    MediaCompositionEncodingProfile EncodingProfile,
    IReadOnlyDictionary<string, string> UserData)
{
    public IReadOnlyList<MediaCompositionExportAudioTrack>
        BackgroundAudioTracks { get; init; } = [];

    public IReadOnlyList<MediaCompositionExportOverlayLayer>
        OverlayLayers { get; init; } = [];
}

/// <summary>
/// Pluggable native composition encoder. Implementations are expected to use
/// the platform media stack and hardware codecs when available.
/// </summary>
public interface IMediaCompositionExportProvider
{
    string Id { get; }
    int Priority { get; }

    bool CanRender(MediaCompositionExportRequest request);

    ValueTask<MediaCompositionExportFailure> RenderAsync(
        MediaCompositionExportRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional diagnostics implemented by exporters that can describe their
/// copy and codec path before rendering. GetCapabilities is called only after
/// CanRender has accepted the same immutable request.
/// </summary>
public interface IMediaCompositionExportCapabilityProvider
{
    MediaCompositionExportCapabilities GetCapabilities(
        MediaCompositionExportRequest request);
}

/// <summary>
/// Explicit, reflection-free registry for native composition exporters.
/// Registration is a startup operation; selection is allocation-free.
/// </summary>
public sealed class MediaCompositionExportRegistry
{
    private readonly object _gate = new();
    private Entry[] _entries = [];
    private long _nextSequence;

    public static MediaCompositionExportRegistry Default { get; } = new();

    public IDisposable Register(IMediaCompositionExportProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        Entry entry;
        lock (_gate)
        {
            entry = new Entry(
                provider,
                Interlocked.Increment(ref _nextSequence));
            Entry[] current = _entries;
            var next = new Entry[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = entry;
            Array.Sort(next, EntryComparer.Instance);
            Volatile.Write(ref _entries, next);
        }

        return new Registration(this, entry);
    }

    public ValueTask<MediaCompositionExportFailure> RenderAsync(
        MediaCompositionExportRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Entry[] entries = Volatile.Read(ref _entries);
        for (int index = 0; index < entries.Length; index++)
        {
            IMediaCompositionExportProvider provider =
                entries[index].Provider;
            if (provider.CanRender(request))
            {
                return provider.RenderAsync(
                    request,
                    progress,
                    cancellationToken);
            }
        }

        return ValueTask.FromResult(
            MediaCompositionExportFailure.CodecNotFound);
    }

    /// <summary>
    /// Reports the copy and codec path of the highest-priority provider that
    /// accepts the request and implements typed diagnostics. Selection is
    /// allocation-free and uses the same ordering as RenderAsync.
    /// </summary>
    public bool TryGetCapabilities(
        MediaCompositionExportRequest request,
        out MediaCompositionExportCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(request);

        Entry[] entries = Volatile.Read(ref _entries);
        for (int index = 0; index < entries.Length; index++)
        {
            IMediaCompositionExportProvider provider =
                entries[index].Provider;
            if (!provider.CanRender(request))
            {
                continue;
            }

            if (provider is
                IMediaCompositionExportCapabilityProvider diagnostic)
            {
                capabilities =
                    diagnostic.GetCapabilities(request);
                return true;
            }

            capabilities = default;
            return false;
        }

        capabilities = default;
        return false;
    }

    private void Unregister(Entry entry)
    {
        lock (_gate)
        {
            Entry[] current = _entries;
            int index = Array.IndexOf(current, entry);
            if (index < 0)
            {
                return;
            }

            var next = new Entry[current.Length - 1];
            if (index > 0)
            {
                Array.Copy(current, 0, next, 0, index);
            }
            if (index < current.Length - 1)
            {
                Array.Copy(
                    current,
                    index + 1,
                    next,
                    index,
                    current.Length - index - 1);
            }
            Volatile.Write(ref _entries, next);
        }
    }

    private sealed record Entry(
        IMediaCompositionExportProvider Provider,
        long Sequence);

    private sealed class EntryComparer : IComparer<Entry>
    {
        public static EntryComparer Instance { get; } = new();

        public int Compare(Entry? x, Entry? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }
            if (x is null)
            {
                return 1;
            }
            if (y is null)
            {
                return -1;
            }

            int priority = y.Provider.Priority.CompareTo(
                x.Provider.Priority);
            return priority != 0
                ? priority
                : x.Sequence.CompareTo(y.Sequence);
        }
    }

    private sealed class Registration : IDisposable
    {
        private MediaCompositionExportRegistry? _owner;
        private Entry? _entry;

        public Registration(
            MediaCompositionExportRegistry owner,
            Entry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public void Dispose()
        {
            MediaCompositionExportRegistry? owner =
                Interlocked.Exchange(ref _owner, null);
            Entry? entry =
                Interlocked.Exchange(ref _entry, null);
            if (owner is not null && entry is not null)
            {
                owner.Unregister(entry);
            }
        }
    }
}
