using ProGPU.Backend;
using ProGPU.Tests.Headless;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class SharedGpuTextureSourceTests
{
    [Fact]
    public void OwnerDisposalRemovesRegistryTokenButLeaseKeepsTextureAlive()
    {
        var texture = CreateTexture();
        var source = new SharedGpuTextureSource(texture);
        nint handle = source.Handle;
        using GpuTextureLease lease = source.AcquireTexture();

        Assert.True(SharedGpuTextureSource.TryAcquire(handle, out var registered, out var registryLease));
        Assert.Same(source, registered);
        Assert.Same(texture, registryLease!.Texture);
        registryLease.Dispose();

        source.Dispose();

        Assert.False(SharedGpuTextureSource.TryAcquire(handle, out registered, out registryLease));
        Assert.Null(registered);
        Assert.Null(registryLease);
        Assert.False(texture.IsDisposed);

        lease.Dispose();
        Assert.True(texture.IsDisposed);
    }

    [Fact]
    public void TextureIsReleasedOnlyAfterEveryLease()
    {
        var texture = CreateTexture();
        var source = new SharedGpuTextureSource(texture);
        GpuTextureLease first = source.AcquireTexture();
        GpuTextureLease second = source.AcquireTexture();

        source.Dispose();
        first.Dispose();

        Assert.False(texture.IsDisposed);

        second.Dispose();
        second.Dispose();

        Assert.True(texture.IsDisposed);
        Assert.Throws<ObjectDisposedException>(source.AcquireTexture);
    }

    [Fact]
    public void OwnerWithoutLeasesReleasesTextureImmediately()
    {
        var texture = CreateTexture();
        var source = new SharedGpuTextureSource(texture);

        source.Dispose();
        source.Dispose();

        Assert.True(texture.IsDisposed);
    }

    [Fact]
    public void UnknownRegistryTokenCannotBeAcquired()
    {
        Assert.False(SharedGpuTextureSource.TryAcquire(
            nint.MinValue,
            out SharedGpuTextureSource? source,
            out GpuTextureLease? lease));
        Assert.Null(source);
        Assert.Null(lease);
    }

    private static GpuTexture CreateTexture() =>
        new(
            HeadlessWindow.Shared.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Shared GPU texture source test");
}
