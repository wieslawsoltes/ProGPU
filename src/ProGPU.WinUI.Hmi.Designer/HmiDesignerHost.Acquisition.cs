using Microsoft.UI.Xaml;
using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private HmiAcquisitionSession? _acquisition;
    private IHmiConnection? _connectedTransport;
    private string? _connectedProfileId;
    private HmiWriteCoordinator? _writeCoordinator;
    private HmiWriteRequest? _writeRequest;
    private CancellationTokenSource? _connectionLifetime;
    private readonly List<Task> _transportRetirements = [];
    private int _connectionGeneration;
    public HmiConnectionDiagnostics? ConnectionDiagnostics => _acquisition?.Diagnostics;

    private static async ValueTask DispatchRuntimeAsync(Action action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = token.Register(() => completion.TrySetCanceled(token));
        UIThread.Post(() =>
        {
            if (token.IsCancellationRequested || completion.Task.IsCompleted) return;
            try { action(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        await completion.Task.ConfigureAwait(false);
    }
    private async Task ConnectHardwareAsync()
    {
        int generation = _connectionGeneration;
        try
        {
            var factory = ConnectionFactory ?? throw new InvalidOperationException("The embedding application has not registered protocol adapters.");
            var project = Session.GetProject();
            var profile = FindProfile(project).Copy();
            profile.Validate(project.Tags); profile.RequireTransportPermission();
            if (profile.Mappings.Count == 0 && profile.Protocol != HmiConnectionProtocol.OpcUa)
                throw new InvalidOperationException("Configure at least one I/O mapping before connecting.");
            StopPreview(); generation = _connectionGeneration;
            string revision = Session.ExportJson();
            string screenId = Session.ActiveScreenId;
            await Task.WhenAll(_transportRetirements.ToArray()).ConfigureAwait(false);
            await DispatchRuntimeAsync(() =>
            {
                if (_disposed || generation != _connectionGeneration) return;
                if (revision != Session.ExportJson()) throw new InvalidOperationException("The project changed during connection setup. Review its configuration and connect again.");
                var connection = factory(profile) ?? throw new InvalidOperationException("The transport factory returned null.");
                HmiAcquisitionSession? acquisition = null;
                HmiScreenView? preview = null;
                try
                {
                    var runtime = new HmiRuntime(project, initializeGoodQuality: false);
                    runtime.Start(allowLocalWrites: false);
                    preview = new HmiScreenView(project, runtime, screenId, _font) { CommandRequested = PrepareHardwareWrite };
                    preview.Error += message => Status(message, true);
                    var coordinator = new HmiWriteCoordinator(project, profile, runtime, connection, DispatchRuntimeAsync,
                        (request, token) => WriteAuthorizer?.Invoke(request, token) ?? ValueTask.FromResult(false));
                    acquisition = new HmiAcquisitionSession(connection, profile, runtime, DispatchRuntimeAsync);
                    _connectionLifetime = new CancellationTokenSource();
                    _connectedTransport = connection; _connectedProfileId = profile.Id;
                    _runtime = runtime; _preview = preview; _writeCoordinator = coordinator; _acquisition = acquisition;
                    _previewViewport.Screen = preview;
                    _previewViewport.Visibility = Visibility.Visible; _canvasScroll.Visibility = Visibility.Collapsed;
                    acquisition.Start();
                }
                catch
                {
                    preview?.Dispose();
                    if (acquisition == null) _transportRetirements.Add(RetireUnownedTransportAsync(connection));
                    else if (!ReferenceEquals(acquisition, _acquisition)) _transportRetirements.Add(RetireTransportAsync(acquisition));
                    throw;
                }
                _pendingCommand.Text = $"LIVE {profile.Protocol}: {profile.Host}:{profile.Port} · Read-only acquisition. Commands require review and host authorization.";
                RefreshTables(); UpdateInspector(); Status("Connecting to " + profile.Name + " · no simulation source");
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await DispatchRuntimeAsync(() =>
            {
                // A late failure from a superseded attempt cannot stop its replacement.
                if (!_disposed && generation == _connectionGeneration) { StopPreview(); Status("Connection failed: " + error.Message, true); }
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }
    private void StopHardwareAcquisition()
    {
        _connectionGeneration++;
        _connectionLifetime?.Cancel(); _connectionLifetime?.Dispose(); _connectionLifetime = null;
        _writeCoordinator?.RevokeAll(); _writeCoordinator = null; _writeRequest = null;
        _connectedTransport = null; _connectedProfileId = null;
        var acquisition = _acquisition; _acquisition = null;
        _transportRetirements.RemoveAll(t => t.IsCompleted);
        if (acquisition != null) _transportRetirements.Add(RetireTransportAsync(acquisition));
        if (_pendingCommand != null) _pendingCommand.Text = "No external command pending.";
    }
    private async Task RetireUnownedTransportAsync(IHmiConnection connection)
    {
        try { await connection.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { UIThread.Post(() => { if (!_disposed) Status("Transport retirement: " + error.Message, true); }); }
    }
    private async Task RetireTransportAsync(HmiAcquisitionSession acquisition)
    {
        try { await acquisition.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { UIThread.Post(() => { if (!_disposed) Status("Transport retirement: " + error.Message, true); }); }
    }
    private void PrepareHardwareWrite(HmiElement element, HmiValue? submittedValue)
    {
        Guard(() =>
        {
            if (_writeCoordinator == null || _runtime == null) throw new InvalidOperationException("No connected command session.");
            string tag;
            HmiValue value;
            if (submittedValue is { } submitted) { tag = element.Tag; value = submitted; }
            else
            {
                tag = element.Action.Target;
                value = element.Action.Kind switch
                {
                    HmiActionKind.WriteTag => element.Action.Value,
                    HmiActionKind.ToggleTag => HmiValue.From(!_runtime.Read(tag).Value.AsBoolean()),
                    _ => throw new InvalidOperationException("Multi-tag recipes and application actions are not sent to equipment. Configure explicit absolute commands.")
                };
            }
            DiscardHardwareWrite();
            _writeRequest = _writeCoordinator.Prepare(tag, value, _operator.Text, _reason.Text);
            _pendingCommand.Text = $"REVIEW {_writeRequest.Tag}: {_writeRequest.ObservedValue} → {_writeRequest.Value} · expires {_writeRequest.ExpiresAt:HH:mm:ss} UTC · {_writeRequest.Reason}";
            Status("Command prepared, not sent. Review in Connections and confirm explicitly.");
        });
    }
    private void DiscardHardwareWrite()
    {
        _writeRequest = null; _writeCoordinator?.RevokeAll();
        if (_pendingCommand != null) _pendingCommand.Text = "No external command pending.";
    }
    private async Task ConfirmHardwareWriteAsync()
    {
        if (_writeRequest == null || _writeCoordinator == null || _connectionLifetime == null) { Status("No pending hardware command.", true); return; }
        var request = _writeRequest; _writeRequest = null;
        var coordinator = _writeCoordinator; int generation = _connectionGeneration;
        var token = _connectionLifetime.Token;
        _pendingCommand.Text = "Checking authorization, session identity and current feedback…";
        try
        {
            var result = await coordinator.ConfirmAsync(request.Id, token).ConfigureAwait(false);
            await DispatchRuntimeAsync(() =>
            {
                if (_disposed || generation != _connectionGeneration) return;
                _pendingCommand.Text = result.Disposition + ": " + result.Detail;
                Status(_pendingCommand.Text, !result.Acknowledged);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            UIThread.Post(() => { if (!_disposed && generation == _connectionGeneration) Status("Command processing failed: " + error.Message, true); });
        }
    }
}
