using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Samples.ActivityMonitor.Monitoring;
using ProGPU.Text;

namespace ProGPU.Samples.ActivityMonitor.Presentation;

internal sealed class ActivityMonitorController : IAsyncDisposable
{
    private readonly IActivityMonitorDataSource _dataSource;
    private readonly ActivityMonitorView _view;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Task? _refreshLoop;
    private bool _disposed;

    public ActivityMonitorController(
        TtfFont font,
        IActivityMonitorDataSource dataSource)
    {
        _dataSource = dataSource;
        _view = new ActivityMonitorView(font);
        _view.RefreshRequested += (_, _) => RequestRefresh();
        _view.InspectRequested += (_, _) => InspectSelected();
        _view.QuitRequested += (_, _) => ConfirmTermination(ProcessTerminationMode.Quit);
        _view.ForceQuitRequested += (_, _) => ConfirmTermination(ProcessTerminationMode.ForceQuit);
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
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
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

    private async void InspectSelected()
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

        string content =
            $"Process: {details.Name}\n" +
            $"PID: {details.ProcessId}\n" +
            $"Parent PID: {details.ParentProcessId}\n" +
            $"User: {details.User}\n" +
            $"CPU time: {ActivityMetricFormatter.Duration(details.Snapshot.CpuTime)}\n" +
            $"Memory: {ActivityMetricFormatter.Bytes(details.Snapshot.MemoryBytes)}\n" +
            $"Threads: {details.Snapshot.ThreadCount:N0}\n" +
            $"Executable: {details.ExecutablePath}\n" +
            $"Command: {details.CommandLine}";
        await ShowMessageAsync(details.Name, content);
    }

    private async void ConfirmTermination(ProcessTerminationMode mode)
    {
        ProcessSnapshot? selected = _view.SelectedProcess;
        if (selected is null)
        {
            await ShowMessageAsync("Quit Process", "Select a process in the table first.");
            return;
        }

        string verb = mode == ProcessTerminationMode.ForceQuit ? "Force Quit" : "Quit";
        var dialog = new ContentDialog
        {
            Title = $"{verb} “{selected.Name}”?",
            Content = mode == ProcessTerminationMode.ForceQuit
                ? "The process will be stopped immediately and may lose unsaved data."
                : "The process will receive a normal termination request.",
            PrimaryButtonText = verb,
            SecondaryButtonText = "Cancel"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ProcessActionResult result = await _dataSource.TerminateProcessAsync(
            selected.ProcessId,
            mode,
            _cancellation.Token);
        _view.SetStatus(result.Message);
        if (!result.Succeeded)
        {
            await ShowMessageAsync($"{verb} Failed", result.Message);
        }
        else
        {
            RequestRefresh();
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
