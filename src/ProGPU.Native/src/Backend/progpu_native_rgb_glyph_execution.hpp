#pragma once

#include "progpu_native_gpu_records.hpp"
#include <span>

// Include after the provider-selected WebGPU C header. The same implementation
// translation unit is compiled against both supported native header ABIs.
struct progpu_native_engine;

namespace progpu::native::execution {

// Internal explicit model, not the public DirectWrite rendering-mode enum.
// The source adapter must retain its original parameters separately and cannot
// select this model as a substitute for an unresolved hinted/default mode.
enum class rgb_glyph_filter_model : std::uint32_t { full_pixel_box_8x8 = 1U };
struct rgb_glyph_policy final {
    float gamma;
    float enhanced_contrast;
    float cleartype_level;
    std::uint32_t pixel_geometry;
    rgb_glyph_filter_model filter_model;
};
struct rgb_glyph_tile final {
    gpu_glyph_record outline;
    float x_start;
    float y_start;
    float scale;
    float subpixel_x;
    std::uint32_t width;
    std::uint32_t height;
    std::int32_t target_x;
    std::int32_t target_y;
    progpu_native_color foreground;
};
struct rgb_glyph_scissor final {
    std::uint32_t x, y, width, height;
};
struct rgb_glyph_metrics final {
    std::uint64_t vertex_upload_bytes = 0U;
    std::uint64_t uniform_upload_bytes = 0U;
    std::uint32_t draw_calls = 0U;
};

// Encode into the current owned semantic encoder, borrowing its live target.
// Target opacity and physical integral placement must already be source-proven.
// The caller holds the engine lock and supplies its current single-sample target
// in engine.target_format with these exact dimensions. This is not a foreign
// texture-view ABI. No source clip/transform may be silently discarded at call-in.
// Reject CPU raster preferences; no local submit, wait, fallback or readback.
// Successful encoding is not a completion or public source-admission claim.
progpu_native_status encode_linear_rgb_glyphs(
    progpu_native_engine& engine, WGPUTextureView target,
    std::uint32_t target_width, std::uint32_t target_height,
    bool target_ignores_alpha, const rgb_glyph_policy& policy,
    const rgb_glyph_scissor& scissor,
    std::span<const rgb_glyph_tile> glyphs,
    std::span<const progpu_native_path_segment> segments,
    rgb_glyph_metrics& metrics);

} // namespace progpu::native::execution
