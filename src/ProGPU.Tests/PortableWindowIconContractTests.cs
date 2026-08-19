using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableWindowIconContractTests
{
    [Fact]
    public void PortableWindowStateCarriesOpaqueIconSource()
    {
        object icon = new();
        var state = new PortableWindowState
        {
            HasIcon = true,
            Icon = icon
        };

        Assert.True(state.HasIcon);
        Assert.Same(icon, state.Icon);
    }

    [Fact]
    public void ActivationCallbacksForwardIconUpdatesIncludingClear()
    {
        object activation = new();
        object icon = new();
        object? receivedActivation = null;
        object? receivedIcon = null;
        int updateCount = 0;
        var callbacks = new PortableWindowActivationCallbacks(
            activate: _ => activation,
            setIcon: (value, source) =>
            {
                receivedActivation = value;
                receivedIcon = source;
                updateCount++;
            });

        callbacks.SetIcon!(activation, icon);

        Assert.Same(activation, receivedActivation);
        Assert.Same(icon, receivedIcon);

        callbacks.SetIcon(activation, null);

        Assert.Equal(2, updateCount);
        Assert.Null(receivedIcon);
    }
}
