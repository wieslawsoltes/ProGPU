using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using ProGPU.Media.Audio;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using ProGPU.Media.Rendering;
using ProGPU.Vector;
using Windows.Foundation.Collections;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.Transcoding;
using Windows.Storage;
using System.Globalization;
using Color = Windows.UI.Color;
using Thickness = Microsoft.UI.Xaml.Thickness;

namespace ProGPU.Samples;

public static class NonLinearVideoEditorPage
{
    private const string SampleUri =
        "https://interactive-examples.mdn.mozilla.net/media/cc0-videos/flower.mp4";
    private const string AudioGainEffectId =
        "ProGPU.Sample.Editing.AudioGain";
    private const string VideoColorEffectId =
        "ProGPU.Sample.Editing.VideoColor";
    private static readonly Lazy<IDisposable>
        s_audioGainRegistration =
            new(
                static () =>
                    MediaEffectRegistry.Default.Register(
                        new MediaAudioGainEffectFactory(
                            AudioGainEffectId)));
    private static readonly Lazy<IDisposable>
        s_videoColorRegistration =
            new(
                static () =>
                    MediaEffectRegistry.Default.Register(
                        new MediaVideoColorEffectFactory(
                            VideoColorEffectId)));

    private sealed class InlineProgress(
        Action<double> report) :
        IProgress<double>
    {
        public void Report(double value) =>
            report(value);
    }

    private sealed class EditorRoot :
        ResponsiveSplitView,
        IAnimatedElement
    {
        public EditorSession? Session { get; set; }

        public void Update(float delta) =>
            Session?.Update(delta);
    }

    public static FrameworkElement Create()
    {
        _ = s_audioGainRegistration.Value;
        _ = s_videoColorRegistration.Value;
        var colorPreview = new Border
        {
            Height = 390f,
            HorizontalAlignment =
                HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        var playerElement = new MediaPlayerElement
        {
            Height = 390f,
            Stretch = Stretch.Uniform
        };
        var previewHost = new Grid
        {
            Height = 390f,
            HorizontalAlignment =
                HorizontalAlignment.Stretch
        };
        previewHost.AddChild(colorPreview);
        previewHost.AddChild(playerElement);
        var status = Text(
            "Add clips, select one, trim it, then play or scrub the composed timeline.");
        var timeline = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 6)
        };
        var backgroundTimeline = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 6)
        };
        var overlayTimeline = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 6)
        };
        var playhead = new Slider
        {
            Minimum = 0d,
            Maximum = 1d,
            Value = 0d,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var trimIn = new Slider
        {
            Minimum = 0d,
            Maximum = 60d,
            Value = 0d,
            Width = 280f
        };
        var trimOut = new Slider
        {
            Minimum = 0.04d,
            Maximum = 60d,
            Value = 10d,
            Width = 280f
        };
        var brightness = new Slider
        {
            Minimum = -1d,
            Maximum = 1d,
            Value = 0d,
            Width = 280f
        };
        var contrast = new Slider
        {
            Minimum = 0d,
            Maximum = 2d,
            Value = 1d,
            Width = 280f
        };
        var saturation = new Slider
        {
            Minimum = 0d,
            Maximum = 2d,
            Value = 1d,
            Width = 280f
        };
        var grayscale = new Slider
        {
            Minimum = 0d,
            Maximum = 1d,
            Value = 0d,
            Width = 280f
        };
        var sepia = new Slider
        {
            Minimum = 0d,
            Maximum = 1d,
            Value = 0d,
            Width = 280f
        };
        var invert = new Slider
        {
            Minimum = 0d,
            Maximum = 1d,
            Value = 0d,
            Width = 280f
        };
        var volume = new Slider
        {
            Minimum = 0d,
            Maximum = 1d,
            Value = 1d,
            Width = 280f
        };
        var clipAudioGain = new Slider
        {
            Minimum = 0d,
            Maximum = 2d,
            Value = 1d,
            Width = 280f
        };
        var backgroundDelay = new Slider
        {
            Minimum = -30d,
            Maximum = 120d,
            Value = 0d,
            Width = 280f
        };
        var backgroundVolume = new Slider
        {
            Minimum = 0d,
            Maximum = 1d,
            Value = 1d,
            Width = 280f
        };
        var backgroundAudioGain = new Slider
        {
            Minimum = 0d,
            Maximum = 2d,
            Value = 1d,
            Width = 280f
        };
        var overlayDelay = new Slider
        {
            Minimum = 0d,
            Maximum = 120d,
            Width = 280f
        };
        var overlayX = new Slider
        {
            Minimum = 0d,
            Maximum = 1_920d,
            Width = 280f
        };
        var overlayY = new Slider
        {
            Minimum = 0d,
            Maximum = 1_080d,
            Width = 280f
        };
        var overlayWidth = new Slider
        {
            Minimum = 1d,
            Maximum = 1_920d,
            Value = 320d,
            Width = 280f
        };
        var overlayHeight = new Slider
        {
            Minimum = 1d,
            Maximum = 1_080d,
            Value = 180d,
            Width = 280f
        };
        var overlayOpacity = new Slider
        {
            Minimum = 0d,
            Maximum = 1d,
            Value = 1d,
            Width = 280f
        };
        var uriInput = new TextBox
        {
            Text = SampleUri,
            Width = 470f
        };
        var colorInput = new TextBox
        {
            Text = "#FF7C3AED",
            Width = 150f
        };
        var colorDuration = new Slider
        {
            Minimum = 0.25d,
            Maximum = 30d,
            Value = 3d,
            Width = 180f
        };

        var composition = new MediaComposition();
        var session = new EditorSession(
            composition,
            playerElement,
            colorPreview,
            previewHost,
            timeline,
            backgroundTimeline,
            overlayTimeline,
            playhead,
            trimIn,
            trimOut,
            brightness,
            contrast,
            saturation,
            grayscale,
            sepia,
            invert,
            volume,
            clipAudioGain,
            backgroundDelay,
            backgroundVolume,
            backgroundAudioGain,
            overlayDelay,
            overlayX,
            overlayY,
            overlayWidth,
            overlayHeight,
            overlayOpacity,
            status);

        var addUri = new Button { Content = "Add URI" };
        var addFile = new Button { Content = "Import media" };
        var addColor = new Button { Content = "Add color clip" };
        var addBackgroundAudio =
            new Button { Content = "Add background audio" };
        var removeBackgroundAudio =
            new Button { Content = "Remove background audio" };
        var addOverlay =
            new Button { Content = "Overlay selected clip" };
        var removeOverlay =
            new Button { Content = "Remove overlay" };
        var play = new Button { Content = "Play timeline" };
        var pause = new Button { Content = "Pause" };
        var split = new Button { Content = "Split" };
        var remove = new Button { Content = "Remove" };
        var left = new Button { Content = "Move left" };
        var right = new Button { Content = "Move right" };
        var saveProject = new Button { Content = "Save project" };
        var loadProject = new Button { Content = "Load project" };
        var export = new Button { Content = "Export MP4" };
        var thumbnails =
            new Button { Content = "Refresh thumbnails" };

        addUri.Click += (_, _) =>
        {
            if (Uri.TryCreate(
                    uriInput.Text,
                    UriKind.Absolute,
                    out Uri? uri))
            {
                session.Add(uri, NameFromUri(uri));
            }
            else
            {
                session.SetStatus("Enter an absolute media URI.");
            }
        };
        addFile.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".mp4");
                picker.FileTypeFilter.Add(".m4v");
                picker.FileTypeFilter.Add(".mov");
                picker.FileTypeFilter.Add(".webm");
                picker.FileTypeFilter.Add(".mp3");
                picker.FileTypeFilter.Add(".m4a");
                picker.FileTypeFilter.Add(".wav");
                StorageFile? file =
                    await picker.PickSingleFileAsync();
                if (file is not null)
                {
                    session.Add(
                        new Uri(file.Path),
                        file.Name);
                }
            }
            catch (Exception exception)
            {
                session.SetStatus(
                    $"Import failed: {exception.Message}");
            }
        };
        addColor.Click += (_, _) =>
        {
            if (!TryParseArgb(
                    colorInput.Text,
                    out Color color))
            {
                session.SetStatus(
                    "Enter color as #RRGGBB or #AARRGGBB.");
                return;
            }
            session.AddColor(
                color,
                TimeSpan.FromSeconds(
                    colorDuration.Value),
                colorInput.Text);
        };
        addBackgroundAudio.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".m4a");
                picker.FileTypeFilter.Add(".mp3");
                picker.FileTypeFilter.Add(".wav");
                picker.FileTypeFilter.Add(".mp4");
                StorageFile? file =
                    await picker.PickSingleFileAsync();
                if (file is not null)
                {
                    session.AddBackgroundAudio(
                        new Uri(file.Path),
                        file.Name);
                }
            }
            catch (Exception exception)
            {
                session.SetStatus(
                    $"Background audio import failed: " +
                    exception.Message);
            }
        };
        removeBackgroundAudio.Click += (_, _) =>
            session.RemoveBackgroundAudio();
        addOverlay.Click += (_, _) =>
            session.AddOverlayFromSelectedClip();
        removeOverlay.Click += (_, _) =>
            session.RemoveOverlay();
        play.Click += (_, _) => session.Play();
        pause.Click += (_, _) => session.Pause();
        split.Click += (_, _) => session.Split();
        remove.Click += (_, _) => session.Remove();
        left.Click += (_, _) => session.Move(-1);
        right.Click += (_, _) => session.Move(1);
        saveProject.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileSavePicker
                {
                    SuggestedFileName = "ProGPU timeline.pgmedia"
                };
                picker.FileTypeChoices.Add(
                    "ProGPU media composition",
                    new[] { ".pgmedia" });
                StorageFile? file =
                    await picker.PickSaveFileAsync();
                if (file is not null)
                {
                    await composition.SaveAsync(file);
                    session.SetStatus(
                        $"Saved editable project to {file.Name}.");
                }
            }
            catch (Exception exception)
            {
                session.SetStatus(
                    $"Project save failed: {exception.Message}");
            }
        };
        loadProject.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".pgmedia");
                StorageFile? file =
                    await picker.PickSingleFileAsync();
                if (file is not null)
                {
                    await composition.LoadProjectAsync(file);
                    session.RefreshAfterProjectLoad();
                    session.SetStatus(
                        $"Loaded editable project {file.Name}.");
                }
            }
            catch (Exception exception)
            {
                session.SetStatus(
                    $"Project load failed: {exception.Message}");
            }
        };
        export.Click += async (_, _) =>
        {
            try
            {
                session.SetStatus(
                    "Choose an MP4 export destination...");
                var picker = new FileSavePicker
                {
                    SuggestedFileName = "ProGPU export.mp4"
                };
                picker.FileTypeChoices.Add(
                    "MPEG-4 video",
                    new[] { ".mp4" });
                StorageFile? file =
                    await picker.PickSaveFileAsync();
                if (file is null)
                {
                    return;
                }

                MediaEncodingProfile profile =
                    MediaEncodingProfile.CreateMp4(
                        VideoEncodingQuality.HD720p);
                string pathStatus =
                    composition.TryGetProGpuExportCapabilities(
                        file,
                        MediaTrimmingPreference.Fast,
                        profile,
                        out MediaCompositionExportCapabilities
                            capabilities)
                    ? $"{capabilities.ProviderId}: " +
                      $"{capabilities.VideoPath} video, " +
                      $"{capabilities.AudioPath} audio"
                    : "registered native media encoder";
                session.SetStatus($"Exporting with {pathStatus}...");
                IProgress<double> progress =
                    new InlineProgress(
                    value => session.SetStatus(
                        $"Exporting {value:0.0}% via {pathStatus}."));
                TranscodeFailureReason result =
                    await composition.RenderToFileAsync(
                        file,
                        MediaTrimmingPreference.Fast,
                        profile,
                        progress);
                session.SetStatus(result ==
                    TranscodeFailureReason.None
                        ? $"Exported {file.Name}."
                        : $"Export unavailable: {result}. Register a native composition encoder for this platform.");
            }
            catch (OperationCanceledException)
            {
                session.SetStatus("Export canceled.");
            }
            catch (Exception exception)
            {
                session.SetStatus(
                    $"Export failed: {exception.Message}");
            }
        };
        thumbnails.Click += async (_, _) =>
            await session.RefreshTimelineThumbnailsAsync();

        var importRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        importRow.AddChild(uriInput);
        importRow.AddChild(addUri);
        importRow.AddChild(addFile);

        var colorRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        colorRow.AddChild(Text("Color"));
        colorRow.AddChild(colorInput);
        colorRow.AddChild(Text("Duration"));
        colorRow.AddChild(colorDuration);
        colorRow.AddChild(addColor);

        var editGrid = new Grid
        {
            Margin = new Thickness(0, 4, 0, 8)
        };
        for (int index = 0; index < 7; index++)
        {
            editGrid.ColumnDefinitions.Add(
                GridLength.Auto);
        }
        for (int index = 0; index < 3; index++)
        {
            editGrid.RowDefinitions.Add(
                GridLength.Auto);
        }

        AddCommand(editGrid, play, 0, 0);
        AddCommand(editGrid, pause, 0, 1);
        AddCommand(editGrid, split, 0, 2);
        AddCommand(editGrid, remove, 0, 3);
        AddCommand(editGrid, left, 0, 4);
        AddCommand(editGrid, right, 0, 5);
        AddCommand(editGrid, addBackgroundAudio, 1, 0);
        AddCommand(editGrid, removeBackgroundAudio, 1, 1);
        AddCommand(editGrid, addOverlay, 1, 2);
        AddCommand(editGrid, removeOverlay, 1, 3);
        AddCommand(editGrid, saveProject, 2, 0);
        AddCommand(editGrid, loadProject, 2, 1);
        AddCommand(editGrid, export, 2, 2);
        AddCommand(editGrid, thumbnails, 2, 3);

        var main = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(12)
        };
        main.AddChild(Header("Non-linear video editor"));
        main.AddChild(importRow);
        main.AddChild(colorRow);
        main.AddChild(previewHost);
        main.AddChild(Text("Composed timeline"));
        main.AddChild(timeline);
        main.AddChild(Text("Background audio"));
        main.AddChild(backgroundTimeline);
        main.AddChild(Text("Overlay layers"));
        main.AddChild(overlayTimeline);
        main.AddChild(playhead);
        main.AddChild(editGrid);
        main.AddChild(status);

        var inspector = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(12)
        };
        inspector.AddChild(Header("Selected clip"));
        inspector.AddChild(Text("Trim in (seconds)"));
        inspector.AddChild(trimIn);
        inspector.AddChild(Text("Trim out (seconds)"));
        inspector.AddChild(trimOut);
        inspector.AddChild(Text("GPU brightness"));
        inspector.AddChild(brightness);
        inspector.AddChild(Text("GPU contrast"));
        inspector.AddChild(contrast);
        inspector.AddChild(Text("GPU saturation"));
        inspector.AddChild(saturation);
        inspector.AddChild(Text("GPU grayscale"));
        inspector.AddChild(grayscale);
        inspector.AddChild(Text("GPU sepia"));
        inspector.AddChild(sepia);
        inspector.AddChild(Text("GPU invert"));
        inspector.AddChild(invert);
        inspector.AddChild(Text("Clip audio volume"));
        inspector.AddChild(volume);
        inspector.AddChild(Text(
            "Clip audio effect gain (0–2×; exported through the typed native effect graph)"));
        inspector.AddChild(clipAudioGain);
        inspector.AddChild(Header("Selected background audio"));
        inspector.AddChild(Text(
            "Delay (seconds; negative advances the source)"));
        inspector.AddChild(backgroundDelay);
        inspector.AddChild(Text("Background audio volume"));
        inspector.AddChild(backgroundVolume);
        inspector.AddChild(Text(
            "Background audio effect gain (0–2×)"));
        inspector.AddChild(backgroundAudioGain);
        inspector.AddChild(Header("Selected overlay"));
        inspector.AddChild(Text("Overlay delay (seconds)"));
        inspector.AddChild(overlayDelay);
        inspector.AddChild(Text("X"));
        inspector.AddChild(overlayX);
        inspector.AddChild(Text("Y"));
        inspector.AddChild(overlayY);
        inspector.AddChild(Text("Width"));
        inspector.AddChild(overlayWidth);
        inspector.AddChild(Text("Height"));
        inspector.AddChild(overlayHeight);
        inspector.AddChild(Text("Opacity"));
        inspector.AddChild(overlayOpacity);
        inspector.AddChild(Text(
            "Edits are non-destructive. URI playback switches native decoder sources at clip boundaries. Generated colors and preview effects stay in retained GPU rendering."));

        var root = new EditorRoot
        {
            OpenPaneLength = 320f,
            PaneContent = inspector,
            MainContent = new ScrollViewer
            {
                HorizontalAlignment =
                    HorizontalAlignment.Stretch,
                VerticalAlignment =
                    VerticalAlignment.Stretch,
                Content = main
            }
        };
        root.Session = session;
        root.Unloaded += (_, _) => session.Dispose();
        session.Add(new Uri(SampleUri), "Flower sample");
        return root;
    }

    private static bool TryParseArgb(
        string? text,
        out Color color)
    {
        string value =
            (text ?? string.Empty)
                .Trim()
                .TrimStart('#');
        if (value.Length == 6)
        {
            value = "FF" + value;
        }
        if (value.Length != 8 ||
            !uint.TryParse(
                value,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out uint argb))
        {
            color = default;
            return false;
        }
        color = Color.FromArgb(
            (byte)(argb >> 24),
            (byte)(argb >> 16),
            (byte)(argb >> 8),
            (byte)argb);
        return true;
    }

    private static string NameFromUri(Uri uri)
    {
        string name = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(name)
            ? uri.Host
            : name;
    }

    private static void AddCommand(
        Grid grid,
        Button button,
        int row,
        int column)
    {
        button.Margin = new Thickness(0, 0, 6, 6);
        grid.AddChild(button);
        Grid.SetRow(button, row);
        Grid.SetColumn(button, column);
    }

    private static RichTextBlock Header(string value)
    {
        var text = Text(string.Empty);
        text.FontSize = 18f;
        text.Margin = new Thickness(0, 0, 0, 10);
        text.Inlines.Add(new Bold(new Run(value)));
        return text;
    }

    private static RichTextBlock Text(string value)
    {
        var text = new RichTextBlock
        {
            Font = AppState._font,
            FontSize = 12f,
            Margin = new Thickness(0, 3, 0, 3)
        };
        text.Inlines.Add(new Run(value));
        return text;
    }

    private sealed class EditorSession : IDisposable
    {
        private const string NameKey =
            "progpu.name";
        private const string ExplicitTrimKey =
            "progpu.explicit-trim";
        private const string SaturationKey =
            "progpu.saturation";
        private const string GrayscaleKey =
            "progpu.grayscale";
        private readonly MediaComposition _composition;
        private readonly MediaPlayerElement _playerElement;
        private readonly Border _colorPreview;
        private readonly Grid _previewHost;
        private readonly MediaPlayer _player;
        private readonly StackPanel _timeline;
        private readonly StackPanel _backgroundTimeline;
        private readonly StackPanel _overlayTimeline;
        private readonly Slider _playhead;
        private readonly Slider _trimIn;
        private readonly Slider _trimOut;
        private readonly Slider _brightness;
        private readonly Slider _contrast;
        private readonly Slider _saturation;
        private readonly Slider _grayscale;
        private readonly Slider _sepia;
        private readonly Slider _invert;
        private readonly Slider _volume;
        private readonly Slider _clipAudioGain;
        private readonly Slider _backgroundDelay;
        private readonly Slider _backgroundVolume;
        private readonly Slider _backgroundAudioGain;
        private readonly Slider _overlayDelay;
        private readonly Slider _overlayX;
        private readonly Slider _overlayY;
        private readonly Slider _overlayWidth;
        private readonly Slider _overlayHeight;
        private readonly Slider _overlayOpacity;
        private readonly RichTextBlock _status;
        private readonly IList<MediaClip> _clips;
        private readonly IList<BackgroundAudioTrack>
            _backgroundAudioTracks;
        private readonly List<BackgroundPlayback>
            _backgroundPlayback = [];
        private readonly List<OverlayPlayback>
            _overlayPlayback = [];
        private EncodedImageSource[]?
            _timelineThumbnails;
        private int _thumbnailRequestVersion;
        private int _selectedIndex = -1;
        private int _selectedBackgroundIndex = -1;
        private int _selectedOverlayIndex = -1;
        private bool _updatingControls;
        private bool _pendingPlay;
        private TimeSpan? _pendingPosition;
        private TimeSpan _colorSourcePosition;
        private long _lastPlaybackChromeUpdate =
            long.MinValue;
        private bool _disposed;

        public EditorSession(
            MediaComposition composition,
            MediaPlayerElement playerElement,
            Border colorPreview,
            Grid previewHost,
            StackPanel timeline,
            StackPanel backgroundTimeline,
            StackPanel overlayTimeline,
            Slider playhead,
            Slider trimIn,
            Slider trimOut,
            Slider brightness,
            Slider contrast,
            Slider saturation,
            Slider grayscale,
            Slider sepia,
            Slider invert,
            Slider volume,
            Slider clipAudioGain,
            Slider backgroundDelay,
            Slider backgroundVolume,
            Slider backgroundAudioGain,
            Slider overlayDelay,
            Slider overlayX,
            Slider overlayY,
            Slider overlayWidth,
            Slider overlayHeight,
            Slider overlayOpacity,
            RichTextBlock status)
        {
            _composition = composition;
            _clips = composition.Clips;
            _backgroundAudioTracks =
                composition.BackgroundAudioTracks;
            _playerElement = playerElement;
            _colorPreview = colorPreview;
            _previewHost = previewHost;
            _player = playerElement.MediaPlayer;
            _timeline = timeline;
            _backgroundTimeline = backgroundTimeline;
            _overlayTimeline = overlayTimeline;
            _playhead = playhead;
            _trimIn = trimIn;
            _trimOut = trimOut;
            _brightness = brightness;
            _contrast = contrast;
            _saturation = saturation;
            _grayscale = grayscale;
            _sepia = sepia;
            _invert = invert;
            _volume = volume;
            _clipAudioGain = clipAudioGain;
            _backgroundDelay = backgroundDelay;
            _backgroundVolume = backgroundVolume;
            _backgroundAudioGain =
                backgroundAudioGain;
            _overlayDelay = overlayDelay;
            _overlayX = overlayX;
            _overlayY = overlayY;
            _overlayWidth = overlayWidth;
            _overlayHeight = overlayHeight;
            _overlayOpacity = overlayOpacity;
            _status = status;

            _player.MediaOpened += OnMediaOpened;
            _player.MediaEnded += OnMediaEnded;
            _player.MediaFailed += OnMediaFailed;
            _player.PlaybackSession.PositionChanged +=
                OnPositionChanged;
            _playhead.ValueChanged += OnPlayheadChanged;
            _trimIn.ValueChanged += OnTrimChanged;
            _trimOut.ValueChanged += OnTrimChanged;
            _brightness.ValueChanged += OnEffectChanged;
            _contrast.ValueChanged += OnEffectChanged;
            _saturation.ValueChanged += OnEffectChanged;
            _grayscale.ValueChanged += OnEffectChanged;
            _sepia.ValueChanged += OnEffectChanged;
            _invert.ValueChanged += OnEffectChanged;
            _volume.ValueChanged += OnVolumeChanged;
            _clipAudioGain.ValueChanged +=
                OnClipAudioGainChanged;
            _backgroundDelay.ValueChanged +=
                OnBackgroundSettingsChanged;
            _backgroundVolume.ValueChanged +=
                OnBackgroundSettingsChanged;
            _backgroundAudioGain.ValueChanged +=
                OnBackgroundAudioGainChanged;
            _overlayDelay.ValueChanged +=
                OnOverlaySettingsChanged;
            _overlayX.ValueChanged +=
                OnOverlaySettingsChanged;
            _overlayY.ValueChanged +=
                OnOverlaySettingsChanged;
            _overlayWidth.ValueChanged +=
                OnOverlaySettingsChanged;
            _overlayHeight.ValueChanged +=
                OnOverlaySettingsChanged;
            _overlayOpacity.ValueChanged +=
                OnOverlaySettingsChanged;
        }

        public void Add(Uri source, string name)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            MediaClip clip = MediaClip.CreateFromUri(
                source,
                TimeSpan.FromSeconds(10));
            clip.UserData[NameKey] = name;
            _clips.Add(clip);
            Select(_clips.Count - 1, TimeSpan.Zero, false);
            RebuildTimeline();
            SetStatus($"Added {name}.");
        }

        public void AddColor(
            Color color,
            TimeSpan duration,
            string name)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            MediaClip clip =
                MediaClip.CreateFromColor(
                    color,
                    duration);
            clip.UserData[NameKey] =
                string.IsNullOrWhiteSpace(name)
                    ? "Color clip"
                    : name;
            _clips.Add(clip);
            Select(_clips.Count - 1, TimeSpan.Zero, false);
            RebuildTimeline();
            SetStatus(
                $"Added color clip {NameOf(clip)}.");
        }

        public void AddBackgroundAudio(
            Uri source,
            string name)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            BackgroundAudioTrack track =
                BackgroundAudioTrack.CreateFromUri(
                    source,
                    TimeSpan.FromSeconds(10));
            track.UserData[NameKey] = name;
            _backgroundAudioTracks.Add(track);
            _backgroundPlayback.Add(
                new BackgroundPlayback(this, track));
            SelectBackground(
                _backgroundAudioTracks.Count - 1);
            RebuildBackgroundTimeline();
            SetStatus($"Added background audio {name}.");
        }

        public void RemoveBackgroundAudio()
        {
            if (_selectedBackgroundIndex < 0 ||
                _selectedBackgroundIndex >=
                    _backgroundAudioTracks.Count)
            {
                return;
            }
            int index = _selectedBackgroundIndex;
            _backgroundPlayback[index].Dispose();
            _backgroundPlayback.RemoveAt(index);
            _backgroundAudioTracks.RemoveAt(index);
            _selectedBackgroundIndex =
                _backgroundAudioTracks.Count == 0
                    ? -1
                    : Math.Min(
                        index,
                        _backgroundAudioTracks.Count - 1);
            RebuildBackgroundTimeline();
            UpdateBackgroundInspector();
            UpdateDuration();
        }

        public void AddOverlayFromSelectedClip()
        {
            MediaClip? selected = Selected;
            if (selected is null)
            {
                SetStatus(
                    "Select a clip before adding an overlay.");
                return;
            }
            MediaOverlayLayer layer;
            if (_composition.OverlayLayers.Count == 0)
            {
                layer = new MediaOverlayLayer();
                _composition.OverlayLayers.Add(layer);
            }
            else
            {
                layer = _composition.OverlayLayers[^1];
            }
            var overlay = new MediaOverlay(
                selected.Clone(),
                new Windows.Foundation.Rect(
                    40d, 40d, 320d, 180d),
                0.9d)
            {
                AudioEnabled = false
            };
            layer.Overlays.Add(overlay);
            RebuildOverlayPlayers();
            _selectedOverlayIndex =
                _overlayPlayback.Count - 1;
            RebuildOverlayTimeline();
            UpdateOverlayInspector();
            SyncOverlays(
                TimeSpan.FromSeconds(
                    _playhead.Value),
                _pendingPlay,
                forceSeek: true);
            SetStatus(
                $"Added {NameOf(selected)} as an overlay.");
        }

        public void RemoveOverlay()
        {
            if (_selectedOverlayIndex < 0 ||
                _selectedOverlayIndex >=
                    _overlayPlayback.Count)
            {
                return;
            }
            OverlayPlayback selected =
                _overlayPlayback[_selectedOverlayIndex];
            selected.Layer.Overlays.Remove(
                selected.Overlay);
            if (selected.Layer.Overlays.Count == 0)
            {
                _composition.OverlayLayers.Remove(
                    selected.Layer);
            }
            RebuildOverlayPlayers();
            _selectedOverlayIndex =
                _overlayPlayback.Count == 0
                    ? -1
                    : Math.Min(
                        _selectedOverlayIndex,
                        _overlayPlayback.Count - 1);
            RebuildOverlayTimeline();
            UpdateOverlayInspector();
        }

        public void Play()
        {
            if (_clips.Count == 0)
            {
                SetStatus("Add at least one clip.");
                return;
            }
            if (_selectedIndex < 0)
            {
                Select(0, TimeSpan.Zero, true);
            }
            else if (Selected?.ProGpuColor is not null)
            {
                MediaClip clip = Selected;
                if (_colorSourcePosition >= EndOf(clip))
                {
                    _colorSourcePosition =
                        StartOf(clip);
                }
                _pendingPlay = true;
                TimeSpan timelinePosition =
                    TimelinePositionOf(
                        _selectedIndex,
                        _colorSourcePosition);
                SyncBackgroundAudio(
                    timelinePosition,
                    play: true,
                    forceSeek: true);
                SyncOverlays(
                    timelinePosition,
                    play: true,
                    forceSeek: true);
                SetStatus(
                    $"Playing color clip " +
                    $"{_selectedIndex + 1}/{_clips.Count}.");
            }
            else
            {
                _pendingPlay = true;
                _player.Play();
                SyncBackgroundAudio(
                    TimeSpan.FromSeconds(
                        _playhead.Value),
                    play: true,
                    forceSeek: true);
                SyncOverlays(
                    TimeSpan.FromSeconds(
                        _playhead.Value),
                    play: true,
                    forceSeek: true);
            }
        }

        public void Pause()
        {
            _pendingPlay = false;
            _player.Pause();
            PauseBackgroundAudio();
            PauseOverlays();
        }

        public void Split()
        {
            MediaClip? clip = Selected;
            if (clip is null)
            {
                return;
            }
            TimeSpan splitAt =
                clip.ProGpuColor is not null
                    ? _colorSourcePosition
                    : _player.PlaybackSession.Position;
            if (splitAt <= StartOf(clip) +
                    TimeSpan.FromMilliseconds(40) ||
                splitAt >= EndOf(clip) -
                    TimeSpan.FromMilliseconds(40))
            {
                SetStatus(
                    "Move the playhead inside the selected clip before splitting.");
                return;
            }

            TimeSpan originalTrimFromEnd =
                clip.TrimTimeFromEnd;
            string originalName = NameOf(clip);
            clip.TrimTimeFromEnd =
                clip.OriginalDuration - splitAt;
            SetExplicitTrim(clip);
            MediaClip second = clip.Clone();
            second.TrimTimeFromStart = splitAt;
            second.TrimTimeFromEnd =
                originalTrimFromEnd;
            second.UserData[NameKey] =
                $"{originalName} B";
            SetExplicitTrim(second);
            _clips.Insert(
                _selectedIndex + 1,
                second);
            clip.UserData[NameKey] =
                $"{originalName} A";
            RebuildTimeline();
            UpdateInspector();
            SetStatus("Clip split at the current frame.");
        }

        public void Remove()
        {
            if (_selectedIndex < 0)
            {
                return;
            }
            _clips.RemoveAt(_selectedIndex);
            if (_clips.Count == 0)
            {
                _selectedIndex = -1;
                _player.Source = null;
                _playerElement.Visibility =
                    Visibility.Visible;
                _colorPreview.Visibility =
                    Visibility.Collapsed;
            }
            else
            {
                Select(
                    Math.Min(_selectedIndex, _clips.Count - 1),
                    TimeSpan.Zero,
                    false);
            }
            RebuildTimeline();
            UpdateDuration();
        }

        public void Move(int delta)
        {
            if (_selectedIndex < 0)
            {
                return;
            }
            int target = Math.Clamp(
                _selectedIndex + delta,
                0,
                _clips.Count - 1);
            if (target == _selectedIndex)
            {
                return;
            }
            MediaClip clip = _clips[_selectedIndex];
            _clips.RemoveAt(_selectedIndex);
            _clips.Insert(target, clip);
            _selectedIndex = target;
            RebuildTimeline();
            UpdateDuration();
        }

        public void SetStatus(string value)
        {
            if (_status.Inlines.Count == 1 &&
                _status.Inlines[0] is Run run)
            {
                run.Text = value;
                return;
            }

            _status.Inlines.Clear();
            _status.Inlines.Add(new Run(value));
        }

        public void RefreshAfterProjectLoad()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pendingPlay = false;
            _player.Pause();
            PauseBackgroundAudio();
            PauseOverlays();
            RebuildBackgroundPlayers();
            RebuildOverlayPlayers();
            _selectedIndex = -1;
            if (_clips.Count == 0)
            {
                _player.Source = null;
                RebuildTimeline();
                RebuildBackgroundTimeline();
                RebuildOverlayTimeline();
                UpdateInspector();
                return;
            }

            Select(0, TimeSpan.Zero, false);
            RebuildTimeline();
            RebuildBackgroundTimeline();
            RebuildOverlayTimeline();
            UpdateInspector();
            UpdateBackgroundInspector();
            UpdateOverlayInspector();
        }

        private MediaClip? Selected =>
            _selectedIndex >= 0 &&
            _selectedIndex < _clips.Count
                ? _clips[_selectedIndex]
                : null;

        private void Select(
            int index,
            TimeSpan localPosition,
            bool play)
        {
            if (index < 0 || index >= _clips.Count)
            {
                return;
            }
            _selectedIndex = index;
            MediaClip clip = _clips[index];
            TimeSpan sourcePosition = StartOf(clip) +
                TimeSpan.FromTicks(Math.Clamp(
                    localPosition.Ticks,
                    0,
                    clip.TrimmedDuration.Ticks));
            _pendingPlay = play;
            if (clip.ProGpuColor is Color)
            {
                _pendingPosition = null;
                _player.Pause();
                _player.Source = null;
                _playerElement.Visibility =
                    Visibility.Collapsed;
                _colorPreview.Visibility =
                    Visibility.Visible;
                _colorSourcePosition =
                    sourcePosition;
                ApplyEffects(clip);
                UpdateTimelinePosition(
                    sourcePosition,
                    forceChrome: true);
                UpdateInspector();
                RebuildTimeline();
                return;
            }

            _colorPreview.Visibility =
                Visibility.Collapsed;
            _playerElement.Visibility =
                Visibility.Visible;
            _pendingPosition = sourcePosition;
            Uri source = clip.ProGpuSourceUri ??
                throw new NotSupportedException(
                    "The sample preview currently requires a provider-backed URI clip.");
            _player.Source =
                MediaSource.CreateFromUri(source);
            _player.Volume = clip.Volume;
            ApplyEffects(clip);
            UpdateInspector();
            RebuildTimeline();
        }

        private void ApplyEffects(MediaClip clip)
        {
            ApplyAudioEffects(
                _player,
                clip.AudioEffectDefinitions);
            if (clip.ProGpuColor is Color)
            {
                ApplyColorPreview(clip);
                return;
            }
            _playerElement.ProGpuVideoEffects =
                new MediaVideoEffectOptions(
                    brightness: BrightnessOf(clip),
                    contrast: ContrastOf(clip),
                    saturation: SaturationOf(clip),
                    grayscale: GrayscaleOf(clip),
                    sepia: SepiaOf(clip),
                    invert: InvertOf(clip));
        }

        private void ApplyColorPreview(MediaClip clip)
        {
            if (clip.ProGpuColor is not Color color)
            {
                return;
            }
            _colorPreview.Background =
                CreateColorBrush(clip, color);
        }

        private static SolidColorBrush CreateColorBrush(
            MediaClip clip,
            Color color)
        {
            System.Numerics.Vector3 transformed =
                MediaVideoColorEffectFactory
                    .CreateTransform(
                        brightness:
                            BrightnessOf(clip),
                        contrast:
                            ContrastOf(clip),
                        saturation:
                            SaturationOf(clip),
                        grayscale:
                            GrayscaleOf(clip),
                        sepia:
                            SepiaOf(clip),
                        invert:
                            InvertOf(clip))
                    .Transform(
                        new System.Numerics.Vector3(
                            color.R / 255f,
                            color.G / 255f,
                            color.B / 255f));
            return new SolidColorBrush(
                new System.Numerics.Vector4(
                    Math.Clamp(
                        transformed.X,
                        0f,
                        1f),
                    Math.Clamp(
                        transformed.Y,
                        0f,
                        1f),
                    Math.Clamp(
                        transformed.Z,
                        0f,
                        1f),
                    color.A / 255f));
        }

        private void RebuildTimeline()
        {
            _timeline.Children.Clear();
            for (int index = 0; index < _clips.Count; index++)
            {
                MediaClip clip = _clips[index];
                float width = Math.Clamp(
                    90f +
                        (float)clip.TrimmedDuration.TotalSeconds *
                        8f,
                    110f,
                    260f);
                EncodedImageSource[]? thumbnails =
                    _timelineThumbnails;
                bool hasThumbnail =
                    thumbnails is not null &&
                    thumbnails.Length == _clips.Count;
                var content = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Width = width - 16f
                };
                if (hasThumbnail)
                {
                    content.AddChild(
                        new Image
                        {
                            Source = thumbnails![index],
                            Height = 42f,
                            Width = width - 20f,
                            Stretch = Stretch.UniformToFill
                        });
                }
                content.AddChild(
                    Text(
                        $"{index + 1}. {NameOf(clip)}\n" +
                        $"{clip.TrimmedDuration:mm\\:ss\\.fff}"));
                var button = new Button
                {
                    Content = content,
                    Width = width,
                    Height = hasThumbnail ? 94f : 58f
                };
                int captured = index;
                button.Click += (_, _) =>
                    Select(captured, TimeSpan.Zero, false);
                _timeline.AddChild(button);
            }
            UpdateDuration();
        }

        public async Task RefreshTimelineThumbnailsAsync()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clips.Count == 0)
            {
                _timelineThumbnails = null;
                RebuildTimeline();
                SetStatus(
                    "Add a clip before generating thumbnails.");
                return;
            }

            int requestVersion =
                ++_thumbnailRequestVersion;
            var positions =
                new TimeSpan[_clips.Count];
            TimeSpan timelinePosition = TimeSpan.Zero;
            for (int index = 0;
                 index < _clips.Count;
                 index++)
            {
                TimeSpan duration =
                    _clips[index].TrimmedDuration;
                positions[index] =
                    timelinePosition +
                    TimeSpan.FromTicks(
                        duration.Ticks / 2);
                timelinePosition += duration;
            }

            try
            {
                SetStatus(
                    $"Generating {_clips.Count} timeline thumbnails with the native batch provider...");
                IReadOnlyList<ImageStream> streams =
                    await _composition.GetThumbnailsAsync(
                        positions,
                        scaledWidth: 160,
                        scaledHeight: 90,
                        VideoFramePrecision.NearestFrame);
                var sources =
                    new EncodedImageSource[streams.Count];
                try
                {
                    for (int index = 0;
                         index < streams.Count;
                         index++)
                    {
                        using var buffer =
                            new MemoryStream();
                        streams[index]
                            .AsStream()
                            .CopyTo(buffer);
                        sources[index] =
                            new EncodedImageSource(
                                buffer.ToArray(),
                                suggestedWidth: 160,
                                suggestedHeight: 90);
                    }
                }
                finally
                {
                    for (int index = 0;
                         index < streams.Count;
                         index++)
                    {
                        streams[index].Dispose();
                    }
                }

                if (_disposed ||
                    requestVersion !=
                        _thumbnailRequestVersion ||
                    sources.Length != _clips.Count)
                {
                    return;
                }
                _timelineThumbnails = sources;
                RebuildTimeline();
                SetStatus(
                    $"Generated {sources.Length} native timeline thumbnails in one batch.");
            }
            catch (Exception exception)
            {
                if (requestVersion !=
                    _thumbnailRequestVersion)
                {
                    return;
                }
                SetStatus(
                    $"Thumbnail generation unavailable: {exception.Message}");
            }
        }

        private void RebuildBackgroundTimeline()
        {
            _backgroundTimeline.Children.Clear();
            for (int index = 0;
                 index < _backgroundAudioTracks.Count;
                 index++)
            {
                BackgroundAudioTrack track =
                    _backgroundAudioTracks[index];
                var button = new Button
                {
                    Content =
                        $"{index + 1}. {NameOf(track)}\n" +
                        $"delay {track.Delay.TotalSeconds:0.###}s  " +
                        $"{track.TrimmedDuration:mm\\:ss\\.fff}",
                    Width = 210f,
                    Height = 58f
                };
                int captured = index;
                button.Click += (_, _) =>
                    SelectBackground(captured);
                _backgroundTimeline.AddChild(button);
            }
        }

        private void SelectBackground(int index)
        {
            if (index < 0 ||
                index >= _backgroundAudioTracks.Count)
            {
                return;
            }
            _selectedBackgroundIndex = index;
            UpdateBackgroundInspector();
            RebuildBackgroundTimeline();
        }

        private void UpdateBackgroundInspector()
        {
            _updatingControls = true;
            if (_selectedBackgroundIndex >= 0 &&
                _selectedBackgroundIndex <
                    _backgroundAudioTracks.Count)
            {
                BackgroundAudioTrack track =
                    _backgroundAudioTracks[
                        _selectedBackgroundIndex];
                _backgroundDelay.Value = Math.Clamp(
                    track.Delay.TotalSeconds,
                    _backgroundDelay.Minimum,
                    _backgroundDelay.Maximum);
                _backgroundVolume.Value =
                    Math.Clamp(track.Volume, 0d, 1d);
                _backgroundAudioGain.Value =
                    AudioGainOf(
                        track.AudioEffectDefinitions);
            }
            else
            {
                _backgroundDelay.Value = 0d;
                _backgroundVolume.Value = 1d;
                _backgroundAudioGain.Value = 1d;
            }
            _updatingControls = false;
        }

        private void RebuildOverlayTimeline()
        {
            _overlayTimeline.Children.Clear();
            for (int index = 0;
                 index < _overlayPlayback.Count;
                 index++)
            {
                OverlayPlayback playback =
                    _overlayPlayback[index];
                MediaOverlay overlay = playback.Overlay;
                var button = new Button
                {
                    Content =
                        $"{index + 1}. " +
                        $"{NameOf(overlay.Clip)}\n" +
                        $"{overlay.Delay.TotalSeconds:0.###}s  " +
                        $"{overlay.Position.Width:0}×" +
                        $"{overlay.Position.Height:0}",
                    Width = 190f,
                    Height = 58f
                };
                int captured = index;
                button.Click += (_, _) =>
                    SelectOverlay(captured);
                _overlayTimeline.AddChild(button);
            }
        }

        private void SelectOverlay(int index)
        {
            if (index < 0 ||
                index >= _overlayPlayback.Count)
            {
                return;
            }
            _selectedOverlayIndex = index;
            UpdateOverlayInspector();
            RebuildOverlayTimeline();
        }

        private void UpdateOverlayInspector()
        {
            _updatingControls = true;
            if (_selectedOverlayIndex >= 0 &&
                _selectedOverlayIndex <
                    _overlayPlayback.Count)
            {
                MediaOverlay overlay =
                    _overlayPlayback[
                        _selectedOverlayIndex].Overlay;
                _overlayDelay.Value = Math.Clamp(
                    overlay.Delay.TotalSeconds,
                    _overlayDelay.Minimum,
                    _overlayDelay.Maximum);
                _overlayX.Value = Math.Clamp(
                    overlay.Position.X,
                    _overlayX.Minimum,
                    _overlayX.Maximum);
                _overlayY.Value = Math.Clamp(
                    overlay.Position.Y,
                    _overlayY.Minimum,
                    _overlayY.Maximum);
                _overlayWidth.Value = Math.Clamp(
                    overlay.Position.Width,
                    _overlayWidth.Minimum,
                    _overlayWidth.Maximum);
                _overlayHeight.Value = Math.Clamp(
                    overlay.Position.Height,
                    _overlayHeight.Minimum,
                    _overlayHeight.Maximum);
                _overlayOpacity.Value =
                    overlay.Opacity;
            }
            else
            {
                _overlayDelay.Value = 0d;
                _overlayX.Value = 0d;
                _overlayY.Value = 0d;
                _overlayWidth.Value = 320d;
                _overlayHeight.Value = 180d;
                _overlayOpacity.Value = 1d;
            }
            _updatingControls = false;
        }

        private void UpdateInspector()
        {
            MediaClip? clip = Selected;
            _updatingControls = true;
            if (clip is not null)
            {
                double maximum = Math.Max(
                    0.04d,
                    clip.OriginalDuration.TotalSeconds);
                _trimIn.Maximum = maximum;
                _trimOut.Maximum = maximum;
                _trimIn.Value =
                    StartOf(clip).TotalSeconds;
                _trimOut.Value =
                    EndOf(clip).TotalSeconds;
                _brightness.Value =
                    BrightnessOf(clip);
                _contrast.Value =
                    ContrastOf(clip);
                _saturation.Value =
                    SaturationOf(clip);
                _grayscale.Value =
                    GrayscaleOf(clip);
                _sepia.Value =
                    SepiaOf(clip);
                _invert.Value =
                    InvertOf(clip);
                _volume.Value =
                    clip.Volume;
                _clipAudioGain.Value =
                    AudioGainOf(
                        clip.AudioEffectDefinitions);
            }
            _updatingControls = false;
        }

        private void UpdateDuration()
        {
            _updatingControls = true;
            _playhead.Maximum = Math.Max(
                1d,
                _composition.Duration.TotalSeconds);
            _updatingControls = false;
        }

        private void OnMediaOpened(
            MediaPlayer sender,
            object args)
        {
            MediaClip? clip = Selected;
            if (clip is null ||
                clip.ProGpuColor is not null)
            {
                return;
            }
            TimeSpan natural =
                _player.PlaybackSession.NaturalDuration;
            if (natural > TimeSpan.Zero)
            {
                TimeSpan previousStart =
                    StartOf(clip);
                TimeSpan previousEnd =
                    EndOf(clip);
                bool explicitTrim =
                    HasExplicitTrim(clip);
                clip.SetProGpuOriginalDuration(
                    natural);
                if (explicitTrim)
                {
                    TimeSpan start = TimeSpan.FromTicks(
                        Math.Clamp(
                            previousStart.Ticks,
                            0,
                            natural.Ticks));
                    TimeSpan end = TimeSpan.FromTicks(
                        Math.Clamp(
                            previousEnd.Ticks,
                            start.Ticks,
                            natural.Ticks));
                    clip.TrimTimeFromEnd =
                        TimeSpan.Zero;
                    clip.TrimTimeFromStart =
                        start;
                    clip.TrimTimeFromEnd =
                        natural - end;
                }
            }
            _player.PlaybackSession.Position =
                _pendingPosition ?? StartOf(clip);
            _pendingPosition = null;
            UpdateInspector();
            RebuildTimeline();
            if (_pendingPlay)
            {
                SyncBackgroundAudio(
                    TimeSpan.FromSeconds(
                        _playhead.Value),
                    play: true,
                    forceSeek: true);
                SyncOverlays(
                    TimeSpan.FromSeconds(
                        _playhead.Value),
                    play: true,
                    forceSeek: true);
                _player.Play();
            }
        }

        private void OnMediaEnded(
            MediaPlayer sender,
            object args)
        {
            if (Selected?.ProGpuColor is null)
            {
                Advance();
            }
        }

        private void OnMediaFailed(
            MediaPlayer sender,
            MediaPlayerFailedEventArgs args)
        {
            if (Selected?.ProGpuColor is not null)
            {
                return;
            }
            SetStatus(
                $"Clip failed: {args.ErrorMessage}");
        }

        private void OnPositionChanged(
            MediaPlaybackSession sender,
            object args)
        {
            MediaClip? clip = Selected;
            if (clip is null ||
                clip.ProGpuColor is not null)
            {
                return;
            }
            TimeSpan sourcePosition = sender.Position;
            if (_pendingPlay &&
                sourcePosition >= EndOf(clip))
            {
                Advance();
                return;
            }
            UpdateTimelinePosition(sourcePosition);
        }

        public void Update(float delta)
        {
            if (_disposed ||
                !_pendingPlay ||
                Selected is not
                    { ProGpuColor: not null } clip)
            {
                return;
            }
            double seconds =
                double.IsFinite(delta) && delta > 0f
                    ? delta
                    : 0d;
            if (seconds == 0d)
            {
                return;
            }
            _colorSourcePosition +=
                TimeSpan.FromSeconds(seconds);
            if (_colorSourcePosition >= EndOf(clip))
            {
                _colorSourcePosition = EndOf(clip);
                UpdateTimelinePosition(
                    _colorSourcePosition,
                    forceChrome: true);
                Advance();
                return;
            }
            UpdateTimelinePosition(
                _colorSourcePosition);
        }

        private void UpdateTimelinePosition(
            TimeSpan sourcePosition,
            bool forceChrome = false)
        {
            MediaClip? clip = Selected;
            if (clip is null)
            {
                return;
            }
            TimeSpan before = TimeSpan.Zero;
            for (int index = 0;
                 index < _selectedIndex;
                 index++)
            {
                before +=
                    _clips[index].TrimmedDuration;
            }
            TimeSpan local =
                sourcePosition - StartOf(clip);
            TimeSpan timelinePosition =
                before + local;
            SyncBackgroundAudio(
                timelinePosition,
                _pendingPlay,
                forceSeek: false);
            SyncOverlays(
                timelinePosition,
                _pendingPlay,
                forceSeek: false);

            long now = Environment.TickCount64;
            if (!forceChrome &&
                _lastPlaybackChromeUpdate != long.MinValue &&
                now - _lastPlaybackChromeUpdate < 100)
            {
                return;
            }
            _lastPlaybackChromeUpdate = now;
            double timelineSeconds = Math.Clamp(
                timelinePosition.TotalSeconds,
                0d,
                _playhead.Maximum);
            _updatingControls = true;
            _playhead.Value = timelineSeconds;
            _updatingControls = false;
            SetStatus(
                $"Clip {_selectedIndex + 1}/{_clips.Count}  " +
                $"source {sourcePosition:mm\\:ss\\.fff}  " +
                $"timeline {timelineSeconds:0.000}s");
        }

        private void Advance()
        {
            if (_selectedIndex + 1 < _clips.Count)
            {
                Select(
                    _selectedIndex + 1,
                    TimeSpan.Zero,
                    true);
            }
            else
            {
                _pendingPlay = false;
                _player.Pause();
                PauseBackgroundAudio();
                PauseOverlays();
                SetStatus("Timeline complete.");
            }
        }

        private void OnPlayheadChanged(
            object? sender,
            EventArgs args)
        {
            if (_updatingControls || _clips.Count == 0)
            {
                return;
            }
            TimeSpan remaining =
                TimeSpan.FromSeconds(_playhead.Value);
            for (int index = 0; index < _clips.Count; index++)
            {
                MediaClip clip = _clips[index];
                if (remaining <=
                        clip.TrimmedDuration ||
                    index == _clips.Count - 1)
                {
                    if (index != _selectedIndex)
                    {
                        Select(index, remaining, _pendingPlay);
                    }
                    else if (clip.ProGpuColor is not null)
                    {
                        _colorSourcePosition =
                            StartOf(clip) + remaining;
                        ApplyColorPreview(clip);
                    }
                    else
                    {
                        _player.PlaybackSession.Position =
                            StartOf(clip) + remaining;
                    }
                    SyncBackgroundAudio(
                        TimeSpan.FromSeconds(
                            _playhead.Value),
                        _pendingPlay,
                        forceSeek: true);
                    SyncOverlays(
                        TimeSpan.FromSeconds(
                            _playhead.Value),
                        _pendingPlay,
                        forceSeek: true);
                    return;
                }
                remaining -=
                    clip.TrimmedDuration;
            }
        }

        private void OnTrimChanged(
            object? sender,
            EventArgs args)
        {
            MediaClip? clip = Selected;
            if (_updatingControls || clip is null)
            {
                return;
            }
            double minimumDuration = 0.04d;
            double trimIn = Math.Min(
                _trimIn.Value,
                _trimOut.Value - minimumDuration);
            double trimOut = Math.Max(
                _trimOut.Value,
                trimIn + minimumDuration);
            TimeSpan start = TimeSpan.FromSeconds(
                Math.Clamp(
                    trimIn,
                    0d,
                    clip.OriginalDuration.TotalSeconds));
            TimeSpan end = TimeSpan.FromSeconds(
                Math.Clamp(
                    trimOut,
                    start.TotalSeconds,
                    clip.OriginalDuration.TotalSeconds));
            clip.TrimTimeFromEnd = TimeSpan.Zero;
            clip.TrimTimeFromStart = start;
            clip.TrimTimeFromEnd =
                clip.OriginalDuration - end;
            SetExplicitTrim(clip);
            if (clip.ProGpuColor is not null)
            {
                _colorSourcePosition =
                    StartOf(clip);
                UpdateTimelinePosition(
                    _colorSourcePosition,
                    forceChrome: true);
            }
            else
            {
                _player.PlaybackSession.Position =
                    StartOf(clip);
            }
            RebuildTimeline();
        }

        private void OnEffectChanged(
            object? sender,
            EventArgs args)
        {
            MediaClip? clip = Selected;
            if (_updatingControls || clip is null)
            {
                return;
            }
            SetVideoColorEffect(
                clip.VideoEffectDefinitions,
                (float)_brightness.Value,
                (float)_contrast.Value,
                (float)_saturation.Value,
                (float)_grayscale.Value,
                (float)_sepia.Value,
                (float)_invert.Value);
            clip.UserData.Remove(SaturationKey);
            clip.UserData.Remove(GrayscaleKey);
            ApplyEffects(clip);
        }

        private void OnVolumeChanged(
            object? sender,
            EventArgs args)
        {
            MediaClip? clip = Selected;
            if (_updatingControls || clip is null)
            {
                return;
            }
            clip.Volume = _volume.Value;
            _player.Volume = clip.Volume;
        }

        private void OnClipAudioGainChanged(
            object? sender,
            EventArgs args)
        {
            MediaClip? clip = Selected;
            if (_updatingControls || clip is null)
            {
                return;
            }
            SetAudioGain(
                clip.AudioEffectDefinitions,
                _clipAudioGain.Value);
            ApplyAudioEffects(
                _player,
                clip.AudioEffectDefinitions);
            SetStatus(
                $"Clip audio gain set to " +
                $"{_clipAudioGain.Value:0.00}×.");
        }

        private void OnBackgroundSettingsChanged(
            object? sender,
            EventArgs args)
        {
            if (_updatingControls ||
                _selectedBackgroundIndex < 0 ||
                _selectedBackgroundIndex >=
                    _backgroundAudioTracks.Count)
            {
                return;
            }
            BackgroundAudioTrack track =
                _backgroundAudioTracks[
                    _selectedBackgroundIndex];
            track.Delay = TimeSpan.FromSeconds(
                _backgroundDelay.Value);
            track.Volume = _backgroundVolume.Value;
            _backgroundPlayback[
                _selectedBackgroundIndex]
                .Player.Volume = track.Volume;
            RebuildBackgroundTimeline();
            UpdateDuration();
            SyncBackgroundAudio(
                TimeSpan.FromSeconds(_playhead.Value),
                _pendingPlay,
                forceSeek: true);
        }

        private void OnBackgroundAudioGainChanged(
            object? sender,
            EventArgs args)
        {
            if (_updatingControls ||
                _selectedBackgroundIndex < 0 ||
                _selectedBackgroundIndex >=
                    _backgroundAudioTracks.Count)
            {
                return;
            }
            BackgroundAudioTrack track =
                _backgroundAudioTracks[
                    _selectedBackgroundIndex];
            SetAudioGain(
                track.AudioEffectDefinitions,
                _backgroundAudioGain.Value);
            ApplyAudioEffects(
                _backgroundPlayback[
                    _selectedBackgroundIndex].Player,
                track.AudioEffectDefinitions);
            SetStatus(
                $"Background audio gain set to " +
                $"{_backgroundAudioGain.Value:0.00}×.");
        }

        private void OnOverlaySettingsChanged(
            object? sender,
            EventArgs args)
        {
            if (_updatingControls ||
                _selectedOverlayIndex < 0 ||
                _selectedOverlayIndex >=
                    _overlayPlayback.Count)
            {
                return;
            }
            OverlayPlayback playback =
                _overlayPlayback[_selectedOverlayIndex];
            MediaOverlay overlay = playback.Overlay;
            overlay.Delay = TimeSpan.FromSeconds(
                _overlayDelay.Value);
            overlay.Position =
                new Windows.Foundation.Rect(
                    _overlayX.Value,
                    _overlayY.Value,
                    _overlayWidth.Value,
                    _overlayHeight.Value);
            overlay.Opacity = _overlayOpacity.Value;
            playback.ApplyLayout();
            RebuildOverlayTimeline();
            SyncOverlays(
                TimeSpan.FromSeconds(_playhead.Value),
                _pendingPlay,
                forceSeek: true);
        }

        private void SyncBackgroundAudio(
            TimeSpan timelinePosition,
            bool play,
            bool forceSeek)
        {
            for (int index = 0;
                 index < _backgroundPlayback.Count;
                 index++)
            {
                _backgroundPlayback[index].Sync(
                    timelinePosition,
                    play,
                    forceSeek);
            }
        }

        private void PauseBackgroundAudio()
        {
            for (int index = 0;
                 index < _backgroundPlayback.Count;
                 index++)
            {
                _backgroundPlayback[index].Pause();
            }
        }

        private void SyncOverlays(
            TimeSpan timelinePosition,
            bool play,
            bool forceSeek)
        {
            for (int index = 0;
                 index < _overlayPlayback.Count;
                 index++)
            {
                _overlayPlayback[index].Sync(
                    timelinePosition,
                    play,
                    forceSeek);
            }
        }

        private void PauseOverlays()
        {
            for (int index = 0;
                 index < _overlayPlayback.Count;
                 index++)
            {
                _overlayPlayback[index].Pause();
            }
        }

        private void RebuildBackgroundPlayers()
        {
            for (int index = 0;
                 index < _backgroundPlayback.Count;
                 index++)
            {
                _backgroundPlayback[index].Dispose();
            }
            _backgroundPlayback.Clear();
            for (int index = 0;
                 index < _backgroundAudioTracks.Count;
                 index++)
            {
                _backgroundPlayback.Add(
                    new BackgroundPlayback(
                        this,
                        _backgroundAudioTracks[index]));
            }
            _selectedBackgroundIndex =
                _backgroundAudioTracks.Count == 0
                    ? -1
                    : 0;
        }

        private void RebuildOverlayPlayers()
        {
            for (int index = 0;
                 index < _overlayPlayback.Count;
                 index++)
            {
                OverlayPlayback playback =
                    _overlayPlayback[index];
                _previewHost.Children.Remove(
                    playback.Element);
                playback.Dispose();
            }
            _overlayPlayback.Clear();
            for (int layerIndex = 0;
                 layerIndex <
                    _composition.OverlayLayers.Count;
                 layerIndex++)
            {
                MediaOverlayLayer layer =
                    _composition.OverlayLayers[layerIndex];
                for (int overlayIndex = 0;
                     overlayIndex < layer.Overlays.Count;
                     overlayIndex++)
                {
                    var playback = new OverlayPlayback(
                        this,
                        layer,
                        layer.Overlays[overlayIndex]);
                    _overlayPlayback.Add(playback);
                    _previewHost.AddChild(playback.Element);
                }
            }
            _selectedOverlayIndex =
                _overlayPlayback.Count == 0
                    ? -1
                    : Math.Clamp(
                        _selectedOverlayIndex,
                        0,
                        _overlayPlayback.Count - 1);
        }

        private void OnBackgroundDurationChanged()
        {
            RebuildBackgroundTimeline();
            UpdateDuration();
            UpdateBackgroundInspector();
        }

        private static string NameOf(MediaClip clip) =>
            clip.UserData.TryGetValue(
                NameKey,
                out string? name)
                ? name
                : "Clip";

        private static string NameOf(
            BackgroundAudioTrack track) =>
            track.UserData.TryGetValue(
                NameKey,
                out string? name)
                ? name
                : "Background audio";

        private static TimeSpan StartOf(
            MediaClip clip) =>
            clip.TrimTimeFromStart;

        private static TimeSpan EndOf(
            MediaClip clip) =>
            clip.OriginalDuration -
            clip.TrimTimeFromEnd;

        private TimeSpan TimelinePositionOf(
            int clipIndex,
            TimeSpan sourcePosition)
        {
            TimeSpan result = TimeSpan.Zero;
            for (int index = 0;
                 index < clipIndex;
                 index++)
            {
                result +=
                    _clips[index].TrimmedDuration;
            }
            return result +
                sourcePosition -
                StartOf(_clips[clipIndex]);
        }

        private static bool HasExplicitTrim(
            MediaClip clip) =>
            clip.UserData.TryGetValue(
                ExplicitTrimKey,
                out string? value) &&
            string.Equals(
                value,
                bool.TrueString,
                StringComparison.Ordinal);

        private static void SetExplicitTrim(
            MediaClip clip) =>
            clip.UserData[ExplicitTrimKey] =
                bool.TrueString;

        private static float SaturationOf(
            MediaClip clip) =>
            ReadVideoColorProperty(
                clip,
                MediaVideoColorEffectFactory
                    .SaturationPropertyName,
                SaturationKey,
                1f,
                0f,
                2f);

        private static float GrayscaleOf(
            MediaClip clip) =>
            ReadVideoColorProperty(
                clip,
                MediaVideoColorEffectFactory
                    .GrayscalePropertyName,
                GrayscaleKey,
                0f,
                0f,
                1f);

        private static float BrightnessOf(
            MediaClip clip) =>
            ReadVideoColorProperty(
                clip,
                MediaVideoColorEffectFactory
                    .BrightnessPropertyName,
                legacyKey: string.Empty,
                fallback: 0f,
                minimum: -1f,
                maximum: 1f);

        private static float ContrastOf(
            MediaClip clip) =>
            ReadVideoColorProperty(
                clip,
                MediaVideoColorEffectFactory
                    .ContrastPropertyName,
                legacyKey: string.Empty,
                fallback: 1f,
                minimum: 0f,
                maximum: 2f);

        private static float SepiaOf(
            MediaClip clip) =>
            ReadVideoColorProperty(
                clip,
                MediaVideoColorEffectFactory
                    .SepiaPropertyName,
                legacyKey: string.Empty,
                fallback: 0f,
                minimum: 0f,
                maximum: 1f);

        private static float InvertOf(
            MediaClip clip) =>
            ReadVideoColorProperty(
                clip,
                MediaVideoColorEffectFactory
                    .InvertPropertyName,
                legacyKey: string.Empty,
                fallback: 0f,
                minimum: 0f,
                maximum: 1f);

        private static float ReadVideoColorProperty(
            MediaClip clip,
            string propertyName,
            string legacyKey,
            float fallback,
            float minimum,
            float maximum)
        {
            for (int index = 0;
                 index <
                    clip.VideoEffectDefinitions.Count;
                 index++)
            {
                IVideoEffectDefinition effect =
                    clip.VideoEffectDefinitions[index];
                if (!string.Equals(
                        effect.ActivatableClassId,
                        VideoColorEffectId,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (!effect.Properties.TryGetValue(
                        propertyName,
                        out object? property))
                {
                    return fallback;
                }
                float value = property switch
                {
                    float number => number,
                    double number => (float)number,
                    decimal number => (float)number,
                    int number => number,
                    long number => number,
                    _ => fallback
                };
                return float.IsFinite(value)
                    ? Math.Clamp(
                        value,
                        minimum,
                        maximum)
                    : fallback;
            }

            return !string.IsNullOrEmpty(legacyKey) &&
            clip.UserData.TryGetValue(
                legacyKey,
                out string? text) &&
            float.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float parsedValue)
                ? Math.Clamp(
                    parsedValue,
                    minimum,
                    maximum)
                : fallback;
        }

        private static void SetVideoColorEffect(
            IList<IVideoEffectDefinition> effects,
            float brightness,
            float contrast,
            float saturation,
            float grayscale,
            float sepia,
            float invert)
        {
            int existingIndex = -1;
            for (int index = 0;
                 index < effects.Count;
                 index++)
            {
                if (string.Equals(
                        effects[index].ActivatableClassId,
                        VideoColorEffectId,
                        StringComparison.Ordinal))
                {
                    existingIndex = index;
                    break;
                }
            }

            if (brightness == 0f &&
                contrast == 1f &&
                saturation == 1f &&
                grayscale == 0f &&
                sepia == 0f &&
                invert == 0f)
            {
                if (existingIndex >= 0)
                {
                    effects.RemoveAt(existingIndex);
                }
                return;
            }

            IVideoEffectDefinition definition;
            if (existingIndex >= 0)
            {
                definition = effects[existingIndex];
            }
            else
            {
                definition =
                    new VideoEffectDefinition(
                        VideoColorEffectId,
                        new PropertySet());
                effects.Add(definition);
            }
            definition.Properties[
                MediaVideoColorEffectFactory
                    .BrightnessPropertyName] =
                brightness;
            definition.Properties[
                MediaVideoColorEffectFactory
                    .ContrastPropertyName] =
                contrast;
            definition.Properties[
                MediaVideoColorEffectFactory
                    .SaturationPropertyName] =
                saturation;
            definition.Properties[
                MediaVideoColorEffectFactory
                    .GrayscalePropertyName] =
                grayscale;
            definition.Properties[
                MediaVideoColorEffectFactory
                    .SepiaPropertyName] =
                sepia;
            definition.Properties[
                MediaVideoColorEffectFactory
                    .InvertPropertyName] =
                invert;
        }

        private static double AudioGainOf(
            IList<IAudioEffectDefinition> effects)
        {
            for (int index = 0;
                 index < effects.Count;
                 index++)
            {
                IAudioEffectDefinition effect =
                    effects[index];
                if (!string.Equals(
                        effect.ActivatableClassId,
                        AudioGainEffectId,
                        StringComparison.Ordinal) ||
                    !effect.Properties.TryGetValue(
                        MediaAudioGainEffectFactory
                            .GainPropertyName,
                        out object? value))
                {
                    continue;
                }
                double gain = value switch
                {
                    float number => number,
                    double number => number,
                    decimal number => (double)number,
                    int number => number,
                    long number => number,
                    _ => 1d
                };
                return double.IsFinite(gain)
                    ? Math.Clamp(gain, 0d, 2d)
                    : 1d;
            }
            return 1d;
        }

        private static void SetAudioGain(
            IList<IAudioEffectDefinition> effects,
            double gain)
        {
            int existingIndex = -1;
            for (int index = 0;
                 index < effects.Count;
                 index++)
            {
                if (string.Equals(
                        effects[index].ActivatableClassId,
                        AudioGainEffectId,
                        StringComparison.Ordinal))
                {
                    existingIndex = index;
                    break;
                }
            }

            if (Math.Abs(gain - 1d) < 0.000_001d)
            {
                if (existingIndex >= 0)
                {
                    effects.RemoveAt(existingIndex);
                }
                return;
            }

            IAudioEffectDefinition definition;
            if (existingIndex >= 0)
            {
                definition = effects[existingIndex];
            }
            else
            {
                definition = new AudioEffectDefinition(
                    AudioGainEffectId,
                    new PropertySet());
                effects.Add(definition);
            }
            definition.Properties[
                MediaAudioGainEffectFactory
                    .GainPropertyName] = gain;
        }

        private static void ApplyAudioEffects(
            MediaPlayer player,
            IList<IAudioEffectDefinition> effects)
        {
            player.RemoveAllEffects();
            for (int index = 0;
                 index < effects.Count;
                 index++)
            {
                IAudioEffectDefinition effect =
                    effects[index];
                player.AddAudioEffect(
                    effect.ActivatableClassId,
                    effectOptional: true,
                    effect.Properties);
            }
        }

        private sealed class BackgroundPlayback : IDisposable
        {
            private static readonly TimeSpan DriftTolerance =
                TimeSpan.FromMilliseconds(200);
            private readonly EditorSession _owner;
            private bool _playing;
            private bool _disposed;

            public BackgroundPlayback(
                EditorSession owner,
                BackgroundAudioTrack track)
            {
                _owner = owner;
                Track = track;
                Player = new MediaPlayer
                {
                    AudioCategory =
                        MediaPlayerAudioCategory.Media,
                    RealTimePlayback = true,
                    Volume = track.Volume
                };
                Player.MediaOpened += OnMediaOpened;
                Player.MediaEnded += OnMediaEnded;
                Player.MediaFailed += OnMediaFailed;
                ApplyAudioEffects(
                    Player,
                    track.AudioEffectDefinitions);
                Player.Source = MediaSource.CreateFromUri(
                    track.ProGpuSourceUri);
            }

            public BackgroundAudioTrack Track { get; }
            public MediaPlayer Player { get; }

            public void Sync(
                TimeSpan timelinePosition,
                bool play,
                bool forceSeek)
            {
                if (_disposed)
                {
                    return;
                }
                TimeSpan sourcePosition =
                    Track.TrimTimeFromStart +
                    timelinePosition -
                    Track.Delay;
                TimeSpan sourceEnd =
                    Track.OriginalDuration -
                    Track.TrimTimeFromEnd;
                bool active =
                    sourcePosition >=
                        Track.TrimTimeFromStart &&
                    sourcePosition < sourceEnd;
                if (!active)
                {
                    Pause();
                    return;
                }

                Player.Volume = Track.Volume;
                TimeSpan drift =
                    Player.PlaybackSession.Position -
                    sourcePosition;
                if (forceSeek ||
                    !_playing ||
                    drift.Duration() > DriftTolerance)
                {
                    Player.PlaybackSession.Position =
                        sourcePosition;
                }
                if (play)
                {
                    if (!_playing)
                    {
                        Player.Play();
                        _playing = true;
                    }
                }
                else
                {
                    Pause();
                }
            }

            public void Pause()
            {
                if (_disposed)
                {
                    return;
                }
                Player.Pause();
                _playing = false;
            }

            private void OnMediaOpened(
                MediaPlayer sender,
                object args)
            {
                TimeSpan duration =
                    sender.PlaybackSession.NaturalDuration;
                if (duration > TimeSpan.Zero)
                {
                    Track.SetProGpuOriginalDuration(
                        duration);
                    _owner.OnBackgroundDurationChanged();
                }
                Sync(
                    TimeSpan.FromSeconds(
                        _owner._playhead.Value),
                    _owner._pendingPlay,
                    forceSeek: true);
            }

            private void OnMediaEnded(
                MediaPlayer sender,
                object args)
            {
                _playing = false;
            }

            private void OnMediaFailed(
                MediaPlayer sender,
                MediaPlayerFailedEventArgs args)
            {
                _playing = false;
                _owner.SetStatus(
                    $"Background audio failed: " +
                    args.ErrorMessage);
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                Player.MediaOpened -= OnMediaOpened;
                Player.MediaEnded -= OnMediaEnded;
                Player.MediaFailed -= OnMediaFailed;
                Player.Dispose();
            }
        }

        private sealed class OverlayPlayback : IDisposable
        {
            private static readonly TimeSpan DriftTolerance =
                TimeSpan.FromMilliseconds(200);
            private readonly EditorSession _owner;
            private bool _playing;
            private bool _disposed;

            public OverlayPlayback(
                EditorSession owner,
                MediaOverlayLayer layer,
                MediaOverlay overlay)
            {
                _owner = owner;
                Layer = layer;
                Overlay = overlay;
                if (overlay.Clip.ProGpuSourceUri is
                    { } source)
                {
                    var playerElement =
                        new MediaPlayerElement
                        {
                            Stretch =
                                Stretch.UniformToFill,
                            HorizontalAlignment =
                                HorizontalAlignment.Left,
                            VerticalAlignment =
                                VerticalAlignment.Top
                        };
                    Element = playerElement;
                    Player = playerElement.MediaPlayer;
                    Player.RealTimePlayback = true;
                    Player.IsMuted =
                        !overlay.AudioEnabled;
                    Player.Volume =
                        overlay.Clip.Volume;
                    playerElement.ProGpuVideoEffects =
                        new MediaVideoEffectOptions(
                            brightness:
                                BrightnessOf(overlay.Clip),
                            contrast:
                                ContrastOf(overlay.Clip),
                            saturation:
                                SaturationOf(overlay.Clip),
                            grayscale:
                                GrayscaleOf(overlay.Clip),
                            sepia:
                                SepiaOf(overlay.Clip),
                            invert:
                                InvertOf(overlay.Clip));
                    Player.MediaOpened += OnMediaOpened;
                    Player.MediaEnded += OnMediaEnded;
                    Player.MediaFailed += OnMediaFailed;
                    ApplyAudioEffects(
                        Player,
                        overlay.Clip
                            .AudioEffectDefinitions);
                    Player.Source =
                        MediaSource.CreateFromUri(source);
                }
                else if (overlay.Clip.ProGpuColor is
                    Color color)
                {
                    Element = new Border
                    {
                        Background =
                            CreateColorBrush(
                                overlay.Clip,
                                color),
                        HorizontalAlignment =
                            HorizontalAlignment.Left,
                        VerticalAlignment =
                            VerticalAlignment.Top
                    };
                }
                else
                {
                    throw new InvalidOperationException(
                        "An overlay requires a URI or color source.");
                }
                ApplyLayout();
                Element.Visibility =
                    Visibility.Collapsed;
            }

            public MediaOverlayLayer Layer { get; }
            public MediaOverlay Overlay { get; }
            public FrameworkElement Element { get; }
            public MediaPlayer? Player { get; }

            public void ApplyLayout()
            {
                Windows.Foundation.Rect position =
                    Overlay.Position;
                Element.Width = (float)position.Width;
                Element.Height = (float)position.Height;
                Element.Margin = new Thickness(
                    (float)position.X,
                    (float)position.Y,
                    0f,
                    0f);
                Element.Opacity = Overlay.Opacity;
                if (Player is not null)
                {
                    Player.IsMuted =
                        !Overlay.AudioEnabled;
                }
            }

            public void Sync(
                TimeSpan timelinePosition,
                bool play,
                bool forceSeek)
            {
                if (_disposed)
                {
                    return;
                }
                MediaClip clip = Overlay.Clip;
                TimeSpan sourcePosition =
                    clip.TrimTimeFromStart +
                    timelinePosition -
                    Overlay.Delay;
                TimeSpan sourceEnd =
                    clip.OriginalDuration -
                    clip.TrimTimeFromEnd;
                bool active =
                    sourcePosition >=
                        clip.TrimTimeFromStart &&
                    sourcePosition < sourceEnd;
                Element.Visibility = active
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (!active)
                {
                    Pause();
                    return;
                }
                if (Player is null)
                {
                    return;
                }

                TimeSpan drift =
                    Player.PlaybackSession.Position -
                    sourcePosition;
                if (forceSeek ||
                    !_playing ||
                    drift.Duration() > DriftTolerance)
                {
                    Player.PlaybackSession.Position =
                        sourcePosition;
                }
                if (play)
                {
                    if (!_playing)
                    {
                        Player.Play();
                        _playing = true;
                    }
                }
                else
                {
                    Pause();
                }
            }

            public void Pause()
            {
                if (_disposed)
                {
                    return;
                }
                Player?.Pause();
                _playing = false;
            }

            private void OnMediaOpened(
                MediaPlayer sender,
                object args)
            {
                TimeSpan duration =
                    sender.PlaybackSession.NaturalDuration;
                if (duration > TimeSpan.Zero)
                {
                    Overlay.Clip.SetProGpuOriginalDuration(
                        duration);
                    _owner.RebuildOverlayTimeline();
                    _owner.UpdateOverlayInspector();
                }
                Sync(
                    TimeSpan.FromSeconds(
                        _owner._playhead.Value),
                    _owner._pendingPlay,
                    forceSeek: true);
            }

            private void OnMediaEnded(
                MediaPlayer sender,
                object args)
            {
                _playing = false;
                Element.Visibility =
                    Visibility.Collapsed;
            }

            private void OnMediaFailed(
                MediaPlayer sender,
                MediaPlayerFailedEventArgs args)
            {
                _playing = false;
                Element.Visibility =
                    Visibility.Collapsed;
                _owner.SetStatus(
                    $"Overlay failed: {args.ErrorMessage}");
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                if (Player is not null)
                {
                    Player.MediaOpened -= OnMediaOpened;
                    Player.MediaEnded -= OnMediaEnded;
                    Player.MediaFailed -= OnMediaFailed;
                    Player.Dispose();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _thumbnailRequestVersion++;
            _player.MediaOpened -= OnMediaOpened;
            _player.MediaEnded -= OnMediaEnded;
            _player.MediaFailed -= OnMediaFailed;
            _player.PlaybackSession.PositionChanged -=
                OnPositionChanged;
            _playhead.ValueChanged -= OnPlayheadChanged;
            _trimIn.ValueChanged -= OnTrimChanged;
            _trimOut.ValueChanged -= OnTrimChanged;
            _brightness.ValueChanged -= OnEffectChanged;
            _contrast.ValueChanged -= OnEffectChanged;
            _saturation.ValueChanged -= OnEffectChanged;
            _grayscale.ValueChanged -= OnEffectChanged;
            _sepia.ValueChanged -= OnEffectChanged;
            _invert.ValueChanged -= OnEffectChanged;
            _volume.ValueChanged -= OnVolumeChanged;
            _clipAudioGain.ValueChanged -=
                OnClipAudioGainChanged;
            _backgroundDelay.ValueChanged -=
                OnBackgroundSettingsChanged;
            _backgroundVolume.ValueChanged -=
                OnBackgroundSettingsChanged;
            _backgroundAudioGain.ValueChanged -=
                OnBackgroundAudioGainChanged;
            _overlayDelay.ValueChanged -=
                OnOverlaySettingsChanged;
            _overlayX.ValueChanged -=
                OnOverlaySettingsChanged;
            _overlayY.ValueChanged -=
                OnOverlaySettingsChanged;
            _overlayWidth.ValueChanged -=
                OnOverlaySettingsChanged;
            _overlayHeight.ValueChanged -=
                OnOverlaySettingsChanged;
            _overlayOpacity.ValueChanged -=
                OnOverlaySettingsChanged;
            for (int index = 0;
                 index < _backgroundPlayback.Count;
                 index++)
            {
                _backgroundPlayback[index].Dispose();
            }
            _backgroundPlayback.Clear();
            for (int index = 0;
                 index < _overlayPlayback.Count;
                 index++)
            {
                _overlayPlayback[index].Dispose();
            }
            _overlayPlayback.Clear();
            _player.Dispose();
        }
    }
}
