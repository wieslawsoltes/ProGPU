using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Content;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public class ContentSiteEnvironment
{
    private readonly object _islandsSync = new();
    private readonly ContentEnvironmentCore _core;
    private readonly List<ContentIslandEnvironment>
        _islands = [];

    protected internal ContentSiteEnvironment(
        WinRT.IObjectReference objRef)
        : this(new ContentEnvironmentCore())
    {
        ArgumentNullException.ThrowIfNull(objRef);
    }

    protected ContentSiteEnvironment(
        WinRT.DerivedComposed _)
        : this(new ContentEnvironmentCore())
    {
        ArgumentNullException.ThrowIfNull(_);
    }

    internal ContentSiteEnvironment()
        : this(new ContentEnvironmentCore())
    {
    }

    private ContentSiteEnvironment(
        ContentEnvironmentCore core)
    {
        _core = core;
        View = new ContentSiteEnvironmentView(core);
    }

    public WindowId AppWindowId
    {
        get => _core.Read().AppWindowId;
        set => _core.SetAppWindowId(value);
    }

    public DisplayId DisplayId
    {
        get => _core.Read().DisplayId;
        set => _core.SetDisplayId(value);
    }

    public float DisplayScale
    {
        get => _core.Read().DisplayScale;
        set => _core.SetDisplayScale(value);
    }

    public ContentSiteEnvironmentView View { get; }

    public void NotifySettingChanged(
        string setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        lock (_islandsSync)
        {
            foreach (
                ContentIslandEnvironment island
                in _islands)
            {
                island.NotifySettingChanged(setting);
            }
        }
    }

    internal void Attach(
        ContentIslandEnvironment island)
    {
        ArgumentNullException.ThrowIfNull(island);
        lock (_islandsSync)
        {
            if (!_islands.Contains(island))
                _islands.Add(island);
        }
    }

    internal void Detach(
        ContentIslandEnvironment island)
    {
        ArgumentNullException.ThrowIfNull(island);
        lock (_islandsSync)
            _islands.Remove(island);
    }

    internal void PropagateTo(
        ContentIslandEnvironment island)
    {
        ArgumentNullException.ThrowIfNull(island);
        island.Apply(_core.Read());
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public class ContentSiteEnvironmentView
{
    private readonly ContentEnvironmentCore _core;

    protected internal ContentSiteEnvironmentView(
        WinRT.IObjectReference objRef)
        : this(new ContentEnvironmentCore())
    {
        ArgumentNullException.ThrowIfNull(objRef);
    }

    protected ContentSiteEnvironmentView(
        WinRT.DerivedComposed _)
        : this(new ContentEnvironmentCore())
    {
        ArgumentNullException.ThrowIfNull(_);
    }

    internal ContentSiteEnvironmentView(
        ContentEnvironmentCore core)
    {
        _core = core;
    }

    public WindowId AppWindowId =>
        _core.Read().AppWindowId;

    public DisplayId DisplayId =>
        _core.Read().DisplayId;

    public float DisplayScale =>
        _core.Read().DisplayScale;
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public class ContentIslandEnvironment
{
    private const int AppWindowIdFlag = 1;
    private const int DisplayIdFlag = 2;
    private const int DisplayScaleFlag = 4;

    private readonly object _eventSync = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueHandler
        _processSettings;
    private readonly DispatcherQueueHandler
        _processState;
    private readonly Queue<string> _pendingSettings =
        new();
    private ContentEnvironmentSnapshot _snapshot;
    private int _pendingStateChanges;
    private bool _isSettingNotificationScheduled;
    private bool _isStateNotificationScheduled;

    protected internal ContentIslandEnvironment(
        WinRT.IObjectReference objRef)
        : this(RequireDispatcherQueue())
    {
        ArgumentNullException.ThrowIfNull(objRef);
    }

    protected ContentIslandEnvironment(
        WinRT.DerivedComposed _)
        : this(RequireDispatcherQueue())
    {
        ArgumentNullException.ThrowIfNull(_);
    }

    internal ContentIslandEnvironment(
        DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue ??
            throw new ArgumentNullException(
                nameof(dispatcherQueue));
        _snapshot =
            ContentEnvironmentSnapshot.Default;
        _processSettings =
            ProcessSettingNotifications;
        _processState =
            ProcessStateNotification;
    }

    public WindowId AppWindowId =>
        Volatile.Read(ref _snapshot).AppWindowId;

    public DisplayId DisplayId =>
        Volatile.Read(ref _snapshot).DisplayId;

    public float DisplayScale =>
        Volatile.Read(ref _snapshot).DisplayScale;

    public event TypedEventHandler<
        ContentIslandEnvironment,
        ContentEnvironmentSettingChangedEventArgs>?
        SettingChanged;

    public event TypedEventHandler<
        ContentIslandEnvironment,
        ContentEnvironmentStateChangedEventArgs>?
        StateChanged;

    internal void Apply(
        ContentEnvironmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ContentEnvironmentSnapshot previous =
            Interlocked.Exchange(
                ref _snapshot,
                snapshot);
        int changes = GetChanges(
            previous,
            snapshot);
        if (changes == 0 ||
            StateChanged is null)
        {
            return;
        }

        lock (_eventSync)
        {
            _pendingStateChanges |= changes;
            if (_isStateNotificationScheduled)
                return;

            _isStateNotificationScheduled = true;
            if (!_dispatcherQueue.TryEnqueue(
                    _processState))
            {
                _isStateNotificationScheduled =
                    false;
            }
        }
    }

    internal void NotifySettingChanged(
        string setting)
    {
        if (SettingChanged is null)
            return;

        lock (_eventSync)
        {
            _pendingSettings.Enqueue(setting);
            if (_isSettingNotificationScheduled)
                return;

            _isSettingNotificationScheduled = true;
            if (!_dispatcherQueue.TryEnqueue(
                    _processSettings))
            {
                _isSettingNotificationScheduled =
                    false;
            }
        }
    }

    private void ProcessSettingNotifications()
    {
        while (true)
        {
            string setting;
            lock (_eventSync)
            {
                if (!_pendingSettings.TryDequeue(
                        out setting!))
                {
                    _isSettingNotificationScheduled =
                        false;
                    return;
                }
            }

            SettingChanged?.Invoke(
                this,
                new(
                    setting));
        }
    }

    private void ProcessStateNotification()
    {
        int changes;
        lock (_eventSync)
        {
            changes = _pendingStateChanges;
            _pendingStateChanges = 0;
            _isStateNotificationScheduled = false;
        }
        if (changes == 0)
            return;

        StateChanged?.Invoke(
            this,
            new(
                didAppWindowIdChange:
                    (changes &
                        AppWindowIdFlag) != 0,
                didDisplayIdChange:
                    (changes &
                        DisplayIdFlag) != 0,
                didDisplayScaleChange:
                    (changes &
                        DisplayScaleFlag) != 0));
    }

    private static int GetChanges(
        ContentEnvironmentSnapshot previous,
        ContentEnvironmentSnapshot current)
    {
        int changes = 0;
        if (previous.AppWindowId !=
            current.AppWindowId)
        {
            changes |= AppWindowIdFlag;
        }
        if (previous.DisplayId !=
            current.DisplayId)
        {
            changes |= DisplayIdFlag;
        }
        if (previous.DisplayScale !=
            current.DisplayScale)
        {
            changes |= DisplayScaleFlag;
        }
        return changes;
    }

    private static DispatcherQueue
        RequireDispatcherQueue() =>
        DispatcherQueue.GetForCurrentThread() ??
        throw new InvalidOperationException(
            "ContentIslandEnvironment requires a DispatcherQueue on the current thread.");
}

internal sealed class ContentEnvironmentCore
{
    private ContentEnvironmentSnapshot _snapshot =
        ContentEnvironmentSnapshot.Default;

    internal ContentEnvironmentSnapshot Read() =>
        Volatile.Read(ref _snapshot);

    internal void SetAppWindowId(
        WindowId value)
    {
        while (true)
        {
            ContentEnvironmentSnapshot current =
                Read();
            if (current.AppWindowId == value)
                return;
            var replacement =
                new ContentEnvironmentSnapshot(
                    value,
                    current.DisplayId,
                    current.DisplayScale);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _snapshot,
                        replacement,
                        current),
                    current))
            {
                return;
            }
        }
    }

    internal void SetDisplayId(
        DisplayId value)
    {
        while (true)
        {
            ContentEnvironmentSnapshot current =
                Read();
            if (current.DisplayId == value)
                return;
            var replacement =
                new ContentEnvironmentSnapshot(
                    current.AppWindowId,
                    value,
                    current.DisplayScale);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _snapshot,
                        replacement,
                        current),
                    current))
            {
                return;
            }
        }
    }

    internal void SetDisplayScale(
        float value)
    {
        if (!float.IsFinite(value) ||
            value <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value));
        }

        while (true)
        {
            ContentEnvironmentSnapshot current =
                Read();
            if (current.DisplayScale == value)
                return;
            var replacement =
                new ContentEnvironmentSnapshot(
                    current.AppWindowId,
                    current.DisplayId,
                    value);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _snapshot,
                        replacement,
                        current),
                    current))
            {
                return;
            }
        }
    }
}

internal sealed class ContentEnvironmentSnapshot
{
    internal static readonly
        ContentEnvironmentSnapshot Default =
        new(
            default,
            default,
            1f);

    internal ContentEnvironmentSnapshot(
        WindowId appWindowId,
        DisplayId displayId,
        float displayScale)
    {
        AppWindowId = appWindowId;
        DisplayId = displayId;
        DisplayScale = displayScale;
    }

    internal WindowId AppWindowId { get; }

    internal DisplayId DisplayId { get; }

    internal float DisplayScale { get; }
}
