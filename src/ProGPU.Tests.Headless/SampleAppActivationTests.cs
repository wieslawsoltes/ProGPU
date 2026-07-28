using Microsoft.UI.Xaml;
using ProGPU.Samples;
using Xunit;

namespace ProGPU.Tests.Headless;

public sealed class SampleAppActivationTests
{
    [Fact]
    public void ReturningFromNativePickerDoesNotRestartSampleWindow()
    {
        var startupCount = 0;
        var startup = new WindowStartupGuard(_ => startupCount++);
        var window = new Window();
        startup.Attach(window);

        window.NotifyHostActivationChanged(WindowActivationState.Deactivated);
        Assert.Equal(0, startupCount);

        window.NotifyHostActivationChanged(WindowActivationState.CodeActivated);
        Assert.Equal(1, startupCount);

        window.NotifyHostActivationChanged(WindowActivationState.Deactivated);
        window.NotifyHostActivationChanged(WindowActivationState.PointerActivated);
        window.NotifyHostActivationChanged(WindowActivationState.CodeActivated);

        Assert.Equal(1, startupCount);
        window.ShutdownExternalRenderer();
    }
}
