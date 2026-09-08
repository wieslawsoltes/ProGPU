using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public class PortableWpfRuntimeTests
{
    [Theory]
    [InlineData(false, PortableWpfMediaBackend.Portable)]
    [InlineData(true, PortableWpfMediaBackend.WindowsMil)]
    public void DefaultIsPlatformSpecificAndDiagnosticsDoNotFreeze(bool windows, PortableWpfMediaBackend expected)
    {
        var selection = new PortableWpfMediaBackendSelection(windows);
        Assert.Equal(expected, selection.ConfiguredBackend);
        Assert.False(selection.IsFrozen);
        Assert.Equal(expected, selection.GetAndFreeze());
        Assert.True(selection.IsFrozen);
    }

    [Fact]
    public void PortableSelectionBeforeUseDoesNotChangeAfterResourceAcquisition()
    {
        var selection = new PortableWpfMediaBackendSelection(true);
        selection.Select(PortableWpfMediaBackend.Portable);
        Assert.Equal(PortableWpfMediaBackend.Portable, selection.GetAndFreeze());
        selection.Select(PortableWpfMediaBackend.Portable);
        Assert.Throws<InvalidOperationException>(() => selection.Select(PortableWpfMediaBackend.WindowsMil));
        Assert.Equal(PortableWpfMediaBackend.Portable, selection.GetAndFreeze());
    }

    [Fact]
    public void LatePortableSelectionCannotChangeAnAcquiredWindowsDomain()
    {
        var selection = new PortableWpfMediaBackendSelection(true);
        Assert.Equal(PortableWpfMediaBackend.WindowsMil, selection.GetAndFreeze());
        Assert.Throws<InvalidOperationException>(() => selection.Select(PortableWpfMediaBackend.Portable));
        Assert.Equal(PortableWpfMediaBackend.WindowsMil, selection.ConfiguredBackend);
    }

    [Fact]
    public void InvalidAndUnavailableSelectionsLeaveThePolicyUnchanged()
    {
        var selection = new PortableWpfMediaBackendSelection(false);
        Assert.Throws<ArgumentOutOfRangeException>(() => selection.Select((PortableWpfMediaBackend)0));
        Assert.Throws<PlatformNotSupportedException>(() => selection.Select(PortableWpfMediaBackend.WindowsMil));
        Assert.False(selection.IsFrozen);
        Assert.Equal(PortableWpfMediaBackend.Portable, selection.ConfiguredBackend);
    }

    [Fact]
    public async Task ConcurrentFirstUseAndSelectionPublishOneImmutableDomain()
    {
        var selection = new PortableWpfMediaBackendSelection(true);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PortableWpfMediaBackend> reader = Task.Run(async () =>
        {
            await start.Task;
            return selection.GetAndFreeze();
        });
        Task<bool> writer = Task.Run(async () =>
        {
            await start.Task;
            try
            {
                selection.Select(PortableWpfMediaBackend.Portable);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        });
        start.SetResult(true);
        await Task.WhenAll(reader, writer).WaitAsync(TimeSpan.FromSeconds(10));
        PortableWpfMediaBackend acquired = await reader;
        Assert.Equal(acquired, selection.GetAndFreeze());
        Assert.Equal(await writer ? PortableWpfMediaBackend.Portable : PortableWpfMediaBackend.WindowsMil, acquired);
    }
}
