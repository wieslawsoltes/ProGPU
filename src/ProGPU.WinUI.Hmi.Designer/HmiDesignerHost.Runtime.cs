using Microsoft.UI.Xaml;
using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private HmiRuntime? _runtime;
    private HmiScreenView? _preview;
    private System.Threading.Timer? _timer;
    private int _generation;
    private int _pendingGeneration;
    private bool _paused;

    /// <summary>Starts a fresh runtime snapshot. No equipment transport or external write endpoint is created.</summary>
    public void StartPreview(bool allowLocalWrites = true, bool automaticTicks = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopPreview();
        var project = Session.GetProject();
        try
        {
            _runtime = new HmiRuntime(project);
            _runtime.Start(allowLocalWrites);
            _preview = new HmiScreenView(project, _runtime, Session.ActiveScreenId, _font);
            _preview.Error += message => Status(message, true);
            _previewScroll.Content = _preview;
            _previewScroll.Visibility = Visibility.Visible;
            _canvasScroll.Visibility = Visibility.Collapsed;
            _paused = false;
            RefreshTables(); UpdateInspector(); RefreshMonitor();
            Status("SIMULATION RUNNING · No equipment connected · Stop to return to design");
            if (automaticTicks)
            {
                int generation = Interlocked.Increment(ref _generation);
                _timer = new System.Threading.Timer(_ => QueueTick(generation), null, 100, 100);
            }
        }
        catch { StopPreview(); throw; }
    }
    private void QueueTick(int generation)
    {
        if (generation != Volatile.Read(ref _generation) || Interlocked.CompareExchange(ref _pendingGeneration, generation, 0) != 0) return;
        try
        {
            UIThread.Post(() =>
            {
                try
                {
                    if (!_disposed && generation == Volatile.Read(ref _generation) && !_paused)
                        Guard(() => AdvancePreview(TimeSpan.FromMilliseconds(100)));
                }
                finally { Interlocked.CompareExchange(ref _pendingGeneration, 0, generation); }
            });
        }
        catch
        {
            Interlocked.CompareExchange(ref _pendingGeneration, 0, generation);
            // A detached dispatcher must not accumulate timer work or crash a ThreadPool thread.
        }
    }
    public void AdvancePreview(TimeSpan elapsed)
    {
        if (_runtime == null) throw new InvalidOperationException("Run simulation first.");
        if (_acquisition != null) throw new InvalidOperationException("Simulation ticks are disabled during live acquisition.");
        _runtime.AdvanceSimulation(elapsed);
    }
    private void TogglePause()
    {
        if (_runtime == null) throw new InvalidOperationException("Run simulation first.");
        _paused = !_paused;
        Status(_paused ? "Simulation paused · Step advances 100 ms" : "Simulation resumed · Local data only");
    }
    public void StopPreview()
    {
        StopHardwareAcquisition();
        Interlocked.Increment(ref _generation);
        _timer?.Dispose(); _timer = null;
        Volatile.Write(ref _pendingGeneration, 0);
        _preview?.Dispose(); _preview = null;
        _runtime?.Stop(); _runtime = null;
        _previewScroll.Content = null;
        _previewScroll.Visibility = Visibility.Collapsed;
        _canvasScroll.Visibility = Visibility.Visible;
        if (!_disposed && _tags != null)
        {
            RefreshTables(); UpdateInspector();
            Status("Design mode · Simulation stopped; the saved document is unchanged");
        }
    }
    public async Task SaveFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_ioBusy) { Status("A file operation is already in progress.", true); return; }
        _ioBusy = true;
        try
        {
            var project = Session.GetProject();
            string savedJson = Session.ExportJson();
            await HmiProjectFile.SaveAsync(path, project, cancellationToken).ConfigureAwait(false);
            UIThread.Post(() =>
            {
                if (_disposed) return;
                Session.MarkSaved(savedJson);
                Status(Session.IsDirty ? "File saved. Newer edits remain unsaved." : "Project saved: " + path);
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
        { UIThread.Post(() => { if (!_disposed) Status("Save failed: " + error.Message, true); }); }
        finally { UIThread.Post(() => _ioBusy = false); }
    }
    public async Task OpenFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_ioBusy) { Status("A file operation is already in progress.", true); return; }
        _ioBusy = true;
        string original = Session.ExportJson();
        try
        {
            var project = await HmiProjectFile.LoadAsync(path, cancellationToken).ConfigureAwait(false);
            UIThread.Post(() =>
            {
                if (_disposed) return;
                if (Session.ExportJson() != original) { Status("The design changed while the file was loading. Open again to avoid overwriting those edits.", true); return; }
                Guard(() => { StopPreview(); Session.Open(project); _filePath.Text = path; Fit(); Status("Opened " + path); });
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
        { UIThread.Post(() => { if (!_disposed) Status("Open failed: " + error.Message, true); }); }
        finally { UIThread.Post(() => _ioBusy = false); }
    }
}
