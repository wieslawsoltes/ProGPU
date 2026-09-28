using System.Globalization;
using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

[CollectionDefinition("Pipeline diagnostics environment", DisableParallelization = true)]
public sealed class PipelineDiagnosticsEnvironmentCollection;

[Collection("Pipeline diagnostics environment")]
public sealed class PipelineCreationDiagnosticsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("TrUe", true)]
    public unsafe void OnlyActualPipelineCreationIsTimed(string? enabled, bool expectDiagnostics)
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var first = new RenderPipelineCache(context);
        using var second = new RenderPipelineCache(context);
        const string source = """
            @vertex fn vs_main() -> @builtin(position) vec4<f32> {
                return vec4<f32>(0.0, 0.0, 0.0, 1.0);
            }
            @fragment fn fs_main() -> @location(0) vec4<f32> {
                return vec4<f32>(1.0, 0.0, 0.0, 1.0);
            }
            @compute @workgroup_size(1) fn cs_main() {}
            """;
        var shader = first.GetOrCreateShader("DiagnosticShader", source);
        var secondShader = second.GetOrCreateShader("DiagnosticShader", source);
        Assert.True(shader == secondShader);
        string? previous = Environment.GetEnvironmentVariable("PROGPU_BACKEND_DIAGNOSTICS");
        TextWriter originalOutput = Console.Out;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Environment.SetEnvironmentVariable("PROGPU_BACKEND_DIAGNOSTICS", enabled);
            Console.SetOut(output);
            var render = first.GetOrCreateRenderPipeline("DiagnosticRender", shader);
            var compute = first.GetOrCreateComputePipeline("DiagnosticCompute", shader, "cs_main");
            Assert.True(render != null && compute != null);
            // Neither a local cache hit nor an acquired device-domain lease
            // starts another timer or claims another pipeline was compiled.
            Assert.True(render == first.GetOrCreateRenderPipeline("DiagnosticRender", shader));
            Assert.True(compute == first.GetOrCreateComputePipeline("DiagnosticCompute", shader, "cs_main"));
            Assert.True(render == second.GetOrCreateRenderPipeline("DiagnosticRender", secondShader));
            Assert.True(compute == second.GetOrCreateComputePipeline("DiagnosticCompute", secondShader, "cs_main"));
        }
        finally
        {
            Console.SetOut(originalOutput);
            Environment.SetEnvironmentVariable("PROGPU_BACKEND_DIAGNOSTICS", previous);
        }
        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (!expectDiagnostics)
        {
            Assert.Empty(lines);
            return;
        }
        Assert.Equal(4, lines.Length);
        Assert.Equal("[PIPELINE] begin kind=render; key=DiagnosticRender; entry=vs_main; fragment=fs_main", lines[0].TrimEnd());
        Assert.Equal("[PIPELINE] begin kind=compute; key=DiagnosticCompute; entry=cs_main; fragment=", lines[2].TrimEnd());
        foreach (string line in new[] { lines[1], lines[3] })
        {
            Assert.Contains("returned=True; hasHandle=True; wallMs=", line, StringComparison.Ordinal);
            Assert.True(double.Parse(line.Split("wallMs=", StringSplitOptions.None)[1], CultureInfo.InvariantCulture) >= 0);
        }
        Assert.Equal(1, context.CachedDeviceRenderPipelineCount);
        Assert.Equal(1, context.CachedDeviceComputePipelineCount);
    }
}
