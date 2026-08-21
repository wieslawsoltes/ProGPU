#pragma once

#include "progpu_native.h"
#include "progpu_native_scene_builder.hpp"
#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <span>
#include <vector>

namespace progpu::native::samples {

struct text_shaping_showcase_metrics final {
    std::uint32_t preset_index = 0U;
    std::uint32_t shaped_glyph_count = 0U;
    std::uint32_t visible_glyph_count = 0U;
    std::uint32_t unique_outline_count = 0U;
    std::uint32_t feature_off_glyph_count = 0U;
    std::uint32_t feature_on_glyph_count = 0U;
    float feature_off_advance = 0.0F;
    float feature_on_advance = 0.0F;
    std::uint32_t command_count = 0U;
    std::uint32_t resource_count = 0U;
    std::uint64_t stream_bytes = 0U;
    std::uint64_t generation = 0U;
};

/*
 * Pure C++20 port of the live specimen and feature-comparison portions of the
 * ProGPU-owned managed sample:
 * src/ProGPU.Samples/Pages/TextShapingShowcasePage.cs.
 *
 * The port preserves the managed sample's exact preset text, OpenType feature
 * ranges, script/language/direction metadata, one value-only shaping result,
 * and retained DrawGlyphRun boundary. Browser UI chrome remains DOM-owned;
 * every specimen pixel is shaped, outlined, rasterized, and composited by the
 * native ProGPU text and WebGPU stacks.
 *
 * A changed preset or physical DPI performs O(S + G + P) CPU work for S input
 * scalars, G shaped glyphs, and P decoded outline segments, then serializes one
 * immutable scene. Stable replay performs no shaping, outline decode, scene
 * serialization, or sample-side allocation.
 */
class text_shaping_showcase_scene final {
public:
    text_shaping_showcase_scene();

    bool load_font(std::span<const std::byte> font_bytes) noexcept;
    bool resize(float width, float height, float dpi_scale) noexcept;
    bool set_preset(std::uint32_t preset_index) noexcept;
    void invalidate() noexcept;

    bool compile(
        std::vector<std::byte>& stream,
        text_shaping_showcase_metrics& metrics) noexcept;

    bool ready() const noexcept;
    bool dirty() const noexcept;
    std::uint64_t generation() const noexcept;
    std::uint32_t preset_index() const noexcept;
    static std::uint32_t preset_count() noexcept;

private:
    void mark_dirty() noexcept;

    static constexpr std::uint64_t scene_id_ = 0x5445585453484150ULL;
    semantic_scene_builder builder_{scene_id_, 1U};
    std::vector<std::byte> font_bytes_{};
    text::sfnt_font_view font_{};
    std::uint64_t generation_ = 1U;
    std::uint32_t preset_index_ = 0U;
    float width_ = 960.0F;
    float height_ = 540.0F;
    float dpi_scale_ = 1.0F;
    bool ready_ = false;
    bool dirty_ = true;
};

} // namespace progpu::native::samples
