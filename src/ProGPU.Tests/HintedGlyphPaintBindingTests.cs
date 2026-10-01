using Xunit;

namespace ProGPU.Tests;

// Device-free wiring checks only; full selected-provider pixel controls remain
// authoritative for render-pass and retained-bundle behavior.
public sealed class HintedGlyphPaintBindingTests
{
    [Fact]
    public void BoundedTextureCoverageUsesTheRetainedGlyphFrameAtTheFragment()
    {
        string shader = ProGPU.Backend.Shaders.HintedGlyphPaintShader;
        Assert.Contains("@location(6) @interpolate(flat) glyphLogicalFrame: vec4<f32>", shader);
        Assert.Contains("output.glyphLogicalFrame = vec4<f32>(minimum, safeExtent);", shader);
        Assert.Contains("select(world - paint.sourceOffsetOpacity.xy, world, paint.kind == 1u)", shader);
        Assert.Contains("let glyphLocal = input.sourceLogical - input.glyphLogicalFrame.xy;", shader);
        Assert.Contains("glyphFrameUV = glyphLocal / input.glyphLogicalFrame.zw;", shader);
        Assert.Contains("texCoord = atlasMinimum + glyphLocal * (atlasSpan / input.glyphLogicalFrame.zw);", shader);
        Assert.Contains("text_glyph_color_with_mask_alpha(vec4<f32>(1.0), texCoord,", shader);
        Assert.DoesNotContain("frameUV = (world - minimum)", shader);
        Assert.DoesNotContain("mix(input.texCoords.xy, input.texCoords.zw, frameUV)", shader);
        // Original bounded image geometry/sampling and outside-glyph clipping
        // remain independent of the stabilized coverage-coordinate transport.
        Assert.Contains("world = paint.textureQuad01.zw", shader);
        Assert.Contains("world = paint.textureQuad23.zw", shader);
        Assert.Contains("paintUV = mix(paint.uvBounds.xy, paint.uvBounds.zw, cornerUV);", shader);
        Assert.Contains("(boundedTexture && outsideGlyph)", shader);
        Assert.Contains("sample_image(sampleInput, addressedUV, modes, paintDx, paintDy)", shader);
        Assert.DoesNotContain("textureLoad(atlasTexture", shader);
    }

    [Fact]
    public void NativeUnmaskedPaintOwnsAndBindsItsEmptyIntermediateSlot()
    {
        string pipeline = Read("ProGPU.Native/src/Scene/progpu_native_semantic_glyph_paint.cpp");
        Assert.Contains("descriptor.entryCount = 0U; descriptor.entries = nullptr;", pipeline);
        Assert.Contains("empty_group_descriptor.layout = empty_mask;", pipeline);
        Assert.Contains("engine.glyph_paint_empty_mask_layout = empty_mask;", pipeline);
        Assert.Contains("engine.glyph_paint_empty_mask_bind_group = empty_group;", pipeline);
        Assert.Contains("masked ? engine.layer_mask_layout : engine.glyph_paint_empty_mask_layout", pipeline);
        string draw = Read("ProGPU.Native/src/Scene/progpu_native_semantic_draw_execution.cpp");
        Assert.Contains("Commands::set_bind_group(encoder, 2U,\n            chained ? mask_chain_bind_group : masked ? mask_bind_group :\n                engine.glyph_paint_empty_mask_bind_group);", draw);
        string owner = Read("ProGPU.Native/src/Backend/progpu_native_engine.hpp");
        Assert.Contains("wgpuBindGroupRelease(glyph_paint_empty_mask_bind_group)", owner);
        Assert.Contains("wgpuBindGroupLayoutRelease(glyph_paint_empty_mask_layout)", owner);
    }

    [Fact]
    public void ManagedPaintAlreadyBindsItsExistingMaskSlotUnconditionally()
    {
        string source = Read("ProGPU.Scene/Compositor.HintedGlyphPaint.cs");
        Assert.Contains("layouts[2] = _maskBindGroupLayout;", source);
        Assert.Contains("RenderPassEncoderSetBindGroup(pass, 2, mask, 0, null);", source);
    }

    [Fact]
    public void AffinePaintRetainsBothCanonicalTrianglesAndTheirSeparateContributions()
    {
        string shader = ProGPU.Backend.Shaders.HintedGlyphPaintShader;
        Assert.Contains("glyph_instance(input, 1u)", shader);
        Assert.Contains("glyph_instance(input, 5u)", shader);
        Assert.Contains("corner1.y == minimum.y && corner3.x == minimum.x", shader);
        Assert.Contains("corner1.x == maximum.x && corner3.y == maximum.y", shader);
        Assert.Contains("hinted_glyph_triangle(corner1 - minimum, maximum - minimum)", shader);
        Assert.Contains("hinted_glyph_triangle(maximum - minimum, corner3 - minimum)", shader);
        Assert.Contains("determinant != 0.0", shader);
        Assert.Contains("determinant > 0.0", shader);
        Assert.Contains("input.vertexIndex % 6u", shader);
        Assert.Contains("input.vertexIndex >= 6u", shader);
        Assert.Contains("secondTriangle && (!boundedTexture || axisFrame)", shader);
        Assert.Contains("var liveFrame = true;", shader);
        Assert.DoesNotContain("var liveFrame = all(extent >", shader);
        Assert.DoesNotContain("!inFirst && !inSecond", shader);
        Assert.Contains("-hinted_glyph_cross(edge2, local)", shader);
        Assert.Contains("edge.y < 0.0 || (edge.y == 0.0 && edge.x > 0.0)", shader);
        string paintFunction = shader[shader.IndexOf("fn hinted_glyph_paint_color(", StringComparison.Ordinal)..];
        int coverage = paintFunction.IndexOf("let coverage = text_glyph_color_with_mask_alpha", StringComparison.Ordinal);
        Assert.True(coverage >= 0 && coverage < paintFunction.IndexOf("        discard;", StringComparison.Ordinal));
    }

    [Fact]
    public void BothRenderersEmitTwoImageQuadsOnlyForBoundedTexturePaint()
    {
        string draw = Read("ProGPU.Native/src/Scene/progpu_native_semantic_draw_execution.cpp");
        Assert.Contains("paint.kind == PROGPU_NATIVE_SCENE_GLYPH_PAINT_TEXTURE &&", draw);
        Assert.Contains("(paint.flags & PROGPU_NATIVE_SCENE_GLYPH_PAINT_BOUNDED) != 0U ? 12U : 6U", draw);
        Assert.Contains("Commands::draw(encoder, vertex_count, draw.instance_count, 0U, draw.first_instance);", draw);
        string managed = Read("ProGPU.Scene/Compositor.HintedGlyphPaint.cs");
        Assert.Contains("BitConverter.SingleToUInt32Bits(_textVerticesList[checked((int)drawCall.IndexStart)].Padding)", managed);
        Assert.Contains("_hintedGlyphPaints![checked((int)paintIndex)].VertexCount", managed);
        Assert.Contains("RenderPassEncoderDraw(pass, vertexCount, drawCall.IndexCount, 0, 0)", managed);
        string record = Read("ProGPU.Scene/GpuHintedGlyphPaint.cs");
        Assert.Contains("Kind == TextureMaterial && (Flags & BoundedTexture) != 0 ? 12u : 6u", record);
    }

    [Fact]
    public void NativePaintPreparationAndDrawUseTheSameOriginalAlphaPolicy()
    {
        string policy = Read("ProGPU.Native/src/Scene/progpu_native_glyph_paint_alpha.hpp");
        Assert.Contains("paint.kind == PROGPU_NATIVE_SCENE_GLYPH_PAINT_TEXTURE", policy);
        Assert.Contains("paint.flags & PROGPU_NATIVE_SCENE_GLYPH_PAINT_PREMULTIPLIED", policy);
        string pipeline = Read("ProGPU.Native/src/Scene/progpu_native_semantic_glyph_paint.cpp");
        Assert.Contains("glyph_paint_premultiplied_output(paint, alpha_mask_target)", pipeline);
        Assert.Contains("blend.color.srcFactor = alpha_mask_target || premultiplied_output", pipeline);
        Assert.Contains("? WGPUBlendFactor_One : WGPUBlendFactor_SrcAlpha;", pipeline);
        Assert.Contains("blend.alpha.srcFactor = WGPUBlendFactor_One;", pipeline);
        Assert.Contains("\"fs_main_mask_chain\" : masked ? \"fs_main\" : \"fs_main_unmasked\"", pipeline);
        Assert.Contains("\"fs_main_mask_chain_premultiplied\"", pipeline);
        Assert.Contains("\"fs_mask_chain\" : masked ? \"fs_mask\" : \"fs_mask_unmasked\"", pipeline);
        string draw = Read("ProGPU.Native/src/Scene/progpu_native_semantic_draw_execution.cpp");
        Assert.Contains("semantic::glyph_paint_premultiplied_output(\n            engine.semantic_glyph_cache.paints[draw.paint_index]", draw);
        Assert.Contains("engine, masked, chained, premultiplied_output", draw);
        Assert.Contains("Commands::draw(encoder, vertex_count, draw.instance_count, 0U, draw.first_instance);", draw);
    }

    [Fact]
    public void NativePaintOwnsEachLazyStraightAndPremultipliedVariant()
    {
        string owner = Read("ProGPU.Native/src/Backend/progpu_native_engine.hpp");
        string pipeline = Read("ProGPU.Native/src/Scene/progpu_native_semantic_glyph_paint.cpp");
        foreach (string field in new[]
        {
            "glyph_paint_pipeline", "glyph_paint_masked_pipeline", "glyph_paint_chain_pipeline",
            "glyph_paint_straight_pipeline", "glyph_paint_straight_masked_pipeline", "glyph_paint_straight_chain_pipeline"
        })
        {
            Assert.Contains($"WGPURenderPipeline {field} = nullptr;", owner);
            Assert.Contains($"wgpuRenderPipelineRelease({field});", owner);
            Assert.Contains($"engine.{field}", pipeline);
        }
        Assert.Contains("if (pipeline != nullptr) return true;", pipeline);
        Assert.Contains("for (const auto& paint : engine.semantic_glyph_cache.paints)", pipeline);
    }

    private static string Read(string relative)
    {
        for (DirectoryInfo? root = new(AppContext.BaseDirectory); root != null; root = root.Parent)
        {
            string path = Path.Combine(root.FullName, "src", relative);
            if (File.Exists(path)) return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        throw new FileNotFoundException(relative);
    }
}
