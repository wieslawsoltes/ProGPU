using Avalonia.Media;
using ProGPU.Backend;
using ProGPU.Scene;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

[Collection(BackendContextCollection.Name)]
public sealed class AvaloniaEffectScopeContractTests
{
    [Fact]
    public void BlendEffectPreservesTheTypedBlendMode()
    {
        var effect = new BlendEffect(GpuBlendMode.Multiply);

        Assert.Equal(GpuBlendMode.Multiply, effect.BlendMode);
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(-4f, 0f, 0f)]
    [InlineData(6f, 2.2320509f, 7f)]
    public void BlurConversionIsFiniteAndDeterministic(
        float radius,
        float expectedSigma,
        float expectedPadding)
    {
        Assert.Equal(
            expectedSigma,
            DrawingContextImpl.ConvertAvaloniaBlurRadiusToSigma(radius),
            precision: 5);
        Assert.Equal(
            expectedPadding,
            DrawingContextImpl.ComputeAvaloniaEffectPadding(radius));
    }

    [Fact]
    public void BlurScopeRecordsOneRetainedVisualAndBalancedClip()
    {
        using var context = NewContext();

        context.PushEffect(
            new Rect(2, 3, 40, 30),
            new ImmutableBlurEffect(6));
        context.PopEffect();

        RenderCommand command = Assert.Single(
            context.DrawingContext.Commands,
            item => item.Type == RenderCommandType.DrawVisual);
        var blur = Assert.IsType<ProGPU.Scene.BlurEffect>(
            command.Visual?.Effect);
        Assert.Equal(
            DrawingContextImpl.ConvertAvaloniaBlurRadiusToSigma(6),
            blur.BlurRadius,
            precision: 5);
        Assert.Equal(1, context.DrawingContext.RetainedResourceCount);
    }

    [Fact]
    public void ResetDiscardsAnUnbalancedEffectSubtree()
    {
        using var context = NewContext();
        context.PushEffect(
            new Rect(0, 0, 16, 16),
            new ImmutableDropShadowEffect(
                2,
                3,
                5,
                Colors.Black,
                0.5));

        context.Reset();

        Assert.Empty(context.DrawingContext.Commands);
        Assert.Equal(0, context.DrawingContext.RetainedResourceCount);
    }

    private static DrawingContextImpl NewContext() =>
        new(
            new DrawingContextImpl.CreateInfo
            {
                Size = new PixelSize(64, 48),
                Dpi = new Vector(96, 96)
            });
}
