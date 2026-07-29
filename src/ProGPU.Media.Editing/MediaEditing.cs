using System.Collections.ObjectModel;
using System.Text.Json;
using ProGPU.Media.Containers;
using ProGPU.Media.Editing;
using Windows.Foundation.Collections;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.UI;

namespace Windows.Media.Editing;

/// <summary>
/// Represents one non-destructive item in a media composition.
/// The public trimming and timeline members follow the WinUI/UWP
/// <c>Windows.Media.Editing.MediaClip</c> contract. ProGPU adds URI source
/// access because its cross-platform providers do not expose WinRT storage
/// handles on every target.
/// </summary>
public sealed class MediaClip
{
    private readonly Dictionary<string, string> _userData =
        new(StringComparer.Ordinal);
    private readonly List<IAudioEffectDefinition>
        _audioEffectDefinitions = [];
    private readonly List<IVideoEffectDefinition>
        _videoEffectDefinitions = [];
    private readonly List<EmbeddedAudioTrack>
        _embeddedAudioTracks = [];
    private readonly ReadOnlyCollection<EmbeddedAudioTrack>
        _embeddedAudioTracksView;
    private VideoEncodingProperties
        _videoEncodingProperties;
    private uint _selectedEmbeddedAudioTrackIndex;
    private TimeSpan _originalDuration;
    private TimeSpan _trimTimeFromStart;
    private TimeSpan _trimTimeFromEnd;
    private double _volume = 1d;
    private MediaComposition? _composition;

    private MediaClip(
        Uri? sourceUri,
        TimeSpan originalDuration,
        Color? color)
    {
        ValidateDuration(
            originalDuration,
            nameof(originalDuration));
        ProGpuSourceUri = sourceUri;
        ProGpuColor = color;
        _originalDuration = originalDuration;
        _embeddedAudioTracksView =
            _embeddedAudioTracks.AsReadOnly();
        _videoEncodingProperties =
            CreateInitialVideoEncodingProperties(color);
    }

    public TimeSpan OriginalDuration => _originalDuration;

    public IList<IAudioEffectDefinition>
        AudioEffectDefinitions =>
        _audioEffectDefinitions;

    public IList<IVideoEffectDefinition>
        VideoEffectDefinitions =>
        _videoEffectDefinitions;

    public IReadOnlyList<EmbeddedAudioTrack>
        EmbeddedAudioTracks =>
        _embeddedAudioTracksView;

    public uint SelectedEmbeddedAudioTrackIndex
    {
        get => _selectedEmbeddedAudioTrackIndex;
        set
        {
            if ((_embeddedAudioTracks.Count == 0 &&
                value != 0) ||
                _embeddedAudioTracks.Count != 0 &&
                value >=
                    (uint)_embeddedAudioTracks.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value));
            }
            _selectedEmbeddedAudioTrackIndex = value;
        }
    }

    public TimeSpan TrimTimeFromStart
    {
        get => _trimTimeFromStart;
        set
        {
            ValidateTrim(value, nameof(value));
            if (value + _trimTimeFromEnd >
                _originalDuration)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "The combined start and end trims cannot exceed the original duration.");
            }
            _trimTimeFromStart = value;
        }
    }

    public TimeSpan TrimTimeFromEnd
    {
        get => _trimTimeFromEnd;
        set
        {
            ValidateTrim(value, nameof(value));
            if (_trimTimeFromStart + value >
                _originalDuration)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "The combined start and end trims cannot exceed the original duration.");
            }
            _trimTimeFromEnd = value;
        }
    }

    public TimeSpan TrimmedDuration =>
        _originalDuration -
        _trimTimeFromStart -
        _trimTimeFromEnd;

    public TimeSpan StartTimeInComposition =>
        _composition?.GetStartTime(this) ??
        TimeSpan.Zero;

    public TimeSpan EndTimeInComposition =>
        StartTimeInComposition + TrimmedDuration;

    public double Volume
    {
        get => _volume;
        set
        {
            if (!double.IsFinite(value) || value < 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value));
            }
            _volume = value;
        }
    }

    public IDictionary<string, string> UserData =>
        _userData;

    /// <summary>
    /// Gets the portable URI consumed by ProGPU media providers. This is a
    /// ProGPU extension to the official MediaClip surface.
    /// </summary>
    public Uri? ProGpuSourceUri { get; }

    /// <summary>
    /// Gets the solid color for a clip created by
    /// <see cref="CreateFromColor"/>.
    /// </summary>
    public Color? ProGpuColor { get; }

    public static async Task<MediaClip> CreateFromFileAsync(
        StorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Uri source = new(Path.GetFullPath(file.Path));
        try
        {
            MediaFileMetadata metadata =
                await MediaFileMetadataReader
                    .ReadIsoBmffAsync(file.Path)
                    .ConfigureAwait(false);
            if (metadata.VideoStreams.Count != 0)
            {
                MediaVideoStreamMetadata video =
                    metadata.VideoStreams[0];
                var clip = new MediaClip(
                    source,
                    video.Duration,
                    null);
                var videoProperties =
                    new VideoEncodingProperties
                    {
                        Subtype = video.Subtype,
                        Width = video.Width,
                        Height = video.Height,
                        Bitrate = video.Bitrate
                    };
                videoProperties.FrameRate.Numerator =
                    video.FrameRateNumerator;
                videoProperties.FrameRate.Denominator =
                    video.FrameRateDenominator;
                clip.SetProGpuEncodingProperties(
                    videoProperties,
                    metadata.AudioStreams.Select(
                        static audio =>
                            new AudioEncodingProperties
                            {
                                Subtype = audio.Subtype,
                                Bitrate = audio.Bitrate,
                                SampleRate =
                                    audio.SampleRate,
                                ChannelCount =
                                    audio.ChannelCount
                            }));
                return clip;
            }
        }
        catch (InvalidDataException)
        {
            // Non-ISO sources remain provider-discoverable. Native metadata
            // loading can update this portable clip after opening.
        }
        catch (EndOfStreamException)
        {
            // Preserve the same provider-discoverable fallback for other
            // container families.
        }

        return new MediaClip(
            source,
            TimeSpan.Zero,
            null);
    }

    public static MediaClip CreateFromColor(
        Color color,
        TimeSpan originalDuration)
    {
        if (originalDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(originalDuration));
        }
        return new MediaClip(
            null,
            originalDuration,
            color);
    }

    /// <summary>
    /// Creates a provider-backed URI clip. This is a ProGPU extension for
    /// desktop, mobile, and browser targets where a WinRT StorageFile is not
    /// available.
    /// </summary>
    public static MediaClip CreateFromUri(
        Uri source,
        TimeSpan originalDuration)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "A media clip URI must be absolute.",
                nameof(source));
        }
        return new MediaClip(
            source,
            originalDuration,
            null);
    }

    public VideoEncodingProperties
        GetVideoEncodingProperties() =>
        MediaEditingMetadata.Clone(
            _videoEncodingProperties);

    public MediaClip Clone()
    {
        var clone = new MediaClip(
            ProGpuSourceUri,
            _originalDuration,
            ProGpuColor)
        {
            _trimTimeFromStart =
                _trimTimeFromStart,
            _trimTimeFromEnd =
                _trimTimeFromEnd,
            _volume = _volume,
            _videoEncodingProperties =
                MediaEditingMetadata.Clone(
                    _videoEncodingProperties),
            _selectedEmbeddedAudioTrackIndex =
                _selectedEmbeddedAudioTrackIndex
        };
        for (int index = 0;
             index < _embeddedAudioTracks.Count;
             index++)
        {
            clone._embeddedAudioTracks.Add(
                _embeddedAudioTracks[index].Clone());
        }
        foreach ((string key, string value) in
            _userData)
        {
            clone._userData.Add(key, value);
        }
        for (int index = 0;
             index < _audioEffectDefinitions.Count;
             index++)
        {
            clone._audioEffectDefinitions.Add(
                MediaEditingEffectClone.Clone(
                    _audioEffectDefinitions[index]));
        }
        for (int index = 0;
             index < _videoEffectDefinitions.Count;
             index++)
        {
            clone._videoEffectDefinitions.Add(
                MediaEditingEffectClone.Clone(
                    _videoEffectDefinitions[index]));
        }
        return clone;
    }

    /// <summary>
    /// Installs encoding metadata obtained from a platform demuxer without
    /// changing the clip's media source. This ProGPU extension performs
    /// bounded configuration-time copies and never participates in frame
    /// rendering.
    /// </summary>
    public void SetProGpuEncodingProperties(
        VideoEncodingProperties video,
        IEnumerable<AudioEncodingProperties>?
            embeddedAudioTracks = null)
    {
        ArgumentNullException.ThrowIfNull(video);
        VideoEncodingProperties nextVideo =
            MediaEditingMetadata.Clone(video);
        var nextAudio =
            new List<EmbeddedAudioTrack>();
        if (embeddedAudioTracks is not null)
        {
            foreach (AudioEncodingProperties audio in
                embeddedAudioTracks)
            {
                ArgumentNullException.ThrowIfNull(audio);
                if (ProGpuSourceUri is null)
                {
                    throw new InvalidOperationException(
                        "Embedded audio metadata requires a URI-backed clip.");
                }
                nextAudio.Add(
                    new EmbeddedAudioTrack(
                        ProGpuSourceUri,
                        _originalDuration,
                        audio));
            }
        }
        _videoEncodingProperties = nextVideo;
        _embeddedAudioTracks.Clear();
        _embeddedAudioTracks.AddRange(nextAudio);
        _selectedEmbeddedAudioTrackIndex = 0;
    }

    /// <summary>
    /// Updates duration after a native provider has read source metadata.
    /// Existing trims are preserved where possible and clamped to the new
    /// duration. This is a ProGPU extension.
    /// </summary>
    public void SetProGpuOriginalDuration(
        TimeSpan originalDuration)
    {
        ValidateDuration(
            originalDuration,
            nameof(originalDuration));
        _originalDuration = originalDuration;
        if (_trimTimeFromStart >
            originalDuration)
        {
            _trimTimeFromStart =
                originalDuration;
        }
        TimeSpan available =
            originalDuration -
            _trimTimeFromStart;
        if (_trimTimeFromEnd > available)
        {
            _trimTimeFromEnd = available;
        }
    }

    internal void Attach(MediaComposition composition)
    {
        if (_composition is not null &&
            !ReferenceEquals(_composition, composition))
        {
            throw new InvalidOperationException(
                "A MediaClip cannot belong to more than one MediaComposition.");
        }
        _composition = composition;
    }

    internal void Detach(MediaComposition composition)
    {
        if (ReferenceEquals(_composition, composition))
        {
            _composition = null;
        }
    }

    private void ValidateTrim(
        TimeSpan value,
        string parameterName)
    {
        ValidateDuration(value, parameterName);
        if (_originalDuration == TimeSpan.Zero &&
            value != TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A clip with unknown duration cannot be trimmed until metadata is available.");
        }
    }

    private static void ValidateDuration(
        TimeSpan value,
        string parameterName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName);
        }
    }

    private static VideoEncodingProperties
        CreateInitialVideoEncodingProperties(
            Color? color)
    {
        var properties = new VideoEncodingProperties
        {
            Subtype =
                color.HasValue
                    ? "ARGB32"
                    : string.Empty,
            Width = 0,
            Height = 0,
            Bitrate = 0
        };
        properties.FrameRate.Numerator = 0;
        properties.FrameRate.Denominator = 1;
        return properties;
    }
}

/// <summary>
/// Represents an ordered, non-destructive composition of media clips.
/// </summary>
public sealed class MediaComposition
{
    private const int ProjectFormatVersion = 1;
    private readonly ClipCollection _clips;
    private readonly List<BackgroundAudioTrack>
        _backgroundAudioTracks = [];
    private readonly List<MediaOverlayLayer>
        _overlayLayers = [];
    private readonly Dictionary<string, string> _userData =
        new(StringComparer.Ordinal);

    public MediaComposition()
    {
        _clips = new ClipCollection(this);
    }

    public IList<MediaClip> Clips => _clips;

    public IList<BackgroundAudioTrack>
        BackgroundAudioTracks =>
        _backgroundAudioTracks;

    public IList<MediaOverlayLayer> OverlayLayers =>
        _overlayLayers;

    public TimeSpan Duration
    {
        get
        {
            long ticks = 0;
            foreach (MediaClip clip in _clips)
            {
                ticks = checked(
                    ticks +
                    clip.TrimmedDuration.Ticks);
            }
            for (int index = 0;
                 index < _backgroundAudioTracks.Count;
                 index++)
            {
                BackgroundAudioTrack track =
                    _backgroundAudioTracks[index];
                long endTicks = checked(
                    track.Delay.Ticks +
                    track.TrimmedDuration.Ticks);
                ticks = Math.Max(ticks, Math.Max(0, endTicks));
            }
            return TimeSpan.FromTicks(ticks);
        }
    }

    public IDictionary<string, string> UserData =>
        _userData;

    public MediaComposition Clone()
    {
        var clone = new MediaComposition();
        foreach (MediaClip clip in _clips)
        {
            clone.Clips.Add(clip.Clone());
        }
        for (int index = 0;
             index < _backgroundAudioTracks.Count;
             index++)
        {
            clone._backgroundAudioTracks.Add(
                _backgroundAudioTracks[index].Clone());
        }
        for (int index = 0;
             index < _overlayLayers.Count;
             index++)
        {
            clone._overlayLayers.Add(
                _overlayLayers[index].Clone());
        }
        foreach ((string key, string value) in
            _userData)
        {
            clone._userData.Add(key, value);
        }
        return clone;
    }

    public static MediaEncodingProfile CreateDefaultEncodingProfile() =>
        MediaEncodingProfile.CreateMp4(
            VideoEncodingQuality.HD720p);

    /// <summary>
    /// Serializes the editable project. This intentionally stores the
    /// composition rather than encoded media, matching WinUI SaveAsync.
    /// </summary>
    public async Task SaveAsync(StorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", ProjectFormatVersion);
            WriteStringMap(writer, "userData", _userData);
            writer.WriteStartArray("clips");
            foreach (MediaClip clip in _clips)
            {
                WriteClip(writer, clip);
            }
            writer.WriteEndArray();
            writer.WriteStartArray("backgroundAudioTracks");
            for (int index = 0;
                 index < _backgroundAudioTracks.Count;
                 index++)
            {
                BackgroundAudioTrack track =
                    _backgroundAudioTracks[index];
                writer.WriteStartObject();
                writer.WriteString(
                    "sourceUri",
                    track.ProGpuSourceUri.AbsoluteUri);
                writer.WriteNumber(
                    "originalDurationTicks",
                    track.OriginalDuration.Ticks);
                writer.WriteNumber(
                    "trimTimeFromStartTicks",
                    track.TrimTimeFromStart.Ticks);
                writer.WriteNumber(
                    "trimTimeFromEndTicks",
                    track.TrimTimeFromEnd.Ticks);
                writer.WriteNumber(
                    "delayTicks",
                    track.Delay.Ticks);
                writer.WriteNumber("volume", track.Volume);
                writer.WritePropertyName(
                    "audioEncodingProperties");
                WriteAudioEncodingProperties(
                    writer,
                    track.GetAudioEncodingProperties());
                WriteStringMap(
                    writer,
                    "userData",
                    track.UserData);
                WriteEffectDefinitions(
                    writer,
                    "audioEffectDefinitions",
                    track.AudioEffectDefinitions);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("overlayLayers");
            for (int layerIndex = 0;
                 layerIndex < _overlayLayers.Count;
                 layerIndex++)
            {
                MediaOverlayLayer layer =
                    _overlayLayers[layerIndex];
                writer.WriteStartObject();
                if (layer.CustomCompositorDefinition is
                    { } compositor)
                {
                    writer.WritePropertyName(
                        "customCompositorDefinition");
                    WriteEffectDefinition(
                        writer,
                        compositor.ActivatableClassId,
                        compositor.Properties);
                }
                writer.WriteStartArray("overlays");
                for (int overlayIndex = 0;
                     overlayIndex < layer.Overlays.Count;
                     overlayIndex++)
                {
                    MediaOverlay overlay =
                        layer.Overlays[overlayIndex];
                    writer.WriteStartObject();
                    writer.WritePropertyName("clip");
                    WriteClip(writer, overlay.Clip);
                    writer.WriteNumber(
                        "delayTicks",
                        overlay.Delay.Ticks);
                    writer.WriteNumber(
                        "positionX",
                        overlay.Position.X);
                    writer.WriteNumber(
                        "positionY",
                        overlay.Position.Y);
                    writer.WriteNumber(
                        "positionWidth",
                        overlay.Position.Width);
                    writer.WriteNumber(
                        "positionHeight",
                        overlay.Position.Height);
                    writer.WriteNumber(
                        "opacity",
                        overlay.Opacity);
                    writer.WriteBoolean(
                        "audioEnabled",
                        overlay.AudioEnabled);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        await file.WriteBytesAsync(stream.ToArray())
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a composition from a project previously written by
    /// <see cref="SaveAsync"/>. This static factory matches the official
    /// Windows.Media.Editing.MediaComposition contract.
    /// </summary>
    public static async Task<MediaComposition> LoadAsync(
        StorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        byte[] bytes = await file.ReadBytesAsync()
            .ConfigureAwait(false);
        return ParseProject(bytes);
    }

    /// <summary>
    /// ProGPU convenience helper that transactionally replaces this
    /// composition with a saved project. The official WinUI-shaped
    /// <see cref="LoadAsync(StorageFile)"/> factory remains static.
    /// </summary>
    public async Task LoadProjectAsync(StorageFile file)
    {
        MediaComposition loaded =
            await LoadAsync(file).ConfigureAwait(false);
        ReplaceWith(loaded);
    }

    private void ReplaceWith(MediaComposition loaded)
    {
        _clips.Clear();
        _backgroundAudioTracks.Clear();
        _overlayLayers.Clear();
        _userData.Clear();
        foreach (MediaClip clip in loaded.Clips)
        {
            _clips.Add(clip.Clone());
        }
        foreach ((string key, string value) in loaded.UserData)
        {
            _userData.Add(key, value);
        }
        for (int index = 0;
             index < loaded.BackgroundAudioTracks.Count;
             index++)
        {
            _backgroundAudioTracks.Add(
                loaded.BackgroundAudioTracks[index].Clone());
        }
        for (int index = 0;
             index < loaded.OverlayLayers.Count;
             index++)
        {
            _overlayLayers.Add(
                loaded.OverlayLayers[index].Clone());
        }
    }

    public Task<TranscodeFailureReason> RenderToFileAsync(
        StorageFile destination) =>
        RenderToFileAsync(
            destination,
            MediaTrimmingPreference.Fast,
            CreateDefaultEncodingProfile());

    public Task<TranscodeFailureReason> RenderToFileAsync(
        StorageFile destination,
        MediaTrimmingPreference trimmingPreference) =>
        RenderToFileAsync(
            destination,
            trimmingPreference,
            CreateDefaultEncodingProfile());

    public Task<TranscodeFailureReason> RenderToFileAsync(
        StorageFile destination,
        MediaTrimmingPreference trimmingPreference,
        MediaEncodingProfile encodingProfile) =>
        RenderToFileAsync(
            destination,
            trimmingPreference,
            encodingProfile,
            progress: null,
            CancellationToken.None);

    /// <summary>
    /// ProGPU extension that reports the native provider and copy path that
    /// would be selected for the same RenderToFileAsync request.
    /// </summary>
    public bool TryGetProGpuExportCapabilities(
        StorageFile destination,
        MediaTrimmingPreference trimmingPreference,
        MediaEncodingProfile encodingProfile,
        out MediaCompositionExportCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(encodingProfile);

        MediaCompositionExportRequest request =
            CreateExportRequest(
                destination.Path,
                trimmingPreference,
                encodingProfile);
        return MediaCompositionExportRegistry.Default
            .TryGetCapabilities(
                request,
                out capabilities);
    }

    /// <summary>
    /// ProGPU extension that adds Task-based cancellation and progress while
    /// retaining the official RenderToFileAsync model.
    /// </summary>
    public async Task<TranscodeFailureReason> RenderToFileAsync(
        StorageFile destination,
        MediaTrimmingPreference trimmingPreference,
        MediaEncodingProfile encodingProfile,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(encodingProfile);
        cancellationToken.ThrowIfCancellationRequested();

        MediaCompositionExportRequest request =
            CreateExportRequest(
                destination.Path,
                trimmingPreference,
                encodingProfile);
        MediaCompositionExportFailure failure =
            await MediaCompositionExportRegistry.Default.RenderAsync(
                request,
                progress,
                cancellationToken).ConfigureAwait(false);
        return failure switch
        {
            MediaCompositionExportFailure.None =>
                TranscodeFailureReason.None,
            MediaCompositionExportFailure.InvalidProfile =>
                TranscodeFailureReason.InvalidProfile,
            MediaCompositionExportFailure.CodecNotFound =>
                TranscodeFailureReason.CodecNotFound,
            _ => TranscodeFailureReason.Unknown
        };
    }

    internal TimeSpan GetStartTime(MediaClip target)
    {
        long ticks = 0;
        foreach (MediaClip clip in _clips)
        {
            if (ReferenceEquals(clip, target))
            {
                return TimeSpan.FromTicks(ticks);
            }
            ticks = checked(
                ticks +
                clip.TrimmedDuration.Ticks);
        }
        return TimeSpan.Zero;
    }

    private MediaCompositionExportRequest CreateExportRequest(
        string destinationPath,
        MediaTrimmingPreference trimmingPreference,
        MediaEncodingProfile profile)
    {
        var clips =
            new MediaCompositionExportClip[_clips.Count];
        for (int index = 0; index < _clips.Count; index++)
        {
            clips[index] =
                CreateExportClip(_clips[index]);
        }

        var overlayLayers =
            new MediaCompositionExportOverlayLayer[
                _overlayLayers.Count];
        for (int layerIndex = 0;
             layerIndex < _overlayLayers.Count;
             layerIndex++)
        {
            MediaOverlayLayer layer =
                _overlayLayers[layerIndex];
            var overlays =
                new MediaCompositionExportOverlay[
                    layer.Overlays.Count];
            for (int overlayIndex = 0;
                 overlayIndex < layer.Overlays.Count;
                 overlayIndex++)
            {
                MediaOverlay overlay =
                    layer.Overlays[overlayIndex];
                overlays[overlayIndex] =
                    new MediaCompositionExportOverlay(
                        CreateExportClip(overlay.Clip),
                        overlay.Delay,
                        overlay.Position.X,
                        overlay.Position.Y,
                        overlay.Position.Width,
                        overlay.Position.Height,
                        overlay.Opacity,
                        overlay.AudioEnabled);
            }
            overlayLayers[layerIndex] =
                new MediaCompositionExportOverlayLayer(
                    Array.AsReadOnly(overlays))
                {
                    CustomCompositorDefinition =
                        layer.CustomCompositorDefinition is
                            { } compositor
                            ? SnapshotEffect(compositor)
                            : null
                };
        }

        var backgroundAudioTracks =
            new MediaCompositionExportAudioTrack[
                _backgroundAudioTracks.Count];
        for (int index = 0;
             index < _backgroundAudioTracks.Count;
             index++)
        {
            BackgroundAudioTrack track =
                _backgroundAudioTracks[index];
            backgroundAudioTracks[index] =
                new MediaCompositionExportAudioTrack(
                    track.ProGpuSourceUri,
                    track.OriginalDuration,
                    track.TrimTimeFromStart,
                    track.TrimTimeFromEnd,
                    track.Delay,
                    track.Volume,
                    SnapshotMap(track.UserData))
                {
                    AudioEffectDefinitions =
                        SnapshotEffects(
                            track.AudioEffectDefinitions)
                };
        }

        var encoding =
            new MediaCompositionEncodingProfile(
                profile.ContainerSubtype,
                profile.VideoSubtype,
                profile.AudioSubtype,
                profile.Width,
                profile.Height,
                profile.VideoBitrate,
                profile.FrameRateNumerator,
                profile.FrameRateDenominator,
                profile.AudioBitrate,
                profile.AudioSampleRate,
                profile.AudioChannelCount);
        return new MediaCompositionExportRequest(
            destinationPath,
            Array.AsReadOnly(clips),
            trimmingPreference ==
                MediaTrimmingPreference.Precise
                ? MediaCompositionTrimmingMode.Precise
                : MediaCompositionTrimmingMode.Fast,
            encoding,
            SnapshotMap(_userData))
        {
            BackgroundAudioTracks =
                Array.AsReadOnly(backgroundAudioTracks),
            OverlayLayers =
                Array.AsReadOnly(overlayLayers)
        };
    }

    private static MediaCompositionExportClip
        CreateExportClip(MediaClip clip)
    {
        VideoEncodingProperties video =
            clip.GetVideoEncodingProperties();
        AudioEncodingProperties? audio =
            clip.EmbeddedAudioTracks.Count == 0
                ? null
                : clip.EmbeddedAudioTracks[
                        checked(
                            (int)clip
                                .SelectedEmbeddedAudioTrackIndex)]
                    .GetAudioEncodingProperties();
        return new MediaCompositionExportClip(
            clip.ProGpuSourceUri,
            clip.OriginalDuration,
            clip.TrimTimeFromStart,
            clip.TrimTimeFromEnd,
            clip.Volume,
            clip.ProGpuColor is Color color
                ? PackColor(color)
                : null,
            SnapshotMap(clip.UserData))
        {
            SourceVideoWidth = video.Width,
            SourceVideoHeight = video.Height,
            SourceAudioSubtype = audio?.Subtype,
            SourceAudioTrackIndex =
                clip.SelectedEmbeddedAudioTrackIndex,
            SourceAudioBitrate =
                audio?.Bitrate ?? 0,
            SourceAudioSampleRate =
                audio?.SampleRate ?? 0,
            SourceAudioChannelCount =
                audio?.ChannelCount ?? 0,
            AudioEffectDefinitions =
                SnapshotEffects(
                    clip.AudioEffectDefinitions),
            VideoEffectDefinitions =
                SnapshotEffects(
                    clip.VideoEffectDefinitions)
        };
    }

    private static IReadOnlyDictionary<string, string>
        SnapshotMap(
            IEnumerable<KeyValuePair<string, string>> source) =>
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(
                source,
                StringComparer.Ordinal));

    private static IReadOnlyList<MediaCompositionEffectDefinition>
        SnapshotEffects(
            IEnumerable<IAudioEffectDefinition> source) =>
        source.Select(
                static effect =>
                    new MediaCompositionEffectDefinition(
                        effect.ActivatableClassId,
                        SnapshotProperties(
                            effect.Properties)))
            .ToArray();

    private static MediaCompositionEffectDefinition
        SnapshotEffect(
            IVideoCompositorDefinition source) =>
        new(
            source.ActivatableClassId,
            SnapshotProperties(source.Properties));

    private static IReadOnlyList<MediaCompositionEffectDefinition>
        SnapshotEffects(
            IEnumerable<IVideoEffectDefinition> source) =>
        source.Select(
                static effect =>
                    new MediaCompositionEffectDefinition(
                        effect.ActivatableClassId,
                        SnapshotProperties(
                            effect.Properties)))
            .ToArray();

    private static IReadOnlyDictionary<string, object?>
        SnapshotProperties(IPropertySet source)
    {
        var snapshot =
            new Dictionary<string, object?>(
                source.Count,
                StringComparer.Ordinal);
        foreach ((string key, object? value) in source)
        {
            snapshot.Add(
                key,
                SnapshotPropertyValue(value));
        }
        return new ReadOnlyDictionary<string, object?>(
            snapshot);
    }

    private static object? SnapshotPropertyValue(
        object? value) =>
        value switch
        {
            null or string or bool or byte or sbyte or
                short or ushort or int or uint or long or
                ulong or decimal => value,
            float number when float.IsFinite(number) =>
                number,
            double number when double.IsFinite(number) =>
                number,
            _ => throw new NotSupportedException(
                $"Media effect property type " +
                $"'{value.GetType().FullName}' is not supported.")
        };

    private static MediaComposition ParseProject(
        byte[] json)
    {
        using JsonDocument document =
            JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(
                "version",
                out JsonElement versionElement) ||
            versionElement.GetInt32() != ProjectFormatVersion)
        {
            throw new InvalidDataException(
                "The media composition project version is not supported.");
        }

        var composition = new MediaComposition();
        ReadStringMap(root, "userData", composition._userData);
        if (!root.TryGetProperty(
                "clips",
                out JsonElement clipsElement) ||
            clipsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "The media composition project does not contain a clip array.");
        }

        foreach (JsonElement element in
            clipsElement.EnumerateArray())
        {
            composition.Clips.Add(ReadClip(element));
        }

        if (root.TryGetProperty(
                "backgroundAudioTracks",
                out JsonElement tracksElement))
        {
            if (tracksElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "'backgroundAudioTracks' must be an array.");
            }
            foreach (JsonElement element in
                tracksElement.EnumerateArray())
            {
                Uri source = ReadAbsoluteUri(
                    element,
                    "sourceUri");
                var track = BackgroundAudioTrack.CreateFromUri(
                    source,
                    TimeSpan.FromTicks(
                        ReadNonNegativeInt64(
                            element,
                            "originalDurationTicks")));
                if (element.TryGetProperty(
                        "audioEncodingProperties",
                        out JsonElement encodingElement))
                {
                    track.SetProGpuEncodingProperties(
                        ReadAudioEncodingProperties(
                            encodingElement));
                }
                track.TrimTimeFromStart =
                    TimeSpan.FromTicks(
                        ReadNonNegativeInt64(
                            element,
                            "trimTimeFromStartTicks"));
                track.TrimTimeFromEnd =
                    TimeSpan.FromTicks(
                        ReadNonNegativeInt64(
                            element,
                            "trimTimeFromEndTicks"));
                if (element.TryGetProperty(
                        "delayTicks",
                        out JsonElement delayElement))
                {
                    track.Delay =
                        TimeSpan.FromTicks(
                            delayElement.GetInt64());
                }
                if (element.TryGetProperty(
                        "volume",
                        out JsonElement volumeElement))
                {
                    track.Volume =
                        volumeElement.GetDouble();
                }
                ReadStringMap(
                    element,
                    "userData",
                    track.UserData);
                ReadAudioEffectDefinitions(
                    element,
                    "audioEffectDefinitions",
                    track.AudioEffectDefinitions);
                composition.BackgroundAudioTracks.Add(track);
            }
        }

        if (root.TryGetProperty(
                "overlayLayers",
                out JsonElement layersElement))
        {
            if (layersElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "'overlayLayers' must be an array.");
            }
            foreach (JsonElement layerElement in
                layersElement.EnumerateArray())
            {
                MediaOverlayLayer layer;
                if (layerElement.TryGetProperty(
                        "customCompositorDefinition",
                        out JsonElement compositorElement))
                {
                    (string classId, PropertySet properties) =
                        ReadEffectDefinition(
                            compositorElement);
                    layer = new MediaOverlayLayer(
                        new VideoCompositorDefinition(
                            classId,
                            properties));
                }
                else
                {
                    layer = new MediaOverlayLayer();
                }

                if (!layerElement.TryGetProperty(
                        "overlays",
                        out JsonElement overlaysElement) ||
                    overlaysElement.ValueKind !=
                        JsonValueKind.Array)
                {
                    throw new InvalidDataException(
                        "An overlay layer must contain an overlay array.");
                }
                foreach (JsonElement overlayElement in
                    overlaysElement.EnumerateArray())
                {
                    MediaClip clip = ReadClip(
                        overlayElement.GetProperty("clip"));
                    var position = new Windows.Foundation.Rect(
                        ReadFiniteDouble(
                            overlayElement,
                            "positionX"),
                        ReadFiniteDouble(
                            overlayElement,
                            "positionY"),
                        ReadFiniteDouble(
                            overlayElement,
                            "positionWidth"),
                        ReadFiniteDouble(
                            overlayElement,
                            "positionHeight"));
                    var overlay = new MediaOverlay(
                        clip,
                        position,
                        ReadFiniteDouble(
                            overlayElement,
                            "opacity"));
                    if (overlayElement.TryGetProperty(
                            "delayTicks",
                            out JsonElement delayElement))
                    {
                        overlay.Delay =
                            TimeSpan.FromTicks(
                                delayElement.GetInt64());
                    }
                    if (overlayElement.TryGetProperty(
                            "audioEnabled",
                            out JsonElement audioElement))
                    {
                        overlay.AudioEnabled =
                            audioElement.GetBoolean();
                    }
                    layer.Overlays.Add(overlay);
                }
                composition.OverlayLayers.Add(layer);
            }
        }
        return composition;
    }

    private static MediaClip ReadClip(JsonElement element)
    {
        long durationTicks =
            ReadNonNegativeInt64(
                element,
                "originalDurationTicks");
        MediaClip clip;
        if (element.TryGetProperty(
                "sourceUri",
                out JsonElement uriElement))
        {
            string text =
                uriElement.GetString() ??
                throw new InvalidDataException(
                    "A clip source URI cannot be null.");
            if (!Uri.TryCreate(
                    text,
                    UriKind.Absolute,
                    out Uri? uri))
            {
                throw new InvalidDataException(
                    "A clip source URI must be absolute.");
            }
            clip = MediaClip.CreateFromUri(
                uri,
                TimeSpan.FromTicks(durationTicks));
        }
        else if (element.TryGetProperty(
                     "argbColor",
                     out JsonElement colorElement))
        {
            clip = MediaClip.CreateFromColor(
                UnpackColor(colorElement.GetUInt32()),
                TimeSpan.FromTicks(durationTicks));
        }
        else
        {
            throw new InvalidDataException(
                "A clip must contain a source URI or a color.");
        }

        if (element.TryGetProperty(
                "videoEncodingProperties",
                out JsonElement videoEncodingElement))
        {
            var embeddedAudio =
                new List<AudioEncodingProperties>();
            if (element.TryGetProperty(
                    "embeddedAudioTracks",
                    out JsonElement embeddedElement))
            {
                if (embeddedElement.ValueKind !=
                    JsonValueKind.Array)
                {
                    throw new InvalidDataException(
                        "'embeddedAudioTracks' must be an array.");
                }
                foreach (JsonElement audioElement in
                    embeddedElement.EnumerateArray())
                {
                    embeddedAudio.Add(
                        ReadAudioEncodingProperties(
                            audioElement));
                }
            }
            clip.SetProGpuEncodingProperties(
                ReadVideoEncodingProperties(
                    videoEncodingElement),
                embeddedAudio);
            if (element.TryGetProperty(
                    "selectedEmbeddedAudioTrackIndex",
                    out JsonElement selectedElement))
            {
                clip.SelectedEmbeddedAudioTrackIndex =
                    selectedElement.GetUInt32();
            }
        }

        clip.TrimTimeFromStart = TimeSpan.FromTicks(
            ReadNonNegativeInt64(
                element,
                "trimTimeFromStartTicks"));
        clip.TrimTimeFromEnd = TimeSpan.FromTicks(
            ReadNonNegativeInt64(
                element,
                "trimTimeFromEndTicks"));
        if (element.TryGetProperty(
                "volume",
                out JsonElement volumeElement))
        {
            clip.Volume = volumeElement.GetDouble();
        }
        ReadStringMap(
            element,
            "userData",
            clip.UserData);
        ReadAudioEffectDefinitions(
            element,
            "audioEffectDefinitions",
            clip.AudioEffectDefinitions);
        ReadVideoEffectDefinitions(
            element,
            "videoEffectDefinitions",
            clip.VideoEffectDefinitions);
        return clip;
    }

    private static double ReadFiniteDouble(
        JsonElement owner,
        string propertyName)
    {
        double value =
            owner.GetProperty(propertyName).GetDouble();
        if (!double.IsFinite(value))
        {
            throw new InvalidDataException(
                $"'{propertyName}' must be finite.");
        }
        return value;
    }

    private static Uri ReadAbsoluteUri(
        JsonElement owner,
        string propertyName)
    {
        if (!owner.TryGetProperty(
                propertyName,
                out JsonElement element) ||
            !Uri.TryCreate(
                element.GetString(),
                UriKind.Absolute,
                out Uri? result))
        {
            throw new InvalidDataException(
                $"'{propertyName}' must be an absolute URI.");
        }
        return result;
    }

    private static void WriteClip(
        Utf8JsonWriter writer,
        MediaClip clip)
    {
        writer.WriteStartObject();
        if (clip.ProGpuSourceUri is not null)
        {
            writer.WriteString(
                "sourceUri",
                clip.ProGpuSourceUri.AbsoluteUri);
        }
        if (clip.ProGpuColor is Color color)
        {
            writer.WriteNumber(
                "argbColor",
                PackColor(color));
        }
        writer.WriteNumber(
            "originalDurationTicks",
            clip.OriginalDuration.Ticks);
        writer.WriteNumber(
            "trimTimeFromStartTicks",
            clip.TrimTimeFromStart.Ticks);
        writer.WriteNumber(
            "trimTimeFromEndTicks",
            clip.TrimTimeFromEnd.Ticks);
        writer.WriteNumber("volume", clip.Volume);
        writer.WriteNumber(
            "selectedEmbeddedAudioTrackIndex",
            clip.SelectedEmbeddedAudioTrackIndex);
        writer.WritePropertyName(
            "videoEncodingProperties");
        WriteVideoEncodingProperties(
            writer,
            clip.GetVideoEncodingProperties());
        writer.WriteStartArray("embeddedAudioTracks");
        for (int index = 0;
             index < clip.EmbeddedAudioTracks.Count;
             index++)
        {
            WriteAudioEncodingProperties(
                writer,
                clip.EmbeddedAudioTracks[index]
                    .GetAudioEncodingProperties());
        }
        writer.WriteEndArray();
        WriteStringMap(
            writer,
            "userData",
            clip.UserData);
        WriteEffectDefinitions(
            writer,
            "audioEffectDefinitions",
            clip.AudioEffectDefinitions);
        WriteEffectDefinitions(
            writer,
            "videoEffectDefinitions",
            clip.VideoEffectDefinitions);
        writer.WriteEndObject();
    }

    private static void WriteVideoEncodingProperties(
        Utf8JsonWriter writer,
        VideoEncodingProperties properties)
    {
        writer.WriteStartObject();
        writer.WriteString("subtype", properties.Subtype);
        writer.WriteNumber("width", properties.Width);
        writer.WriteNumber("height", properties.Height);
        writer.WriteNumber("bitrate", properties.Bitrate);
        writer.WriteNumber(
            "frameRateNumerator",
            properties.FrameRate.Numerator);
        writer.WriteNumber(
            "frameRateDenominator",
            properties.FrameRate.Denominator);
        writer.WriteEndObject();
    }

    private static VideoEncodingProperties
        ReadVideoEncodingProperties(
            JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Video encoding properties must be an object.");
        }
        var properties = new VideoEncodingProperties
        {
            Subtype =
                element.GetProperty("subtype")
                    .GetString() ??
                string.Empty,
            Width =
                element.GetProperty("width")
                    .GetUInt32(),
            Height =
                element.GetProperty("height")
                    .GetUInt32(),
            Bitrate =
                element.GetProperty("bitrate")
                    .GetUInt32()
        };
        properties.FrameRate.Numerator =
            element.GetProperty(
                    "frameRateNumerator")
                .GetUInt32();
        properties.FrameRate.Denominator =
            element.GetProperty(
                    "frameRateDenominator")
                .GetUInt32();
        return properties;
    }

    private static void WriteAudioEncodingProperties(
        Utf8JsonWriter writer,
        AudioEncodingProperties properties)
    {
        writer.WriteStartObject();
        writer.WriteString("subtype", properties.Subtype);
        writer.WriteNumber("bitrate", properties.Bitrate);
        writer.WriteNumber(
            "sampleRate",
            properties.SampleRate);
        writer.WriteNumber(
            "channelCount",
            properties.ChannelCount);
        writer.WriteEndObject();
    }

    private static AudioEncodingProperties
        ReadAudioEncodingProperties(
            JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Audio encoding properties must be an object.");
        }
        return new AudioEncodingProperties
        {
            Subtype =
                element.GetProperty("subtype")
                    .GetString() ??
                string.Empty,
            Bitrate =
                element.GetProperty("bitrate")
                    .GetUInt32(),
            SampleRate =
                element.GetProperty("sampleRate")
                    .GetUInt32(),
            ChannelCount =
                element.GetProperty("channelCount")
                    .GetUInt32()
        };
    }

    private static void WriteEffectDefinitions(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<IAudioEffectDefinition> effects) =>
        WriteEffectDefinitionsCore(
            writer,
            propertyName,
            effects.Select(
                static effect =>
                    (effect.ActivatableClassId,
                     effect.Properties)));

    private static void WriteEffectDefinitions(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<IVideoEffectDefinition> effects) =>
        WriteEffectDefinitionsCore(
            writer,
            propertyName,
            effects.Select(
                static effect =>
                    (effect.ActivatableClassId,
                     effect.Properties)));

    private static void WriteEffectDefinitionsCore(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<(string ActivatableClassId,
            IPropertySet Properties)> effects)
    {
        writer.WriteStartArray(propertyName);
        foreach ((string classId, IPropertySet properties)
                 in effects)
        {
            WriteEffectDefinition(
                writer,
                classId,
                properties);
        }
        writer.WriteEndArray();
    }

    private static void WriteEffectDefinition(
        Utf8JsonWriter writer,
        string classId,
        IPropertySet properties)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "activatableClassId",
            classId);
        writer.WriteStartObject("properties");
        foreach ((string key, object? value) in properties)
        {
            writer.WritePropertyName(key);
            WritePropertyValue(writer, value);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WritePropertyValue(
        Utf8JsonWriter writer,
        object? value)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case null:
                writer.WriteString("type", "Null");
                writer.WriteNull("value");
                break;
            case string text:
                writer.WriteString("type", "String");
                writer.WriteString("value", text);
                break;
            case bool boolean:
                writer.WriteString("type", "Boolean");
                writer.WriteBoolean("value", boolean);
                break;
            case byte number:
                WriteNumber(writer, "Byte", (long)number);
                break;
            case sbyte number:
                WriteNumber(writer, "SByte", (long)number);
                break;
            case short number:
                WriteNumber(writer, "Int16", (long)number);
                break;
            case ushort number:
                WriteNumber(writer, "UInt16", (ulong)number);
                break;
            case int number:
                WriteNumber(writer, "Int32", (long)number);
                break;
            case uint number:
                WriteNumber(writer, "UInt32", (ulong)number);
                break;
            case long number:
                WriteNumber(writer, "Int64", number);
                break;
            case ulong number:
                WriteNumber(writer, "UInt64", number);
                break;
            case float number when float.IsFinite(number):
                WriteNumber(writer, "Single", number);
                break;
            case double number when double.IsFinite(number):
                WriteNumber(writer, "Double", number);
                break;
            case decimal number:
                WriteNumber(writer, "Decimal", number);
                break;
            default:
                throw new NotSupportedException(
                    $"Media effect property type " +
                    $"'{value.GetType().FullName}' is not supported.");
        }
        writer.WriteEndObject();
    }

    private static void WriteNumber(
        Utf8JsonWriter writer,
        string type,
        long value)
    {
        writer.WriteString("type", type);
        writer.WriteNumber("value", value);
    }

    private static void WriteNumber(
        Utf8JsonWriter writer,
        string type,
        ulong value)
    {
        writer.WriteString("type", type);
        writer.WriteNumber("value", value);
    }

    private static void WriteNumber(
        Utf8JsonWriter writer,
        string type,
        float value)
    {
        writer.WriteString("type", type);
        writer.WriteNumber("value", value);
    }

    private static void WriteNumber(
        Utf8JsonWriter writer,
        string type,
        double value)
    {
        writer.WriteString("type", type);
        writer.WriteNumber("value", value);
    }

    private static void WriteNumber(
        Utf8JsonWriter writer,
        string type,
        decimal value)
    {
        writer.WriteString("type", type);
        writer.WriteNumber("value", value);
    }

    private static void ReadAudioEffectDefinitions(
        JsonElement owner,
        string propertyName,
        IList<IAudioEffectDefinition> destination)
    {
        foreach ((string classId, PropertySet properties) in
                 ReadEffectDefinitions(owner, propertyName))
        {
            destination.Add(
                new AudioEffectDefinition(
                    classId,
                    properties));
        }
    }

    private static void ReadVideoEffectDefinitions(
        JsonElement owner,
        string propertyName,
        IList<IVideoEffectDefinition> destination)
    {
        foreach ((string classId, PropertySet properties) in
                 ReadEffectDefinitions(owner, propertyName))
        {
            destination.Add(
                new VideoEffectDefinition(
                    classId,
                    properties));
        }
    }

    private static IEnumerable<(string ActivatableClassId,
        PropertySet Properties)> ReadEffectDefinitions(
            JsonElement owner,
            string propertyName)
    {
        if (!owner.TryGetProperty(
                propertyName,
                out JsonElement effects))
        {
            yield break;
        }
        if (effects.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"'{propertyName}' must be an array.");
        }
        foreach (JsonElement effect in effects.EnumerateArray())
        {
            yield return ReadEffectDefinition(effect);
        }
    }

    private static (string ActivatableClassId,
        PropertySet Properties) ReadEffectDefinition(
            JsonElement effect)
    {
        string classId =
            effect.GetProperty(
                "activatableClassId").GetString() ??
            throw new InvalidDataException(
                "An effect class ID cannot be null.");
        var properties = new PropertySet();
        if (effect.TryGetProperty(
                "properties",
                out JsonElement values))
        {
            if (values.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "Effect properties must be an object.");
            }
            foreach (JsonProperty property in
                values.EnumerateObject())
            {
                properties.Add(
                    property.Name,
                    ReadPropertyValue(property.Value));
            }
        }
        return (classId, properties);
    }

    private static object? ReadPropertyValue(
        JsonElement wrapper)
    {
        string type =
            wrapper.GetProperty("type").GetString() ??
            throw new InvalidDataException(
                "An effect property type cannot be null.");
        JsonElement value = wrapper.GetProperty("value");
        return type switch
        {
            "Null" => null,
            "String" => value.GetString(),
            "Boolean" => value.GetBoolean(),
            "Byte" => value.GetByte(),
            "SByte" => value.GetSByte(),
            "Int16" => value.GetInt16(),
            "UInt16" => value.GetUInt16(),
            "Int32" => value.GetInt32(),
            "UInt32" => value.GetUInt32(),
            "Int64" => value.GetInt64(),
            "UInt64" => value.GetUInt64(),
            "Single" => value.GetSingle(),
            "Double" => value.GetDouble(),
            "Decimal" => value.GetDecimal(),
            _ => throw new InvalidDataException(
                $"Effect property type '{type}' is not supported.")
        };
    }

    private static void WriteStringMap(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<KeyValuePair<string, string>> values)
    {
        writer.WriteStartObject(propertyName);
        foreach ((string key, string value) in values)
        {
            writer.WriteString(key, value);
        }
        writer.WriteEndObject();
    }

    private static void ReadStringMap(
        JsonElement owner,
        string propertyName,
        IDictionary<string, string> destination)
    {
        if (!owner.TryGetProperty(
                propertyName,
                out JsonElement map))
        {
            return;
        }
        if (map.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"'{propertyName}' must be an object.");
        }
        foreach (JsonProperty property in
            map.EnumerateObject())
        {
            destination.Add(
                property.Name,
                property.Value.GetString() ??
                throw new InvalidDataException(
                    $"'{propertyName}.{property.Name}' must be a string."));
        }
    }

    private static long ReadNonNegativeInt64(
        JsonElement owner,
        string propertyName)
    {
        if (!owner.TryGetProperty(
                propertyName,
                out JsonElement value))
        {
            return 0;
        }
        long result = value.GetInt64();
        if (result < 0)
        {
            throw new InvalidDataException(
                $"'{propertyName}' cannot be negative.");
        }
        return result;
    }

    private static uint PackColor(Color value) =>
        (uint)value.A << 24 |
        (uint)value.R << 16 |
        (uint)value.G << 8 |
        value.B;

    private static Color UnpackColor(uint value) =>
        Color.FromArgb(
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value);

    private sealed class ClipCollection :
        Collection<MediaClip>
    {
        private readonly MediaComposition _owner;

        public ClipCollection(MediaComposition owner)
        {
            _owner = owner;
        }

        protected override void InsertItem(
            int index,
            MediaClip item)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (Contains(item))
            {
                throw new InvalidOperationException(
                    "A MediaClip can appear only once in a MediaComposition.");
            }
            item.Attach(_owner);
            try
            {
                base.InsertItem(index, item);
            }
            catch
            {
                item.Detach(_owner);
                throw;
            }
        }

        protected override void SetItem(
            int index,
            MediaClip item)
        {
            ArgumentNullException.ThrowIfNull(item);
            MediaClip previous = this[index];
            if (ReferenceEquals(previous, item))
            {
                return;
            }
            if (Contains(item))
            {
                throw new InvalidOperationException(
                    "A MediaClip can appear only once in a MediaComposition.");
            }
            item.Attach(_owner);
            try
            {
                base.SetItem(index, item);
                previous.Detach(_owner);
            }
            catch
            {
                item.Detach(_owner);
                throw;
            }
        }

        protected override void RemoveItem(int index)
        {
            MediaClip previous = this[index];
            base.RemoveItem(index);
            previous.Detach(_owner);
        }

        protected override void ClearItems()
        {
            MediaClip[] previous = this.ToArray();
            base.ClearItems();
            foreach (MediaClip clip in previous)
            {
                clip.Detach(_owner);
            }
        }
    }
}

public enum MediaTrimmingPreference
{
    Fast = 0,
    Precise = 1
}
