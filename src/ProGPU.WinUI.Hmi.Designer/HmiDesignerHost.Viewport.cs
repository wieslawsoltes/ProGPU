using System.Numerics;
using ProGPU.Hmi;
using Microsoft.UI.Xaml.Controls;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    public void ZoomToSelection()
    {
        var selected = _selection.Selection.OfType<HmiControl>().Select(c => c.CaptureDefinition()).ToArray();
        float left, top, right, bottom;
        if (selected.Length == 0)
        {
            if (SelectedLinkId == null) { Fit(); return; }
            var link = Session.ActiveScreen.Links.SingleOrDefault(l => l.Id == SelectedLinkId);
            if (link == null) { Fit(); return; }
            var points = new List<HmiPoint>();
            if (DiagramLayer.Routes.TryGetValue(SelectedLinkId, out var route)) points.AddRange(route.Points);
            points.AddRange(_segmentGesture?.Preview?.Waypoints ?? _waypointGesture?.Points ?? link.Waypoints);
            if (route?.Status != HmiRouteStatus.Success)
            {
                // A blocked route has no invented line geometry. Frame its pins and
                // endpoint controls so a misplaced pin or tiny terminal can be repaired.
                foreach (var endpoint in new[] { link.Source, link.Target })
                {
                    var element = Session.ActiveScreen.Elements.Single(e => e.Id == endpoint.ElementId);
                    points.Add(new(element.X, element.Y));
                    points.Add(new(element.X + element.Width, element.Y + element.Height));
                }
            }
            if (points.Count == 0) { Fit(); return; }
            left = points.Min(p => p.X) - 12; right = points.Max(p => p.X) + 12;
            top = points.Min(p => p.Y) - 12; bottom = points.Max(p => p.Y) + 12;
        }
        else
        {
            left = selected.Min(e => e.X); top = selected.Min(e => e.Y);
            right = selected.Max(e => e.X + e.Width); bottom = selected.Max(e => e.Y + e.Height);
        }
        float width = Math.Max(100, _workspace.Size.X), height = Math.Max(100, _workspace.Size.Y);
        _canvas.ZoomScale = Math.Clamp(Math.Min((width - 64) / (right - left), (height - 64) / (bottom - top)), 0.15f, 4);
        _canvas.PanOffset = new Vector2(width / 2 - (left + right) * _canvas.ZoomScale / 2,
            height / 2 - (top + bottom) * _canvas.ZoomScale / 2);
        _canvas.ApplyTransforms(); _canvas.Invalidate();
        Status($"Selection framed · {_canvas.ZoomScale:P0}");
    }
}
