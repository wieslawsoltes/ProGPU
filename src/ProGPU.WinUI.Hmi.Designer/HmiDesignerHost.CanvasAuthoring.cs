using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.WinUI.Designer;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private sealed class CanvasBoxGesture(string document, string screen, Vector2 anchor, float zoom, Vector2 pan)
    {
        internal readonly string Document = document, Screen = screen;
        internal readonly Vector2 Anchor = anchor, Pan = pan;
        internal Vector2 RawAnchor = anchor;
        internal readonly float Zoom = zoom;
        internal Pointer? Pointer;
        internal HmiElement? Placement;
        internal DesignerRegionQuery? Query;
        internal DesignerSelectionOperation Operation;
        internal IReadOnlyList<FrameworkElement> Matches = Array.Empty<FrameworkElement>();
        internal Dictionary<string, HmiControl[]> Groups = new(StringComparer.Ordinal);
        internal Dictionary<FrameworkElement, string> Membership = [];
        internal Rect Bounds;
        internal bool Crossing, HasPreview;
    }

    private CanvasBoxGesture? _canvasBox;
    private HmiSymbol? _placementSymbol;
    private HmiSymbol _lastPlacementSymbol = HmiSymbol.Tank;
    private HmiControl? _placementPreview;
    private DesignerRegionAdorner? _regionAdorner;
    private long _authoringEpoch;
    private bool _applyingAreaSelection;
    private readonly List<HmiCommandIcon> _paletteDrawIcons = [];
    private readonly List<Button> _palettePlacementButtons = [];
    private Button? _selectionToolButton, _placementToolButton;

    public HmiSymbol? PlacementSymbol => _placementSymbol;
    public bool IsPlacingComponent => _placementSymbol != null || _canvasBox?.Placement != null;
    public bool IsSelectingArea => _canvasBox?.Query != null;
    public Rect? AuthoringBounds => _canvasBox?.Bounds;

    private void InitializeCanvasAuthoring()
    {
        _regionAdorner = new DesignerRegionAdorner(_canvas) { IsVisible = false, IsHitTestVisible = false, LabelFont = _font };
        _canvas.AdornerSurface.Children.Add(_regionAdorner);
        _canvas.AuthoringPointerPressed = HandleAuthoringPointerPressed;
        _canvas.AuthoringPointerMoved = HandleAuthoringPointerMoved;
        _canvas.AuthoringPointerReleased = HandleAuthoringPointerReleased;
        _canvas.AuthoringPointerCanceled = e =>
        {
            if (_canvasBox?.Pointer?.PointerId == e.Pointer.PointerId) CancelCanvasAuthoring();
        };
        _canvas.ViewportChanged += OnAuthoringViewportChanged;
        ApplyAuthoringTheme();
    }

    /// <summary>Arm a one-shot draw-to-size tool. No project object exists until the drag is committed.</summary>
    public void BeginComponentPlacement(HmiSymbol symbol) => DesignCommand(() =>
    {
        if (!Enum.IsDefined(symbol)) throw new ArgumentOutOfRangeException(nameof(symbol));
        if (Session.ActiveScreen.Elements.Count >= 5000) throw new InvalidOperationException("This screen has reached its 5000-component budget.");
        RetireOtherAuthoring();
        _placementSymbol = _lastPlacementSymbol = symbol;
        InputSystem.SetFocus(_canvas);
        if (_placementSymbol != symbol) return; // A focus observer may have switched tools.
        Status($"Draw {symbol}: drag its bounds, or click for catalog size · Shift preserves aspect · Alt bypasses grid · Escape cancels");
    });

    /// <summary>Begin a detached component preview in document coordinates; native gestures also capture their pointer.</summary>
    public void BeginPlacementDrag(HmiPoint anchor, bool bypassSnap = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPreviewing || _canvas.IsInteractionMode) throw new InvalidOperationException("Stop runtime interaction before placing components.");
        var symbol = _placementSymbol ?? throw new InvalidOperationException("Choose a component placement tool first.");
        if (_canvasBox != null) throw new InvalidOperationException("A canvas authoring gesture is already active.");
        var start = SnapAuthoringPoint(anchor, bypassSnap);
        var definition = HmiControlCatalog.CreateDefinition(symbol);
        definition.X = start.X; definition.Y = start.Y;
        var bounds = new Rect(start.X, start.Y, definition.Width, definition.Height);
        ValidatePlacementBounds(bounds);
        var gesture = new CanvasBoxGesture(Session.ExportJson(), Session.ActiveScreenId, start, _canvas.ZoomScale, _canvas.PanOffset)
        { Placement = definition, Bounds = bounds, RawAnchor = new Vector2(anchor.X, anchor.Y) };
        _canvasBox = gesture;
        _placementPreview = HmiControlCatalog.Create(symbol);
        _placementPreview.Font = _font;
        _placementPreview.ColorScheme = ColorScheme;
        _placementPreview.ApplyDefinition(definition);
        _placementPreview.IsHitTestVisible = false;
        _placementPreview.CommandsEnabled = false;
        _placementPreview.Opacity = .82f;
        _canvas.AdornerSurface.Children.Add(_placementPreview);
        // Outline and dimensions paint over the retained component preview, not behind it.
        _canvas.AdornerSurface.Children.Remove(_regionAdorner!);
        _canvas.AdornerSurface.Children.Add(_regionAdorner!);
        PreviewPlacement(anchor, bypassSnap: bypassSnap);
    }

    public void PreviewPlacement(HmiPoint pointer, bool preserveAspect = false, bool bypassSnap = false)
    {
        var gesture = RequireCanvasBox(placement: true);
        var point = SnapAuthoringPoint(pointer, bypassSnap);
        var defaults = gesture.Placement!;
        var rawPoint = SnapAuthoringPoint(pointer, bypassSnap: true);
        bool click = Math.Max(Math.Abs(rawPoint.X - gesture.RawAnchor.X), Math.Abs(rawPoint.Y - gesture.RawAnchor.Y)) * gesture.Zoom < 4;
        var bounds = click ? new Rect(gesture.Anchor.X, gesture.Anchor.Y, defaults.Width, defaults.Height)
            : DesignerDragRectangle.Create(gesture.Anchor, point, 8, preserveAspect ? defaults.Width / defaults.Height : null);
        ValidatePlacementBounds(bounds);
        if (gesture.HasPreview && gesture.Bounds.Equals(bounds)) return;
        gesture.HasPreview = true; gesture.Bounds = bounds;
        var preview = _placementPreview!;
        Canvas.SetLeft(preview, bounds.X); Canvas.SetTop(preview, bounds.Y);
        preview.Width = bounds.Width; preview.Height = bounds.Height;
        _regionAdorner!.Show(bounds, false, FormattableString.Invariant($"{defaults.Symbol}  {bounds.Width:0.#} × {bounds.Height:0.#}  /  {bounds.X:0.#}, {bounds.Y:0.#}"));
    }

    public void CommitPlacement()
    {
        if (_canvasBox?.Placement == null) return;
        var gesture = RequireCanvasBox(placement: true);
        var definition = gesture.Placement!.Copy();
        definition.X = gesture.Bounds.X; definition.Y = gesture.Bounds.Y;
        definition.Width = gesture.Bounds.Width; definition.Height = gesture.Bounds.Height;
        long epoch = RetireCanvasBox();
        EnsureRetiredGestureCurrent(gesture, epoch);
        int suffix = Session.ActiveScreen.Elements.Count + 1;
        while (Session.ActiveScreen.Elements.Any(e => e.Name == definition.Symbol + "_" + suffix)) suffix++;
        definition.Name = definition.Symbol + "_" + suffix;
        _restoreSelection = [definition.Id];
        Session.Edit("Draw " + definition.Symbol, project => project.Screens.Single(s => s.Id == gesture.Screen).Elements.Add(definition));
        Status($"Placed {definition.Symbol} · One undo restores the previous screen · F2 edits its caption");
    }

    /// <summary>Begin a window/crossing query. The real selection stays untouched until commit.</summary>
    public void BeginAreaSelection(HmiPoint anchor, DesignerSelectionOperation operation = DesignerSelectionOperation.Replace)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPreviewing || _canvas.IsInteractionMode) throw new InvalidOperationException("Stop runtime interaction before selecting a design region.");
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        var point = SnapAuthoringPoint(anchor, bypassSnap: true);
        RetireOtherAuthoring();
        var gesture = new CanvasBoxGesture(Session.ExportJson(), Session.ActiveScreenId, point, _canvas.ZoomScale, _canvas.PanOffset)
        { Query = new DesignerRegionQuery(_canvas), Operation = operation };
        // Group identities are captured once; pointer moves never clone configuration or serialize JSON.
        foreach (var control in _canvas.DesignSurface.Children.OfType<HmiControl>().Where(c => c.Visibility == Visibility.Visible && c.IsVisible))
        {
            string group = control.CaptureDefinition().Group;
            if (group.Length > 0) gesture.Membership.Add(control, group);
        }
        gesture.Groups = gesture.Membership.GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(pair => (HmiControl)pair.Key).ToArray(), StringComparer.Ordinal);
        _canvasBox = gesture;
        PreviewAreaSelection(anchor);
    }

    public void PreviewAreaSelection(HmiPoint pointer)
    {
        var gesture = RequireCanvasBox(placement: false);
        var point = SnapAuthoringPoint(pointer, bypassSnap: true);
        var bounds = DesignerDragRectangle.Create(gesture.Anchor, point);
        bool crossing = point.X < gesture.Anchor.X;
        if (gesture.HasPreview && gesture.Bounds.Equals(bounds) && gesture.Crossing == crossing) return;
        gesture.HasPreview = true; gesture.Bounds = bounds; gesture.Crossing = crossing;
        var hits = gesture.Query!.Query(bounds, crossing);
        var admitted = hits.ToHashSet();
        foreach (var hit in hits)
            if (gesture.Membership.TryGetValue(hit, out var group)) foreach (var member in gesture.Groups[group]) admitted.Add(member);
        // Keep scene painter order rather than hash iteration order, including expanded groups.
        gesture.Matches = _canvas.DesignSurface.Children.OfType<FrameworkElement>().Where(admitted.Contains).ToArray();
        _regionAdorner!.Show(bounds, crossing, $"{(crossing ? "Crossing" : "Window")} · {gesture.Operation} · {gesture.Matches.Count} components",
            gesture.Matches.Select(_canvas.GetElementRect));
    }

    public void CommitAreaSelection()
    {
        if (_canvasBox?.Query == null) return;
        var gesture = RequireCanvasBox(placement: false);
        long epoch = RetireCanvasBox();
        EnsureRetiredGestureCurrent(gesture, epoch);
        _applyingAreaSelection = true;
        try
        {
            SelectDiagramLink(null);
            _selection.SelectRange(gesture.Matches, gesture.Operation);
            _canvas.SelectElement(_selection.Selection.LastOrDefault());
            _outline.SelectedElement = _canvas.SelectedElement; _outline.RefreshTree();
        }
        finally { _applyingAreaSelection = false; }
        UpdateInspector();
        Status($"{_selection.Selection.Count} components selected · Locked components remain protected · Selection does not modify the project");
    }

    public void CancelCanvasAuthoring()
    {
        RetireCanvasBox();
        UpdateStudioState();
    }

    private void RetireOtherAuthoring()
    {
        CancelCanvasAuthoring();
        long epoch = _authoringEpoch;
        CancelLabelEdit(); CancelRouteEdit(); CancelDiagramConnection();
        if (epoch != _authoringEpoch || _disposed || IsPreviewing)
            throw new InvalidOperationException("Another operation replaced this canvas authoring request.");
    }

    private long RetireCanvasBox()
    {
        var gesture = _canvasBox; var preview = _placementPreview;
        long epoch = ++_authoringEpoch;
        _canvasBox = null; _placementPreview = null; _placementSymbol = null;
        if (preview != null) _canvas.AdornerSurface.Children.Remove(preview);
        _regionAdorner?.Hide();
        // Retire before capture release: a callback can start a replacement gesture on this very canvas.
        if (gesture?.Pointer != null) _canvas.ReleasePointerCapture(gesture.Pointer);
        return epoch;
    }

    private CanvasBoxGesture RequireCanvasBox(bool placement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var gesture = _canvasBox ?? throw new InvalidOperationException("Begin a canvas authoring gesture first.");
        if ((gesture.Placement != null) != placement) throw new InvalidOperationException("A different canvas tool is active.");
        if (IsPreviewing || _canvas.IsInteractionMode || Session.ActiveScreenId != gesture.Screen || !ReferenceEquals(Session.ExportJson(), gesture.Document) ||
            _canvas.ZoomScale != gesture.Zoom || _canvas.PanOffset != gesture.Pan)
        { CancelCanvasAuthoring(); throw new InvalidOperationException("The document or viewport changed; canvas authoring was canceled."); }
        return gesture;
    }

    private void EnsureRetiredGestureCurrent(CanvasBoxGesture gesture, long epoch)
    {
        if (_disposed || epoch != _authoringEpoch || IsPreviewing || _canvas.IsInteractionMode || gesture.Screen != Session.ActiveScreenId ||
            !ReferenceEquals(gesture.Document, Session.ExportJson()))
            throw new InvalidOperationException("The canvas gesture was replaced; no stale edit was applied.");
    }

    private Vector2 SnapAuthoringPoint(HmiPoint point, bool bypassSnap)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || Math.Abs(point.X) > 65536 || Math.Abs(point.Y) > 65536)
            throw new ArgumentOutOfRangeException(nameof(point), "Canvas coordinates must be finite and within ±65536.");
        var result = new Vector2(point.X, point.Y);
        if (!bypassSnap && _canvas.GridSnappingEnabled && float.IsFinite(_canvas.GridSize) && _canvas.GridSize > 0)
        {
            result.X = _canvas.SnapToGrid(result.X, _canvas.GridSize);
            result.Y = _canvas.SnapToGrid(result.Y, _canvas.GridSize);
        }
        return result;
    }

    private static void ValidatePlacementBounds(Rect bounds)
    {
        DesignerDragRectangle.Validate(bounds);
        if (Math.Abs(bounds.X) > 32768 || Math.Abs(bounds.Y) > 32768 || bounds.Width is < 8 or > 16384 || bounds.Height is < 8 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Component positions must be within ±32768, with dimensions 8–16384.");
    }

    private bool HandleAuthoringPointerPressed(PointerRoutedEventArgs e)
    {
        if (_canvasBox != null)
        {
            if (_canvasBox.Pointer?.PointerId == e.Pointer.PointerId && (e.IsCanceled || e.IsMiddleButtonPressed || e.IsRightButtonPressed)) CancelCanvasAuthoring();
            return true;
        }
        if (e.Handled || IsPreviewing || _canvas.IsInteractionMode || IsConnectingDiagram || IsEditingRoute ||
            !e.IsLeftButtonPressed || e.IsMiddleButtonPressed || e.IsRightButtonPressed || _canvas.IsResizingElement || DragDropManager.IsDragging) return false;
        var point = RoutePoint(e, snap: false);
        bool placement = _placementSymbol != null;
        if (!placement)
        {
            // Even an unavailable route can expose recovery handles outside equipment and successful paths.
            if ((_routeAdorner?.Hit(point, 9 / _canvas.ZoomScale) ?? -1) >= 0) return false;
            // Foreground equipment, nozzle/link tools and resize handles retain their existing priority.
            for (var owner = e.OriginalSource as FrameworkElement; owner != null; owner = owner.Parent as FrameworkElement)
                if (owner is Thumb) return false;
            if (_canvas.ShowRulers && (e.Position.X < 18 || e.Position.Y < 18)) return false;
            if (_canvas.DesignSurface.Children.OfType<HmiControl>().Any(c => c.Visibility == Visibility.Visible && _canvas.GetElementRect(c).Contains(new(point.X, point.Y)))) return false;
            if (DiagramLayer.HitLink(point, 6 / _canvas.ZoomScale) != null) return false;
        }
        Guard(() =>
        {
            if (placement) BeginPlacementDrag(point, InputSystem.Current.IsAltPressed);
            else BeginAreaSelection(point, InputSystem.Current.IsControlPressed
                ? InputSystem.Current.IsShiftPressed ? DesignerSelectionOperation.Remove : DesignerSelectionOperation.Toggle
                : InputSystem.Current.IsShiftPressed ? DesignerSelectionOperation.Add : DesignerSelectionOperation.Replace);
            var gesture = _canvasBox!;
            gesture.Pointer = e.Pointer;
            InputSystem.SetFocus(_canvas);
            if (!ReferenceEquals(gesture, _canvasBox)) return;
            if (!_canvas.CapturePointer(e.Pointer))
            {
                if (ReferenceEquals(gesture, _canvasBox)) CancelCanvasAuthoring();
                throw new InvalidOperationException("Pointer capture failed; no component or selection was changed.");
            }
            if (!ReferenceEquals(gesture, _canvasBox) && _canvasBox == null) _canvas.ReleasePointerCapture(e.Pointer);
        });
        return true;
    }

    private bool HandleAuthoringPointerMoved(PointerRoutedEventArgs e)
    {
        if (_canvasBox == null) return false;
        var ownedGesture = _canvasBox;
        if (_canvasBox.Pointer?.PointerId != e.Pointer.PointerId) return true;
        if (e.IsCanceled || !e.IsLeftButtonPressed || e.IsMiddleButtonPressed || e.IsRightButtonPressed)
        { CancelCanvasAuthoring(); return true; }
        Guard(() =>
        {
            try
            {
                if (_canvasBox.Placement != null) PreviewPlacement(RoutePoint(e, false), InputSystem.Current.IsShiftPressed, InputSystem.Current.IsAltPressed);
                else PreviewAreaSelection(RoutePoint(e, false));
            }
            catch { if (ReferenceEquals(_canvasBox, ownedGesture)) CancelCanvasAuthoring(); throw; }
        });
        return true;
    }

    private bool HandleAuthoringPointerReleased(PointerRoutedEventArgs e)
    {
        if (_canvasBox == null) return false;
        var ownedGesture = _canvasBox;
        if (_canvasBox.Pointer?.PointerId != e.Pointer.PointerId) return true;
        if (e.IsCanceled || e.IsMiddleButtonPressed || e.IsRightButtonPressed) { CancelCanvasAuthoring(); return true; }
        Guard(() =>
        {
            try
            {
                // The final release position is authoritative even without a preceding move event.
                if (_canvasBox.Placement != null)
                { PreviewPlacement(RoutePoint(e, false), InputSystem.Current.IsShiftPressed, InputSystem.Current.IsAltPressed); CommitPlacement(); }
                else { PreviewAreaSelection(RoutePoint(e, false)); CommitAreaSelection(); }
            }
            catch { if (ReferenceEquals(_canvasBox, ownedGesture)) CancelCanvasAuthoring(); throw; }
        });
        return true;
    }

    private void OnAuthoringViewportChanged() { if (_canvasBox != null) CancelCanvasAuthoring(); }
    private void ApplyAuthoringTheme()
    {
        if (_regionAdorner != null)
        {
            _regionAdorner.BorderBrush = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Accent);
            _regionAdorner.Background = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Surface);
            _regionAdorner.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Text);
        }
        if (_placementPreview != null) _placementPreview.ColorScheme = ColorScheme;
        foreach (var icon in _paletteDrawIcons) { icon.ColorScheme = ColorScheme; icon.Invalidate(); }
    }
}
