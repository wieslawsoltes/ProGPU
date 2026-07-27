using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Platform;

namespace Avalonia.ProGpu;

/// <summary>
/// Stores the dirty pixel rectangles supplied by Avalonia's compositor.
/// </summary>
/// <remarks>
/// Add and query operations are O(R), where R is bounded by
/// <see cref="MaximumIndependentRectangles"/>. Once the bound is reached the
/// region conservatively collapses to one union rectangle, keeping storage O(1)
/// and avoiding unbounded per-frame allocations.
/// </remarks>
internal sealed class AvaloniaDirtyRegion : IPlatformRenderInterfaceRegion
{
    private const int MaximumIndependentRectangles = 64;

    private readonly List<LtrbPixelRect> _rectangles = new(8);
    private readonly ReadOnlyCollection<LtrbPixelRect> _readOnlyRectangles;
    private LtrbPixelRect _bounds;

    public AvaloniaDirtyRegion()
    {
        _readOnlyRectangles = _rectangles.AsReadOnly();
    }

    public bool IsEmpty => _rectangles.Count == 0;

    public LtrbPixelRect Bounds => _bounds;

    public IList<LtrbPixelRect> Rects => _readOnlyRectangles;

    public void AddRect(LtrbPixelRect rectangle)
    {
        if (AvaloniaRectMath.IsEmpty(rectangle))
        {
            return;
        }

        if (_rectangles.Count == 0)
        {
            _bounds = rectangle;
            _rectangles.Add(rectangle);
            return;
        }

        _bounds = AvaloniaRectMath.Union(_bounds, rectangle);
        if (_rectangles.Count < MaximumIndependentRectangles)
        {
            _rectangles.Add(rectangle);
            return;
        }

        _rectangles.Clear();
        _rectangles.Add(_bounds);
    }

    public void Reset()
    {
        _rectangles.Clear();
        _bounds = default;
    }

    public bool Intersects(LtrbRect rectangle)
    {
        if (rectangle.Right <= rectangle.Left || rectangle.Bottom <= rectangle.Top)
        {
            return false;
        }

        for (var index = 0; index < _rectangles.Count; index++)
        {
            if (AvaloniaRectMath.Intersects(_rectangles[index], rectangle))
            {
                return true;
            }
        }

        return false;
    }

    public bool Contains(Point point)
    {
        for (var index = 0; index < _rectangles.Count; index++)
        {
            var rectangle = _rectangles[index];
            if (point.X >= rectangle.Left &&
                point.X <= rectangle.Right &&
                point.Y >= rectangle.Top &&
                point.Y <= rectangle.Bottom)
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        Reset();
    }
}
