using Xunit;

namespace ProGPU.Tests;

// Device-free wiring checks only; full selected-provider pixel controls remain
// authoritative for render-pass and retained-bundle behavior.
public sealed class HintedGlyphPaintBindingTests
{
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
        Assert.Contains("Commands::draw(encoder, 6U, draw.instance_count, 0U, draw.first_instance);", draw);
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
