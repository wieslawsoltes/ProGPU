using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private sealed class WaypointGesture(string document, string screen, string link, int index, List<HmiPoint> points)
    {
        internal readonly string Document = document, Screen = screen, Link = link;
        internal readonly int Index = index;
        internal List<HmiPoint> Points = points;
        internal Pointer? Pointer;
    }
    private HmiRouteAdorner? _routeAdorner;
    private WaypointGesture? _waypointGesture;
    private bool _placingWaypoint;
    private int _selectedWaypointIndex = -1;
    public bool IsEditingRoute => _placingWaypoint || _waypointGesture != null;
    public int SelectedWaypointIndex => _selectedWaypointIndex;

    private void InitializeRouteEditing()
    {
        _routeAdorner = new HmiRouteAdorner(_canvas) { IsHitTestVisible = false, LabelFont = _font };
        _canvas.AdornerSurface.Children.Add(_routeAdorner);
        _canvas.RoutePointerPressed = HandleRoutePointerPressed;
        _canvas.RoutePointerMoved = HandleRoutePointerMoved;
        _canvas.RoutePointerReleased = HandleRoutePointerReleased;
        _canvas.RoutePointerCanceled = e =>
        {
            if (_waypointGesture?.Pointer?.PointerId == e.Pointer.PointerId) CancelRouteEdit();
        };
    }

    /// <summary>Next canvas click adds an exact, grid-snapped constraint to the selected link.</summary>
    public void BeginWaypointPlacement() => DesignCommand(() =>
    {
        RequireEditableRoute();
        CancelCanvasAuthoring(); CancelLabelEdit(); CancelDiagramConnection(); CancelRouteEdit();
        _placingWaypoint = true;
        Status("Add waypoint: click where the route must pass. Pins are ordered along the current route. Escape cancels.");
    });

    public void InsertRouteWaypoint(int index, HmiPoint point)
    {
        RequireEditableRoute();
        HmiRouteWaypoints.ValidatePoint(point);
        EditSelectedLink("Insert route waypoint", link =>
        {
            if ((uint)index > (uint)link.Waypoints.Count) throw new ArgumentOutOfRangeException(nameof(index));
            link.Waypoints.Insert(index, point);
        });
        _selectedWaypointIndex = index;
        UpdateInspector(); ReportRoute();
    }

    public void MoveRouteWaypoint(int index, HmiPoint point)
    {
        RequireEditableRoute(); HmiRouteWaypoints.ValidatePoint(point);
        EditSelectedLink("Move route waypoint", link =>
        {
            if ((uint)index >= (uint)link.Waypoints.Count) throw new ArgumentOutOfRangeException(nameof(index));
            link.Waypoints[index] = point;
        });
        _selectedWaypointIndex = index;
        UpdateInspector(); ReportRoute();
    }

    public void SelectRouteWaypoint(int index)
    {
        var link = Session.ActiveScreen.Links.SingleOrDefault(l => l.Id == _selectedLinkId);
        if (index < -1 || index >= (link?.Waypoints.Count ?? 0)) throw new ArgumentOutOfRangeException(nameof(index));
        CancelRouteEdit();
        _selectedWaypointIndex = index;
        UpdateInspector();
    }

    public void RemoveSelectedWaypoint()
    {
        if (_selectedWaypointIndex < 0) return;
        RequireEditableRoute();
        int index = _selectedWaypointIndex;
        EditSelectedLink("Remove route waypoint", link => link.Waypoints.RemoveAt(index));
        _selectedWaypointIndex = -1;
        UpdateInspector(); ReportRoute();
    }

    public void ClearSelectedWaypoints()
    {
        RequireEditableRoute();
        EditSelectedLink("Restore automatic routing", link => link.Waypoints.Clear());
        _selectedWaypointIndex = -1;
        UpdateInspector(); ReportRoute();
    }

    /// <summary>Begin a detached preview. Does not serialize a new snapshot or write history on pointer moves.</summary>
    public void BeginWaypointDrag(int index)
    {
        var link = RequireEditableRoute();
        if ((uint)index >= (uint)link.Waypoints.Count) throw new ArgumentOutOfRangeException(nameof(index));
        CancelCanvasAuthoring(); CancelLabelEdit(); CancelDiagramConnection(); CancelRouteEdit();
        _selectedWaypointIndex = index;
        _waypointGesture = new(Session.ExportJson(), Session.ActiveScreenId, link.Id, index, [.. link.Waypoints]);
        UpdateInspector();
    }

    public void PreviewWaypointPosition(HmiPoint point)
    {
        var gesture = _waypointGesture ?? throw new InvalidOperationException("Begin a waypoint gesture first.");
        if (IsPreviewing || Session.ActiveScreenId != gesture.Screen || Session.ExportJson() != gesture.Document)
        { CancelRouteEdit(); throw new InvalidOperationException("The design changed; the route gesture was canceled."); }
        if (gesture.Points[gesture.Index] == point) return;
        var next = new List<HmiPoint>(gesture.Points) { [gesture.Index] = point };
        HmiRouteWaypoints.Validate(next);
        var route = DiagramLayer.PreviewWaypoints(gesture.Link, next);
        gesture.Points = next;
        RefreshWaypointAdorner();
        Status(route.Status == HmiRouteStatus.Success
            ? $"Waypoint {gesture.Index + 1}: {point.X:0.##}, {point.Y:0.##}. Release to commit; Escape cancels."
            : route.Diagnostic, error: false);
    }

    /// <summary>Publish exactly one validated project transaction. Capture loss and foreign revisions cancel instead.</summary>
    public void CommitWaypointDrag()
    {
        var gesture = _waypointGesture;
        if (gesture == null) return;
        _waypointGesture = null;
        // Clear the gesture before release: native capture-lost callbacks may run synchronously.
        if (gesture.Pointer != null) _canvas.ReleasePointerCapture(gesture.Pointer);
        try
        {
            if (IsPreviewing || Session.ActiveScreenId != gesture.Screen || Session.ExportJson() != gesture.Document)
                throw new InvalidOperationException("The design changed; the route gesture was canceled.");
            EditLink(gesture.Link, "Move route waypoint", link => link.Waypoints = [.. gesture.Points], allowLocked: false);
            _selectedWaypointIndex = gesture.Index;
            ReportRoute();
        }
        finally
        {
            DiagramLayer.SetScreen(Session.ActiveScreen);
            UpdateInspector();
        }
    }

    public void CancelRouteEdit()
    {
        _placingWaypoint = false;
        var gesture = _waypointGesture;
        _waypointGesture = null;
        if (gesture != null)
        {
            if (gesture.Pointer != null) _canvas.ReleasePointerCapture(gesture.Pointer);
            DiagramLayer.SetScreen(Session.ActiveScreen);
        }
        RefreshWaypointAdorner();
    }

    private HmiDiagramLink RequireEditableRoute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPreviewing) throw new InvalidOperationException("Stop simulation or live acquisition before editing routes.");
        var link = Session.ActiveScreen.Links.SingleOrDefault(l => l.Id == _selectedLinkId)
            ?? throw new InvalidOperationException("Select a diagram link first.");
        if (link.IsLocked) throw new InvalidOperationException("Unlock the diagram link before editing its route.");
        if (link.IsHidden || !DiagramLayer.Routes.ContainsKey(link.Id)) throw new InvalidOperationException("Show the link and its equipment before editing waypoints.");
        return link;
    }

    private bool HandleRoutePointerPressed(PointerRoutedEventArgs e)
    {
        if (_waypointGesture != null) return true;
        if (IsPreviewing || _canvas.IsInteractionMode || !e.IsLeftButtonPressed || e.IsMiddleButtonPressed || e.IsRightButtonPressed) return false;
        var point = RoutePoint(e, snap: false);
        if (_placingWaypoint)
        {
            Guard(() =>
            {
                InputSystem.SetFocus(_canvas);
                var link = RequireEditableRoute();
                int index = HmiRouteWaypoints.FindInsertionIndex(DiagramLayer.Routes.GetValueOrDefault(link.Id), link.Waypoints, point);
                InsertRouteWaypoint(index, RoutePoint(e, snap: true));
                _placingWaypoint = false;
            });
            return true;
        }
        int hit = _routeAdorner?.Hit(point, 9 / _canvas.ZoomScale) ?? -1;
        if (hit < 0) { _selectedWaypointIndex = -1; return false; }
        Guard(() =>
        {
            InputSystem.SetFocus(_canvas);
            BeginWaypointDrag(hit);
            var gesture = _waypointGesture!;
            gesture.Pointer = e.Pointer;
            if (!_canvas.CapturePointer(e.Pointer))
            {
                if (ReferenceEquals(_waypointGesture, gesture)) CancelRouteEdit();
                throw new InvalidOperationException("The pointer could not be captured; no route edit was started.");
            }
            // Cursor/capture callbacks may invoke host code and retire this gesture.
            if (!ReferenceEquals(_waypointGesture, gesture)) _canvas.ReleasePointerCapture(e.Pointer);
        });
        return true;
    }

    private bool HandleRoutePointerMoved(PointerRoutedEventArgs e)
    {
        if (_waypointGesture == null) return false;
        if (_waypointGesture.Pointer?.PointerId != e.Pointer.PointerId) return true;
        if (e.IsCanceled || e.IsMiddleButtonPressed || e.IsRightButtonPressed || !e.IsLeftButtonPressed)
        { CancelRouteEdit(); return true; }
        Guard(() => PreviewWaypointPosition(RoutePoint(e, snap: true)));
        return true;
    }

    private bool HandleRoutePointerReleased(PointerRoutedEventArgs e)
    {
        if (_waypointGesture == null) return false;
        if (_waypointGesture.Pointer?.PointerId != e.Pointer.PointerId) return true;
        if (e.IsCanceled) CancelRouteEdit();
        else Guard(() =>
        {
            // A release may carry a final position not previously delivered by a move event.
            try { PreviewWaypointPosition(RoutePoint(e, snap: true)); CommitWaypointDrag(); }
            catch { CancelRouteEdit(); throw; }
        });
        return true;
    }

    private HmiPoint RoutePoint(PointerRoutedEventArgs e, bool snap)
    {
        var point = (e.Position - _canvas.PanOffset) / _canvas.ZoomScale;
        if (snap && _canvas.GridSnappingEnabled && float.IsFinite(_canvas.GridSize) && _canvas.GridSize > 0)
        {
            point.X = _canvas.SnapToGrid(point.X, _canvas.GridSize);
            point.Y = _canvas.SnapToGrid(point.Y, _canvas.GridSize);
        }
        return new(point.X, point.Y);
    }

    private void RefreshWaypointAdorner()
    {
        if (_routeAdorner == null) return;
        var link = Session.ActiveScreen.Links.SingleOrDefault(l => l.Id == _selectedLinkId);
        var points = _waypointGesture?.Points ?? link?.Waypoints;
        if (_selectedWaypointIndex >= (points?.Count ?? 0)) _selectedWaypointIndex = -1;
        _routeAdorner.Scheme = ColorScheme;
        _routeAdorner.SelectedIndex = _selectedWaypointIndex;
        _routeAdorner.Locked = link?.IsLocked == true;
        _routeAdorner.Blocked = link != null && DiagramLayer.Routes.TryGetValue(link.Id, out var route) && route.Status != HmiRouteStatus.Success;
        _routeAdorner.SetPoints(!IsPreviewing && link is { IsHidden: false } && DiagramLayer.Routes.ContainsKey(link.Id) ? points ?? [] : []);
    }

    private void BuildWaypointInspector(HmiDiagramLink link)
    {
        ReadOnlyProperty("Routing constraints", $"{link.Waypoints.Count} / {HmiRouteWaypoints.MaximumCount} pins · drag handles; Delete removes the selected pin");
        ReadOnlyProperty("Selected pin", _selectedWaypointIndex >= 0 ? (_selectedWaypointIndex + 1).ToString(Invariant) : "None");
        for (int i = 0; i < link.Waypoints.Count; i++)
        {
            int index = i;
            if (link.IsLocked) { ReadOnlyProperty($"Waypoint {i + 1}", $"{Format(link.Waypoints[i].X)}, {Format(link.Waypoints[i].Y)}"); continue; }
            LinkProperty($"Pin {i + 1} X", Format(link.Waypoints[i].X), (l, v) => l.Waypoints[index] = l.Waypoints[index] with { X = Coordinate(v) });
            LinkProperty($"Pin {i + 1} Y", Format(link.Waypoints[i].Y), (l, v) => l.Waypoints[index] = l.Waypoints[index] with { Y = Coordinate(v) });
        }
    }

    private void NudgeDesignSelection(float dx, float dy)
    {
        if (_selectedLinkId != null && _selectedWaypointIndex >= 0)
        {
            var link = RequireEditableRoute();
            var point = link.Waypoints[_selectedWaypointIndex];
            MoveRouteWaypoint(_selectedWaypointIndex, new(point.X + dx, point.Y + dy));
        }
        else _selection.Translate(dx, dy);
    }

    private void ReportRoute()
    {
        if (_selectedLinkId != null && DiagramLayer.Routes.TryGetValue(_selectedLinkId, out var route))
            Status(route.Diagnostic, route.Status != HmiRouteStatus.Success);
    }
}
