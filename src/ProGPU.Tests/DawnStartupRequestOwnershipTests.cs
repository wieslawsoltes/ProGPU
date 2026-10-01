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
