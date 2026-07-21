using System.Buffers.Binary;
using System.Runtime.InteropServices.JavaScript;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Fonts.Inter;
using ProGPU.Fonts.Noto;
using ProGPU.Text;

namespace ProGPU.Browser;

/// <summary>
/// Drives ordinary ProGPU windows from requestAnimationFrame and a browser canvas.
/// WebGPU commands still flow exclusively through <see cref="BrowserWebGpuApi"/>.
/// </summary>
public sealed partial class BrowserWindowHost : IWindowHost, IDisposable
{
    private readonly BrowserGpuCapabilities _capabilities;
    private readonly List<HostedWindow> _windows = [];
    private bool _disposed;

    public BrowserWindowHost(BrowserGpuCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!capabilities.IsSupported) throw new PlatformNotSupportedException("WebGPU is unavailable.");
        _capabilities = capabilities;
        InterFontFamily.RegisterFonts();
        NotoFontFamily.RegisterFallbacks();
        var fallbackFont = InterFontFamily.Regular;
        FontApi.RegisterPlatformFallbackFont(fallbackFont);
        PopupService.DefaultFont ??= fallbackFont;
        ClipboardHelper.PlatformSetText = SetClipboardText;
        ClipboardHelper.PlatformGetText = GetClipboardText;
        ClipboardHelper.PlatformSetRichText = SetClipboardRichText;
        ClipboardHelper.PlatformGetRichText = GetClipboardRichText;
        BrowserStorageServices.Initialize();
    }

    public void Activate(Window window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windows.Any(item => ReferenceEquals(item.Window, window))) return;
        if (_windows.Count != 0)
            throw new NotSupportedException("The standard browser canvas host currently supports one top-level Window; popups remain compositor layers.");

        var metrics = ReadCanvasMetrics();
        var gpu = BrowserGpuContext.Create(_capabilities);
        try
        {
            window.InitializeExternalRenderer(gpu.Context, metrics.DpiScale);
            if (window.InputState is { } inputState) BrowserInputDispatcher.Attach(inputState);
            _windows.Add(new HostedWindow(window, gpu));
        }
        catch
        {
            gpu.Dispose();
            throw;
        }
    }

    public void Close(Window window)
    {
        var index = _windows.FindIndex(item => ReferenceEquals(item.Window, window));
        if (index < 0) return;
        var hosted = _windows[index];
        _windows.RemoveAt(index);
        if (hosted.Window.InputState is { } inputState) BrowserInputDispatcher.Detach(inputState);
        hosted.Window.ShutdownExternalRenderer();
        hosted.Gpu.Dispose();
    }

    public void Hide(Window window)
    {
        var hosted = _windows.FirstOrDefault(item => ReferenceEquals(item.Window, window));
        if (hosted != null) hosted.IsVisible = false;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        double previousTimestamp = 0;
        while (_windows.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool vsync = false;
            for (var index = 0; index < _windows.Count; index++)
            {
                var hosted = _windows[index];
                if (hosted.IsVisible && hosted.Gpu.Context.VSync)
                {
                    vsync = true;
                    break;
                }
            }

            var timestamp = await NextAnimationFrameAsync(vsync).WaitAsync(cancellationToken).ConfigureAwait(false);
            var delta = previousTimestamp == 0 ? 0 : Math.Clamp((timestamp - previousTimestamp) / 1000.0, 0, 0.25);
            previousTimestamp = timestamp;
            var metrics = ReadCanvasMetrics();
            for (var index = 0; index < _windows.Count; index++)
            {
                var hosted = _windows[index];
                if (hosted.IsVisible)
                {
                    if (hosted.Window.InputState is { } inputState) BrowserInputDispatcher.Drain(inputState);
                    hosted.Window.RenderExternalFrame(delta, metrics.Width, metrics.Height, metrics.DpiScale);
                }
            }

            var counters = BrowserGpuRuntime.Counters;
            UpdateCounters(counters.Frames, counters.CommandDispatches, counters.CommandBytes);
        }
    }

    private static unsafe CanvasMetrics ReadCanvasMetrics()
    {
        Span<byte> bytes = stackalloc byte[16];
        fixed (byte* pointer = bytes) WriteCanvasMetrics((nint)pointer);
        var width = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var dpiScale = BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]));
        return new CanvasMetrics(Math.Max(1u, width), Math.Max(1u, height), (float)(double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1));
    }

    public void Dispose()
    {
        if (_disposed) return;
        while (_windows.Count != 0) Close(_windows[^1].Window);
        ClipboardHelper.PlatformSetText = null;
        ClipboardHelper.PlatformGetText = null;
        ClipboardHelper.PlatformSetRichText = null;
        ClipboardHelper.PlatformGetRichText = null;
        _disposed = true;
    }

    [JSImport("nextAnimationFrame", "progpu-browser")]
    private static partial Task<double> NextAnimationFrameAsync(bool vsync);

    [JSImport("writeCanvasMetrics", "progpu-browser")]
    private static partial void WriteCanvasMetrics(nint destination);

    [JSImport("updateCounters", "progpu-browser")]
    private static partial void UpdateCounters(double frames, double dispatches, double commandBytes);

    [JSImport("setClipboardText", "progpu-browser")]
    private static partial void SetClipboardText(string text);

    [JSImport("getClipboardText", "progpu-browser")]
    private static partial string GetClipboardText();

    [JSImport("setClipboardRichText", "progpu-browser")]
    private static partial void SetClipboardRichTextNative(string plainText, string rtf, string html);

    [JSImport("getClipboardRtf", "progpu-browser")]
    private static partial string GetClipboardRtf();

    [JSImport("getClipboardHtml", "progpu-browser")]
    private static partial string GetClipboardHtml();

    private static void SetClipboardRichText(RichClipboardPayload payload) =>
        SetClipboardRichTextNative(payload.PlainText, payload.Rtf, payload.Html);

    private static RichClipboardPayload? GetClipboardRichText()
    {
        string rtf = GetClipboardRtf();
        string html = GetClipboardHtml();
        return rtf.Length == 0 && html.Length == 0
            ? null
            : new RichClipboardPayload(GetClipboardText(), rtf, html);
    }

    private sealed class HostedWindow(Window window, BrowserGpuContext gpu)
    {
        public Window Window { get; } = window;
        public BrowserGpuContext Gpu { get; } = gpu;
        public bool IsVisible { get; set; } = true;
    }

    private readonly record struct CanvasMetrics(uint Width, uint Height, float DpiScale);

}
