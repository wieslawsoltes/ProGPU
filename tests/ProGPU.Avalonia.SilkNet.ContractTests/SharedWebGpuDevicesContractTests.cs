using Avalonia.SilkNet;
using ProGPU.Backend;
using Xunit;

namespace ProGPU.Avalonia.SilkNet.ContractTests;

public sealed class SharedWebGpuDevicesContractTests
{
    [Fact]
    public void FirstSilkWindowCanAdoptAnExistingActiveDevice()
    {
        WgpuContext? previous = WgpuContext.Current;
        using var embeddedContext = new WgpuContext();
        try
        {
            embeddedContext.Initialize(window: null);
            WgpuContext.Current = embeddedContext;

            WgpuContext? selected =
                SharedWebGpuDevices.FindHealthyContext();

            Assert.Same(embeddedContext, selected);
        }
        finally
        {
            WgpuContext.Current = previous;
        }
    }
}
