using System;
using System.Buffers;
using System.Numerics;
using Microsoft.UI.Xaml.Media;
using ProGPU.Backend;
using ProGPU.Layout;
using ProGPU.Media.Rendering;
using ProGPU.Scene;
using ProGPU.Vector;
using Windows.Media;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>
/// User-visible playback command surface for a MediaPlayerElement.
/// </summary>
public class MediaTransportControls : Control
{
    private const float ControlHeight = 76f;
    private const float CommandHeight = 34f;
    private const float CommandSpacing = 4f;
    private const float HorizontalInset = 10f;
    private const int MaximumThumbnailBytes =
        16 * 1024 * 1024;
    private static readonly TimeSpan AutoHideDelay =
        TimeSpan.FromSeconds(3);

    private readonly Brush _background =
        new ThemeResourceBrush("HeaderBackground");
    private readonly Pen _borderPen =
        new(
            new ThemeResourceBrush("ControlBorder"),
            1f);
    private readonly Button _previousButton;
    private readonly Button _fastRewindButton;
    private readonly Button _skipBackwardButton;
    private readonly Button _playPauseButton;
    private readonly Button _skipForwardButton;
    private readonly Button _fastForwardButton;
    private readonly Button _nextButton;
    private readonly Button _stopButton;
    private readonly Button _repeatButton;
    private readonly Button _rateButton;
    private readonly Button _muteButton;
    private readonly Button _zoomButton;
    private readonly TextBlock _playPauseLabel;
    private readonly TextBlock _repeatLabel;
    private readonly TextBlock _rateLabel;
    private readonly TextBlock _muteLabel;
    private readonly TextBlock _zoomLabel;
    private readonly TextBlock _timeText;
    private readonly Image _thumbnailPreview;
    private readonly Slider _positionSlider;
    private readonly Slider _volumeSlider;
    private readonly FrameworkElement[] _commandElements;
    private readonly UIElement[] _dropoutCandidates;
    private MediaPlayerElement? _owner;
    private MediaPlayer? _mediaPlayer;
    private IInputStream? _lastThumbnailImage;
    private long _lastDisplayedSecond = long.MinValue;
    private long _autoHideGeneration;
    private long _thumbnailGeneration;
    private bool _synchronizing;

    public static readonly DependencyProperty IsZoomButtonVisibleProperty = Register(nameof(IsZoomButtonVisible), true);
    public static readonly DependencyProperty IsZoomEnabledProperty = Register(nameof(IsZoomEnabled), true);
    public static readonly DependencyProperty IsFastForwardButtonVisibleProperty = Register(nameof(IsFastForwardButtonVisible), false);
    public static readonly DependencyProperty IsFastForwardEnabledProperty = Register(nameof(IsFastForwardEnabled), false);
    public static readonly DependencyProperty IsFastRewindButtonVisibleProperty = Register(nameof(IsFastRewindButtonVisible), false);
    public static readonly DependencyProperty IsFastRewindEnabledProperty = Register(nameof(IsFastRewindEnabled), false);
    public static readonly DependencyProperty IsStopButtonVisibleProperty = Register(nameof(IsStopButtonVisible), false);
    public static readonly DependencyProperty IsStopEnabledProperty = Register(nameof(IsStopEnabled), false);
    public static readonly DependencyProperty IsVolumeButtonVisibleProperty = Register(nameof(IsVolumeButtonVisible), true);
    public static readonly DependencyProperty IsVolumeEnabledProperty = Register(nameof(IsVolumeEnabled), true);
    public static readonly DependencyProperty IsPlaybackRateButtonVisibleProperty = Register(nameof(IsPlaybackRateButtonVisible), false);
    public static readonly DependencyProperty IsPlaybackRateEnabledProperty = Register(nameof(IsPlaybackRateEnabled), false);
    public static readonly DependencyProperty IsSeekBarVisibleProperty = Register(nameof(IsSeekBarVisible), true);
    public static readonly DependencyProperty IsSeekEnabledProperty = Register(nameof(IsSeekEnabled), true);
    public static readonly DependencyProperty IsCompactProperty = Register(nameof(IsCompact), false);
    public static readonly DependencyProperty IsSkipForwardButtonVisibleProperty = Register(nameof(IsSkipForwardButtonVisible), false);
    public static readonly DependencyProperty IsSkipForwardEnabledProperty = Register(nameof(IsSkipForwardEnabled), false);
    public static readonly DependencyProperty IsSkipBackwardButtonVisibleProperty = Register(nameof(IsSkipBackwardButtonVisible), false);
    public static readonly DependencyProperty IsSkipBackwardEnabledProperty = Register(nameof(IsSkipBackwardEnabled), false);
    public static readonly DependencyProperty IsNextTrackButtonVisibleProperty = Register(nameof(IsNextTrackButtonVisible), false);
    public static readonly DependencyProperty IsPreviousTrackButtonVisibleProperty = Register(nameof(IsPreviousTrackButtonVisible), false);
    public static readonly DependencyProperty FastPlayFallbackBehaviourProperty = Register(nameof(FastPlayFallbackBehaviour), FastPlayFallbackBehaviour.Skip);
    public static readonly DependencyProperty ShowAndHideAutomaticallyProperty = Register(nameof(ShowAndHideAutomatically), true);
    public static readonly DependencyProperty IsRepeatEnabledProperty = Register(nameof(IsRepeatEnabled), false);
    public static readonly DependencyProperty IsRepeatButtonVisibleProperty = Register(nameof(IsRepeatButtonVisible), false);

    public MediaTransportControls()
    {
        (_previousButton, _) =
            CreateButton("Previous", ExecutePrevious);
        (_fastRewindButton, _) =
            CreateButton("Rewind", ExecuteFastRewind);
        (_skipBackwardButton, _) =
            CreateButton("-10 s", ExecuteSkipBackward);
        (_playPauseButton, _playPauseLabel) =
            CreateButton("Play", ExecutePlayPause);
        (_skipForwardButton, _) =
            CreateButton("+10 s", ExecuteSkipForward);
        (_fastForwardButton, _) =
            CreateButton("Fast", ExecuteFastForward);
        (_nextButton, _) =
            CreateButton("Next", ExecuteNext);
        (_stopButton, _) =
            CreateButton("Stop", ExecuteStop);
        (_repeatButton, _repeatLabel) =
            CreateButton("Repeat", ExecuteRepeat);
        (_rateButton, _rateLabel) =
            CreateButton("1×", ExecuteNextPlaybackRate);
        (_muteButton, _muteLabel) =
            CreateButton("Mute", ExecuteMute);
        (_zoomButton, _zoomLabel) =
            CreateButton("Fill", ExecuteZoom);

        _positionSlider = new Slider
        {
            Minimum = 0d,
            Maximum = 1d,
            Value = 0d,
            Height = 26f,
            IsThumbToolTipEnabled = true
        };
        _positionSlider.ValueChanged +=
            OnPositionSliderChanged;

        _volumeSlider = new Slider
        {
            Minimum = 0d,
            Maximum = 100d,
            Value = 100d,
            Width = 104f,
            Height = CommandHeight,
            IsThumbToolTipEnabled = true
        };
        _volumeSlider.ValueChanged +=
            OnVolumeSliderChanged;

        _timeText = new TextBlock
        {
            Text = "0:00 / 0:00",
            Width = 100f,
            Height = CommandHeight,
            FontSize = 11f,
            Foreground =
                new ThemeResourceBrush("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        _thumbnailPreview = new Image
        {
            Width = 160f,
            Height = 90f,
            Stretch = Stretch.UniformToFill,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };

        _commandElements =
        [
            _previousButton,
            _fastRewindButton,
            _skipBackwardButton,
            _playPauseButton,
            _skipForwardButton,
            _fastForwardButton,
            _nextButton,
            _stopButton,
            _repeatButton,
            _rateButton,
            _muteButton,
            _volumeSlider,
            _timeText,
            _zoomButton
        ];
        _dropoutCandidates =
        [
            _zoomButton,
            _rateButton,
            _stopButton,
            _fastForwardButton,
            _fastRewindButton,
            _skipForwardButton,
            _skipBackwardButton,
            _repeatButton,
            _volumeSlider,
            _timeText,
            _previousButton,
            _nextButton
        ];

        SetDefaultDropoutOrder(_zoomButton, 120);
        SetDefaultDropoutOrder(_rateButton, 110);
        SetDefaultDropoutOrder(_stopButton, 100);
        SetDefaultDropoutOrder(_fastForwardButton, 90);
        SetDefaultDropoutOrder(_fastRewindButton, 80);
        SetDefaultDropoutOrder(_skipForwardButton, 70);
        SetDefaultDropoutOrder(_skipBackwardButton, 60);
        SetDefaultDropoutOrder(_repeatButton, 50);
        SetDefaultDropoutOrder(_volumeSlider, 40);
        SetDefaultDropoutOrder(_timeText, 30);
        SetDefaultDropoutOrder(_previousButton, 20);
        SetDefaultDropoutOrder(_nextButton, 10);

        AddChild(_positionSlider);
        for (int index = 0;
             index < _commandElements.Length;
             index++)
        {
            AddChild(_commandElements[index]);
        }
        AddChild(_thumbnailPreview);

        UpdatePresentation(forceTimeText: true);
    }

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

    public event Windows.Foundation.TypedEventHandler<
        MediaTransportControls,
        MediaTransportControlsThumbnailRequestedEventArgs>?
        ThumbnailRequested;

    public void Show()
    {
        Visibility = Visibility.Visible;
        ScheduleAutoHide();
    }

    public void Hide()
    {
        Interlocked.Increment(ref _autoHideGeneration);
        Visibility = Visibility.Collapsed;
    }

    internal MediaPlayer? AttachedMediaPlayer =>
        _mediaPlayer;
    internal IInputStream? LastThumbnailImage =>
        _lastThumbnailImage;

    internal void SetMediaPlayer(
        MediaPlayerElement? owner,
        MediaPlayer? mediaPlayer)
    {
        if (ReferenceEquals(_owner, owner) &&
            ReferenceEquals(_mediaPlayer, mediaPlayer))
        {
            return;
        }

        DetachPlayer();
        _owner = owner;
        _mediaPlayer = mediaPlayer;
        if (mediaPlayer is not null)
        {
            MediaPlaybackSession session =
                mediaPlayer.PlaybackSession;
            session.PlaybackStateChanged +=
                OnPlaybackSessionChanged;
            session.PositionChanged +=
                OnPlaybackSessionChanged;
            session.NaturalDurationChanged +=
                OnPlaybackSessionChanged;
            session.PlaybackRateChanged +=
                OnPlaybackSessionChanged;
            session.SupportedPlaybackRatesChanged +=
                OnPlaybackSessionChanged;
            mediaPlayer.SourceChanged += OnPlayerChanged;
            mediaPlayer.VolumeChanged += OnPlayerChanged;
            mediaPlayer.IsMutedChanged += OnPlayerChanged;
            AttachCommandBehaviorHandlers(
                mediaPlayer.CommandManager,
                attach: true);
        }

        UpdatePresentation(forceTimeText: true);
    }

    internal void NotifyInteraction() => Show();

    internal void ExecutePlayPause()
    {
        MediaPlayer? player = _mediaPlayer;
        if (player is null ||
            !player.CommandManager.IsEnabled)
        {
            return;
        }

        if (player.PlaybackSession.PlaybackState is
            MediaPlaybackState.Playing or
            MediaPlaybackState.Buffering)
        {
            if (player.CommandManager.PauseBehavior.IsEnabled)
            {
                player.CommandManager.ReceivePause();
            }
        }
        else if (player.CommandManager.PlayBehavior.IsEnabled)
        {
            player.CommandManager.ReceivePlay();
        }
    }

    internal void ExecuteSeek(TimeSpan position)
    {
        MediaPlayer? player = _mediaPlayer;
        if (player is null ||
            !IsSeekEnabled ||
            !player.CommandManager.IsEnabled ||
            !player.CommandManager.PositionBehavior.IsEnabled)
        {
            return;
        }

        TimeSpan duration =
            player.PlaybackSession.NaturalDuration;
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }
        if (duration > TimeSpan.Zero &&
            position > duration)
        {
            position = duration;
        }

        RaiseThumbnailRequested();
        player.CommandManager.ReceivePosition(position);
    }

    internal void ExecuteVolume(double volume)
    {
        MediaPlayer? player = _mediaPlayer;
        if (player is null ||
            !IsVolumeEnabled ||
            !player.CommandManager.IsEnabled ||
            !double.IsFinite(volume))
        {
            return;
        }

        player.Volume = Math.Clamp(volume, 0d, 1d);
    }

    internal void ExecutePlaybackRate(double rate)
    {
        MediaPlayer? player = _mediaPlayer;
        if (player is null ||
            !IsPlaybackRateEnabled ||
            !player.CommandManager.IsEnabled ||
            !player.CommandManager.RateBehavior.IsEnabled ||
            !double.IsFinite(rate) ||
            rate <= 0d)
        {
            return;
        }

        player.CommandManager.ReceiveRate(rate);
    }

    internal void ExecuteRepeat()
    {
        MediaPlayer? player = _mediaPlayer;
        if (player is null ||
            !IsRepeatEnabled ||
            !player.CommandManager.IsEnabled)
        {
            return;
        }

        if (player.Source is MediaPlaybackList list)
        {
            if (!player.CommandManager
                    .AutoRepeatModeBehavior.IsEnabled)
            {
                return;
            }

            player.CommandManager.ReceiveAutoRepeatMode(
                list.AutoRepeatEnabled
                    ? MediaPlaybackAutoRepeatMode.None
                    : MediaPlaybackAutoRepeatMode.List);
        }
        else
        {
            player.IsLoopingEnabled =
                !player.IsLoopingEnabled;
        }

        UpdatePresentation(forceTimeText: false);
    }

    internal void ExecuteNext()
    {
        MediaPlayer? player = _mediaPlayer;
        if (player?.CommandManager is
                { IsEnabled: true } manager &&
            manager.NextBehavior.IsEnabled)
        {
            manager.ReceiveNext();
        }
    }

    internal void ExecutePrevious()
    {
        MediaPlayer? player = _mediaPlayer;
        if (player?.CommandManager is
                { IsEnabled: true } manager &&
            manager.PreviousBehavior.IsEnabled)
        {
            manager.ReceivePrevious();
        }
    }

    internal void ExecuteStop()
    {
        MediaPlayer? player = _mediaPlayer;
        if (player is null ||
            !IsStopEnabled ||
            !player.CommandManager.IsEnabled)
        {
            return;
        }

        player.Pause();
        if (player.PlaybackSession.CanSeek)
        {
            player.PlaybackSession.Position =
                TimeSpan.Zero;
        }
    }

    internal void ExecuteZoom()
    {
        if (!IsZoomEnabled || _owner is null)
        {
            return;
        }

        _owner.Stretch =
            _owner.Stretch == Stretch.UniformToFill
                ? Stretch.Uniform
                : Stretch.UniformToFill;
        UpdatePresentation(forceTimeText: false);
    }

    internal void RaiseThumbnailRequested()
    {
        Windows.Foundation.TypedEventHandler<
            MediaTransportControls,
            MediaTransportControlsThumbnailRequestedEventArgs>?
            handler = ThumbnailRequested;
        if (handler is null || _owner is null)
        {
            return;
        }

        var args =
            new MediaTransportControlsThumbnailRequestedEventArgs();
        handler(this, args);
        args.Seal(
            image =>
            {
                _lastThumbnailImage = image;
                long generation =
                    Interlocked.Increment(
                        ref _thumbnailGeneration);
                if (image is null)
                {
                    DispatchVisualUpdate(
                        new WeakReference<
                            MediaTransportControls>(this),
                        controls =>
                        {
                            if (Volatile.Read(
                                    ref controls
                                        ._thumbnailGeneration) !=
                                generation)
                            {
                                return;
                            }

                            controls._thumbnailPreview.Source =
                                null;
                            controls._thumbnailPreview.Visibility =
                                Visibility.Collapsed;
                            controls.Invalidate();
                        });
                }
                else
                {
                    _ = LoadThumbnailAsync(
                        new WeakReference<
                            MediaTransportControls>(this),
                        image,
                        generation);
                }
            });
    }

    public override void OnPointerPressed(
        PointerRoutedEventArgs e)
    {
        NotifyInteraction();
        base.OnPointerPressed(e);
    }

    public override void OnPointerMoved(
        PointerRoutedEventArgs e)
    {
        NotifyInteraction();
        base.OnPointerMoved(e);
    }

    protected override Vector2 MeasureOverride(
        Vector2 availableSize)
    {
        float width = float.IsFinite(availableSize.X)
            ? availableSize.X
            : MeasureRequestedCommandWidth();
        UpdateControlStates(width);

        _positionSlider.Measure(
            new Vector2(
                Math.Max(0f, width - 2f * HorizontalInset),
                26f));
        for (int index = 0;
             index < _commandElements.Length;
             index++)
        {
            FrameworkElement element =
                _commandElements[index];
            element.Measure(
                element.Visibility == Visibility.Collapsed
                    ? Vector2.Zero
                    : new Vector2(
                        GetElementWidth(element),
                        CommandHeight));
        }

        return new Vector2(width, ControlHeight);
    }

    protected override void ArrangeOverride(
        Rect arrangeRect)
    {
        UpdateControlStates(arrangeRect.Width);
        float top =
            arrangeRect.Y +
            Math.Max(0f, arrangeRect.Height - ControlHeight);
        if (_positionSlider.Visibility !=
            Visibility.Collapsed)
        {
            _positionSlider.Arrange(
                new Rect(
                    arrangeRect.X + HorizontalInset,
                    top + 4f,
                    Math.Max(
                        0f,
                        arrangeRect.Width -
                        2f * HorizontalInset),
                    26f));
        }
        else
        {
            _positionSlider.Arrange(
                new Rect(arrangeRect.X, top, 0f, 0f));
        }

        float requestedWidth =
            MeasureVisibleCommandWidth();
        float x =
            arrangeRect.X +
            Math.Max(
                HorizontalInset,
                (arrangeRect.Width - requestedWidth) * 0.5f);
        float y = top + 36f;
        for (int index = 0;
             index < _commandElements.Length;
             index++)
        {
            FrameworkElement element =
                _commandElements[index];
            if (element.Visibility ==
                Visibility.Collapsed)
            {
                element.Arrange(
                    new Rect(x, y, 0f, 0f));
                continue;
            }

            float width = GetElementWidth(element);
            element.Arrange(
                new Rect(x, y, width, CommandHeight));
            x += width + CommandSpacing;
        }

        if (_thumbnailPreview.Visibility !=
            Visibility.Collapsed)
        {
            double range =
                _positionSlider.Maximum -
                _positionSlider.Minimum;
            double ratio = range <= 0d
                ? 0d
                : (_positionSlider.Value -
                   _positionSlider.Minimum) / range;
            float previewWidth =
                _thumbnailPreview.Width;
            float previewHeight =
                _thumbnailPreview.Height;
            float previewCenter =
                arrangeRect.X +
                HorizontalInset +
                (float)ratio *
                Math.Max(
                    0f,
                    arrangeRect.Width -
                    2f * HorizontalInset);
            float previewX =
                Math.Clamp(
                    previewCenter -
                    previewWidth * 0.5f,
                    arrangeRect.X,
                    arrangeRect.X +
                    Math.Max(
                        0f,
                        arrangeRect.Width -
                        previewWidth));
            _thumbnailPreview.Arrange(
                new Rect(
                    previewX,
                    Math.Max(
                        arrangeRect.Y,
                        top - previewHeight - 6f),
                    previewWidth,
                    previewHeight));
        }
        else
        {
            _thumbnailPreview.Arrange(
                new Rect(
                    arrangeRect.X,
                    top,
                    0f,
                    0f));
        }
    }

    public override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        float height = Math.Min(ControlHeight, Size.Y);
        if (height <= 0f)
        {
            return;
        }

        context.DrawRectangle(
            _background,
            _borderPen,
            new Rect(
                0f,
                Size.Y - height,
                Size.X,
                height));
    }

    protected override void OnPropertyChanged(
        DependencyProperty dp,
        object? oldValue,
        object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);
        if (dp.OwnerType != typeof(MediaTransportControls))
        {
            return;
        }

        if (dp == ShowAndHideAutomaticallyProperty &&
            !(bool)(newValue ?? true))
        {
            Interlocked.Increment(
                ref _autoHideGeneration);
        }

        UpdateControlStates(Size.X);
        InvalidateMeasure();
        Invalidate();
    }

    private bool GetBool(DependencyProperty property) => (bool)(GetValue(property) ?? false);

    private static (
        Button Button,
        TextBlock Label)
        CreateButton(
            string text,
            Action action)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11f,
            Foreground =
                new ThemeResourceBrush("TextPrimary"),
            HorizontalAlignment =
                HorizontalAlignment.Center,
            VerticalAlignment =
                VerticalAlignment.Center
        };
        var button = new Button
        {
            Content = label,
            Width = 58f,
            Height = CommandHeight,
            Padding = new Thickness(6f, 2f, 6f, 2f)
        };
        button.Click += (_, _) => action();
        return (button, label);
    }

    private static void SetDefaultDropoutOrder(
        UIElement element,
        int value)
    {
        if (MediaTransportControlsHelper
                .GetDropoutOrder(element) is null)
        {
            MediaTransportControlsHelper
                .SetDropoutOrder(element, value);
        }
    }

    private void DetachPlayer()
    {
        MediaPlayer? player = _mediaPlayer;
        if (player is null)
        {
            return;
        }

        MediaPlaybackSession session =
            player.PlaybackSession;
        session.PlaybackStateChanged -=
            OnPlaybackSessionChanged;
        session.PositionChanged -=
            OnPlaybackSessionChanged;
        session.NaturalDurationChanged -=
            OnPlaybackSessionChanged;
        session.PlaybackRateChanged -=
            OnPlaybackSessionChanged;
        session.SupportedPlaybackRatesChanged -=
            OnPlaybackSessionChanged;
        player.SourceChanged -= OnPlayerChanged;
        player.VolumeChanged -= OnPlayerChanged;
        player.IsMutedChanged -= OnPlayerChanged;
        AttachCommandBehaviorHandlers(
            player.CommandManager,
            attach: false);
        _mediaPlayer = null;
    }

    private void AttachCommandBehaviorHandlers(
        MediaPlaybackCommandManager manager,
        bool attach)
    {
        MediaPlaybackCommandManagerCommandBehavior[]
            behaviors =
            [
                manager.AutoRepeatModeBehavior,
                manager.FastForwardBehavior,
                manager.NextBehavior,
                manager.PauseBehavior,
                manager.PlayBehavior,
                manager.PositionBehavior,
                manager.PreviousBehavior,
                manager.RateBehavior,
                manager.RewindBehavior,
                manager.ShuffleBehavior
            ];
        for (int index = 0;
             index < behaviors.Length;
             index++)
        {
            if (attach)
            {
                behaviors[index].IsEnabledChanged +=
                    OnCommandBehaviorChanged;
            }
            else
            {
                behaviors[index].IsEnabledChanged -=
                    OnCommandBehaviorChanged;
            }
        }
    }

    private void OnPlaybackSessionChanged(
        MediaPlaybackSession sender,
        object args) =>
        UpdatePresentation(forceTimeText: false);

    private void OnPlayerChanged(
        MediaPlayer sender,
        object args) =>
        UpdatePresentation(forceTimeText: true);

    private void OnCommandBehaviorChanged(
        MediaPlaybackCommandManagerCommandBehavior sender,
        object args) =>
        UpdateControlStates(Size.X);

    private void OnPositionSliderChanged(
        object? sender,
        RoutedPropertyChangedEventArgs<double> args)
    {
        if (!_synchronizing)
        {
            ExecuteSeek(
                TimeSpan.FromSeconds(args.NewValue));
        }
    }

    private void OnVolumeSliderChanged(
        object? sender,
        RoutedPropertyChangedEventArgs<double> args)
    {
        if (!_synchronizing)
        {
            ExecuteVolume(args.NewValue / 100d);
        }
    }

    private void ExecuteSkipBackward() =>
        ExecuteRelativeSeek(TimeSpan.FromSeconds(-10));

    private void ExecuteSkipForward() =>
        ExecuteRelativeSeek(TimeSpan.FromSeconds(10));

    private void ExecuteRelativeSeek(TimeSpan delta)
    {
        MediaPlayer? player = _mediaPlayer;
        if (player is null)
        {
            return;
        }

        ExecuteSeek(
            player.PlaybackSession.Position + delta);
    }

    private void ExecuteFastForward()
    {
        MediaPlayer? player = _mediaPlayer;
        if (player?.CommandManager is not
                { IsEnabled: true } manager ||
            !IsFastForwardEnabled)
        {
            return;
        }

        if (manager.FastForwardBehavior.IsEnabled)
        {
            manager.ReceiveFastForward();
        }
        else if (FastPlayFallbackBehaviour ==
                     FastPlayFallbackBehaviour.Skip)
        {
            ExecuteRelativeSeek(
                TimeSpan.FromSeconds(10));
        }
    }

    private void ExecuteFastRewind()
    {
        MediaPlayer? player = _mediaPlayer;
        if (player?.CommandManager is not
                { IsEnabled: true } manager ||
            !IsFastRewindEnabled)
        {
            return;
        }

        if (manager.RewindBehavior.IsEnabled)
        {
            manager.ReceiveRewind();
        }
        else if (FastPlayFallbackBehaviour ==
                     FastPlayFallbackBehaviour.Skip)
        {
            ExecuteRelativeSeek(
                TimeSpan.FromSeconds(-10));
        }
    }

    private void ExecuteNextPlaybackRate()
    {
        double rate =
            _mediaPlayer?.PlaybackSession.PlaybackRate ??
            1d;
        double next = rate switch
        {
            < 0.75d => 1d,
            < 1.25d => 1.5d,
            < 1.75d => 2d,
            _ => 0.5d
        };
        ExecutePlaybackRate(next);
    }

    private void ExecuteMute()
    {
        MediaPlayer? player = _mediaPlayer;
        if (player is null ||
            !IsVolumeEnabled ||
            !player.CommandManager.IsEnabled)
        {
            return;
        }

        player.IsMuted = !player.IsMuted;
    }

    private void UpdatePresentation(bool forceTimeText)
    {
        MediaPlayer? player = _mediaPlayer;
        _synchronizing = true;
        try
        {
            if (player is null)
            {
                _positionSlider.Maximum = 1d;
                _positionSlider.Value = 0d;
                _volumeSlider.Value = 100d;
                _playPauseLabel.Text = "Play";
                _rateLabel.Text = "1×";
                _muteLabel.Text = "Mute";
                _repeatLabel.Text = "Repeat";
                _zoomLabel.Text = "Fill";
                UpdateTimeText(
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    force: true);
            }
            else
            {
                MediaPlaybackSession session =
                    player.PlaybackSession;
                TimeSpan duration =
                    session.NaturalDuration;
                TimeSpan position =
                    session.Position;
                _positionSlider.Maximum =
                    Math.Max(
                        1d,
                        duration.TotalSeconds);
                _positionSlider.Value =
                    Math.Clamp(
                        position.TotalSeconds,
                        0d,
                        _positionSlider.Maximum);
                _volumeSlider.Value =
                    player.Volume * 100d;
                _playPauseLabel.Text =
                    session.PlaybackState is
                        MediaPlaybackState.Playing or
                        MediaPlaybackState.Buffering
                        ? "Pause"
                        : "Play";
                _rateLabel.Text =
                    $"{session.PlaybackRate:0.##}×";
                _muteLabel.Text =
                    player.IsMuted
                        ? "Unmute"
                        : "Mute";
                bool repeating =
                    player.Source is MediaPlaybackList list
                        ? list.AutoRepeatEnabled
                        : player.IsLoopingEnabled;
                _repeatLabel.Text =
                    repeating
                        ? "Repeat on"
                        : "Repeat";
                _zoomLabel.Text =
                    _owner?.Stretch ==
                        Stretch.UniformToFill
                        ? "Fit"
                        : "Fill";
                UpdateTimeText(
                    position,
                    duration,
                    forceTimeText);
            }
        }
        finally
        {
            _synchronizing = false;
        }

        UpdateControlStates(Size.X);
        Invalidate();
    }

    private void UpdateTimeText(
        TimeSpan position,
        TimeSpan duration,
        bool force)
    {
        long second =
            Math.Max(0L, (long)position.TotalSeconds);
        if (!force &&
            second == _lastDisplayedSecond)
        {
            return;
        }

        _lastDisplayedSecond = second;
        _timeText.Text =
            $"{FormatTime(position)} / {FormatTime(duration)}";
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1d
            ? $"{(long)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(long)value.TotalMinutes}:{value.Seconds:00}";
    }

    private void UpdateControlStates(float availableWidth)
    {
        MediaPlayer? player = _mediaPlayer;
        MediaPlaybackCommandManager? manager =
            player?.CommandManager;
        bool linked = manager?.IsEnabled == true;
        bool canSeek =
            linked &&
            manager!.PositionBehavior.IsEnabled;

        SetState(
            _previousButton,
            IsPreviousTrackButtonVisible,
            linked &&
            manager!.PreviousBehavior.IsEnabled);
        SetState(
            _fastRewindButton,
            IsFastRewindButtonVisible &&
            ShouldShowFastPlay(
                manager?.RewindBehavior.IsEnabled == true),
            IsFastRewindEnabled &&
            linked &&
            CanExecuteFastPlay(
                manager!.RewindBehavior.IsEnabled,
                canSeek));
        SetState(
            _skipBackwardButton,
            IsSkipBackwardButtonVisible,
            IsSkipBackwardEnabled && canSeek);
        SetState(
            _playPauseButton,
            visible: true,
            enabled:
                linked &&
                (manager!.PlayBehavior.IsEnabled ||
                 manager.PauseBehavior.IsEnabled));
        SetState(
            _skipForwardButton,
            IsSkipForwardButtonVisible,
            IsSkipForwardEnabled && canSeek);
        SetState(
            _fastForwardButton,
            IsFastForwardButtonVisible &&
            ShouldShowFastPlay(
                manager?.FastForwardBehavior
                    .IsEnabled == true),
            IsFastForwardEnabled &&
            linked &&
            CanExecuteFastPlay(
                manager!.FastForwardBehavior.IsEnabled,
                canSeek));
        SetState(
            _nextButton,
            IsNextTrackButtonVisible,
            linked &&
            manager!.NextBehavior.IsEnabled);
        SetState(
            _stopButton,
            IsStopButtonVisible,
            IsStopEnabled &&
            linked &&
            player?.CanPause == true);
        SetState(
            _repeatButton,
            IsRepeatButtonVisible,
            IsRepeatEnabled &&
            linked &&
            (player?.Source is not MediaPlaybackList ||
             manager!.AutoRepeatModeBehavior.IsEnabled));
        SetState(
            _rateButton,
            IsPlaybackRateButtonVisible,
            IsPlaybackRateEnabled &&
            linked &&
            manager!.RateBehavior.IsEnabled);
        SetState(
            _muteButton,
            IsVolumeButtonVisible,
            IsVolumeEnabled && linked);
        SetState(
            _volumeSlider,
            IsVolumeButtonVisible && !IsCompact,
            IsVolumeEnabled && linked);
        SetState(
            _timeText,
            IsSeekBarVisible && !IsCompact,
            enabled: true);
        SetState(
            _zoomButton,
            IsZoomButtonVisible,
            IsZoomEnabled && _owner is not null);
        SetState(
            _positionSlider,
            IsSeekBarVisible,
            IsSeekEnabled && canSeek);

        DropCommandsToFit(availableWidth);
    }

    private bool ShouldShowFastPlay(bool supported) =>
        supported ||
        FastPlayFallbackBehaviour !=
            FastPlayFallbackBehaviour.Hide;

    private bool CanExecuteFastPlay(
        bool supported,
        bool canSeek) =>
        supported ||
        (FastPlayFallbackBehaviour ==
             FastPlayFallbackBehaviour.Skip &&
         canSeek);

    private static void SetState(
        FrameworkElement element,
        bool visible,
        bool enabled)
    {
        element.Visibility =
            visible
                ? Visibility.Visible
                : Visibility.Collapsed;
        element.IsEnabled = enabled;
    }

    private void DropCommandsToFit(
        float availableWidth)
    {
        if (!float.IsFinite(availableWidth) ||
            availableWidth <= 0f)
        {
            return;
        }

        float limit =
            Math.Max(
                0f,
                availableWidth -
                2f * HorizontalInset);
        while (MeasureVisibleCommandWidth() > limit)
        {
            UIElement? candidate = null;
            int highestOrder = int.MinValue;
            for (int index = 0;
                 index < _dropoutCandidates.Length;
                 index++)
            {
                UIElement current =
                    _dropoutCandidates[index];
                if (current is not FrameworkElement
                    {
                        Visibility: Visibility.Visible
                    })
                {
                    continue;
                }

                int order =
                    MediaTransportControlsHelper
                        .GetDropoutOrder(current) ??
                    0;
                if (order > highestOrder)
                {
                    highestOrder = order;
                    candidate = current;
                }
            }

            if (candidate is not FrameworkElement
                element)
            {
                break;
            }

            element.Visibility =
                Visibility.Collapsed;
        }
    }

    private float MeasureRequestedCommandWidth()
    {
        UpdateControlStates(float.PositiveInfinity);
        return Math.Max(
            240f,
            MeasureVisibleCommandWidth() +
            2f * HorizontalInset);
    }

    private float MeasureVisibleCommandWidth()
    {
        float width = 0f;
        bool hasPrevious = false;
        for (int index = 0;
             index < _commandElements.Length;
             index++)
        {
            FrameworkElement element =
                _commandElements[index];
            if (element.Visibility ==
                Visibility.Collapsed)
            {
                continue;
            }

            if (hasPrevious)
            {
                width += CommandSpacing;
            }
            width += GetElementWidth(element);
            hasPrevious = true;
        }

        return width;
    }

    private static float GetElementWidth(
        FrameworkElement element) =>
        float.IsFinite(element.Width)
            ? element.Width
            : Math.Max(48f, element.DesiredSize.X);

    private void ScheduleAutoHide()
    {
        long generation =
            Interlocked.Increment(
                ref _autoHideGeneration);
        if (!ShowAndHideAutomatically ||
            _owner is null)
        {
            return;
        }

        _ = HideAfterDelayAsync(
            new WeakReference<MediaTransportControls>(
                this),
            generation);
    }

    private static async Task HideAfterDelayAsync(
        WeakReference<MediaTransportControls> weakControls,
        long generation)
    {
        await Task.Delay(AutoHideDelay)
            .ConfigureAwait(false);
        if (!weakControls.TryGetTarget(
                out MediaTransportControls? controls))
        {
            return;
        }

        void HideIfCurrent()
        {
            if (Volatile.Read(
                    ref controls._autoHideGeneration) ==
                generation)
            {
                controls.Hide();
            }
        }

        Action<Action>? dispatcher =
            Microsoft.UI.Xaml.Input.InputSystem
                .DispatcherQueue;
        if (dispatcher is not null)
        {
            dispatcher(HideIfCurrent);
        }
        else
        {
            UIThread.Post(HideIfCurrent);
        }
    }

    private static async Task LoadThumbnailAsync(
        WeakReference<MediaTransportControls> weakControls,
        IInputStream input,
        long generation)
    {
        byte[]? encoded;
        try
        {
            encoded =
                await ReadThumbnailAsync(input)
                    .ConfigureAwait(false);
        }
        catch
        {
            encoded = null;
        }

        DispatchVisualUpdate(
            weakControls,
            controls =>
            {
                if (Volatile.Read(
                        ref controls._thumbnailGeneration) !=
                    generation)
                {
                    return;
                }

                if (encoded is { Length: > 0 })
                {
                    controls._thumbnailPreview.Source =
                        new EncodedImageSource(
                            encoded,
                            suggestedWidth: 160,
                            suggestedHeight: 90);
                    controls._thumbnailPreview.Visibility =
                        Visibility.Visible;
                }
                else
                {
                    controls._thumbnailPreview.Source =
                        null;
                    controls._thumbnailPreview.Visibility =
                        Visibility.Collapsed;
                }

                controls.Invalidate();
            });
    }

    private static void DispatchVisualUpdate(
        WeakReference<MediaTransportControls> weakControls,
        Action<MediaTransportControls> update)
    {
        void Apply()
        {
            if (weakControls.TryGetTarget(
                    out MediaTransportControls? controls))
            {
                update(controls);
            }
        }

        Action<Action>? dispatcher =
            Microsoft.UI.Xaml.Input.InputSystem
                .DispatcherQueue;
        if (dispatcher is not null)
        {
            dispatcher(Apply);
        }
        else
        {
            UIThread.Post(Apply);
        }
    }

    private static async Task<byte[]?> ReadThumbnailAsync(
        IInputStream input)
    {
        Stream stream = input.AsStream();
        byte[] buffer =
            ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            using var output = new MemoryStream();
            while (output.Length <= MaximumThumbnailBytes)
            {
                int remaining =
                    MaximumThumbnailBytes -
                    checked((int)output.Length);
                if (remaining == 0)
                {
                    int overflow =
                        await stream.ReadAsync(
                                buffer.AsMemory(0, 1))
                            .ConfigureAwait(false);
                    return overflow == 0
                        ? output.ToArray()
                        : null;
                }

                int read = await stream.ReadAsync(
                        buffer.AsMemory(
                            0,
                            Math.Min(
                                buffer.Length,
                                remaining)))
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return output.Length == 0
                        ? null
                        : output.ToArray();
                }

                output.Write(buffer, 0, read);
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

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
    private readonly Image _poster;
    private MediaPlayer _mediaPlayer;
    private bool _ownsMediaPlayer = true;
    private bool _hasPresentedVideoFrame;

    public static readonly DependencyProperty SourceProperty = Register<IMediaPlaybackSource?>(nameof(Source), null, OnSourceChanged);
    public static readonly DependencyProperty TransportControlsProperty = Register<MediaTransportControls?>(nameof(TransportControls), null, OnTransportControlsChanged);
    public static readonly DependencyProperty AreTransportControlsEnabledProperty = Register(nameof(AreTransportControlsEnabled), false, OnTransportControlsEnabledChanged);
    public static readonly DependencyProperty PosterSourceProperty = Register<ImageSource?>(nameof(PosterSource), null, OnPosterSourceChanged);
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
        _poster = new Image
        {
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        AddChild(_presenter);
        AddChild(_poster);
        AttachPlayerEvents(_mediaPlayer, attach: true);
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
    internal Image PosterImage => _poster;

    public void SetMediaPlayer(MediaPlayer mediaPlayer)
    {
        ArgumentNullException.ThrowIfNull(mediaPlayer);
        if (ReferenceEquals(_mediaPlayer, mediaPlayer))
        {
            return;
        }

        MediaPlayer previous = _mediaPlayer;
        bool disposePrevious = _ownsMediaPlayer;
        AttachPlayerEvents(previous, attach: false);
        _mediaPlayer = mediaPlayer;
        _ownsMediaPlayer = false;
        _hasPresentedVideoFrame = false;
        SetValue(MediaPlayerProperty, mediaPlayer);
        _presenter.MediaPlayer = mediaPlayer;
        AttachPlayerEvents(mediaPlayer, attach: true);
        UpdatePosterPresentation();
        TransportControls?.SetMediaPlayer(
            this,
            mediaPlayer);
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
        _poster.Measure(availableSize);
        TransportControls?.Measure(availableSize);
        return _presenter.DesiredSize;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        _presenter.Arrange(arrangeRect);
        _poster.Arrange(arrangeRect);
        TransportControls?.Arrange(arrangeRect);
    }

    public override void OnPointerPressed(
        PointerRoutedEventArgs e)
    {
        if (AreTransportControlsEnabled)
        {
            TransportControls?.NotifyInteraction();
        }
        base.OnPointerPressed(e);
    }

    public override void OnPointerMoved(
        PointerRoutedEventArgs e)
    {
        if (AreTransportControlsEnabled)
        {
            TransportControls?.NotifyInteraction();
        }
        base.OnPointerMoved(e);
    }

    private static void OnSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var element = (MediaPlayerElement)dependencyObject;
        element._hasPresentedVideoFrame = false;
        element.UpdatePosterPresentation();
        element._mediaPlayer.Source = args.NewValue as IMediaPlaybackSource;
        if (element.AutoPlay)
            element._mediaPlayer.Play();
    }

    private static void OnTransportControlsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var element = (MediaPlayerElement)dependencyObject;
        if (args.OldValue is MediaTransportControls oldControls)
        {
            oldControls.SetMediaPlayer(null, null);
            if (ReferenceEquals(oldControls.Parent, element))
            {
                element.RemoveChild(oldControls);
            }
        }
        if (args.NewValue is MediaTransportControls newControls)
        {
            newControls.SetMediaPlayer(
                element,
                element._mediaPlayer);
            if (element.AreTransportControlsEnabled)
            {
                element.AddChild(newControls);
                newControls.Show();
            }
        }
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
            controls.Show();
        }
        else if (ReferenceEquals(controls.Parent, element))
        {
            element.RemoveChild(controls);
        }

        element.InvalidateMeasure();
        element.Invalidate();
    }

    private static void OnStretchChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((MediaPlayerElement)dependencyObject).SetStretch(
            (Stretch)(args.NewValue ?? Stretch.Uniform));

    private static void OnPosterSourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var element = (MediaPlayerElement)dependencyObject;
        element._poster.Source = args.NewValue;
        element.UpdatePosterPresentation();
    }

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

    private void SetStretch(Stretch stretch)
    {
        _presenter.Stretch = stretch;
        _poster.Stretch = stretch;
    }

    private void AttachPlayerEvents(
        MediaPlayer player,
        bool attach)
    {
        if (attach)
        {
            player.SourceChanged += OnPlayerSourceChanged;
            player.ProGpuFrameAvailable +=
                OnPlayerFrameAvailable;
        }
        else
        {
            player.SourceChanged -= OnPlayerSourceChanged;
            player.ProGpuFrameAvailable -=
                OnPlayerFrameAvailable;
        }
    }

    private void OnPlayerSourceChanged(
        MediaPlayer sender,
        object args)
    {
        _hasPresentedVideoFrame = false;
        UpdatePosterPresentation();
    }

    private void OnPlayerFrameAvailable(
        object? sender,
        EventArgs args)
    {
        if (_hasPresentedVideoFrame)
        {
            return;
        }

        _hasPresentedVideoFrame = true;
        UpdatePosterPresentation();
    }

    private void UpdatePosterPresentation()
    {
        _poster.Visibility =
            PosterSource is not null &&
            !_hasPresentedVideoFrame
                ? Visibility.Visible
                : Visibility.Collapsed;
        _poster.InvalidateMeasure();
        _poster.Invalidate();
        InvalidateMeasure();
        Invalidate();
    }

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
