using System.Reflection;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class GrBackendHandleInfoCompatibilityTests
{
    [Fact]
    public void GlFramebufferInfoUsesOfficialSequentialStorage()
    {
        AssertFieldTypes<GRGlFramebufferInfo>(typeof(uint), typeof(uint), typeof(byte));

        var info = new GRGlFramebufferInfo(17);
        Assert.Equal(17u, info.FramebufferObjectId);
        Assert.Equal(0u, info.Format);
        Assert.False(info.Protected);

        info.Format = 0x8058;
        info.Protected = true;
        var same = new GRGlFramebufferInfo(17, 0x8058) { Protected = true };
        Assert.Equal(same, info);
        Assert.Equal(same.GetHashCode(), info.GetHashCode());
        Assert.True(same == info);
        Assert.False(same != info);

        info.Protected = false;
        Assert.NotEqual(same, info);
    }

    [Fact]
    public void GlTextureInfoUsesOfficialSequentialStorage()
    {
        AssertFieldTypes<GRGlTextureInfo>(typeof(uint), typeof(uint), typeof(uint), typeof(byte));

        var info = new GRGlTextureInfo(0x0de1, 29);
        Assert.Equal(0x0de1u, info.Target);
        Assert.Equal(29u, info.Id);
        Assert.Equal(0u, info.Format);
        Assert.False(info.Protected);

        info.Format = 0x8058;
        info.Protected = true;
        var same = new GRGlTextureInfo(0x0de1, 29, 0x8058) { Protected = true };
        Assert.Equal(same, info);
        Assert.Equal(same.GetHashCode(), info.GetHashCode());
        Assert.True(same == info);
        Assert.False(same != info);

        info.Id++;
        Assert.NotEqual(same, info);
    }

    [Fact]
    public void MetalTextureInfoRetainsPointerIdentity()
    {
        AssertFieldTypes<GRMtlTextureInfo>(typeof(IntPtr));

        var info = new GRMtlTextureInfo((IntPtr)0x1234);
        var same = new GRMtlTextureInfo((IntPtr)0x1234);
        Assert.Equal((IntPtr)0x1234, info.TextureHandle);
        Assert.True(info.Equals(same));
        Assert.True(info == same);
        Assert.Equal(info.GetHashCode(), same.GetHashCode());

        info.TextureHandle = (IntPtr)0x5678;
        Assert.True(info != same);
        Assert.False(info.Equals((object)same));
    }

    [Fact]
    public void ConstructorAndValueParameterNamesMatchOfficialContract()
    {
        AssertParameterNames(typeof(GRGlFramebufferInfo).GetConstructor([typeof(uint)]), "fboId");
        AssertParameterNames(typeof(GRGlFramebufferInfo).GetConstructor([typeof(uint), typeof(uint)]), "fboId", "format");
        AssertParameterNames(typeof(GRGlTextureInfo).GetConstructor([typeof(uint), typeof(uint)]), "target", "id");
        AssertParameterNames(typeof(GRGlTextureInfo).GetConstructor([typeof(uint), typeof(uint), typeof(uint)]), "target", "id", "format");
        AssertParameterNames(typeof(GRMtlTextureInfo).GetConstructor([typeof(IntPtr)]), "textureHandle");
    }

    private static void AssertFieldTypes<T>(params Type[] expected)
    {
        var fieldTypes = typeof(T)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .OrderBy(static field => field.MetadataToken)
            .Select(static field => field.FieldType);
        Assert.Equal(expected, fieldTypes);
    }

    private static void AssertParameterNames(MethodBase? method, params string[] expected)
    {
        Assert.NotNull(method);
        Assert.Equal(expected, method!.GetParameters().Select(static parameter => parameter.Name));
    }
}
