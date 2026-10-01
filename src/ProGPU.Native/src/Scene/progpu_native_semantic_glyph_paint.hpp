#pragma once

#include "progpu_native_glyph_paint_alpha.hpp"
#include <cstddef>
#include <cstdint>

struct progpu_native_engine;

namespace progpu::native::semantic {

bool ensure_glyph_paint_pipeline(progpu_native_engine& engine,
    bool masked, bool chained, bool premultiplied_output);
bool prepare_glyph_paint_pipelines(progpu_native_engine& engine,
    bool masked, bool chained);
WGPURenderPipeline select_glyph_paint_pipeline(progpu_native_engine& engine,
    bool masked, bool chained, bool premultiplied_output) noexcept;
bool admit_glyph_paint_storage(progpu_native_engine& engine,
    std::uint64_t paint_bytes);
bool prepare_glyph_paints(progpu_native_engine& engine,
    std::uint64_t identity, std::uint64_t& upload_bytes);
WGPUBindGroup glyph_paint_uniform_binding(progpu_native_engine& engine,
    std::uint32_t target_layer);

} // namespace progpu::native::semantic
