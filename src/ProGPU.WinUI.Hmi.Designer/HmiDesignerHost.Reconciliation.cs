using Microsoft.UI.Xaml;
using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private void ReconcileCanvas(HmiScreen screen)
    {
        // Reuse retained controls and their private visuals across edits and undo/redo.
        // Dictionary lookup also removes the old per-element linear tag lookup.
        var existing = _canvas.DesignSurface.Children.OfType<HmiControl>()
            .GroupBy(c => c.ElementId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var tags = Session.Document.Tags.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var desired = new List<HmiControl>(screen.Elements.Count);
        foreach (var element in screen.Elements)
        {
            var control = existing.TryGetValue(element.Id, out var found) && found.Symbol == element.Symbol
                ? found : HmiControlCatalog.Create(element.Symbol);
            if (!HmiElementComparer.Equals(control.CaptureDefinition(), element)) control.ApplyDefinition(element);
            if (!ReferenceEquals(control.Font, _font)) control.Font = _font;
            control.IsHitTestVisible = false; control.CommandsEnabled = false;
            if (tags.TryGetValue(element.Tag, out var tag)) control.UpdateSample(new HmiTagSample(tag.InitialValue, HmiQuality.Good, DateTimeOffset.UnixEpoch));
            desired.Add(control);
        }
        var retained = desired.ToHashSet();
        foreach (var previous in _canvas.DesignSurface.Children.OfType<FrameworkElement>().ToArray())
            if (previous is not HmiControl control || !retained.Contains(control)) _canvas.DesignSurface.Children.Remove(previous);
        for (int index = 0; index < desired.Count; index++)
        {
            var control = desired[index];
            if (index < _canvas.DesignSurface.Children.Count && ReferenceEquals(_canvas.DesignSurface.Children[index], control)) continue;
            _canvas.DesignSurface.Children.Remove(control);
            _canvas.DesignSurface.Children.Insert(index, control);
        }
    }
}
