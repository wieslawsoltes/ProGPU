using System;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class SourceVisualVisibilityTests
{
    [Fact]
    public void PortableStateDefaultsToLocalVisibleWithoutChangingOpacity()
    {
        var state = new PortableVisualState();
        Assert.False(state.HasVisibility);
        Assert.Equal(PortableVisualVisibility.Visible, state.Visibility);
        Assert.Equal(1.0, state.Opacity);
        state.HasVisibility = true;
        state.Visibility = PortableVisualVisibility.Collapsed;
        Assert.Equal(1.0, state.Opacity);
        state.Visibility = PortableVisualVisibility.Hidden;
        Assert.Equal(1.0, state.Opacity);
    }

    [Fact]
    public void HiddenAncestorExcludesMaskedDescendantWithoutAdmittingVisibleMasks()
    {
        var ancestor = new SourceVisual { IsVisible = false, Opacity = 0 };
        var child = new SourceVisual { HitTestId = 41 };
        child.OpacityMask = new SolidColorBrush(Vector4.One);
        child.SourceHitTestCommands.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(0, 0, 20, 10));
        ancestor.Children.Add(child);
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        capture.AddSourceVisual(ancestor, Matrix4x4.Identity);
        Assert.Empty(capture.BuildIndex().Primitives);

        ancestor.IsVisible = true;
        capture.Clear();
        Assert.Throws<NotSupportedException>(() => capture.AddSourceVisual(ancestor, Matrix4x4.Identity));

        child.OpacityMask = null;
        capture.Clear();
        capture.AddSourceVisual(ancestor, Matrix4x4.Identity);
        Assert.Equal(41, Assert.Single(capture.BuildIndex().Primitives).Id);
    }

    private sealed class SourceVisual : ContainerVisual, ISourceGeometryHitTestCommands
    {
        public DrawingContext SourceHitTestCommands { get; } = new();
    }
}
