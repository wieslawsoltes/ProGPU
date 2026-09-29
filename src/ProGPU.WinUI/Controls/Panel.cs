using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using System.Numerics;

namespace Microsoft.UI.Xaml.Controls;

[ContentProperty(Name = "Children")]
public class Panel : FrameworkElement
{
    public static readonly DependencyProperty BackgroundProperty =
        DependencyProperty.Register(
            nameof(Background),
            typeof(Brush),
            typeof(Panel),
            new PropertyMetadata(null) { AffectsRender = true });

    public Brush? Background
    {
        get => GetValue(BackgroundProperty) as Brush;
        set => SetValue(BackgroundProperty, value);
    }

    public static readonly DependencyProperty BackgroundTransitionProperty =
        DependencyProperty.Register(
            nameof(BackgroundTransition),
            typeof(BrushTransition),
            typeof(Panel),
            new PropertyMetadata(null));

    public BrushTransition? BackgroundTransition
    {
        get => GetValue(BackgroundTransitionProperty) as BrushTransition;
        set => SetValue(BackgroundTransitionProperty, value);
    }

    private PanelChildrenCollection? _childrenCollection;
    public new PanelChildrenCollection Children => _childrenCollection ??= new PanelChildrenCollection(this);

    internal IReadOnlyList<Visual> VisualChildren => base.Children;

    public new void AddChild(Visual child) => base.AddChild(child);
    public new void RemoveChild(Visual child) => base.RemoveChild(child);
    public new void ClearChildren() => base.ClearChildren();

    public override void OnRender(DrawingContext context)
    {
        if (Background is { } background)
            context.DrawRectangle(background, null, new Rect(Vector2.Zero, Size));
        base.OnRender(context);
    }
}

/// <summary>
/// List adapter over the retained visual tree. Mutations always use the owner's tree APIs,
/// preserving parent ownership, cycle checks, topmost overlays and layout/render invalidation.
/// Like its owner, this collection is accessed on the owning UI thread.
/// </summary>
public class PanelChildrenCollection : IList<Visual>, global::System.Collections.IList
{
    private readonly Panel _owner;

    public PanelChildrenCollection(Panel owner) => _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    public int Count => _owner.VisualChildren.Count;
    public bool IsReadOnly => false;
    public bool IsFixedSize => false;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public Visual this[int index]
    {
        get => _owner.VisualChildren[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Visual previous = _owner.VisualChildren[index];
            if (ReferenceEquals(previous, value)) return;
            if (ReferenceEquals(value.Parent, _owner))
                throw new InvalidOperationException("A visual cannot occupy two child positions. Remove or move it before replacing another child.");
            // Insert performs cycle validation before detaching either visual. Remove the
            // replaced child only after the new visual has been admitted successfully.
            _owner.InsertChild(index, value);
            _owner.RemoveChild(previous);
        }
    }

    object? global::System.Collections.IList.this[int index]
    {
        get => this[index];
        set => this[index] = RequireVisual(value);
    }

    public void Add(Visual item) => _owner.AddChild(item);
    public int Add(object? value)
    {
        var visual = RequireVisual(value);
        Add(visual);
        return IndexOf(visual);
    }

    public void Clear() => _owner.ClearChildren();
    public bool Contains(Visual item) => IndexOf(item) >= 0;
    public bool Contains(object? value) => value is Visual visual && Contains(visual);

    public int IndexOf(Visual item)
    {
        for (int i = 0; i < Count; i++)
            if (ReferenceEquals(_owner.VisualChildren[i], item)) return i;
        return -1;
    }
    public int IndexOf(object? value) => value is Visual visual ? IndexOf(visual) : -1;

    public void Insert(int index, Visual item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        _owner.InsertChild(index, item);
    }
    public void Insert(int index, object? value) => Insert(index, RequireVisual(value));

    public bool Remove(Visual item)
    {
        if (!Contains(item)) return false;
        _owner.RemoveChild(item);
        return true;
    }
    public void Remove(object? value) { if (value is Visual visual) Remove(visual); }
    public void RemoveAt(int index) => _owner.RemoveChild(_owner.VisualChildren[index]);

    public void CopyTo(Visual[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ValidateCopyTarget(array.Length, arrayIndex);
        for (int i = 0; i < Count; i++) array[arrayIndex + i] = _owner.VisualChildren[i];
    }
    public void CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array.Rank != 1 || array.GetLowerBound(0) != 0)
            throw new ArgumentException("The destination must be a zero-based, one-dimensional array.", nameof(array));
        ValidateCopyTarget(array.Length, index);
        if (array is Visual[] visuals) { CopyTo(visuals, index); return; }
        if (array is not object[] objects)
            throw new ArgumentException("The destination array must accept Visual instances.", nameof(array));
        try
        {
            for (int i = 0; i < Count; i++) objects[index + i] = _owner.VisualChildren[i];
        }
        catch (ArrayTypeMismatchException error)
        { throw new ArgumentException("The destination array must accept Visual instances.", nameof(array), error); }
    }

    private void ValidateCopyTarget(int length, int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        if (index > length || Count > length - index)
            throw new ArgumentException("The destination array has insufficient remaining capacity.");
    }
    private static Visual RequireVisual(object? value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value as Visual ?? throw new ArgumentException("A panel child must be a Visual.", nameof(value));
    }

    public IEnumerator<Visual> GetEnumerator() => _owner.VisualChildren.GetEnumerator();
    global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
