using System;
using Avalonia.Platform;
using Avalonia.SilkNet;
using Xunit;

namespace Avalonia.IntegrationTests.SilkNet;

public class SilkNetFramebufferAddressProviderTests
{
    [Fact]
    public void LockedFramebufferAllocatesCpuBufferOnlyWhenAddressIsRequested()
    {
        using var provider = new SilkNetFramebufferAddressProvider();
        using var framebuffer = new SilkNetLockedFramebuffer(
            provider,
            64,
            new PixelSize(4, 4),
            16,
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul,
            () => { },
            null!);

        Assert.Equal(0, provider.Capacity);

        var address = framebuffer.Address;

        Assert.NotEqual(IntPtr.Zero, address);
        Assert.Equal(64, provider.Capacity);
        Assert.Equal(address, framebuffer.Address);
    }

    [Fact]
    public void DirectGpuFramebufferDisposalDoesNotAllocateCpuBuffer()
    {
        using var provider = new SilkNetFramebufferAddressProvider();
        var framebuffer = new SilkNetLockedFramebuffer(
            provider,
            64,
            new PixelSize(4, 4),
            16,
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul,
            () => { },
            null!);

        framebuffer.Dispose();

        Assert.Equal(0, provider.Capacity);
    }

    [Fact]
    public void CpuBufferGrowsGeometricallyAndDoesNotShrink()
    {
        using var provider = new SilkNetFramebufferAddressProvider();

        var firstAddress = provider.GetAddress(100);
        var firstCapacity = provider.Capacity;
        var secondAddress = provider.GetAddress(101);

        Assert.NotEqual(IntPtr.Zero, firstAddress);
        Assert.NotEqual(IntPtr.Zero, secondAddress);
        Assert.Equal(100, firstCapacity);
        Assert.Equal(200, provider.Capacity);

        Assert.Equal(secondAddress, provider.GetAddress(150));
        Assert.Equal(200, provider.Capacity);
    }

    [Fact]
    public void LockedFramebufferReleasesOnlyOnce()
    {
        var releaseCount = 0;
        var framebuffer = new SilkNetLockedFramebuffer(
            IntPtr.Zero,
            new PixelSize(1, 1),
            4,
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul,
            () => releaseCount++,
            null!);

        framebuffer.Dispose();
        framebuffer.Dispose();

        Assert.Equal(1, releaseCount);
    }
}
