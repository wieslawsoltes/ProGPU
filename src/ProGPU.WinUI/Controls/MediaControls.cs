using System;
using System.Numerics;
using Microsoft.UI.Xaml.Media;
using ProGPU.Backend;
using ProGPU.Layout;
using ProGPU.Media.Rendering;
using ProGPU.Scene;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>
/// User-visible playback command surface for a MediaPlayerElement.
/// </summary>
public class MediaTransportControls : Control
{
    public static readonly DependencyProperty IsZoomButtonVisibleProperty = Register(nameof(IsZoomButtonVisible), true);
    public static readonly DependencyProperty IsZoomEnabledProperty = Register(nameof(IsZoomEnabled), true);
    public static readonly DependencyProperty IsFastForwardButtonVisibleProperty = Register(nameof(IsFastForwardButtonVisible), true);
    public static readonly DependencyProperty IsFastForwardEnabledProperty = Register(nameof(IsFastForwardEnabled), true);
    public static readonly DependencyProperty IsFastRewindButtonVisibleProperty = Register(nameof(IsFastRewindButtonVisible), true);
    public static readonly DependencyProperty IsFastRewindEnabledProperty = Register(nameof(IsFastRewindEnabled), true);
    public static readonly DependencyProperty IsStopButtonVisibleProperty = Register(nameof(IsStopButtonVisible), true);
    public static readonly DependencyProperty IsStopEnabledProperty = Register(nameof(IsStopEnabled), true);
    public static readonly DependencyProperty IsVolumeButtonVisibleProperty = Register(nameof(IsVolumeButtonVisible), true);
    public static readonly DependencyProperty IsVolumeEnabledProperty = Register(nameof(IsVolumeEnabled), true);
    public static readonly DependencyProperty IsPlaybackRateButtonVisibleProperty = Register(nameof(IsPlaybackRateButtonVisible), true);
    public static readonly DependencyProperty IsPlaybackRateEnabledProperty = Register(nameof(IsPlaybackRateEnabled), true);
    public static readonly DependencyProperty IsSeekBarVisibleProperty = Register(nameof(IsSeekBarVisible), true);
    public static readonly DependencyProperty IsSeekEnabledProperty = Register(nameof(IsSeekEnabled), true);
    public static readonly DependencyProperty IsCompactProperty = Register(nameof(IsCompact), false);
    public static readonly DependencyProperty IsSkipForwardButtonVisibleProperty = Register(nameof(IsSkipForwardButtonVisible), false);
    public static readonly DependencyProperty IsSkipForwardEnabledProperty = Register(nameof(IsSkipForwardEnabled), true);
    public static readonly DependencyProperty IsSkipBackwardButtonVisibleProperty = Register(nameof(IsSkipBackwardButtonVisible), false);
    public static readonly DependencyProperty IsSkipBackwardEnabledProperty = Register(nameof(IsSkipBackwardEnabled), true);
    public static readonly DependencyProperty IsNextTrackButtonVisibleProperty = Register(nameof(IsNextTrackButtonVisible), false);
    public static readonly DependencyProperty IsPreviousTrackButtonVisibleProperty = Register(nameof(IsPreviousTrackButtonVisible), false);
    public static readonly DependencyProperty FastPlayFallbackBehaviourProperty = Register(nameof(FastPlayFallbackBehaviour), FastPlayFallbackBehaviour.Skip);
    public static readonly DependencyProperty ShowAndHideAutomaticallyProperty = Register(nameof(ShowAndHideAutomatically), true);
    public static readonly DependencyProperty IsRepeatEnabledProperty = Register(nameof(IsRepeatEnabled), true);
    public static readonly DependencyProperty IsRepeatButtonVisibleProperty = Register(nameof(IsRepeatButtonVisible), false);

    public bool IsZoomButtonVisible { get => GetBool(IsZoomButtonVisibleProperty); set => SetValue(IsZoomButtonVisibleProperty, value); }
    public bool IsZoomEnabled { get => GetBool(IsZoomEnabledProperty); set => SetValue(IsZoomEnabledProperty, value); }
    public bool IsFastForwardButtonVisible { get => GetBool(IsFastForwardButtonVisibleProperty); set => SetValue(IsFastForwardButtonVisibleProperty, value); }
    public bool IsFastForwardEnabled { get => GetBool(IsFastForwardEnabledProperty); set => SetValue(IsFastForwardEnabledProperty, value); }
    public bool IsFastRewindButtonVisible { get => GetBool(IsFastRewindButtonVisibleProperty); set => SetValue(IsFastRewindButtonVisibleProperty, value); }
    public bool IsFastRewindEnabled { get => GetBool(IsFastRewindEnabledProperty); set => SetValue(IsFastRewindEnabledProperty, value); }
    public bool IsStopButtonVisible { get => GetBool(IsStopButtonVisibleProperty); set => SetValue(IsStopButtonVisibleProperty, value); }
    public bool IsStopEnabled { get => GetBool(IsStopEnabledProperty); set => SetValue(IsStopEnabledProperty, value); }
    public bool IsVolumeButtonVisible { get => GetBool(IsVolumeButtonVisibleProperty); set => SetValue(IsVolumeButtonVisibleProperty, value); }
    public bool IsVolumeEnabled { get => GetBool(IsVolumeEnabledProperty); set => SetValue(IsVolumeEnabledProperty, value); }
    public bool IsPlaybackRateButtonVisible { get => GetBool(IsPlaybackRateButtonVisibleProperty); set => SetValue(IsPlaybackRateButtonVisibleProperty, value); }
    public bool IsPlaybackRateEnabled { get => GetBool(IsPlaybackRateEnabledProperty); set => SetValue(IsPlaybackRateEnabledProperty, value); }
    public bool IsSeekBarVisible { get => GetBool(IsSeekBarVisibleProperty); set => SetValue(IsSeekBarVisibleProperty, value); }
    public bool IsSeekEnabled { get => GetBool(IsSeekEnabledProperty); set => SetValue(IsSeekEnabledProperty, value); }
    public bool IsCompact { get => GetBool(IsCompactProperty); set => SetValue(IsCompactProperty, value); }
    public bool IsSkipForwardButtonVisible { get => GetBool(IsSkipForwardButtonVisibleProperty); set => SetValue(IsSkipForwardButtonVisibleProperty, value); }
    public bool IsSkipForwardEnabled { get => GetBool(IsSkipForwardEnabledProperty); set => SetValue(IsSkipForwardEnabledProperty, value); }
    public bool IsSkipBackwardButtonVisible { get => GetBool(IsSkipBackwardButtonVisibleProperty); set => SetValue(IsSkipBackwardButtonVisibleProperty, value); }
    public bool IsSkipBackwardEnabled { get => GetBool(IsSkipBackwardEnabledProperty); set => SetValue(IsSkipBackwardEnabledProperty, value); }
    public bool IsNextTrackButtonVisible { get => GetBool(IsNextTrackButtonVisibleProperty); set => SetValue(IsNextTrackButtonVisibleProperty, value); }
    public bool IsPreviousTrackButtonVisible { get => GetBool(IsPreviousTrackButtonVisibleProperty); set => SetValue(IsPreviousTrackButtonVisibleProperty, value); }
    public FastPlayFallbackBehaviour FastPlayFallbackBehaviour { get => (FastPlayFallbackBehaviour)(GetValue(FastPlayFallbackBehaviourProperty) ?? FastPlayFallbackBehaviour.Skip); set => SetValue(FastPlayFallbackBehaviourProperty, value); }
    public bool ShowAndHideAutomatically { get => GetBool(ShowAndHideAutomaticallyProperty); set => SetValue(ShowAndHideAutomaticallyProperty, value); }
    public bool IsRepeatEnabled { get => GetBool(IsRepeatEnabledProperty); set => SetValue(IsRepeatEnabledProperty, value); }
    public bool IsRepeatButtonVisible { get => GetBool(IsRepeatButtonVisibleProperty); set => SetValue(IsRepeatButtonVisibleProperty, value); }

    public void Show() => Visibility = Visibility.Visible;
    public void Hide() => Visibility = Visibility.Collapsed;

    private bool GetBool(DependencyProperty property) => (bool)(GetValue(property) ?? false);

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(MediaTransportControls),
            new PropertyMetadata(defaultValue) { AffectsMeasure = true, AffectsRender = true });
}

/// <summary>
/// Visual host for frames produced by a typed MediaPlayer adapter.
/// </summary>
public class MediaPlayerPresenter : FrameworkElement
{
    private MediaGpuSurfacePresenter? _surfacePresenter;
    public static readonly DependencyProperty MediaPlayerProperty = Register<MediaPlayer?>(nameof(MediaPlayer), null, OnMediaPlayerChanged);
    public static readonly DependencyProperty StretchProperty = Register(nameof(Stretch), Stretch.Uniform);
    public static readonly DependencyProperty IsFullWindowProperty = Register(nameof(IsFullWindow), false);
    public static readonly DependencyProperty ProGpuVideoEffectsProperty =
        Register(
            nameof(ProGpuVideoEffects),
            MediaVideoEffectOptions.Identity);

    public MediaPlayer? MediaPlayer { get => GetValue(MediaPlayerProperty) as MediaPlayer; set => SetValue(MediaPlayerProperty, value); }
    public Stretch Stretch { get => (Stretch)(GetValue(StretchProperty) ?? Stretch.Uniform); set => SetValue(StretchProperty, value); }
    public bool IsFullWindow { get => (bool)(GetValue(IsFullWindowProperty) ?? false); set => SetValue(IsFullWindowProperty, value); }
    public MediaVideoEffectOptions ProGpuVideoEffects
    {
        get => (MediaVideoEffectOptions)(
            GetValue(ProGpuVideoEffectsProperty) ??
            MediaVideoEffectOptions.Identity);
        set => SetValue(ProGpuVideoEffectsProperty, value);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize) =>
        MeasureMedia(availableSize, GetNaturalSize(), Stretch);

    public override void OnRender(DrawingContext context)
    {
        MediaPlayer? player = MediaPlayer;
        WgpuContext? gpuContext = GetActiveWgpuContext();
        Vector2 controlSize = Size;
        if (player is null ||
            gpuContext is null ||
            controlSize.X <= 0f ||
            controlSize.Y <= 0f)
        {
            return;
        }

        MediaPlaybackSession session = player.PlaybackSession;
        Windows.Foundation.Rect normalized =
            session.NormalizedSourceRect;
        var presentation = new MediaVideoPresentationOptions(
            stretch: ToMediaStretch(Stretch),
            normalizedSourceRect: new Vector4(
                (float)normalized.X,
                (float)normalized.Y,
                (float)normalized.Width,
                (float)normalized.Height),
            rotation: ToMediaRotation(session.PlaybackRotation),
            isMirrored: session.IsMirroring,
            effects: ProGpuVideoEffects,
            sphericalProjection:
                ToSphericalProjection(
                    session.SphericalVideoProjection));
        _surfacePresenter?.Record(
            context,
            gpuContext,
            new Rect(Vector2.Zero, controlSize),
            in presentation);
    }

    private WgpuContext? GetActiveWgpuContext()
    {
        IReadOnlyList<Window> windows = WindowManager.ActiveWindows;
        if (windows.Count == 0)
        {
            return WgpuContext.Current;
        }
        if (windows.Count == 1)
        {
            return windows[0].WgpuContext;
        }

        Visual? current = this;
        while (current is not null)
        {
            for (int index = 0; index < windows.Count; index++)
            {
                if (ReferenceEquals(windows[index].Content, current))
                {
                    return windows[index].WgpuContext;
                }
            }
            current = current.Parent;
        }
        return WgpuContext.Current;
    }

    private static void OnMediaPlayerChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var presenter = (MediaPlayerPresenter)dependencyObject;
        if (args.OldValue is MediaPlayer oldPlayer)
        {
            oldPlayer.PlaybackSession.NaturalVideoSizeChanged -=
                presenter.OnNaturalVideoSizeChanged;
            oldPlayer.PlaybackSession.PresentationChanged -=
                presenter.OnPresentationChanged;
        }
        presenter._surfacePresenter?.Dispose();
        presenter._surfacePresenter = null;
        if (args.NewValue is MediaPlayer newPlayer)
        {
            presenter._surfacePresenter =
                new MediaGpuSurfacePresenter(
                    newPlayer.ProGpuVideoSurface,
                    presenter.Invalidate,
                    ownerDispatcher:
                        static action =>
                        {
                            Action<Action>? dispatcher =
                                Microsoft.UI.Xaml.Input
                                    .InputSystem
                                    .DispatcherQueue;
                            if (dispatcher is not null)
                            {
                                dispatcher(action);
                            }
                            else
                            {
                                UIThread.Post(action);
                            }
                        });
            newPlayer.PlaybackSession.NaturalVideoSizeChanged +=
                presenter.OnNaturalVideoSizeChanged;
            newPlayer.PlaybackSession.PresentationChanged +=
                presenter.OnPresentationChanged;
        }
        presenter.InvalidateMeasure();
        presenter.Invalidate();
    }

    private void OnNaturalVideoSizeChanged(
        MediaPlaybackSession sender,
        object args)
    {
        InvalidateMeasure();
        Invalidate();
    }

    private void OnPresentationChanged(
        object? sender,
        EventArgs args)
    {
        InvalidateMeasure();
        Invalidate();
    }

    private Vector2 GetNaturalSize()
    {
        MediaPlaybackSession? session = MediaPlayer?.PlaybackSession;
        if (session is null ||
            session.NaturalVideoWidth == 0 ||
            session.NaturalVideoHeight == 0)
        {
            return Vector2.Zero;
        }

        Windows.Foundation.Rect crop =
            session.NormalizedSourceRect;
        float width =
            (float)(session.NaturalVideoWidth * crop.Width);
        float height =
            (float)(session.NaturalVideoHeight * crop.Height);
        return session.PlaybackRotation is
            MediaRotation.Clockwise90Degrees or
            MediaRotation.Clockwise270Degrees
                ? new Vector2(height, width)
                : new Vector2(width, height);
    }

    private static Vector2 MeasureMedia(
        Vector2 availableSize,
        Vector2 naturalSize,
        Stretch stretch)
    {
        if (naturalSize.X <= 0f || naturalSize.Y <= 0f)
        {
            naturalSize = new Vector2(320f, 180f);
        }

        bool finiteWidth = float.IsFinite(availableSize.X);
        bool finiteHeight = float.IsFinite(availableSize.Y);
        if (!finiteWidth && !finiteHeight)
        {
            return naturalSize;
        }
        if (stretch == Stretch.None)
        {
            return naturalSize;
        }
        if (stretch is Stretch.Fill or Stretch.UniformToFill)
        {
            return new Vector2(
                finiteWidth ? availableSize.X : naturalSize.X,
                finiteHeight ? availableSize.Y : naturalSize.Y);
        }
        if (!finiteWidth)
        {
            float scale = availableSize.Y / naturalSize.Y;
            return new Vector2(naturalSize.X * scale, availableSize.Y);
        }
        if (!finiteHeight)
        {
            float scale = availableSize.X / naturalSize.X;
            return new Vector2(availableSize.X, naturalSize.Y * scale);
        }

        float uniformScale = Math.Min(
            availableSize.X / naturalSize.X,
            availableSize.Y / naturalSize.Y);
        return naturalSize * uniformScale;
    }

    private static MediaVideoStretch ToMediaStretch(Stretch stretch) =>
        stretch switch
        {
            Stretch.None => MediaVideoStretch.None,
            Stretch.Fill => MediaVideoStretch.Fill,
            Stretch.UniformToFill =>
                MediaVideoStretch.UniformToFill,
            _ => MediaVideoStretch.Uniform
        };

    private static MediaVideoRotation ToMediaRotation(
        MediaRotation rotation) =>
        rotation switch
        {
            MediaRotation.Clockwise90Degrees =>
                MediaVideoRotation.Clockwise90Degrees,
            MediaRotation.Clockwise180Degrees =>
                MediaVideoRotation.Clockwise180Degrees,
            MediaRotation.Clockwise270Degrees =>
                MediaVideoRotation.Clockwise270Degrees,
            _ => MediaVideoRotation.None
        };

    private static MediaSphericalProjectionOptions
        ToSphericalProjection(
            MediaPlaybackSphericalVideoProjection projection) =>
        new(
            projection.IsEnabled &&
                projection.ProjectionMode ==
                    SphericalVideoProjectionMode.Spherical,
            projection.FrameFormat ==
                Windows.Media.MediaProperties
                    .SphericalVideoFrameFormat.Equirectangular
                ? MediaSphericalVideoFrameFormat.Equirectangular
                : MediaSphericalVideoFrameFormat.None,
            (float)projection.HorizontalFieldOfViewInDegrees,
            projection.ViewOrientation);

    private static DependencyProperty Register<T>(
        string name,
        T defaultValue,
        PropertyChangedCallback? callback = null) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(MediaPlayerPresenter),
            new PropertyMetadata(defaultValue, callback) { AffectsMeasure = true, AffectsRender = true });
}

/// <summary>
/// Owns media playback state, presentation, and transport controls.
/// </summary>
public class MediaPlayerElement : Control
{
    private readonly MediaPlayerPresenter _presenter;
    private MediaPlayer _mediaPlayer;
    private bool _ownsMediaPlayer = true;

    public static readonly DependencyProperty SourceProperty = Register<IMediaPlaybackSource?>(nameof(Source), null, OnSourceChanged);
    public static readonly DependencyProperty TransportControlsProperty = Register<MediaTransportControls?>(nameof(TransportControls), null, OnTransportControlsChanged);
    public static readonly DependencyProperty AreTransportControlsEnabledProperty = Register(nameof(AreTransportControlsEnabled), false, OnTransportControlsEnabledChanged);
    public static readonly DependencyProperty PosterSourceProperty = Register<ImageSource?>(nameof(PosterSource), null);
    public static readonly DependencyProperty StretchProperty = Register(nameof(Stretch), Stretch.Uniform, OnStretchChanged);
    public static readonly DependencyProperty AutoPlayProperty = Register(nameof(AutoPlay), false, OnAutoPlayChanged);
    public static readonly DependencyProperty IsFullWindowProperty = Register(nameof(IsFullWindow), false, OnFullWindowChanged);
    public static readonly DependencyProperty MediaPlayerProperty = Register<MediaPlayer?>(nameof(MediaPlayer), null);
    public static readonly DependencyProperty ProGpuVideoEffectsProperty =
        Register(
            nameof(ProGpuVideoEffects),
            MediaVideoEffectOptions.Identity,
            OnProGpuVideoEffectsChanged);

    public MediaPlayerElement()
    {
        _mediaPlayer = new MediaPlayer();
        _presenter = new MediaPlayerPresenter { MediaPlayer = _mediaPlayer };
        AddChild(_presenter);
        TransportControls = new MediaTransportControls();
        SetValue(MediaPlayerProperty, _mediaPlayer);
    }

    public IMediaPlaybackSource? Source { get => GetValue(SourceProperty) as IMediaPlaybackSource; set => SetValue(SourceProperty, value); }
    public MediaTransportControls? TransportControls { get => GetValue(TransportControlsProperty) as MediaTransportControls; set => SetValue(TransportControlsProperty, value); }
    public bool AreTransportControlsEnabled { get => (bool)(GetValue(AreTransportControlsEnabledProperty) ?? false); set => SetValue(AreTransportControlsEnabledProperty, value); }
    public ImageSource? PosterSource { get => GetValue(PosterSourceProperty) as ImageSource; set => SetValue(PosterSourceProperty, value); }
    public Stretch Stretch { get => (Stretch)(GetValue(StretchProperty) ?? Stretch.Uniform); set => SetValue(StretchProperty, value); }
    public bool AutoPlay { get => (bool)(GetValue(AutoPlayProperty) ?? false); set => SetValue(AutoPlayProperty, value); }
    public bool IsFullWindow { get => (bool)(GetValue(IsFullWindowProperty) ?? false); set => SetValue(IsFullWindowProperty, value); }
    public MediaVideoEffectOptions ProGpuVideoEffects
    {
        get => (MediaVideoEffectOptions)(
            GetValue(ProGpuVideoEffectsProperty) ??
            MediaVideoEffectOptions.Identity);
        set => SetValue(ProGpuVideoEffectsProperty, value);
    }
    public MediaPlayer MediaPlayer => _mediaPlayer;

    public void SetMediaPlayer(MediaPlayer mediaPlayer)
    {
        ArgumentNullException.ThrowIfNull(mediaPlayer);
        if (ReferenceEquals(_mediaPlayer, mediaPlayer))
        {
            return;
        }

        MediaPlayer previous = _mediaPlayer;
        bool disposePrevious = _ownsMediaPlayer;
        _mediaPlayer = mediaPlayer;
        _ownsMediaPlayer = false;
        SetValue(MediaPlayerProperty, mediaPlayer);
        _presenter.MediaPlayer = mediaPlayer;
        mediaPlayer.AutoPlay = AutoPlay;
        mediaPlayer.Source = Source;
        if (AutoPlay)
            mediaPlayer.Play();
        if (disposePrevious)
        {
            previous.Dispose();
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        _presenter.Measure(availableSize);
        TransportControls?.Measure(availableSize);
        return _presenter.DesiredSize;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        _presenter.Arrange(arrangeRect);
        TransportControls?.Arrange(arrangeRect);
    }

    private static void OnSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var element = (MediaPlayerElement)dependencyObject;
        element._mediaPlayer.Source = args.NewValue as IMediaPlaybackSource;
        if (element.AutoPlay)
            element._mediaPlayer.Play();
    }

    private static void OnTransportControlsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var element = (MediaPlayerElement)dependencyObject;
        if (args.OldValue is MediaTransportControls oldControls && ReferenceEquals(oldControls.Parent, element))
            element.RemoveChild(oldControls);
        if (args.NewValue is MediaTransportControls newControls && element.AreTransportControlsEnabled)
            element.AddChild(newControls);
        element.InvalidateMeasure();
        element.Invalidate();
    }

    private static void OnTransportControlsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var element = (MediaPlayerElement)dependencyObject;
        var controls = element.TransportControls;
        if (controls == null)
            return;

        if ((bool)(args.NewValue ?? true))
        {
            if (!ReferenceEquals(controls.Parent, element))
                element.AddChild(controls);
        }
        else if (ReferenceEquals(controls.Parent, element))
        {
            element.RemoveChild(controls);
        }

        element.InvalidateMeasure();
        element.Invalidate();
    }

    private static void OnStretchChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((MediaPlayerElement)dependencyObject)._presenter.Stretch = (Stretch)(args.NewValue ?? Stretch.Uniform);

    private static void OnAutoPlayChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var element = (MediaPlayerElement)dependencyObject;
        bool autoPlay = (bool)(args.NewValue ?? false);
        element._mediaPlayer.AutoPlay = autoPlay;
        if (autoPlay)
            element._mediaPlayer.Play();
    }

    private static void OnFullWindowChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((MediaPlayerElement)dependencyObject)._presenter.IsFullWindow = (bool)(args.NewValue ?? false);

    private static void OnProGpuVideoEffectsChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((MediaPlayerElement)dependencyObject)
            ._presenter.ProGpuVideoEffects =
                (MediaVideoEffectOptions)(
                    args.NewValue ??
                    MediaVideoEffectOptions.Identity);

    private static DependencyProperty Register<T>(
        string name,
        T defaultValue,
        PropertyChangedCallback? callback = null) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(MediaPlayerElement),
            new PropertyMetadata(defaultValue, callback) { AffectsMeasure = true, AffectsArrange = true, AffectsRender = true });
}
