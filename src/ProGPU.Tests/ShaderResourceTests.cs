using System.Reflection;
using System.Text.RegularExpressions;
using ProGPU.Backend;
using ProGPU.Browser;
using ProGPU.Compute;
using ProGPU.DirectX;
using ProGPU.Samples;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public class ShaderResourceTests
{
    [Fact]
    public void ShaderResourceCachesDecodedSourceByReference()
    {
        string first = ShaderResource.Load(typeof(Shaders), "Vector.wgsl");
        string second = ShaderResource.Load(typeof(Shaders), "Vector.wgsl");

        Assert.Same(first, second);
        Assert.Same(Shaders.VectorShader, first);
    }

    [Fact]
    public void TextureShaderSupportsBatchedFixedColorLatticeCells()
    {
        Assert.Contains("@location(3) patchKind: f32", Shaders.TextureShader, StringComparison.Ordinal);
        Assert.Contains("@interpolate(flat) patchKind", Shaders.TextureShader, StringComparison.Ordinal);
        Assert.Contains(
            "if (input.patchKind > 0.5 && input.patchKind < 2.5)",
            Shaders.TextureShader,
            StringComparison.Ordinal);
        Assert.Contains("if (input.patchKind > 1.5)", Shaders.TextureShader, StringComparison.Ordinal);
    }

    [Fact]
    public void TextureShaderSupportsBatchedAtlasTransformsAndColorBlending()
    {
        Assert.Contains("@location(5) colorBlendMode: f32", Shaders.TextureShader, StringComparison.Ordinal);
        Assert.Contains("@location(6) patchOpacity: f32", Shaders.TextureShader, StringComparison.Ordinal);
        Assert.Contains("fn blend_atlas_color(", Shaders.TextureShader, StringComparison.Ordinal);
        Assert.Contains("Sprite is the blend source", Shaders.TextureShader, StringComparison.Ordinal);
        Assert.Contains("if (input.patchKind > 2.5)", Shaders.TextureShader, StringComparison.Ordinal);
        Assert.Contains("u32(round(input.colorBlendMode))", Shaders.TextureShader, StringComparison.Ordinal);
    }

    [Fact]
    public void MagnifierShaderPreservesSkiaLensWeightingAndSamplingQuality()
    {
        Assert.Contains(
            "2.0 - length(vec2<f32>(2.0) - edgeInset)",
            ComputeShaders.Magnifier,
            StringComparison.Ordinal);
        Assert.Contains("weight *= weight", ComputeShaders.Magnifier, StringComparison.Ordinal);
        Assert.Contains("let outputBounds = params.outputBounds", ComputeShaders.Magnifier, StringComparison.Ordinal);
        Assert.Contains("fn sample_linear", ComputeShaders.Magnifier, StringComparison.Ordinal);
        Assert.Contains("fn sample_cubic", ComputeShaders.Magnifier, StringComparison.Ordinal);
        Assert.Contains("for (var y = -1; y <= 2", ComputeShaders.Magnifier, StringComparison.Ordinal);
        Assert.Contains("for (var x = -1; x <= 2", ComputeShaders.Magnifier, StringComparison.Ordinal);
    }

    [Fact]
    public void NonlinearColorFilterShaderPreservesSkiaColorDomains()
    {
        Assert.Contains("fn rgb_to_hsl", ComputeShaders.NonlinearColorFilter, StringComparison.Ordinal);
        Assert.Contains("fn hsl_to_rgb", ComputeShaders.NonlinearColorFilter, StringComparison.Ordinal);
        Assert.Contains("srgb_to_linear_component", ComputeShaders.NonlinearColorFilter, StringComparison.Ordinal);
        Assert.Contains("linear_to_srgb_component", ComputeShaders.NonlinearColorFilter, StringComparison.Ordinal);
        Assert.Contains("filtered.rgb * filtered.a", ComputeShaders.NonlinearColorFilter, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenTypeShapingKeepsContextTasksAndClusterSpecificSafetyFlushesInPrivateStorage()
    {
        string source = ShaderResource.Load(typeof(ComputeShaders), "OpenTypeShaping.wgsl");

        Assert.Contains("var<private> lookup_tasks: array<LookupTask, 64>;", source, StringComparison.Ordinal);
        Assert.Contains("var<private> lookup_task_count: u32;", source, StringComparison.Ordinal);
        Assert.Contains("var<private> pending_unsafe_to_break: u32;", source, StringComparison.Ordinal);
        Assert.Contains("fn schedule_unsafe_to_break(start: u32, end: u32)", source, StringComparison.Ordinal);
        Assert.Contains("fn flush_pending_unsafe_to_break()", source, StringComparison.Ordinal);
        Assert.Contains("fn flush_pending_unsafe_to_break_monotone()", source, StringComparison.Ordinal);
        Assert.Contains("fn execute_contextual_substitution_lookup_stage_monotone", source, StringComparison.Ordinal);
        Assert.Contains("fn context_match_value(class_def: u32, glyph_id: u32)", source, StringComparison.Ordinal);
        Assert.Contains("schedule_unsafe_to_break(match_start, match_end);", source, StringComparison.Ordinal);
        Assert.Contains("flush_pending_unsafe_to_break();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ptr<function, array<LookupTask", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VectorShaderMapsSkiaSweepAngleRangesBeforeTiling()
    {
        Assert.Contains(
            "let angleDegrees = angleTurns * 360.0",
            Shaders.VectorShader,
            StringComparison.Ordinal);
        Assert.Contains(
            "brush.gradientStart.y - brush.gradientStart.x",
            Shaders.VectorShader,
            StringComparison.Ordinal);
        Assert.Contains(
            "t = (angleDegrees - brush.gradientStart.x) / angleSpan",
            Shaders.VectorShader,
            StringComparison.Ordinal);
        Assert.Contains(
            "This is O(1) time and O(1) local storage.",
            Shaders.VectorShader,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryShaderSourceIsEmbeddedAndDocumentsItsCostModel()
    {
        DirectoryInfo root = FindRepositoryRoot();
        var projects = new (string Project, Type Anchor)[]
        {
            ("ProGPU.Backend", typeof(Shaders)),
            ("ProGPU.Browser", typeof(BrowserSmokeScene)),
            ("ProGPU.Compute", typeof(ComputeShaders)),
            ("ProGPU.DirectX", typeof(ProGpuDirectXSciChartRenderContext2D)),
            ("ProGPU.Samples", typeof(ShaderToyPlaygroundPageGrid)),
            ("ProGPU.Scene", typeof(Compositor)),
            ("ProGPU.Vector", typeof(GpuHitTestEngine))
        };

        foreach ((string project, Type anchor) in projects)
        {
            string shaderDirectory = Path.Combine(root.FullName, "src", project, "Shaders");
            string[] files = Directory.GetFiles(shaderDirectory)
                .Where(IsShaderFile)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            Assert.NotEmpty(files);

            string assemblyName = anchor.Assembly.GetName().Name!;
            string[] embeddedNames = anchor.Assembly.GetManifestResourceNames();
            foreach (string file in files)
            {
                string fileName = Path.GetFileName(file);
                string logicalName = $"{assemblyName}.Shaders.{fileName}";
                Assert.Contains(logicalName, embeddedNames);

                string source = ShaderResource.Load(anchor, fileName);
                Assert.Contains("// Algorithm:", source, StringComparison.Ordinal);
                Assert.Contains("// Time complexity:", source, StringComparison.Ordinal);
                Assert.Contains("// Space complexity:", source, StringComparison.Ordinal);
                Assert.Equal(
                    File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal),
                    source.Replace("\r\n", "\n", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void ProductionStageModulesAreNotEmbeddedInCSharpLiterals()
    {
        DirectoryInfo root = FindRepositoryRoot();
        string sourceRoot = Path.Combine(root.FullName, "src");

        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(root.FullName, file).Replace('\\', '/');
            if (relativePath.Contains("/ProGPU.Tests/", StringComparison.Ordinal) ||
                relativePath.Contains("/ProGPU.Tests.Headless/", StringComparison.Ordinal) ||
                relativePath.Contains("/obj/", StringComparison.Ordinal) ||
                relativePath.Contains("/bin/", StringComparison.Ordinal) ||
                relativePath.EndsWith("/ProGpuDirectXHlslTranslator.cs", StringComparison.Ordinal))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            Assert.DoesNotContain("@vertex", source, StringComparison.Ordinal);
            Assert.DoesNotContain("@fragment", source, StringComparison.Ordinal);
            Assert.DoesNotContain("@compute", source, StringComparison.Ordinal);

            MatchCollection literals = Regex.Matches(
                source,
                "(?s)@\"(?<verbatim>(?:\"\"|[^\"])*)\"|\"\"\"(?<raw>.*?)\"\"\"");
            foreach (Match literal in literals)
            {
                string body = literal.Groups["verbatim"].Success
                    ? literal.Groups["verbatim"].Value
                    : literal.Groups["raw"].Value;
                bool looksLikeShader =
                    body.Contains("@group(", StringComparison.Ordinal) ||
                    body.Contains("void mainImage", StringComparison.Ordinal) ||
                    (body.Contains("fn ", StringComparison.Ordinal) &&
                     (body.Contains("vec2<", StringComparison.Ordinal) ||
                      body.Contains("vec3<", StringComparison.Ordinal) ||
                      body.Contains("vec4<", StringComparison.Ordinal) ||
                      body.Contains("texture_", StringComparison.Ordinal)));
                Assert.False(
                    looksLikeShader,
                    $"Production shader source must be an embedded resource, not a C# literal: {relativePath}");
            }
        }
    }

    [Fact]
    public void DirectXSciChartResourcesTrackNativeBindingMap()
    {
        Assembly assembly = typeof(ProGpuDirectXSciChartRenderContext2D).Assembly;
        Type bindingMap = assembly.GetType("ProGPU.DirectX.ProGpuDirectXNativeBindingMap", throwOnError: true)!;
        string texture = ShaderResource.Load(typeof(ProGpuDirectXSciChartRenderContext2D), "TexturePixel.wgsl");
        string heatmap = ShaderResource.Load(typeof(ProGpuDirectXSciChartRenderContext2D), "ShapedHeatmapPixel.wgsl");

        uint constantBuffer0 = InvokeBinding(bindingMap, "GetConstantBufferBinding", DxShaderStage.Pixel, 0);
        uint shaderResource0 = InvokeBinding(bindingMap, "GetShaderResourceBinding", DxShaderStage.Pixel, 0);
        uint shaderResource1 = InvokeBinding(bindingMap, "GetShaderResourceBinding", DxShaderStage.Pixel, 1);
        uint sampler0 = InvokeBinding(bindingMap, "GetSamplerBinding", DxShaderStage.Pixel, 0);

        Assert.Contains($"@binding({shaderResource0})", texture, StringComparison.Ordinal);
        Assert.Contains($"@binding({sampler0})", texture, StringComparison.Ordinal);
        Assert.Contains($"@binding({constantBuffer0})", heatmap, StringComparison.Ordinal);
        Assert.Contains($"@binding({shaderResource0})", heatmap, StringComparison.Ordinal);
        Assert.Contains($"@binding({shaderResource1})", heatmap, StringComparison.Ordinal);
        Assert.Contains($"@binding({sampler0})", heatmap, StringComparison.Ordinal);
    }

    private static uint InvokeBinding(Type bindingMap, string methodName, DxShaderStage stage, uint slot)
    {
        MethodInfo method = bindingMap.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(bindingMap.FullName, methodName);
        return (uint)method.Invoke(null, new object[] { stage, slot })!;
    }

    private static bool IsShaderFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension is ".wgsl" or ".glsl" or ".hlsl";
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src", "ProGPU.Backend")))
            {
                return directory;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the ProGPU repository root.");
    }
}
