using System.Numerics;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Media3D;
using ProGPU.Media;
using ProGPU.Media.Audio;
using ProGPU.Media.Effects;
using ProGPU.Media.Rendering;
using ProGPU.Scene.Extensions;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media;
using Windows.Foundation.Collections;
using Thickness = Microsoft.UI.Xaml.Thickness;

namespace ProGPU.Samples;

public static class MediaPlayerPage
{
    private static MediaPlayer? s_benchmarkPlayer;
    private static long s_benchmarkMaximumPositionTicks;
    private const string AudioGainEffectId =
        "ProGPU.Samples.AudioGain";
    private const string AudioBalanceEffectId =
        "ProGPU.Samples.AudioBalance";
    private const string BenchmarkMediaUriVariable =
        "PROGPU_SAMPLE_BENCHMARK_MEDIA_URI";
    private const string DefaultMediaUri =
        "https://interactive-examples.mdn.mozilla.net/media/cc0-videos/flower.mp4";

    public static FrameworkElement Create()
    {
        var playerElement = new MediaPlayerElement
        {
            Height = 430f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Stretch = Stretch.Uniform,
            AutoPlay = false,
            AreTransportControlsEnabled = true
        };
        MediaPlayer player = playerElement.MediaPlayer;
        MediaTransportControls transport =
            playerElement.TransportControls!;
        transport.ShowAndHideAutomatically = false;
        transport.IsSkipBackwardButtonVisible = true;
        transport.IsSkipBackwardEnabled = true;
        transport.IsSkipForwardButtonVisible = true;
        transport.IsSkipForwardEnabled = true;
        transport.IsPlaybackRateButtonVisible = true;
        transport.IsPlaybackRateEnabled = true;
        transport.IsRepeatButtonVisible = true;
        transport.IsRepeatEnabled = true;
        var mediaMaterial =
            new ProGpuMediaTextureMaterial
            {
                MediaPlayer = player,
                Brush = new ProGPU.Vector.ThemeResourceBrush(
                    "TextControlForeground")
            };
        var videoMesh = new MeshGeometry3D
        {
            Positions =
            [
                new Vector3(-1.7f, -0.95f, 0f),
                new Vector3(1.7f, -0.95f, 0f),
                new Vector3(1.7f, 0.95f, 0f),
                new Vector3(-1.7f, 0.95f, 0f)
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
        var mediaViewport = new Viewport3D
        {
            Height = 430f,
            HorizontalAlignment =
                HorizontalAlignment.Stretch,
            VerticalAlignment =
                VerticalAlignment.Stretch,
            Camera = new PerspectiveCamera
            {
                Position = new Vector3(0f, 0f, -5f),
                LookDirection = Vector3.UnitZ,
                FieldOfView = 42f
            },
            ShadingMode = ShadingMode3D.Flat,
            Visibility = Visibility.Collapsed
        };
        mediaViewport.Children.Add(
            new ModelVisual3D
            {
                Content = new GeometryModel3D
                {
                    Geometry = videoMesh,
                    Material = mediaMaterial,
                    BackMaterial = mediaMaterial,
                    Transform =
                        Matrix4x4.CreateRotationY(-0.28f)
                }
            });
        var videoHost = new Grid
        {
            Height = 430f,
            HorizontalAlignment =
                HorizontalAlignment.Stretch,
            VerticalAlignment =
                VerticalAlignment.Stretch
        };
        videoHost.AddChild(playerElement);
        videoHost.AddChild(mediaViewport);

        var status = Text(
            "Choose a local file or load the sample URI.");
        var position = new Slider
        {
            Minimum = 0d,
            Maximum = 1d,
            Value = 0d,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        bool updatingPosition = false;
        long lastPositionChromeUpdate = long.MinValue;

        var uri = new TextBox
        {
            Text = DefaultMediaUri,
            Width = 520f
        };
        var load = new Button { Content = "Load URI" };
        var open = new Button { Content = "Open file" };
        var play = new Button { Content = "Play" };
        var pause = new Button { Content = "Pause" };
        var previousFrame = new Button { Content = "◀ Frame" };
        var nextFrame = new Button { Content = "Frame ▶" };
        var mute = new ToggleSwitch { Content = "Mute" };
        var loop = new ToggleSwitch { Content = "Loop" };
        var mirror = new ToggleSwitch { Content = "Mirror" };
        var spherical =
            new ToggleSwitch { Content = "360°" };
        var use3D = new ToggleSwitch { Content = "3D mesh" };

        var brightness = EffectSlider(-1d, 1d, 0d);
        var contrast = EffectSlider(0d, 2d, 1d);
        var saturation = EffectSlider(0d, 2d, 1d);
        var grayscale = EffectSlider(0d, 1d, 0d);
        var sepia = EffectSlider(0d, 1d, 0d);
        var blur = EffectSlider(0d, 8d, 0d);
        var sphericalYaw =
            EffectSlider(-180d, 180d, 0d);
        var sphericalFieldOfView =
            EffectSlider(30d, 150d, 90d);
        sphericalYaw.IsEnabled = false;
        sphericalFieldOfView.IsEnabled = false;
        var audioGain = EffectSlider(0d, 2d, 1d);
        var audioBalance = EffectSlider(-1d, 1d, 0d);
        string audioGainEffectId =
            $"{AudioGainEffectId}.{Guid.NewGuid():N}";
        string audioBalanceEffectId =
            $"{AudioBalanceEffectId}.{Guid.NewGuid():N}";
        var audioGainFactory =
            new MediaAudioGainEffectFactory(
                audioGainEffectId);
        var audioBalanceFactory =
            new MediaAudioStereoBalanceEffectFactory(
                audioBalanceEffectId);
        IDisposable audioGainRegistration =
            MediaEffectRegistry.Default.Register(
                audioGainFactory);
        IDisposable audioBalanceRegistration =
            MediaEffectRegistry.Default.Register(
                audioBalanceFactory);
        player.AddAudioEffect(
            audioGainEffectId,
            effectOptional: true,
            new PropertySet());
        player.AddAudioEffect(
            audioBalanceEffectId,
            effectOptional: true,
            new PropertySet());

        void ApplyEffects()
        {
            playerElement.ProGpuVideoEffects =
                new MediaVideoEffectOptions(
                    brightness: (float)brightness.Value,
                    contrast: (float)contrast.Value,
                    saturation: (float)saturation.Value,
                    grayscale: (float)grayscale.Value,
                    sepia: (float)sepia.Value,
                    blurSigma: (float)blur.Value);
            mediaMaterial.Effects =
                playerElement.ProGpuVideoEffects;
        }

        brightness.ValueChanged += (_, _) => ApplyEffects();
        contrast.ValueChanged += (_, _) => ApplyEffects();
        saturation.ValueChanged += (_, _) => ApplyEffects();
        grayscale.ValueChanged += (_, _) => ApplyEffects();
        sepia.ValueChanged += (_, _) => ApplyEffects();
        blur.ValueChanged += (_, _) => ApplyEffects();
        void ApplySphericalProjection()
        {
            MediaPlaybackSphericalVideoProjection
                projection =
                    player.PlaybackSession
                        .SphericalVideoProjection;
            projection.FrameFormat =
                Windows.Media.MediaProperties
                    .SphericalVideoFrameFormat
                    .Equirectangular;
            projection.ProjectionMode =
                SphericalVideoProjectionMode.Spherical;
            projection.HorizontalFieldOfViewInDegrees =
                sphericalFieldOfView.Value;
            projection.ViewOrientation =
                Quaternion.CreateFromAxisAngle(
                    Vector3.UnitY,
                    (float)sphericalYaw.Value *
                    (MathF.PI / 180f));
            projection.IsEnabled = spherical.IsOn;
        }
        spherical.Toggled +=
            (_, _) =>
            {
                sphericalYaw.IsEnabled =
                    spherical.IsOn;
                sphericalFieldOfView.IsEnabled =
                    spherical.IsOn;
                ApplySphericalProjection();
            };
        sphericalYaw.ValueChanged +=
            (_, _) => ApplySphericalProjection();
        sphericalFieldOfView.ValueChanged +=
            (_, _) => ApplySphericalProjection();
        audioGain.ValueChanged += (_, _) =>
            audioGainFactory.Gain =
                (float)audioGain.Value;
        audioBalance.ValueChanged += (_, _) =>
            audioBalanceFactory.Balance =
                (float)audioBalance.Value;

        void LoadSource(Uri source)
        {
            try
            {
                SetText(status, $"Opening {source} …");
                var item = new MediaPlaybackItem(
                    MediaSource.CreateFromUri(source));
                MediaItemDisplayProperties display =
                    item.GetDisplayProperties();
                display.Type = MediaPlaybackType.Video;
                display.VideoProperties.Title =
                    source.IsFile
                        ? Path.GetFileName(source.LocalPath)
                        : source.Host;
                display.VideoProperties.Subtitle =
                    "ProGPU WebGPU media";
                item.ApplyDisplayProperties(display);
                player.Source = item;
                player.Play();
            }
            catch (Exception exception)
            {
                SetText(status, $"Open failed: {exception.Message}");
            }
        }

        load.Click += (_, _) =>
        {
            if (Uri.TryCreate(
                    uri.Text,
                    UriKind.Absolute,
                    out Uri? source))
            {
                LoadSource(source);
            }
            else
            {
                SetText(status, "Enter an absolute file or network URI.");
            }
        };
        open.Click += async (_, _) =>
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
                    uri.Text = new Uri(file.Path).AbsoluteUri;
                    LoadSource(new Uri(file.Path));
                }
            }
            catch (Exception exception)
            {
                SetText(status, $"File picker failed: {exception.Message}");
            }
        };
        play.Click += (_, _) => player.Play();
        pause.Click += (_, _) => player.Pause();
        previousFrame.Click +=
            (_, _) => player.StepBackwardOneFrame();
        nextFrame.Click +=
            (_, _) => player.StepForwardOneFrame();
        mute.Toggled += (_, _) => player.IsMuted = mute.IsOn;
        loop.Toggled +=
            (_, _) =>
            {
                try
                {
                    player.IsLoopingEnabled = loop.IsOn;
                    UpdateStatus(status, player);
                }
                catch (Exception exception)
                {
                    SetText(
                        status,
                        $"Loop change failed: {exception.Message}");
                }
            };
        mirror.Toggled += (_, _) =>
            player.PlaybackSession.IsMirroring = mirror.IsOn;
        use3D.Toggled += (_, _) =>
        {
            playerElement.Visibility = use3D.IsOn
                ? Visibility.Collapsed
                : Visibility.Visible;
            mediaViewport.Visibility = use3D.IsOn
                ? Visibility.Visible
                : Visibility.Collapsed;
        };

        position.ValueChanged += (_, _) =>
        {
            if (!updatingPosition &&
                player.PlaybackSession.CanSeek)
            {
                player.PlaybackSession.Position =
                    TimeSpan.FromSeconds(position.Value);
            }
        };
        player.PlaybackSession.NaturalDurationChanged +=
            (_, _) =>
            {
                updatingPosition = true;
                position.Maximum = Math.Max(
                    1d,
                    player.PlaybackSession
                        .NaturalDuration.TotalSeconds);
                updatingPosition = false;
            };
        player.PlaybackSession.PositionChanged +=
            (_, _) =>
            {
                if (ReferenceEquals(s_benchmarkPlayer, player))
                {
                    RecordBenchmarkPosition(
                        player.PlaybackSession.Position);
                }
                long now = Environment.TickCount64;
                if (lastPositionChromeUpdate != long.MinValue &&
                    now - lastPositionChromeUpdate < 100)
                {
                    return;
                }
                lastPositionChromeUpdate = now;
                updatingPosition = true;
                position.Value = Math.Clamp(
                    player.PlaybackSession.Position.TotalSeconds,
                    position.Minimum,
                    position.Maximum);
                updatingPosition = false;
                UpdateStatus(status, player);
            };
        player.PlaybackSession.PlaybackStateChanged +=
            (_, _) => UpdateStatus(status, player);
        player.MediaOpened +=
            (_, _) => UpdateStatus(status, player);
        player.MediaFailed += (_, args) =>
            SetText(
                status,
                $"Playback failed: {args.Error} — {args.ErrorMessage}");
        if (TryGetBenchmarkMediaUri(out Uri? benchmarkMediaUri) &&
            benchmarkMediaUri is not null)
        {
            s_benchmarkPlayer = player;
            Interlocked.Exchange(
                ref s_benchmarkMaximumPositionTicks,
                0);
            uri.Text = benchmarkMediaUri.AbsoluteUri;
            loop.IsOn = true;
            LoadSource(benchmarkMediaUri);
        }

        var sourceRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        sourceRow.AddChild(uri);
        sourceRow.AddChild(load);
        sourceRow.AddChild(open);

        var transportRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 8)
        };
        transportRow.AddChild(play);
        transportRow.AddChild(pause);
        transportRow.AddChild(previousFrame);
        transportRow.AddChild(nextFrame);
        transportRow.AddChild(mute);
        transportRow.AddChild(loop);
        transportRow.AddChild(mirror);
        transportRow.AddChild(use3D);

        var preview = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(12)
        };
        preview.AddChild(Header("WinUI MediaPlayer + WebGPU"));
        preview.AddChild(sourceRow);
        preview.AddChild(videoHost);
        preview.AddChild(position);
        preview.AddChild(transportRow);
        preview.AddChild(status);

        var effects = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(12)
        };
        effects.AddChild(Header("GPU post processing"));
        effects.AddChild(Text(
            "The preview samples decoded GPU textures directly. Color effects stay fused; Gaussian blur uses a retained two-axis WebGPU graph."));
        AddEffect(effects, "Brightness", brightness);
        AddEffect(effects, "Contrast", contrast);
        AddEffect(effects, "Saturation", saturation);
        AddEffect(effects, "Grayscale", grayscale);
        AddEffect(effects, "Sepia", sepia);
        AddEffect(effects, "Blur", blur);
        effects.AddChild(Text(
            "WinUI spherical-video projection"));
        effects.AddChild(spherical);
        AddEffect(effects, "360° yaw", sphericalYaw);
        AddEffect(
            effects,
            "360° field of view",
            sphericalFieldOfView);
        effects.AddChild(Text(
            "Native decoded-audio callback (optional by provider)"));
        AddEffect(effects, "Audio gain", audioGain);
        AddEffect(
            effects,
            "Audio balance effect",
            audioBalance);

        var root = new ResponsiveSplitView
        {
            OpenPaneLength = 320f,
            PaneContent = effects,
            MainContent = preview
        };
        root.Unloaded += (_, _) =>
        {
            if (ReferenceEquals(s_benchmarkPlayer, player))
            {
                s_benchmarkPlayer = null;
            }
            mediaMaterial.Dispose();
            player.Dispose();
            audioBalanceRegistration.Dispose();
            audioGainRegistration.Dispose();
        };
        return root;
    }

    internal static bool TryGetBenchmarkMediaUri(
        out Uri? source)
    {
        string? value = Environment.GetEnvironmentVariable(
            BenchmarkMediaUriVariable);
        return Uri.TryCreate(
            value,
            UriKind.Absolute,
            out source);
    }

    internal static bool TryGetBenchmarkPlaybackState(
        out TimeSpan position,
        out TimeSpan maximumPosition,
        out string playbackState,
        out string provider,
        out bool hardwareDecoded,
        out string transferMode)
    {
        MediaPlayer? player = s_benchmarkPlayer;
        if (player is null)
        {
            position = default;
            maximumPosition = default;
            playbackState = "Unavailable";
            provider = "none";
            hardwareDecoded = false;
            transferMode = "unavailable";
            return false;
        }

        position = player.PlaybackSession.Position;
        maximumPosition = TimeSpan.FromTicks(
            Interlocked.Read(
                ref s_benchmarkMaximumPositionTicks));
        playbackState =
            player.PlaybackSession.PlaybackState.ToString();
        var diagnostics = player.GetProGpuDiagnostics();
        provider = diagnostics.ProviderId ?? "none";
        hardwareDecoded = diagnostics.HardwareDecoded;
        transferMode =
            diagnostics.TransferMode?.ToString() ??
            "unavailable";
        return maximumPosition > TimeSpan.Zero &&
            !string.Equals(
                provider,
                "none",
                StringComparison.Ordinal);
    }

    private static void RecordBenchmarkPosition(
        TimeSpan position)
    {
        long candidate = position.Ticks;
        long observed = Interlocked.Read(
            ref s_benchmarkMaximumPositionTicks);
        while (candidate > observed)
        {
            long previous = Interlocked.CompareExchange(
                ref s_benchmarkMaximumPositionTicks,
                candidate,
                observed);
            if (previous == observed)
            {
                return;
            }
            observed = previous;
        }
    }

    private static Slider EffectSlider(
        double minimum,
        double maximum,
        double value) =>
        new()
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            Width = 280f
        };

    private static void AddEffect(
        StackPanel panel,
        string label,
        Slider slider)
    {
        panel.AddChild(Text(label));
        panel.AddChild(slider);
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

    private static void SetText(
        RichTextBlock text,
        string value)
    {
        if (text.Inlines.Count == 1 &&
            text.Inlines[0] is Run run)
        {
            run.Text = value;
            return;
        }

        text.Inlines.Clear();
        text.Inlines.Add(new Run(value));
    }

    private static void UpdateStatus(
        RichTextBlock status,
        MediaPlayer player)
    {
        var diagnostics = player.GetProGpuDiagnostics();
        SetText(
            status,
            $"{player.PlaybackSession.PlaybackState}  " +
            $"{player.PlaybackSession.Position:mm\\:ss\\.fff} / " +
            $"{player.PlaybackSession.NaturalDuration:mm\\:ss\\.fff}  " +
            $"provider={diagnostics.ProviderId ?? "none"}  " +
            $"decode={(diagnostics.HardwareDecoded ? "hardware" : "software/unknown")}  " +
            $"transfer={diagnostics.TransferMode?.ToString() ?? "unavailable"}  " +
            $"loop={(player.IsLoopingEnabled ? "on" : "off")}" +
            (string.IsNullOrWhiteSpace(
                diagnostics.LastFallbackReason)
                ? string.Empty
                : $"  {diagnostics.LastFallbackReason}"));
    }

}
