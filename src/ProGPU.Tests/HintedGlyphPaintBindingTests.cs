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
