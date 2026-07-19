using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class VisualChangeVersionTests
{
    [Fact]
    public void PropertyChangeIncrementsChangeVersionEvenWhenAlreadyDirty()
    {
        var visual = new Visual();
        var initialVersion = visual.ChangeVersion;

        visual.Offset = new Vector2(10f, 20f);

        Assert.True(visual.IsDirty);
        Assert.True(visual.ChangeVersion > initialVersion);
    }

    [Fact]
    public void ClearingDirtyDoesNotIncrementChangeVersion()
    {
        var visual = new Visual
        {
            Offset = new Vector2(1f, 2f)
        };
        var changedVersion = visual.ChangeVersion;

        visual.IsDirty = false;

        Assert.False(visual.IsDirty);
        Assert.Equal(changedVersion, visual.ChangeVersion);
    }

    [Fact]
    public void SettingDirtyDirectlyIncrementsChangeVersion()
    {
        var visual = new Visual();
        visual.IsDirty = false;
        var cleanVersion = visual.ChangeVersion;

        visual.IsDirty = true;

        Assert.True(visual.IsDirty);
        Assert.True(visual.ChangeVersion > cleanVersion);
    }

    [Fact]
    public void ChildInvalidationIncrementsParentChangeVersion()
    {
        var parent = new ContainerVisual();
        var child = new Visual();
        parent.AddChild(child);
        parent.IsDirty = false;
        child.IsDirty = false;
        var parentVersion = parent.ChangeVersion;

        child.Opacity = 0.5f;

        Assert.True(child.IsDirty);
        Assert.True(parent.IsDirty);
        Assert.True(parent.ChangeVersion > parentVersion);
    }

    [Fact]
    public void ClipBoundsChangeIncrementsVisualAndParentChangeVersion()
    {
        var parent = new ContainerVisual();
        var child = new Visual();
        parent.AddChild(child);
        parent.IsDirty = false;
        child.IsDirty = false;
        var parentVersion = parent.ChangeVersion;
        var childVersion = child.ChangeVersion;

        child.ClipBounds = new Rect(1f, 2f, 30f, 40f);

        Assert.True(child.IsDirty);
        Assert.True(parent.IsDirty);
        Assert.True(child.ChangeVersion > childVersion);
        Assert.True(parent.ChangeVersion > parentVersion);
    }

    [Fact]
    public void OuterClipBoundsChangeIncrementsVisualAndParentChangeVersion()
    {
        var parent = new ContainerVisual();
        var child = new Visual();
        parent.AddChild(child);
        parent.IsDirty = false;
        child.IsDirty = false;
        var parentVersion = parent.ChangeVersion;
        var childVersion = child.ChangeVersion;

        child.OuterClipBounds = new Rect(1f, 2f, 30f, 40f);

        Assert.True(child.IsDirty);
        Assert.True(parent.IsDirty);
        Assert.True(child.ChangeVersion > childVersion);
        Assert.True(parent.ChangeVersion > parentVersion);
    }

    [Fact]
    public void GeometryClipChangeIncrementsVisualAndParentChangeVersion()
    {
        var parent = new ContainerVisual();
        var child = new Visual();
        parent.AddChild(child);
        parent.IsDirty = false;
        child.IsDirty = false;
        var parentVersion = parent.ChangeVersion;
        var childVersion = child.ChangeVersion;

        child.GeometryClip = PrimitivePathGeometry.CreateRectangle(1f, 2f, 30f, 40f);

        Assert.True(child.IsDirty);
        Assert.True(parent.IsDirty);
        Assert.True(child.ChangeVersion > childVersion);
        Assert.True(parent.ChangeVersion > parentVersion);
    }

    [Fact]
    public void OpacityMaskChangesIncrementVisualAndParentChangeVersion()
    {
        var parent = new ContainerVisual();
        var child = new Visual();
        parent.AddChild(child);
        parent.IsDirty = false;
        child.IsDirty = false;
        var parentVersion = parent.ChangeVersion;
        var childVersion = child.ChangeVersion;

        child.OpacityMask = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f));
        child.OpacityMaskBounds = new Rect(1f, 2f, 30f, 40f);

        Assert.True(child.IsDirty);
        Assert.True(parent.IsDirty);
        Assert.True(child.ChangeVersion > childVersion);
        Assert.True(parent.ChangeVersion > parentVersion);
    }

    [Fact]
    public void EffectPropertyChangesAdvanceEffectChangeVersion()
    {
        var blur = new BlurEffect(2f);
        var blurVersion = blur.ChangeVersion;
        blur.BlurRadius = 4f;
        Assert.True(blur.ChangeVersion > blurVersion);

        var shadow = new DropShadowEffect(2f);
        var shadowVersion = shadow.ChangeVersion;
        shadow.Offset = new Vector2(3f, 4f);
        Assert.True(shadow.ChangeVersion > shadowVersion);

        var shader = new WpfShaderEffect(new WpfShaderEffectParams());
        var shaderVersion = shader.ChangeVersion;
        shader.Padding = 6f;
        Assert.True(shader.ChangeVersion > shaderVersion);
    }

    [Fact]
    public void EffectPropertyChangesInvalidateOwnerAndCachedAncestor()
    {
        var parent = new ContainerVisual { CacheAsLayer = true };
        var child = new DrawingVisual { Effect = new BlurEffect(2f) };
        parent.AddChild(child);
        parent.IsDirty = false;
        child.IsDirty = false;
        var parentVersion = parent.ChangeVersion;
        var childVersion = child.ChangeVersion;

        ((BlurEffect)child.Effect!).BlurRadius = 4f;

        Assert.True(child.IsDirty);
        Assert.True(parent.IsDirty);
        Assert.True(child.ChangeVersion > childVersion);
        Assert.True(parent.ChangeVersion > parentVersion);
    }

    [Fact]
    public void SharedEffectPropertyChangesInvalidateAllOwners()
    {
        var effect = new DropShadowEffect(2f);
        var first = new DrawingVisual { Effect = effect };
        var second = new DrawingVisual { Effect = effect };
        first.IsDirty = false;
        second.IsDirty = false;
        var firstVersion = first.ChangeVersion;
        var secondVersion = second.ChangeVersion;

        effect.Offset = new Vector2(3f, 4f);

        Assert.True(first.IsDirty);
        Assert.True(second.IsDirty);
        Assert.True(first.ChangeVersion > firstVersion);
        Assert.True(second.ChangeVersion > secondVersion);
    }

    [Fact]
    public void DetachedEffectNoLongerInvalidatesPreviousOwner()
    {
        var effect = new BlurEffect(2f);
        var visual = new DrawingVisual { Effect = effect };
        visual.Effect = null;
        visual.IsDirty = false;
        var visualVersion = visual.ChangeVersion;

        effect.BlurRadius = 4f;

        Assert.False(visual.IsDirty);
        Assert.Equal(visualVersion, visual.ChangeVersion);
    }

    [Fact]
    public void ChildCollectionChangesIncrementParentChangeVersion()
    {
        var parent = new ContainerVisual();
        var initialVersion = parent.ChangeVersion;
        var child = new Visual();

        parent.AddChild(child);
        var addVersion = parent.ChangeVersion;
        parent.RemoveChild(child);

        Assert.True(addVersion > initialVersion);
        Assert.True(parent.ChangeVersion > addVersion);
    }

    [Fact]
    public void RenderOffscreenDoesNotMutateOffsetOrChangeVersion()
    {
        using var window = new HeadlessWindow(64, 64);
        using var target = new GpuTexture(
            window.Context,
            64,
            64,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "RenderOffscreen ChangeVersion Test");
        var visual = new DrawingVisual
        {
            Offset = new Vector2(10f, 20f),
            Size = new Vector2(16f, 16f)
        };
        visual.IsDirty = false;
        var version = visual.ChangeVersion;
        var offset = visual.Offset;

        window.Compositor.RenderOffscreen(
            visual,
            width: 64,
            height: 64,
            targetTexture: target,
            padding: 4f,
            dpiScale: 1f);

        Assert.Equal(offset, visual.Offset);
        Assert.Equal(version, visual.ChangeVersion);
        Assert.False(visual.IsDirty);
    }

    [Fact]
    public void RenderOffscreenPublishesCurrentContextForGpuSeriesUploads()
    {
        using var window = new HeadlessWindow(64, 64);
        using var target = new GpuTexture(
            window.Context,
            64,
            64,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "RenderOffscreen Current Context Test");
        var visual = new DrawingVisual
        {
            Size = new Vector2(64f, 64f)
        };
        visual.Context.DrawGpuLineSeries(
            Array.Empty<float>(),
            pointsCount: 0,
            thickness: 2f,
            brush: new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)));

        var previous = WgpuContext.Current;
        WgpuContext.Current = null;

        try
        {
            window.Compositor.RenderOffscreen(
                visual,
                width: 64,
                height: 64,
                targetTexture: target,
                padding: 0f,
                dpiScale: 1f);

            Assert.Null(WgpuContext.Current);
        }
        finally
        {
            WgpuContext.Current = previous;
        }
    }
}
