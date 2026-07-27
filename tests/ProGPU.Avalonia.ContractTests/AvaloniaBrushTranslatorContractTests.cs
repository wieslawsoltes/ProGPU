using Avalonia.Media;
using Avalonia.ProGpu;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

public sealed class AvaloniaBrushTranslatorContractTests
{
    [Fact]
    public void OnlyContentBrushesRequirePrimitiveGeometryClips()
    {
        Assert.False(
            DrawingContextImpl.RequiresBrushContentClip(
                new SolidColorBrush(Colors.Red)));
        Assert.False(
            DrawingContextImpl.RequiresBrushContentClip(
                new LinearGradientBrush()));
        Assert.True(
            DrawingContextImpl.RequiresBrushContentClip(
                new ImageBrush()));
        Assert.True(
            DrawingContextImpl.RequiresBrushContentClip(
                new VisualBrush()));
    }
}
