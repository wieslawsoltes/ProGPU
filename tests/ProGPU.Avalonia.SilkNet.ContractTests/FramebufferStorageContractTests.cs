using System;
using Avalonia.SilkNet;
using Xunit;

namespace ProGPU.Avalonia.SilkNet.ContractTests;

public sealed class FramebufferStorageContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveRequestsAreRejected(int requestedBytes)
    {
        using var storage = new SilkNetFramebufferAddressProvider();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => storage.GetAddress(requestedBytes));
        Assert.Equal(0, storage.Capacity);
    }

    [Fact]
    public void AddressRemainsStableWhileCapacityIsSufficient()
    {
        using var storage = new SilkNetFramebufferAddressProvider();

        IntPtr initial = storage.GetAddress(4096);
        IntPtr smaller = storage.GetAddress(1024);
        IntPtr exact = storage.GetAddress(4096);

        Assert.NotEqual(IntPtr.Zero, initial);
        Assert.Equal(initial, smaller);
        Assert.Equal(initial, exact);
        Assert.Equal(4096, storage.Capacity);
    }

    [Fact]
    public void GrowthUsesBoundedGeometricCapacity()
    {
        using var storage = new SilkNetFramebufferAddressProvider();

        storage.GetAddress(1024);
        storage.GetAddress(1025);
        Assert.Equal(2048, storage.Capacity);

        storage.GetAddress(5000);
        Assert.Equal(5000, storage.Capacity);
    }

    [Fact]
    public void DisposalReleasesCapacityAndIsIdempotent()
    {
        var storage = new SilkNetFramebufferAddressProvider();
        storage.GetAddress(256);

        storage.Dispose();
        storage.Dispose();

        Assert.Equal(0, storage.Capacity);
        Assert.Throws<ObjectDisposedException>(
            () => storage.GetAddress(1));
    }
}
