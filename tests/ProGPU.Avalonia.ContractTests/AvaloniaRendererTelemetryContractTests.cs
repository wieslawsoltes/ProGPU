using Avalonia.ProGpu;
using ProGPU.Scene;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

public sealed class AvaloniaRendererTelemetryContractTests
{
    [Fact]
    public void SubscriptionObservesFramesUntilRemoved()
    {
        int calls = 0;
        void Observe(CompositorMetrics _) => calls++;

        ProGpuRenderingDiagnostics.FrameRendered += Observe;
        try
        {
            ProGpuRenderingDiagnostics.ReportFrame(default);
            Assert.Equal(1, calls);
        }
        finally
        {
            ProGpuRenderingDiagnostics.FrameRendered -= Observe;
        }

        ProGpuRenderingDiagnostics.ReportFrame(default);
        Assert.Equal(1, calls);
    }
}
