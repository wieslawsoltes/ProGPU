using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.WinUI.Hmi;

/// <summary>
/// Standalone retained diagram underlay. Owned topology and geometry are separate from feedback and commands.
/// Routes are cached across value/quality/theme changes; geometry edits reroute only potentially affected links.
/// </summary>
public sealed class HmiLinkLayer : Control
{
    private sealed class Entry(HmiDiagramLink model, HmiRouteTerminal source, HmiRouteTerminal target, HmiRouteResult route)
    {
        internal readonly HmiDiagramLink Model = model;
        internal readonly HmiRouteTerminal Source = source;
        internal readonly HmiRouteTerminal Target = target;
        internal HmiRouteResult Route = route;
        internal HmiQuality Quality = HmiQuality.Good;
        internal bool Active;
        internal bool Visible = true;
        internal Pen? Outer, Normal, Running, Unknown, Selected;
    }
    private Dictionary<string, HmiElement> _elements = new(StringComparer.Ordinal);
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private Dictionary<string, List<Entry>> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HmiRouteResult> _routes = new(StringComparer.Ordinal);
    private HmiRouteObstacle[] _obstacles = [];
    private HmiColorScheme _scheme;
    private string? _selected;
    public IReadOnlyDictionary<string, HmiRouteResult> Routes { get; }
    public long RoutingPasses { get; private set; }
    public HmiColorScheme ColorScheme
    {
        get => _scheme;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (_scheme == value) return;
            _scheme = value;
            foreach (var entry in _entries.Values) ConfigurePens(entry);
            Invalidate();
        }
    }
    public string? SelectedLinkId { get => _selected; set { if (_selected == value) return; _selected = value; Invalidate(); } }
    public Func<string, HmiTagSample?>? ReadSample { get; set; }
    private bool _showPortHandles;
    public bool ShowPortHandles { get => _showPortHandles; set { if (_showPortHandles == value) return; _showPortHandles = value; Invalidate(); } }
    private HmiLinkEndpoint? _pendingPort;
    public HmiLinkEndpoint? PendingPort { get => _pendingPort?.Copy(); set { _pendingPort = value?.Copy(); Invalidate(); } }

    public void Clear()
    {
        _elements.Clear(); _entries.Clear(); _bindings.Clear(); _routes.Clear(); _obstacles = [];
        _selected = null; _pendingPort = null; ReadSample = null;
        Invalidate();
    }

    public HmiQuality? GetLinkQuality(string id) => _entries.TryGetValue(id, out var entry) ? entry.Quality : null;

    public HmiLinkLayer()
    {
        IsHitTestVisible = false;
        Routes = new ReadOnlyDictionary<string, HmiRouteResult>(_routes);
    }

    public void SetScreen(HmiScreen screen)
    {
        HmiDiagram.ValidateTopology(screen);
        var elements = screen.Elements.Select(e => e.Copy()).ToDictionary(e => e.Id, StringComparer.Ordinal);
        var obstacles = elements.Values.Where(e => !e.IsHidden && e.Symbol is not (HmiSymbol.Label or HmiSymbol.Rectangle or HmiSymbol.Pipe))
            .Select(e => new HmiRouteObstacle(e.Id, HmiPortLayout.GetBounds(e))).ToArray();
        var before = _obstacles.ToDictionary(o => o.ElementId, StringComparer.Ordinal);
        var after = obstacles.ToDictionary(o => o.ElementId, StringComparer.Ordinal);
        var changed = _obstacles.Where(o => !after.TryGetValue(o.ElementId, out var next) || next != o)
            .Concat(obstacles.Where(o => !before.TryGetValue(o.ElementId, out var old) || old != o)).ToArray();
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var bindings = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        foreach (var model in screen.Links)
        {
            var link = model.Copy();
            var from = elements[link.Source.ElementId]; var to = elements[link.Target.ElementId];
            // Hidden equipment retains topology but contributes no live diagram ink or routing obstacle.
            if (from.IsHidden || to.IsHidden || link.IsHidden) continue;
            bool routable = HmiPortLayout.HasRoutableGlyph(from) && HmiPortLayout.HasRoutableGlyph(to);
            var source = Terminal(from, link.Source.PortId);
            var target = Terminal(to, link.Target.PortId);
            var ownedObstacles = obstacles;
            if (!ownedObstacles.Any(o => o.ElementId == from.Id)) ownedObstacles = [.. ownedObstacles, new(from.Id, source.Bounds)];
            if (!ownedObstacles.Any(o => o.ElementId == to.Id)) ownedObstacles = [.. ownedObstacles, new(to.Id, target.Bounds)];
            HmiRouteResult route;
            if (!routable) route = HmiRouteResult.Unavailable(HmiRouteStatus.BlockedTerminal, "Enlarge the endpoint component to expose a routable glyph.");
            else if (_entries.TryGetValue(link.Id, out var old) && old.Source == source && old.Target == target &&
                old.Model.Clearance == link.Clearance && old.Model.Thickness == link.Thickness &&
                old.Model.Waypoints.SequenceEqual(link.Waypoints) && ownedObstacles.Length <= HmiOrthogonalRouter.MaximumObstacles &&
                old.Route.Status == HmiRouteStatus.Success && !changed.Any(o => Crosses(old.Route, o.Bounds.Inflate(RoutingPadding(link))))) route = old.Route;
            else { route = HmiOrthogonalRouter.Route(source, target, ownedObstacles, link.Waypoints, RoutingPadding(link)); RoutingPasses++; }
            var entry = new Entry(link, source, target, route);
            ConfigurePens(entry); Refresh(entry);
            entries.Add(link.Id, entry);
            if (link.ActivityTag.Length > 0)
            {
                if (!bindings.TryGetValue(link.ActivityTag, out var list)) bindings[link.ActivityTag] = list = [];
                list.Add(entry);
            }
        }
        _elements = elements; _obstacles = obstacles; _entries = entries; _bindings = bindings;
        _routes.Clear(); foreach (var pair in entries) _routes.Add(pair.Key, pair.Value.Route);
        Width = screen.Width; Height = screen.Height;
        Invalidate();
    }

    /// <summary>
    /// Update one owned routing snapshot during a gesture without copying the screen or rebuilding
    /// equipment. The authoring owner commits separately; SetScreen restores document state on cancel.
    /// </summary>
    public HmiRouteResult PreviewWaypoints(string id, IReadOnlyList<HmiPoint> waypoints)
    {
        HmiRouteWaypoints.Validate(waypoints);
        if (!_entries.TryGetValue(id, out var entry)) throw new ArgumentException("The diagram link is not visible.", nameof(id));
        if (entry.Model.IsLocked) throw new InvalidOperationException("Unlock the diagram link before editing its route.");
        if (entry.Model.Waypoints.SequenceEqual(waypoints)) return entry.Route;
        var pins = waypoints.ToList();
        var obstacles = _obstacles;
        if (!obstacles.Any(o => o.ElementId == entry.Source.ElementId)) obstacles = [.. obstacles, new(entry.Source.ElementId, entry.Source.Bounds)];
        if (!obstacles.Any(o => o.ElementId == entry.Target.ElementId)) obstacles = [.. obstacles, new(entry.Target.ElementId, entry.Target.Bounds)];
        var route = HmiPortLayout.HasRoutableGlyph(_elements[entry.Source.ElementId]) && HmiPortLayout.HasRoutableGlyph(_elements[entry.Target.ElementId])
            ? HmiOrthogonalRouter.Route(entry.Source, entry.Target, obstacles, pins, RoutingPadding(entry.Model))
            : HmiRouteResult.Unavailable(HmiRouteStatus.BlockedTerminal, "Enlarge the endpoint component to expose a routable glyph.");
        entry.Model.Waypoints = pins;
        entry.Route = route;
        _routes[id] = route;
        RoutingPasses++;
        Invalidate();
        return route;
    }

    public void RefreshTags(IReadOnlyList<string>? tags = null)
    {
        bool changed = false;
        if (tags == null) { foreach (var entry in _entries.Values) changed |= Refresh(entry); }
        else foreach (var tag in tags) if (_bindings.TryGetValue(tag, out var entries)) foreach (var entry in entries) changed |= Refresh(entry);
        if (changed) Invalidate();
    }

    public void SetElementVisible(string id, bool visible)
    {
        // Store effective visibility separately: bad visibility feedback must not look like a hidden design edit.
        if (!_elements.TryGetValue(id, out var element)) return;
        element.IsHidden = !visible;
        bool changed = false;
        foreach (var entry in _entries.Values)
        {
            if (entry.Source.ElementId != id && entry.Target.ElementId != id) continue;
            bool next = !entry.Model.IsHidden && !_elements[entry.Source.ElementId].IsHidden && !_elements[entry.Target.ElementId].IsHidden;
            if (entry.Visible != next) { entry.Visible = next; changed = true; }
        }
        if (changed) Invalidate();
    }

    public HmiLinkEndpoint? HitPort(HmiPoint point, float tolerance)
    {
        if (!float.IsFinite(tolerance) || tolerance <= 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) throw new ArgumentException("Hit position must be finite.", nameof(point));
        HmiLinkEndpoint? hit = null; double best = tolerance * tolerance;
        foreach (var element in _elements.Values.Reverse())
        {
            if (element.IsHidden || !HmiPortLayout.HasRoutableGlyph(element)) continue;
            foreach (var port in HmiSymbolPorts.GetPorts(element.Symbol))
            {
                var anchor = HmiPortLayout.Resolve(element, port.Id).Point;
                double d = Math.Pow((double)point.X - anchor.X, 2) + Math.Pow((double)point.Y - anchor.Y, 2);
                if (d > best || hit != null && d == best) continue;
                best = d; hit = new() { ElementId = element.Id, PortId = port.Id };
            }
        }
        return hit;
    }

    public string? HitLink(HmiPoint point, float tolerance)
    {
        if (!float.IsFinite(tolerance) || tolerance <= 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) throw new ArgumentException("Hit position must be finite.", nameof(point));
        // Foreground equipment wins when a line crosses its glyph. Endpoint handles have their own explicit tool.
        if (_obstacles.Any(o => o.Bounds.Contains(point))) return null;
        string? result = null; double best = tolerance;
        foreach (var entry in _entries.Values)
        {
            if (!entry.Visible || entry.Route.Status != HmiRouteStatus.Success) continue;
            var points = entry.Route.Points;
            for (int i = 1; i < points.Count; i++)
            {
                var a = points[i - 1]; var b = points[i];
                double x = Math.Clamp(point.X, Math.Min(a.X, b.X), Math.Max(a.X, b.X));
                double y = Math.Clamp(point.Y, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y));
                double distance = Math.Sqrt(Math.Pow(point.X - x, 2) + Math.Pow(point.Y - y, 2));
                if (distance > best) continue;
                best = distance; result = entry.Model.Id;
            }
        }
        return result;
    }

    public override void OnRender(DrawingContext context)
    {
        var palette = HmiPalette.Get(ColorScheme);
        foreach (var entry in _entries.Values)
        {
            if (!entry.Visible) continue;
            if (entry.Route.Status != HmiRouteStatus.Success)
            {
                Cross(context, entry.Source.Point, palette.FaultLine); Cross(context, entry.Target.Point, palette.FaultLine);
                continue;
            }
            bool unknown = entry.Quality != HmiQuality.Good;
            var pen = entry.Model.Id == _selected ? entry.Selected! : unknown ? entry.Unknown! : entry.Active ? entry.Running! : entry.Normal!;
            var points = entry.Route.Points;
            bool dashed = unknown || entry.Model.Kind != HmiLinkKind.Process;
            double dash = Math.Max(entry.Model.Kind == HmiLinkKind.Electrical ? 18 : 12, entry.Route.Length / 512);
            for (int i = 1; i < points.Count; i++)
            {
                var a = V(points[i - 1]); var b = V(points[i]);
                if (entry.Model.Kind == HmiLinkKind.Process && !unknown) context.DrawLine(entry.Outer!, a, b);
                if (dashed) Dashed(context, pen, a, b, (float)dash);
                else context.DrawLine(pen, a, b);
                if (i + 1 < points.Count && !dashed) context.DrawEllipse(pen.Brush, null, b, entry.Model.Thickness / 2, entry.Model.Thickness / 2);
            }
            // Direction is topology, not proof of measured process flow. Unknown quality suppresses the arrow.
            if (entry.Model.ShowDirection && !unknown && points.Count > 1)
            {
                int longest = 1; double length = 0;
                for (int i = 1; i < points.Count; i++)
                {
                    double current = Vector2.Distance(V(points[i - 1]), V(points[i]));
                    if (current > length) { length = current; longest = i; }
                }
                if (length >= 24)
                {
                    var a = V(points[longest - 1]); var b = V(points[longest]);
                    var direction = Vector2.Normalize(b - a); var normal = new Vector2(-direction.Y, direction.X);
                    var tip = Vector2.Lerp(a, b, .6f);
                    context.DrawLine(pen, tip - direction * 7 + normal * 4, tip);
                    context.DrawLine(pen, tip - direction * 7 - normal * 4, tip);
                }
            }
            if (unknown) Cross(context, points[points.Count / 2], palette.WarningLine);
        }
        if (ShowPortHandles)
            foreach (var element in _elements.Values)
                if (!element.IsHidden && HmiPortLayout.HasRoutableGlyph(element)) foreach (var port in HmiSymbolPorts.GetPorts(element.Symbol))
                {
                    var point = V(HmiPortLayout.Resolve(element, port.Id).Point);
                    bool pending = _pendingPort?.ElementId == element.Id && _pendingPort.PortId == port.Id;
                    context.DrawEllipse(pending ? palette.Accent : palette.Surface, palette.AccentLine, point, pending ? 7 : 5, pending ? 7 : 5);
                }
    }

    // Includes the widest selection stroke and arrow wing; style/selection changes can reuse the same route.
    private static float RoutingPadding(HmiDiagramLink link) => link.Clearance + link.Thickness / 2 + 5;

    private static HmiRouteTerminal Terminal(HmiElement element, string port)
    {
        if (HmiPortLayout.HasRoutableGlyph(element)) return HmiPortLayout.Resolve(element, port);
        var bounds = HmiPortLayout.GetBounds(element);
        return new(element.Id, new((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2), HmiPortDirection.Right, bounds);
    }

    private void ConfigurePens(Entry entry)
    {
        var p = HmiPalette.Get(_scheme); float width = entry.Model.Thickness;
        entry.Outer = new(p.Edge, width + 2); entry.Normal = new(entry.Model.Kind == HmiLinkKind.Process ? p.Body : p.Muted, width);
        entry.Running = new(p.Running, width); entry.Unknown = new(p.Warning, width);
        entry.Selected = new(p.Accent, width + 1);
    }
    private bool Refresh(Entry entry)
    {
        var sample = entry.Model.ActivityTag.Length == 0 ? null : ReadSample?.Invoke(entry.Model.ActivityTag);
        HmiQuality quality = entry.Model.ActivityTag.Length == 0 ? HmiQuality.Good : sample is { Value.Type: HmiTagType.Boolean } value ? value.Quality : HmiQuality.Uncertain;
        bool active = quality == HmiQuality.Good && sample?.Value.Boolean == true;
        if (quality == entry.Quality && active == entry.Active) return false;
        entry.Quality = quality; entry.Active = active; return true;
    }
    private static bool Crosses(HmiRouteResult route, HmiRouteBox box)
    {
        for (int i = 1; i < route.Points.Count; i++) if (HmiOrthogonalRouter.Intersects(route.Points[i - 1], route.Points[i], box)) return true;
        return false;
    }
    private static Vector2 V(HmiPoint point) => new(point.X, point.Y);
    private static void Cross(DrawingContext context, HmiPoint point, Pen pen)
    {
        var p = V(point); context.DrawLine(pen, p - new Vector2(4), p + new Vector2(4));
        context.DrawLine(pen, p + new Vector2(-4, 4), p + new Vector2(4, -4));
    }
    private static void Dashed(DrawingContext context, Pen pen, Vector2 a, Vector2 b, float period)
    {
        float length = Vector2.Distance(a, b); if (length == 0) return;
        Vector2 direction = (b - a) / length;
        for (float distance = 0; distance < length; distance += period)
            context.DrawLine(pen, a + direction * distance, a + direction * Math.Min(length, distance + period * .55f));
    }
}
