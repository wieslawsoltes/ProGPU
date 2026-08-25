using System.Reflection;
using Avalonia.Skia;
using Xunit;

namespace ProGPU.BinaryCompatibility.Tests;

public sealed class BinaryCompatibilityAssemblyTests
{
    [Fact]
    public void AvaloniaSkiaFacadeUsesOfficialIdentityAndForwardsLeaseContracts()
    {
        var facadePath = Path.Combine(
            AppContext.BaseDirectory,
            "Avalonia.Skia.dll");
        var identity = AssemblyName.GetAssemblyName(facadePath);

        Assert.Equal("Avalonia.Skia", identity.Name);
        Assert.Equal(new Version(12, 1, 1, 0), identity.Version);
        Assert.Equal(
            "c8d484a7012f9a8b",
            Convert.ToHexString(identity.GetPublicKeyToken() ?? [])
                .ToLowerInvariant());

        var facade = Assembly.Load(identity);
        var forwarded = facade.GetForwardedTypes();

        Assert.Contains(typeof(ISkiaSharpApiLeaseFeature), forwarded);
        Assert.Contains(typeof(ISkiaSharpApiLease), forwarded);
        Assert.Contains(
            typeof(ISkiaSharpPlatformGraphicsApiLease),
            forwarded);
        Assert.All(
            forwarded,
            static type => Assert.Same(
                typeof(ISkiaSharpApiLeaseFeature).Assembly,
                type.Assembly));
    }
}
