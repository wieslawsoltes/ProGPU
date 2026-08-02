using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableWindowActivationCallbacksTests
{
    [Fact]
    public void RequestActivationPreservesTypedActivationIdentity()
    {
        var activation = new object();
        object? requestedActivation = null;
        var callbacks = new PortableWindowActivationCallbacks(
            activate: _ => activation,
            requestActivation: candidate =>
            {
                requestedActivation = candidate;
                return ReferenceEquals(candidate, activation);
            });

        object? resolvedActivation = callbacks.Activate(new object());

        Assert.Same(activation, resolvedActivation);
        Assert.NotNull(callbacks.RequestActivation);
        Assert.True(callbacks.RequestActivation(resolvedActivation!));
        Assert.Same(activation, requestedActivation);
    }

    [Fact]
    public void RequestActivationRemainsOptionalForExistingHosts()
    {
        var callbacks = new PortableWindowActivationCallbacks(activate: value => value);

        Assert.Null(callbacks.RequestActivation);
    }
}
