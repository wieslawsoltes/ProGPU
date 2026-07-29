using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Samples.ActivityMonitor.Monitoring;
using ProGPU.Text;

namespace ProGPU.Samples.ActivityMonitor.Presentation;

internal sealed class ActivityMonitorController : IAsyncDisposable
{
    private readonly IActivityMonitorDataSource _dataSource;
    private readonly ActivityMonitorView _view;
    private readonly TtfFont _font;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private TimeSpan _updateInterval = TimeSpan.FromSeconds(2);
    private Task? _refreshLoop;
    private bool _disposed;

    public ActivityMonitorController(
        TtfFont font,
        IActivityMonitorDataSource dataSource)
    {
        _font = font;
        _dataSource = dataSource;
        _view = new ActivityMonitorView(font);
        _view.RefreshRequested += (_, _) => RequestRefresh();
        _view.InspectRequested += (_, _) => BeginUserAction(
            "Inspect Failed",
            InspectSelectedAsync);
        _view.TerminationRequested += (_, _) => BeginUserAction(
            "Quit Failed",
            ConfirmTerminationAsync);
        _view.UpdateFrequencyChanged += (_, args) => _updateInterval = args.Interval;
        _view.SampleRequested += (_, _) => BeginUserAction(
            "Sample Failed",
            SampleSelectedAsync);
        _view.SpindumpRequested += (_, _) => BeginUserAction(
            "Spindump Failed",
            () => RunDiagnosticAsync(ActivityDiagnosticKind.Spindump));
        _view.SystemDiagnosticsRequested += (_, _) => BeginUserAction(
            "System Diagnostics Failed",
            () => RunDiagnosticAsync(ActivityDiagnosticKind.SystemDiagnostics));
    }

    public FrameworkElement View => _view;

    public void Start()
    {
        _refreshLoop ??= RefreshLoopAsync(_cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cancellation.Cancel();
        if (_refreshLoop is not null)
        {
            try
            {
                await _refreshLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        await _dataSource.DisposeAsync().ConfigureAwait(false);
        _refreshGate.Dispose();
        _cancellation.Dispose();
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(_updateInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private void RequestRefresh()
    {
        _ = RefreshAsync(_cancellation.Token);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            ActivitySnapshot snapshot = await _dataSource.CaptureAsync(
                new ActivityCaptureOptions(),
                cancellationToken).ConfigureAwait(false);
            UIThread.Post(() => _view.ApplySnapshot(snapshot));
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException &&
            exception is not ObjectDisposedException)
        {
            UIThread.Post(() => _view.SetStatus($"Refresh failed: {exception.Message}"));
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task InspectSelectedAsync()
    {
        ProcessSnapshot? selected = _view.SelectedProcess;
        if (selected is null)
        {
            await ShowMessageAsync("Inspect Process", "Select a process in the table first.");
            return;
        }

        ProcessDetails? details = await _dataSource.GetProcessDetailsAsync(
            selected.ProcessId,
            _cancellation.Token);
        if (details is null)
        {
            await ShowMessageAsync("Process Unavailable", "The selected process has already exited.");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = $"{details.Name} ({details.ProcessId})",
            Content = new ProcessInspectorView(_font, details),
            PrimaryButtonText = "Sample",
            SecondaryButtonText = "Quit",
            CloseButtonText = "Done",
            FullSizeDesired = true
        };
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                BeginUserAction("Sample Failed", SampleSelectedAsync);
                break;
            case ContentDialogResult.Secondary:
                BeginUserAction("Quit Failed", ConfirmTerminationAsync);
                break;
        }
    }

    private async Task ConfirmTerminationAsync()
    {
        ProcessSnapshot? selected = _view.SelectedProcess;
        if (selected is null)
        {
            await ShowMessageAsync("Quit Process", "Select a process in the table first.");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Are you sure you want to quit this process?",
            Content = $"Do you really want to quit “{selected.Name}”?",
            PrimaryButtonText = "Quit",
            SecondaryButtonText = "Force Quit",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        ContentDialogResult choice = await dialog.ShowAsync();
        ProcessTerminationMode? mode = choice switch
        {
            ContentDialogResult.Primary => ProcessTerminationMode.Quit,
            ContentDialogResult.Secondary => ProcessTerminationMode.ForceQuit,
            _ => null
        };
        if (mode is null)
        {
            return;
        }

        ProcessActionResult result = await _dataSource.TerminateProcessAsync(
            selected.ProcessId,
            mode.Value,
            _cancellation.Token);
        _view.SetStatus(result.Message);
        if (!result.Succeeded)
        {
            string verb = mode == ProcessTerminationMode.ForceQuit
                ? "Force Quit"
                : "Quit";
            await ShowMessageAsync($"{verb} Failed", result.Message);
        }
        else
        {
            RequestRefresh();
        }
    }

    private async Task SampleSelectedAsync()
    {
        ProcessSnapshot? selected = _view.SelectedProcess;
        if (selected is null)
        {
            await ShowMessageAsync("Sample Process", "Select a process in the table first.");
            return;
        }

        ProcessReportResult result = await _dataSource.SampleProcessAsync(
            selected.ProcessId,
            _cancellation.Token);
        await ShowMessageAsync(
            result.Succeeded ? $"Sample of {selected.Name}" : "Sample Failed",
            result.Succeeded ? result.Report : result.Message);
    }

    private async Task RunDiagnosticAsync(ActivityDiagnosticKind kind)
    {
        int? processId = kind == ActivityDiagnosticKind.Spindump
            ? _view.SelectedProcess?.ProcessId
            : null;
        ProcessActionResult result = await _dataSource.RunDiagnosticAsync(
            kind,
            processId,
            _cancellation.Token);
        _view.SetStatus(result.Message);
        await ShowMessageAsync(
            result.Succeeded ? "Diagnostic Started" : "Diagnostic Failed",
            result.Message);
    }

    private void BeginUserAction(string failureTitle, Func<Task> action)
    {
        _ = ExecuteUserActionAsync(failureTitle, action);
    }

    private async Task ExecuteUserActionAsync(string failureTitle, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(failureTitle, exception.Message);
        }
    }

    private static async Task ShowMessageAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "OK"
        };
        await dialog.ShowAsync();
    }
}
