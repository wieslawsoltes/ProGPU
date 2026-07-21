using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests;

public sealed class MobileWinUiControlApiTests
{
    [Fact]
    public void FrameUsesTypedFactoriesAndMaintainsNavigationStacks()
    {
        Frame.RegisterPageFactory(static () => new FirstPage());
        Frame.RegisterPageFactory(static () => new SecondPage());
        var frame = new Frame();
        var modes = new List<NavigationMode>();
        frame.Navigated += (_, args) => modes.Add(args.NavigationMode);

        Assert.True(frame.Navigate(typeof(FirstPage), "first"));
        FirstPage first = Assert.IsType<FirstPage>(frame.Content);
        Assert.Same(frame, first.Frame);
        Assert.Equal("first", first.LastParameter);

        Assert.True(frame.Navigate(typeof(SecondPage), "second"));
        Assert.True(frame.CanGoBack);
        Assert.Equal(1, frame.BackStackDepth);
        Assert.Equal(typeof(SecondPage), frame.CurrentSourcePageType);

        frame.GoBack();
        Assert.Same(first, frame.Content);
        Assert.True(frame.CanGoForward);
        Assert.Equal(new[] { NavigationMode.New, NavigationMode.New, NavigationMode.Back }, modes);
        Assert.Equal(typeof(SecondPage), first.LastNavigatedFromTarget);
    }

    [Fact]
    public void FrameHonorsPageNavigationCancellation()
    {
        Frame.RegisterPageFactory(static () => new CancelingPage());
        Frame.RegisterPageFactory(static () => new SecondPage());
        var frame = new Frame();

        Assert.True(frame.Navigate(typeof(CancelingPage)));
        Assert.False(frame.Navigate(typeof(SecondPage)));
        Assert.IsType<CancelingPage>(frame.Content);
        Assert.False(frame.CanGoBack);
    }

    [Fact]
    public void RelativePanelSolvesSiblingAndPanelEdgeRelationships()
    {
        var first = FixedElement(40f, 20f);
        var second = FixedElement(20f, 10f);
        var stretched = new Border { HeightConstraint = 5f };
        RelativePanel.SetRightOf(second, first);
        RelativePanel.SetBelow(second, first);
        RelativePanel.SetAlignLeftWithPanel(stretched, true);
        RelativePanel.SetAlignRightWithPanel(stretched, true);
        RelativePanel.SetAlignBottomWithPanel(stretched, true);

        var panel = new RelativePanel();
        panel.Children.Add(first);
        panel.Children.Add(second);
        panel.Children.Add(stretched);
        panel.Measure(new Vector2(200f, 100f));
        panel.Arrange(new Rect(0f, 0f, 200f, 100f));

        Assert.Equal(new Vector2(0f, 0f), first.Offset);
        Assert.Equal(new Vector2(40f, 20f), second.Offset);
        Assert.Equal(new Vector2(0f, 95f), stretched.Offset);
        Assert.Equal(200f, stretched.Size.X);
    }

    [Fact]
    public void RelativePanelRejectsConstraintCycles()
    {
        var first = FixedElement(10f, 10f);
        var second = FixedElement(10f, 10f);
        RelativePanel.SetRightOf(first, second);
        RelativePanel.SetRightOf(second, first);
        var panel = new RelativePanel();
        panel.Children.Add(first);
        panel.Children.Add(second);

        Assert.Throws<InvalidOperationException>(() => panel.Measure(new Vector2(100f, 100f)));
    }

    [Fact]
    public void VariableSizedWrapGridUsesFirstFitSpansAndWraps()
    {
        var first = FixedElement(50f, 20f);
        var second = FixedElement(50f, 20f);
        var third = FixedElement(50f, 20f);
        VariableSizedWrapGrid.SetColumnSpan(first, 2);
        var panel = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 50f,
            ItemHeight = 20f,
            MaximumRowsOrColumns = 3
        };
        panel.Children.Add(first);
        panel.Children.Add(second);
        panel.Children.Add(third);

        panel.Measure(new Vector2(150f, 100f));
        panel.Arrange(new Rect(0f, 0f, 150f, 40f));

        Assert.Equal(new Vector2(150f, 40f), panel.DesiredSize);
        Assert.Equal(new Vector2(0f, 0f), first.Offset);
        Assert.Equal(new Vector2(100f, 0f), second.Offset);
        Assert.Equal(new Vector2(0f, 20f), third.Offset);
        Assert.Throws<ArgumentOutOfRangeException>(() => VariableSizedWrapGrid.SetRowSpan(first, 0));
    }

    [Fact]
    public void ViewboxScalesThroughPresenterAndPreservesChildTransform()
    {
        var child = FixedElement(200f, 100f);
        child.Scale = new Vector3(2f, 2f, 1f);
        var viewbox = new Viewbox { Child = child, Stretch = Stretch.Uniform };

        viewbox.Measure(new Vector2(100f, 100f));
        viewbox.Arrange(new Rect(0f, 0f, 100f, 100f));

        Matrix4x4 global = child.GetGlobalTransformMatrix();
        Assert.Equal(new Vector2(100f, 50f), viewbox.DesiredSize);
        Assert.Equal(new Vector3(2f, 2f, 1f), child.Scale);
        Assert.Equal(new Vector2(0f, 25f), child.Parent!.Offset);
        Assert.Equal(1f, global.M11, 4);
        Assert.Equal(1f, global.M22, 4);
    }

    [Fact]
    public void ViewboxStretchDirectionPreventsDownscaling()
    {
        var child = FixedElement(200f, 100f);
        var viewbox = new Viewbox
        {
            Child = child,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.UpOnly
        };

        viewbox.Measure(new Vector2(100f, 100f));
        viewbox.Arrange(new Rect(0f, 0f, 100f, 100f));

        Matrix4x4 global = child.GetGlobalTransformMatrix();
        Assert.Equal(1f, global.M11, 4);
        Assert.Equal(-50f, global.M41, 4);
    }

    [Fact]
    public void OfficialItemsPanelPropertiesMapToSharedVirtualizers()
    {
        var stack = new ItemsStackPanel();
        Assert.Equal(Orientation.Vertical, stack.Orientation);
        Assert.Equal(4d, stack.CacheLength);
        Assert.True(stack.AreStickyGroupHeadersEnabled);
        Assert.Equal(GroupHeaderPlacement.Top, stack.GroupHeaderPlacement);
        Assert.Equal(2f, ((VirtualizingStackPanel)stack).CacheLength);

        stack.Orientation = Orientation.Horizontal;
        stack.CacheLength = 6d;
        stack.ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset;
        Assert.Equal(Orientation.Horizontal, ((VirtualizingStackPanel)stack).Orientation);
        Assert.Equal(3f, ((VirtualizingStackPanel)stack).CacheLength);

        var wrap = new ItemsWrapGrid();
        Assert.Equal(Orientation.Vertical, wrap.Orientation);
        Assert.True(double.IsNaN(wrap.ItemWidth));
        Assert.True(double.IsNaN(wrap.ItemHeight));
        Assert.Equal(4d, wrap.CacheLength);
        Assert.True(wrap.AreStickyGroupHeadersEnabled);
        Assert.True(((UniformVirtualizingGridPanel)wrap).IsHorizontal);
    }

    [Fact]
    public void ItemsWrapGridVirtualizesInOfficialVerticalFillOrder()
    {
        var wrap = new ItemsWrapGrid
        {
            ItemsCount = 20,
            ItemWidth = 20d,
            ItemHeight = 10d,
            MaximumRowsOrColumns = 2,
            CacheLength = 0d,
            CreateVisualFactory = static () => new Border(),
            BindVisualCallback = static (visual, index) => visual.HitTestId = index + 1
        };

        wrap.Measure(new Vector2(100f, 100f));
        wrap.Arrange(new Rect(0f, 0f, 100f, 100f));

        Visual first = Assert.Single(wrap.Children, visual => visual.HitTestId == 1);
        Visual second = Assert.Single(wrap.Children, visual => visual.HitTestId == 2);
        Visual third = Assert.Single(wrap.Children, visual => visual.HitTestId == 3);
        Assert.Equal(new Vector2(0f, 0f), first.Offset);
        Assert.Equal(new Vector2(0f, 10f), second.Offset);
        Assert.Equal(new Vector2(20f, 0f), third.Offset);
        Assert.Equal(200f, wrap.TotalVirtualWidth);
    }

    private static Border FixedElement(float width, float height) => new()
    {
        WidthConstraint = width,
        HeightConstraint = height
    };

    private sealed class FirstPage : Page
    {
        public object? LastParameter { get; private set; }
        public Type? LastNavigatedFromTarget { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e) => LastParameter = e.Parameter;
        protected override void OnNavigatedFrom(NavigationEventArgs e) => LastNavigatedFromTarget = e.SourcePageType;
    }

    private sealed class SecondPage : Page
    {
    }

    private sealed class CancelingPage : Page
    {
        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e) => e.Cancel = true;
    }
}
