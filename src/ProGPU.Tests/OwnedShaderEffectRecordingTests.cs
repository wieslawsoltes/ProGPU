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

    private sealed class Preparation : IShaderEffectPreparation
    {
        internal List<ShaderEffectPreparationContext> Targets { get; } = [];
        internal int DisposeCount { get; private set; }
        public OwnedShaderEffectParameters Prepare(ShaderEffectPreparationContext context)
        {
            Targets.Add(context);
            bool wide = context.CaptureFrame.PixelsPerUnit.X == 2;
            return new OwnedShaderEffectParameters(new WpfShaderEffectParams
            {
                ShaderKey = wide ? "owned_target_wide" : "owned_target_unit",
                ShaderSource = wide
                    ? "fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> { return vec4<f32>(1.0, 0.0, 0.0, 1.0); }"
                    : "fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> { return vec4<f32>(0.0, 0.0, 1.0, 1.0); }"
            });
        }
        public void Dispose() => DisposeCount++;
    }
}
