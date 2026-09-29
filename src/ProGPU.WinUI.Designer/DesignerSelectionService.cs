using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProGPU.WinUI.Designer;

public enum DesignerAlignment { Left, Center, Right, Top, Middle, Bottom }

/// <summary>Shared canvas commands. Specialized designers supply edit policy and snapshot ownership.</summary>
public sealed class DesignerSelectionService
{
    private readonly DesignerCanvas _canvas;
    private readonly List<FrameworkElement> _selection = [];
    public IReadOnlyList<FrameworkElement> Selection => _selection;
    public bool IsExecutingCommand { get; private set; }
    public Func<FrameworkElement, bool> CanEdit { get; set; } = _ => true;
    public event Action? SelectionChanged;
    public DesignerSelectionService(DesignerCanvas canvas) => _canvas = canvas;
    public void Select(FrameworkElement? element, bool additive = false)
    {
        if (!additive) _selection.Clear();
        if (element != null && element != _canvas.DesignSurface)
        {
            if (additive && _selection.Contains(element)) _selection.Remove(element);
            else _selection.Add(element);
        }
        SelectionChanged?.Invoke();
    }
    public void SelectAll()
    {
        _selection.Clear();
        _selection.AddRange(_canvas.DesignSurface.Children.OfType<FrameworkElement>());
        SelectionChanged?.Invoke();
    }
    public void Translate(float x, float y) => Edit(items =>
    {
        foreach (var element in items)
        {
            Canvas.SetLeft(element, Canvas.GetLeft(element) + x);
            Canvas.SetTop(element, Canvas.GetTop(element) + y);
        }
    });
    public void Align(DesignerAlignment alignment) => Edit(items =>
    {
        if (items.Length < 2) return;
        float left = items.Min(Canvas.GetLeft), top = items.Min(Canvas.GetTop);
        float right = items.Max(e => Canvas.GetLeft(e) + Width(e));
        float bottom = items.Max(e => Canvas.GetTop(e) + Height(e));
        foreach (var element in items)
        {
            switch (alignment)
            {
                case DesignerAlignment.Left: Canvas.SetLeft(element, left); break;
                case DesignerAlignment.Center: Canvas.SetLeft(element, (left + right - Width(element)) / 2); break;
                case DesignerAlignment.Right: Canvas.SetLeft(element, right - Width(element)); break;
                case DesignerAlignment.Top: Canvas.SetTop(element, top); break;
                case DesignerAlignment.Middle: Canvas.SetTop(element, (top + bottom - Height(element)) / 2); break;
                case DesignerAlignment.Bottom: Canvas.SetTop(element, bottom - Height(element)); break;
            }
        }
    });
    public void Distribute(bool horizontal) => Edit(items =>
    {
        if (items.Length < 3) return;
        var ordered = items.OrderBy(e => horizontal ? Canvas.GetLeft(e) : Canvas.GetTop(e)).ToArray();
        float start = horizontal ? Canvas.GetLeft(ordered[0]) : Canvas.GetTop(ordered[0]);
        float end = horizontal ? Canvas.GetLeft(ordered[^1]) + Width(ordered[^1]) : Canvas.GetTop(ordered[^1]) + Height(ordered[^1]);
        float occupied = ordered.Sum(e => horizontal ? Width(e) : Height(e));
        float gap = (end - start - occupied) / (ordered.Length - 1);
        foreach (var element in ordered)
        {
            if (horizontal) Canvas.SetLeft(element, start); else Canvas.SetTop(element, start);
            start += (horizontal ? Width(element) : Height(element)) + gap;
        }
    });
    public void Reorder(bool front) => Edit(items =>
    {
        var ordered = _canvas.DesignSurface.Children.OfType<FrameworkElement>().Where(items.Contains).ToArray();
        foreach (var element in ordered) _canvas.DesignSurface.Children.Remove(element);
        if (front) foreach (var element in ordered) _canvas.DesignSurface.Children.Add(element);
        else for (int i = ordered.Length - 1; i >= 0; i--) _canvas.DesignSurface.Children.Insert(0, ordered[i]);
    });
    public void Delete() => Edit(items =>
    {
        foreach (var element in items) DesignerElementRegistry.RemoveFromParent(element);
        _selection.RemoveAll(items.Contains);
        _canvas.SelectElement(null);
        SelectionChanged?.Invoke();
    });
    private void Edit(Action<FrameworkElement[]> action)
    {
        var items = _selection.Where(CanEdit).ToArray();
        if (items.Length == 0) return;
        IsExecutingCommand = true;
        try
        {
            _canvas.NotifyCanvasModifying();
            action(items);
            _canvas.UpdateSelectionAdorner();
            _canvas.Invalidate();
            _canvas.NotifyCanvasModified();
        }
        finally { IsExecutingCommand = false; }
    }
    private static float Width(FrameworkElement element) => float.IsFinite(element.Width) ? element.Width : element.Size.X;
    private static float Height(FrameworkElement element) => float.IsFinite(element.Height) ? element.Height : element.Size.Y;
}
