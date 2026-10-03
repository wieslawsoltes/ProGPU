using System;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed partial class SourceVisualHitTestTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceVisualMasksPreserveOwnersClipsAndRestoredGeometry(bool pictureMask)
    {
        // Paired with native scene 9842 and original VisualMaskInput reference.
        // Mask bounds intentionally have no overlap with the source drawings.
        using var maskCommands = new DrawingContext();
        maskCommands.DrawRectangle(new SolidColorBrush(Vector4.Zero), null, new Rect(100, 100, 1, 1));
        using var picture = maskCommands.CreatePictureSnapshot();
        var root = new SourceVisual { Opacity = 0 };
        var masked = new MaskSourceVisual { HitTestId = 1, ClipBounds = new Rect(10, 12, 20, 18),
            OpacityMaskBounds = new Rect(100, 100, 1, 1) };
        if (pictureMask) masked.OpacityMaskPicture = picture;
        else masked.OpacityMask = new SolidColorBrush(Vector4.Zero);
        masked.SourceHitTestCommands.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(8, 10, 32, 24));
        var child = new MaskSourceVisual { HitTestId = 7, OpacityMask = new SolidColorBrush(Vector4.Zero),
            OpacityMaskBounds = new Rect(-100, -100, 1, 1) };
        child.SourceHitTestCommands.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(20, 20, 16, 16));
        var sibling = new SourceVisual { HitTestId = 9 };
        sibling.SourceHitTestCommands.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(8, 10, 32, 24));
        masked.AddChild(child); root.AddChild(masked); root.AddChild(sibling);
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        for (int phase = 0; phase < 6; phase++)
        {
            if (phase == 1) root.Opacity = 1;
            if (phase == 2) { masked.OpacityMask = null; masked.OpacityMaskPicture = null; }
            if (phase == 3) masked.Transform = Matrix4x4.CreateScale(0, 1, 1);
            if (phase == 4) { masked.Transform = Matrix4x4.Identity; masked.IsVisible = false; }
            if (phase == 5) masked.IsVisible = true;
            capture.Clear();
            capture.AddSourceVisual(root, Matrix4x4.Identity);
            var hits = capture.BuildIndex().Primitives;
            bool suppressed = phase is 3 or 4;
            Assert.Equal(suppressed ? 1 : 3, hits.Count);
            if (!suppressed)
            {
                Assert.Equal(1, hits[0].Id);
                Assert.Equal(new Vector2(10, 12), hits[0].BoundsMin);
                Assert.Equal(new Vector2(30, 30), hits[0].BoundsMax);
                Assert.Equal(7, hits[1].Id);
                Assert.Equal(new Vector2(20, 20), hits[1].BoundsMin);
                Assert.Equal(new Vector2(30, 30), hits[1].BoundsMax);
            }
            Assert.Equal(9, hits[^1].Id);
            Assert.Equal(new Vector2(8, 10), hits[^1].BoundsMin);
            Assert.Equal(new Vector2(40, 34), hits[^1].BoundsMax);
            Assert.Equal(0u, hits[^1].ClipSegmentCount);
        }
        Assert.Equal(0, root.RenderCalls + masked.RenderCalls + child.RenderCalls + sibling.RenderCalls);
        Assert.NotNull(child.OpacityMask); // capture did not mutate raster state
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VisualMaskCapabilityDoesNotAdmitUnknownSourcesOrOtherMappings(bool pictureMask)
    {
        using var commands = new DrawingContext();
        using var picture = commands.CreatePictureSnapshot();
        SourceVisual[] denied = [new SourceVisual(), new MaskSourceVisual { Effect = new UnmappedEffect() },
            new RequiredMaskCacheSourceVisual()];
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        foreach (var source in denied)
        {
            if (pictureMask) source.OpacityMaskPicture = picture;
            else source.OpacityMask = new SolidColorBrush(Vector4.Zero);
            source.SourceHitTestCommands.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(1, 2, 3, 4));
            capture.Clear();
            Assert.Throws<NotSupportedException>(() => capture.AddSourceVisual(source, Matrix4x4.Identity));
            Assert.Throws<InvalidOperationException>(() => capture.BuildIndex());
        }
        Assert.False(((ISourceGeometryHitTestCommands)denied[0]).SourceOpacityMaskPreservesHitGeometry);
        capture.Clear();
        Assert.Empty(capture.BuildIndex().Primitives);
    }

    [Fact]
    public unsafe void ZeroOpacityAncestorCapturesMaskedDescendantsAndReappearance()
    {
        using var window = new HeadlessWindow(64, 64);
        using var target = new GpuTexture(window.Context, 64, 64, TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Source visual mask input");
        using var compositor = new Compositor(window.Context, TextureFormat.Rgba8Unorm,
            CompositorOptions.Default with { EnableGpuHitTesting = true, EnableCompiledSceneCache = false });
        compositor.ClearColor = Vector4.Zero;
        var root = new SourceVisual();
        var ancestor = new SourceVisual { Opacity = 0 };
        var masked = new MaskSourceVisual { HitTestId = 41, Size = new Vector2(64),
            ClipBounds = new Rect(10, 12, 20, 18), OpacityMaskBounds = new Rect(8, 10, 32, 24),
            OpacityMask = new SolidColorBrush(Vector4.Zero) };
        masked.SourceHitTestCommands.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(8, 10, 32, 24));
        ancestor.AddChild(masked); root.AddChild(ancestor);
        for (int phase = 0; phase < 4; phase++)
        {
            if (phase == 1) ancestor.Opacity = 1;
            if (phase == 2) masked.OpacityMask = null;
            if (phase == 3) ancestor.Opacity = 0;
            compositor.RenderScene(root, 64, 64, target.ViewPtr);
            var hit = Assert.Single(Assert.IsType<GpuHitTestIndex>(compositor.LastHitTestIndex).Primitives);
            Assert.Equal(41, hit.Id);
            Assert.Equal(new Vector2(10, 12), hit.BoundsMin);
            Assert.Equal(new Vector2(30, 30), hit.BoundsMax);
            if (phase == 0) Assert.Equal(0, masked.RenderCalls);
            byte[] pixels = target.ReadPixels();
            Assert.Equal((byte)(phase == 2 ? 255 : 0), pixels[(20 * 64 + 20) * 4]);
        }
    }

    private class MaskSourceVisual : SourceVisual, ISourceGeometryHitTestCommands
    {
        public bool SourceOpacityMaskPreservesHitGeometry => true;
    }

    private sealed class RequiredMaskCacheSourceVisual : MaskSourceVisual
    {
        internal override bool RequiresLayerCache => true;
    }
}
