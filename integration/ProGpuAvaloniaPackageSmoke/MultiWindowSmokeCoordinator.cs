using System;
using System.IO;
using System.Text.Json;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.ProGpu;
using Avalonia.SilkNet;
using Avalonia.Threading;
using ProGpuCompositorMetrics = ProGPU.Scene.CompositorMetrics;

namespace ProGpuAvaloniaPackageSmoke;

internal sealed class MultiWindowSmokeCoordinator
{
    private const int InitialFrames = 24;
    private const int OwnerDisposedFrames = 22;
    private const int BorrowerDisposedFrames = 20;

    private readonly IClassicDesktopStyleApplicationLifetime
        _desktop;
    private readonly SmokeWindow _owner =
        new(standalone: false)
        {
            Title = "ProGPU package smoke — device owner",
            Width = 640,
            Height = 420
        };
    private readonly SmokeWindow _survivor =
        new(standalone: false)
        {
            Title = "ProGPU package smoke — survivor",
            Width = 560,
            Height = 420
        };
    private SmokeWindow? _borrower;
    private WindowImpl? _ownerImpl;
    private WindowImpl? _survivorImpl;
    private WindowImpl? _borrowerImpl;
    private readonly string? _outputPath;
    private Stage _stage;
    private int _stageFrames;
    private int _totalFrames;
    private int _maximumSceneCount;
    private int _maximumFallbackNodes;
    private bool _transitionPending;
    private bool _ownerPairShared;
    private bool _borrowerPairShared;
    private bool _ownerDisposed;
    private bool _borrowerDisposed;
    private ProGpuCompositorMetrics _lastMetrics;

    internal MultiWindowSmokeCoordinator(
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop ??
            throw new ArgumentNullException(nameof(desktop));
        _outputPath = ReadOptionalPath(
            "PROGPU_PACKAGE_SMOKE_OUTPUT");
    }

    internal void Start()
    {
        _desktop.MainWindow = _survivor;
        ProGpuRenderingDiagnostics.FrameRendered +=
            OnFrameRendered;
        _stage = Stage.InitialPair;
        _owner.Show();
        _survivor.Show();
    }

    private void OnFrameRendered(
        ProGpuCompositorMetrics metrics)
    {
        _lastMetrics = metrics;
        _totalFrames++;
        _stageFrames++;
        _maximumSceneCount = Math.Max(
            _maximumSceneCount,
            metrics.RetainedCompositionSceneCount);
        _maximumFallbackNodes = Math.Max(
            _maximumFallbackNodes,
            metrics.RetainedCompositionFallbackNodeCount);

        if (_transitionPending)
            return;

        switch (_stage)
        {
            case Stage.InitialPair
                when _stageFrames >= InitialFrames:
                _transitionPending = true;
                Dispatcher.UIThread.Post(
                    DisposeOwnerAsync,
                    DispatcherPriority.Background);
                break;
            case Stage.OwnerDisposed
                when _stageFrames >= OwnerDisposedFrames:
                _transitionPending = true;
                Dispatcher.UIThread.Post(
                    OpenBorrower,
                    DispatcherPriority.Background);
                break;
            case Stage.OpeningBorrower:
                TryDisposeBorrower();
                break;
            case Stage.BorrowerDisposed
                when _stageFrames >= BorrowerDisposedFrames:
                _transitionPending = true;
                Dispatcher.UIThread.Post(
                    Complete,
                    DispatcherPriority.Background);
                break;
        }
    }

    private async void DisposeOwnerAsync()
    {
        try
        {
            _ownerImpl = RequireWindowImpl(_owner);
            _survivorImpl = RequireWindowImpl(_survivor);
            _ownerPairShared =
                _ownerImpl.HasActiveWebGpuContext &&
                _survivorImpl.HasActiveWebGpuContext &&
                _ownerImpl.SharesWebGpuDeviceWith(
                    _survivorImpl);
            _owner.Close();
            await _ownerImpl.DisposedTask;
            _ownerDisposed = true;
            _stage = Stage.OwnerDisposed;
            _stageFrames = 0;
            _transitionPending = false;
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void OpenBorrower()
    {
        try
        {
            _borrower = new SmokeWindow(standalone: false)
            {
                Title =
                    "ProGPU package smoke — disposable borrower",
                Width = 480,
                Height = 320
            };
            _borrower.Show();
            _stage = Stage.OpeningBorrower;
            _stageFrames = 0;
            _transitionPending = false;
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void TryDisposeBorrower()
    {
        if (_borrower is null ||
            _borrower.PlatformImpl is not WindowImpl borrower ||
            _survivor.PlatformImpl is not WindowImpl survivor ||
            !borrower.HasActiveWebGpuContext ||
            !survivor.HasActiveWebGpuContext)
        {
            return;
        }

        _borrowerPairShared =
            survivor.SharesWebGpuDeviceWith(borrower);
        if (!_borrowerPairShared)
        {
            Fail(
                new InvalidOperationException(
                    "The survivor and borrower did not share a WebGPU device."));
            return;
        }

        _transitionPending = true;
        _borrowerImpl = borrower;
        Dispatcher.UIThread.Post(
            DisposeBorrowerAsync,
            DispatcherPriority.Background);
    }

    private async void DisposeBorrowerAsync()
    {
        try
        {
            _borrower!.Close();
            await _borrowerImpl!.DisposedTask;
            _borrowerDisposed = true;
            _stage = Stage.BorrowerDisposed;
            _stageFrames = 0;
            _transitionPending = false;
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void Complete()
    {
        bool passed =
            _ownerPairShared &&
            _borrowerPairShared &&
            _ownerDisposed &&
            _borrowerDisposed &&
            _survivorImpl is
            {
                HasActiveWebGpuContext: true
            } &&
            _maximumSceneCount >= 2 &&
            _maximumFallbackNodes == 0 &&
            _lastMetrics.RetainedCompositionSceneCount >= 1 &&
            _lastMetrics
                .RetainedCompositionServerBackendRenderCount > 0 &&
            _lastMetrics.RetainedCompositionFallbackNodeCount == 0 &&
            !string.IsNullOrWhiteSpace(
                _lastMetrics.PresentationPath);

        WriteResult(passed, error: null);
        Shutdown(passed ? 0 : 6);
    }

    private void Fail(Exception exception)
    {
        WriteResult(passed: false, exception.ToString());
        Shutdown(7);
    }

    private void Shutdown(int exitCode)
    {
        ProGpuRenderingDiagnostics.FrameRendered -=
            OnFrameRendered;
        Environment.Exit(exitCode);
    }

    private void WriteResult(bool passed, string? error)
    {
        if (string.IsNullOrWhiteSpace(_outputPath))
            return;

        string? directory =
            Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using FileStream output = File.Create(_outputPath);
        using var writer = new Utf8JsonWriter(
            output,
            new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteBoolean("Passed", passed);
        writer.WriteBoolean(
            "MultiWindowLifecyclePassed",
            passed);
        writer.WriteNumber("Frames", _totalFrames);
        writer.WriteNumber(
            "InitialPairTargetFrames",
            InitialFrames);
        writer.WriteNumber(
            "OwnerDisposedTargetFrames",
            OwnerDisposedFrames);
        writer.WriteNumber(
            "BorrowerDisposedTargetFrames",
            BorrowerDisposedFrames);
        writer.WriteBoolean(
            "OwnerPairSharedDevice",
            _ownerPairShared);
        writer.WriteBoolean(
            "BorrowerPairSharedDevice",
            _borrowerPairShared);
        writer.WriteBoolean(
            "OwnerDisposed",
            _ownerDisposed);
        writer.WriteBoolean(
            "BorrowerDisposed",
            _borrowerDisposed);
        writer.WriteBoolean(
            "SurvivorActive",
            _survivorImpl?.HasActiveWebGpuContext == true);
        writer.WriteNumber(
            "MaximumRetainedCompositionScenes",
            _maximumSceneCount);
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
        writer.WriteString(
            "PresentationPath",
            _lastMetrics.PresentationPath ??
            "Unavailable");
        if (!string.IsNullOrWhiteSpace(error))
            writer.WriteString("Error", error);
        writer.WriteEndObject();
        writer.Flush();
    }

    private static WindowImpl RequireWindowImpl(
        SmokeWindow window) =>
        window.PlatformImpl as WindowImpl ??
        throw new InvalidOperationException(
            "The package smoke did not receive a Silk.NET window.");

    private static string? ReadOptionalPath(string name)
    {
        string? value =
            Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : Path.GetFullPath(value);
    }

    private enum Stage
    {
        InitialPair,
        OwnerDisposed,
        OpeningBorrower,
        BorrowerDisposed
    }
}
