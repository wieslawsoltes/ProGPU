using Android.App;
using Android.Content;
using Android.Runtime;
using Android.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Fonts.Inter;
using ProGPU.Fonts.Noto;
using ProGPU.Text;
using Silk.NET.Input;
using System.Runtime.InteropServices;
using Windows.Graphics.Display;
using Windows.Storage;
using Windows.UI.Core;
using XamlWindow = Microsoft.UI.Xaml.Window;

namespace ProGPU.Android;

internal sealed class AndroidWindowHost : Java.Lang.Object, IWindowHost, Choreographer.IFrameCallback
{
    private readonly Activity _activity;
    private readonly AndroidRenderView _renderView;
    private readonly AndroidTextInputBridge _textInput;
    private readonly AndroidStoragePickerService _storagePicker;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<string> _setClipboard;
    private readonly Func<string> _getClipboard;
    private HostedWindow? _hosted;
    private Choreographer? _choreographer;
    private nint _nativeWindow;
    private nint _javaSurfaceHandle;
    private long _previousFrameNanos;
    private bool _framePending;
    private bool _resumed;
    private bool _reportedFirstFrame;
    private bool _disposed;

    public AndroidWindowHost(
        Activity activity,
        AndroidRenderView renderView,
        AndroidTextInputBridge textInput)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _renderView = renderView ?? throw new ArgumentNullException(nameof(renderView));
        _textInput = textInput ?? throw new ArgumentNullException(nameof(textInput));
        _storagePicker = new AndroidStoragePickerService(activity);

        InterFontFamily.RegisterFonts();
        NotoFontFamily.RegisterFallbacks();
        FontApi.RegisterPlatformFallbackFont(InterFontFamily.Regular);
        PopupService.DefaultFont ??= InterFontFamily.Regular;

        _setClipboard = SetClipboardText;
        _getClipboard = GetClipboardText;
        ClipboardHelper.PlatformSetText = _setClipboard;
        ClipboardHelper.PlatformGetText = _getClipboard;
        StoragePlatformServices.PickPathAsync = _storagePicker.PickPathAsync;
        StoragePlatformServices.ReadTextAsync = _storagePicker.ReadTextAsync;
        StoragePlatformServices.ReadBytesAsync = _storagePicker.ReadBytesAsync;
        StoragePlatformServices.WriteTextAsync = _storagePicker.WriteTextAsync;
        StoragePlatformServices.WriteBytesAsync = _storagePicker.WriteBytesAsync;
        StoragePlatformServices.EnumerateFilesAsync = _storagePicker.EnumerateFilesAsync;
        StoragePlatformServices.EnumerateFoldersAsync = _storagePicker.EnumerateFoldersAsync;
        StoragePlatformServices.CreateFileAsync = _storagePicker.CreateFileAsync;
        StoragePlatformServices.CreateFolderAsync = _storagePicker.CreateFolderAsync;

        _renderView.InputStateProvider = GetInputState;
        _renderView.SurfaceAvailable += OnSurfaceAvailable;
        _renderView.SurfaceUnavailable += OnSurfaceUnavailable;
        _renderView.MetricsChanged += OnMetricsChanged;
        _choreographer = Choreographer.Instance;
    }

    public void Activate(XamlWindow window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(window);
        if (_hosted != null)
        {
            if (ReferenceEquals(_hosted.Window, window)) return;
            throw new NotSupportedException("The Android host currently supports one top-level Window; popups remain compositor layers.");
        }

        _hosted = new HostedWindow(window);
        window.ConfigureInputPane(_textInput.TryShow, _textInput.TryHide);
        TryInitializeRenderer();
        if (_hosted.Context == null)
            throw new InvalidOperationException(
                "The Android Window can only activate while its SurfaceView surface is available.");
        AndroidRenderMetrics metrics = _renderView.Metrics;
        window.NotifyHostInsetsChanged(metrics.SafeAreaInsets, metrics.InputPaneOccludedRect);
        StartFrameLoop();
    }

    public void Close(XamlWindow window)
    {
        if (_hosted is not { } hosted || !ReferenceEquals(hosted.Window, window)) return;
        StopFrameLoop();
        DetachTextInput(hosted.Window);
        hosted.Window.ConfigureInputPane(null, null);
        hosted.Window.ShutdownExternalRenderer();
        DisposeRendererContext(hosted);
        ReleaseNativeWindow();
        _hosted = null;
        _completion.TrySetResult();
    }

    public void Hide(XamlWindow window)
    {
        if (_hosted is not { } hosted || !ReferenceEquals(hosted.Window, window)) return;
        hosted.IsVisible = false;
        StopFrameLoop();
        window.NotifyHostVisibilityChanged(false);
        window.NotifyHostActivationChanged(WindowActivationState.Deactivated);
    }

    public Task RunAsync(CancellationToken cancellationToken = default) =>
        _completion.Task.WaitAsync(cancellationToken);

    public void Resume()
    {
        _resumed = true;
        if (_hosted is not { } hosted) return;
        hosted.IsVisible = true;
        _previousFrameNanos = 0;
        hosted.Window.NotifyHostVisibilityChanged(true);
        hosted.Window.NotifyHostActivationChanged(WindowActivationState.CodeActivated);
        StartFrameLoop();
    }

    public void Pause()
    {
        _resumed = false;
        StopFrameLoop();
        if (_hosted is not { } hosted) return;
        hosted.Window.NotifyHostActivationChanged(WindowActivationState.Deactivated);
    }

    public bool HandleActivityResult(int requestCode, Result resultCode, Intent? data) =>
        _storagePicker.HandleActivityResult(requestCode, resultCode, data);

    public bool HandleKeyEvent(KeyEvent keyEvent)
    {
        ArgumentNullException.ThrowIfNull(keyEvent);
        if (_hosted?.Window.InputState is not { } inputState) return false;
        if (keyEvent.KeyCode == Keycode.Back)
        {
            // Let Activity observe the down event so its back dispatcher can track the
            // gesture. On release, a WinUI handler gets first refusal; an unhandled
            // release returns to Activity and preserves Android's normal navigation.
            if (keyEvent.Action == KeyEventActions.Down) return false;
            return keyEvent.Action == KeyEventActions.Up && HandleBackRequested();
        }

        Key key = MapKey(keyEvent.KeyCode);
        if (key == Key.Unknown) return false;
        bool textOwnsKey = _textInput.IsActive &&
            key is not (Key.Tab or Key.Escape or Key.F1 or Key.F2 or Key.F3 or Key.F4 or Key.F5 or Key.F6 or
                Key.F7 or Key.F8 or Key.F9 or Key.F10 or Key.F11 or Key.F12);
        if (textOwnsKey) return false;

        InputSystem.Current = inputState;
        if (keyEvent.Action == KeyEventActions.Down)
        {
            InputSystem.InjectKeyDown(key);
            return true;
        }
        if (keyEvent.Action == KeyEventActions.Up)
        {
            InputSystem.InjectKeyUp(key);
            return true;
        }
        return false;
    }

    public bool HandleBackRequested()
    {
        if (_hosted is null) return false;
        return SystemNavigationManager.GetForCurrentView().NotifyBackRequested();
    }

    public void DoFrame(long frameTimeNanos)
    {
        _framePending = false;
        if (_disposed || !_resumed || _nativeWindow == 0 ||
            _hosted is not { IsVisible: true, Context: not null } hosted) return;

        double delta = _previousFrameNanos == 0
            ? 0d
            : Math.Clamp((frameTimeNanos - _previousFrameNanos) / 1_000_000_000d, 0d, 0.25d);
        _previousFrameNanos = frameTimeNanos;
        AndroidRenderMetrics metrics = _renderView.Metrics;
        hosted.Window.NotifyHostInsetsChanged(metrics.SafeAreaInsets, metrics.InputPaneOccludedRect);
        if (hosted.Window.InputState is { } inputState) InputSystem.Current = inputState;
        hosted.Window.RenderExternalFrame(delta, metrics.Width, metrics.Height, metrics.DpiScale);
        if (!_reportedFirstFrame)
        {
            _reportedFirstFrame = true;
            WindowFrameMetrics frame = hosted.Window.FrameMetrics;
            ProGPU.Scene.CompositorMetrics compositor = hosted.Window.Compositor?.Metrics ?? default;
            FrameworkElement? content = hosted.Window.Content;
            global::Android.Util.Log.Info(
                "ProGPU.Android",
                $"First frame: adapter='{hosted.Context.AdapterName}', backend={hosted.Context.AdapterBackendType}, " +
                $"format={hosted.Context.SwapChainFormat}, " +
                $"physical={metrics.Width}x{metrics.Height}, scale={metrics.DpiScale:F3}, " +
                $"surface={frame.SurfaceAcquireTimeMs:F3}ms, compositor={frame.CompositorTimeMs:F3}ms, " +
                $"present={frame.PresentTimeMs:F3}ms, total={frame.TotalTimeMs:F3}ms, " +
                $"draws={compositor.DrawCallsCount}, vectors={compositor.VectorVerticesCount}, " +
                $"text={compositor.TextVerticesCount}, content={content?.Size.X:F1}x{content?.Size.Y:F1}.");
        }
        StartFrameLoop();
    }

    private void OnSurfaceAvailable(Surface surface, int width, int height)
    {
        if (_disposed || !surface.IsValid) return;
        nint surfaceHandle = surface.Handle;
        if (_nativeWindow != 0 && _javaSurfaceHandle == surfaceHandle)
        {
            StartFrameLoop();
            return;
        }

        if (_nativeWindow != 0 && _hosted is { Context: not null } hosted)
        {
            StopFrameLoop();
            hosted.Context.DetachExternalNativePresentationSurface();
            ReleaseNativeWindow();
        }

        _javaSurfaceHandle = surfaceHandle;
        TryInitializeRenderer();
        StartFrameLoop();
    }

    private void OnSurfaceUnavailable()
    {
        StopFrameLoop();
        if (_hosted is { Context: not null } hosted)
        {
            hosted.Context.DetachExternalNativePresentationSurface();
        }
        ReleaseNativeWindow();
    }

    private void TryInitializeRenderer()
    {
        if (_disposed || _hosted is not { } hosted || _nativeWindow != 0) return;
        Surface? surface = _renderView.Holder?.Surface;
        if (surface is not { IsValid: true }) return;

        nint nativeWindow = AndroidNativeWindow.Acquire(surface);
        if (hosted.Context is { } existingContext)
        {
            try
            {
                AndroidRenderMetrics replacementMetrics = _renderView.Metrics;
                if (hosted.DawnContext is { } existingDawn)
                {
                    using DawnNativeWindowSource replacementSource =
                        DawnNativeWindowSource.CreateAndroid(nativeWindow);
                    existingDawn.AttachNativePresentation(
                        replacementSource,
                        replacementMetrics.Width,
                        replacementMetrics.Height);
                }
                else
                {
                    existingContext.AttachAndroidNativeWindow(
                        nativeWindow,
                        replacementMetrics.Width,
                        replacementMetrics.Height);
                }
                _nativeWindow = nativeWindow;
                _javaSurfaceHandle = surface.Handle;
                PublishDisplayInformation(replacementMetrics);
                hosted.Window.NotifyHostInsetsChanged(
                    replacementMetrics.SafeAreaInsets,
                    replacementMetrics.InputPaneOccludedRect);
                return;
            }
            catch
            {
                AndroidNativeWindow.Release(nativeWindow);
                throw;
            }
        }

        WgpuContext? context = null;
        DawnGpuContext? dawnContext = null;
        try
        {
            AndroidRenderMetrics metrics = _renderView.Metrics;
            if (DawnGpuContext.IsNativeLibraryAvailable())
            {
                using DawnNativeWindowSource source =
                    DawnNativeWindowSource.CreateAndroid(nativeWindow);
                dawnContext =
                    DawnGpuContext.CreateNativePresentation(source);
                dawnContext.AttachNativePresentation(
                    source,
                    metrics.Width,
                    metrics.Height);
                context = dawnContext.Context;
            }
            else
            {
                context = new WgpuContext { VSync = true };
                context.InitializeAndroidNativeWindow(
                    nativeWindow,
                    metrics.Width,
                    metrics.Height);
                global::Android.Util.Log.Warn(
                    "ProGPU.Android",
                    "libwebgpu_dawn.so is unavailable; using the wgpu-native UI fallback. " +
                    "AHardwareBuffer media frames cannot be imported by this fallback device.");
            }
            hosted.Window.InitializeExternalRenderer(context, metrics.DpiScale);
            hosted.Context = context;
            hosted.DawnContext = dawnContext;
            _nativeWindow = nativeWindow;
            _javaSurfaceHandle = surface.Handle;
            if (hosted.Window.InputState is { } inputState)
            {
                _textInput.Attach(inputState);
                InputSystem.Current = inputState;
            }
            PublishDisplayInformation(metrics);
            hosted.Window.NotifyHostInsetsChanged(metrics.SafeAreaInsets, metrics.InputPaneOccludedRect);
        }
        catch
        {
            dawnContext?.Dispose();
            if (dawnContext is null)
            {
                context?.Dispose();
            }
            AndroidNativeWindow.Release(nativeWindow);
            throw;
        }
    }

    private void OnMetricsChanged(AndroidRenderMetrics metrics)
    {
        if (_hosted is not { } hosted) return;
        PublishDisplayInformation(metrics);
        hosted.Window.NotifyHostInsetsChanged(metrics.SafeAreaInsets, metrics.InputPaneOccludedRect);
    }

    private void PublishDisplayInformation(AndroidRenderMetrics renderMetrics)
    {
        global::Android.Util.DisplayMetrics? displayMetrics = _activity.Resources?.DisplayMetrics;
        var configuration = _activity.Resources?.Configuration;
        bool currentPortrait = configuration?.Orientation == global::Android.Content.Res.Orientation.Portrait;
        SurfaceOrientation rotation = _activity.Display.Rotation;
        bool nativePortrait = rotation is SurfaceOrientation.Rotation0 or SurfaceOrientation.Rotation180
            ? currentPortrait
            : !currentPortrait;
        bool flipped = rotation is SurfaceOrientation.Rotation180 or SurfaceOrientation.Rotation270;
        DisplayOrientations currentOrientation = currentPortrait
            ? flipped ? DisplayOrientations.PortraitFlipped : DisplayOrientations.Portrait
            : flipped ? DisplayOrientations.LandscapeFlipped : DisplayOrientations.Landscape;

        global::Android.Views.Display.Mode? displayMode = _activity.Display.GetMode();
        int physicalWidth = displayMode?.PhysicalWidth ??
            displayMetrics?.WidthPixels ?? (int)renderMetrics.Width;
        int physicalHeight = displayMode?.PhysicalHeight ??
            displayMetrics?.HeightPixels ?? (int)renderMetrics.Height;
        uint screenWidth = checked((uint)Math.Max(1, physicalWidth));
        uint screenHeight = checked((uint)Math.Max(1, physicalHeight));
        double? diagonal = null;
        float xdpi = displayMetrics?.Xdpi ?? 0f;
        float ydpi = displayMetrics?.Ydpi ?? 0f;
        if (float.IsFinite(xdpi) && xdpi > 0f && float.IsFinite(ydpi) && ydpi > 0f)
        {
            double widthInches = screenWidth / xdpi;
            double heightInches = screenHeight / ydpi;
            double candidate = Math.Sqrt(widthInches * widthInches + heightInches * heightInches);
            if (double.IsFinite(candidate) && candidate > 0d) diagonal = candidate;
        }

        DisplayInformation.NotifyHostMetricsChanged(new DisplayInformationMetrics(
            currentOrientation,
            nativePortrait ? DisplayOrientations.Portrait : DisplayOrientations.Landscape,
            96f * renderMetrics.DpiScale,
            renderMetrics.DpiScale,
            screenWidth,
            screenHeight,
            diagonal));
    }

    private void StartFrameLoop()
    {
        if (_disposed || _framePending || !_resumed || _nativeWindow == 0 ||
            _hosted is not { IsVisible: true, Context: not null } || _choreographer == null) return;
        _framePending = true;
        _choreographer.PostFrameCallback(this);
    }

    private void StopFrameLoop()
    {
        if (_framePending) _choreographer?.RemoveFrameCallback(this);
        _framePending = false;
        _previousFrameNanos = 0;
    }

    private WindowInputState? GetInputState() => _hosted?.Window.InputState;

    private void DetachTextInput(XamlWindow window)
    {
        if (window.InputState is { } inputState) _textInput.Detach(inputState);
    }

    private static void DisposeRendererContext(HostedWindow hosted)
    {
        WgpuContext? context = hosted.Context;
        hosted.Context = null;
        DawnGpuContext? dawn = hosted.DawnContext;
        hosted.DawnContext = null;
        if (dawn is not null)
        {
            dawn.Dispose();
        }
        else
        {
            context?.Dispose();
        }
    }

    private void ReleaseNativeWindow()
    {
        nint nativeWindow = _nativeWindow;
        _nativeWindow = 0;
        _javaSurfaceHandle = 0;
        if (nativeWindow != 0) AndroidNativeWindow.Release(nativeWindow);
    }

    private void SetClipboardText(string text)
    {
        var clipboard = (ClipboardManager?)_activity.GetSystemService(Context.ClipboardService);
        if (clipboard != null) clipboard.PrimaryClip = ClipData.NewPlainText("ProGPU", text ?? string.Empty);
    }

    private string GetClipboardText()
    {
        var clipboard = (ClipboardManager?)_activity.GetSystemService(Context.ClipboardService);
        ClipData? clip = clipboard?.PrimaryClip;
        if (clip == null || clip.ItemCount == 0) return string.Empty;
        return clip.GetItemAt(0)?.CoerceToText(_activity)?.ToString() ?? string.Empty;
    }

    private static Key MapKey(Keycode code)
    {
        if (code is >= Keycode.A and <= Keycode.Z)
            return Key.A + (code - Keycode.A);
        if (code is >= Keycode.Num0 and <= Keycode.Num9)
            return Key.Number0 + (code - Keycode.Num0);
        if (code is >= Keycode.Numpad0 and <= Keycode.Numpad9)
            return Key.Keypad0 + (code - Keycode.Numpad0);
        if (code is >= Keycode.F1 and <= Keycode.F12)
            return Key.F1 + (code - Keycode.F1);

        return code switch
        {
            Keycode.Space => Key.Space,
            Keycode.Apostrophe => Key.Apostrophe,
            Keycode.Comma => Key.Comma,
            Keycode.Minus => Key.Minus,
            Keycode.Period => Key.Period,
            Keycode.Slash => Key.Slash,
            Keycode.Semicolon => Key.Semicolon,
            Keycode.Equals or Keycode.Plus => Key.Equal,
            Keycode.LeftBracket => Key.LeftBracket,
            Keycode.Backslash => Key.BackSlash,
            Keycode.RightBracket => Key.RightBracket,
            Keycode.Grave => Key.GraveAccent,
            Keycode.Escape or Keycode.Back => Key.Escape,
            Keycode.Enter => Key.Enter,
            Keycode.Tab => Key.Tab,
            Keycode.Del => Key.Backspace,
            Keycode.Insert => Key.Insert,
            Keycode.ForwardDel => Key.Delete,
            Keycode.DpadRight => Key.Right,
            Keycode.DpadLeft => Key.Left,
            Keycode.DpadDown => Key.Down,
            Keycode.DpadUp => Key.Up,
            Keycode.PageUp => Key.PageUp,
            Keycode.PageDown => Key.PageDown,
            Keycode.MoveHome => Key.Home,
            Keycode.MoveEnd => Key.End,
            Keycode.CapsLock => Key.CapsLock,
            Keycode.ScrollLock => Key.ScrollLock,
            Keycode.NumLock => Key.NumLock,
            Keycode.Print => Key.PrintScreen,
            Keycode.Break => Key.Pause,
            Keycode.NumpadDot => Key.KeypadDecimal,
            Keycode.NumpadDivide => Key.KeypadDivide,
            Keycode.NumpadMultiply => Key.KeypadMultiply,
            Keycode.NumpadSubtract => Key.KeypadSubtract,
            Keycode.NumpadAdd => Key.KeypadAdd,
            Keycode.NumpadEnter => Key.KeypadEnter,
            Keycode.NumpadEquals => Key.KeypadEqual,
            Keycode.ShiftLeft => Key.ShiftLeft,
            Keycode.CtrlLeft => Key.ControlLeft,
            Keycode.AltLeft => Key.AltLeft,
            Keycode.MetaLeft => Key.SuperLeft,
            Keycode.ShiftRight => Key.ShiftRight,
            Keycode.CtrlRight => Key.ControlRight,
            Keycode.AltRight => Key.AltRight,
            Keycode.MetaRight => Key.SuperRight,
            Keycode.Menu => Key.Menu,
            _ => Key.Unknown
        };
    }

    public new void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hosted is { } hosted) Close(hosted.Window);
        StopFrameLoop();
        _renderView.SurfaceAvailable -= OnSurfaceAvailable;
        _renderView.SurfaceUnavailable -= OnSurfaceUnavailable;
        _renderView.MetricsChanged -= OnMetricsChanged;
        _renderView.InputStateProvider = null;
        ReleaseNativeWindow();
        if (ReferenceEquals(ClipboardHelper.PlatformSetText, _setClipboard)) ClipboardHelper.PlatformSetText = null;
        if (ReferenceEquals(ClipboardHelper.PlatformGetText, _getClipboard)) ClipboardHelper.PlatformGetText = null;
        if (StoragePlatformServices.PickPathAsync?.Target == _storagePicker)
            StoragePlatformServices.PickPathAsync = null;
        if (StoragePlatformServices.ReadTextAsync?.Target == _storagePicker)
            StoragePlatformServices.ReadTextAsync = null;
        if (StoragePlatformServices.ReadBytesAsync?.Target == _storagePicker)
            StoragePlatformServices.ReadBytesAsync = null;
        if (StoragePlatformServices.WriteTextAsync?.Target == _storagePicker)
            StoragePlatformServices.WriteTextAsync = null;
        if (StoragePlatformServices.WriteBytesAsync?.Target == _storagePicker)
            StoragePlatformServices.WriteBytesAsync = null;
        if (StoragePlatformServices.EnumerateFilesAsync?.Target == _storagePicker)
            StoragePlatformServices.EnumerateFilesAsync = null;
        if (StoragePlatformServices.EnumerateFoldersAsync?.Target == _storagePicker)
            StoragePlatformServices.EnumerateFoldersAsync = null;
        if (StoragePlatformServices.CreateFileAsync?.Target == _storagePicker)
            StoragePlatformServices.CreateFileAsync = null;
        if (StoragePlatformServices.CreateFolderAsync?.Target == _storagePicker)
            StoragePlatformServices.CreateFolderAsync = null;
        _storagePicker.Dispose();
        _textInput.Dispose();
        _completion.TrySetResult();
        base.Dispose();
    }

    private sealed class HostedWindow(XamlWindow window)
    {
        public XamlWindow Window { get; } = window;
        public WgpuContext? Context { get; set; }
        public DawnGpuContext? DawnContext { get; set; }
        public bool IsVisible { get; set; } = true;
    }

    private static class AndroidNativeWindow
    {
        public static nint Acquire(Surface surface)
        {
            nint result = ANativeWindowFromSurface(JNIEnv.Handle, surface.Handle);
            if (result == 0) throw new InvalidOperationException("ANativeWindow_fromSurface returned a null Android native window.");
            return result;
        }

        public static void Release(nint nativeWindow)
        {
            if (nativeWindow != 0) ANativeWindowRelease(nativeWindow);
        }

        [DllImport("android", EntryPoint = "ANativeWindow_fromSurface")]
        private static extern nint ANativeWindowFromSurface(nint environment, nint surface);

        [DllImport("android", EntryPoint = "ANativeWindow_release")]
        private static extern void ANativeWindowRelease(nint nativeWindow);
    }
}
