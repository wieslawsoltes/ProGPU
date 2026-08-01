using System.Reflection;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class GrBackendWrapperCompatibilityTests
{
    [Fact]
    public void BackendWrappersUseOfficialObjectHierarchy()
    {
        Assert.Equal(typeof(SKObject), typeof(GRBackendTexture).BaseType);
        Assert.Equal(typeof(SKObject), typeof(GRBackendRenderTarget).BaseType);
        Assert.False(typeof(GRBackendTexture).IsSealed);
        Assert.False(typeof(GRBackendRenderTarget).IsSealed);

        AssertDeclaredProtectedOverride<GRBackendTexture>("Dispose", typeof(bool));
        AssertDeclaredProtectedOverride<GRBackendTexture>("DisposeNative");
        AssertDeclaredProtectedOverride<GRBackendRenderTarget>("Dispose", typeof(bool));
        AssertDeclaredProtectedOverride<GRBackendRenderTarget>("DisposeNative");
    }

    [Fact]
    public void GlTextureExposesImmutableBackendMetadata()
    {
        var info = new GRGlTextureInfo(0x0de1, 17, 0x8058) { Protected = true };
        using var texture = new GRBackendTexture(320, 180, true, info);

        Assert.True(texture.IsValid);
        Assert.Equal(GRBackend.OpenGL, texture.Backend);
        Assert.Equal(320, texture.Width);
        Assert.Equal(180, texture.Height);
        Assert.Equal(new SKSizeI(320, 180), texture.Size);
        Assert.Equal(new SKRectI(0, 0, 320, 180), texture.Rect);
        Assert.True(texture.HasMipMaps);
        Assert.Equal(info, texture.GetGlTextureInfo());
        Assert.True(texture.GetGlTextureInfo(out var copy));
        Assert.Equal(info, copy);
        Assert.Null(texture.BackendTexture);
    }

    [Fact]
    public void GlRenderTargetExposesImmutableBackendMetadata()
    {
        var info = new GRGlFramebufferInfo(29, 0x8058) { Protected = true };
        using var target = new GRBackendRenderTarget(640, 360, 4, 8, info);

        Assert.True(target.IsValid);
        Assert.Equal(GRBackend.OpenGL, target.Backend);
        Assert.Equal(640, target.Width);
        Assert.Equal(360, target.Height);
        Assert.Equal(4, target.SampleCount);
        Assert.Equal(8, target.StencilBits);
        Assert.Equal(new SKSizeI(640, 360), target.Size);
        Assert.Equal(new SKRectI(0, 0, 640, 360), target.Rect);
        Assert.Equal(info, target.GetGlFramebufferInfo());
        Assert.True(target.GetGlFramebufferInfo(out var copy));
        Assert.Equal(info, copy);
        Assert.Null(target.BackendTexture);
    }

    [Fact]
    public void NonGlWrappersFailGlQueriesWithoutAllocatingAdapters()
    {
        var vkInfo = new GRVkImageInfo
        {
            Image = 0x1234,
            SampleCount = 4,
            LevelCount = 3,
        };
        using var texture = new GRBackendTexture(128, 64, vkInfo);
        using var target = new GRBackendRenderTarget(128, 64, vkInfo);

        Assert.Equal(GRBackend.Vulkan, texture.Backend);
        Assert.True(texture.HasMipMaps);
        Assert.True(texture.IsValid);
        Assert.False(texture.GetGlTextureInfo(out var textureInfo));
        Assert.Equal(default, textureInfo);
        Assert.Equal(default, texture.GetGlTextureInfo());

        Assert.Equal(GRBackend.Vulkan, target.Backend);
        Assert.Equal(4, target.SampleCount);
        Assert.True(target.IsValid);
        Assert.False(target.GetGlFramebufferInfo(out var framebufferInfo));
        Assert.Equal(default, framebufferInfo);
        Assert.Equal(default, target.GetGlFramebufferInfo());
    }

    [Fact]
    public void D3DAndMetalWrappersBorrowCallerOwnedResources()
    {
        var d3dInfo = new TrackedD3DInfo
        {
            Resource = (IntPtr)0x1234,
            LevelCount = 2,
            SampleCount = 8,
        };
        using (var texture = new GRBackendTexture(32, 16, d3dInfo))
        using (var target = new GRBackendRenderTarget(32, 16, d3dInfo))
        {
            Assert.Equal(GRBackend.Direct3D, texture.Backend);
            Assert.True(texture.HasMipMaps);
            Assert.Equal(GRBackend.Direct3D, target.Backend);
            Assert.Equal(8, target.SampleCount);
        }

        using (var texture = new GRBackendTexture(
                   32,
                   16,
                   false,
                   new GRMtlTextureInfo((IntPtr)0x5678)))
        using (var target = new GRBackendRenderTarget(
                   32,
                   16,
                   new GRMtlTextureInfo((IntPtr)0x5678)))
        {
            Assert.Equal(GRBackend.Metal, texture.Backend);
            Assert.Equal(GRBackend.Metal, target.Backend);
        }

        Assert.Equal(0, d3dInfo.DisposeCount);
    }

    [Fact]
    public void DisposeInvalidatesOnlyTheWrapper()
    {
        var texture = new GRBackendTexture(
            4,
            4,
            false,
            new GRGlTextureInfo(0x0de1, 1, 0x8058));
        var target = new GRBackendRenderTarget(
            4,
            4,
            1,
            0,
            new GRGlFramebufferInfo(1, 0x8058));

        texture.Dispose();
        target.Dispose();

        Assert.False(texture.IsValid);
        Assert.False(target.IsValid);
    }

    [Fact]
    public void OfficialConstructorParameterNamesAreStable()
    {
        AssertParameterNames(
            typeof(GRBackendTexture).GetConstructor(
                [typeof(int), typeof(int), typeof(bool), typeof(GRGlTextureInfo)]),
            "width", "height", "mipmapped", "glInfo");
        AssertParameterNames(
            typeof(GRBackendTexture).GetConstructor(
                [typeof(int), typeof(int), typeof(bool), typeof(GRMtlTextureInfo)]),
            "width", "height", "mipmapped", "mtlInfo");
        AssertParameterNames(
            typeof(GRBackendTexture).GetConstructor(
                [typeof(int), typeof(int), typeof(GRVkImageInfo)]),
            "width", "height", "vkInfo");
        AssertParameterNames(
            typeof(GRBackendTexture).GetConstructor(
                [typeof(int), typeof(int), typeof(GRD3DTextureResourceInfo)]),
            "width", "height", "d3dTextureInfo");
        AssertParameterNames(
            typeof(GRBackendRenderTarget).GetConstructor(
                [typeof(int), typeof(int), typeof(GRMtlTextureInfo)]),
            "width", "height", "mtlInfo");
        AssertParameterNames(
            typeof(GRBackendRenderTarget).GetConstructor(
                [typeof(int), typeof(int), typeof(GRVkImageInfo)]),
            "width", "height", "vkImageInfo");
        AssertParameterNames(
            typeof(GRBackendRenderTarget).GetConstructor(
                [typeof(int), typeof(int), typeof(GRD3DTextureResourceInfo)]),
            "width", "height", "d3dTextureInfo");
    }

    private static void AssertDeclaredProtectedOverride<T>(string name, params Type[] parameters)
    {
        var method = typeof(T).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            parameters,
            modifiers: null);
        Assert.NotNull(method);
        Assert.Equal(typeof(T), method!.DeclaringType);
        Assert.True(method.IsVirtual);
        Assert.False(method.IsFinal);
    }

    private static void AssertParameterNames(MethodBase? method, params string[] expected)
    {
        Assert.NotNull(method);
        Assert.Equal(expected, method!.GetParameters().Select(static parameter => parameter.Name));
    }

    private sealed class TrackedD3DInfo : GRD3DTextureResourceInfo
    {
        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }
}
