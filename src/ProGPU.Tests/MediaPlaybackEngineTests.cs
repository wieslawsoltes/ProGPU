using System.Numerics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Media3D;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Layout;
using ProGPU.Media;
using ProGPU.Media.Audio;
using ProGPU.Media.Diagnostics;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using ProGPU.Media.Extensibility;
using ProGPU.Media.Playback;
using ProGPU.Media.Rendering;
using ProGPU.Scene;
using ProGPU.Scene.Extensions;
using ProGPU.Tests.Headless;
using Silk.NET.WebGPU;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Streams;
using Xunit;

namespace ProGPU.Tests;

public sealed class MediaPlaybackEngineTests
{
    [Fact]
    public void PlaybackTrackApiUsesOfficialWinUiShape()
    {
        Assert.Equal(
            "Windows.Media.Core",
            typeof(AudioTrack).Namespace);
        Assert.Equal(
            "Windows.Media.Core",
            typeof(VideoTrack).Namespace);
        Assert.Equal(
            "Windows.Media.Playback",
            typeof(MediaPlaybackAudioTrackList).Namespace);
        Assert.Equal(
            "Windows.Media.Core",
            typeof(TimedMetadataTrack).Namespace);
        Assert.Equal(
            "Windows.Media.Playback",
            typeof(MediaPlaybackTimedMetadataTrackList)
                .Namespace);
        Assert.Equal(
            typeof(IReadOnlyList<AudioTrack>),
            Assert.Single(
                typeof(MediaPlaybackAudioTrackList)
                    .GetInterfaces(),
                static type =>
                    type == typeof(
                        IReadOnlyList<AudioTrack>)));
        Assert.Contains(
            typeof(ISingleSelectMediaTrackList),
            typeof(MediaPlaybackVideoTrackList)
                .GetInterfaces());
        Assert.Contains(
            typeof(IReadOnlyList<TimedMetadataTrack>),
            typeof(MediaPlaybackTimedMetadataTrackList)
                .GetInterfaces());
        Assert.Equal(
            typeof(MediaPlaybackAudioTrackList),
            typeof(MediaPlaybackItem)
                .GetProperty(
                    nameof(MediaPlaybackItem.AudioTracks))!
                .PropertyType);
        Assert.Equal(
            typeof(Windows.Foundation.TypedEventHandler<
                MediaPlaybackItem,
                IVectorChangedEventArgs>),
            typeof(MediaPlaybackItem)
                .GetEvent(
                    nameof(
                        MediaPlaybackItem
                            .AudioTracksChanged))!
                .EventHandlerType);
        Assert.Equal(
            typeof(MediaPlaybackTimedMetadataTrackList),
            typeof(MediaPlaybackItem)
                .GetProperty(
                    nameof(
                        MediaPlaybackItem
                            .TimedMetadataTracks))!
                .PropertyType);
        Assert.Equal(
            typeof(Windows.Foundation.TypedEventHandler<
                MediaPlaybackItem,
                IVectorChangedEventArgs>),
            typeof(MediaPlaybackItem)
                .GetEvent(
                    nameof(
                        MediaPlaybackItem
                            .TimedMetadataTracksChanged))!
                .EventHandlerType);
        Assert.Equal(
            typeof(IObservableVector<TimedMetadataTrack>),
            typeof(MediaSource)
                .GetProperty(
                    nameof(
                        MediaSource
                            .ExternalTimedMetadataTracks))!
                .PropertyType);
        Assert.Equal(
            typeof(IObservableVector<TimedTextSource>),
            typeof(MediaSource)
                .GetProperty(
                    nameof(
                        MediaSource
                            .ExternalTimedTextSources))!
                .PropertyType);
        Assert.Equal(
            typeof(Windows.Foundation.TypedEventHandler<
                TimedTextSource,
                TimedTextSourceResolveResultEventArgs>),
            typeof(TimedTextSource)
                .GetEvent(
                    nameof(TimedTextSource.Resolved))!
                .EventHandlerType);
        Assert.Equal(
            typeof(IReadOnlyList<TimedMetadataTrack>),
            typeof(
                    TimedTextSourceResolveResultEventArgs)
                .GetProperty(
                    nameof(
                        TimedTextSourceResolveResultEventArgs
                            .Tracks))!
                .PropertyType);
        Assert.Equal(
            typeof(IBuffer),
            typeof(DataCue)
                .GetProperty(nameof(DataCue.Data))!
                .PropertyType);
        Assert.Equal(
            typeof(PropertySet),
            typeof(DataCue)
                .GetProperty(nameof(DataCue.Properties))!
                .PropertyType);
        Assert.Equal(
            "Windows.Media.Core",
            typeof(TimedTextCue).Namespace);
        Assert.Equal(
            "Windows.Media.Core",
            typeof(TimedTextLine).Namespace);
        Assert.Equal(
            typeof(IList<TimedTextLine>),
            typeof(TimedTextCue)
                .GetProperty(nameof(TimedTextCue.Lines))!
                .PropertyType);
        Assert.Equal(
            typeof(TimedTextStyle),
            typeof(TimedTextCue)
                .GetProperty(nameof(TimedTextCue.CueStyle))!
                .PropertyType);
        Assert.Equal(
            typeof(TimedTextRegion),
            typeof(TimedTextCue)
                .GetProperty(nameof(TimedTextCue.CueRegion))!
                .PropertyType);
        Assert.Equal(
            typeof(IList<TimedTextSubformat>),
            typeof(TimedTextLine)
                .GetProperty(
                    nameof(TimedTextLine.Subformats))!
                .PropertyType);
        Assert.Equal(
            typeof(TimedTextStyle),
            typeof(TimedTextSubformat)
                .GetProperty(
                    nameof(
                        TimedTextSubformat
                            .SubformatStyle))!
                .PropertyType);
        Assert.Equal(
            typeof(TimedTextPoint),
            typeof(TimedTextRegion)
                .GetProperty(
                    nameof(TimedTextRegion.Position))!
                .PropertyType);
        Assert.Equal(
            typeof(TimedTextSize),
            typeof(TimedTextRegion)
                .GetProperty(
                    nameof(TimedTextRegion.Extent))!
                .PropertyType);
        Assert.Equal(
            typeof(TimedTextDouble),
            typeof(TimedTextStyle)
                .GetProperty(
                    nameof(TimedTextStyle.FontSize))!
                .PropertyType);
        Assert.Equal(400, (int)TimedTextWeight.Normal);
        Assert.Equal(700, (int)TimedTextWeight.Bold);
        Assert.Equal(
            2,
            (int)TimedTextLineAlignment.Center);
        Assert.Equal(
            6,
            (int)TimedTextWritingMode.TopBottom);
        Assert.Equal(0, (int)MediaTrackKind.Audio);
        Assert.Equal(1, (int)MediaTrackKind.Video);
        Assert.Equal(2, (int)MediaTrackKind.TimedMetadata);
    }

    [Fact]
    public void TimedTextFormattingApiMatchesOfficialContract()
    {
        Assert.Equal(
            [
                "Background",
                "Bouten",
                "FlowDirection",
                "FontAngleInDegrees",
                "FontFamily",
                "FontSize",
                "FontStyle",
                "FontWeight",
                "Foreground",
                "IsBackgroundAlwaysShown",
                "IsLineThroughEnabled",
                "IsOverlineEnabled",
                "IsTextCombined",
                "IsUnderlineEnabled",
                "LineAlignment",
                "Name",
                "OutlineColor",
                "OutlineRadius",
                "OutlineThickness",
                "Ruby"
            ],
            typeof(TimedTextStyle)
                .GetProperties()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "Background",
                "DisplayAlignment",
                "Extent",
                "IsOverflowClipped",
                "LineHeight",
                "Name",
                "Padding",
                "Position",
                "ScrollMode",
                "TextWrapping",
                "WritingMode",
                "ZIndex"
            ],
            typeof(TimedTextRegion)
                .GetProperties()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.False(
            typeof(TimedTextStyle)
                .GetProperty(nameof(TimedTextStyle.Bouten))!
                .CanWrite);
        Assert.False(
            typeof(TimedTextStyle)
                .GetProperty(nameof(TimedTextStyle.Ruby))!
                .CanWrite);
        Assert.Equal(
            [
                "Color",
                "Position",
                "Type"
            ],
            typeof(TimedTextBouten)
                .GetProperties()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "Align",
                "Position",
                "Reserve",
                "Text"
            ],
            typeof(TimedTextRuby)
                .GetProperties()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PlaybackItemProjectsProviderTracksAndSelection()
    {
        var registry = new MediaProviderRegistry();
        var factory =
            new RecordingProviderFactory(priority: 10);
        using IDisposable registration =
            registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource source =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/tracks.mp4"));
        var item = new MediaPlaybackItem(source);
        var audioChanges =
            new List<(CollectionChange Change, uint Index)>();
        int selectedChanges = 0;
        item.AudioTracksChanged +=
            (_, args) =>
                audioChanges.Add(
                    (args.CollectionChange, args.Index));
        item.AudioTracks.SelectedIndexChanged +=
            (_, _) => selectedChanges++;

        player.Source = item;

        RecordingProvider provider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        Assert.Equal(2, item.AudioTracks.Count);
        Assert.Equal(2u, item.AudioTracks.Size);
        Assert.Single(item.VideoTracks);
        Assert.Single(item.TimedMetadataTracks);
        Assert.Equal(0, item.AudioTracks.SelectedIndex);
        Assert.Equal(0, item.VideoTracks.SelectedIndex);
        Assert.Equal(
            [
                (CollectionChange.ItemInserted, 0u),
                (CollectionChange.ItemInserted, 1u)
            ],
            audioChanges);

        AudioTrack english = item.AudioTracks.GetAt(0);
        AudioTrack polish = item.AudioTracks[1];
        Assert.Same(item, english.PlaybackItem);
        Assert.Equal(MediaTrackKind.Audio, english.TrackKind);
        Assert.Equal("audio-en", english.Id);
        Assert.Equal("en-US", english.Language);
        Assert.Equal(
            MediaDecoderStatus.FullySupported,
            english.SupportInfo.DecoderStatus);
        AudioEncodingProperties audioEncoding =
            english.GetEncodingProperties();
        Assert.Equal("AAC", audioEncoding.Subtype);
        Assert.Equal(48_000u, audioEncoding.SampleRate);
        Assert.Equal(2u, audioEncoding.ChannelCount);
        Assert.True(
            item.AudioTracks.IndexOf(
                polish,
                out uint polishIndex));
        Assert.Equal(1u, polishIndex);
        var copied = new AudioTrack[2];
        Assert.Equal(
            2u,
            item.AudioTracks.GetMany(0, copied));
        Assert.Same(english, copied[0]);
        Assert.Same(polish, copied[1]);

        english.Label = "Commentary";
        int membershipChanges = audioChanges.Count;
        item.AudioTracks.SelectedIndex = 1;

        Assert.Equal(1, provider.TrackSelectionCalls);
        Assert.Equal(
            MediaPlaybackTrackKind.Audio,
            provider.LastSelectedTrackKind);
        Assert.Equal(1, provider.LastSelectedTrackIndex);
        Assert.Equal(1, item.AudioTracks.SelectedIndex);
        Assert.Equal(2, selectedChanges);
        Assert.Equal(membershipChanges, audioChanges.Count);
        Assert.Same(english, item.AudioTracks[0]);
        Assert.Equal("Commentary", item.AudioTracks[0].Label);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => item.AudioTracks.SelectedIndex = 2);

        VideoTrack video = item.VideoTracks[0];
        VideoEncodingProperties videoEncoding =
            video.GetEncodingProperties();
        Assert.Equal("H264", videoEncoding.Subtype);
        Assert.Equal(1920u, videoEncoding.Width);
        Assert.Equal(1080u, videoEncoding.Height);
        Assert.Equal(30u, videoEncoding.FrameRate.Numerator);
        Assert.Equal(1u, videoEncoding.FrameRate.Denominator);

        TimedMetadataTrack subtitles =
            item.TimedMetadataTracks[0];
        Assert.Same(item, subtitles.PlaybackItem);
        Assert.Equal(
            MediaTrackKind.TimedMetadata,
            subtitles.TrackKind);
        Assert.Equal(
            TimedMetadataKind.Subtitle,
            subtitles.TimedMetadataKind);
        Assert.Equal("text/vtt", subtitles.DispatchType);
        Assert.Equal(
            TimedMetadataTrackPresentationMode.Disabled,
            item.TimedMetadataTracks
                .GetPresentationMode(0));

        TimedMetadataPresentationModeChangedEventArgs?
            modeChanged = null;
        item.TimedMetadataTracks.PresentationModeChanged +=
            (_, args) => modeChanged = args;
        item.TimedMetadataTracks.SetPresentationMode(
            0,
            TimedMetadataTrackPresentationMode
                .ApplicationPresented);

        Assert.Equal(1, provider.TimedMetadataModeCalls);
        Assert.Equal(
            MediaPlaybackTimedMetadataPresentationMode
                .ApplicationPresented,
            provider.LastTimedMetadataMode);
        Assert.Same(subtitles, modeChanged?.Track);
        Assert.Equal(
            TimedMetadataTrackPresentationMode.Disabled,
            modeChanged?.OldPresentationMode);
        Assert.Equal(
            TimedMetadataTrackPresentationMode
                .ApplicationPresented,
            modeChanged?.NewPresentationMode);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => item.TimedMetadataTracks
                .SetPresentationMode(
                    1,
                    TimedMetadataTrackPresentationMode
                        .Hidden));
    }

    [Fact]
    public void
        ProviderTimedTextCuesPreserveIdentityAndSchedule()
    {
        var registry = new MediaProviderRegistry();
        var factory =
            new RecordingProviderFactory(priority: 10);
        using IDisposable registration =
            registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource source =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/provider-cues.mp4"));
        var item = new MediaPlaybackItem(source);

        player.Source = item;
        RecordingProvider provider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        TimedMetadataTrack track =
            Assert.Single(item.TimedMetadataTracks);
        int entered = 0;
        int exited = 0;
        track.CueEntered += (_, _) => entered++;
        track.CueExited += (_, _) => exited++;

        var sourceCues =
            new MediaPlaybackTimedMetadataCueDescriptor[]
            {
                new(
                    "subtitle-1",
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    "First GPU",
                    new MediaPlaybackTimedTextCuePresentation(
                        [
                            new(
                                "First GPU",
                                [
                                    new(
                                        6,
                                        3,
                                        new(
                                            FontWeight:
                                                MediaPlaybackTimedTextWeight
                                                    .Bold))
                                ])
                        ],
                        layout:
                            new(
                                RegionName: "captions",
                                LinePosition: 80d,
                                LinePositionUnit:
                                    MediaPlaybackTimedTextLinePositionUnit
                                        .Percentage,
                                TextPositionPercentage: 25d,
                                PositionAlignment:
                                    MediaPlaybackTimedTextAlignment
                                        .Center,
                                SizePercentage: 50d,
                                TextAlignment:
                                    MediaPlaybackTimedTextAlignment
                                        .Center,
                                WritingMode:
                                    MediaPlaybackTimedTextWritingMode
                                        .TopBottomRightLeft)))
            };
        var firstSnapshot =
            new MediaPlaybackTimedMetadataCueSnapshot(
                track.Id,
                sourceCues);
        sourceCues[0] = sourceCues[0] with
        {
            Text = "mutated caller buffer"
        };
        provider.ReportTimedMetadataCues(firstSnapshot);

        TimedTextCue cue =
            Assert.IsType<TimedTextCue>(
                Assert.Single(track.Cues));
        Assert.Equal("subtitle-1", cue.Id);
        TimedTextLine projectedLine =
            Assert.Single(cue.Lines);
        Assert.Equal("First GPU", projectedLine.Text);
        TimedTextSubformat projectedSubformat =
            Assert.Single(projectedLine.Subformats);
        Assert.Equal(6, projectedSubformat.StartIndex);
        Assert.Equal(3, projectedSubformat.Length);
        Assert.Equal(
            TimedTextWeight.Bold,
            projectedSubformat.SubformatStyle.FontWeight);
        Assert.Equal(
            TimedTextLineAlignment.Center,
            cue.CueStyle.LineAlignment);
        Assert.Equal("captions", cue.CueRegion.Name);
        Assert.Equal(
            TimedTextWritingMode.TopBottomRightLeft,
            cue.CueRegion.WritingMode);
        Assert.Equal(
            TimedTextUnit.Percentage,
            cue.CueRegion.Position.Unit);
        Assert.Equal(80d, cue.CueRegion.Position.X);
        Assert.Equal(0d, cue.CueRegion.Position.Y);
        Assert.Equal(50d, cue.CueRegion.Extent.Height);
        item.TimedMetadataTracks.SetPresentationMode(
            0,
            TimedMetadataTrackPresentationMode
                .ApplicationPresented);
        provider.Report(CreatePlaybackSnapshot(
            TimeSpan.FromSeconds(1.5)));

        Assert.Same(cue, Assert.Single(track.ActiveCues));
        Assert.Equal(1, entered);
        Assert.Equal(0, exited);

        provider.ReportTimedMetadataCues(
            new MediaPlaybackTimedMetadataCueSnapshot(
                track.Id,
                [
                    new(
                        "subtitle-1",
                        TimeSpan.FromSeconds(4),
                        TimeSpan.FromSeconds(3),
                        "Updated")
                ]));

        Assert.Same(cue, Assert.Single(track.Cues));
        Assert.Equal(
            TimeSpan.FromSeconds(4),
            cue.StartTime);
        Assert.Equal(
            "Updated",
            Assert.Single(cue.Lines).Text);
        Assert.Empty(cue.Lines[0].Subformats);
        Assert.Equal(
            TimedTextWeight.Normal,
            cue.CueStyle.FontWeight);
        Assert.Empty(track.ActiveCues);
        Assert.Equal(1, exited);

        provider.Report(CreatePlaybackSnapshot(
            TimeSpan.FromSeconds(4.5)));
        Assert.Same(cue, Assert.Single(track.ActiveCues));
        Assert.Equal(2, entered);

        provider.ReportTimedMetadataCues(
            new MediaPlaybackTimedMetadataCueSnapshot(
                track.Id,
                []));
        Assert.Empty(track.Cues);
        Assert.Empty(track.ActiveCues);
        Assert.Equal(2, exited);

        using MediaSource replacementSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/new-provider-cues.mp4"));
        var replacementItem =
            new MediaPlaybackItem(replacementSource);
        player.Source = replacementItem;
        provider.ReportTimedMetadataCues(firstSnapshot);

        Assert.Empty(track.Cues);
        Assert.Empty(
            replacementItem.TimedMetadataTracks[0].Cues);
    }

    [Fact]
    public void
        ProviderBinaryCuesProjectRetainedWinUiDataCueBuffers()
    {
        var registry = new MediaProviderRegistry();
        var factory =
            new RecordingProviderFactory(priority: 10);
        using IDisposable registration =
            registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource source =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/provider-data.mp4"));
        var item = new MediaPlaybackItem(source);

        player.Source = item;
        RecordingProvider provider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        provider.ReportTracks(
            new MediaPlaybackTracksSnapshot(
                audioTracks: null,
                selectedAudioTrackIndex: -1,
                videoTracks: null,
                selectedVideoTrackIndex: -1,
                timedMetadataTracks:
                [
                    new MediaPlaybackTrackDescriptor(
                        "metadata-data",
                        MediaPlaybackTrackKind
                            .TimedMetadata,
                        "Binary metadata",
                        "Data",
                        string.Empty,
                        new MediaPlaybackTrackEncoding(
                            "application/octet-stream"),
                        MediaPlaybackTrackSupport
                            .Supported,
                        MediaPlaybackTimedMetadataKind
                            .Data,
                        "application/octet-stream")
                ]));
        TimedMetadataTrack track =
            Assert.Single(item.TimedMetadataTracks);
        Assert.Equal(
            TimedMetadataKind.Data,
            track.TimedMetadataKind);

        byte[] callerBytes = [1, 2, 3, 4];
        var payload =
            new MediaPlaybackTimedMetadataCueData(
                callerBytes);
        callerBytes[0] = 255;
        var snapshot =
            new MediaPlaybackTimedMetadataCueSnapshot(
                track.Id,
                [
                    new
                        MediaPlaybackTimedMetadataCueDescriptor(
                            "data-1",
                            TimeSpan.FromSeconds(1),
                            TimeSpan.FromSeconds(2),
                            string.Empty,
                            Data: payload)
                ]);
        provider.ReportTimedMetadataCues(snapshot);

        DataCue cue =
            Assert.IsType<DataCue>(
                Assert.Single(track.Cues));
        Assert.Equal("data-1", cue.Id);
        var firstBuffer =
            Assert.IsType<
                Windows.Storage.Streams.Buffer>(
                    cue.Data);
        Assert.Equal(4u, firstBuffer.Length);
        Assert.Equal(
            [1, 2, 3, 4],
            firstBuffer.Memory.ToArray());
        Assert.Equal(
            [1, 2, 3, 4],
            payload.Bytes.ToArray());

        cue.Data =
            new Windows.Storage.Streams.Buffer(1);
        provider.ReportTimedMetadataCues(snapshot);

        Assert.Same(cue, Assert.Single(track.Cues));
        Assert.Same(firstBuffer, cue.Data);

        provider.ReportTimedMetadataCues(
            new MediaPlaybackTimedMetadataCueSnapshot(
                track.Id,
                [
                    new
                        MediaPlaybackTimedMetadataCueDescriptor(
                            "data-1",
                            TimeSpan.FromSeconds(4),
                            TimeSpan.FromSeconds(3),
                            string.Empty,
                            Data:
                                new
                                    MediaPlaybackTimedMetadataCueData(
                                        [5, 6, 7]))
                ]));

        Assert.Same(cue, Assert.Single(track.Cues));
        Assert.Equal(
            TimeSpan.FromSeconds(4),
            cue.StartTime);
        var updatedBuffer =
            Assert.IsType<
                Windows.Storage.Streams.Buffer>(
                    cue.Data);
        Assert.NotSame(firstBuffer, updatedBuffer);
        Assert.Equal(
            [5, 6, 7],
            updatedBuffer.Memory.ToArray());

        int entered = 0;
        track.CueEntered += (_, args) =>
        {
            Assert.Same(cue, args.Cue);
            entered++;
        };
        item.TimedMetadataTracks.SetPresentationMode(
            0,
            TimedMetadataTrackPresentationMode
                .ApplicationPresented);
        provider.Report(
            CreatePlaybackSnapshot(
                TimeSpan.FromSeconds(4.5)));

        Assert.Same(
            cue,
            Assert.Single(track.ActiveCues));
        Assert.Equal(1, entered);
    }

    [Fact]
    public void TimedMetadataCueSnapshotsValidateIdentityAndTiming()
    {
        Assert.Throws<ArgumentException>(
            () =>
                new MediaPlaybackTimedMetadataCueSnapshot(
                    "track",
                    [
                        new(
                            string.Empty,
                            TimeSpan.Zero,
                            TimeSpan.FromSeconds(1),
                            string.Empty)
                    ]));
        Assert.Throws<ArgumentException>(
            () =>
                new MediaPlaybackTimedMetadataCueSnapshot(
                    "track",
                    [
                        new(
                            "duplicate",
                            TimeSpan.Zero,
                            TimeSpan.FromSeconds(1),
                            string.Empty),
                        new(
                            "duplicate",
                            TimeSpan.FromSeconds(1),
                            TimeSpan.FromSeconds(1),
                            string.Empty)
                    ]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                new MediaPlaybackTimedMetadataCueSnapshot(
                    "track",
                    [
                        new(
                            "negative",
                            TimeSpan.FromTicks(-1),
                            TimeSpan.Zero,
                            string.Empty)
                    ]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                new MediaPlaybackTimedTextLineDescriptor(
                    "short",
                    [
                        new(
                            4,
                            2,
                            default)
                    ]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                new MediaPlaybackTimedTextCuePresentation(
                    [],
                    layout:
                        new(
                            SizePercentage: 101d)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                new MediaPlaybackTimedTextCuePresentation(
                    [],
                    region:
                        new(
                            Name: "captions",
                            LineCount: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                new MediaPlaybackTimedTextCuePresentation(
                    [],
                    region:
                        new(
                            Name: "captions",
                            WidthPercentage:
                                double.NaN)));
        Assert.Throws<ArgumentException>(
            () =>
                new MediaPlaybackTimedMetadataCueSnapshot(
                    "track",
                    [
                        new
                            MediaPlaybackTimedMetadataCueDescriptor(
                                "binary-with-text",
                                TimeSpan.Zero,
                                TimeSpan.FromSeconds(1),
                                string.Empty,
                                new
                                    MediaPlaybackTimedTextCuePresentation(
                                        []),
                                new
                                    MediaPlaybackTimedMetadataCueData(
                                        [1]))
                    ]));
    }

    [Fact]
    public void NativeTimedTextSnapshotsAccumulateStableReplayableCues()
    {
        var accumulator =
            new MediaPlaybackTimedTextCueAccumulator(
                "native-subtitles");

        MediaPlaybackTimedMetadataCueSnapshot first =
            accumulator.Update(
                TimeSpan.FromSeconds(1),
                ["First"],
                TimeSpan.FromSeconds(10));
        MediaPlaybackTimedMetadataCueDescriptor firstCue =
            Assert.Single(first.Cues);
        Assert.Equal(
            "native-subtitles:10000000:0",
            firstCue.CueId);
        Assert.Equal(
            TimeSpan.FromSeconds(9),
            firstCue.Duration);

        MediaPlaybackTimedMetadataCueSnapshot second =
            accumulator.Update(
                TimeSpan.FromSeconds(3),
                ["Second"],
                TimeSpan.FromSeconds(10));
        Assert.Collection(
            second.Cues,
            cue =>
            {
                Assert.Equal(firstCue.CueId, cue.CueId);
                Assert.Equal(
                    TimeSpan.FromSeconds(2),
                    cue.Duration);
            },
            cue =>
            {
                Assert.Equal(
                    "native-subtitles:30000000:0",
                    cue.CueId);
                Assert.Equal("Second", cue.Text);
            });

        MediaPlaybackTimedMetadataCueSnapshot replay =
            accumulator.Update(
                TimeSpan.FromSeconds(1),
                ["First updated"],
                TimeSpan.FromSeconds(10));
        Assert.Equal(2, replay.Cues.Count);
        Assert.Equal(firstCue.CueId, replay.Cues[0].CueId);
        Assert.Equal(
            "First updated",
            replay.Cues[0].Text);

        MediaPlaybackTimedMetadataCueSnapshot flushed =
            accumulator.Flush(TimeSpan.FromSeconds(2));
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            flushed.Cues[0].Duration);
    }

    [Fact]
    public async Task
        PlaybackEnginePublishesImmutableTrackSnapshots()
    {
        var registry = new MediaProviderRegistry();
        var factory =
            new RecordingProviderFactory(priority: 10);
        using IDisposable registration =
            registry.Register(factory);
        using var engine = new MediaPlaybackEngine(
            registry,
            new MediaEffectRegistry());
        using MediaSourceDescriptor source =
            MediaSourceDescriptor.FromUri(
                new Uri(
                    "https://example.invalid/engine-tracks.mp4"));
        var snapshots =
            new List<MediaPlaybackTracksSnapshot>();
        engine.TracksChanged +=
            (_, args) => snapshots.Add(args.Tracks);

        await engine.SetSourceAsync(source);

        Assert.Equal(2, engine.Tracks.AudioTracks.Count);
        Assert.Equal(
            "audio-en",
            engine.Tracks.AudioTracks[0].ProviderTrackId);
        Assert.Equal(2, snapshots.Count);
        Assert.Empty(snapshots[0].AudioTracks);
        Assert.Equal(2, snapshots[1].AudioTracks.Count);

        engine.SelectTrack(
            MediaPlaybackTrackKind.Audio,
            1);

        Assert.Equal(
            1,
            engine.Tracks.SelectedAudioTrackIndex);
        Assert.Equal(3, snapshots.Count);
        Assert.Throws<NotSupportedException>(
            () => engine.SelectTrack(
                MediaPlaybackTrackKind.TimedMetadata,
                -1));

        RecordingProvider firstProvider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        using MediaSourceDescriptor replacement =
            MediaSourceDescriptor.FromUri(
                new Uri(
                    "https://example.invalid/replacement-tracks.mp4"));
        await engine.SetSourceAsync(replacement);
        RecordingProvider replacementProvider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        Assert.NotSame(firstProvider, replacementProvider);

        firstProvider.ReportTracks(
            MediaPlaybackTracksSnapshot.Empty);

        Assert.Equal(2, engine.Tracks.AudioTracks.Count);
        Assert.Equal(
            "audio-en",
            engine.Tracks.AudioTracks[0].ProviderTrackId);

        await engine.SetSourceAsync(null);
        Assert.Empty(engine.Tracks.AudioTracks);
        Assert.Equal(-1, engine.Tracks.SelectedAudioTrackIndex);
    }

    [Fact]
    public void PlaybackTrackSelectionFollowsCurrentItemLifetime()
    {
        var registry = new MediaProviderRegistry();
        var factory =
            new RecordingProviderFactory(priority: 10);
        using IDisposable registration =
            registry.Register(factory);
        var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource firstSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/first-tracks.mp4"));
        using MediaSource secondSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/second-tracks.mp4"));
        var firstItem = new MediaPlaybackItem(firstSource);
        var secondItem = new MediaPlaybackItem(secondSource);

        player.Source = firstItem;
        RecordingProvider firstProvider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        firstItem.AudioTracks.SelectedIndex = 1;
        Assert.Equal(1, firstProvider.TrackSelectionCalls);
        firstItem.TimedMetadataTracks.SetPresentationMode(
            0,
            TimedMetadataTrackPresentationMode
                .ApplicationPresented);
        Assert.Equal(1, firstProvider.TimedMetadataModeCalls);

        player.Source = secondItem;
        RecordingProvider secondProvider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        Assert.NotSame(firstProvider, secondProvider);

        firstItem.AudioTracks.SelectedIndex = 0;
        Assert.Equal(0, secondProvider.TrackSelectionCalls);
        firstItem.TimedMetadataTracks.SetPresentationMode(
            0,
            TimedMetadataTrackPresentationMode.Hidden);
        Assert.Equal(
            0,
            secondProvider.TimedMetadataModeCalls);

        secondItem.AudioTracks.SelectedIndex = 1;
        Assert.Equal(1, secondProvider.TrackSelectionCalls);
        secondItem.TimedMetadataTracks.SetPresentationMode(
            0,
            TimedMetadataTrackPresentationMode
                .ApplicationPresented);
        Assert.Equal(
            1,
            secondProvider.TimedMetadataModeCalls);

        player.Dispose();
        secondItem.AudioTracks.SelectedIndex = 0;
        Assert.Equal(1, secondProvider.TrackSelectionCalls);
        secondItem.TimedMetadataTracks.SetPresentationMode(
            0,
            TimedMetadataTrackPresentationMode.Hidden);
        Assert.Equal(
            1,
            secondProvider.TimedMetadataModeCalls);
    }

    [Fact]
    public void CustomTimedMetadataTrackOwnsItsCueCollection()
    {
        var track = new TimedMetadataTrack(
            "chapters",
            "en-US",
            TimedMetadataKind.Chapter)
        {
            Label = "Chapters"
        };
        var cue = new RecordingMediaCue
        {
            Id = "chapter-1",
            StartTime = TimeSpan.FromSeconds(3),
            Duration = TimeSpan.FromSeconds(12)
        };

        track.AddCue(cue);
        track.AddCue(cue);

        Assert.Single(track.Cues);
        Assert.Empty(track.ActiveCues);
        Assert.Same(cue, track.Cues[0]);
        Assert.Equal("chapters", track.Id);
        Assert.Equal("en-US", track.Language);
        Assert.Equal("Chapters", track.Label);
        Assert.Null(track.PlaybackItem);
        Assert.Equal(
            TimedMetadataKind.Chapter,
            track.TimedMetadataKind);

        track.RemoveCue(cue);

        Assert.Empty(track.Cues);
    }

    [Fact]
    public void
        ExternalTimedMetadataTracksScheduleCuesAcrossModesAndSeeks()
    {
        var registry = new MediaProviderRegistry();
        var factory =
            new RecordingProviderFactory(priority: 10);
        using IDisposable registration =
            registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource source =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/cues.mp4"));
        var track = new TimedMetadataTrack(
            "application-data",
            "en-US",
            TimedMetadataKind.Data);
        var cue = new DataCue
        {
            Id = "cue-1",
            StartTime = TimeSpan.FromSeconds(1),
            Duration = TimeSpan.FromSeconds(2),
            Data = new Windows.Storage.Streams.Buffer(4)
            {
                Length = 4
            }
        };
        cue.Properties["kind"] = "marker";
        track.AddCue(cue);
        var sourceChanges =
            new List<(CollectionChange Change, uint Index)>();
        source.ExternalTimedMetadataTracks.VectorChanged +=
            (_, args) =>
                sourceChanges.Add(
                    (args.CollectionChange, args.Index));

        source.ExternalTimedMetadataTracks.Add(track);
        var item = new MediaPlaybackItem(source);

        Assert.Equal(
            [(CollectionChange.ItemInserted, 0u)],
            sourceChanges);
        Assert.Same(
            track,
            Assert.Single(
                source.ExternalTimedMetadataTracks));
        Assert.Same(item, track.PlaybackItem);
        Assert.Same(
            track,
            Assert.Single(item.TimedMetadataTracks));
        Assert.Throws<InvalidOperationException>(
            () => source.ExternalTimedMetadataTracks.Add(
                track));
        using (MediaSource otherSource =
               MediaSource.CreateFromUri(
                   new Uri(
                       "https://example.invalid/other-cues.mp4")))
        {
            Assert.Throws<InvalidOperationException>(
                () => otherSource
                    .ExternalTimedMetadataTracks.Add(track));
            Assert.Empty(
                otherSource.ExternalTimedMetadataTracks);
        }
        Assert.Same(item, track.PlaybackItem);

        int entered = 0;
        int exited = 0;
        track.CueEntered += (_, args) =>
        {
            Assert.Same(cue, args.Cue);
            entered++;
        };
        track.CueExited += (_, args) =>
        {
            Assert.Same(cue, args.Cue);
            exited++;
        };

        player.Source = item;
        RecordingProvider provider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        Assert.Equal(2, item.TimedMetadataTracks.Count);
        Assert.Same(track, item.TimedMetadataTracks[1]);

        provider.Report(CreatePlaybackSnapshot(
            TimeSpan.FromSeconds(1.5)));

        Assert.Empty(track.ActiveCues);
        Assert.Equal(0, entered);
        Assert.Equal(0, exited);

        item.TimedMetadataTracks.SetPresentationMode(
            1,
            TimedMetadataTrackPresentationMode
                .ApplicationPresented);

        Assert.Equal(0, provider.TimedMetadataModeCalls);
        Assert.Same(cue, Assert.Single(track.ActiveCues));
        Assert.Equal(1, entered);
        Assert.Equal(0, exited);

        item.TimedMetadataTracks.SetPresentationMode(
            1,
            TimedMetadataTrackPresentationMode.Disabled);

        Assert.Empty(track.ActiveCues);
        Assert.Equal(1, entered);
        Assert.Equal(0, exited);

        item.TimedMetadataTracks.SetPresentationMode(
            1,
            TimedMetadataTrackPresentationMode.Hidden);
        provider.Report(CreatePlaybackSnapshot(
            TimeSpan.FromSeconds(3)));

        Assert.Empty(track.ActiveCues);
        Assert.Equal(2, entered);
        Assert.Equal(1, exited);

        provider.Report(CreatePlaybackSnapshot(
            TimeSpan.FromSeconds(1.5)));

        Assert.Same(cue, Assert.Single(track.ActiveCues));
        Assert.Equal(3, entered);

        cue.StartTime = TimeSpan.FromSeconds(5);

        Assert.Empty(track.ActiveCues);
        Assert.Equal(2, exited);

        provider.Report(CreatePlaybackSnapshot(
            TimeSpan.FromSeconds(5.5)));

        Assert.Same(cue, Assert.Single(track.ActiveCues));
        Assert.Equal(4, entered);
        Assert.Equal("marker", cue.Properties["kind"]);

        using MediaSource replacementSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/replacement-cues.mp4"));
        var replacementItem =
            new MediaPlaybackItem(replacementSource);
        player.Source = replacementItem;

        Assert.Empty(track.ActiveCues);
        Assert.Equal(2, exited);

        provider.Report(CreatePlaybackSnapshot(
            TimeSpan.FromSeconds(5.5)));

        Assert.Empty(track.ActiveCues);
        Assert.Equal(4, entered);

        source.ExternalTimedMetadataTracks.Remove(track);

        Assert.Null(track.PlaybackItem);
        Assert.Empty(track.ActiveCues);
        Assert.Single(item.TimedMetadataTracks);
        Assert.Equal(2, sourceChanges.Count);
        Assert.Equal(
            (CollectionChange.ItemRemoved, 0u),
            sourceChanges[1]);
    }

    [Fact]
    public async Task
        ExternalTimedTextSourceResolvesWebVttIntoExternalTrack()
    {
        const string WebVtt =
            "\uFEFFWEBVTT - ProGPU test\n\n" +
            "NOTE ignored metadata\nignored\n\n" +
            "intro\n" +
            "00:00:01.000 --> 00:00:03.500 " +
            "line:20%,center position:30%,start " +
            "size:60% align:center\n" +
            "First <b>bold</b>\n" +
            "<i>second</i>\n\n";
        using var subtitleStream =
            new RandomAccessStream(
                new MemoryStream(
                    Encoding.UTF8.GetBytes(WebVtt)));
        TimedTextSource textSource =
            TimedTextSource.CreateFromStream(
                subtitleStream,
                "en-US");
        var completion =
            new TaskCompletionSource<
                TimedTextSourceResolveResultEventArgs>(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        textSource.Resolved +=
            (_, args) =>
                completion.TrySetResult(args);

        using MediaSource source =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/video.mp4"));
        var sourceChanges =
            new List<(CollectionChange Change, uint Index)>();
        source.ExternalTimedTextSources.VectorChanged +=
            (_, args) =>
                sourceChanges.Add(
                    (args.CollectionChange, args.Index));
        var item = new MediaPlaybackItem(source);
        source.ExternalTimedTextSources.Add(textSource);

        TimedTextSourceResolveResultEventArgs result =
            await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

        Assert.Null(result.Error);
        TimedMetadataTrack track =
            Assert.Single(result.Tracks);
        Assert.Same(
            track,
            Assert.Single(
                source.ExternalTimedMetadataTracks));
        Assert.Equal("en-US", track.Language);
        Assert.Equal(
            TimedMetadataKind.Subtitle,
            track.TimedMetadataKind);
        TimedTextCue cue =
            Assert.IsType<TimedTextCue>(
                Assert.Single(track.Cues));
        Assert.Equal("intro", cue.Id);
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            cue.StartTime);
        Assert.Equal(
            TimeSpan.FromSeconds(2.5),
            cue.Duration);
        Assert.Equal(2, cue.Lines.Count);
        Assert.Equal("First bold", cue.Lines[0].Text);
        Assert.Equal("second", cue.Lines[1].Text);
        TimedTextSubformat bold =
            Assert.Single(cue.Lines[0].Subformats);
        Assert.Equal(6, bold.StartIndex);
        Assert.Equal(4, bold.Length);
        Assert.Equal(
            TimedTextWeight.Bold,
            bold.SubformatStyle.FontWeight);
        TimedTextSubformat italic =
            Assert.Single(cue.Lines[1].Subformats);
        Assert.Equal(
            TimedTextFontStyle.Italic,
            italic.SubformatStyle.FontStyle);
        Assert.Equal(
            TimedTextLineAlignment.Center,
            cue.CueStyle.LineAlignment);
        Assert.Equal(
            TimedTextUnit.Percentage,
            cue.CueRegion.Extent.Unit);
        Assert.Equal(60d, cue.CueRegion.Extent.Width);
        Assert.Equal(
            [(CollectionChange.ItemInserted, 0u)],
            sourceChanges);

        Assert.Same(
            track,
            Assert.Single(item.TimedMetadataTracks));
        Assert.Same(item, track.PlaybackItem);

        source.ExternalTimedTextSources.Remove(textSource);

        Assert.Empty(source.ExternalTimedTextSources);
        Assert.Empty(source.ExternalTimedMetadataTracks);
        Assert.Empty(item.TimedMetadataTracks);
        Assert.Null(track.PlaybackItem);
        Assert.Equal(
            [
                (CollectionChange.ItemInserted, 0u),
                (CollectionChange.ItemRemoved, 0u)
            ],
            sourceChanges);
    }

    [Fact]
    public async Task
        ExternalTimedTextSourceReportsFormatAndIndexErrors()
    {
        using var invalidStream =
            new RandomAccessStream(
                new MemoryStream(
                    Encoding.UTF8.GetBytes(
                        "not WebVTT")));
        TimedTextSource invalid =
            TimedTextSource.CreateFromStream(
                invalidStream);
        var invalidCompletion =
            new TaskCompletionSource<
                TimedTextSourceResolveResultEventArgs>(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        invalid.Resolved +=
            (_, args) =>
                invalidCompletion.TrySetResult(args);
        using MediaSource source =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/video.mp4"));
        source.ExternalTimedTextSources.Add(invalid);

        TimedTextSourceResolveResultEventArgs invalidResult =
            await invalidCompletion.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

        Assert.Empty(invalidResult.Tracks);
        Assert.Equal(
            TimedMetadataTrackErrorCode.DataFormatError,
            Assert.IsType<TimedMetadataTrackError>(
                    invalidResult.Error)
                .ErrorCode);
        Assert.Empty(source.ExternalTimedMetadataTracks);

        using var imageStream =
            new RandomAccessStream(
                new MemoryStream([1, 2, 3]));
        using var indexStream =
            new RandomAccessStream(
                new MemoryStream([4, 5, 6]));
        TimedTextSource indexed =
            TimedTextSource.CreateFromStreamWithIndex(
                imageStream,
                indexStream);
        var indexedCompletion =
            new TaskCompletionSource<
                TimedTextSourceResolveResultEventArgs>(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        indexed.Resolved +=
            (_, args) =>
                indexedCompletion.TrySetResult(args);
        source.ExternalTimedTextSources.Add(indexed);

        TimedTextSourceResolveResultEventArgs indexedResult =
            await indexedCompletion.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

        Assert.Empty(indexedResult.Tracks);
        Assert.Equal(
            TimedMetadataTrackErrorCode.InternalError,
            Assert.IsType<TimedMetadataTrackError>(
                    indexedResult.Error)
                .ErrorCode);
        Assert.IsType<NotSupportedException>(
            indexedResult.Error!.ExtendedError);
    }

    [Fact]
    public void WebVttDocumentParserSkipsMalformedCueBlocks()
    {
        WebVttDocument document =
            WebVttDocumentParser.Parse(
                "WEBVTT\n\n" +
                "bad\nnot timing\npayload\n\n" +
                "00:01.250 --> 00:02.500\nvalid\n");

        WebVttDocumentCue cue =
            Assert.Single(document.Cues);
        Assert.Equal(
            TimeSpan.FromSeconds(1.25),
            cue.StartTime);
        Assert.Equal(
            TimeSpan.FromSeconds(1.25),
            cue.Duration);
        Assert.Equal("valid", cue.Text);
    }

    [Fact]
    public void
        WebVttDocumentParserResolvesExactRegionDefinitions()
    {
        WebVttDocument document =
            WebVttDocumentParser.Parse(
                "WEBVTT\n\n" +
                "REGION\n" +
                "id:chat width:80%\n\n" +
                "REGION\n" +
                "id:chat width:40% lines:4 " +
                "regionanchor:25%,100% " +
                "viewportanchor:50%,90% scroll:up\n\n" +
                "00:00.000 --> 00:01.000 " +
                "region:chat position:20%,center\n" +
                "eligible\n\n" +
                "00:01.000 --> 00:02.000 " +
                "line:2 region:chat\n" +
                "line drops out\n\n" +
                "00:02.000 --> 00:03.000 " +
                "region:chat size:100%\n" +
                "full size remains\n\n" +
                "00:03.000 --> 00:04.000 " +
                "region:chat size:99%\n" +
                "sized drops out\n");

        Assert.Equal(4, document.Cues.Count);
        WebVttDocumentCue eligible =
            document.Cues[0];
        MediaPlaybackTimedTextRegionDescriptor region =
            Assert.IsType<
                MediaPlaybackTimedTextRegionDescriptor>(
                    eligible.Presentation.Region);
        Assert.Equal("chat", region.Name);
        Assert.Equal(40d, region.WidthPercentage);
        Assert.Equal(4, region.LineCount);
        Assert.Equal(
            25d,
            region.RegionAnchorXPercentage);
        Assert.Equal(
            100d,
            region.RegionAnchorYPercentage);
        Assert.Equal(
            50d,
            region.ViewportAnchorXPercentage);
        Assert.Equal(
            90d,
            region.ViewportAnchorYPercentage);
        Assert.True(region.ScrollUp);
        Assert.Equal(
            "chat",
            eligible.Presentation.Layout.RegionName);

        Assert.Null(
            document.Cues[1].Presentation.Region);
        Assert.Equal(
            string.Empty,
            document.Cues[1]
                .Presentation.Layout.RegionName);
        Assert.NotNull(
            document.Cues[2].Presentation.Region);
        Assert.Null(
            document.Cues[3].Presentation.Region);

        var descriptor =
            new MediaPlaybackTimedMetadataCueDescriptor(
                "region-cue",
                eligible.StartTime,
                eligible.Duration,
                eligible.Text,
                eligible.Presentation);
        var cue = new TimedTextCue();
        cue.ApplyProviderState(in descriptor);

        Assert.Equal("chat", cue.CueRegion.Name);
        Assert.Equal(
            TimedTextUnit.Percentage,
            cue.CueRegion.Position.Unit);
        Assert.Equal(40d, cue.CueRegion.Position.X);
        Assert.Equal(90d, cue.CueRegion.Position.Y);
        Assert.Equal(
            TimedTextUnit.Percentage,
            cue.CueRegion.Extent.Unit);
        Assert.Equal(40d, cue.CueRegion.Extent.Width);
        Assert.Equal(0d, cue.CueRegion.Extent.Height);
        Assert.Equal(
            TimedTextScrollMode.Rollup,
            cue.CueRegion.ScrollMode);
        Assert.Equal(
            TimedTextWrapping.Wrap,
            cue.CueRegion.TextWrapping);
        Assert.True(cue.CueRegion.IsOverflowClipped);
        Assert.Equal(region, cue.ProviderRegion);
    }

    [Fact]
    public void
        WebVttDocumentParserProjectsRubyAnnotationsThroughWinUi()
    {
        WebVttDocument document =
            WebVttDocumentParser.Parse(
                "WEBVTT\n\n" +
                "00:00.000 --> 00:01.000\n" +
                "Learn <ruby>日<rt>に</rt>" +
                "本<rt>ほん</rt></ruby>!\n\n" +
                "00:01.000 --> 00:02.000\n" +
                "<ruby>漢<rt>かん</ruby>\n");

        Assert.Equal(2, document.Cues.Count);
        WebVttDocumentCue first =
            document.Cues[0];
        Assert.Equal("Learn 日本!", first.Text);
        MediaPlaybackTimedTextLineDescriptor firstLine =
            Assert.Single(first.Presentation.Lines);
        Assert.Equal(first.Text, firstLine.Text);
        Assert.Equal(2, firstLine.Subformats.Count);

        MediaPlaybackTimedTextSubformatDescriptor
            firstRuby = firstLine.Subformats[0];
        Assert.Equal(6, firstRuby.StartIndex);
        Assert.Equal(1, firstRuby.Length);
        MediaPlaybackTimedTextRubyDescriptor
            firstAnnotation =
                Assert.IsType<
                    MediaPlaybackTimedTextRubyDescriptor>(
                        firstRuby.Style.Ruby);
        Assert.Equal("に", firstAnnotation.Text);
        Assert.Equal(
            MediaPlaybackTimedTextRubyPosition.Before,
            firstAnnotation.Position);
        Assert.Equal(
            MediaPlaybackTimedTextRubyReserve.None,
            firstAnnotation.Reserve);
        Assert.Equal(
            MediaPlaybackTimedTextRubyAlign.Center,
            firstAnnotation.Align);

        MediaPlaybackTimedTextSubformatDescriptor
            secondRuby = firstLine.Subformats[1];
        Assert.Equal(7, secondRuby.StartIndex);
        Assert.Equal(1, secondRuby.Length);
        Assert.Equal(
            "ほん",
            Assert.IsType<
                    MediaPlaybackTimedTextRubyDescriptor>(
                        secondRuby.Style.Ruby)
                .Text);

        WebVttDocumentCue omittedEndTag =
            document.Cues[1];
        Assert.Equal("漢", omittedEndTag.Text);
        Assert.Equal(
            "かん",
            Assert.IsType<
                    MediaPlaybackTimedTextRubyDescriptor>(
                        Assert.Single(
                                Assert.Single(
                                        omittedEndTag
                                            .Presentation
                                            .Lines)
                                    .Subformats)
                            .Style.Ruby)
                .Text);

        var descriptor =
            new MediaPlaybackTimedMetadataCueDescriptor(
                "ruby-cue",
                first.StartTime,
                first.Duration,
                first.Text,
                first.Presentation);
        var cue = new TimedTextCue();
        cue.ApplyProviderState(in descriptor);

        TimedTextLine projectedLine =
            Assert.Single(cue.Lines);
        Assert.Equal("Learn 日本!", projectedLine.Text);
        Assert.Equal(2, projectedLine.Subformats.Count);
        TimedTextRuby projectedRuby =
            projectedLine.Subformats[0]
                .SubformatStyle.Ruby;
        Assert.Equal("に", projectedRuby.Text);
        Assert.Equal(
            TimedTextRubyPosition.Before,
            projectedRuby.Position);
        Assert.Equal(
            TimedTextRubyReserve.None,
            projectedRuby.Reserve);
        Assert.Equal(
            TimedTextRubyAlign.Center,
            projectedRuby.Align);

        var plainPresentation =
            new MediaPlaybackTimedTextCuePresentation(
                [
                    new
                        MediaPlaybackTimedTextLineDescriptor(
                            "plain",
                            [
                                new
                                    MediaPlaybackTimedTextSubformatDescriptor(
                                        0,
                                        5,
                                        new
                                            MediaPlaybackTimedTextStyle(
                                                FontWeight:
                                                    MediaPlaybackTimedTextWeight
                                                        .Bold))
                            ])
                ]);
        var plainDescriptor =
            new MediaPlaybackTimedMetadataCueDescriptor(
                "plain-cue",
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                "plain",
                plainPresentation);
        cue.ApplyProviderState(in plainDescriptor);

        TimedTextStyle resetStyle =
            Assert.Single(
                    Assert.Single(cue.Lines).Subformats)
                .SubformatStyle;
        Assert.Equal(string.Empty, resetStyle.Ruby.Text);
        Assert.Equal(
            TimedTextRubyPosition.Before,
            resetStyle.Ruby.Position);
        Assert.Equal(
            TimedTextRubyReserve.None,
            resetStyle.Ruby.Reserve);
        Assert.Equal(
            TimedTextRubyAlign.Center,
            resetStyle.Ruby.Align);
    }

    [Fact]
    public void
        TimedCueTimelineSteadyForwardUpdatesAllocateNothing()
    {
        var client = new RecordingTimedCueTimelineClient();
        var timeline =
            new MediaTimedCueTimeline<RecordingMediaCue>(
                client);
        timeline.AddCue(
            new RecordingMediaCue
            {
                StartTime = TimeSpan.FromMinutes(1),
                Duration = TimeSpan.FromSeconds(1)
            });
        timeline.Synchronize(TimeSpan.Zero, enabled: true);
        for (int index = 1; index < 257; index++)
        {
            timeline.Synchronize(
                TimeSpan.FromTicks(index),
                enabled: true);
        }

        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int index = 257; index < 10_257; index++)
        {
            timeline.Synchronize(
                TimeSpan.FromTicks(index),
                enabled: true);
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Empty(timeline.ActiveCues);
        Assert.Equal(0, client.Entered);
        Assert.Equal(0, client.Exited);
    }

    [Fact]
    public void PlaybackItemDisplayMetadataUsesOfficialWinUiTypes()
    {
        Assert.Equal(
            "Windows.Media",
            typeof(MediaPlaybackType).Namespace);
        Assert.Equal(
            "Windows.Media",
            typeof(MusicDisplayProperties).Namespace);
        Assert.Equal(
            "Windows.Media",
            typeof(VideoDisplayProperties).Namespace);
        Assert.Equal(
            typeof(RandomAccessStreamReference),
            typeof(MediaItemDisplayProperties)
                .GetProperty(
                    nameof(
                        MediaItemDisplayProperties.Thumbnail))!
                .PropertyType);
        Assert.Equal(
            typeof(MediaItemDisplayProperties),
            typeof(MediaPlaybackItem)
                .GetMethod(
                    nameof(
                        MediaPlaybackItem
                            .GetDisplayProperties))!
                .ReturnType);
        Assert.Equal(
            typeof(Task<
                IRandomAccessStreamWithContentType>),
            typeof(RandomAccessStreamReference)
                .GetMethod(
                    nameof(
                        RandomAccessStreamReference
                            .OpenReadAsync))!
                .ReturnType);
        Assert.Equal(
            typeof(IStorageFile),
            typeof(RandomAccessStreamReference)
                .GetMethod(
                    nameof(
                        RandomAccessStreamReference
                            .CreateFromFile))!
                .GetParameters()[0]
                .ParameterType);
        Assert.True(
            typeof(IStorageFile).IsAssignableFrom(
                typeof(StorageFile)));
    }

    [Fact]
    public void PlaybackListItemsUseOfficialObservableVector()
    {
        Assert.Equal(
            typeof(IObservableVector<MediaPlaybackItem>),
            typeof(MediaPlaybackList)
                .GetProperty(
                    nameof(MediaPlaybackList.Items))!
                .PropertyType);

        var list = new MediaPlaybackList();
        var changes = new List<
            (CollectionChange Change, uint Index)>();
        list.Items.VectorChanged += (_, args) =>
            changes.Add(
                (args.CollectionChange, args.Index));
        using MediaSource firstSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/first.mp4"));
        using MediaSource secondSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/second.mp4"));
        using MediaSource replacementSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/replacement.mp4"));
        var first = new MediaPlaybackItem(firstSource);
        var second = new MediaPlaybackItem(secondSource);
        var replacement =
            new MediaPlaybackItem(replacementSource);

        list.Items.Add(first);
        list.Items.Add(second);
        list.Items[1] = replacement;
        list.Items.RemoveAt(0);
        list.Items.Clear();

        Assert.Equal(
            [
                (CollectionChange.ItemInserted, 0u),
                (CollectionChange.ItemInserted, 1u),
                (CollectionChange.ItemChanged, 1u),
                (CollectionChange.ItemRemoved, 0u),
                (CollectionChange.Reset, 0u)
            ],
            changes);
    }

    [Fact]
    public void PlaybackRotationUsesOfficialWinUiMediaPropertiesType()
    {
        Assert.Equal(
            "Windows.Media.MediaProperties",
            typeof(MediaRotation).Namespace);
        Assert.Equal(
            typeof(MediaRotation),
            typeof(Windows.Media.Playback.MediaPlaybackSession)
                .GetProperty("PlaybackRotation")!
                .PropertyType);
    }

    [Fact]
    public void SharedPresenterCoalescesFrameworkInvalidationDispatch()
    {
        using var surface = new MediaGpuSurface();
        var context = new QueuedSynchronizationContext();
        int invalidations = 0;
        using var presenter =
            new MediaGpuSurfacePresenter(
                surface,
                () => invalidations++,
                context);

        presenter.RequestInvalidation();
        presenter.RequestInvalidation();

        Assert.Equal(1, context.PendingCount);
        Assert.Equal(0, invalidations);
        context.Drain();
        Assert.Equal(1, invalidations);
        Assert.Equal(Vector2.Zero, presenter.NaturalSize);
    }

    [Fact]
    public async Task SharedPresenterUsesOwnerDispatcherWithoutSynchronizationContext()
    {
        using var surface = new MediaGpuSurface();
        var pending = new Queue<Action>();
        int invalidations = 0;
        SynchronizationContext? previous =
            SynchronizationContext.Current;
        MediaGpuSurfacePresenter presenter;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            presenter = new MediaGpuSurfacePresenter(
                surface,
                () => invalidations++,
                ownerContext: null,
                ownerDispatcher: pending.Enqueue);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        using (presenter)
        {
            await Task.Run(presenter.RequestInvalidation);

            Assert.Equal(0, invalidations);
            Action dispatch = Assert.Single(pending);
            dispatch();
            Assert.Equal(1, invalidations);
        }
    }

    [Fact]
    public void SharedPresenterRecordsThroughLibreWpfContextWithOuterTransform()
    {
        using var surface = new MediaGpuSurface();
        surface.Publish(CreateFrame(sequence: 2));
        var nativeContext = new DrawingContext();
        using var wpfContext =
            new System.Windows.Media.DrawingContext(
                nativeContext);
        wpfContext.PushTransform(
            new System.Windows.Media.MatrixTransform(
                2d,
                0d,
                0d,
                3d,
                11d,
                13d));
        using var presenter =
            new MediaGpuSurfacePresenter(
                surface,
                static () => { });

        Assert.True(presenter.Record(
            (IProGpuDrawingContextSource)wpfContext,
            HeadlessWindow.Shared.Context,
            new Rect(0f, 0f, 320f, 180f)));

        RenderCommand command =
            Assert.Single(nativeContext.Commands);
        Assert.Equal(2f, command.Transform.M11);
        Assert.Equal(3f, command.Transform.M22);
        Assert.Equal(11f, command.Transform.M41);
        Assert.Equal(13f, command.Transform.M42);

        nativeContext.Clear();
    }

    [Fact]
    public void
        SharedPresenterRecordsPortableWpfStateWithoutAdapterAllocation()
    {
        using var surface = new MediaGpuSurface();
        surface.Publish(CreateFrame(sequence: 4));
        var nativeContext = new DrawingContext();
        using var wpfContext =
            new System.Windows.Media.DrawingContext(
                nativeContext);
        wpfContext.PushTransform(
            new System.Windows.Media.MatrixTransform(
                2d,
                0d,
                0d,
                3d,
                11d,
                13d));
        var portableSource =
            (ProGPU.Wpf.Interop
                .IPortableNativeDrawingContextStateSource)
            wpfContext;
        Assert.True(
            portableSource
                .TryGetPortableNativeDrawingContextState(
                    out ProGPU.Wpf.Interop
                        .PortableNativeDrawingContextState
                        portableState));
        Assert.True(
            ProGpuDrawingContextState.TryCreate(
                portableState.NativeDrawingContext,
                portableState.Transform,
                out ProGpuDrawingContextState state));
        Assert.True(
            ProGpuDrawingContextState.TryCreate(
                portableState.NativeDrawingContext,
                portableState.Transform,
                out _));
        bool converted = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            converted &=
                ProGpuDrawingContextState.TryCreate(
                    portableState.NativeDrawingContext,
                    portableState.Transform,
                    out state);
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;
        using var presenter =
            new MediaGpuSurfacePresenter(
                surface,
                static () => { });

        Assert.True(converted);
        Assert.Equal(0, allocated);
        Assert.True(presenter.Record(
            in state,
            HeadlessWindow.Shared.Context,
            new Rect(0f, 0f, 320f, 180f)));

        RenderCommand command =
            Assert.Single(nativeContext.Commands);
        Assert.Equal(2f, command.Transform.M11);
        Assert.Equal(3f, command.Transform.M22);
        Assert.Equal(11f, command.Transform.M41);
        Assert.Equal(13f, command.Transform.M42);

        nativeContext.Clear();
    }

    [Fact]
    public void SharedPresenterRecordsThroughLibreWinFormsGraphicsTransform()
    {
        using var surface = new MediaGpuSurface();
        surface.Publish(CreateFrame(sequence: 3));
        var nativeContext = new DrawingContext();
        Matrix4x4 outerTransform =
            Matrix4x4.CreateTranslation(
                11f,
                13f,
                0f);
        using var graphics =
            System.Drawing.Graphics.FromProGpuDrawingContext(
                nativeContext,
                outerTransform);
        graphics.TranslateTransform(5f, 7f);
        using var presenter =
            new MediaGpuSurfacePresenter(
                surface,
                static () => { });

        Assert.True(presenter.Record(
            (IProGpuDrawingContextSource)graphics,
            HeadlessWindow.Shared.Context,
            new Rect(0f, 0f, 320f, 180f)));

        RenderCommand command =
            Assert.Single(nativeContext.Commands);
        Assert.Equal(16f, command.Transform.M41);
        Assert.Equal(20f, command.Transform.M42);

        nativeContext.Clear();
    }

    [Fact]
    public async Task EngineProjectsProviderStateAndForwardsControls()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var engine = new MediaPlaybackEngine(registry);
        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/video.mp4"));

        await engine.SetSourceAsync(source);

        RecordingProvider provider = Assert.IsType<RecordingProvider>(
            factory.LastProvider);
        Assert.Equal(
            MediaEnginePlaybackState.Paused,
            engine.Snapshot.State);
        Assert.True(engine.Snapshot.Capabilities.HardwareDecoded);
        Assert.Equal("test-provider", engine.Diagnostics.ProviderId);
        Assert.Equal(2, engine.Diagnostics.VideoQueueDepth);
        Assert.Equal(TimeSpan.FromMilliseconds(8), engine.Diagnostics.AudioLatency);

        engine.Volume = 0.4d;
        engine.AudioBalance = -0.25d;
        engine.IsMuted = true;
        engine.IsLoopingEnabled = true;
        engine.SetPlaybackRate(1.5d);
        engine.Play();
        engine.Seek(TimeSpan.FromSeconds(3));

        Assert.Equal(1, provider.PlayCalls);
        Assert.Equal(0.4d, provider.Volume);
        Assert.Equal(-0.25d, provider.Balance);
        Assert.True(provider.Muted);
        Assert.True(provider.Looping);
        Assert.Equal(1.5d, provider.Rate);
        Assert.Equal(TimeSpan.FromSeconds(3), provider.LastSeek);
        Assert.Equal(
            MediaEnginePlaybackState.Playing,
            engine.Snapshot.State);
    }

    [Fact]
    public async Task EngineProjectsBoundedSourceRangeAndEndsAtLimit()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var engine = new MediaPlaybackEngine(registry);
        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/range.mp4"));
        int ended = 0;
        engine.Ended += (_, _) => ended++;

        await engine.SetSourceAsync(
            source,
            new MediaPlaybackRange(
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(10)));

        RecordingProvider provider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            provider.LastSeek);
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            engine.Snapshot.NaturalDuration);
        Assert.Equal(TimeSpan.Zero, engine.Snapshot.Position);

        engine.Seek(TimeSpan.FromSeconds(4));

        Assert.Equal(
            TimeSpan.FromSeconds(34),
            provider.LastSeek);
        Assert.Equal(
            TimeSpan.FromSeconds(4),
            engine.Snapshot.Position);

        engine.Play();
        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Playing,
            TimeSpan.FromSeconds(36),
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            engine.Snapshot.Capabilities));
        Assert.Equal(
            TimeSpan.FromSeconds(6),
            engine.Snapshot.Position);

        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Playing,
            TimeSpan.FromSeconds(40),
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            engine.Snapshot.Capabilities));

        Assert.Equal(1, provider.PauseCalls);
        Assert.Equal(1, ended);
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            engine.Snapshot.Position);
        Assert.Equal(
            MediaEnginePlaybackState.Paused,
            engine.Snapshot.State);

        engine.Play();

        Assert.Equal(
            TimeSpan.FromSeconds(30),
            provider.LastSeek);
        Assert.Equal(TimeSpan.Zero, engine.Snapshot.Position);
        Assert.Equal(2, provider.PlayCalls);
    }

    [Fact]
    public async Task BoundedRangeLoopUsesEngineRelativeBoundary()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var engine = new MediaPlaybackEngine(registry)
        {
            IsLoopingEnabled = true
        };
        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/range-loop.mp4"));

        await engine.SetSourceAsync(
            source,
            new MediaPlaybackRange(
                TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(5)));

        RecordingProvider provider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        Assert.False(provider.Looping);

        engine.Play();
        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Playing,
            TimeSpan.FromSeconds(25),
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            engine.Snapshot.Capabilities));

        Assert.Equal(0, provider.PauseCalls);
        Assert.Equal(
            TimeSpan.FromSeconds(20),
            provider.LastSeek);
        Assert.Equal(2, provider.PlayCalls);
        Assert.Equal(TimeSpan.Zero, engine.Snapshot.Position);
        Assert.Equal(
            MediaEnginePlaybackState.Playing,
            engine.Snapshot.State);
    }

    [Fact]
    public async Task ProviderRegistryUsesHighestPriorityWithoutReflection()
    {
        var registry = new MediaProviderRegistry();
        var low = new RecordingProviderFactory(priority: 1);
        var high = new RecordingProviderFactory(priority: 100);
        using IDisposable lowRegistration = registry.Register(low);
        using IDisposable highRegistration = registry.Register(high);
        using var engine = new MediaPlaybackEngine(registry);
        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/priority.mp4"));

        await engine.SetSourceAsync(source);

        Assert.Null(low.LastProvider);
        Assert.NotNull(high.LastProvider);
        Assert.Equal("test-provider", engine.Diagnostics.ProviderId);
    }

    [Fact]
    public async Task AutoPlayAndPlaybackRateSurviveAsynchronousOpen()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var engine = new MediaPlaybackEngine(registry)
        {
            AutoPlay = true
        };
        engine.SetPlaybackRate(1.25d);
        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/autoplay.mp4"));

        await engine.SetSourceAsync(source);

        RecordingProvider provider = Assert.IsType<RecordingProvider>(
            factory.LastProvider);
        Assert.Equal(1, provider.PlayCalls);
        Assert.Equal(1.25d, provider.Rate);
        Assert.Equal(
            MediaEnginePlaybackState.Playing,
            engine.Snapshot.State);
    }

    [Fact]
    public async Task PlayAfterEndRestartsBeforeProviderPlayback()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var engine = new MediaPlaybackEngine(registry);
        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/replay.mp4"));

        await engine.SetSourceAsync(source);

        RecordingProvider provider = Assert.IsType<RecordingProvider>(
            factory.LastProvider);
        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Paused,
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            engine.Snapshot.Capabilities));
        engine.IsLoopingEnabled = true;

        engine.Play();

        Assert.Equal(TimeSpan.Zero, provider.LastSeek);
        Assert.Equal(1, provider.PlayCalls);
        Assert.Equal(TimeSpan.Zero, engine.Snapshot.Position);
        Assert.Equal(
            MediaEnginePlaybackState.Playing,
            engine.Snapshot.State);
    }

    [Fact]
    public async Task LoopingProviderEndSeeksAndReplaysWithoutBlankState()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var engine = new MediaPlaybackEngine(registry)
        {
            IsLoopingEnabled = true
        };
        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/loop.mp4"));

        await engine.SetSourceAsync(source);

        RecordingProvider provider = Assert.IsType<RecordingProvider>(
            factory.LastProvider);
        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Paused,
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            engine.Snapshot.Capabilities));

        provider.ReportEnded();

        Assert.Equal(TimeSpan.Zero, provider.LastSeek);
        Assert.Equal(1, provider.PlayCalls);
        Assert.Equal(TimeSpan.Zero, engine.Snapshot.Position);
        Assert.Equal(
            MediaEnginePlaybackState.Playing,
            engine.Snapshot.State);
    }

    [Fact]
    public void LatestFrameReplacementKeepsBorrowedTextureAlive()
    {
        var surface = new MediaGpuSurface();
        var first = CreateFrame(sequence: 1);
        var second = CreateFrame(sequence: 2);

        surface.Publish(first);
        Assert.True(surface.TryAcquireGpuTextureLease(out var lease));
        GpuTexture firstTexture = lease.Texture;

        surface.Publish(second);

        Assert.True(first.IsDisposed);
        Assert.False(firstTexture.IsDisposed);
        Assert.Equal(2, surface.CurrentDescriptor.Sequence);

        lease.Dispose();
        Assert.True(firstTexture.IsDisposed);

        GpuTexture secondTexture = second.Texture;
        surface.Dispose();
        Assert.True(second.IsDisposed);
        Assert.True(secondTexture.IsDisposed);
    }

    [Fact]
    public void WinUiPlayerUsesPlaybackSessionAndPresenterRecordsOneGpuDraw()
    {
        var registry = new MediaProviderRegistry();
        TestGpuFrame presentedFrame = CreateFrame(sequence: 7);
        var factory = new RecordingProviderFactory(
            priority: 10,
            frameFactory: () => presentedFrame);
        using IDisposable registration = registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource source = MediaSource.CreateFromUri(
            new Uri("https://example.invalid/presenter.mp4"));

        player.Source = source;
        player.PlaybackSession.NormalizedSourceRect =
            new Windows.Foundation.Rect(0.25d, 0d, 0.5d, 1d);
        player.PlaybackSession.PlaybackRotation =
            MediaRotation.Clockwise90Degrees;
        player.PlaybackSession.IsMirroring = true;
        var presenter = new MediaPlayerPresenter
        {
            MediaPlayer = player,
            Stretch = Stretch.UniformToFill
        };
        presenter.Measure(new System.Numerics.Vector2(400f, 200f));
        presenter.Arrange(new Rect(0f, 0f, 400f, 200f));
        var drawingContext = new DrawingContext();
        WgpuContext? previousContext = WgpuContext.Current;

        try
        {
            WgpuContext.Current = HeadlessWindow.Shared.Context;
            presenter.OnRender(drawingContext);

            Assert.Equal(
                MediaPlaybackState.Paused,
                player.PlaybackSession.PlaybackState);
            Assert.Equal(
                (uint)1920,
                player.PlaybackSession.NaturalVideoWidth);
            RenderCommand textureCommand = Assert.Single(
                drawingContext.Commands,
                command =>
                    command.Type == RenderCommandType.DrawTexture);
            Assert.Equal(1f, textureCommand.SrcRect.X);
            Assert.Equal(2f, textureCommand.SrcRect.Width);
            Assert.NotEqual(
                System.Numerics.Matrix4x4.Identity,
                textureCommand.Transform);
            Assert.Equal(1, drawingContext.RetainedResourceCount);
            Assert.Same(
                HeadlessWindow.Shared.Context,
                presentedFrame.LastRequiredContext);
        }
        finally
        {
            drawingContext.Clear();
            WgpuContext.Current = previousContext;
        }
    }

    [Fact]
    public void AudioProcessorChainIsAllocationFreeAfterConfiguration()
    {
        var gain = new MediaAudioGainProcessor { Gain = 0.5f };
        var chain = new MediaAudioProcessorChain();
        chain.SetProcessors([gain]);
        var samples = new float[480 * 2];
        Array.Fill(samples, 1f);
        var context = new MediaAudioProcessContext(
            new MediaAudioFormat(48_000, 2),
            FrameCount: 480,
            PresentationTime: TimeSpan.Zero);

        chain.Process(samples, context);
        Array.Fill(samples, 1f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 100; iteration++)
        {
            Array.Fill(samples, 1f);
            chain.Process(samples, context);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.All(samples, sample => Assert.Equal(0.5f, sample));
    }

    [Fact]
    public void PortableGainEffectUpdatesPcmAndNativeGraphState()
    {
        var factory =
            new MediaAudioGainEffectFactory(
                "ProGPU.Tests.AudioGain");
        using IMediaEffect effect = factory.Create(
            new MediaEffectDescriptor(
                factory.ActivatableClassId,
                MediaEffectKind.Audio,
                new Dictionary<string, object?>()));
        var graphEffect =
            Assert.IsAssignableFrom<
                IMediaAudioGraphEffect>(effect);
        int changes = 0;
        graphEffect.StateChanged += () => changes++;

        factory.Gain = 0.25f;
        MediaAudioGraphEffectState state =
            graphEffect.CaptureState();
        var samples = new float[] { 1f, -1f, 0.5f, -0.5f };
        graphEffect.Process(
            samples,
            new MediaAudioProcessContext(
                new MediaAudioFormat(48_000, 2),
                FrameCount: 2,
                PresentationTime: TimeSpan.Zero));

        Assert.Equal(1, changes);
        Assert.Equal(
            MediaAudioGraphEffectKind.Gain,
            state.Kind);
        Assert.Equal(0.25f, state.Parameter0);
        Assert.Equal(
            [0.25f, -0.25f, 0.125f, -0.125f],
            samples);
    }

    [Fact]
    public void
        PortableStereoBalanceEffectUpdatesPcmAndNativeGraphStateWithoutAllocating()
    {
        var factory =
            new MediaAudioStereoBalanceEffectFactory(
                "ProGPU.Tests.AudioBalance");
        using IMediaEffect effect = factory.Create(
            new MediaEffectDescriptor(
                factory.ActivatableClassId,
                MediaEffectKind.Audio,
                new Dictionary<string, object?>()));
        var graphEffect =
            Assert.IsAssignableFrom<
                IMediaAudioGraphEffect>(effect);
        int changes = 0;
        graphEffect.StateChanged += () => changes++;

        factory.Balance = -0.5f;
        MediaAudioGraphEffectState state =
            graphEffect.CaptureState();
        var samples =
            new float[] { 1f, -1f, 0.5f, -0.5f };
        var context = new MediaAudioProcessContext(
            new MediaAudioFormat(48_000, 2),
            FrameCount: 2,
            PresentationTime: TimeSpan.Zero);
        graphEffect.Process(samples, context);

        Assert.Equal(1, changes);
        Assert.Equal(
            MediaAudioGraphEffectKind.StereoBalance,
            state.Kind);
        Assert.Equal(-0.5f, state.Parameter0);
        Assert.Equal(
            [1f, -0.5f, 0.5f, -0.25f],
            samples);

        var surroundSamples =
            new float[] { 1f, 1f, 0.25f, 0.5f, 0.5f, -0.25f };
        graphEffect.Process(
            surroundSamples,
            new MediaAudioProcessContext(
                new MediaAudioFormat(48_000, 3),
                FrameCount: 2,
                PresentationTime: TimeSpan.Zero));
        Assert.Equal(
            [1f, 0.5f, 0.25f, 0.5f, 0.25f, -0.25f],
            surroundSamples);
        var monoSamples = new float[] { 1f, -1f };
        graphEffect.Process(
            monoSamples,
            new MediaAudioProcessContext(
                new MediaAudioFormat(48_000, 1),
                FrameCount: 2,
                PresentationTime: TimeSpan.Zero));
        Assert.Equal([1f, -1f], monoSamples);

        var callbackSamples = new float[480 * 2];
        var callbackContext =
            new MediaAudioProcessContext(
                new MediaAudioFormat(48_000, 2),
                FrameCount: 480,
                PresentationTime: TimeSpan.Zero);
        graphEffect.Process(
            callbackSamples,
            callbackContext);
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0;
             iteration < 100;
             iteration++)
        {
            Array.Fill(callbackSamples, 1f);
            graphEffect.Process(
                callbackSamples,
                callbackContext);
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            before;

        Assert.Equal(0, allocated);
        Assert.Equal(1f, callbackSamples[0]);
        Assert.Equal(0.5f, callbackSamples[1]);
    }

    [Fact]
    public void
        StereoBalanceEffectDefinitionOwnsSerializedState()
    {
        var registry = new MediaEffectRegistry();
        var factory =
            new MediaAudioStereoBalanceEffectFactory(
                "ProGPU.Tests.SerializedAudioBalance");
        using IDisposable registration =
            registry.Register(factory);
        var descriptor = new MediaEffectDescriptor(
            factory.ActivatableClassId,
            MediaEffectKind.Audio,
            new Dictionary<string, object?>
            {
                [MediaAudioStereoBalanceEffectFactory
                    .BalancePropertyName] = 0.25d
            });

        Assert.True(
            registry.TryCreate(
                descriptor,
                out IMediaEffect? created));
        using IMediaEffect effect = created!;
        var graphEffect =
            Assert.IsAssignableFrom<
                IMediaAudioGraphEffect>(effect);

        factory.Balance = -0.75f;
        Assert.Equal(
            0.25f,
            graphEffect.CaptureState().Parameter0,
            precision: 6);
    }

    [Fact]
    public void StereoLevelsFoldGainAndBalance()
    {
        MediaAudioStereoLevels levels =
            MediaAudioStereoLevels
                .FromBalance(-0.25f)
                .Apply(
                    new MediaAudioGraphEffectState(
                        MediaAudioGraphEffectKind.Gain,
                        2f))
                .Apply(
                    new MediaAudioGraphEffectState(
                        MediaAudioGraphEffectKind
                            .StereoBalance,
                        0.5f));

        Assert.Equal(1f, levels.Left);
        Assert.Equal(1.5f, levels.Right);
        Assert.Equal(1.5f, levels.Peak);
        Assert.Equal(
            1f / 3f,
            levels.Balance,
            precision: 6);
        Assert.Equal(
            0f,
            new MediaAudioStereoLevels(0f, 0f)
                .Balance);
        Assert.Equal(
            float.MaxValue,
            MediaAudioStereoLevels.Identity
                .Scale(float.MaxValue)
                .Scale(2f)
                .Peak);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaAudioStereoLevels
                .FromBalance(1.01f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MediaAudioStereoLevels(
                1f,
                float.NaN));
        Assert.Equal(
            (MediaAudioGraphEffectKind)99,
            new MediaAudioGraphEffectState(
                (MediaAudioGraphEffectKind)99)
                .Kind);
    }

    [Fact]
    public void SharedPcm16StereoProcessorIsSaturatingAndAllocationFree()
    {
        short[] samples =
        [
            1_000,
            1_000,
            -20_000,
            -20_000,
            short.MinValue,
            short.MaxValue
        ];
        var levels =
            new MediaAudioStereoLevels(
                2f,
                0.5f);
        int channelOffset = 0;
        MediaPcm16StereoProcessor.ApplyStereo(
            samples.AsSpan(0, 3),
            channelCount: 2,
            levels,
            ref channelOffset);
        MediaPcm16StereoProcessor.ApplyStereo(
            samples.AsSpan(3),
            channelCount: 2,
            levels,
            ref channelOffset);

        Assert.Equal(0, channelOffset);
        Assert.Equal(
            [
                2_000,
                500,
                -32_768,
                -10_000,
                short.MinValue,
                16_383
            ],
            samples);

        short[] mono = [1_000, -1_000];
        MediaPcm16StereoProcessor.ApplyStereo(
            mono,
            channelCount: 1,
            new MediaAudioStereoLevels(
                0.5f,
                0.25f),
            ref channelOffset);
        Assert.Equal([500, -500], mono);

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0;
             iteration < 1_000;
             iteration++)
        {
            MediaPcm16StereoProcessor.ApplyStereo(
                samples,
                channelCount: 2,
                MediaAudioStereoLevels.Identity,
                ref channelOffset);
        }
        long after =
            GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(before, after);
    }

    [Fact]
    public void SharedPcmTimelineMathUsesHalfOpenFrameBoundaries()
    {
        const uint sampleRate = 48_000;
        Assert.Equal(
            0,
            MediaPcmTimelineMath
                .GetBoundaryFrameOffset(
                    -1,
                    sampleRate,
                    maximumFrames: 8));
        Assert.Equal(
            1,
            MediaPcmTimelineMath
                .GetBoundaryFrameOffset(
                    1,
                    sampleRate,
                    maximumFrames: 8));
        Assert.Equal(
            1,
            MediaPcmTimelineMath
                .GetBoundaryFrameOffset(
                    20,
                    sampleRate,
                    maximumFrames: 8));
        Assert.Equal(
            2,
            MediaPcmTimelineMath
                .GetBoundaryFrameOffset(
                    21,
                    sampleRate,
                    maximumFrames: 8));
        Assert.Equal(
            8,
            MediaPcmTimelineMath
                .GetBoundaryFrameOffset(
                    1_000_000,
                    sampleRate,
                    maximumFrames: 8));
        Assert.Equal(
            0,
            MediaPcmTimelineMath
                .GetFrameTimestampMicroseconds(
                    0,
                    sampleRate));
        Assert.Equal(
            20,
            MediaPcmTimelineMath
                .GetFrameTimestampMicroseconds(
                    1,
                    sampleRate));
        Assert.Equal(
            1_000_000,
            MediaPcmTimelineMath
                .GetFrameTimestampMicroseconds(
                    sampleRate,
                    sampleRate));
        Assert.Equal(
            3_600_000_000,
            MediaPcmTimelineMath
                .GetFrameTimestampMicroseconds(
                    sampleRate * 3_600L,
                    sampleRate));
        Assert.Equal(
            1,
            MediaPcmTimelineMath
                .GetDurationFrameCountCeiling(
                    TimeSpan.FromTicks(1),
                    sampleRate));
        Assert.Equal(
            48_000,
            MediaPcmTimelineMath
                .GetDurationFrameCountCeiling(
                    TimeSpan.FromSeconds(1),
                    sampleRate));
        Assert.Equal(
            172_800_000,
            MediaPcmTimelineMath
                .GetDurationFrameCountCeiling(
                    TimeSpan.FromHours(1),
                    sampleRate));

        long previousFrame = 0;
        long accumulatedFrames = 0;
        for (long timelineTicks = 1;
             timelineTicks <= 10_000;
             timelineTicks++)
        {
            long targetFrame =
                MediaPcmTimelineMath
                    .GetDurationFrameCountCeiling(
                        TimeSpan.FromTicks(
                            timelineTicks),
                        sampleRate);
            long clipFrames =
                targetFrame -
                previousFrame;
            Assert.InRange(
                clipFrames,
                0,
                1);
            accumulatedFrames +=
                clipFrames;
            previousFrame =
                targetFrame;
        }
        Assert.Equal(48, accumulatedFrames);
    }

    [Fact]
    public void GainEffectDefinitionOwnsSerializedGainState()
    {
        var registry = new MediaEffectRegistry();
        var factory =
            new MediaAudioGainEffectFactory(
                "ProGPU.Tests.SerializedAudioGain");
        using IDisposable registration =
            registry.Register(factory);
        Assert.True(
            registry.IsRegistered(
                factory.ActivatableClassId));

        var descriptor = new MediaEffectDescriptor(
            factory.ActivatableClassId,
            MediaEffectKind.Audio,
            new Dictionary<string, object?>
            {
                [MediaAudioGainEffectFactory
                    .GainPropertyName] = 0.2d
            });
        Assert.True(
            registry.TryCreate(
                descriptor,
                out IMediaEffect? created));
        using IMediaEffect effect = created!;
        var graphEffect =
            Assert.IsAssignableFrom<
                IMediaAudioGraphEffect>(effect);

        factory.Gain = 0.75f;
        Assert.Equal(
            0.2f,
            graphEffect.CaptureState().Parameter0,
            precision: 6);
    }

    [Fact]
    public void AudioGraphResolverCapturesGainAndStereoDefinitions()
    {
        const string gainId =
            "ProGPU.Tests.CompositionAudioGain";
        var registry = new MediaEffectRegistry();
        using IDisposable registration =
            registry.Register(
                new MediaAudioGainEffectFactory(
                    gainId));
        MediaCompositionEffectDefinition[] definitions =
        [
            new(
                gainId,
                new Dictionary<string, object?>
                {
                    [MediaAudioGainEffectFactory
                        .GainPropertyName] = 0.5d
                }),
            new(
                gainId,
                new Dictionary<string, object?>
                {
                    [MediaAudioGainEffectFactory
                        .GainPropertyName] = 0.25f
                })
        ];

        Assert.True(
            MediaAudioGraphEffectResolver
                .TryCaptureCombinedGain(
                    registry,
                    definitions,
                    out double gain));
        Assert.Equal(0.125d, gain);
        Assert.False(
            MediaAudioGraphEffectResolver
                .TryCaptureCombinedGain(
                    registry,
                    [
                        new MediaCompositionEffectDefinition(
                            "ProGPU.Tests.Unregistered",
                            new Dictionary<string, object?>())
                    ],
                    out _));

        const string balanceId =
            "ProGPU.Tests.CompositionAudioBalance";
        using IDisposable balanceRegistration =
            registry.Register(
                new MediaAudioStereoBalanceEffectFactory(
                    balanceId));
        MediaCompositionEffectDefinition balanceDefinition =
            new(
                balanceId,
                new Dictionary<string, object?>
                {
                    [MediaAudioStereoBalanceEffectFactory
                        .BalancePropertyName] = -0.25f
                });
        Assert.False(
            MediaAudioGraphEffectResolver
                .TryCaptureCombinedGain(
                    registry,
                    [balanceDefinition],
                    out _));
        Assert.True(
            MediaAudioGraphEffectResolver
                .TryCaptureCombinedStereoLevels(
                    registry,
                    [
                        definitions[0],
                        balanceDefinition,
                        definitions[1]
                    ],
                    out MediaAudioStereoLevels
                        levels));
        Assert.Equal(0.125f, levels.Left);
        Assert.Equal(0.09375f, levels.Right);

        Assert.True(
            MediaAudioGraphEffectResolver
                .TryCaptureBuiltInGraph(
                    registry,
                    [
                        definitions[0],
                        balanceDefinition
                    ],
                    out MediaAudioGraphEffectState[]
                        states));
        Assert.Collection(
            states,
            state =>
            {
                Assert.Equal(
                    MediaAudioGraphEffectKind.Gain,
                    state.Kind);
                Assert.Equal(0.5f, state.Parameter0);
            },
            state =>
            {
                Assert.Equal(
                    MediaAudioGraphEffectKind.StereoBalance,
                    state.Kind);
                Assert.Equal(-0.25f, state.Parameter0);
            });
    }

    [Fact]
    public void AudioTimelineProcessesOnlyScheduledFramesWithoutAllocating()
    {
        var gain = new MediaAudioGainProcessor
        {
            Gain = 0.25f
        };
        var timeline = new MediaAudioTimelineProcessor(
            [
                new MediaAudioTimelineSegment(
                    TimeSpan.FromMilliseconds(5),
                    TimeSpan.FromMilliseconds(10),
                    [gain])
            ]);
        var samples = new float[160 * 2];
        var context = new MediaAudioProcessContext(
            new MediaAudioFormat(8_000, 2),
            FrameCount: 160,
            PresentationTime: TimeSpan.Zero);

        Array.Fill(samples, 1f);
        timeline.Process(samples, context);
        for (int frame = 0; frame < 160; frame++)
        {
            float expected =
                frame is >= 40 and < 120
                    ? 0.25f
                    : 1f;
            Assert.Equal(expected, samples[frame * 2]);
            Assert.Equal(expected, samples[frame * 2 + 1]);
        }

        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0;
             iteration < 100;
             iteration++)
        {
            Array.Fill(samples, 1f);
            timeline.Process(samples, context);
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SharedSceneAdapterRecordsTypedGpuEffectWithoutPayloadAllocation()
    {
        using var surface = new MediaGpuSurface();
        surface.Publish(CreateFrame(sequence: 12));
        var context = new DrawingContext();
        var options = new MediaVideoPresentationOptions(
            stretch: MediaVideoStretch.Fill,
            normalizedSourceRect:
                new System.Numerics.Vector4(
                    0.25f,
                    0f,
                    0.5f,
                    1f),
            effects: new MediaVideoEffectOptions(
                brightness: 0.1f,
                contrast: 1.2f,
                invert: 1f));

        Assert.True(context.DrawLatestFrame(
            surface,
            HeadlessWindow.Shared.Context,
            new Rect(0f, 0f, 320f, 180f),
            in options));

        RenderCommand command = Assert.Single(context.Commands);
        Assert.Equal(
            CompositorBuiltInExtensions.ImageEffect,
            command.ExtensionId);
        Assert.True(command.HasImageEffect);
        Assert.Null(command.DataParam);
        Assert.Equal(new Rect(1f, 0f, 2f, 2f), command.SrcRect);
        ImageEffectCommandData effect = context.GetImageEffect(in command);
        Assert.Equal(0.1f, effect.Brightness);
        Assert.Equal(1.2f, effect.Contrast);
        Assert.Equal(1f, effect.Invert);
        Assert.Equal(1, context.RetainedResourceCount);

        context.Clear();
    }

    [Fact]
    public void SharedSceneAdapterRetainsAndFusesNv12Planes()
    {
        using var surface = new MediaGpuSurface();
        var frame = new TestPlanarGpuFrame(
            HeadlessWindow.Shared.Context);
        surface.Publish(frame);
        var context = new DrawingContext();
        var options = new MediaVideoPresentationOptions(
            stretch: MediaVideoStretch.Fill);

        Assert.True(context.DrawLatestFrame(
            surface,
            HeadlessWindow.Shared.Context,
            new Rect(0f, 0f, 320f, 180f),
            in options));

        RenderCommand command = Assert.Single(context.Commands);
        Assert.True(command.HasImageEffect);
        Assert.Same(
            frame.LumaTexture,
            command.Texture);
        ImageEffectCommandData effect = context.GetImageEffect(in command);
        Assert.Same(
            frame.ChromaTexture,
            effect.ChromaTexture);
        Assert.True(
            effect.YuvConversion.HasValue);
        Assert.Equal(2, context.RetainedResourceCount);

        context.Clear();
        Assert.False(frame.LumaTexture.IsDisposed);
        Assert.False(frame.ChromaTexture.IsDisposed);
    }

    [Fact]
    public void SharedMesh3DAdapterBindsPlanarSurfaceAndEffects()
    {
        using var surface = new MediaGpuSurface();
        var frame = new TestPlanarGpuFrame(
            HeadlessWindow.Shared.Context);
        surface.Publish(frame);
        var entry = new MeshCompilationEntry();
        var effects = new MediaVideoEffectOptions(
            brightness: 0.1f,
            grayscale: 0.4f,
            samplingMode: TextureSamplingMode.Nearest);
        var presentation = new MediaVideoPresentationOptions(
            stretch: MediaVideoStretch.Fill,
            normalizedSourceRect:
                new Vector4(0.25f, 0f, 0.5f, 1f),
            rotation:
                MediaVideoRotation.Clockwise270Degrees,
            isMirrored: true,
            effects: effects);

        Assert.True(entry.UseLatestFrame(
            surface,
            in presentation));

        Assert.Same(surface, entry.TextureSource);
        Assert.True(entry.YuvConversion.HasValue);
        Assert.Equal(0.1f, entry.TextureEffect.Brightness);
        Assert.Equal(0.4f, entry.TextureEffect.Grayscale);
        Assert.Equal(
            TextureSamplingMode.Nearest,
            entry.TextureSamplingMode);
        Assert.Equal(
            new Vector4(0.25f, 0f, 0.5f, 1f),
            entry.TexturePresentation.NormalizedSourceRect);
        Assert.Equal(
            3,
            entry.TexturePresentation.ClockwiseQuarterTurns);
        Assert.True(entry.TexturePresentation.IsMirrored);
    }

    [Fact]
    public void Mesh3DShadersSharePlanarStorageRecordAbi()
    {
        string solid = ShaderResource.Load(
            typeof(Mesh3DExtensionPipeline),
            "Mesh3DSolid.wgsl");
        string wireframe = ShaderResource.Load(
            typeof(Mesh3DExtensionPipeline),
            "Mesh3DWireframe.wgsl");

        Assert.Equal(448, System.Runtime.InteropServices.Marshal
            .SizeOf<GpuMesh3DRecord>());
        Assert.Equal(
            GetRecordDeclaration(solid),
            GetRecordDeclaration(wireframe));
        Assert.Contains(
            "yuvRange: vec4<f32>",
            solid,
            StringComparison.Ordinal);
        Assert.Contains(
            "TransformMaterialCoordinate",
            solid,
            StringComparison.Ordinal);

        static string GetRecordDeclaration(string shader)
        {
            const string prefix =
                "struct GpuMesh3DRecord {";
            int start = shader.IndexOf(
                prefix,
                StringComparison.Ordinal);
            Assert.True(start >= 0);
            int end = shader.IndexOf(
                "};",
                start,
                StringComparison.Ordinal);
            Assert.True(end > start);
            return shader.Substring(
                start,
                end + 2 - start);
        }
    }

    [Fact]
    public void Mesh3DCompileScratchReusesPeakCapacity()
    {
        var scratch = new Mesh3DCompileScratch();
        scratch.EnsureCapacity(3);

        Assert.Equal(4, scratch.Capacity);
        scratch.Records[0] = new GpuMesh3DRecord
        {
            Opacity = 0.75f
        };
        scratch.TextureBindGroups[0] = (nint)42;
        scratch.UnfilterableMaterials[0] = 1;

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0;
             iteration < 4_096;
             iteration++)
        {
            scratch.EnsureCapacity(3);
            scratch.Records[1].Opacity =
                iteration;
            scratch.TextureBindGroups[1] =
                (nint)iteration;
            scratch.UnfilterableMaterials[1] =
                (byte)(iteration & 1);
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            before;

        Assert.Equal(0, allocated);
        Assert.Equal(
            4_095f,
            scratch.Records[1].Opacity);
        Assert.Equal(
            (nint)4_095,
            scratch.TextureBindGroups[1]);
        Assert.Equal(
            (byte)1,
            scratch.UnfilterableMaterials[1]);

        scratch.EnsureCapacity(5);
        Assert.Equal(8, scratch.Capacity);
        Assert.Equal(
            0.75f,
            scratch.Records[0].Opacity);
        Assert.Equal(
            (nint)42,
            scratch.TextureBindGroups[0]);
        Assert.Equal(
            (byte)1,
            scratch.UnfilterableMaterials[0]);
    }

    [Fact]
    public void WinUiMesh3DMaterialRendersNv12WithoutFallbackTexture()
    {
        using var window =
            new HeadlessWindow(160, 90);
        using var player =
            new Windows.Media.Playback.MediaPlayer();
        MediaGpuSurface surface =
            player.GetProGpuSurface();
        var frame = new TestPlanarGpuFrame(window.Context);
        frame.LumaTexture.WritePixels(
            new byte[] { 63, 63, 63, 63, 63, 63, 63, 63 });
        frame.ChromaTexture.WritePixels(
            new byte[] { 102, 240, 102, 240 });
        surface.Publish(frame);
        using var material =
            new ProGpuMediaTextureMaterial
            {
                MediaPlayer = player
            };
        var mesh = new MeshGeometry3D
        {
            Positions =
            [
                new Vector3(-1.5f, -0.8f, 0f),
                new Vector3(1.5f, -0.8f, 0f),
                new Vector3(1.5f, 0.8f, 0f),
                new Vector3(-1.5f, 0.8f, 0f)
            ],
            Normals =
            [
                -Vector3.UnitZ,
                -Vector3.UnitZ,
                -Vector3.UnitZ,
                -Vector3.UnitZ
            ],
            TextureCoordinates =
            [
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f),
                new Vector2(0f, 0f)
            ],
            TriangleIndices = [0, 1, 2, 0, 2, 3]
        };
        var viewport = new Viewport3D
        {
            Camera = new OrthographicCamera
            {
                Width = 4f
            },
            ShadingMode = ShadingMode3D.Flat
        };
        viewport.Children.Add(
            new ModelVisual3D
            {
                Content = new GeometryModel3D
                {
                    Geometry = mesh,
                    Material = material,
                    BackMaterial = material
                }
            });
        window.Content = viewport;

        try
        {
            window.Render();
            byte[] pixels = window.ReadPixels();
            int redVideoPixels = 0;
            for (int offset = 0;
                 offset < pixels.Length;
                 offset += 4)
            {
                if (pixels[offset] >= 180 &&
                    pixels[offset + 1] <= 60 &&
                    pixels[offset + 2] <= 60 &&
                    pixels[offset + 3] == 255)
                {
                    redVideoPixels++;
                }
            }

            Assert.True(
                redVideoPixels >= 1_000,
                $"Expected a filled converted-red video quad, " +
                $"found {redVideoPixels} red pixels.");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void
        WinUiMesh3DMaterialPreparesOneRgbGaussianForFrontAndBack()
    {
        using var window =
            new HeadlessWindow(160, 90);
        using var player =
            new Windows.Media.Playback.MediaPlayer();
        MediaGpuSurface surface =
            player.GetProGpuSurface();
        var texture = new GpuTexture(
            window.Context,
            5,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding |
                TextureUsage.CopyDst,
            "Mesh3D retained RGB Gaussian source");
        texture.WritePixels(
            new byte[]
            {
                0, 0, 0, 255,
                0, 0, 0, 255,
                255, 255, 255, 255,
                0, 0, 0, 255,
                0, 0, 0, 255
            });
        surface.Publish(
            new TestGpuFrame(
                texture,
                new MediaGpuFrameDescriptor(
                    31,
                    TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(16),
                    5,
                    1,
                    MediaVideoPixelFormat.Rgba8,
                    MediaTransferMode.NativeZeroCopy,
                    new MediaColorInfo(
                        MediaColorPrimaries.Bt709,
                        MediaTransferFunction.Srgb,
                        MediaMatrixCoefficients.Identity,
                        FullRange: true))));
        using var material =
            new ProGpuMediaTextureMaterial
            {
                MediaPlayer = player,
                Effects = new MediaVideoEffectOptions(
                    blurSigma: 1f)
            };
        window.Content =
            CreateMediaMeshViewport(material);

        try
        {
            window.Render();

            var extension =
                Assert.IsType<Mesh3DExtensionPipeline>(
                    window.Compositor.GetExtension(
                        CompositorBuiltInExtensions
                            .Mesh3D));
            Assert.Equal(
                1,
                extension.LiveMaterialBlurResourceCount);
            Assert.Equal(
                2,
                extension.PreparedLiveMaterialCount);
            Assert.Equal(
                1,
                extension
                    .LiveMaterialBlurSubmissionCount);
            Assert.Equal(
                1f,
                material.Effects.BlurSigma);

            byte[] pixels = window.ReadPixels();
            Assert.True(
                pixels.Count(value => value > 20) >
                    1_000,
                "Expected the retained RGB Gaussian result on the mesh.");

            player.PlaybackSession.IsMirroring = true;
            window.Render();
            Assert.Equal(
                1,
                extension.LiveMaterialBlurResourceCount);
            Assert.Equal(
                2,
                extension.PreparedLiveMaterialCount);
            Assert.Equal(
                1,
                extension
                    .LiveMaterialBlurSubmissionCount);
            Assert.Equal(
                1f,
                material.Effects.BlurSigma);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void
        WinUiMesh3DMaterialPreparesOneNv12GaussianForFrontAndBack()
    {
        using var window =
            new HeadlessWindow(160, 90);
        using var player =
            new Windows.Media.Playback.MediaPlayer();
        MediaGpuSurface surface =
            player.GetProGpuSurface();
        var frame = new TestPlanarGpuFrame(window.Context);
        frame.LumaTexture.WritePixels(
            new byte[] { 63, 63, 63, 63, 63, 63, 63, 63 });
        frame.ChromaTexture.WritePixels(
            new byte[] { 102, 240, 102, 240 });
        surface.Publish(frame);
        using var material =
            new ProGpuMediaTextureMaterial
            {
                MediaPlayer = player,
                Effects = new MediaVideoEffectOptions(
                    blurSigma: 1f)
            };
        window.Content =
            CreateMediaMeshViewport(material);

        try
        {
            window.Render();

            var extension =
                Assert.IsType<Mesh3DExtensionPipeline>(
                    window.Compositor.GetExtension(
                        CompositorBuiltInExtensions
                            .Mesh3D));
            Assert.Equal(
                1,
                extension.LiveMaterialBlurResourceCount);
            Assert.Equal(
                2,
                extension.PreparedLiveMaterialCount);
            Assert.Equal(
                1,
                extension
                    .LiveMaterialBlurSubmissionCount);
            Assert.Equal(
                1f,
                material.Effects.BlurSigma);

            AssertFilledRedMediaMesh(
                window.ReadPixels());
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public unsafe void
        WinUiMesh3DMaterialPreparesTier1P010Gaussian()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var dawn =
            DawnGpuContext.CreateMetalPresentation();
        Assert.True(
            dawn.Context.SupportsTextureFormatsTier1);
        using var compositor = new Compositor(
            dawn.Context,
            TextureFormat.Rgba8Unorm,
            CompositorOptions.Default with
            {
                EnableGpuHitTesting = false
            });
        using var target = new GpuTexture(
            dawn.Context,
            160,
            90,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment |
                TextureUsage.CopySrc,
            "Mesh3D retained P010 Gaussian target");
        using var player =
            new Windows.Media.Playback.MediaPlayer();
        MediaGpuSurface surface =
            player.GetProGpuSurface();
        var frame = new TestPlanarGpuFrame(
            dawn.Context,
            p010: true);
        frame.LumaTexture.WritePixels<ushort>(
        [
            252 << 6,
            252 << 6,
            252 << 6,
            252 << 6,
            252 << 6,
            252 << 6,
            252 << 6,
            252 << 6
        ]);
        frame.ChromaTexture.WritePixels<ushort>(
        [
            408 << 6,
            960 << 6,
            408 << 6,
            960 << 6
        ]);
        surface.Publish(frame);
        using var material =
            new ProGpuMediaTextureMaterial
            {
                MediaPlayer = player,
                Effects = new MediaVideoEffectOptions(
                    blurSigma: 1f)
            };
        Viewport3D viewport =
            CreateMediaMeshViewport(material);
        viewport.Measure(new Vector2(160f, 90f));
        viewport.Arrange(
            new Rect(
                0f,
                0f,
                160f,
                90f));

        compositor.RenderScene(
            viewport,
            160,
            90,
            target.ViewPtr);

        var extension =
            Assert.IsType<Mesh3DExtensionPipeline>(
                compositor.GetExtension(
                    CompositorBuiltInExtensions
                        .Mesh3D));
        Assert.Equal(
            1,
            extension.LiveMaterialBlurResourceCount);
        Assert.Equal(
            2,
            extension.PreparedLiveMaterialCount);
        Assert.Equal(
            1,
            extension.LiveMaterialBlurSubmissionCount);
        Assert.Equal(
            1f,
            material.Effects.BlurSigma);
        AssertFilledRedMediaMesh(
            target.ReadPixels());
    }

    [Theory]
    [InlineData(TextureSamplingMode.Nearest, false)]
    [InlineData(TextureSamplingMode.Nearest, true)]
    [InlineData(TextureSamplingMode.Linear, false)]
    [InlineData(TextureSamplingMode.Linear, true)]
    public unsafe void
        WinUiMesh3DMaterialRendersTier1P010Direct(
            TextureSamplingMode samplingMode,
            bool applyEffect)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var dawn =
            DawnGpuContext.CreateMetalPresentation();
        Assert.True(
            dawn.Context.SupportsTextureFormatsTier1);
        using var compositor = new Compositor(
            dawn.Context,
            TextureFormat.Rgba8Unorm,
            CompositorOptions.Default with
            {
                EnableGpuHitTesting = false
            });
        using var target = new GpuTexture(
            dawn.Context,
            160,
            90,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment |
                TextureUsage.CopySrc,
            "Mesh3D direct P010 target");
        using var player =
            new Windows.Media.Playback.MediaPlayer();
        MediaGpuSurface surface =
            player.GetProGpuSurface();
        var frame = new TestPlanarGpuFrame(
            dawn.Context,
            p010: true);
        frame.LumaTexture.WritePixels<ushort>(
        [
            252 << 6,
            252 << 6,
            252 << 6,
            252 << 6,
            252 << 6,
            252 << 6,
            252 << 6,
            252 << 6
        ]);
        frame.ChromaTexture.WritePixels<ushort>(
        [
            408 << 6,
            960 << 6,
            408 << 6,
            960 << 6
        ]);
        surface.Publish(frame);
        using var material =
            new ProGpuMediaTextureMaterial
            {
                MediaPlayer = player,
                SamplingMode = samplingMode,
                Effects = applyEffect
                    ? new MediaVideoEffectOptions(
                        brightness: 0.02f)
                    : MediaVideoEffectOptions.Identity
            };
        Viewport3D viewport =
            CreateMediaMeshViewport(material);
        viewport.Measure(new Vector2(160f, 90f));
        viewport.Arrange(
            new Rect(
                0f,
                0f,
                160f,
                90f));

        compositor.RenderScene(
            viewport,
            160,
            90,
            target.ViewPtr);

        var extension =
            Assert.IsType<Mesh3DExtensionPipeline>(
                compositor.GetExtension(
                    CompositorBuiltInExtensions
                        .Mesh3D));
        Assert.Equal(
            0,
            extension.LiveMaterialBlurResourceCount);
        Assert.Equal(
            0,
            extension.LiveMaterialBlurSubmissionCount);
        AssertFilledRedMediaMesh(
            target.ReadPixels());
    }

    [Fact]
    public unsafe void
        WinUiMesh3DMaterialSwitchesBetweenRgbAndP010()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var dawn =
            DawnGpuContext.CreateMetalPresentation();
        Assert.True(
            dawn.Context.SupportsTextureFormatsTier1);
        using var compositor = new Compositor(
            dawn.Context,
            TextureFormat.Rgba8Unorm,
            CompositorOptions.Default with
            {
                EnableGpuHitTesting = false
            });
        using var target = new GpuTexture(
            dawn.Context,
            180,
            90,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment |
                TextureUsage.CopySrc,
            "Mesh3D mixed RGB P010 target");
        using var rgbPlayer =
            new Windows.Media.Playback.MediaPlayer();
        using var p010Player =
            new Windows.Media.Playback.MediaPlayer();
        var rgbTexture = new GpuTexture(
            dawn.Context,
            4,
            2,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding |
                TextureUsage.CopyDst,
            "Mesh3D mixed RGB source");
        rgbTexture.WritePixels(
        [
            255, 0, 0, 255,
            255, 0, 0, 255,
            255, 0, 0, 255,
            255, 0, 0, 255,
            255, 0, 0, 255,
            255, 0, 0, 255,
            255, 0, 0, 255,
            255, 0, 0, 255
        ]);
        rgbPlayer.GetProGpuSurface().Publish(
            new TestGpuFrame(
                rgbTexture,
                new MediaGpuFrameDescriptor(
                    1,
                    TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(16),
                    4,
                    2,
                    MediaVideoPixelFormat.Rgba8,
                    MediaTransferMode.NativeZeroCopy,
                    new MediaColorInfo(
                        MediaColorPrimaries.Bt709,
                        MediaTransferFunction.Srgb,
                        MediaMatrixCoefficients.Identity,
                        FullRange: true))));
        var p010Frame = new TestPlanarGpuFrame(
            dawn.Context,
            p010: true);
        p010Frame.LumaTexture.WritePixels<ushort>(
        [
            252 << 6,
            252 << 6,
            252 << 6,
            252 << 6,
            252 << 6,
            252 << 6,
            252 << 6,
            252 << 6
        ]);
        p010Frame.ChromaTexture.WritePixels<ushort>(
        [
            408 << 6,
            960 << 6,
            408 << 6,
            960 << 6
        ]);
        p010Player.GetProGpuSurface().Publish(
            p010Frame);
        using var rgbMaterial =
            new ProGpuMediaTextureMaterial
            {
                MediaPlayer = rgbPlayer
            };
        using var p010Material =
            new ProGpuMediaTextureMaterial
            {
                MediaPlayer = p010Player
            };
        Viewport3D viewport =
            CreateMixedMediaMeshViewport(
                rgbMaterial,
                p010Material,
                rgbMaterial);
        viewport.Measure(new Vector2(180f, 90f));
        viewport.Arrange(
            new Rect(0f, 0f, 180f, 90f));

        compositor.RenderScene(
            viewport,
            180,
            90,
            target.ViewPtr);

        var extension =
            Assert.IsType<Mesh3DExtensionPipeline>(
                compositor.GetExtension(
                    CompositorBuiltInExtensions
                        .Mesh3D));
        Assert.Equal(
            0,
            extension.LiveMaterialBlurResourceCount);
        AssertFilledRedMediaMesh(
            target.ReadPixels());
    }

    [Fact]
    public void Nv12ProcessorRendersScaledRgbaThumbnailTarget()
    {
        WgpuContext context =
            HeadlessWindow.Shared.Context;
        using var frame =
            new TestPlanarGpuFrame(context);
        frame.LumaTexture.WritePixels(
            new byte[]
            {
                63, 63, 63, 63,
                63, 63, 63, 63
            });
        frame.ChromaTexture.WritePixels(
            new byte[]
            {
                102, 240,
                102, 240
            });
        using var target =
            new GpuTexture(
                context,
                8,
                4,
                TextureFormat.Rgba8Unorm,
                TextureUsage.RenderAttachment |
                TextureUsage.CopySrc,
                "NV12 RGBA thumbnail test");

        GpuNv12Processor.ProcessToRgba(
            frame.LumaTexture,
            frame.ChromaTexture,
            target,
            saturation: 1f,
            grayscale: 0f,
            inFlightSlot: 0);
        byte[] pixels =
            target.ReadPixels();

        int redPixels = 0;
        for (int offset = 0;
             offset < pixels.Length;
             offset += 4)
        {
            if (pixels[offset] >= 180 &&
                pixels[offset + 1] <= 60 &&
                pixels[offset + 2] <= 60 &&
                pixels[offset + 3] == 255)
            {
                redPixels++;
            }
        }
        Assert.Equal(32, redPixels);
    }

    [Fact]
    public void Nv12ProcessorAppliesAffineColorTransform()
    {
        WgpuContext context =
            HeadlessWindow.Shared.Context;
        using var frame =
            new TestPlanarGpuFrame(context);
        frame.LumaTexture.WritePixels(
            new byte[]
            {
                63, 63, 63, 63,
                63, 63, 63, 63
            });
        frame.ChromaTexture.WritePixels(
            new byte[]
            {
                102, 240,
                102, 240
            });
        using var target =
            new GpuTexture(
                context,
                4,
                2,
                TextureFormat.Rgba8Unorm,
                TextureUsage.RenderAttachment |
                TextureUsage.CopySrc,
                "NV12 affine RGBA test");
        MediaVideoColorTransform effect =
            MediaVideoColorEffectFactory
                .CreateTransform(invert: 1f);

        GpuNv12Processor.ProcessToRgba(
            frame.LumaTexture,
            frame.ChromaTexture,
            target,
            new GpuTextureColorTransform(
                effect.Red,
                effect.Green,
                effect.Blue),
            inFlightSlot: 0);
        byte[] pixels =
            target.ReadPixels();

        for (int offset = 0;
             offset < pixels.Length;
             offset += 4)
        {
            Assert.InRange(pixels[offset], 0, 70);
            Assert.InRange(pixels[offset + 1], 190, 255);
            Assert.InRange(pixels[offset + 2], 190, 255);
            Assert.Equal(255, pixels[offset + 3]);
        }
    }

    [Fact]
    public void Nv12ProcessorEncodesRgbaForNativeVideoTargets()
    {
        WgpuContext context =
            HeadlessWindow.Shared.Context;
        using var source =
            new GpuTexture(
                context,
                4,
                2,
                TextureFormat.Rgba8Unorm,
                TextureUsage.TextureBinding |
                TextureUsage.CopyDst,
                "RGBA to NV12 source");
        using var luma =
            new GpuTexture(
                context,
                4,
                2,
                TextureFormat.R8Unorm,
                TextureUsage.TextureBinding |
                TextureUsage.RenderAttachment,
                "RGBA to NV12 luma");
        using var chroma =
            new GpuTexture(
                context,
                2,
                1,
                TextureFormat.RG8Unorm,
                TextureUsage.TextureBinding |
                TextureUsage.RenderAttachment,
                "RGBA to NV12 chroma");
        using var decoded =
            new GpuTexture(
                context,
                4,
                2,
                TextureFormat.Rgba8Unorm,
                TextureUsage.RenderAttachment |
                TextureUsage.CopySrc,
                "RGBA to NV12 verification");
        byte[] sourcePixels =
            new byte[4 * 2 * 4];
        for (int offset = 0;
             offset < sourcePixels.Length;
             offset += 4)
        {
            sourcePixels[offset] = 255;
            sourcePixels[offset + 3] = 255;
        }
        source.WritePixels(sourcePixels);

        GpuNv12Processor.ProcessRgbaToNv12(
            source,
            luma,
            chroma,
            inFlightSlot: 0);
        GpuNv12Processor.ProcessToRgba(
            luma,
            chroma,
            decoded,
            GpuTextureColorTransform.Identity,
            inFlightSlot: 1);

        byte[] pixels = decoded.ReadPixels();
        for (int offset = 0;
             offset < pixels.Length;
             offset += 4)
        {
            Assert.InRange(pixels[offset], 245, 255);
            Assert.InRange(pixels[offset + 1], 0, 12);
            Assert.InRange(pixels[offset + 2], 0, 12);
            Assert.Equal(255, pixels[offset + 3]);
        }
    }

    [Fact]
    public void WinUiMesh3DMaterialAppliesSessionCropRotationAndMirror()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(160, 90);
        using var player =
            new Windows.Media.Playback.MediaPlayer();
        MediaGpuSurface surface =
            player.GetProGpuSurface();
        TestGpuFrame frame = CreateFrame(sequence: 21);
        frame.Texture.WritePixels(
        new byte[]
        {
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
            255, 255, 0, 255,
            255, 0, 255, 255,
            0, 255, 255, 255,
            255, 255, 255, 255,
            0, 0, 0, 255
        });
        surface.Publish(frame);
        player.PlaybackSession.NormalizedSourceRect =
            new Windows.Foundation.Rect(0d, 0d, 0.5d, 1d);
        player.PlaybackSession.PlaybackRotation =
            MediaRotation.Clockwise90Degrees;
        using var material =
            new ProGpuMediaTextureMaterial
            {
                MediaPlayer = player,
                SamplingMode = TextureSamplingMode.Nearest
            };
        var mesh = new MeshGeometry3D
        {
            Positions =
            [
                new Vector3(-1.5f, -0.8f, 0f),
                new Vector3(1.5f, -0.8f, 0f),
                new Vector3(1.5f, 0.8f, 0f),
                new Vector3(-1.5f, 0.8f, 0f)
            ],
            Normals =
            [
                -Vector3.UnitZ,
                -Vector3.UnitZ,
                -Vector3.UnitZ,
                -Vector3.UnitZ
            ],
            TextureCoordinates =
            [
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f),
                new Vector2(0f, 0f)
            ],
            TriangleIndices = [0, 1, 2, 0, 2, 3]
        };
        var viewport = new Viewport3D
        {
            Camera = new OrthographicCamera
            {
                Width = 4f
            },
            ShadingMode = ShadingMode3D.Flat
        };
        viewport.Children.Add(
            new ModelVisual3D
            {
                Content = new GeometryModel3D
                {
                    Geometry = mesh,
                    Material = material,
                    BackMaterial = material
                }
            });
        window.Content = viewport;

        try
        {
            window.Render();
            long versionBeforePresentationChange =
                viewport.ChangeVersion;
            player.PlaybackSession.IsMirroring = true;
            Assert.True(
                viewport.ChangeVersion >
                    versionBeforePresentationChange);
            window.Render();
            byte[] pixels = window.ReadPixels();
            (int Count, long X, long Y) red = default;
            (int Count, long X, long Y) magenta = default;
            (int Count, long X, long Y) green = default;
            (int Count, long X, long Y) cyan = default;
            for (int y = 0; y < 90; y++)
            {
                for (int x = 0; x < 160; x++)
                {
                    int offset = (y * 160 + x) * 4;
                    byte r = pixels[offset];
                    byte g = pixels[offset + 1];
                    byte b = pixels[offset + 2];
                    if (r > 220 && g < 35 && b < 35)
                    {
                        red.Count++;
                        red.X += x;
                        red.Y += y;
                    }
                    else if (r > 220 && g < 35 && b > 220)
                    {
                        magenta.Count++;
                        magenta.X += x;
                        magenta.Y += y;
                    }
                    else if (r < 35 && g > 220 && b < 35)
                    {
                        green.Count++;
                        green.X += x;
                        green.Y += y;
                    }
                    else if (r < 35 && g > 220 && b > 220)
                    {
                        cyan.Count++;
                        cyan.X += x;
                        cyan.Y += y;
                    }
                }
            }

            string counts =
                $"red={red.Count}, magenta={magenta.Count}, " +
                $"green={green.Count}, cyan={cyan.Count}";
            Assert.True(red.Count > 200, counts);
            Assert.True(magenta.Count > 200, counts);
            Assert.True(green.Count > 200, counts);
            Assert.True(cyan.Count > 200, counts);
            double redX = (double)red.X / red.Count;
            double redY = (double)red.Y / red.Count;
            double magentaX =
                (double)magenta.X / magenta.Count;
            double magentaY =
                (double)magenta.Y / magenta.Count;
            double greenX = (double)green.X / green.Count;
            double greenY = (double)green.Y / green.Count;
            double cyanX = (double)cyan.X / cyan.Count;
            double cyanY = (double)cyan.Y / cyan.Count;
            string layout =
                $"{counts}; red=({redX:F1},{redY:F1}), " +
                $"magenta=({magentaX:F1},{magentaY:F1}), " +
                $"green=({greenX:F1},{greenY:F1}), " +
                $"cyan=({cyanX:F1},{cyanY:F1})";
            // The default Viewport3D camera reverses screen X. The expected
            // UV arrangement after crop, clockwise rotation, then mirror is
            // red/magenta over green/cyan.
            Assert.True(magentaX < redX, layout);
            Assert.True(redY < greenY, layout);
            Assert.True(cyanX < greenX, layout);
            Assert.True(magentaY < cyanY, layout);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public async Task TypedEffectRegistryReplaysEffectsToNewProvider()
    {
        var providers = new MediaProviderRegistry();
        var providerFactory = new RecordingProviderFactory(priority: 10);
        using IDisposable providerRegistration =
            providers.Register(providerFactory);
        var effects = new MediaEffectRegistry();
        var effectFactory = new RecordingEffectFactory();
        using IDisposable effectRegistration =
            effects.Register(effectFactory);
        using var engine = new MediaPlaybackEngine(
            providers,
            effects);

        engine.AddEffect(
            effectFactory.ActivatableClassId,
            MediaEffectKind.Audio,
            optional: true,
            new Dictionary<string, object?>());
        engine.AddEffect(
            "missing.optional.effect",
            MediaEffectKind.Video,
            optional: true,
            new Dictionary<string, object?>());
        Assert.Throws<NotSupportedException>(() =>
            engine.AddEffect(
                "missing.required.effect",
                MediaEffectKind.Video,
                optional: false,
                new Dictionary<string, object?>()));

        using var source = MediaSourceDescriptor.FromUri(
            new Uri("https://example.invalid/effects.mp4"));
        await engine.SetSourceAsync(source);

        RecordingProvider provider = Assert.IsType<RecordingProvider>(
            providerFactory.LastProvider);
        Assert.Equal(1, provider.AddEffectCalls);
        Assert.True(provider.LastEffectOptional);
        RecordingEffect effect = Assert.IsType<RecordingEffect>(
            effectFactory.LastEffect);

        engine.RemoveAllEffects();

        Assert.Equal(1, provider.RemoveAllEffectsCalls);
        Assert.True(effect.IsDisposed);
    }

    [Fact]
    public void WinUiPlaybackSessionProjectsOfficialTimeRangesAndEvents()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource source = MediaSource.CreateFromUri(
            new Uri("https://example.invalid/ranges.mp4"));
        player.Source = source;
        RecordingProvider provider = Assert.IsType<RecordingProvider>(
            factory.LastProvider);
        object? bufferingArgs = null;
        int bufferedChanges = 0;
        int playedChanges = 0;
        player.PlaybackSession.BufferingStarted +=
            (_, args) => bufferingArgs = args;
        player.PlaybackSession.BufferedRangesChanged +=
            (_, _) => bufferedChanges++;
        player.PlaybackSession.PlayedRangesChanged +=
            (_, _) => playedChanges++;

        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Buffering,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(10),
            1920,
            1080,
            BufferingProgress: 0.25d,
            DownloadProgress: 0.5d,
            PlaybackRate: 1d,
            new MediaProviderCapabilities(
                CanPause: true,
                CanSeek: true,
                SupportsRate: true,
                SupportsFrameStepping: true,
                HardwareDecoded: true,
                HasAudio: true,
                HasVideo: true)));

        MediaTimeRange buffered = Assert.Single(
            player.PlaybackSession.GetBufferedRanges());
        Assert.Equal(TimeSpan.Zero, buffered.Start);
        Assert.Equal(TimeSpan.FromSeconds(5), buffered.End);
        MediaTimeRange played = Assert.Single(
            player.PlaybackSession.GetPlayedRanges());
        Assert.Equal(TimeSpan.FromSeconds(3), played.End);
        MediaTimeRange seekable = Assert.Single(
            player.PlaybackSession.GetSeekableRanges());
        Assert.Equal(TimeSpan.FromSeconds(10), seekable.End);
        Assert.True(
            player.PlaybackSession.IsSupportedPlaybackRateRange(
                0.5d,
                2d));
        Assert.False(
            player.PlaybackSession.IsSupportedPlaybackRateRange(
                0.25d,
                4d));
        Assert.IsType<
            MediaPlaybackSessionBufferingStartedEventArgs>(
            bufferingArgs);
        Assert.True(bufferedChanges > 0);
        Assert.True(playedChanges > 0);
    }

    [Fact]
    public void WinUiPlaybackItemOwnsItsMediaSourceAssociation()
    {
        using MediaSource associatedSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/associated.mp4"));
        using MediaSource previousSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/previous.mp4"));

        Assert.Null(
            MediaPlaybackItem.FindFromMediaSource(
                associatedSource));

        var item = new MediaPlaybackItem(associatedSource);

        Assert.Same(
            item,
            MediaPlaybackItem.FindFromMediaSource(
                associatedSource));
        Assert.Throws<InvalidOperationException>(
            () => new MediaPlaybackItem(associatedSource));

        using var player = new MediaPlayer(
            new MediaProviderRegistry(),
            new MediaEffectRegistry());
        player.Source = previousSource;

        Assert.Throws<InvalidOperationException>(
            () => player.Source = associatedSource);
        Assert.Same(previousSource, player.Source);

        player.Source = item;

        Assert.Same(item, player.Source);
    }

    [Fact]
    public void WinUiPlaybackItemDisplayPropertiesRequireApply()
    {
        using MediaSource source =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/metadata.mp4"));
        var item = new MediaPlaybackItem(source);
        using var thumbnailStream =
            new RandomAccessStream(
                new MemoryStream([1, 2, 3]),
                leaveOpen: false);
        var thumbnail = RandomAccessStreamReference
            .CreateFromStream(thumbnailStream);
        MediaItemDisplayProperties edited =
            item.GetDisplayProperties();
        edited.Type = MediaPlaybackType.Video;
        edited.Thumbnail = thumbnail;
        edited.VideoProperties.Title = "Applied title";
        edited.VideoProperties.Subtitle = "Applied subtitle";
        edited.VideoProperties.Genres.Add("Documentary");

        Assert.Equal(
            MediaPlaybackType.Unknown,
            item.GetDisplayProperties().Type);

        item.ApplyDisplayProperties(edited);
        edited.Type = MediaPlaybackType.Music;
        edited.VideoProperties.Title = "Leaked mutation";
        edited.VideoProperties.Genres.Add("Mutation");

        MediaItemDisplayProperties applied =
            item.GetDisplayProperties();
        Assert.Equal(MediaPlaybackType.Video, applied.Type);
        Assert.Same(thumbnail, applied.Thumbnail);
        Assert.Equal(
            "Applied title",
            applied.VideoProperties.Title);
        Assert.Equal(
            "Applied subtitle",
            applied.VideoProperties.Subtitle);
        Assert.Equal(
            ["Documentary"],
            applied.VideoProperties.Genres);

        applied.VideoProperties.Title = "Second mutation";
        applied.VideoProperties.Genres.Clear();

        MediaItemDisplayProperties secondSnapshot =
            item.GetDisplayProperties();
        Assert.Equal(
            "Applied title",
            secondSnapshot.VideoProperties.Title);
        Assert.Equal(
            ["Documentary"],
            secondSnapshot.VideoProperties.Genres);
    }

    [Fact]
    public void WinUiDisplayPropertiesClearAllResetsEveryField()
    {
        using var thumbnailStream =
            new RandomAccessStream(
                new MemoryStream([1]),
                leaveOpen: false);
        var properties = new MediaItemDisplayProperties
        {
            Type = MediaPlaybackType.Music,
            Thumbnail =
                RandomAccessStreamReference.CreateFromStream(
                    thumbnailStream)
        };
        properties.MusicProperties.AlbumArtist = "Album artist";
        properties.MusicProperties.AlbumTitle = "Album";
        properties.MusicProperties.AlbumTrackCount = 9;
        properties.MusicProperties.Artist = "Artist";
        properties.MusicProperties.Genres.Add("Genre");
        properties.MusicProperties.Title = "Title";
        properties.MusicProperties.TrackNumber = 4;
        properties.VideoProperties.Genres.Add("Video genre");
        properties.VideoProperties.Subtitle = "Subtitle";
        properties.VideoProperties.Title = "Video";

        properties.ClearAll();

        Assert.Equal(MediaPlaybackType.Unknown, properties.Type);
        Assert.Null(properties.Thumbnail);
        Assert.Equal(
            string.Empty,
            properties.MusicProperties.AlbumArtist);
        Assert.Equal(
            string.Empty,
            properties.MusicProperties.AlbumTitle);
        Assert.Equal(
            0u,
            properties.MusicProperties.AlbumTrackCount);
        Assert.Equal(
            string.Empty,
            properties.MusicProperties.Artist);
        Assert.Empty(properties.MusicProperties.Genres);
        Assert.Equal(
            string.Empty,
            properties.MusicProperties.Title);
        Assert.Equal(
            0u,
            properties.MusicProperties.TrackNumber);
        Assert.Empty(properties.VideoProperties.Genres);
        Assert.Equal(
            string.Empty,
            properties.VideoProperties.Subtitle);
        Assert.Equal(
            string.Empty,
            properties.VideoProperties.Title);
    }

    [Fact]
    public void WinUiPlaybackItemValidatesDisplayMetadataEnums()
    {
        using MediaSource source =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/enums.mp4"));
        var item = new MediaPlaybackItem(source);
        var properties = new MediaItemDisplayProperties();

        Assert.Equal(
            AutoLoadedDisplayPropertyKind.None,
            item.AutoLoadedDisplayProperties);
        item.AutoLoadedDisplayProperties =
            AutoLoadedDisplayPropertyKind.Video;
        Assert.Equal(
            AutoLoadedDisplayPropertyKind.Video,
            item.AutoLoadedDisplayProperties);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => item.AutoLoadedDisplayProperties =
                (AutoLoadedDisplayPropertyKind)99);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => properties.Type = (MediaPlaybackType)99);
        Assert.Throws<ArgumentNullException>(
            () => item.ApplyDisplayProperties(null!));
    }

    [Fact]
    public async Task RandomAccessStreamReferenceSnapshotsOwnership()
    {
        byte[] sourceBytes = [10, 20, 30, 40];
        using var backing = new MemoryStream(
            sourceBytes,
            writable: true);
        using var source = new RandomAccessStream(
            backing,
            leaveOpen: true);
        source.Seek(2);

        RandomAccessStreamReference reference =
            RandomAccessStreamReference.CreateFromStream(source);

        Assert.Equal(2ul, source.Position);
        sourceBytes[0] = 99;
        using IRandomAccessStreamWithContentType first =
            await reference.OpenReadAsync();
        using IRandomAccessStreamWithContentType second =
            await reference.OpenReadAsync();

        Assert.NotSame(first, second);
        Assert.Equal(
            "application/octet-stream",
            first.ContentType);
        Assert.Equal(4ul, first.Size);
        Assert.Equal(0ul, first.Position);
        Assert.Equal(0ul, second.Position);
        Assert.Equal(10, first.AsStream().ReadByte());
        Assert.Equal(1ul, first.Position);
        Assert.Equal(0ul, second.Position);
        Assert.Equal(10, second.AsStream().ReadByte());
    }

    [Fact]
    public async Task RandomAccessStreamReferenceCreatesFromStorageFile()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"progpu-stream-reference-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "thumbnail.png");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllBytesAsync(
                path,
                [137, 80, 78, 71]);
            StorageFile file =
                await StorageFile.GetFileFromPathAsync(path);
            RandomAccessStreamReference reference =
                RandomAccessStreamReference.CreateFromFile(file);

            using IRandomAccessStreamWithContentType stream =
                await reference.OpenReadAsync();

            Assert.Equal("image/png", file.ContentType);
            Assert.Equal("image/png", stream.ContentType);
            Assert.Equal(4ul, stream.Size);
            Assert.Equal(137, stream.AsStream().ReadByte());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
    }

    [Fact]
    public void WinUiPlaybackItemProjectsItsOwnDownloadProgress()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource firstSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/progress-first.mp4"));
        using MediaSource secondSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/progress-second.mp4"));
        var first = new MediaPlaybackItem(firstSource);
        var second = new MediaPlaybackItem(secondSource);
        var list = new MediaPlaybackList();
        list.Items.Add(first);
        list.Items.Add(second);

        player.Source = list;
        RecordingProvider firstProvider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        firstProvider.Report(CreateSnapshot(0.35d));

        Assert.Equal(0.35d, first.TotalDownloadProgress);
        Assert.Equal(0d, second.TotalDownloadProgress);

        Assert.Same(second, list.MoveNext());
        RecordingProvider secondProvider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        Assert.NotSame(firstProvider, secondProvider);
        secondProvider.Report(CreateSnapshot(0.65d));

        Assert.Equal(0.35d, first.TotalDownloadProgress);
        Assert.Equal(0.65d, second.TotalDownloadProgress);

        static MediaPlaybackSnapshot CreateSnapshot(
            double downloadProgress) =>
            new(
                MediaEnginePlaybackState.Paused,
                TimeSpan.Zero,
                TimeSpan.FromMinutes(2),
                1920,
                1080,
                BufferingProgress: 1d,
                DownloadProgress: downloadProgress,
                PlaybackRate: 1d,
                new MediaProviderCapabilities(
                    CanPause: true,
                    CanSeek: true,
                    SupportsRate: true,
                    SupportsFrameStepping: true,
                    HardwareDecoded: true,
                    HasAudio: true,
                    HasVideo: true));
    }

    [Fact]
    public void WinUiPlaybackListAdvancesAtItemDurationLimit()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource firstSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/first-range.mp4"));
        using MediaSource secondSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/second-range.mp4"));
        var first = new MediaPlaybackItem(
            firstSource,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5))
        {
            CanSkip = false
        };
        var second = new MediaPlaybackItem(
            secondSource,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(7));
        var list = new MediaPlaybackList();
        list.Items.Add(first);
        list.Items.Add(second);
        CurrentMediaPlaybackItemChangedEventArgs? changed = null;
        list.CurrentItemChanged +=
            (_, args) => changed = args;

        player.Source = list;
        RecordingProvider firstProvider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            firstProvider.LastSeek);
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            player.PlaybackSession.NaturalDuration);

        player.Play();
        firstProvider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Playing,
            TimeSpan.FromSeconds(35),
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            new MediaProviderCapabilities(
                CanPause: true,
                CanSeek: true,
                SupportsRate: true,
                SupportsFrameStepping: true,
                HardwareDecoded: true,
                HasAudio: true,
                HasVideo: true)));

        Assert.Same(second, list.CurrentItem);
        Assert.NotNull(changed);
        Assert.Equal(
            MediaPlaybackItemChangedReason.EndOfStream,
            changed.Reason);
        RecordingProvider secondProvider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        Assert.NotSame(firstProvider, secondProvider);
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            secondProvider.LastSeek);
        Assert.Equal(
            TimeSpan.FromSeconds(7),
            player.PlaybackSession.NaturalDuration);
        Assert.Equal(1, secondProvider.PlayCalls);
    }

    [Fact]
    public void WinUiPlaybackListNavigationReturnsCurrentItem()
    {
        using MediaSource firstSource =
            MediaSource.CreateFromUri(
                new Uri("https://example.invalid/first.mp4"));
        using MediaSource secondSource =
            MediaSource.CreateFromUri(
                new Uri("https://example.invalid/second.mp4"));
        using MediaSource thirdSource =
            MediaSource.CreateFromUri(
                new Uri("https://example.invalid/third.mp4"));
        var first = new MediaPlaybackItem(firstSource);
        var second = new MediaPlaybackItem(secondSource)
        {
            IsDisabledInPlaybackList = true
        };
        var third = new MediaPlaybackItem(thirdSource);
        var list = new MediaPlaybackList();
        list.Items.Add(first);
        list.Items.Add(second);
        list.Items.Add(third);

        Assert.Equal(
            typeof(MediaPlaybackItem),
            typeof(MediaPlaybackList)
                .GetMethod(nameof(MediaPlaybackList.MoveNext))!
                .ReturnType);
        Assert.Same(first, list.CurrentItem);
        Assert.Same(third, list.MoveNext());
        Assert.Null(list.MoveNext());
        Assert.Same(third, list.CurrentItem);
        Assert.Same(first, list.MovePrevious());
        Assert.Same(third, list.MoveTo(2));
        Assert.Null(list.MoveTo(3));

        list.AutoRepeatEnabled = true;

        Assert.Same(first, list.MoveNext());
    }

    [Fact]
    public void PlaybackListMutationsPreserveActiveItemAndProvider()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource currentSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/current.mp4"));
        using MediaSource laterSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/later.mp4"));
        using MediaSource prefixSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/prefix.mp4"));
        using MediaSource replacementSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/replacement.mp4"));
        var current = new MediaPlaybackItem(currentSource);
        var later = new MediaPlaybackItem(laterSource);
        var prefix = new MediaPlaybackItem(prefixSource);
        var replacement =
            new MediaPlaybackItem(replacementSource);
        var list = new MediaPlaybackList();
        int sourceInvalidations = 0;
        int playbackOrderChanges = 0;
        var itemChanges =
            new List<
                CurrentMediaPlaybackItemChangedEventArgs>();
        ((IProGpuMediaPlaybackSource)list)
            .SourceInvalidated +=
            (_, _) => sourceInvalidations++;
        list.PlaybackOrderChanged +=
            (_, _) => playbackOrderChanges++;
        list.CurrentItemChanged +=
            (_, args) => itemChanges.Add(args);

        list.Items.Add(current);
        list.Items.Add(later);
        player.Source = list;
        RecordingProvider activeProvider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);

        list.Items.Insert(0, prefix);

        Assert.Same(current, list.CurrentItem);
        Assert.Equal(1u, list.CurrentItemIndex);
        Assert.Same(activeProvider, factory.LastProvider);

        list.Items.Remove(prefix);

        Assert.Same(current, list.CurrentItem);
        Assert.Equal(0u, list.CurrentItemIndex);
        Assert.Same(activeProvider, factory.LastProvider);

        list.Items[1] = replacement;

        Assert.Same(current, list.CurrentItem);
        Assert.Equal(0u, list.CurrentItemIndex);
        Assert.Same(activeProvider, factory.LastProvider);
        Assert.Equal(1, sourceInvalidations);
        Assert.Equal(4, playbackOrderChanges);
        Assert.Single(itemChanges);
        Assert.Null(itemChanges[0].OldItem);
        Assert.Same(current, itemChanges[0].NewItem);
        Assert.Equal(
            MediaPlaybackItemChangedReason.InitialItem,
            itemChanges[0].Reason);

        list.Items.RemoveAt(0);

        Assert.Same(replacement, list.CurrentItem);
        Assert.Equal(0u, list.CurrentItemIndex);
        Assert.NotSame(activeProvider, factory.LastProvider);
        Assert.Equal(2, sourceInvalidations);
        Assert.Equal(4, playbackOrderChanges);
        Assert.Equal(2, itemChanges.Count);
        Assert.Same(current, itemChanges[1].OldItem);
        Assert.Same(replacement, itemChanges[1].NewItem);
        Assert.Equal(
            MediaPlaybackItemChangedReason.AppRequested,
            itemChanges[1].Reason);

        RecordingProvider replacementProvider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        list.Items[0] = later;

        Assert.Same(later, list.CurrentItem);
        Assert.Equal(0u, list.CurrentItemIndex);
        Assert.NotSame(
            replacementProvider,
            factory.LastProvider);
        Assert.Equal(3, sourceInvalidations);
        Assert.Equal(3, itemChanges.Count);
        Assert.Same(replacement, itemChanges[2].OldItem);
        Assert.Same(later, itemChanges[2].NewItem);

        list.Items.Clear();

        Assert.Null(list.CurrentItem);
        Assert.Equal(uint.MaxValue, list.CurrentItemIndex);
        Assert.Equal(4, sourceInvalidations);
        Assert.Equal(4, itemChanges.Count);
        Assert.Same(later, itemChanges[3].OldItem);
        Assert.Null(itemChanges[3].NewItem);
    }

    [Fact]
    public void PlaybackItemStateRefreshesCommandsWithoutProviderReopen()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource firstSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/live-state-first.mp4"));
        using MediaSource secondSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/live-state-second.mp4"));
        var first = new MediaPlaybackItem(firstSource);
        var second = new MediaPlaybackItem(secondSource);
        var list = new MediaPlaybackList();
        list.Items.Add(first);
        list.Items.Add(second);
        int sourceInvalidations = 0;
        int playbackOrderChanges = 0;
        ((IProGpuMediaPlaybackSource)list)
            .SourceInvalidated +=
            (_, _) => sourceInvalidations++;
        list.PlaybackOrderChanged +=
            (_, _) => playbackOrderChanges++;
        player.Source = list;
        player.Play();
        RecordingProvider activeProvider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);

        Assert.True(player.CommandManager.NextBehavior.IsEnabled);

        first.CanSkip = false;

        Assert.False(
            player.CommandManager.NextBehavior.IsEnabled);
        Assert.Equal(1, playbackOrderChanges);
        Assert.Equal(0, sourceInvalidations);
        Assert.Same(activeProvider, factory.LastProvider);
        Assert.Same(first, list.CurrentItem);

        first.CanSkip = false;

        Assert.Equal(1, playbackOrderChanges);

        first.CanSkip = true;
        first.IsDisabledInPlaybackList = true;

        Assert.True(player.CommandManager.NextBehavior.IsEnabled);
        Assert.Same(first, list.CurrentItem);
        Assert.Same(activeProvider, factory.LastProvider);

        second.IsDisabledInPlaybackList = true;

        Assert.False(
            player.CommandManager.NextBehavior.IsEnabled);
        Assert.Equal(4, playbackOrderChanges);
        Assert.Equal(0, sourceInvalidations);
        Assert.Same(activeProvider, factory.LastProvider);
        Assert.Same(first, list.CurrentItem);

        second.IsDisabledInPlaybackList = false;

        Assert.True(player.CommandManager.NextBehavior.IsEnabled);
        Assert.Equal(5, playbackOrderChanges);
        Assert.Equal(0, sourceInvalidations);
        Assert.Same(activeProvider, factory.LastProvider);
    }

    [Fact]
    public void PlaybackItemStateTracksDuplicateAndSharedListOwnership()
    {
        using MediaSource source =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/shared-state.mp4"));
        var item = new MediaPlaybackItem(source);
        var firstList = new MediaPlaybackList();
        var secondList = new MediaPlaybackList();
        firstList.Items.Add(item);
        firstList.Items.Add(item);
        secondList.Items.Add(item);
        int firstChanges = 0;
        int secondChanges = 0;
        firstList.PlaybackOrderChanged +=
            (_, _) => firstChanges++;
        secondList.PlaybackOrderChanged +=
            (_, _) => secondChanges++;

        item.IsDisabledInPlaybackList = true;

        Assert.Equal(1, firstChanges);
        Assert.Equal(1, secondChanges);

        firstList.Items.RemoveAt(0);
        firstChanges = 0;
        secondChanges = 0;
        item.IsDisabledInPlaybackList = false;

        Assert.Equal(1, firstChanges);
        Assert.Equal(1, secondChanges);

        firstList.Items.Clear();
        secondList.Items.Clear();
        firstChanges = 0;
        secondChanges = 0;
        item.CanSkip = false;

        Assert.Equal(0, firstChanges);
        Assert.Equal(0, secondChanges);
    }

    [Fact]
    public void WinUiPlaybackListCanSkipBlocksOnlyManualActiveNavigation()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource firstSource =
            MediaSource.CreateFromUri(
                new Uri("https://example.invalid/unskippable.mp4"));
        using MediaSource secondSource =
            MediaSource.CreateFromUri(
                new Uri("https://example.invalid/skippable.mp4"));
        var first = new MediaPlaybackItem(firstSource)
        {
            CanSkip = false
        };
        var second = new MediaPlaybackItem(secondSource);
        var list = new MediaPlaybackList();
        list.Items.Add(first);
        list.Items.Add(second);
        player.Source = list;

        Assert.True(player.CommandManager.NextBehavior.IsEnabled);
        player.Play();

        Assert.Equal(
            MediaPlaybackState.Playing,
            player.PlaybackSession.PlaybackState);
        Assert.False(player.CommandManager.NextBehavior.IsEnabled);
        Assert.False(
            player.TryDispatchProGpuCommand(
                new ProGpuMediaPlaybackCommand(
                    ProGpuMediaPlaybackCommandKind.Next)));
        Assert.Throws<InvalidOperationException>(
            () => list.MoveNext());
        Assert.Throws<InvalidOperationException>(
            () => list.MoveTo(1));
        Assert.Throws<InvalidOperationException>(
            () => list.StartingItem = second);
        Assert.Null(list.StartingItem);
        Assert.Same(first, list.MoveTo(0));
        Assert.Same(first, list.CurrentItem);

        player.Pause();

        Assert.True(player.CommandManager.NextBehavior.IsEnabled);
        Assert.True(
            player.TryDispatchProGpuCommand(
                new ProGpuMediaPlaybackCommand(
                    ProGpuMediaPlaybackCommandKind.Next)));
        Assert.Same(second, list.CurrentItem);
    }

    [Fact]
    public void SharedPlaybackListTracksEveryAttachedPlayerIndependently()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var firstPlayer = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using var secondPlayer = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource firstSource =
            MediaSource.CreateFromUri(
                new Uri("https://example.invalid/shared-first.mp4"));
        using MediaSource secondSource =
            MediaSource.CreateFromUri(
                new Uri("https://example.invalid/shared-second.mp4"));
        var first = new MediaPlaybackItem(firstSource)
        {
            CanSkip = false
        };
        var second = new MediaPlaybackItem(secondSource);
        var list = new MediaPlaybackList();
        list.Items.Add(first);
        list.Items.Add(second);
        firstPlayer.Source = list;
        secondPlayer.Source = list;

        firstPlayer.Play();
        secondPlayer.Play();

        Assert.Throws<InvalidOperationException>(
            () => list.MoveNext());

        firstPlayer.Source = null;

        Assert.Throws<InvalidOperationException>(
            () => list.MoveNext());

        secondPlayer.Pause();

        Assert.Same(second, list.MoveNext());
    }

    [Fact]
    public void WinUiTransportControlDefaultsMatchOfficialContract()
    {
        var controls = new MediaTransportControls();

        Assert.True(controls.IsZoomButtonVisible);
        Assert.True(controls.IsZoomEnabled);
        Assert.False(controls.IsFastForwardButtonVisible);
        Assert.False(controls.IsFastForwardEnabled);
        Assert.False(controls.IsFastRewindButtonVisible);
        Assert.False(controls.IsFastRewindEnabled);
        Assert.False(controls.IsStopButtonVisible);
        Assert.False(controls.IsStopEnabled);
        Assert.True(controls.IsVolumeButtonVisible);
        Assert.True(controls.IsVolumeEnabled);
        Assert.False(
            controls.IsPlaybackRateButtonVisible);
        Assert.False(controls.IsPlaybackRateEnabled);
        Assert.True(controls.IsSeekBarVisible);
        Assert.True(controls.IsSeekEnabled);
        Assert.False(controls.IsCompact);
        Assert.False(controls.IsSkipForwardButtonVisible);
        Assert.False(controls.IsSkipForwardEnabled);
        Assert.False(
            controls.IsSkipBackwardButtonVisible);
        Assert.False(controls.IsSkipBackwardEnabled);
        Assert.False(controls.IsNextTrackButtonVisible);
        Assert.False(
            controls.IsPreviousTrackButtonVisible);
        Assert.Equal(
            FastPlayFallbackBehaviour.Skip,
            controls.FastPlayFallbackBehaviour);
        Assert.True(controls.ShowAndHideAutomatically);
        Assert.False(controls.IsRepeatEnabled);
        Assert.False(controls.IsRepeatButtonVisible);
    }

    [Fact]
    public void WinUiTransportControlsDriveAttachedPlayer()
    {
        var registry = new MediaProviderRegistry();
        var factory =
            new RecordingProviderFactory(priority: 10);
        using IDisposable registration =
            registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        var element = new MediaPlayerElement();
        element.SetMediaPlayer(player);
        MediaTransportControls controls =
            Assert.IsType<MediaTransportControls>(
                element.TransportControls);
        controls.IsStopEnabled = true;
        controls.IsRepeatEnabled = true;
        controls.IsPlaybackRateEnabled = true;

        using MediaSource source =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/transport.mp4"));
        element.Source = source;
        RecordingProvider provider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);

        Assert.Same(player, controls.AttachedMediaPlayer);

        controls.ExecutePlayPause();
        Assert.Equal(1, provider.PlayCalls);

        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Playing,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            TransportCapabilities()));
        controls.ExecutePlayPause();
        Assert.Equal(1, provider.PauseCalls);

        controls.ExecuteSeek(TimeSpan.FromSeconds(37));
        Assert.Equal(
            TimeSpan.FromSeconds(37),
            provider.LastSeek);

        controls.ExecuteVolume(0.25d);
        Assert.Equal(0.25d, provider.Volume);

        controls.ExecutePlaybackRate(1.5d);
        Assert.Equal(1.5d, provider.Rate);

        controls.ExecuteRepeat();
        Assert.True(provider.Looping);

        controls.ExecuteStop();
        Assert.Equal(2, provider.PauseCalls);
        Assert.Equal(TimeSpan.Zero, provider.LastSeek);

        player.CommandManager.IsEnabled = false;
        controls.ExecutePlayPause();
        controls.ExecuteSeek(TimeSpan.FromSeconds(10));
        controls.ExecuteVolume(0.75d);

        Assert.Equal(1, provider.PlayCalls);
        Assert.Equal(TimeSpan.Zero, provider.LastSeek);
        Assert.Equal(0.25d, provider.Volume);

        static MediaProviderCapabilities
            TransportCapabilities() =>
            new(
                CanPause: true,
                CanSeek: true,
                SupportsRate: true,
                SupportsFrameStepping: true,
                HardwareDecoded: true,
                HasAudio: true,
                HasVideo: true);
    }

    [Fact]
    public void WinUiPlayerPointerRestoresAutoHiddenControls()
    {
        var element = new MediaPlayerElement
        {
            Width = 640f,
            Height = 360f,
            AreTransportControlsEnabled = true
        };
        element.Measure(new Vector2(640f, 360f));
        element.Arrange(
            new ProGPU.Scene.Rect(
                0f,
                0f,
                640f,
                360f));
        MediaTransportControls controls =
            Assert.IsType<MediaTransportControls>(
                element.TransportControls);
        controls.Hide();
        WindowInputState previous = InputSystem.Current;
        try
        {
            InputSystem.Current =
                InputSystem.CreateExternalState(element);
            FrameworkElement hit =
                Assert.IsAssignableFrom<FrameworkElement>(
                    InputSystem.HitTest(
                        new Vector2(320f, 180f)));

            hit.OnPointerPressed(
                new PointerRoutedEventArgs
                {
                    Position =
                        new Vector2(320f, 180f)
                });

            Assert.Equal(
                Visibility.Visible,
                controls.Visibility);
        }
        finally
        {
            InputSystem.Current = previous;
        }
    }

    [Fact]
    public void WinUiTransportControlsRenderVisibleChrome()
    {
        using var window = new HeadlessWindow(640, 360);
        var element = new MediaPlayerElement
        {
            Width = 640f,
            Height = 360f
        };
        window.Content = element;
        window.Render();
        byte[] hiddenPixels = window.ReadPixels();

        element.AreTransportControlsEnabled = true;
        element.TransportControls!
            .ShowAndHideAutomatically = false;
        window.Render();
        byte[] visiblePixels = window.ReadPixels();

        int changedPixels = 0;
        for (int offset = 0;
             offset < hiddenPixels.Length;
             offset += 4)
        {
            if (!hiddenPixels.AsSpan(offset, 4)
                    .SequenceEqual(
                        visiblePixels.AsSpan(offset, 4)))
            {
                changedPixels++;
            }
        }

        Assert.True(
            changedPixels >= 1_000,
            $"Expected visible transport chrome, but only " +
            $"{changedPixels} pixels changed.");
    }

    [Fact]
    public void WinUiThumbnailRequestHonorsDeferralAndStream()
    {
        var element = new MediaPlayerElement();
        MediaTransportControls controls =
            Assert.IsType<MediaTransportControls>(
                element.TransportControls);
        var stream = new TestInputStream();
        Windows.Foundation.Deferral? deferral = null;
        int raised = 0;
        controls.ThumbnailRequested +=
            (_, args) =>
            {
                raised++;
                deferral = args.GetDeferral();
                args.SetThumbnailImage(stream);
            };

        controls.RaiseThumbnailRequested();

        Assert.Equal(1, raised);
        Assert.Null(controls.LastThumbnailImage);

        deferral!.Complete();

        Assert.Same(
            stream,
            controls.LastThumbnailImage);
    }

    [Fact]
    public void WinUiPlayerElementShowsPosterUntilFirstVideoFrame()
    {
        using var player = new MediaPlayer();
        var element = new MediaPlayerElement();
        element.SetMediaPlayer(player);
        var poster =
            new EncodedImageSource(
                new byte[] { 1, 2, 3, 4 },
                suggestedWidth: 320,
                suggestedHeight: 180);

        element.PosterSource = poster;
        element.Stretch = Stretch.UniformToFill;

        Assert.Same(poster, element.PosterImage.Source);
        Assert.Equal(
            Stretch.UniformToFill,
            element.PosterImage.Stretch);
        Assert.Equal(
            Visibility.Visible,
            element.PosterImage.Visibility);

        player.GetProGpuSurface().Publish(
            CreateFrame(sequence: 91));

        Assert.Equal(
            Visibility.Collapsed,
            element.PosterImage.Visibility);

        using MediaSource nextSource =
            MediaSource.CreateFromUri(
                new Uri(
                    "https://example.invalid/next-poster.mp4"));
        element.Source = nextSource;

        Assert.Equal(
            Visibility.Visible,
            element.PosterImage.Visibility);
    }

    [Fact]
    public void WinUiPlayerProjectsLegacyStateAndProviderConfiguration()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry())
        {
            AudioCategory = MediaPlayerAudioCategory.Movie,
            AudioDeviceType =
                MediaPlayerAudioDeviceType.Communications,
            RealTimePlayback = true,
            StereoscopicVideoRenderMode =
                StereoscopicVideoRenderMode.Stereo
        };
        using MediaSource source = MediaSource.CreateFromUri(
            new Uri("https://example.invalid/legacy.mp4"));
        int bufferingStarted = 0;
        int bufferingEnded = 0;
        int stateChanges = 0;
        double changedRate = 0d;
#pragma warning disable CS0618
        player.BufferingStarted += (_, _) => bufferingStarted++;
        player.BufferingEnded += (_, _) => bufferingEnded++;
        player.CurrentStateChanged += (_, _) => stateChanges++;
        player.MediaPlayerRateChanged +=
            (_, args) => changedRate = args.NewRate;
#pragma warning restore CS0618

        player.Source = source;
        RecordingProvider provider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);

        Assert.Equal(
            MediaAudioCategory.Movie,
            provider.Configuration.AudioCategory);
        Assert.Equal(
            MediaAudioDeviceRole.Communications,
            provider.Configuration.AudioDeviceRole);
        Assert.True(provider.Configuration.RealTimePlayback);
        Assert.Equal(
            MediaStereoscopicRenderMode.Stereo,
            provider.Configuration.StereoscopicRenderMode);
#pragma warning disable CS0618
        Assert.Equal(TimeSpan.FromMinutes(2), player.NaturalDuration);
        Assert.Equal(1d, player.BufferingProgress);
        Assert.Equal(MediaPlayerState.Paused, player.CurrentState);
#pragma warning restore CS0618

        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Buffering,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            0.5d,
            1d,
            1d,
            engineCapabilities()));
        provider.Report(new MediaPlaybackSnapshot(
            MediaEnginePlaybackState.Paused,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            engineCapabilities()));
        player.PlaybackSession.PlaybackRate = 1.5d;

        Assert.Equal(1, bufferingStarted);
        Assert.Equal(1, bufferingEnded);
        Assert.Equal(4, stateChanges);
        Assert.Equal(1.5d, changedRate);

        static MediaProviderCapabilities engineCapabilities() =>
            new(
                CanPause: true,
                CanSeek: true,
                SupportsRate: true,
                SupportsFrameStepping: true,
                HardwareDecoded: true,
                HasAudio: true,
                HasVideo: true);
    }

    [Fact]
    public void WinUiCommandManagerBehaviorsFollowPlaybackState()
    {
        using var player = new MediaPlayer(
            new MediaProviderRegistry(),
            new MediaEffectRegistry());
        var list = new MediaPlaybackList();
        using MediaSource first = MediaSource.CreateFromUri(
            new Uri("https://example.invalid/one.mp4"));
        using MediaSource second = MediaSource.CreateFromUri(
            new Uri("https://example.invalid/two.mp4"));
        list.Items.Add(new MediaPlaybackItem(first));
        list.Items.Add(new MediaPlaybackItem(second));

        player.Source = list;

        Assert.Same(player, player.CommandManager.MediaPlayer);
        Assert.True(player.CommandManager.NextBehavior.IsEnabled);
        Assert.False(
            player.CommandManager.PreviousBehavior.IsEnabled);
        int enabledChanges = 0;
        player.CommandManager.NextBehavior.IsEnabledChanged +=
            (_, _) => enabledChanges++;

        player.CommandManager.NextBehavior.EnablingRule =
            MediaCommandEnablingRule.Never;

        Assert.False(player.CommandManager.NextBehavior.IsEnabled);
        Assert.Equal(1, enabledChanges);
        Assert.Same(
            player.CommandManager,
            player.CommandManager.NextBehavior.CommandManager);
    }

    [Fact]
    public void NativeCommandSeamHonorsWinUiDeferralAndHandled()
    {
        var registry = new MediaProviderRegistry();
        var factory = new RecordingProviderFactory(priority: 10);
        using IDisposable registration = registry.Register(factory);
        using var player = new MediaPlayer(
            registry,
            new MediaEffectRegistry());
        using MediaSource source = MediaSource.CreateFromUri(
            new Uri("https://example.invalid/commands.mp4"));
        player.Source = source;
        RecordingProvider provider =
            Assert.IsType<RecordingProvider>(
                factory.LastProvider);
        Windows.Foundation.Deferral? deferral = null;
        MediaPlaybackCommandManagerPlayReceivedEventArgs?
            received = null;
        player.CommandManager.PlayReceived += (_, args) =>
        {
            received = args;
            deferral = args.GetDeferral();
        };

        bool dispatched = player.TryDispatchProGpuCommand(
            new ProGpuMediaPlaybackCommand(
                ProGpuMediaPlaybackCommandKind.Play));

        Assert.True(dispatched);
        Assert.Equal(0, provider.PlayCalls);
        Assert.NotNull(received);
        received.Handled = true;
        Assert.NotNull(deferral);
        deferral.Complete();
        Assert.Equal(0, provider.PlayCalls);
    }

    [Fact]
    public void ExternalFrameRetainsRejectedNativeOwnerUntilDisposal()
    {
        using var context = new WgpuContext();
        context.SetExternalTextureImporter(
            new RejectingMediaTextureImporter());
        var owner = new RecordingNativeOwner();
        using var frame = CreateExternalFrame(owner);

        Assert.False(
            frame.TryAcquireGpuTextureLease(
                context,
                out IProGpuTextureLease lease));
        Assert.Null(lease);
        Assert.False(owner.IsDisposed);

        frame.Dispose();

        Assert.True(owner.IsDisposed);
    }

    [Fact]
    public void ExternalFrameLeaseDefersImportedNativeOwnerRelease()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        context.SetExternalTextureImporter(
            new AllocatingMediaTextureImporter());
        var owner = new RecordingNativeOwner();
        var frame = CreateExternalFrame(owner);

        Assert.True(
            frame.TryAcquireGpuTextureLease(
                context,
                out IProGpuTextureLease lease));
        frame.Dispose();
        Assert.False(owner.IsDisposed);

        lease.Dispose();
        Assert.False(owner.IsDisposed);
        context.CleanupPendingResources();

        Assert.True(owner.IsDisposed);
    }

    private static ExternalMediaGpuFrame CreateExternalFrame(
        IDisposable owner)
    {
        var descriptor = new MediaGpuFrameDescriptor(
            1,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(16),
            4,
            2,
            MediaVideoPixelFormat.Bgra8,
            MediaTransferMode.NativeZeroCopy,
            new MediaColorInfo(
                MediaColorPrimaries.Bt709,
                MediaTransferFunction.Srgb,
                MediaMatrixCoefficients.Identity,
                FullRange: true));
        var externalDescriptor =
            new ProGpuExternalTextureDescriptor(
                ProGpuExternalTextureHandleKind.IOSurface,
                1,
                descriptor.Width,
                descriptor.Height,
                TextureFormat.Bgra8Unorm,
                TextureUsage.TextureBinding,
                GpuTextureAlphaMode.Straight,
                IsInitialized: true);
        return new ExternalMediaGpuFrame(
            in descriptor,
            in externalDescriptor,
            owner);
    }

    private static TestGpuFrame CreateFrame(long sequence)
    {
        var texture = new GpuTexture(
            HeadlessWindow.Shared.Context,
            4,
            2,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            $"Media test frame {sequence}");
        return new TestGpuFrame(
            texture,
            new MediaGpuFrameDescriptor(
                sequence,
                TimeSpan.FromMilliseconds(sequence * 16),
                TimeSpan.FromMilliseconds(16),
                4,
                2,
                MediaVideoPixelFormat.Rgba8,
                MediaTransferMode.NativeZeroCopy,
                new MediaColorInfo(
                    MediaColorPrimaries.Bt709,
                    MediaTransferFunction.Srgb,
                    MediaMatrixCoefficients.Identity,
                    FullRange: true)));
    }

    private static Viewport3D CreateMediaMeshViewport(
        Material material)
    {
        var mesh = new MeshGeometry3D
        {
            Positions =
            [
                new Vector3(-1.5f, -0.8f, 0f),
                new Vector3(1.5f, -0.8f, 0f),
                new Vector3(1.5f, 0.8f, 0f),
                new Vector3(-1.5f, 0.8f, 0f)
            ],
            Normals =
            [
                -Vector3.UnitZ,
                -Vector3.UnitZ,
                -Vector3.UnitZ,
                -Vector3.UnitZ
            ],
            TextureCoordinates =
            [
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f),
                new Vector2(0f, 0f)
            ],
            TriangleIndices = [0, 1, 2, 0, 2, 3]
        };
        var viewport = new Viewport3D
        {
            Camera = new OrthographicCamera
            {
                Width = 4f
            },
            ShadingMode = ShadingMode3D.Flat
        };
        viewport.Children.Add(
            new ModelVisual3D
            {
                Content = new GeometryModel3D
                {
                    Geometry = mesh,
                    Material = material,
                    BackMaterial = material
                }
            });
        return viewport;
    }

    private static Viewport3D
        CreateMixedMediaMeshViewport(
            params Material[] materials)
    {
        var viewport = new Viewport3D
        {
            Camera = new OrthographicCamera
            {
                Width = 4f
            },
            ShadingMode = ShadingMode3D.Flat
        };
        for (int index = 0;
             index < materials.Length;
             index++)
        {
            float left =
                -1.5f +
                3f * index / materials.Length;
            float right =
                -1.5f +
                3f * (index + 1) /
                    materials.Length;
            var mesh = new MeshGeometry3D
            {
                Positions =
                [
                    new Vector3(left, -0.8f, 0f),
                    new Vector3(right, -0.8f, 0f),
                    new Vector3(right, 0.8f, 0f),
                    new Vector3(left, 0.8f, 0f)
                ],
                Normals =
                [
                    -Vector3.UnitZ,
                    -Vector3.UnitZ,
                    -Vector3.UnitZ,
                    -Vector3.UnitZ
                ],
                TextureCoordinates =
                [
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                    new Vector2(1f, 0f),
                    new Vector2(0f, 0f)
                ],
                TriangleIndices =
                    [0, 1, 2, 0, 2, 3]
            };
            viewport.Children.Add(
                new ModelVisual3D
                {
                    Content = new GeometryModel3D
                    {
                        Geometry = mesh,
                        Material = materials[index],
                        BackMaterial =
                            materials[index]
                    }
                });
        }
        return viewport;
    }

    private static void AssertFilledRedMediaMesh(
        byte[] pixels)
    {
        int redVideoPixels = 0;
        for (int offset = 0;
             offset < pixels.Length;
             offset += 4)
        {
            if (pixels[offset] >= 175 &&
                pixels[offset + 1] <= 70 &&
                pixels[offset + 2] <= 70 &&
                pixels[offset + 3] == 255)
            {
                redVideoPixels++;
            }
        }

        Assert.True(
            redVideoPixels >= 1_000,
            $"Expected a filled converted-red video mesh, " +
            $"found {redVideoPixels} red pixels.");
    }

    private static MediaPlaybackSnapshot
        CreatePlaybackSnapshot(TimeSpan position) =>
        new(
            MediaEnginePlaybackState.Playing,
            position,
            TimeSpan.FromMinutes(2),
            1920,
            1080,
            1d,
            1d,
            1d,
            new MediaProviderCapabilities(
                CanPause: true,
                CanSeek: true,
                SupportsRate: true,
                SupportsFrameStepping: true,
                HardwareDecoded: true,
                HasAudio: true,
                HasVideo: true));

    private sealed class RecordingMediaCue : IMediaCue
    {
        public TimeSpan Duration { get; set; }
        public string Id { get; set; } = string.Empty;
        public TimeSpan StartTime { get; set; }
    }

    private sealed class RecordingTimedCueTimelineClient :
        IMediaTimedCueTimelineClient<RecordingMediaCue>
    {
        public int Entered { get; private set; }
        public int Exited { get; private set; }

        public TimeSpan GetStartTime(
            RecordingMediaCue cue) =>
            cue.StartTime;

        public TimeSpan GetDuration(
            RecordingMediaCue cue) =>
            cue.Duration;

        public void OnCueEntered(
            RecordingMediaCue cue) =>
            Entered++;

        public void OnCueExited(
            RecordingMediaCue cue) =>
            Exited++;
    }

    private sealed class RecordingProviderFactory :
        IMediaPlaybackProviderFactory
    {
        private readonly Func<IMediaGpuFrame>? _frameFactory;

        public RecordingProviderFactory(
            int priority,
            Func<IMediaGpuFrame>? frameFactory = null)
        {
            Priority = priority;
            _frameFactory = frameFactory;
        }

        public string Id => $"test-factory-{Priority}";
        public int Priority { get; }
        public RecordingProvider? LastProvider { get; private set; }

        public bool CanOpen(MediaSourceDescriptor source) => true;

        public ValueTask<IMediaPlaybackProvider> CreateAsync(
            MediaSourceDescriptor source,
            IMediaPlaybackSink sink,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastProvider = new RecordingProvider(
                sink,
                _frameFactory);
            return ValueTask.FromResult<IMediaPlaybackProvider>(
                LastProvider);
        }
    }

    private sealed class RecordingProvider :
        IMediaPlaybackProvider,
        IMediaPlaybackConfigurationProvider,
        IMediaPlaybackTrackProvider,
        IMediaPlaybackTimedMetadataProvider
    {
        private readonly IMediaPlaybackSink _sink;
        private readonly Func<IMediaGpuFrame>? _frameFactory;
        private MediaPlaybackTracksSnapshot _tracks =
            CreateTracks(
                selectedAudioTrackIndex: 0,
                selectedVideoTrackIndex: 0);

        public RecordingProvider(
            IMediaPlaybackSink sink,
            Func<IMediaGpuFrame>? frameFactory)
        {
            _sink = sink;
            _frameFactory = frameFactory;
        }

        public string Id => "test-provider";
        public int PlayCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public TimeSpan LastSeek { get; private set; }
        public double Rate { get; private set; } = 1d;
        public double Volume { get; private set; } = 1d;
        public double Balance { get; private set; }
        public bool Muted { get; private set; }
        public bool Looping { get; private set; }
        public int AddEffectCalls { get; private set; }
        public bool LastEffectOptional { get; private set; }
        public int RemoveAllEffectsCalls { get; private set; }
        public int TrackSelectionCalls { get; private set; }
        public MediaPlaybackTrackKind LastSelectedTrackKind
        {
            get;
            private set;
        }
        public int LastSelectedTrackIndex { get; private set; } = -1;
        public int TimedMetadataModeCalls { get; private set; }
        public int LastTimedMetadataTrackIndex
        {
            get;
            private set;
        } = -1;
        public MediaPlaybackTimedMetadataPresentationMode
            LastTimedMetadataMode { get; private set; }
        public MediaPlaybackConfiguration Configuration
        {
            get;
            private set;
        } = MediaPlaybackConfiguration.Default;

        public ValueTask OpenAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sink.UpdateTracks(_tracks);
            _sink.Opened(new MediaPlaybackSnapshot(
                MediaEnginePlaybackState.Paused,
                TimeSpan.Zero,
                TimeSpan.FromMinutes(2),
                1920,
                1080,
                1d,
                1d,
                Rate,
                new MediaProviderCapabilities(
                    CanPause: true,
                    CanSeek: true,
                    SupportsRate: true,
                    SupportsFrameStepping: true,
                    HardwareDecoded: true,
                    HasAudio: true,
                    HasVideo: true)));
            if (_frameFactory is not null)
            {
                _sink.Present(_frameFactory());
            }
            _sink.UpdateDiagnostics(new MediaProviderDiagnostics(
                HardwareDecoded: true,
                TransferMode: _frameFactory is null
                    ? null
                    : MediaTransferMode.NativeZeroCopy,
                DroppedFrames: 0,
                VideoQueueDepth: 2,
                AudioQueueDepth: 1,
                AudioLatency: TimeSpan.FromMilliseconds(8),
                LastFallbackReason: null));
            return ValueTask.CompletedTask;
        }

        public void Play() => PlayCalls++;
        public void Pause() => PauseCalls++;
        public void Seek(TimeSpan position)
        {
            LastSeek = position;
            _sink.SeekCompleted(position);
        }
        public void SetPlaybackRate(double value) => Rate = value;

        public void SetVolume(
            double volume,
            double balance,
            bool muted)
        {
            Volume = volume;
            Balance = balance;
            Muted = muted;
        }

        public void SetLooping(bool enabled) => Looping = enabled;
        public bool StepForwardOneFrame() => true;
        public bool StepBackwardOneFrame() => true;
        public bool TrySelectTrack(
            MediaPlaybackTrackKind kind,
            int index)
        {
            IReadOnlyList<MediaPlaybackTrackDescriptor> tracks =
                _tracks.GetTracks(kind);
            if (kind is not (
                    MediaPlaybackTrackKind.Audio or
                    MediaPlaybackTrackKind.Video) ||
                index < -1 ||
                index >= tracks.Count)
            {
                return false;
            }
            TrackSelectionCalls++;
            LastSelectedTrackKind = kind;
            LastSelectedTrackIndex = index;
            _tracks = _tracks.WithSelectedIndex(kind, index);
            _sink.UpdateTracks(_tracks);
            return true;
        }
        public bool TrySetTimedMetadataPresentationMode(
            int index,
            MediaPlaybackTimedMetadataPresentationMode mode)
        {
            if ((uint)index >=
                    (uint)_tracks.TimedMetadataTracks.Count ||
                !Enum.IsDefined(mode))
            {
                return false;
            }
            TimedMetadataModeCalls++;
            LastTimedMetadataTrackIndex = index;
            LastTimedMetadataMode = mode;
            return true;
        }
        public void AddEffect(IMediaEffect effect, bool optional)
        {
            AddEffectCalls++;
            LastEffectOptional = optional;
        }

        public void RemoveAllEffects() =>
            RemoveAllEffectsCalls++;
        public void ApplyConfiguration(
            in MediaPlaybackConfiguration configuration) =>
            Configuration = configuration;
        public void Report(MediaPlaybackSnapshot snapshot) =>
            _sink.Update(in snapshot);
        public void ReportTracks(
            MediaPlaybackTracksSnapshot tracks) =>
            _sink.UpdateTracks(tracks);
        public void ReportTimedMetadataCues(
            MediaPlaybackTimedMetadataCueSnapshot snapshot) =>
            _sink.UpdateTimedMetadataCues(snapshot);
        public void ReportEnded() => _sink.Ended();
        public void Dispose() { }

        private static MediaPlaybackTracksSnapshot CreateTracks(
            int selectedAudioTrackIndex,
            int selectedVideoTrackIndex) =>
            new(
                [
                    new MediaPlaybackTrackDescriptor(
                        "audio-en",
                        MediaPlaybackTrackKind.Audio,
                        "English",
                        "English",
                        "en-US",
                        new MediaPlaybackTrackEncoding(
                            "AAC",
                            Bitrate: 192_000,
                            SampleRate: 48_000,
                            ChannelCount: 2),
                        MediaPlaybackTrackSupport.Supported),
                    new MediaPlaybackTrackDescriptor(
                        "audio-pl",
                        MediaPlaybackTrackKind.Audio,
                        "Polish",
                        "Polski",
                        "pl-PL",
                        new MediaPlaybackTrackEncoding(
                            "AAC",
                            Bitrate: 128_000,
                            SampleRate: 48_000,
                            ChannelCount: 2),
                        MediaPlaybackTrackSupport.Supported)
                ],
                selectedAudioTrackIndex,
                [
                    new MediaPlaybackTrackDescriptor(
                        "video-main",
                        MediaPlaybackTrackKind.Video,
                        "Main video",
                        "Main",
                        string.Empty,
                        new MediaPlaybackTrackEncoding(
                            "H264",
                            Bitrate: 5_000_000,
                            Width: 1920,
                            Height: 1080,
                            FrameRateNumerator: 30,
                            FrameRateDenominator: 1),
                        MediaPlaybackTrackSupport.Supported)
                ],
                selectedVideoTrackIndex,
                [
                    new MediaPlaybackTrackDescriptor(
                        "metadata-en",
                        MediaPlaybackTrackKind.TimedMetadata,
                        "English subtitles",
                        "English",
                        "en-US",
                        new MediaPlaybackTrackEncoding(
                            "WebVTT"),
                        MediaPlaybackTrackSupport.Supported,
                        MediaPlaybackTimedMetadataKind.Subtitle,
                        "text/vtt")
                ]);
    }

    private sealed class RecordingEffectFactory :
        IMediaEffectFactory
    {
        public string ActivatableClassId =>
            "ProGPU.Tests.RecordingAudioEffect";

        public RecordingEffect? LastEffect { get; private set; }

        public IMediaEffect Create(
            in MediaEffectDescriptor descriptor)
        {
            LastEffect = new RecordingEffect(descriptor.Kind);
            return LastEffect;
        }
    }

    private sealed class RecordingEffect : IMediaEffect
    {
        private int _disposed;

        public RecordingEffect(MediaEffectKind kind)
        {
            Kind = kind;
        }

        public string Id => "recording-effect";
        public MediaEffectKind Kind { get; }
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose() =>
            Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class TestInputStream : IInputStream
    {
        private readonly MemoryStream _stream = new();

        public Stream AsStream() => _stream;
    }

    private sealed class TestGpuFrame :
        IMediaGpuFrame,
        IProGpuContextTextureLeaseSource
    {
        private readonly SharedGpuTextureSource _source;
        private int _disposed;

        public TestGpuFrame(
            GpuTexture texture,
            MediaGpuFrameDescriptor descriptor)
        {
            Texture = texture;
            Descriptor = descriptor;
            _source = new SharedGpuTextureSource(texture);
        }

        public GpuTexture Texture { get; }
        public MediaGpuFrameDescriptor Descriptor { get; }
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public WgpuContext? LastRequiredContext { get; private set; }

        public bool TryGetGpuTexture(out GpuTexture texture) =>
            _source.TryGetGpuTexture(out texture);

        public bool TryAcquireGpuTextureLease(
            out IProGpuTextureLease lease) =>
            _source.TryAcquireGpuTextureLease(out lease);

        public bool TryGetGpuTexture(
            WgpuContext requiredContext,
            out GpuTexture texture)
        {
            LastRequiredContext = requiredContext;
            if (!Texture.Context.SharesDeviceWith(requiredContext))
            {
                texture = null!;
                return false;
            }
            return TryGetGpuTexture(out texture);
        }

        public bool TryAcquireGpuTextureLease(
            WgpuContext requiredContext,
            out IProGpuTextureLease lease)
        {
            LastRequiredContext = requiredContext;
            if (!Texture.Context.SharesDeviceWith(requiredContext))
            {
                lease = null!;
                return false;
            }
            return TryAcquireGpuTextureLease(out lease);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _source.Dispose();
            }
        }
    }

    private sealed class TestPlanarGpuFrame :
        IMediaGpuPlanarFrame
    {
        private readonly SharedGpuTextureSource _luma;
        private readonly SharedGpuTextureSource _chroma;
        private int _disposed;

        public TestPlanarGpuFrame(
            WgpuContext context,
            bool p010 = false)
        {
            LumaTexture = new GpuTexture(
                context,
                4,
                2,
                p010
                    ? ProGpuTextureFormats.R16Unorm
                    : TextureFormat.R8Unorm,
                TextureUsage.TextureBinding |
                TextureUsage.CopyDst,
                p010
                    ? "Test P010 luma"
                    : "Test NV12 luma");
            ChromaTexture = new GpuTexture(
                context,
                2,
                1,
                p010
                    ? ProGpuTextureFormats.RG16Unorm
                    : TextureFormat.RG8Unorm,
                TextureUsage.TextureBinding |
                TextureUsage.CopyDst,
                p010
                    ? "Test P010 chroma"
                    : "Test NV12 chroma");
            _luma = new SharedGpuTextureSource(LumaTexture);
            _chroma =
                new SharedGpuTextureSource(ChromaTexture);
            Descriptor = new MediaGpuFrameDescriptor(
                1,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(16),
                4,
                2,
                p010
                    ? MediaVideoPixelFormat.P010
                    : MediaVideoPixelFormat.Nv12,
                MediaTransferMode.NativeZeroCopy,
                new MediaColorInfo(
                    MediaColorPrimaries.Bt709,
                    MediaTransferFunction.Bt709,
                    MediaMatrixCoefficients.Bt709,
                    FullRange: false));
        }

        public GpuTexture LumaTexture { get; }
        public GpuTexture ChromaTexture { get; }
        public MediaGpuFrameDescriptor Descriptor { get; }

        public bool TryGetGpuTexture(out GpuTexture texture)
        {
            texture = null!;
            return false;
        }

        public bool TryAcquireGpuTextureLease(
            out IProGpuTextureLease lease)
        {
            lease = null!;
            return false;
        }

        public bool TryAcquireGpuPlaneTextureLeases(
            WgpuContext requiredContext,
            out IProGpuTextureLease lumaLease,
            out IProGpuTextureLease chromaLease)
        {
            if (!LumaTexture.Context.SharesDeviceWith(
                    requiredContext) ||
                !_luma.TryAcquireGpuTextureLease(
                    out lumaLease))
            {
                lumaLease = null!;
                chromaLease = null!;
                return false;
            }
            if (!_chroma.TryAcquireGpuTextureLease(
                    out chromaLease))
            {
                lumaLease.Dispose();
                lumaLease = null!;
                return false;
            }
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) == 0)
            {
                _luma.Dispose();
                _chroma.Dispose();
            }
        }
    }

    private sealed class RecordingNativeOwner : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            Assert.False(IsDisposed);
            IsDisposed = true;
        }
    }

    private sealed class QueuedSynchronizationContext :
        SynchronizationContext
    {
        private readonly Queue<(
            SendOrPostCallback Callback,
            object? State)> _queue = new();

        public int PendingCount => _queue.Count;

        public override void Post(
            SendOrPostCallback callback,
            object? state) =>
            _queue.Enqueue((callback, state));

        public void Drain()
        {
            while (_queue.TryDequeue(
                       out (
                           SendOrPostCallback Callback,
                           object? State) item))
            {
                item.Callback(item.State);
            }
        }
    }

    private sealed class RejectingMediaTextureImporter :
        IProGpuExternalTextureImporter
    {
        public bool TryImportExternalTexture(
            WgpuContext targetContext,
            in ProGpuExternalTextureDescriptor descriptor,
            IDisposable nativeOwner,
            out GpuTexture texture)
        {
            texture = null!;
            return false;
        }
    }

    private sealed unsafe class AllocatingMediaTextureImporter :
        IProGpuExternalTextureImporter
    {
        public bool TryImportExternalTexture(
            WgpuContext targetContext,
            in ProGpuExternalTextureDescriptor descriptor,
            IDisposable nativeOwner,
            out GpuTexture texture)
        {
            var textureDescriptor = new TextureDescriptor
            {
                Usage = descriptor.Usage,
                Dimension = TextureDimension.Dimension2D,
                Size = new Extent3D
                {
                    Width = descriptor.Width,
                    Height = descriptor.Height,
                    DepthOrArrayLayers = 1
                },
                Format = descriptor.Format,
                MipLevelCount = 1,
                SampleCount = 1
            };
            Texture* nativeTexture =
                targetContext.Api.DeviceCreateTexture(
                    targetContext.Device,
                    &textureDescriptor);
            Assert.True(nativeTexture != null);
            texture = GpuTexture.WrapOwnedExternal(
                targetContext,
                nativeTexture,
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.Usage,
                "Synthetic external media frame",
                descriptor.AlphaMode,
                nativeOwner);
            return true;
        }
    }
}
