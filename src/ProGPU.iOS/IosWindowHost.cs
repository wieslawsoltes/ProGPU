using CoreAnimation;
using Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Backend;
using ProGPU.Fonts.Inter;
using ProGPU.Fonts.Noto;
using ProGPU.Text;
using UIKit;

namespace ProGPU.iOS;

internal sealed class IosWindowHost : IWindowHost, IDisposable
{
    private readonly MetalRenderView _renderView;
    private readonly UIViewController _controller;
    private readonly UIScreen _screen;
    private readonly IosStoragePickerService _storagePicker;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HostedWindow? _hosted;
    private CADisplayLink? _displayLink;
    private IosTextInputBridge? _textInput;
    private double _previousTimestamp;
    private Windows.Foundation.Rect _inputPaneOccludedRect;
    private bool _disposed;

    public IosWindowHost(MetalRenderView renderView, UIViewController controller, UIScreen screen)
    {
        _renderView = renderView;
        _controller = controller;
        _screen = screen;
        InterFontFamily.RegisterFonts();
        NotoFontFamily.RegisterFallbacks();
        FontApi.RegisterPlatformFallbackFont(InterFontFamily.Regular);
        PopupService.DefaultFont ??= InterFontFamily.Regular;
        ClipboardHelper.PlatformSetText = static text => UIPasteboard.General.String = text;
        ClipboardHelper.PlatformGetText = static () => UIPasteboard.General.String ?? string.Empty;
        _storagePicker = new IosStoragePickerService(controller);
        StoragePlatformServices.PickPathAsync = _storagePicker.PickPathAsync;
        StoragePlatformServices.WriteTextAsync = _storagePicker.WriteTextAsync;
        StoragePlatformServices.WriteBytesAsync = _storagePicker.WriteBytesAsync;
    }

    public void Activate(Window window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hosted != null)
        {
            if (ReferenceEquals(_hosted.Window, window)) return;
            throw new NotSupportedException("The iPhone host currently supports one top-level Window; popups remain compositor layers.");
        }

        _controller.View!.LayoutIfNeeded();
        MetalViewMetrics metrics = _renderView.Metrics;
        var context = new WgpuContext { VSync = true };
        try
        {
            context.InitializeMetalLayer(_renderView.MetalLayerHandle, metrics.Width, metrics.Height);
            window.InitializeExternalRenderer(context, metrics.DpiScale);
            var textInput = new IosTextInputBridge(_controller);
            if (window.InputState is { } inputState)
            {
                textInput.SetInputState(inputState);
                InputSystem.Current = inputState;
            }
            textInput.OccludedRectChanged += OnInputPaneOccludedRectChanged;
            _textInput = textInput;
            _hosted = new HostedWindow(window, context);
            window.ConfigureInputPane(textInput.TryShow, textInput.TryHide);
            window.NotifyHostInsetsChanged(metrics.SafeAreaInsets, _inputPaneOccludedRect);
            StartDisplayLink();
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    public void Close(Window window)
    {
        if (_hosted is not { } hosted || !ReferenceEquals(hosted.Window, window)) return;
        StopDisplayLink();
        if (hosted.Window.InputState is { } inputState) _textInput?.Detach(inputState);
        if (_textInput != null)
        {
            _textInput.OccludedRectChanged -= OnInputPaneOccludedRectChanged;
            _textInput.Dispose();
        }
        _textInput = null;
        hosted.Window.ConfigureInputPane(null, null);
        hosted.Window.ShutdownExternalRenderer();
        hosted.Context.Dispose();
        _hosted = null;
        _completion.TrySetResult();
    }

    public void Hide(Window window)
    {
        if (_hosted is not { } hosted || !ReferenceEquals(hosted.Window, window)) return;
        hosted.IsVisible = false;
        if (_displayLink != null) _displayLink.Paused = true;
        window.NotifyHostVisibilityChanged(false);
        window.NotifyHostActivationChanged(WindowActivationState.Deactivated);
    }

    public Task RunAsync(CancellationToken cancellationToken = default) =>
        _completion.Task.WaitAsync(cancellationToken);

    public void Pause()
    {
        if (_hosted is not { } hosted) return;
        if (_displayLink != null) _displayLink.Paused = true;
        hosted.Window.NotifyHostActivationChanged(WindowActivationState.Deactivated);
    }

    public void Resume()
    {
        if (_hosted is not { } hosted) return;
        hosted.IsVisible = true;
        _previousTimestamp = 0d;
        if (_displayLink != null) _displayLink.Paused = false;
        hosted.Window.NotifyHostVisibilityChanged(true);
        hosted.Window.NotifyHostActivationChanged(WindowActivationState.CodeActivated);
    }

    private void StartDisplayLink()
    {
        if (_displayLink != null) return;
        _displayLink = CADisplayLink.Create(RenderFrame);
        float maximum = Math.Max(1f, (float)_screen.MaximumFramesPerSecond);
        if (OperatingSystem.IsIOSVersionAtLeast(15))
        {
            _displayLink.PreferredFrameRateRange = CAFrameRateRange.Create(30f, maximum, maximum);
        }
        else
        {
            _displayLink.PreferredFramesPerSecond = (nint)maximum;
        }
        _displayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
    }

    private void StopDisplayLink()
    {
        _displayLink?.Invalidate();
        _displayLink?.Dispose();
        _displayLink = null;
        _previousTimestamp = 0d;
    }

    private void RenderFrame()
    {
        if (_displayLink == null || _hosted is not { IsVisible: true } hosted) return;
        double timestamp = _displayLink.Timestamp;
        double delta = _previousTimestamp == 0d ? 0d : Math.Clamp(timestamp - _previousTimestamp, 0d, 0.25d);
        _previousTimestamp = timestamp;
        MetalViewMetrics metrics = _renderView.Metrics;
        hosted.Window.NotifyHostInsetsChanged(metrics.SafeAreaInsets, _inputPaneOccludedRect);
        if (hosted.Window.InputState is { } inputState) InputSystem.Current = inputState;
        hosted.Window.RenderExternalFrame(delta, metrics.Width, metrics.Height, metrics.DpiScale);
    }

    private void OnInputPaneOccludedRectChanged(Windows.Foundation.Rect rect)
    {
        _inputPaneOccludedRect = rect;
        if (_hosted is not { } hosted) return;
        hosted.Window.NotifyHostInsetsChanged(_renderView.Metrics.SafeAreaInsets, rect);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_hosted is { } hosted) Close(hosted.Window);
        StopDisplayLink();
        ClipboardHelper.PlatformSetText = null;
        ClipboardHelper.PlatformGetText = null;
        if (StoragePlatformServices.PickPathAsync?.Target == _storagePicker)
            StoragePlatformServices.PickPathAsync = null;
        if (StoragePlatformServices.WriteTextAsync?.Target == _storagePicker)
            StoragePlatformServices.WriteTextAsync = null;
        if (StoragePlatformServices.WriteBytesAsync?.Target == _storagePicker)
            StoragePlatformServices.WriteBytesAsync = null;
        _storagePicker.Dispose();
        _completion.TrySetResult();
        _disposed = true;
    }

    private sealed class HostedWindow(Window window, WgpuContext context)
    {
        public Window Window { get; } = window;
        public WgpuContext Context { get; } = context;
        public bool IsVisible { get; set; } = true;
    }
}
