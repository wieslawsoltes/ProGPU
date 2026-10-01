using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Xunit;

namespace ProGPU.Tests;

// Device-free wiring guards; the isolated native control proves callback and
// result retirement. Neither replaces package or platform presentation gates.
public sealed class DawnStartupRequestOwnershipTests
{
    [Fact]
    public void AdapterRoutesRetainTheirOptionsAndShareOwnedRequestCompletion()
    {
        string ordinary = Read("DawnGpuContext.cs");
        string presentation = Read("DawnNativePresentation.cs");
        string offscreen = Read("DawnGpuContext.Offscreen.cs");
        string requests = Read("DawnGpuContext.Requests.cs");
        Assert.Contains("return RequestOwnedAdapter(instance, options, \"request a Metal adapter\");", ordinary);
        Assert.Contains("BackendType = W.BackendType.Metal", ordinary);
        Assert.Contains("CompatibleSurface = compatibleSurface", presentation);
        Assert.Contains("return RequestOwnedAdapter(instance, options, $\"request a {backendName} adapter\");", presentation);
        Assert.Contains("ForceFallbackAdapter = forceFallbackAdapter", offscreen);
        Assert.Contains("offscreenFallbackPolicy: forceFallbackAdapter", offscreen);
        foreach (string source in new[] { ordinary, presentation, offscreen })
            Assert.DoesNotContain("new AdapterRequest()", source);
        Assert.Contains("state.BeginNativeUse()", requests);
        Assert.Contains("state.CancelUnqueuedNativeUse(); throw;", requests);
        Assert.Contains("state.TakeHandle((int)W.RequestAdapterStatus.Success, operation)", requests);
        Assert.Contains("DrainAbandonedDawnRequest(instance, queued, state, error);", requests);
        Assert.Contains("finally { state.EndManagedUse(failure); }", requests);
        string production = requests[..requests.IndexOf("internal static void VerifyDawnAdapterRequestAbandonmentForDiagnostics", StringComparison.Ordinal)];
        Assert.DoesNotContain("CreateSystemWarpOffscreen(", production);
        Assert.DoesNotContain("DawnSystemWarpArtifact", production);
    }

    [Fact]
    public void OrdinaryAndWarpDeviceRequestsUseTheSameTestedOwner()
    {
        string ordinary = Read("DawnGpuContext.cs");
        string owned = Read("DawnGpuContext.SystemWarp.cs");
        Assert.Contains("=> RequestOwnedDevice(instance, adapter, requiredFeatures,", ordinary);
        Assert.DoesNotContain("new DeviceRequest()", ordinary);
        Assert.Contains("device = RequestDevice(instance, adapter, features[..featureCount],", owned);
        Assert.Contains("new OwnedDawnRequest<DeviceHandle>", owned);
        Assert.Contains("state.RetainUntilRetirement(retainedLoss.Free);", owned);
        Assert.Contains("lossHandle = default;", owned);
    }

    [Theory]
    [InlineData("DawnGpuContext.cs", "public static DawnGpuContext CreateMetalPresentation()")]
    [InlineData("DawnNativePresentation.cs", "public static DawnGpuContext CreateNativePresentation(")]
    [InlineData("DawnGpuContext.Offscreen.cs", "public static DawnGpuContext CreateOffscreen(")]
    public void FactoriesTransferRawHandlesBeforeAnyContextPublication(string file, string factory)
    {
        string source = Read(file);
        int start = source.IndexOf(factory, StringComparison.Ordinal);
        Assert.True(start >= 0);
        source = source[start..];
        int transfer = source.IndexOf("lifetime = new DawnDeviceLifetime(new NativeLifetime(", StringComparison.Ordinal);
        int allocate = source.IndexOf("context = new WgpuContext", transfer, StringComparison.Ordinal);
        int initialize = source.IndexOf("context.InitializeExternalNativeDevice(", allocate, StringComparison.Ordinal);
        Assert.True(transfer >= 0 && allocate > transfer && initialize > allocate);
        string transferred = source[transfer..allocate];
        foreach (string raw in new[] { "instance = InstanceHandle.Null;", "adapter = AdapterHandle.Null;",
                     "device = DeviceHandle.Null;", "queue = QueueHandle.Null;", "deviceLossStateHandle = default;" })
            Assert.Contains(raw, transferred);
        Assert.Contains("var result = new DawnGpuContext(", source[allocate..initialize]);
        Assert.Contains("deviceLossState.Bind(context);", source[allocate..initialize]);
        Assert.Contains("if (context.IsDeviceLost)", source[allocate..initialize]);
        int published = source.IndexOf("return result;", initialize, StringComparison.Ordinal);
        Assert.Contains("if (context.IsDeviceLost)", source[initialize..published]);
        string cleanup = source[published..];
        Assert.Contains("try { context?.Dispose(); } catch { }", cleanup);
        Assert.Contains("try { lifetime?.Dispose(); } catch { }", cleanup);
        Assert.True(cleanup.IndexOf("instance.Release();", StringComparison.Ordinal) <
                    cleanup.IndexOf("deviceLossStateHandle.Free();", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedFactoryOwnerReleasesOnceEvenWithReentrantOrThrowingCleanup(bool throwOnRelease)
    {
        var native = new CountingLifetime();
        var owner = new DawnDeviceLifetime(native);
        native.OnDispose = () =>
        {
            owner.Dispose();
            if (throwOnRelease) throw new InvalidOperationException("original cleanup failure");
        };
        owner.Poll(false);
        owner.Poll(true);
        Assert.Equal(new[] { false, true }, native.Polls);
        if (throwOnRelease)
            Assert.Equal("original cleanup failure", Assert.Throws<InvalidOperationException>(owner.Dispose).Message);
        else owner.Dispose();
        owner.Dispose();
        Assert.Equal(1, native.Releases);
        Assert.Throws<ObjectDisposedException>(() => owner.Poll(false));
        Assert.Equal(2, native.Polls.Count);
    }

    private sealed class CountingLifetime : IWebGpuExternalDeviceLifetime
    {
        internal readonly List<bool> Polls = [];
        internal int Releases;
        internal Action? OnDispose;
        public void Poll(bool wait) => Polls.Add(wait);
        public void Dispose() { Releases++; OnDispose?.Invoke(); }
    }

    private static string Read(string name)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "src", "ProGPU.Backend.Dawn", name);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(name);
    }
}
