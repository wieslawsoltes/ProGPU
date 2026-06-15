using System.Reflection;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class StrongNameSigningTests
{
    private static readonly byte[] ExpectedPublicKeyToken =
    [
        0xc2, 0x9c, 0x97, 0x52, 0x85, 0x5e, 0xe1, 0x83
    ];

    [Theory]
    [InlineData(typeof(WgpuContext))]
    [InlineData(typeof(Compositor))]
    [InlineData(typeof(TtfFont))]
    [InlineData(typeof(PathGeometry))]
    public void ProGpuCoreAssembliesAreStrongNameSigned(Type publicType)
    {
        var token = publicType.Assembly.GetName().GetPublicKeyToken();

        Assert.NotNull(token);
        Assert.Equal(ExpectedPublicKeyToken, token);
    }
}
