using ProGPU.Backend;

namespace ProGPU.Scene;

internal static class RetainedGlyphShaders
{
    public static readonly string Source =
        string.Concat(ShaderResource.Load(typeof(Shaders), "GlyphCoverageTransfer.wgsl"), "\n",
            ShaderResource.Load(typeof(RetainedGlyphShaders), "RetainedGlyph.wgsl"));
}
