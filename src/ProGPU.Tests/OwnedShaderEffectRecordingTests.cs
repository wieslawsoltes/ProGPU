using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class OwnedShaderEffectRecordingTests
{
    private static readonly ShaderEffectSourceCapture Capture = new(0, 0, 8, 4, 0, 0, 0, 0);

    [Fact]
    public void RecordingPreservesStreamPositionAndRetainsItsExactSourceAndContent()
    {
        var contentOwner = new CountedOwner();
        using var content = RecordContent(contentOwner);
        var recipe = new Preparation();
        using var source = new OwnedShaderEffectSource(Capture, Vector2.Zero, recipe);
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(new Rect(0, 0, 64, 32));
        drawing.DrawRectangle(new SolidColorBrush(new Vector4(1, 0, 0, 1)), null, new Rect(1, 2, 3, 4));
        var transform = Matrix4x4.CreateTranslation(12, 8, 0);
        drawing.DrawOwnedShaderEffect(content, source, transform);
        drawing.DrawRectangle(new SolidColorBrush(new Vector4(0, 1, 0, 1)), null, new Rect(5, 6, 7, 8));
        using var picture = recorder.EndRecording();
        using var clone = picture.Clone();
        Assert.Equal(3, picture.CommandCount);
        Assert.Equal(RenderCommandType.DrawRect, picture.GetCommand(0).Type);
        Assert.Equal(RenderCommandType.DrawVisual, picture.GetCommand(1).Type);
        Assert.Equal(RenderCommandType.DrawRect, picture.GetCommand(2).Type);
        Assert.Equal(transform, picture.GetCommand(1).Transform);
        Assert.Equal(new Rect(1, 2, 3, 4), picture.GetCommand(0).Rect);
        Assert.Equal(new Rect(5, 6, 7, 8), picture.GetCommand(2).Rect);
        Assert.Empty(recipe.Targets);
        Assert.True(picture.SharesRetainedCommandStorageWith(clone));

        content.Dispose(); source.Dispose(); picture.Dispose(); drawing.Clear();
        Assert.Equal(0, contentOwner.DisposeCount);
        Assert.Equal(0, recipe.DisposeCount);
        clone.Dispose();
        Assert.Equal(1, contentOwner.DisposeCount);
        Assert.Equal(1, recipe.DisposeCount);
        clone.Dispose(); source.Dispose();
        Assert.Equal(1, recipe.DisposeCount);
    }

    [Fact]
    public void InvalidSourceConstructionDoesNotTakeRecipeOwnership()
    {
        var recipe = new Preparation();
        Assert.Throws<ArgumentException>(() => new OwnedShaderEffectSource(default, Vector2.Zero, recipe));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OwnedShaderEffectSource(Capture,
            new Vector2(float.NaN, 0), recipe));
        Assert.Empty(recipe.Targets);
        Assert.Equal(0, recipe.DisposeCount);
        recipe.Dispose();
        Assert.Equal(1, recipe.DisposeCount);
    }

    [Fact]
    public void FailedContentCloneLeavesNoCommandOrOwnershipTail()
    {
        using var content = RecordContent();
        content.Dispose();
        var recipe = new Preparation();
        using var source = new OwnedShaderEffectSource(Capture, Vector2.Zero, recipe);
        var drawing = new DrawingContext();
        Assert.Throws<ObjectDisposedException>(() => drawing.DrawOwnedShaderEffect(content, source));
        Assert.Empty(drawing.Commands);
        Assert.Equal(0, drawing.RetainedResourceCount);
        Assert.Equal(0, recipe.DisposeCount);
        source.Dispose();
        Assert.Equal(1, recipe.DisposeCount);
    }

    [Fact]
    public void DisposedSourceCannotCreateAnotherRecording()
    {
        using var content = RecordContent();
        var recipe = new Preparation();
        using var source = new OwnedShaderEffectSource(Capture, Vector2.Zero, recipe);
        source.Dispose();
        var drawing = new DrawingContext();
        Assert.Throws<ObjectDisposedException>(() => drawing.DrawOwnedShaderEffect(content, source));
        Assert.Empty(drawing.Commands);
        Assert.Equal(0, drawing.RetainedResourceCount);
        Assert.Equal(1, recipe.DisposeCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OneRecordingKeepsSeparateTargetsWithSharedNativeProvider(bool returnPriorGeneration)
    {
        using var window = new HeadlessWindow(64, 64);
        VerifySeparateTargets(window.Context, returnPriorGeneration);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OneRecordingKeepsSeparateTargetsWithSharedDawnProvider(bool returnPriorGeneration)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var dawn = DawnGpuContext.CreateMetalPresentation();
        VerifySeparateTargets(dawn.Context, returnPriorGeneration);
    }

    private static unsafe void VerifySeparateTargets(WgpuContext context, bool returnPriorGeneration)
    {
        using var compositor = new Compositor(context, TextureFormat.Rgba8Unorm,
            CompositorOptions.Default with { EnableGpuHitTesting = false, PrimarySampleCount = 1 });
        compositor.ClearColor = new Vector4(0, 0, 0, 1);
        using var target = new GpuTexture(context, 128, 64, TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Two owned shader targets",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        var contentOwner = new CountedOwner();
        using var content = RecordContent(contentOwner);
        var recipe = new Preparation(returnPriorGeneration);
        using var source = new OwnedShaderEffectSource(Capture, Vector2.Zero, recipe);
        var recorder = new GpuPictureRecorder();
        var commands = recorder.BeginRecording(new Rect(0, 0, 8, 4));
        commands.DrawOwnedShaderEffect(content, source);
        using var original = recorder.EndRecording();
        using var picture = original.Clone();
        original.Dispose(); content.Dispose(); source.Dispose();

        var outer = new DrawingVisual
        {
            Offset = new Vector2(20, 8), Size = new Vector2(8, 4),
            EffectContentBounds = new Rect(0, 0, 8, 4),
            // This existing scalar effect really captures at semantic DPI 1,
            // independently of the main target's actual geometric X scale 2.
            Effect = new WpfShaderEffect(new WpfShaderEffectParams
            {
                ShaderKey = "owned_target_outer_input",
                SamplingMode = TextureSamplingMode.Nearest,
                ShaderSource = "fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> { return inputColor; }"
            })
        };
        outer.Context.DrawPicture(picture);
        var root = new DrawingVisual { Size = new Vector2(64) };
        root.Context.DrawPictureTransformed(picture, Matrix4x4.CreateTranslation(4, 8, 0));
        root.Context.DrawVisual(outer);
        // Following content must stay after the effect, not before appended
        // child visuals. It deliberately overlaps the first effect only.
        root.Context.DrawRectangle(new SolidColorBrush(new Vector4(0, 1, 0, 1)), null, new Rect(8, 8, 2, 4));
        try
        {
            if (returnPriorGeneration)
            {
                // The second target deliberately returns the first target's
                // already-transferred object. Reject it without disposing the
                // first generation, then draw that earlier generation again.
                Assert.Throws<InvalidOperationException>(() => compositor.RenderScene(root,
                    64, 64, 128, 64, RenderTargetViewport.Full(128, 64), 1, target.ViewPtr));
                Assert.Equal(2, recipe.Targets.Count);
                root.Context.Clear();
                outer.Context.Clear();
                root.Context.DrawPictureTransformed(picture, Matrix4x4.CreateTranslation(4, 8, 0));
                root.Context.DrawRectangle(new SolidColorBrush(new Vector4(0, 1, 0, 1)), null, new Rect(8, 8, 2, 4));
            }
            byte[] expected = ExpectedSeparateTargets(includeNested: !returnPriorGeneration);
            compositor.RenderScene(root, 64, 64, 128, 64, RenderTargetViewport.Full(128, 64), 1, target.ViewPtr);
            Assert.Equal(expected, target.ReadPixels());
            Assert.Equal(2, recipe.Targets.Count);
            Assert.Contains(recipe.Targets, frame => frame.CaptureFrame.PixelsPerUnit == new Vector2(2, 1) &&
                frame.TargetWidth == 128 && frame.TargetHeight == 64 && frame.DpiScale == 1);
            Assert.Contains(recipe.Targets, frame => frame.CaptureFrame.PixelsPerUnit == Vector2.One &&
                frame.TargetWidth == 8 && frame.TargetHeight == 4 && frame.DpiScale == 1);
            Assert.All(recipe.Targets, frame =>
            {
                Assert.Same(source, frame.Source);
                Assert.Same(context.DeviceIdentity, frame.DeviceIdentity);
                Assert.Same(compositor, frame.Compositor);
            });
            Assert.Equal(0, recipe.DisposeCount);
            Assert.Equal(0, contentOwner.DisposeCount);

            compositor.RenderScene(root, 64, 64, 128, 64, RenderTargetViewport.Full(128, 64), 1, target.ViewPtr);
            Assert.Equal(expected, target.ReadPixels());
            Assert.Equal(2, recipe.Targets.Count);
        }
        finally
        {
            root.Context.Clear(); outer.Context.Clear(); picture.Dispose(); compositor.Dispose();
        }
        Assert.Equal(1, recipe.DisposeCount);
        Assert.Equal(1, contentOwner.DisposeCount);
    }

    private static byte[] ExpectedSeparateTargets(bool includeNested)
    {
        // Independent literal physical rectangles: red main capture, blue
        // nested unit-scale capture, then green later content above the red.
        var pixels = new byte[128 * 64 * 4];
        for (int y = 0; y < 64; ++y)
        for (int x = 0; x < 128; ++x)
        {
            int index = (y * 128 + x) * 4;
            if (y >= 8 && y < 12)
            {
                if (x >= 8 && x < 24) pixels[index] = 255;
                if (includeNested && x >= 40 && x < 56) pixels[index + 2] = 255;
                if (x >= 16 && x < 20)
                {
                    pixels[index] = 0;
                    pixels[index + 1] = 255;
                }
            }
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    private static GpuPicture RecordContent(IDisposable? owner = null)
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(new Rect(0, 0, 8, 4));
        if (owner is not null) drawing.RetainResource(owner);
        drawing.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(0, 0, 8, 4));
        return recorder.EndRecording();
    }

    private sealed class CountedOwner : IDisposable
    {
        internal int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class Preparation(bool returnPriorGeneration = false) : IShaderEffectPreparation
    {
        private OwnedShaderEffectParameters? _prior;
        internal List<ShaderEffectPreparationContext> Targets { get; } = [];
        internal int DisposeCount { get; private set; }
        public OwnedShaderEffectParameters Prepare(ShaderEffectPreparationContext context)
        {
            Targets.Add(context);
            if (returnPriorGeneration && _prior is not null) return _prior;
            bool wide = context.CaptureFrame.PixelsPerUnit.X == 2;
            var result = new OwnedShaderEffectParameters(new WpfShaderEffectParams
            {
                ShaderKey = wide ? "owned_target_wide" : "owned_target_unit",
                ShaderSource = wide
                    ? "fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> { return vec4<f32>(1.0, 0.0, 0.0, 1.0); }"
                    : "fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> { return vec4<f32>(0.0, 0.0, 1.0, 1.0); }"
            });
            if (returnPriorGeneration) _prior = result;
            return result;
        }
        public void Dispose() => DisposeCount++;
    }
}
