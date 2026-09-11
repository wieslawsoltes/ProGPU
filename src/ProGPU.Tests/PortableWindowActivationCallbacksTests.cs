using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableWindowActivationCallbacksTests
{
    [Fact]
    public void DialogReleaseCarriesActivationAndDeferredCompletionWithoutWpfTypes()
    {
        object activation = new();
        Action? pending = null;
        int completions = 0;
        var callbacks = new PortableWindowActivationCallbacks(activate: value => value)
        {
            ReleaseDialog = (value, completed) =>
            {
                Assert.Same(activation, value);
                pending = completed;
            }
        };
        callbacks.ReleaseDialog(activation, () => completions++);
        Assert.Equal(0, completions);
        pending!();
        Assert.Equal(1, completions);
        Assert.Null(new PortableWindowActivationCallbacks(activate: value => value).ReleaseDialog);
    }

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
