using Xunit;

namespace ProGPU.Tests;

public sealed class ShaderIdentifierCompatibilityTests
{
    [Theory]
    [InlineData("ProGPU.Backend/Shaders/GlyphRasterizer.wgsl")]
    [InlineData("ProGPU.Backend/Shaders/PathRasterizer.wgsl")]
    [InlineData("ProGPU.Backend/Shaders/PathSignedWindingCoverage.wgsl")]
    [InlineData("ProGPU.Compute/Shaders/ShadowBlurVertical.wgsl")]
    [InlineData("ProGPU.Native/Shaders/HitTestReadback.wgsl")]
    public void SharedPackingShadersAvoidGlslPackedIdentifier(string relativePath)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Packages.props")))
            root = root.Parent;
        Assert.NotNull(root);
        string source = File.ReadAllText(Path.Combine(root.FullName, "src", relativePath));
        // This identifier is valid WGSL, but the pinned wgpu-native GLSL writer
        // can preserve it unchanged. Mesa then rejects it as PACKED_TOK.
        Assert.DoesNotMatch(@"\b(?:var|let|const)\s*(?:<[^>]+>\s*)?\s+packed\b", source);
    }

    [Theory]
    [InlineData("var packed = 0u;")]
    [InlineData("let packed = textureLoad(input, position, 0);")]
    [InlineData("var packed: vec4<u32>;")]
    [InlineData("var<private> packed: u32;")]
    public void RegressionPatternCoversAllOriginalPackingDeclarations(string declaration)
        => Assert.Matches(@"\b(?:var|let|const)\s*(?:<[^>]+>\s*)?\s+packed\b", declaration);
}
