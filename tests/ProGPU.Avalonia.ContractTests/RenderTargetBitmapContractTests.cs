using Avalonia.Platform;
using ProGPU.Backend;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

[Collection(BackendContextCollection.Name)]
public sealed class RenderTargetBitmapContractTests
{
    [Fact]
    public void ConstructionBeforeWindowCreationIsGpuLazy()
    {
        WgpuContext? previous = WgpuContext.Current;
        WgpuContext.Current = null;
        int activeBefore = WgpuContext.ActiveContexts.Count;
        try
        {
            using var bitmap = new RenderTargetBitmapImpl(
                new PixelSize(32, 24),
                new Vector(96, 96));

            Assert.Null(bitmap.Texture);
            Assert.False(bitmap.HasAllocatedCpuPixels);
            Assert.Equal(
                activeBefore,
                WgpuContext.ActiveContexts.Count);
        }
        finally
        {
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void FirstDrawingContextUsesCurrentDeviceWithoutCpuPixels()
    {
        using var context = new WgpuContext();
        context.Initialize(window: null);
        using WgpuContext.CurrentContextScope scope =
            WgpuContext.PushCurrent(context);
        using var bitmap = new RenderTargetBitmapImpl(
            new PixelSize(32, 24),
            new Vector(96, 96));

        using IDrawingContextImpl drawing =
            bitmap.CreateDrawingContext();

        Assert.Same(context, bitmap.Texture?.Context);
        Assert.False(bitmap.HasAllocatedCpuPixels);
    }
}
