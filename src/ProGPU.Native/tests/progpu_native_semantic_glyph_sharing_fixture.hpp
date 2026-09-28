#pragma once

#include "progpu_native_scene_builder.hpp"

#include <algorithm>
#include <array>
#include <cstdio>
#include <vector>

namespace progpu::native::tests {

// The reference packs all three outlines into one resource. Distinct unused line
// control points prevent exact-byte raster sharing without changing coverage;
// both resource-level and individual-outline sharing must remain independent.
inline bool build_semantic_glyph_sharing_fixture(bool reference,
    unsigned variant, std::uint64_t generation, std::vector<std::byte>& scene) {
    semantic_scene_builder builder(reference ? 0x9472U : 0x9471U, generation);
    const auto fail = [&](const char* stage) {
        std::fprintf(stderr, "Glyph fixture: reference=%u variant=%u stage=%s error=%u\n",
            reference ? 1U : 0U, variant, stage, static_cast<unsigned>(builder.last_error()));
        return false;
    };
    const std::array base_segments{
        progpu_native_path_segment{{0.0F, 0.0F}, {12.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{{12.0F, 0.0F}, {9.0F, 12.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{{9.0F, 12.0F}, {3.0F, 12.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{{3.0F, 12.0F}, {0.0F, 0.0F}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}};
    std::array<progpu_native_scene_glyph_outline, 3U> outlines{};
    std::array<progpu_native_path_segment, 12U> segments{};
    std::array<std::uint32_t, 3U> resources{};
    for (std::uint32_t index = 0U; index < 3U; ++index) {
        outlines[index] = {0U, 4U, 0.0F, 0.0F, 12.0F, 12.0F, 1.0F, 0.25F};
        for (std::size_t curve = 0U; curve < base_segments.size(); ++curve)
            segments[index * 4U + curve] = base_segments[curve];
    }
    if (variant == 2U) outlines[1].subpixel_x = 0.75F;
    if (variant == 3U) outlines[1].raster_scale = 1.25F;
    if (variant == 4U) outlines[1].max_x = 15.0F;
    if (variant == 5U) {
        segments[4U].p1.x = 6.0F;
        segments[5U].p0.x = 6.0F;
    }
    if (reference) {
        for (std::uint32_t index = 0U; index < 3U; ++index)
            segments[index * 4U].p2.x = static_cast<float>(index + (variant == 9U ? 4U : 1U));
    }
    const bool packed = reference || variant == 9U;
    if (packed) {
        for (std::uint32_t index = 0U; index < 3U; ++index)
            outlines[index].segment_offset = index * 4U;
        if (!builder.add_glyph_outlines(outlines, segments, resources[0])) return fail("reference outlines");
        if (!reference && !builder.set_resource_identity(resources[0], 0x9500U,
                generation)) return fail("packed subject identity");
        resources.fill(resources[0]);
    } else {
        for (std::uint32_t index = 0U; index < 3U; ++index) {
            if (!builder.add_glyph_outlines(
                    std::span(&outlines[index], 1U),
                    std::span(segments).subspan(index * 4U, 4U), resources[index]) ||
                !builder.set_resource_identity(resources[index], index + 1U,
                    generation + index)) return fail("subject outlines");
        }
    }
    auto clip = semantic_scene_builder::identity_state();
    clip.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
    clip.clip_rect = {14.0F, 8.0F, 10.0F, 16.0F};
    std::uint32_t clip_resource{};
    if (!builder.add_state(clip, clip_resource)) return fail("clip");
    if (!reference && packed && !builder.set_resource_identity(clip_resource,
            0x9501U, generation)) return fail("packed clip identity");
    constexpr std::array colors{
        progpu_native_color{1.0F, 0.15F, 0.0F, 0.7F},
        progpu_native_color{0.0F, 1.0F, 0.2F, 0.6F},
        progpu_native_color{0.1F, 0.2F, 1.0F, 0.8F}};
    for (std::uint32_t draw = 0U; draw < 4U; ++draw) {
        const auto index = std::min(draw, 2U);
        progpu_native_scene_text_style style{
            colors[index], PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE, 0U, 0U, 0U};
        if (variant == 1U) style.color = colors[(index + 1U) % 3U];
        std::uint32_t style_index{};
        if (!builder.add_text_style(style, style_index)) return fail("style");
        if (!reference && packed && !builder.set_resource_identity(style_index,
                0x9500U + style_index, generation)) return fail("packed style identity");
        progpu_native_positioned_glyph glyph{
            packed ? index : 0U, 0U,
            {8.0F + draw * 5.0F, draw == 3U ? 30.0F : 8.0F + draw * 2.0F},
            {1.0F, 0.0F}, {0.0F, 1.0F}, colors[index], 1.0F, 0.0F, 0.0F, 0.0F};
        if (variant == 1U) glyph.position.x += 2.0F;
        if (variant == 6U && index == 1U) glyph.basis_y.x = 0.25F;
        if (draw == 1U && !builder.save(clip_resource)) return fail("save");
        if (!builder.draw_glyph_run(resources[index], std::span(&glyph, 1U),
                {0.0F, 0.0F, 48.0F, 48.0F}, PROGPU_NATIVE_SCENE_NO_INDEX,
                style_index)) return fail("draw");
        if (draw == 1U && !builder.restore()) return fail("restore");
    }
    return builder.build(scene) || fail("build");
}

// Render must use independent persistent engines for subject/reference and return
// tightly packed RGBA/BGRA pixels after actual GPU completion (not submission).
template<typename Render, typename Require>
void verify_semantic_glyph_sharing(Render render, Require require) {
    std::vector<std::uint8_t> original_pixels;
    for (unsigned variant = 0U; variant < 10U; ++variant) {
        const std::uint64_t generation = variant + 1U;
        const float dpi = variant == 7U ? 1.5F : 1.0F;
        std::vector<std::byte> subject, reference;
        require(build_semantic_glyph_sharing_fixture(false, variant, generation, subject) &&
            build_semantic_glyph_sharing_fixture(true, variant, generation, reference),
            "glyph-sharing fixture construction failed");
        progpu_native_scene_frame_metrics cold{}, warm{}, independent{};
        const auto pixels = render(false, subject, generation, dpi, cold);
        const auto replay = render(false, subject, generation, dpi, warm);
        const auto expected = render(true, reference, generation, dpi, independent);
        require(pixels == expected && replay == expected,
            "glyph sharing changed cold/warm pixels versus independent raster packing");
        bool painted = false;
        for (std::size_t pixel = 0U; pixel + 3U < pixels.size(); pixel += 4U)
            painted |= pixels[pixel] != 0U || pixels[pixel + 1U] != 0U || pixels[pixel + 2U] != 0U;
        require(painted, "glyph-sharing differential compared empty images");
        require(warm.coverage_staging_bytes == 0U && warm.vertex_upload_bytes == 0U &&
            warm.index_upload_bytes == 0U && warm.text_style_upload_bytes == 0U,
            "warm glyph-sharing replay rebuilt retained resources");
        if (variant == 0U || variant == 9U) {
            require(cold.coverage_staging_bytes != 0U &&
                independent.coverage_staging_bytes == cold.coverage_staging_bytes * 3U,
                "identical glyph resources did not share actual raster coverage");
            original_pixels = pixels;
        } else if (variant == 1U) {
            require(cold.coverage_staging_bytes == 0U && cold.vertex_upload_bytes != 0U &&
                pixels != original_pixels,
                "placement/paint changes lost raster reuse or current draw instances");
        } else if (variant >= 2U && variant <= 5U) {
            require(cold.coverage_staging_bytes != 0U,
                "changed glyph raster identity reused stale coverage");
        }
        if (variant == 7U || variant == 8U) require(cold.coverage_staging_bytes != 0U,
            "DPI changes failed to invalidate retained glyph coverage");
        if (variant == 8U) require(pixels == original_pixels,
            "glyph sharing failed to restore original output after mutations/DPI change");
        std::fprintf(stderr, "Glyph sharing: variant=%u dpi=%.1f coverage=%llu reference=%llu exact pixels passed\n",
            variant, dpi, static_cast<unsigned long long>(cold.coverage_staging_bytes),
            static_cast<unsigned long long>(independent.coverage_staging_bytes));
    }
}

} // namespace progpu::native::tests
