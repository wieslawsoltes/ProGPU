namespace ProGPU.Backend;

public static class Shaders
{
    public static readonly string SharedWgpuMathCode = ShaderResource.Load(typeof(Shaders), "SharedWgpuMath.wgsl");

    public static readonly string VectorShader = ShaderResource.Load(typeof(Shaders), "Vector.wgsl");

    public static readonly string TextShader = ShaderResource.Load(typeof(Shaders), "Text.wgsl");

    public static readonly string TextureShader = ShaderResource.Load(typeof(Shaders), "Texture.wgsl");

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
