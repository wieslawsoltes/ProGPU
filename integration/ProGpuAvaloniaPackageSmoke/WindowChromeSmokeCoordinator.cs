using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.ProGpu;
using Avalonia.SilkNet;
using Avalonia.Threading;
using ProGPU.Backend;
using ProGpuCompositorMetrics = ProGPU.Scene.CompositorMetrics;

namespace ProGpuAvaloniaPackageSmoke;

internal sealed class WindowChromeSmokeCoordinator
{
    private readonly IClassicDesktopStyleApplicationLifetime
        _desktop;
    private readonly string? _outputPath;
    private readonly int _targetFrames;
    private readonly bool _requireRetainedCompositor;
    private readonly SmokeWindow _owner;
    private SmokeWindow? _owned;
    private int _frameCount;
    private bool _ownedValidated;
    private bool _completionScheduled;
    private ProGpuCompositorMetrics _lastMetrics;

    internal WindowChromeSmokeCoordinator(
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop ??
            throw new ArgumentNullException(nameof(desktop));
        _outputPath = ReadOptionalPath(
            "PROGPU_PACKAGE_SMOKE_OUTPUT");
        _targetFrames = ReadPositiveInt(
            "PROGPU_PACKAGE_SMOKE_FRAMES");
        _requireRetainedCompositor = ReadBoolean(
            "PROGPU_PACKAGE_SMOKE_REQUIRE_RETAINED");
        _owner = new SmokeWindow(standalone: false)
        {
            Title =
                "ProGPU package smoke — native window chrome",
            Width = 520,
            Height = 320,
            Background = Brushes.Transparent,
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = 44,
            TransparencyLevelHint =
            [
                WindowTransparencyLevel.Mica,
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.Transparent
            ]
        };
        _owner.Opened += OnOwnerOpened;
    }

    internal void Start()
    {
        _desktop.MainWindow = _owner;
        ProGpuRenderingDiagnostics.FrameRendered +=
            OnFrameRendered;
        _owner.Show();
    }

    private void OnFrameRendered(
        ProGpuCompositorMetrics metrics)
    {
        _lastMetrics = metrics;
        _frameCount++;
        TryScheduleCompletion();
    }

    private void OnOwnerOpened(
        object? sender,
        EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(
            ValidateOwnerAndOpenChild,
            DispatcherPriority.Background);
    }

    private void ValidateOwnerAndOpenChild()
    {
        try
        {
            WindowImpl owner =
                RequireWindowImpl(_owner);
            ValidateOwner(owner);

            _owned = new SmokeWindow(standalone: false)
            {
                Title =
                    "ProGPU package smoke — owned window",
                Width = 320,
                Height = 220,
                ShowInTaskbar = false
            };
            _owned.Opened += OnOwnedOpened;
            _owned.Show(_owner);
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void OnOwnedOpened(
        object? sender,
        EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(
            ValidateOwnedWindow,
            DispatcherPriority.Background);
    }

    private void ValidateOwnedWindow()
    {
        try
        {
            WindowImpl owner =
                RequireWindowImpl(_owner);
            WindowImpl owned =
                RequireWindowImpl(_owned!);
            IPlatformHandle ownerHandle =
                owner.Handle ??
                throw new InvalidOperationException(
                    "Owner did not expose a native handle.");
            NativeWindowHandle nativeParent =
                owned.NativeParentHandle;
            if (!nativeParent.IsValid ||
                nativeParent.Handle != ownerHandle.Handle)
            {
                throw new InvalidOperationException(
                    "Owned window did not retain the owner's native handle.");
            }

            _ownedValidated = true;
            TryScheduleCompletion();
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void TryScheduleCompletion()
    {
        if (!_ownedValidated ||
            _frameCount < _targetFrames ||
            _completionScheduled)
        {
            return;
        }

        _completionScheduled = true;
        Dispatcher.UIThread.Post(
            Complete,
            DispatcherPriority.Background);
    }

    private void Complete()
    {
        WindowImpl owner =
            RequireWindowImpl(_owner);
        NativeWindowHandle nativeParent =
            RequireWindowImpl(_owned!).NativeParentHandle;
        bool renderingPassed =
            _lastMetrics.DrawCallsCount > 0 &&
            (!_requireRetainedCompositor ||
             (_lastMetrics.RetainedCompositionSceneCount > 0 &&
              _lastMetrics
                  .RetainedCompositionServerBackendRenderCount > 0 &&
              _lastMetrics.RetainedCompositionFallbackNodeCount == 0)) &&
            !string.IsNullOrWhiteSpace(
                _lastMetrics.PresentationPath);

        WriteResult(
            renderingPassed,
            owner,
            nativeParent,
            error: null);
        Shutdown(renderingPassed ? 0 : 8);
    }

    private void ValidateOwner(
        WindowImpl owner)
    {
        IPlatformHandle handle =
            owner.Handle ??
            throw new InvalidOperationException(
                "Window did not expose a native handle.");
        if (handle.Handle == 0)
        {
            throw new InvalidOperationException(
                "Window exposed a zero native handle.");
        }
        if (!owner.IsClientAreaExtendedToDecorations)
        {
            throw new InvalidOperationException(
                "Extended client area was not retained.");
        }

        if (OperatingSystem.IsMacOS())
        {
            Require(
                handle.HandleDescriptor == "NSWindow",
                "macOS did not expose an NSWindow.");
            Require(
                !owner.NeedsManagedDecorations,
                "macOS unexpectedly requested managed decorations.");
            Require(
                Math.Abs(owner.ExtendedMargins.Top - 44) <
                0.01,
                "macOS did not retain the requested title-bar height.");
            Require(
                owner.TransparencyLevel ==
                WindowTransparencyLevel.Mica,
                "macOS did not select its best requested backdrop.");
        }
        else if (OperatingSystem.IsWindows())
        {
            Require(
                handle.HandleDescriptor == "HWND",
                "Windows did not expose an HWND.");
            Require(
                owner.NeedsManagedDecorations,
                "Windows did not request managed title-bar drawing.");
            Require(
                owner.TransparencyLevel ==
                WindowTransparencyLevel.Mica,
                "Windows did not select Mica.");
        }
        else if (OperatingSystem.IsLinux())
        {
            Require(
                handle.HandleDescriptor is
                    "XID" or "wl_surface",
                "Linux did not expose an X11 or Wayland handle.");
            Require(
                owner.NeedsManagedDecorations,
                "Linux did not request managed decorations.");
            WindowTransparencyLevel expected =
                handle.HandleDescriptor == "XID"
                    ? WindowTransparencyLevel.AcrylicBlur
                    : WindowTransparencyLevel.Transparent;
            Require(
                owner.TransparencyLevel == expected,
                "Linux did not select the expected backdrop.");
        }

        _ = owner.RequestedDrawnDecorations;
        _ = owner.OffScreenMargin;
        _ = owner.AcrylicCompensationLevels;

        _owner.WindowDecorations =
            WindowDecorations.BorderOnly;
        Require(
            owner.ExtendedMargins.Top == 0,
            "Border-only chrome retained a title-bar margin.");
        _owner.WindowDecorations =
            WindowDecorations.Full;
        _owner.CanResize = false;
        _owner.CanMinimize = false;
        _owner.CanMaximize = false;
        Require(
            (int)owner.AllowedWindowActions == 2,
            "Disabled window actions were still advertised.");
    }

    private void Fail(Exception exception)
    {
        WindowImpl? owner =
            _owner.PlatformImpl as WindowImpl;
        WriteResult(
            passed: false,
            owner,
            default,
            exception.ToString());
        Shutdown(9);
    }

    private void Shutdown(int exitCode)
    {
        ProGpuRenderingDiagnostics.FrameRendered -=
            OnFrameRendered;
        _owned?.Close();
        _owner.Close();
        _desktop.Shutdown(exitCode);
    }

    private void WriteResult(
        bool passed,
        WindowImpl? owner,
        NativeWindowHandle nativeParent,
        string? error)
    {
        if (string.IsNullOrWhiteSpace(_outputPath))
            return;

        string? directory =
            Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using FileStream output =
            File.Create(_outputPath);
        using var writer = new Utf8JsonWriter(
            output,
            new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteBoolean("Passed", passed);
        writer.WriteBoolean(
            "WindowChromePassed",
            passed);
        writer.WriteString(
            "HandleDescriptor",
            owner?.Handle?.HandleDescriptor ??
            "Unavailable");
        writer.WriteBoolean(
            "ExtendedClientArea",
            owner?.IsClientAreaExtendedToDecorations ??
            false);
        writer.WriteBoolean(
            "NeedsManagedDecorations",
            owner?.NeedsManagedDecorations ??
            false);
        writer.WriteNumber(
            "ExtendedTopMargin",
            owner?.ExtendedMargins.Top ?? 0);
        writer.WriteString(
            "TransparencyLevel",
            owner?.TransparencyLevel.ToString() ??
            "Unavailable");
        writer.WriteBoolean(
            "NativeOwnerApplied",
            nativeParent.IsValid);
        writer.WriteNumber("Frames", _frameCount);
        writer.WriteString(
            "PresentationPath",
            _lastMetrics.PresentationPath ??
            "Unavailable");
        writer.WriteNumber(
            "DrawCalls",
            _lastMetrics.DrawCallsCount);
        writer.WriteNumber(
            "RetainedCompositionScenes",
            _lastMetrics.RetainedCompositionSceneCount);
        writer.WriteNumber(
            "RetainedCompositionServerBackendRenders",
            _lastMetrics
                .RetainedCompositionServerBackendRenderCount);
        writer.WriteNumber(
            "RetainedCompositionFallbackNodes",
            _lastMetrics
                .RetainedCompositionFallbackNodeCount);
        if (!string.IsNullOrWhiteSpace(error))
            writer.WriteString("Error", error);
        writer.WriteEndObject();
        writer.Flush();
    }

    private static WindowImpl RequireWindowImpl(
        Window window) =>
        window.PlatformImpl as WindowImpl ??
        throw new InvalidOperationException(
            "Window did not use the Silk.NET backend.");

    private static void Require(
        bool condition,
        string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string? ReadOptionalPath(
        string name)
    {
        string? value =
            Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : Path.GetFullPath(value);
    }

    private static int ReadPositiveInt(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(
                   value,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out int result) &&
               result > 0
            ? result
            : 20;
    }

    private static bool ReadBoolean(string name)
    {
        string? value =
            Environment.GetEnvironmentVariable(name);
        return value is "1" ||
               string.Equals(
                   value,
                   "true",
                   StringComparison.OrdinalIgnoreCase);
    }
}
