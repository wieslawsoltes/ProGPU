using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ProGPU.Backend;
using ProGPU.Tests;
using Xunit.Abstractions;

if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64 ||
    args.Length != 1 || args[0].Length != 64)
    throw new InvalidOperationException("This diagnostic requires Windows ARM64 and one exact shader identity.");
using (Stream shader = typeof(WgpuContext).Assembly.GetManifestResourceStream("ProGPU.Backend.Shaders.BinaryGlyphRasterizer.wgsl")!)
{
    string hash = Convert.ToHexString(SHA256.HashData(shader)).ToLowerInvariant();
    if (hash != args[0]) throw new InvalidOperationException("Unexpected embedded canonical shader.");
    Console.WriteLine($"shader-identity={hash} runtime={Environment.Version} architecture={RuntimeInformation.ProcessArchitecture}");
}
WgpuContext.OnWebGpuError += (kind, text) => Console.Error.WriteLine($"WebGPU {kind}: {text}");
int completed = 0;
void Run(string resource, string digest, int cases, int noDropout)
{
    Console.WriteLine($"fixture-start {resource} cases={cases}");
    new BinaryGlyphOriginalCoverageTests(new Output()).EveryOriginalAuthoredBitmapMatchesColdAndWarmCoverage(resource, digest, cases, noDropout);
    Console.WriteLine($"fixture-passed {resource} cases={cases}");
    completed++;
}
Run("BinaryGlyphOriginals.json", "6D13E632B5F260217BC17D9C86B61E77506F463644EEEE923280400A52F3B092", 880, 176);
Run("BinaryGlyphCurveOriginals.json", "B30470FEAE7D095ADAD5E549595ADEAED929058E3040D94640F0CA214313E2B8", 880, 176);
Run("BinaryGlyphDirectionOriginals.json", "AEBB5E4955A0003808F639601188C9151315ECC98EAD3929F366513CD2A643FD", 7, 1);
Run("BinaryGlyphAllDirectionsOriginals.json", "2B9AB3FABED7D7437CD181A9B93648725D9189214CE76F4EB79809E8068ADBBB", 440, 88);
Run("BinaryGlyphCapsOriginals.json", "AF48FDDCB0277D338E9301A01584E0CD21CF6A71F3B75D7599B54926F0286D3C", 110, 22);
Run("BinaryGlyphCapsReversedOriginals.json", "6761D767449C974DCC046BA9B2D5F7D32198FEB7D92E1E44EF986F37130808BF", 110, 22);
Run("BinaryGlyphConnectedCapsOriginals.json", "6A627AB861F3079202F8210AA7AA2781DB3FD0062134BC5761C1C54E2964C604", 55, 11);
Run("BinaryGlyphConnectedCapsReversedOriginals.json", "F3E8EFD17E39D90B5B8B90D4458AFC90A47023E61DB820F99101E1991D1C231A", 55, 11);
if (completed != 8) throw new InvalidOperationException("Incomplete original fixture inventory.");
Console.WriteLine("completed=True fixtures=8 cases=2537 originalNativePackageQualified=False");

sealed class Output : ITestOutputHelper
{
    public void WriteLine(string message) => Console.WriteLine(message);
    public void WriteLine(string format, params object[] args) => Console.WriteLine(format, args);
}

static class ProbeGuard
{
    internal static void Verify(WgpuContext context)
    {
        WindowsOwnedRuntimeValidation.Verify(context);
        var adapter = context.AdapterSelectionDiagnostics;
        if (context.AdapterBackendType != Silk.NET.WebGPU.BackendType.D3D12 ||
            context.SelectedDx12ShaderCompiler != WgpuDx12ShaderCompiler.Dxc || !context.ForceFallbackAdapter ||
            adapter.SelectionReason != WgpuAdapterSelectionReason.RequiredFallback || adapter.VendorId != 0x1414)
            throw new InvalidOperationException("The diagnostic did not select the original explicit DXC/WARP route.");
        using var process = Process.GetCurrentProcess();
        var warp = process.Modules.Cast<ProcessModule>().Single(m => string.Equals(m.ModuleName, "d3d10warp.dll", StringComparison.OrdinalIgnoreCase));
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "d3d10warp.dll");
        if (!string.Equals(Path.GetFullPath(warp.FileName), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("WARP is not the original system module.");
        using var file = File.OpenRead(warp.FileName);
        Console.WriteLine($"original-warp path={warp.FileName}; sha256={Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant()}; adapter={adapter.Name}");
    }
}
