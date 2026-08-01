using Microsoft.UI.Content;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Graphics.Imaging;

namespace Microsoft.UI.Input.DragDrop;

[Flags]
[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public enum DragDropModifiers : uint
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    LeftButton = 8,
    MiddleButton = 16,
    RightButton = 32
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public enum DragUIContentMode
{
    Auto = 0,
    Deferred = 1
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public interface IDropOperationTarget
{
    IAsyncOperation<DataPackageOperation>
        EnterAsync(
            DragInfo dragInfo,
            DragUIOverride dragUIOverride);

    IAsyncOperation<DataPackageOperation>
        OverAsync(
            DragInfo dragInfo,
            DragUIOverride dragUIOverride);

    IAsyncAction LeaveAsync(
        DragInfo dragInfo);

    IAsyncOperation<DataPackageOperation>
        DropAsync(
            DragInfo dragInfo);
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class DragInfo
{
    internal DragInfo(
        DataPackageView data,
        DataPackageOperation allowedOperations,
        DragDropModifiers modifiers,
        Point position)
    {
        Data = data;
        AllowedOperations = allowedOperations;
        Modifiers = modifiers;
        Position = position;
    }

    public DataPackageOperation AllowedOperations
    {
        get;
    }

    public DataPackageView Data { get; }

    public DragDropModifiers Modifiers { get; }

    public Point Position { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class DragUIOverride
{
    private readonly object _sync = new();
    private SoftwareBitmap? _bitmap;
    private Point _anchorPoint;
    private string _caption = string.Empty;
    private bool _isCaptionVisible = true;
    private bool _isContentVisible = true;
    private bool _isGlyphVisible = true;

    internal DragUIOverride(
        SoftwareBitmap? bitmap = null,
        Point anchorPoint = default)
    {
        _bitmap = bitmap;
        _anchorPoint = anchorPoint;
    }

    public string Caption
    {
        get
        {
            lock (_sync)
                return _caption;
        }
        set
        {
            lock (_sync)
                _caption = value ?? string.Empty;
        }
    }

    public bool IsCaptionVisible
    {
        get
        {
            lock (_sync)
                return _isCaptionVisible;
        }
        set
        {
            lock (_sync)
                _isCaptionVisible = value;
        }
    }

    public bool IsContentVisible
    {
        get
        {
            lock (_sync)
                return _isContentVisible;
        }
        set
        {
            lock (_sync)
                _isContentVisible = value;
        }
    }

    public bool IsGlyphVisible
    {
        get
        {
            lock (_sync)
                return _isGlyphVisible;
        }
        set
        {
            lock (_sync)
                _isGlyphVisible = value;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _bitmap = null;
            _anchorPoint = default;
            _caption = string.Empty;
            _isCaptionVisible = false;
            _isContentVisible = false;
            _isGlyphVisible = false;
        }
    }

    public void SetContentFromSoftwareBitmap(
        SoftwareBitmap bitmap)
    {
        SetContentFromSoftwareBitmap(
            bitmap,
            default);
    }

    public void SetContentFromSoftwareBitmap(
        SoftwareBitmap bitmap,
        Point anchorPoint)
    {
        ValidateBitmapAndAnchor(
            bitmap,
            anchorPoint);
        lock (_sync)
        {
            _bitmap = bitmap;
            _anchorPoint = anchorPoint;
            _isContentVisible = true;
        }
    }

    internal ProGPU.WinUI.Platform
        .DragDropVisualSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new(
                _bitmap,
                _anchorPoint,
                _caption,
                _isCaptionVisible,
                _isContentVisible,
                _isGlyphVisible);
        }
    }

    internal static void ValidateBitmapAndAnchor(
        SoftwareBitmap bitmap,
        Point anchorPoint)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        bitmap.VerifyAvailable();
        if (!double.IsFinite(anchorPoint.X) ||
            !double.IsFinite(anchorPoint.Y) ||
            anchorPoint.X < 0 ||
            anchorPoint.Y < 0 ||
            anchorPoint.X >
                bitmap.PixelWidth ||
            anchorPoint.Y >
                bitmap.PixelHeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(anchorPoint));
        }
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class
    DropOperationTargetRequestedEventArgs
{
    internal IDropOperationTarget? Target
    {
        get;
        private set;
    }

    public void SetTarget(
        IDropOperationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class DragDropManager :
    IDisposable
{
    private readonly object _sync = new();
    private readonly ContentIsland _island;
    private readonly Dictionary<uint, DragSession>
        _sessions = new();
    private int _areConcurrentOperationsEnabled;
    private bool _isDisposed;

    private DragDropManager(
        ContentIsland island)
    {
        _island = island;
        _island.Closed += OnIslandClosed;
    }

    public bool AreConcurrentOperationsEnabled
    {
        get =>
            Volatile.Read(
                ref _areConcurrentOperationsEnabled) !=
            0;
        set =>
            Volatile.Write(
                ref _areConcurrentOperationsEnabled,
                value ? 1 : 0);
    }

    public event TypedEventHandler<
        DragDropManager,
        DropOperationTargetRequestedEventArgs>?
        TargetRequested;

    public static DragDropManager GetForIsland(
        ContentIsland content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.IsClosed)
            return null!;
        return new DragDropManager(content);
    }

    public void Dispose()
    {
        DragSession[] sessions;
        lock (_sync)
        {
            if (_isDisposed)
                return;
            _isDisposed = true;
            _island.Closed -= OnIslandClosed;
            TargetRequested = null;
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }

        foreach (DragSession session in sessions)
            session.Cancel();
    }

    internal Task<DataPackageOperation> Start(
        DragOperation operation,
        PointerPoint pointerPoint)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(pointerPoint);

        var requested =
            new DropOperationTargetRequestedEventArgs();
        uint pointerId = pointerPoint.PointerId;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!AreConcurrentOperationsEnabled &&
                _sessions.Count != 0)
            {
                throw new InvalidOperationException(
                    "A drag operation is already active.");
            }
            if (_sessions.ContainsKey(pointerId))
            {
                throw new InvalidOperationException(
                    "The pointer already owns an active drag operation.");
            }
        }

        TargetRequested?.Invoke(this, requested);
        if (requested.Target is null)
        {
            return Task.FromResult(
                DataPackageOperation.None);
        }

        var session = new DragSession(
            operation,
            requested.Target,
            pointerPoint);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!AreConcurrentOperationsEnabled &&
                _sessions.Count != 0)
            {
                throw new InvalidOperationException(
                    "A drag operation is already active.");
            }
            _sessions.Add(pointerId, session);
        }
        session.Begin();
        _ = RemoveWhenCompleted(
            pointerId,
            session);
        return session.Completion;
    }

    internal Task<DataPackageOperation> NotifyOver(
        uint pointerId,
        Point position,
        DragDropModifiers modifiers) =>
        GetSession(pointerId).Over(
            position,
            modifiers);

    internal Task NotifyLeave(
        uint pointerId,
        Point position,
        DragDropModifiers modifiers) =>
        GetSession(pointerId).Leave(
            position,
            modifiers);

    internal Task<DataPackageOperation> NotifyDrop(
        uint pointerId,
        Point position,
        DragDropModifiers modifiers)
    {
        DragSession session =
            GetSession(pointerId);
        return session.Drop(
            position,
            modifiers);
    }

    internal bool Cancel(uint pointerId)
    {
        DragSession? session;
        lock (_sync)
        {
            if (!_sessions.Remove(
                    pointerId,
                    out session))
            {
                return false;
            }
        }
        session.Cancel();
        return true;
    }

    internal bool TryGetVisual(
        uint pointerId,
        out ProGPU.WinUI.Platform
            .DragDropVisualSnapshot visual)
    {
        lock (_sync)
        {
            if (_isDisposed ||
                !_sessions.TryGetValue(
                    pointerId,
                    out DragSession? session))
            {
                visual = default;
                return false;
            }
            visual = session.GetVisual();
            return true;
        }
    }

    private async Task RemoveWhenCompleted(
        uint pointerId,
        DragSession session)
    {
        try
        {
            await session.Completion
                .ConfigureAwait(false);
        }
        catch
        {
            // The public operation preserves the target exception.
        }
        finally
        {
            lock (_sync)
            {
                if (_sessions.TryGetValue(
                        pointerId,
                        out DragSession? current) &&
                    ReferenceEquals(current, session))
                {
                    _sessions.Remove(pointerId);
                }
            }
        }
    }

    private DragSession GetSession(
        uint pointerId)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_sessions.TryGetValue(
                    pointerId,
                    out DragSession? session))
            {
                throw new InvalidOperationException(
                    "The pointer does not own an active drag operation.");
            }
            return session;
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            _isDisposed,
            this);

    private void OnIslandClosed() => Dispose();
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class DragOperation :
    IDisposable
{
    private SoftwareBitmap? _bitmap;
    private Point _anchorPoint;
    private int _isDisposed;
    private int _hasStarted;

    public DragOperation()
    {
        Data = new DataPackage();
    }

    public DataPackageOperation AllowedOperations
    {
        get;
        set;
    }

    public DataPackage Data { get; }

    public DragUIContentMode DragUIContentMode
    {
        get;
        set;
    }

    public void Dispose()
    {
        Interlocked.Exchange(
            ref _isDisposed,
            1);
        _bitmap = null;
    }

    public void SetDragUIContentFromSoftwareBitmap(
        SoftwareBitmap bitmap)
    {
        SetDragUIContentFromSoftwareBitmap(
            bitmap,
            default);
    }

    public void SetDragUIContentFromSoftwareBitmap(
        SoftwareBitmap bitmap,
        Point anchorPoint)
    {
        ThrowIfUnavailable();
        DragUIOverride.ValidateBitmapAndAnchor(
            bitmap,
            anchorPoint);
        _bitmap = bitmap;
        _anchorPoint = anchorPoint;
    }

    public IAsyncOperation<DataPackageOperation>
        StartAsync(
            DragDropManager initialTarget,
            PointerPoint initialPointerPoint)
    {
        ThrowIfUnavailable();
        ArgumentNullException.ThrowIfNull(
            initialTarget);
        ArgumentNullException.ThrowIfNull(
            initialPointerPoint);
        if (Interlocked.CompareExchange(
                ref _hasStarted,
                1,
                0) != 0)
        {
            throw new InvalidOperationException(
                "A DragOperation can only be started once.");
        }
        try
        {
            return new Windows.Foundation
                .TaskAsyncOperation<
                    DataPackageOperation>(
                    initialTarget.Start(
                        this,
                        initialPointerPoint));
        }
        catch
        {
            Volatile.Write(ref _hasStarted, 0);
            throw;
        }
    }

    internal DragUIOverride CreateOverride() =>
        new(_bitmap, _anchorPoint);

    private void ThrowIfUnavailable() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _isDisposed) != 0,
            this);
}

internal sealed class DragSession
{
    private readonly object _sync = new();
    private readonly IDropOperationTarget _target;
    private readonly DataPackageView _data;
    private readonly DataPackageOperation
        _allowedOperations;
    private readonly DragUIOverride _uiOverride;
    private readonly TaskCompletionSource<
        DataPackageOperation> _completion =
        new(TaskCreationOptions
            .RunContinuationsAsynchronously);
    private Task _tail = Task.CompletedTask;
    private Point _position;
    private DragDropModifiers _modifiers;
    private bool _isComplete;

    internal DragSession(
        DragOperation operation,
        IDropOperationTarget target,
        PointerPoint pointerPoint)
    {
        _target = target;
        _data = operation.Data.GetView();
        _allowedOperations =
            operation.AllowedOperations;
        _uiOverride = operation.CreateOverride();
        _position = pointerPoint.Position;
        _modifiers = ToModifiers(
            pointerPoint.Properties);
    }

    internal Task<DataPackageOperation>
        Completion => _completion.Task;

    internal void Begin()
    {
        lock (_sync)
        {
            _tail = EnterCore(
                CreateInfo(
                    _position,
                    _modifiers));
        }
        Observe(_tail);
    }

    internal Task<DataPackageOperation> Over(
        Point position,
        DragDropModifiers modifiers)
    {
        lock (_sync)
        {
            ThrowIfComplete();
            _position = position;
            _modifiers = modifiers;
            DragInfo info = CreateInfo(
                position,
                modifiers);
            Task<DataPackageOperation> next =
                OverAfter(
                    _tail,
                    info);
            _tail = next;
            Observe(next);
            return next;
        }
    }

    internal Task Leave(
        Point position,
        DragDropModifiers modifiers)
    {
        lock (_sync)
        {
            ThrowIfComplete();
            _position = position;
            _modifiers = modifiers;
            DragInfo info = CreateInfo(
                position,
                modifiers);
            Task next = LeaveAfter(
                _tail,
                info);
            _tail = next;
            Observe(next);
            return next;
        }
    }

    internal Task<DataPackageOperation> Drop(
        Point position,
        DragDropModifiers modifiers)
    {
        lock (_sync)
        {
            ThrowIfComplete();
            _isComplete = true;
            _position = position;
            _modifiers = modifiers;
            DragInfo info = CreateInfo(
                position,
                modifiers);
            Task<DataPackageOperation> next =
                DropAfter(
                    _tail,
                    info);
            _tail = next;
            Observe(next);
            return next;
        }
    }

    internal void Cancel()
    {
        lock (_sync)
        {
            if (_isComplete)
                return;
            _isComplete = true;
            DragInfo info = CreateInfo(
                _position,
                _modifiers);
            Task leave = LeaveAfter(
                _tail,
                info);
            _tail = leave;
            _ = CompleteCanceled(leave);
        }
    }

    internal ProGPU.WinUI.Platform
        .DragDropVisualSnapshot GetVisual()
    {
        lock (_sync)
        {
            return _uiOverride.GetSnapshot();
        }
    }

    private async Task EnterCore(
        DragInfo info)
    {
        DataPackageOperation accepted =
            await _target.EnterAsync(
                    info,
                    _uiOverride)
                .AsTask()
                .ConfigureAwait(false);
        _ = Clamp(accepted);
    }

    private async Task<DataPackageOperation>
        OverAfter(
            Task previous,
            DragInfo info)
    {
        await previous.ConfigureAwait(false);
        DataPackageOperation accepted =
            await _target.OverAsync(
                    info,
                    _uiOverride)
                .AsTask()
                .ConfigureAwait(false);
        return Clamp(accepted);
    }

    private async Task LeaveAfter(
        Task previous,
        DragInfo info)
    {
        await previous.ConfigureAwait(false);
        await _target.LeaveAsync(
                info)
            .AsTask()
            .ConfigureAwait(false);
    }

    private async Task<DataPackageOperation>
        DropAfter(
            Task previous,
            DragInfo info)
    {
        try
        {
            await previous.ConfigureAwait(false);
            DataPackageOperation result =
                Clamp(await _target.DropAsync(
                            info)
                        .AsTask()
                        .ConfigureAwait(false));
            _completion.TrySetResult(result);
            return result;
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
            throw;
        }
    }

    private async Task CompleteCanceled(
        Task leave)
    {
        try
        {
            await leave.ConfigureAwait(false);
            _completion.TrySetResult(
                DataPackageOperation.None);
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
    }

    private DragInfo CreateInfo(
        Point position,
        DragDropModifiers modifiers) =>
        new(
            _data,
            _allowedOperations,
            modifiers,
            position);

    private DataPackageOperation Clamp(
        DataPackageOperation value) =>
        value & _allowedOperations;

    private void ThrowIfComplete()
    {
        if (_isComplete)
        {
            throw new InvalidOperationException(
                "The drag operation is complete.");
        }
    }

    private void Observe(Task task)
    {
        if (task.IsCompletedSuccessfully)
            return;
        _ = ObserveCore(task);
    }

    private async Task ObserveCore(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
    }

    private static DragDropModifiers ToModifiers(
        PointerPointProperties properties)
    {
        DragDropModifiers modifiers =
            DragDropModifiers.None;
        if (properties.IsLeftButtonPressed)
            modifiers |=
                DragDropModifiers.LeftButton;
        if (properties.IsMiddleButtonPressed)
            modifiers |=
                DragDropModifiers.MiddleButton;
        if (properties.IsRightButtonPressed)
            modifiers |=
                DragDropModifiers.RightButton;
        return modifiers;
    }
}
