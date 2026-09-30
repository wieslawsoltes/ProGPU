using Microsoft.UI.Xaml;
using ProGPU.Scene;

namespace ProGPU.WinUI.Designer;

/// <summary>
/// One retained-geometry snapshot for an area-selection gesture. Queries reuse the existing
/// packed R-tree and return top-level logical components in painter order, never decorations.
/// Recreate after document/layout mutation; viewport changes need only coordinate conversion.
/// </summary>
public sealed class DesignerRegionQuery
{
    private readonly RTree<int> _index = new();
    private readonly FrameworkElement[] _elements;
    private readonly Rect[] _bounds;

    public DesignerRegionQuery(DesignerCanvas canvas, int maximumElements = 5000)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (maximumElements is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maximumElements));
        var elements = new List<FrameworkElement>();
        var bounds = new List<Rect>();
        var entries = new List<RTreeEntry<int>>();
        foreach (var element in canvas.DesignSurface.Children.OfType<FrameworkElement>())
        {
            if (DesignerElementRegistry.IsDecoration(element) || element.Visibility != Visibility.Visible || !element.IsVisible) continue;
            if (elements.Count == maximumElements) throw new InvalidOperationException("The area-selection snapshot exceeds its component budget.");
            var rectangle = canvas.GetElementRect(element);
            DesignerDragRectangle.Validate(rectangle);
            if (rectangle.IsEmpty) continue;
            entries.Add(new(rectangle, elements.Count)); elements.Add(element); bounds.Add(rectangle);
        }
        _elements = elements.ToArray(); _bounds = bounds.ToArray(); _index.Rebuild(entries);
    }

    public IReadOnlyList<FrameworkElement> Query(Rect rectangle, bool crossing)
    {
        DesignerDragRectangle.Validate(rectangle);
        if (rectangle.IsEmpty) return Array.Empty<FrameworkElement>();
        var indices = _index.Query(rectangle);
        indices.Sort(); // R-tree packing order is not scene painter order.
        var matches = new List<FrameworkElement>(indices.Count);
        foreach (int index in indices)
        {
            var item = _bounds[index];
            if (crossing || rectangle.X <= item.X && rectangle.Y <= item.Y && rectangle.Right >= item.Right && rectangle.Bottom >= item.Bottom)
                matches.Add(_elements[index]);
        }
        return matches.AsReadOnly();
    }
}
