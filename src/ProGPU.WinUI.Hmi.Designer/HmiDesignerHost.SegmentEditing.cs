using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private sealed class SegmentGesture(string document, string screen, string link, HmiRouteSegmentEdit plan)
    {
        internal readonly string Document = document, Screen = screen, Link = link;
        internal readonly HmiRouteSegmentEdit Plan = plan;
        internal HmiRouteConstraints? Preview;
        internal float Offset;
        internal Pointer? Pointer;
        internal HmiPoint Anchor;
    }
    private SegmentGesture? _segmentGesture;
    public bool IsDraggingRouteSegment => _segmentGesture != null;

    public IReadOnlyList<HmiRouteSegment> GetSelectedRouteSegments() => _selectedLinkId == null
        ? Array.Empty<HmiRouteSegment>() : DiagramLayer.GetEditableSegments(_selectedLinkId);

    /// <summary>Capture one computed segment; its fixed nozzle escape leads are never part of the movable portion.</summary>
    public void BeginSegmentDrag(int routeSegmentIndex)
    {
        var link = RequireEditableRoute();
        if (_canvas.IsInteractionMode) throw new InvalidOperationException("Exit interaction mode before editing segments.");
        var segment = GetSelectedRouteSegments().FirstOrDefault(s => s.Index == routeSegmentIndex);
        if (segment.Start == segment.End) throw new ArgumentOutOfRangeException(nameof(routeSegmentIndex), "Select a free route segment, not a fixed nozzle lead.");
        var plan = segment.StraightSpanIndex >= 0
            ? new HmiRouteSegmentEdit(link.Waypoints, link.StraightSegments, segment.StraightSpanIndex)
            : new HmiRouteSegmentEdit(DiagramLayer.Routes[link.Id], segment, link.Waypoints, link.StraightSegments);
        string document = Session.ExportJson(), screen = Session.ActiveScreenId;
        CancelCanvasAuthoring(); CancelLabelEdit(); CancelDiagramConnection();
        long epoch = RetireRouteEdit(); EnsureRouteRevision(document, screen, epoch);
        _selectedWaypointIndex = -1;
        _segmentGesture = new(document, screen, link.Id, plan);
        RefreshWaypointAdorner();
    }

    /// <summary>Preview a document-space perpendicular displacement; only this link is rerouted.</summary>
    public void PreviewSegmentOffset(float offset)
    {
        var gesture = _segmentGesture ?? throw new InvalidOperationException("Begin a segment gesture first.");
        EnsureRouteRevision(gesture.Document, gesture.Screen, _routeEditEpoch);
        if (offset == gesture.Offset) return;
        var constraints = gesture.Plan.Preview(offset);
        var route = DiagramLayer.PreviewConstraints(gesture.Link, constraints.Waypoints, constraints.StraightSegments);
        gesture.Preview = constraints; gesture.Offset = offset;
        RefreshWaypointAdorner();
        Status(route.Status == HmiRouteStatus.Success
            ? FormattableString.Invariant($"Move segment: {(gesture.Plan.Segment.IsHorizontal ? "Y" : "X")} offset {offset:0.##} · release commits one edit · Escape cancels")
            : route.Diagnostic);
    }

    public void CommitSegmentDrag()
    {
        var gesture = _segmentGesture;
        if (gesture == null) return;
        long epoch = RetireRouteEdit();
        EnsureRouteRevision(gesture.Document, gesture.Screen, epoch);
        if (gesture.Offset == 0 || gesture.Preview == null) return; // A grip click is not a document edit.
        EditLink(gesture.Link, "Move route segment", link =>
        {
            link.Waypoints = [.. gesture.Preview.Waypoints];
            link.StraightSegments = [.. gesture.Preview.StraightSegments];
        }, allowLocked: false);
        if (!IsEditingRoute) { UpdateInspector(); ReportRoute(); }
    }

    /// <summary>Explicitly free straight spans while preserving the ordered pins and topology.</summary>
    public void ReleaseStraightSegments()
    {
        RequireEditableRoute(); CancelRouteEdit();
        EditSelectedLink("Release straight route constraints", link => link.StraightSegments.Clear());
        UpdateInspector(); ReportRoute();
    }

    private bool TryBeginSegmentPointer(PointerRoutedEventArgs e, HmiPoint point)
    {
        int index = _routeAdorner?.HitSegment(point) ?? -1;
        if (index < 0 || IsConnectingDiagram || IsPlacingComponent) return false;
        Guard(() =>
        {
            InputSystem.SetFocus(_canvas);
            BeginSegmentDrag(index);
            var gesture = _segmentGesture!;
            gesture.Pointer = e.Pointer; gesture.Anchor = point;
            if (!_canvas.CapturePointer(e.Pointer))
            {
                if (ReferenceEquals(_segmentGesture, gesture)) CancelRouteEdit();
                throw new InvalidOperationException("Pointer capture failed; no route was changed.");
            }
            if (!ReferenceEquals(_segmentGesture, gesture) && _segmentGesture == null && _waypointGesture == null)
                _canvas.ReleasePointerCapture(e.Pointer);
        });
        return true;
    }

    private bool HandleSegmentPointerMoved(PointerRoutedEventArgs e)
    {
        var gesture = _segmentGesture!;
        if (gesture.Pointer?.PointerId != e.Pointer.PointerId) return true;
        if (e.IsCanceled || !e.IsLeftButtonPressed || e.IsMiddleButtonPressed || e.IsRightButtonPressed)
        { CancelRouteEdit(); return true; }
        Guard(() =>
        {
            try { PreviewSegmentPointer(gesture, e); }
            catch { if (ReferenceEquals(_segmentGesture, gesture)) CancelRouteEdit(); throw; }
        });
        return true;
    }

    private bool HandleSegmentPointerReleased(PointerRoutedEventArgs e)
    {
        var gesture = _segmentGesture!;
        if (gesture.Pointer?.PointerId != e.Pointer.PointerId) return true;
        if (e.IsCanceled || e.IsMiddleButtonPressed || e.IsRightButtonPressed) { CancelRouteEdit(); return true; }
        Guard(() =>
        {
            try { PreviewSegmentPointer(gesture, e); CommitSegmentDrag(); }
            catch { if (ReferenceEquals(_segmentGesture, gesture)) CancelRouteEdit(); throw; }
        });
        return true;
    }

    private void PreviewSegmentPointer(SegmentGesture gesture, PointerRoutedEventArgs e)
    {
        var position = RoutePoint(e, snap: false);
        bool horizontal = gesture.Plan.Segment.IsHorizontal;
        float delta = horizontal ? position.Y - gesture.Anchor.Y : position.X - gesture.Anchor.X;
        // Unsnapped physical movement owns the click threshold; Alt changes cannot create an edit at rest.
        if (Math.Abs(delta) * _canvas.ZoomScale < 3) { PreviewSegmentOffset(0); return; }
        float origin = horizontal ? gesture.Plan.Segment.Start.Y : gesture.Plan.Segment.Start.X;
        if (!InputSystem.Current.IsAltPressed && _canvas.GridSnappingEnabled && float.IsFinite(_canvas.GridSize) && _canvas.GridSize > 0)
            delta = _canvas.SnapToGrid(origin + delta, _canvas.GridSize) - origin;
        PreviewSegmentOffset(delta);
    }
}
