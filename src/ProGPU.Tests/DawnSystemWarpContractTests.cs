using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace ProGPU.Tests;

// Authored, device-free source/provenance controls. These are not substitute
// evidence for original-header compilation or actual Windows WARP completion.
public sealed class DawnSystemWarpContractTests
{
    [Fact]
    public void NativeSelectionUsesOriginalPublicTypesAndVerifiesActualWarp()
    {
        string source = Read("eng", "dawn-system-warp", "progpu_dawn_system_warp.cpp");
        Assert.Contains("#include <dawn/native/D3DBackend.h>", source, StringComparison.Ordinal);
        Assert.Contains("dawn::native::d3d::RequestAdapterOptionsLUID selection;", source, StringComparison.Ordinal);
        Assert.Contains("factory->EnumWarpAdapter(IID_PPV_ARGS(&adapter))", source, StringComparison.Ordinal);
        Assert.Contains("selection.adapterLUID = descriptor.AdapterLuid;", source, StringComparison.Ordinal);
        Assert.Contains("options.forceFallbackAdapter = WGPU_FALSE;", source, StringComparison.Ordinal);
        Assert.Equal(1, Count(source, "wgpuInstanceRequestAdapter(instance, &options, *callback)"));
        Assert.Contains("dawn::native::d3d::GetDXGIAdapter(adapter)", source, StringComparison.Ordinal);
        Assert.Contains("info.backendType == WGPUBackendType_D3D12 && info.adapterType == WGPUAdapterType_CPU", source, StringComparison.Ordinal);
        Assert.Contains("actual.Flags & DXGI_ADAPTER_FLAG_SOFTWARE", source, StringComparison.Ordinal);
        Assert.Contains("actual.AdapterLuid.LowPart != low || actual.AdapterLuid.HighPart != high", source, StringComparison.Ordinal);
        Assert.Contains("current_warp.AdapterLuid.LowPart != low || current_warp.AdapterLuid.HighPart != high", source, StringComparison.Ordinal);
        Assert.Contains("offsetof(WGPUChainedStruct, next)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("struct RequestAdapterOptionsLUID", source, StringComparison.Ordinal);
        Assert.DoesNotContain("D3D12CreateDevice", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnumAdapters", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PreparationPinsOriginalGeneratorAndEveryActualInputMachine()
    {
        using JsonDocument document = JsonDocument.Parse(Read("eng", "dawn-system-warp", "inputs.json"));
        JsonElement pin = document.RootElement;
        Assert.Equal("01249a97332468dbdd6cf5edb8dd7bae77875de5", pin.GetProperty("dawnRevision").GetString());
        Assert.Equal(28411907227L, pin.GetProperty("producerRun").GetInt64());
        Assert.Equal("32f8063fa2aa5977da27d8a39479581ac834e6cb0cf39f2f9bd6de928a998e5d", pin.GetProperty("generatedHeaderSha256").GetString());
        JsonElement runtimes = pin.GetProperty("runtimes");
        Assert.Equal(2, runtimes.EnumerateObject().Count());
        Assert.Equal(0x8664, runtimes.GetProperty("win-x64").GetProperty("peMachine").GetInt32());
        Assert.Equal(0xAA64, runtimes.GetProperty("win-arm64").GetProperty("peMachine").GetInt32());
        Assert.Equal("f8801b2be52bf1ddd536c72e2e8e77f757f32a85846c3505d0215ef19b444f5f", runtimes.GetProperty("win-x64").GetProperty("providerSha256").GetString());
        Assert.Equal("68e388593559c1a5dad9537873b0377f02aaec55d5f30b5eb5246b8df9743dc6", runtimes.GetProperty("win-arm64").GetProperty("providerSha256").GetString());
        string script = Read("eng", "build-dawn-system-warp-windows.ps1");
        Assert.Contains("'--targets','headers,cpp_headers'", script, StringComparison.Ordinal);
        Assert.Contains("'--jinja2-path',$jinja, '--markupsafe-path',$markup", script, StringComparison.Ordinal);
        Assert.Contains("Assert-Hash (Join-Path $generated 'include/dawn/webgpu.h') $pin.generatedHeaderSha256", script, StringComparison.Ordinal);
        Assert.Contains("Assert-ImportLibrary (Join-Path $release 'webgpu_dawn.lib')", script, StringComparison.Ordinal);
        Assert.Contains("$machine -ne $runtime.peMachine", script, StringComparison.Ordinal);
        Assert.Contains("Assert-Pe $companion", script, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::Move($stage,$output)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--allow-fallback", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalManagedFactoryKeepsPendingCallbacksAndLossStateOwned()
    {
        string source = Read("src", "ProGPU.Backend.Dawn", "DawnGpuContext.SystemWarp.cs");
        Assert.Contains("public static DawnGpuContext CreateSystemWarpOffscreen", source, StringComparison.Ordinal);
        Assert.Contains("public DawnSystemWarpAdapterLuid? SystemWarpAdapterLuid", source, StringComparison.Ordinal);
        Assert.Contains("state.BeginNativeUse()", source, StringComparison.Ordinal);
        Assert.Contains("if (status < 0) state.CancelUnqueuedNativeUse();", source, StringComparison.Ordinal);
        Assert.Contains("finally { state.EndManagedUse(failure); }", source, StringComparison.Ordinal);
        Assert.Contains("retire = --_uses == 0;", source, StringComparison.Ordinal);
        Assert.Contains("if (_managedEnded) return;", source, StringComparison.Ordinal);
        Assert.Contains("finally { EndNativeUse(); }", source, StringComparison.Ordinal);
        int shutdown = source.IndexOf("if (instance != InstanceHandle.Null) Cleanup(instance.Release);", StringComparison.Ordinal);
        int lossRetirement = source.IndexOf("if (lossHandle.IsAllocated) Cleanup(lossHandle.Free);", StringComparison.Ordinal);
        Assert.True(shutdown >= 0 && lossRetirement > shutdown);
        Assert.Contains("[LibraryImport(LibraryName", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ForceFallbackAdapter =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDelegateForFunctionPointer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetDllImportResolver", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationControlAbandonsRealRequestsBeforeWaitAndRequiresNativeRetirement()
    {
        string source = Read("src", "ProGPU.Backend.Dawn", "DawnGpuContext.SystemWarp.cs");
        Assert.Contains("=> CreateSystemWarpOffscreenCore(companionDirectory, null);", source, StringComparison.Ordinal);
        Assert.Contains("cancellationProbe?.BeforeWait(deviceRequest: false);", source, StringComparison.Ordinal);
        Assert.Contains("cancellationProbe?.BeforeWait(deviceRequest: true);", source, StringComparison.Ordinal);
        Assert.Contains("probe.Adapter.Verify((int)W.RequestAdapterStatus.CallbackCancelled);", source, StringComparison.Ordinal);
        Assert.Contains("probe.Device.Verify((int)W.RequestDeviceStatus.CallbackCancelled);", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(error, probe.Failure)", source, StringComparison.Ordinal);
        Assert.Contains("_receipt?.Complete(status, _completionFailure);", source, StringComparison.Ordinal);
        Assert.Contains("_receipt?.Fail(error);", source, StringComparison.Ordinal);
        Assert.Contains("_self.Free();", source, StringComparison.Ordinal);
        Assert.Contains("_receipt?.Retire();", source, StringComparison.Ordinal);
        Assert.Contains("Volatile.Read(ref _callbacks) != 1 || Volatile.Read(ref _retirements) != 1", source, StringComparison.Ordinal);
        string consumer = Read("tests", "ProGPU.DawnSystemWarp.Conformance", "Program.cs");
        Assert.Contains("VerifySystemWarpRequestCancellationForDiagnostics(deviceRequest: false);", consumer, StringComparison.Ordinal);
        Assert.Contains("VerifySystemWarpRequestCancellationForDiagnostics(deviceRequest: true);", consumer, StringComparison.Ordinal);
        string script = Read("eng", "test-dawn-system-warp-windows.ps1");
        Assert.Contains("@('readback','foreign-resolver','device-loss','request-cancellation')", script, StringComparison.Ordinal);
        Assert.Contains("$arguments += '--request-cancellation'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AvailabilityValidatesOriginalFileAndActualBoundModuleIdentities()
    {
        string source = Read("src", "ProGPU.Backend.Dawn", "DawnSystemWarpArtifact.cs");
        Assert.Contains("if (!OperatingSystem.IsWindows())", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeInformation.ProcessArchitecture", source, StringComparison.Ordinal);
        Assert.Contains("BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) != expectedMachine", source, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData(stream)", source, StringComparison.Ordinal);
        Assert.Contains("GetModulePath(s_provider)", source, StringComparison.Ordinal);
        Assert.Contains("DawnSystemWarpNative.LibraryIdentity(out nint actualCompanion, out nint actualProvider)", source, StringComparison.Ordinal);
        Assert.Contains("actualCompanion != s_companion || actualProvider != s_provider", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeLibrary.Free", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetDllImportResolver", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagingIsExplicitAndContainsNoProviderOrWarpRuntime()
    {
        XDocument project = XDocument.Parse(Read("src", "ProGPU.Backend.Dawn", "ProGPU.Backend.Dawn.csproj"));
        Assert.Contains(project.Descendants("Import"), e => e.Attribute("Project")?.Value == "../../eng/dawn-system-warp/packaging.targets");
        XDocument publication = XDocument.Parse(Read("eng", "dawn-system-warp", "packaging.targets"));
        string[] included = publication.Descendants("None").Select(e => e.Attribute("Include")!.Value).ToArray();
        Assert.Equal(2, included.Length);
        foreach (string paths in included)
        {
            Assert.Equal(3, paths.Split(';').Length);
            Assert.DoesNotContain("webgpu_dawn.dll", paths, StringComparison.Ordinal);
            Assert.DoesNotContain("d3d10warp.dll", paths, StringComparison.Ordinal);
            Assert.DoesNotContain("**", paths, StringComparison.Ordinal);
        }
        string consumer = Read("src", "ProGPU.Backend.Dawn", "buildTransitive", "ProGPU.Backend.Dawn.targets");
        Assert.Contains("<ProGpuEnableDawnSystemWarp Condition=\"'$(ProGpuEnableDawnSystemWarp)' == ''\">false", consumer, StringComparison.Ordinal);
        Assert.Contains("ExcludeFromSingleFile=\"true\"", consumer, StringComparison.Ordinal);
        Assert.Contains("RejectDawnSystemWarpCallerAssetReplacement", consumer, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeCopyLocalItems Remove=", consumer, StringComparison.Ordinal);
    }

    private static int Count(string text, string value) => (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
    private static string Read(params string[] parts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException($"Repository source not found: {Path.Combine(parts)}");
    }
}
