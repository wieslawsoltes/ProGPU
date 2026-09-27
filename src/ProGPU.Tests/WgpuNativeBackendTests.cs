using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class WgpuNativeBackendTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("auto")]
    [InlineData(" AUTOMATIC ")]
    public void AutomaticRetainsTheDefaultOptions(string? value) =>
        Assert.Same(WgpuNativeBackendOptions.Default, WgpuNativeBackendOptions.Parse(value));

    [Theory]
    [InlineData("vulkan", WgpuNativeBackend.Vulkan)]
    [InlineData(" VULKAN ", WgpuNativeBackend.Vulkan)]
    [InlineData("gl", WgpuNativeBackend.OpenGL)]
    [InlineData("gles", WgpuNativeBackend.OpenGL)]
    [InlineData("opengl", WgpuNativeBackend.OpenGL)]
    [InlineData("metal", WgpuNativeBackend.Metal)]
    [InlineData("dx12", WgpuNativeBackend.D3D12)]
    [InlineData("d3d12", WgpuNativeBackend.D3D12)]
    public void ExplicitNamesSelectExactlyOneBackend(string value, WgpuNativeBackend expected) =>
        Assert.Equal(expected, WgpuNativeBackendOptions.Parse(value).Preference);

    [Theory]
    [InlineData("vulkan,gl")]
    [InlineData("dx11")]
    [InlineData("browser")]
    [InlineData("unknown")]
    [InlineData("1")]
    [InlineData("vulkan\0")]
    public void UnsupportedRequestsNeverBecomeAutomatic(string value) =>
        Assert.Throws<ArgumentException>(() => WgpuNativeBackendOptions.Parse(value));

    [Fact]
    public void ProGpuEnvironmentSettingOverridesTheCompatibleAlias()
    {
        Assert.Equal(WgpuNativeBackend.Vulkan, WgpuNativeBackendOptions.FromEnvironmentValues(null, "vulkan").Preference);
        Assert.Equal(WgpuNativeBackend.Metal, WgpuNativeBackendOptions.FromEnvironmentValues("metal", "vulkan").Preference);
        Assert.Same(WgpuNativeBackendOptions.Default, WgpuNativeBackendOptions.FromEnvironmentValues("auto", "gl"));
        Assert.Same(WgpuNativeBackendOptions.Default, WgpuNativeBackendOptions.FromEnvironmentValues("", "gl"));
        Assert.Throws<ArgumentException>(() => WgpuNativeBackendOptions.FromEnvironmentValues("invalid", "vulkan"));
    }

    [Theory]
    [InlineData(false, false, false, 0u)]
    [InlineData(true, false, false, 8u)]
    [InlineData(false, true, false, 1u)]
    [InlineData(false, false, true, 4u)]
    public void UnconfiguredPlatformMasksRemainUnchanged(bool windows, bool android, bool ios, uint expected) =>
        Assert.Equal(expected, WgpuNativeBackendOptions.Default.ResolveInstanceBackends(
            windows, android, ios, WgpuDx12ShaderCompiler.Automatic));

    [Theory]
    [InlineData(WgpuNativeBackend.Vulkan, 1u, BackendType.Vulkan)]
    [InlineData(WgpuNativeBackend.OpenGL, 2u, BackendType.OpenGL)]
    [InlineData(WgpuNativeBackend.OpenGL, 2u, BackendType.OpenGles)]
    [InlineData(WgpuNativeBackend.Metal, 4u, BackendType.Metal)]
    [InlineData(WgpuNativeBackend.D3D12, 8u, BackendType.D3D12)]
    public unsafe void ExplicitMasksReachTheActualPinnedDescriptor(
        WgpuNativeBackend backend, uint expected, BackendType actual)
    {
        var options = new WgpuNativeBackendOptions(backend);
        uint mask = options.ResolveInstanceBackends(false, false, false, WgpuDx12ShaderCompiler.Automatic);
        Assert.Equal(expected, mask);
        var extras = WgpuContext.CreateNativeInstanceExtras(mask, WgpuDx12ShaderCompiler.Automatic, null, null);
        Assert.Equal(0x00030006u, (uint)extras.Chain.SType);
        Assert.True(extras.Chain.Next == null);
        Assert.Equal(expected, extras.Backends);
        Assert.Equal(0u, extras.Flags);
        Assert.Equal(0, extras.Dx12ShaderCompiler);
        Assert.Equal(0, extras.Gles3MinorVersion);
        Assert.True(extras.DxcPath == null && extras.DxilPath == null);
        options.ValidateAdapter(actual);
        Assert.Throws<NotSupportedException>(() => options.ValidateAdapter(BackendType.Undefined));
        Assert.Throws<NotSupportedException>(() => options.ValidateAdapter(
            actual == BackendType.Vulkan ? BackendType.Metal : BackendType.Vulkan));
    }

    [Fact]
    public unsafe void DefaultDesktopDescriptorHasNoExtensionChain()
    {
        var extras = WgpuContext.CreateNativeInstanceExtras(0, WgpuDx12ShaderCompiler.Automatic, null, null);
        Assert.Equal(0u, (uint)extras.Chain.SType);
        Assert.Equal(0u, extras.Backends);
    }

    [Fact]
    public unsafe void BackendMaskPreservesDx12PathsAndThePinnedAbi()
    {
        byte compiler = 1, validator = 2;
        var extras = WgpuContext.CreateNativeInstanceExtras(8, WgpuDx12ShaderCompiler.Dxc, &compiler, &validator);
        Assert.Equal(8u, extras.Backends);
        Assert.Equal(2, extras.Dx12ShaderCompiler);
        Assert.True(extras.DxcPath == &compiler && extras.DxilPath == &validator);
        Assert.Equal(IntPtr.Size == 8 ? 48 : 32, sizeof(WgpuContext.NativeInstanceExtras));
        Assert.Equal(2 * IntPtr.Size, Marshal.OffsetOf<WgpuContext.NativeInstanceExtras>(nameof(extras.Backends)).ToInt32());
        Assert.Equal(2 * IntPtr.Size + 16, Marshal.OffsetOf<WgpuContext.NativeInstanceExtras>(nameof(extras.DxilPath)).ToInt32());
        Assert.Equal(3 * IntPtr.Size + 16, Marshal.OffsetOf<WgpuContext.NativeInstanceExtras>(nameof(extras.DxcPath)).ToInt32());
    }

    [Theory]
    [InlineData(WgpuNativeBackend.OpenGL, false, true, false, WgpuDx12ShaderCompiler.Automatic)]
    [InlineData(WgpuNativeBackend.Metal, false, true, false, WgpuDx12ShaderCompiler.Automatic)]
    [InlineData(WgpuNativeBackend.Vulkan, false, false, true, WgpuDx12ShaderCompiler.Automatic)]
    [InlineData(WgpuNativeBackend.OpenGL, false, false, true, WgpuDx12ShaderCompiler.Automatic)]
    [InlineData(WgpuNativeBackend.Vulkan, true, false, false, WgpuDx12ShaderCompiler.Fxc)]
    [InlineData(WgpuNativeBackend.Metal, true, false, false, WgpuDx12ShaderCompiler.Dxc)]
    public void ConflictingMobileOrCompilerSelectionIsRejected(
        WgpuNativeBackend backend, bool windows, bool android, bool ios, WgpuDx12ShaderCompiler compiler) =>
        Assert.Throws<NotSupportedException>(() => new WgpuNativeBackendOptions(backend)
            .ResolveInstanceBackends(windows, android, ios, compiler));

    [Theory]
    [InlineData(WgpuNativeBackend.Automatic, true, false, false, WgpuDx12ShaderCompiler.Dxc, 8u)]
    [InlineData(WgpuNativeBackend.D3D12, true, false, false, WgpuDx12ShaderCompiler.Fxc, 8u)]
    [InlineData(WgpuNativeBackend.Vulkan, false, true, false, WgpuDx12ShaderCompiler.Automatic, 1u)]
    [InlineData(WgpuNativeBackend.Metal, false, false, true, WgpuDx12ShaderCompiler.Automatic, 4u)]
    public void CompatibleMobileAndCompilerRequestsKeepTheirExactMask(
        WgpuNativeBackend backend, bool windows, bool android, bool ios, WgpuDx12ShaderCompiler compiler, uint expected) =>
        Assert.Equal(expected, new WgpuNativeBackendOptions(backend).ResolveInstanceBackends(windows, android, ios, compiler));

    [Theory]
    [InlineData(WgpuNativeBackend.Vulkan)]
    [InlineData(WgpuNativeBackend.OpenGL)]
    [InlineData(WgpuNativeBackend.Metal)]
    [InlineData(WgpuNativeBackend.D3D12)]
    public void ExplicitSelectionCannotReconfigureBorrowedDevices(WgpuNativeBackend backend) =>
        Assert.Throws<NotSupportedException>(() => new WgpuNativeBackendOptions(backend).ValidateOwnership(false));

    [Fact]
    public void AutomaticAllowsBorrowedAndSharedDevicesWithoutInventingIdentity()
    {
        WgpuNativeBackendOptions.Default.ValidateOwnership(false);
        WgpuNativeBackendOptions.Default.ValidateAdapter(BackendType.Undefined);
        WgpuNativeBackendOptions.Default.ValidateAdapter(BackendType.Vulkan);
    }

    [Fact]
    public unsafe void InvalidTypedChoiceFailsBeforeNativeLibraryOrInstanceCreation()
    {
        using var context = new WgpuContext
        {
            NativeBackendOptions = new((WgpuNativeBackend)999),
            Dx12CompilerOptions = WgpuDx12CompilerOptions.Default
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => context.Initialize(null));
        Assert.Null(context.Api);
        Assert.True(context.Instance == null && context.Adapter == null && context.Device == null);
    }

    [Fact]
    public void OwnedSharedAndBorrowedEntryPointsEnforceSelectionBeforePublishingHandles()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string source = File.ReadAllText(Path.Combine(directory.FullName, "src/ProGPU.Backend/WgpuContext.cs"));
        int validation = source.IndexOf("uint instanceBackends = NativeBackendOptions.ResolveInstanceBackends(", StringComparison.Ordinal);
        int load = source.IndexOf("Wgpu = CreateNativeWebGpuApi();", validation, StringComparison.Ordinal);
        int descriptor = source.IndexOf("CreateNativeInstanceExtras(instanceBackends,", load, StringComparison.Ordinal);
        int create = source.IndexOf("Instance = Wgpu.CreateInstance(&instanceDesc);", descriptor, StringComparison.Ordinal);
        int verify = source.IndexOf("NativeBackendOptions.ValidateAdapter(AdapterBackendType);", create, StringComparison.Ordinal);
        int release = source.IndexOf("ReleaseAdapterInitializationResources();", verify, StringComparison.Ordinal);
        int device = source.IndexOf("// 4. Request Device", release, StringComparison.Ordinal);
        Assert.True(validation >= 0 && load > validation && descriptor > load && create > descriptor && verify > create && release > verify && device > release);
        int shared = source.IndexOf("public void InitializeSharedDevice(", StringComparison.Ordinal);
        int owner = source.IndexOf("NativeBackendOptions.ValidateAdapter(deviceOwner.AdapterBackendType);", shared, StringComparison.Ordinal);
        int acquire = source.IndexOf("deviceOwner._sharedDeviceLifetime?.Acquire()", owner, StringComparison.Ordinal);
        Assert.True(owner > shared && acquire > owner);
        Assert.Equal(2, source.Split("NativeBackendOptions.ValidateOwnership(false);", StringSplitOptions.None).Length - 1);
    }
}
