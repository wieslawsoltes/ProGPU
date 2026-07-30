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

    public new void AddChild(Visual child)
    {
        base.AddChild(child);
    }

    public new void RemoveChild(Visual child)
    {
        base.RemoveChild(child);
    }

    public new void ClearChildren()
    {
        base.ClearChildren();
    }

    public override void OnRender(DrawingContext context)
    {
        if (Background is { } background)
        {
            context.DrawRectangle(background, null, new Rect(Vector2.Zero, Size));
        }

        base.OnRender(context);
    }
}

public class PanelChildrenCollection : global::System.Collections.Generic.IList<Visual>, global::System.Collections.IList
{
    private readonly Panel _owner;

    public PanelChildrenCollection(Panel owner)
    {
        _owner = owner;
    }

    public int Count => _owner.VisualChildren.Count;
    public bool IsReadOnly => false;

    public Visual this[int index]
    {
        get => _owner.VisualChildren[index];
        set => throw new NotSupportedException();
    }

    object? global::System.Collections.IList.this[int index]
    {
        get => _owner.VisualChildren[index];
        set => throw new NotSupportedException();
    }

    public void Add(Visual item) => _owner.AddChild(item);
    public int Add(object? value)
    {
        if (value is Visual v)
        {
            _owner.AddChild(v);
            return _owner.VisualChildren.Count - 1;
        }
        return -1;
    }

    public void Clear() => _owner.ClearChildren();
    
    public bool Contains(Visual item)
    {
        for (int i = 0; i < _owner.VisualChildren.Count; i++)
        {
            if (_owner.VisualChildren[i] == item) return true;
        }
        return false;
    }
    
    public bool Contains(object? value) => value is Visual v && Contains(v);
    
    public void CopyTo(Visual[] array, int arrayIndex) => throw new NotImplementedException();
    public void CopyTo(Array array, int index) => throw new NotImplementedException();
    
    public int IndexOf(Visual item)
    {
        for (int i = 0; i < _owner.VisualChildren.Count; i++)
        {
            if (_owner.VisualChildren[i] == item) return i;
        }
        return -1;
    }
    
    public int IndexOf(object? value) => value is Visual v ? IndexOf(v) : -1;
    
    public void Insert(int index, Visual item) => throw new NotImplementedException();
    public void Insert(int index, object? value) => throw new NotImplementedException();
    public bool Remove(Visual item) { _owner.RemoveChild(item); return true; }
    public void Remove(object? value) { if (value is Visual v) _owner.RemoveChild(v); }
    public void RemoveAt(int index) => _owner.RemoveChild(_owner.VisualChildren[index]);
    
    public global::System.Collections.Generic.IEnumerator<Visual> GetEnumerator() => _owner.VisualChildren.GetEnumerator();
    global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => _owner.VisualChildren.GetEnumerator();

    public bool IsFixedSize => false;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
}
