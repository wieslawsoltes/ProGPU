namespace ProGPU.Backend;

public static class Shaders
{
    public static readonly string SharedWgpuMathCode = ShaderResource.Load(typeof(Shaders), "SharedWgpuMath.wgsl");

    public static readonly string VectorShader = string.Concat(
        ShaderResource.Load(typeof(Shaders), "PathAtlasSampling.wgsl"),
        "\n",
        ShaderResource.Load(typeof(Shaders), "RegisteredMaterialCommon.wgsl"),
        "\n",
        ShaderResource.Load(typeof(Shaders), "Vector.wgsl"));

    public static readonly string TextShader = string.Concat(
        ShaderResource.Load(typeof(Shaders), "TextGlyphGeometryCommon.wgsl"), "\n",
        ShaderResource.Load(typeof(Shaders), "TextMaskCommon.wgsl"), "\n",
        ShaderResource.Load(typeof(Shaders), "TextGlyphCoverageCommon.wgsl"), "\n",
        ShaderResource.Load(typeof(Shaders), "Text.wgsl"));

    public static readonly string TextureShader = string.Concat(
        ShaderResource.Load(typeof(Shaders), "SampledMaskCommon.wgsl"),
        "\n",
        ShaderResource.Load(typeof(Shaders), "TextureImageSamplingCommon.wgsl"),
        "\n",
        ShaderResource.Load(typeof(Shaders), "Texture.wgsl"));

    public static readonly string HintedGlyphPaintShader = string.Concat(
        ShaderResource.Load(typeof(Shaders), "RegisteredMaterialCommon.wgsl"), "\n",
        ShaderResource.Load(typeof(Shaders), "TextGlyphGeometryCommon.wgsl"), "\n",
        ShaderResource.Load(typeof(Shaders), "TextMaskCommon.wgsl"), "\n",
        ShaderResource.Load(typeof(Shaders), "TextGlyphCoverageCommon.wgsl"), "\n",
        ShaderResource.Load(typeof(Shaders), "TextureImageSamplingCommon.wgsl"), "\n",
        ShaderResource.Load(typeof(Shaders), "HintedGlyphPaint.wgsl"));

    public static readonly string GlyphRasterizerShader = ShaderResource.Load(typeof(Shaders), "GlyphRasterizer.wgsl");

    public static readonly string PathRasterizerShader = string.Concat(
        ShaderResource.Load(typeof(Shaders), "PathRasterizerCommon.wgsl"),
        "\n",
        ShaderResource.Load(typeof(Shaders), "PathRasterizer.wgsl"));

    public static readonly string ChartLineShader = ShaderResource.Load(typeof(Shaders), "ChartLine.wgsl");

    public static readonly string ChartScatterShader = ShaderResource.Load(typeof(Shaders), "ChartScatter.wgsl");

    public static readonly string PathOpGeometryShader = ShaderResource.Load(typeof(Shaders), "PathOpGeometry.wgsl");

    public static readonly string PathOpRecordFinalizerShader = ShaderResource.Load(typeof(Shaders), "PathOpRecordFinalizer.wgsl");

    public static readonly string AdvancedBlendShader = ShaderResource.Load(typeof(Shaders), "AdvancedBlend.wgsl");

}
