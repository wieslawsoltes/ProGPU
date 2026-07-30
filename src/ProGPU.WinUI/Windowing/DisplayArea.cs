using Microsoft.UI.Dispatching;
using ProGPU.WinUI.Platform;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Graphics;

namespace Microsoft.UI.Windowing;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class DisplayArea
{
    private readonly WindowingDisplayAreaInfo _info;

    internal DisplayArea(in WindowingDisplayAreaInfo info)
    {
        _info = info;
    }

    public DisplayId DisplayId => _info.DisplayId;

    public bool IsPrimary => _info.IsPrimary;

    public RectInt32 OuterBounds => _info.OuterBounds;

    public RectInt32 WorkArea => _info.WorkArea;

    public static DisplayArea Primary =>
        FindAll().FirstOrDefault(area => area.IsPrimary) ??
        throw new InvalidOperationException(
            "The platform did not report a primary display area.");

    public static DisplayAreaWatcher CreateWatcher() => new();

    public static IReadOnlyList<DisplayArea> FindAll()
    {
        IReadOnlyList<WindowingDisplayAreaInfo> source =
            GetProvider().GetDisplayAreas();
        var result = new DisplayArea[source.Count];
        for (int index = 0; index < source.Count; index++)
            result[index] = new DisplayArea(source[index]);
        return result;
    }

    public static DisplayArea GetFromDisplayId(DisplayId displayId) =>
        FindAll().FirstOrDefault(
            area => area.DisplayId == displayId)!;

    public static DisplayArea GetFromPoint(
        PointInt32 point,
        DisplayAreaFallback displayAreaFallback)
    {
        IReadOnlyList<DisplayArea> areas = FindAll();
        foreach (DisplayArea area in areas)
        {
            if (Contains(area.OuterBounds, point))
                return area;
        }

        return ResolveFallback(
            areas,
            displayAreaFallback,
            point.X,
            point.Y);
    }

    public static DisplayArea GetFromRect(
        RectInt32 rect,
        DisplayAreaFallback displayAreaFallback)
    {
        IReadOnlyList<DisplayArea> areas = FindAll();
        DisplayArea? best = null;
        long bestIntersection = 0;
        foreach (DisplayArea area in areas)
        {
            long intersection =
                IntersectionArea(area.OuterBounds, rect);
            if (intersection <= bestIntersection)
                continue;
            bestIntersection = intersection;
            best = area;
        }

        if (best is not null)
            return best;

        return ResolveFallback(
            areas,
            displayAreaFallback,
            (long)rect.X + rect.Width / 2L,
            (long)rect.Y + rect.Height / 2L);
    }

    public static DisplayArea GetFromWindowId(
        WindowId windowId,
        DisplayAreaFallback displayAreaFallback)
    {
        AppWindow? window = AppWindow.GetFromWindowId(windowId);
        if (window is null)
        {
            return displayAreaFallback == DisplayAreaFallback.Primary
                ? Primary
                : null!;
        }

        return GetFromRect(
            new RectInt32(
                window.Position.X,
                window.Position.Y,
                window.Size.Width,
                window.Size.Height),
            displayAreaFallback);
    }

    internal WindowingDisplayAreaInfo Snapshot => _info;

    private static IWindowingDisplayAreaProvider GetProvider() =>
        WindowingPlatformServices.DisplayAreas ??
        throw new PlatformNotSupportedException(
            "The current ProGPU host does not provide display-area enumeration.");

    private static DisplayArea ResolveFallback(
        IReadOnlyList<DisplayArea> areas,
        DisplayAreaFallback fallback,
        long x,
        long y)
    {
        if (fallback == DisplayAreaFallback.None)
            return null!;
        if (fallback == DisplayAreaFallback.Primary)
        {
            return areas.FirstOrDefault(area => area.IsPrimary)!;
        }

        DisplayArea? nearest = null;
        ulong nearestDistance = ulong.MaxValue;
        foreach (DisplayArea area in areas)
        {
            RectInt32 bounds = area.OuterBounds;
            long nearestX = Math.Clamp(
                x,
                bounds.X,
                (long)bounds.X + Math.Max(0, bounds.Width));
            long nearestY = Math.Clamp(
                y,
                bounds.Y,
                (long)bounds.Y + Math.Max(0, bounds.Height));
            ulong dx = checked((ulong)Math.Abs(x - nearestX));
            ulong dy = checked((ulong)Math.Abs(y - nearestY));
            ulong distance = SaturatingSquareSum(dx, dy);
            if (distance >= nearestDistance)
                continue;
            nearestDistance = distance;
            nearest = area;
        }

        return nearest!;
    }

    private static bool Contains(RectInt32 bounds, PointInt32 point) =>
        point.X >= bounds.X &&
        point.Y >= bounds.Y &&
        (long)point.X < (long)bounds.X + bounds.Width &&
        (long)point.Y < (long)bounds.Y + bounds.Height;

    private static long IntersectionArea(RectInt32 left, RectInt32 right)
    {
        long x1 = Math.Max(left.X, right.X);
        long y1 = Math.Max(left.Y, right.Y);
        long x2 = Math.Min(
            (long)left.X + left.Width,
            (long)right.X + right.Width);
        long y2 = Math.Min(
            (long)left.Y + left.Height,
            (long)right.Y + right.Height);
        return Math.Max(0, x2 - x1) *
            Math.Max(0, y2 - y1);
    }

    private static ulong SaturatingSquareSum(ulong left, ulong right)
    {
        if (left > uint.MaxValue || right > uint.MaxValue)
            return ulong.MaxValue;
        ulong leftSquared = left * left;
        ulong rightSquared = right * right;
        return ulong.MaxValue - leftSquared < rightSquared
            ? ulong.MaxValue
            : leftSquared + rightSquared;
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class DisplayAreaWatcher
{
    private readonly DispatcherQueue? _dispatcherQueue =
        DispatcherQueue.GetForCurrentThread();
    private readonly Dictionary<DisplayId, DisplayArea> _areas = [];
    private IWindowingDisplayAreaProvider? _provider;
    private DisplayAreaWatcherStatus _status;

    internal DisplayAreaWatcher()
    {
    }

    public DisplayAreaWatcherStatus Status => _status;

    public event TypedEventHandler<DisplayAreaWatcher, DisplayArea>? Added;

    public event TypedEventHandler<DisplayAreaWatcher, DisplayArea>? Removed;

    public event TypedEventHandler<DisplayAreaWatcher, DisplayArea>? Updated;

    public event TypedEventHandler<DisplayAreaWatcher, object>?
        EnumerationCompleted;

    public event TypedEventHandler<DisplayAreaWatcher, object>? Stopped;

    public void Start()
    {
        VerifyAccess();
        if (_status != DisplayAreaWatcherStatus.Created)
        {
            throw new InvalidOperationException(
                "A DisplayAreaWatcher can only be started once.");
        }

        _provider = WindowingPlatformServices.DisplayAreas ??
            throw new PlatformNotSupportedException(
                "The current ProGPU host does not provide display-area enumeration.");
        _provider.DisplayAreasChanged += OnDisplayAreasChanged;
        _status = DisplayAreaWatcherStatus.Started;
        PublishSnapshot(isInitial: true);
        _status = DisplayAreaWatcherStatus.EnumerationCompleted;
        EnumerationCompleted?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        VerifyAccess();
        if (_status is DisplayAreaWatcherStatus.Stopped or
            DisplayAreaWatcherStatus.Aborted)
        {
            return;
        }

        _status = DisplayAreaWatcherStatus.Stopping;
        if (_provider is not null)
            _provider.DisplayAreasChanged -= OnDisplayAreasChanged;
        _provider = null;
        _areas.Clear();
        _status = DisplayAreaWatcherStatus.Stopped;
        Stopped?.Invoke(this, EventArgs.Empty);
    }

    private void OnDisplayAreasChanged(object? sender, EventArgs args)
    {
        if (_dispatcherQueue is not null &&
            !_dispatcherQueue.HasThreadAccess)
        {
            _ = _dispatcherQueue.TryEnqueue(
                () => PublishSnapshot(isInitial: false));
            return;
        }

        PublishSnapshot(isInitial: false);
    }

    private void PublishSnapshot(bool isInitial)
    {
        if (_provider is null ||
            _status is DisplayAreaWatcherStatus.Stopping or
                DisplayAreaWatcherStatus.Stopped)
        {
            return;
        }

        IReadOnlyList<WindowingDisplayAreaInfo> snapshot =
            _provider.GetDisplayAreas();
        var retained = new HashSet<DisplayId>();
        foreach (WindowingDisplayAreaInfo info in snapshot)
        {
            retained.Add(info.DisplayId);
            if (!_areas.TryGetValue(info.DisplayId, out DisplayArea? area))
            {
                area = new DisplayArea(info);
                _areas.Add(info.DisplayId, area);
                Added?.Invoke(this, area);
            }
            else if (!area.Snapshot.Equals(info))
            {
                area = new DisplayArea(info);
                _areas[info.DisplayId] = area;
                Updated?.Invoke(this, area);
            }
        }

        if (isInitial)
            return;

        foreach (DisplayId id in _areas.Keys.ToArray())
        {
            if (retained.Contains(id))
                continue;
            DisplayArea removed = _areas[id];
            _areas.Remove(id);
            Removed?.Invoke(this, removed);
        }
    }

    private void VerifyAccess()
    {
        if (_dispatcherQueue is not null &&
            !_dispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException(
                "This operation must run on the watcher's DispatcherQueue.");
        }
    }
}
