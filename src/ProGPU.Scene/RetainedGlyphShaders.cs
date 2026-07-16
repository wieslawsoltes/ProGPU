using ProGPU.Backend;

namespace ProGPU.Scene;

internal static class RetainedGlyphShaders
{
    public static readonly string Source =
        ShaderResource.Load(typeof(RetainedGlyphShaders), "RetainedGlyph.wgsl");
}
