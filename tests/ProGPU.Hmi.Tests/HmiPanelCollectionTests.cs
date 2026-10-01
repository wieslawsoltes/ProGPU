using System.Collections;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Scene;
using ProGPU.WinUI.Designer;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiPanelCollectionTests
{
    [Fact]
    public void InsertMaintainsVisualOrderAndReparentsThroughTheOwner()
    {
        var source = new Canvas(); var target = new Canvas();
        var first = new Button(); var middle = new Button(); var last = new Button();
        target.Children.Add(first); target.Children.Add(last); source.Children.Add(middle);
        target.Children.Insert(1, middle);
        Assert.Equal(new Visual[] { first, middle, last }, target.Children.ToArray());
        Assert.Empty(source.Children);
        Assert.Same(target, middle.Parent);
        target.Children.Insert(0, last);
        Assert.Equal(new Visual[] { last, first, middle }, target.Children.ToArray());
    }

    [Fact]
    public void InsertRejectsInvalidIndicesBeforeDetachingTheSource()
    {
        var source = new Canvas(); var target = new Canvas(); var child = new Button();
        source.Children.Add(child);
        Assert.Throws<ArgumentOutOfRangeException>(() => target.Children.Insert(1, child));
        Assert.Throws<ArgumentOutOfRangeException>(() => target.Children.Insert(-1, child));
        Assert.Same(source, child.Parent); Assert.Single(source.Children); Assert.Empty(target.Children);
    }

    [Fact]
    public void InsertionAndReplacementRejectVisualCyclesWithoutLosingChildren()
    {
        var root = new Canvas(); var nested = new Canvas(); var original = new Button();
        root.Children.Add(nested); nested.Children.Add(original);
        Assert.Throws<InvalidOperationException>(() => nested.Children.Insert(0, root));
        Assert.Throws<InvalidOperationException>(() => nested.Children[0] = root);
        Assert.Same(original, Assert.Single(nested.Children));
        Assert.Same(root, nested.Parent);
    }

    [Fact]
    public void ReplacementDetachesOldChildAndRejectsDuplicateOwnership()
    {
        var panel = new Canvas(); var first = new Button(); var second = new Button(); var replacement = new Button();
        panel.Children.Add(first); panel.Children.Add(second);
        panel.Children[0] = replacement;
        Assert.Null(first.Parent); Assert.Same(panel, replacement.Parent);
        Assert.Equal(new Visual[] { replacement, second }, panel.Children.ToArray());
        Assert.Throws<InvalidOperationException>(() => panel.Children[0] = second);
        Assert.Equal(2, panel.Children.Count);
    }

    [Fact]
    public void NongenericListsAndCopyTargetsAreValidated()
    {
        var panel = new Canvas(); var first = new Button(); var second = new Button();
        IList children = panel.Children;
        Assert.Equal(0, children.Add(first)); children.Insert(0, second);
        var array = new object?[3]; ((ICollection)children).CopyTo(array, 1);
        Assert.Same(second, array[1]); Assert.Same(first, array[2]);
        Assert.Throws<ArgumentException>(() => children.Insert(0, new object()));
        Assert.Throws<ArgumentException>(() => ((ICollection)children).CopyTo(new int[2], 0));
        Assert.Throws<ArgumentException>(() => ((ICollection)children).CopyTo(new object[1, 2], 0));
        Assert.Throws<ArgumentException>(() => panel.Children.CopyTo(new Visual[1], 0));
        Assert.False(panel.Children.Remove(new Button()));
    }

    [Fact]
    public void OrdinaryInsertionsStayBelowTopmostOverlays()
    {
        var panel = new Canvas(); var overlay = new Button(); var ordinary = new Button();
        panel.AddTopmostChild(overlay);
        int index = ((IList)panel.Children).Add(ordinary);
        Assert.Equal(0, index);
        Assert.Same(overlay, panel.Children[^1]);
        panel.Children.Insert(panel.Children.Count, new Button());
        Assert.Same(overlay, panel.Children[^1]);
    }

    [Fact]
    public void SharedDesignerSendToBackUsesTheImplementedCollectionInsertion()
    {
        var canvas = new DesignerCanvas(); var first = new Button(); var second = new Button(); var third = new Button();
        canvas.DesignSurface.Children.Add(first); canvas.DesignSurface.Children.Add(second); canvas.DesignSurface.Children.Add(third);
        var selection = new DesignerSelectionService(canvas);
        selection.Select(second); selection.Select(third, additive: true); selection.Reorder(front: false);
        Assert.Equal(new Visual[] { second, third, first }, canvas.DesignSurface.Children.ToArray());
        Assert.False(selection.IsExecutingCommand);
    }
}
