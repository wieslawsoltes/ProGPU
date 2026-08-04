using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Samples;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public sealed class SampleMediaResponsiveTests
{
    [Fact]
    public void MediaPreviewStatesClearCompactPaneButtonAndScaleHeight()
    {
        var root = new Grid();
        var header = new Border();
        var primaryPreview = new Border();
        var alternatePreview = new Border();
        root.Children.Add(header);
        root.Children.Add(primaryPreview);
        root.Children.Add(alternatePreview);

        SampleMediaResponsiveLayout.AttachPreviewStates(
            root,
            header,
            compactHeight: 220f,
            mediumHeight: 320f,
            wideHeight: 430f,
            primaryPreview,
            alternatePreview);

        VisualStateManager.UpdateAdaptiveStates(
            root,
            new Vector2(419f, 646f));

        Assert.Equal(52f, header.Margin.Left);
        Assert.Equal(220f, primaryPreview.Height);
        Assert.Equal(220f, alternatePreview.Height);

        VisualStateManager.UpdateAdaptiveStates(
            root,
            new Vector2(700f, 700f));

        Assert.Equal(0f, header.Margin.Left);
        Assert.Equal(320f, primaryPreview.Height);
        Assert.Equal(320f, alternatePreview.Height);

        VisualStateManager.UpdateAdaptiveStates(
            root,
            new Vector2(1_200f, 800f));

        Assert.Equal(0f, header.Margin.Left);
        Assert.Equal(430f, primaryPreview.Height);
        Assert.Equal(430f, alternatePreview.Height);
    }

    [Fact]
    public void ActionPanelWrapsWithinCompactViewport()
    {
        WrapPanel panel =
            SampleMediaResponsiveLayout.CreateActionPanel();
        panel.Children.Add(new Button
        {
            Content = "Play timeline",
            Width = 140f,
            Height = 36f
        });
        panel.Children.Add(new Button
        {
            Content = "Add background audio",
            Width = 180f,
            Height = 36f
        });
        panel.Children.Add(new Button
        {
            Content = "Refresh thumbnails",
            Width = 160f,
            Height = 36f
        });

        panel.Measure(new Vector2(395f, float.PositiveInfinity));
        panel.Arrange(new Rect(0f, 0f, 395f, panel.DesiredSize.Y));

        Assert.True(panel.DesiredSize.X <= 395f);
        Assert.True(panel.DesiredSize.Y >= 78f);
    }

    [Fact]
    public void TimelineScrollerOwnsHorizontalOverflow()
    {
        var timeline = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        timeline.Children.Add(new Border
        {
            Width = 260f,
            Height = 58f
        });
        timeline.Children.Add(new Border
        {
            Width = 260f,
            Height = 58f
        });
        ScrollViewer scroller =
            SampleMediaResponsiveLayout.CreateTimelineScroller(
                timeline,
                80f);

        scroller.Measure(
            new Vector2(395f, float.PositiveInfinity));
        scroller.Arrange(new Rect(0f, 0f, 395f, 80f));

        Assert.Equal(395f, scroller.DesiredSize.X);
        Assert.Equal(80f, scroller.DesiredSize.Y);
        Assert.True(scroller.ScrollableWidth > 0f);
        Assert.Equal(
            ScrollBarVisibility.Auto,
            scroller.HorizontalScrollBarVisibility);
        Assert.Equal(
            ScrollBarVisibility.Disabled,
            scroller.VerticalScrollBarVisibility);
    }
}
