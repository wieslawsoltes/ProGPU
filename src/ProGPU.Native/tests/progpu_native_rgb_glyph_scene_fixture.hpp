#pragma once

#include "progpu_native_scene_builder.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <span>
#include <vector>

namespace progpu::native::tests {

// This is the explicitly selected full-pixel box model, not a DirectWrite
// rendering-mode oracle. The reference counts original rectangle membership at
// 64 independent sample points per stripe; it never samples a grayscale atlas,
// reads the compiled scene, or calls a production coverage/placement helper.
struct rgb_scene_case final {
    unsigned variant{};
    float dpi{1.0F};
    float opacity{1.0F};
    float foreground_alpha{1.0F};
    float phase{0.25F};
    float right{4.21875F};
    std::uint32_t geometry{1U};
    std::int32_t translation{};
    bool clip{};
    bool reversed{};
    bool mapped{};
    bool accepted{true};
    progpu_native_scene_presentation presentation{};
};

inline rgb_scene_case rgb_scene_case_for(unsigned variant) {
    rgb_scene_case test{};
    test.variant = variant;
    if (variant == 0U) test.geometry = 0U;
    if (variant == 2U) test.geometry = 2U;
    if (variant == 3U) test.phase = 0.75F;
    if (variant == 4U) test.opacity = 0.61F;
    if (variant == 5U) test.foreground_alpha = 0.37F;
    if (variant == 6U) test.reversed = true;
    if (variant == 7U) test.clip = true;
    if (variant == 8U) test.translation = 4;
    if (variant == 9U) test.dpi = 1.25F;
    if (variant == 10U) test.dpi = 1.5F;
    if (variant == 11U) test.dpi = 2.0F;
    if (variant == 13U) test.right = 3.71875F;
    // Variant 14 restores exactly variant 1 after all mutations on the same
    // retained engine, including the mapped and changed-DPI generations.
    // The mask implementation child admits the original valid full-coverage
    // rounded-mask input. The independent parent PR retains its rejection.
    test.accepted = variant < 15U || variant == 21U;
    test.mapped = variant == 12U || variant == 18U;
    test.presentation = {sizeof(test.presentation), variant == 12U ? 4U : 0U,
        variant == 12U ? 8U : 0U, variant == 12U ? 60U : 64U,
        variant == 12U ? 56U : 64U, test.dpi, variant == 18U ? 2.0F : test.dpi, 0U};
    return test;
}

inline std::uint64_t rgb_scene_engine_flags(unsigned route) {
    if (route == 1U) return PROGPU_NATIVE_ENGINE_GLYPH_RASTER_SHADER_FALLBACK;
    if (route == 2U) return PROGPU_NATIVE_ENGINE_GLYPH_SCALAR_CPU_FALLBACK;
    if (route == 3U) return PROGPU_NATIVE_ENGINE_GLYPH_INTRINSIC_SIMD_CPU_FALLBACK;
    return 0U;
}

inline std::array<progpu_native_scene_rgb_glyph_tile, 4U> rgb_scene_tiles(const rgb_scene_case& test) {
    const auto origin = static_cast<std::int32_t>(8.0F * test.dpi);
    std::array tiles{
        progpu_native_scene_rgb_glyph_tile{0U, 8U, 8U, 0U, -1, -5, 1, test.phase,
            origin + 4, origin + 4, {1, 0, 0, test.foreground_alpha}},
        progpu_native_scene_rgb_glyph_tile{0U, 8U, 8U, 0U, -1, -5, 1, 0.75F,
            origin + 6, origin + 5, {0, 1, 1, test.foreground_alpha}},
        progpu_native_scene_rgb_glyph_tile{0U, 8U, 8U, 0U, -1, -5, 1, 0.5F,
            origin + 12, origin + 4, {1, 1, 0, test.foreground_alpha}},
        progpu_native_scene_rgb_glyph_tile{0U, 8U, 8U, 0U, -1, -5, 1, 0.25F,
            static_cast<std::int32_t>(16.0F * test.dpi), origin + 10,
            {0, 1, 0, test.foreground_alpha}}};
    if (test.reversed) std::swap(tiles[0], tiles[1]);
    return tiles;
}

template<class ConfigureMask>
bool build_rgb_scene_fixture(const rgb_scene_case& test, std::uint64_t generation,
    std::vector<std::byte>& stream, progpu_native_scene_header& header, ConfigureMask configure_mask) {
    semantic_scene_builder builder(0x9682U, generation);
    const auto fail = [&](const char* stage) {
        std::fprintf(stderr, "RGB scene fixture variant=%u stage=%s builder-error=%u\n",
            test.variant, stage, static_cast<unsigned>(builder.last_error()));
        return false;
    };
    constexpr float left = 0.21875F, bottom = 0.21875F, top = 3.71875F;
    const std::array segments{
        progpu_native_path_segment{{left, bottom}, {test.right, bottom}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{{test.right, bottom}, {test.right, top}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{{test.right, top}, {left, top}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
        progpu_native_path_segment{{left, top}, {left, bottom}, {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}};
    const progpu_native_scene_glyph_outline outline{0U, 4U, left, bottom, test.right, top, 1, 0};
    std::uint32_t glyph_resource{};
    if (!builder.add_glyph_outlines(std::span(&outline, 1U), segments, glyph_resource)) return fail("outlines");

    auto state = semantic_scene_builder::identity_state();
    state.opacity = test.opacity;
    state.transform.m31 = state.transform.m32 = static_cast<float>(test.translation);
    if (test.clip) {
        state.flags |= PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
        state.clip_rect = {12, 12, 8, 8};
    }
    if (test.variant == 19U) state.transform.m11 = 1.25F;
    if (test.variant == 20U) state.transform.m31 = 0.25F;
    if (test.variant == 21U) {
        progpu_native_scene_layer_mask mask{};
        mask.struct_size = sizeof(mask);
        mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE;
        mask.bounds = {8, 8, 24, 24};
        mask.transform = semantic_scene_builder::identity_transform();
        mask.opacity = 1.0F;
        if (!builder.add_rounded_rectangle_mask(mask, state.mask_resource_index)) return fail("mask");
        state.flags |= PROGPU_NATIVE_SCENE_STATE_MASK;
    }
    if (test.variant == 22U) {
        constexpr std::array guides{8.0, 24.0};
        if (!builder.add_guideline_set(guides, guides, state.guideline_resource_index, false, true))
            return fail("per-point guidelines");
        state.flags |= PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET;
    }
    if (!configure_mask(builder, state)) return fail("source mask");
    std::uint32_t state_resource{};
    if (!builder.add_state(state, state_resource)) return fail("state");
    progpu_native_scene_layer layer{};
    layer.struct_size = sizeof(layer);
    layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS | PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION |
        PROGPU_NATIVE_SCENE_LAYER_IGNORE_ALPHA;
    layer.bounds = {8, 8, 24, 24};
    layer.opacity = 1.0F;
    layer.mask_resource_index = layer.effect_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (test.variant == 16U) layer.flags &= ~PROGPU_NATIVE_SCENE_LAYER_IGNORE_ALPHA;
    if (test.variant != 15U && !builder.push_layer(layer)) return fail("opaque layer");
    if (test.variant == 17U) {
        auto inner = layer;
        inner.flags &= ~PROGPU_NATIVE_SCENE_LAYER_IGNORE_ALPHA;
        if (!builder.push_layer(inner)) return fail("transparent innermost layer");
    }
    const auto rectangle = [&](progpu_native_image_rect bounds, progpu_native_color color) {
        std::uint32_t brush{};
        progpu_native_analytic_primitive primitive{};
        primitive.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
        primitive.x = bounds.x;
        primitive.y = bounds.y;
        primitive.width = bounds.width;
        primitive.height = bounds.height;
        primitive.color = {1, 1, 1, 1};
        primitive.transform = semantic_scene_builder::identity_transform();
        return builder.add_solid_brush(color, 1.0F, brush) &&
            builder.draw_analytic(std::span(&primitive, 1U), std::span(&brush, 1U), bounds);
    };
    const auto tiles = rgb_scene_tiles(test);
    progpu_native_scene_rgb_glyph_draw draw{sizeof(draw), 3U,
        PROGPU_NATIVE_RGB_GLYPH_FULL_PIXEL_BOX_8X8, test.geometry, 1, 0, 1,
        test.variant == 23U ? 2.0F : test.dpi, 0U, 0U};
    const auto draw_rgb = [&](std::span<const progpu_native_scene_rgb_glyph_tile> glyphs) {
        if (test.variant != 22U)
            return builder.draw_rgb_glyph_run(glyph_resource, draw, glyphs, layer.bounds, state_resource);
        // Direct per-point draw state is invalid at the builder boundary.
        // A valid retained SAVE may carry those guidelines to the draw; keep
        // that separate runtime rejection control and both original owners.
        if (builder.draw_rgb_glyph_run(glyph_resource, draw, glyphs, layer.bounds, state_resource) ||
            builder.last_error() != scene_build_error::invalid_argument) return false;
        return builder.save(state_resource) &&
            builder.draw_rgb_glyph_run(glyph_resource, draw, glyphs, layer.bounds) && builder.restore();
    };
    if (!rectangle(layer.bounds, {1, 1, 1, 1}) ||
        !draw_rgb(std::span(tiles).first(3U)) ||
        !rectangle({16, 8, 4, 24}, {0, 0, 1, 1})) return fail("ordered prefix");
    draw.glyph_count = 1U;
    if (!draw_rgb(std::span(tiles).last(1U))) return fail("ordered suffix");
    if (test.variant == 17U && !builder.pop_layer()) return fail("inner pop");
    if (test.variant != 15U && !builder.pop_layer()) return fail("pop");
    if (!builder.build(stream) || stream.size() < sizeof(header)) return fail("build");
    std::memcpy(&header, stream.data(), sizeof(header));
    return header.scene_id == 0x9682U && header.generation == generation &&
        header.command_count == (test.variant == 15U ? 4U : test.variant == 17U ? 8U : test.variant == 22U ? 10U : 6U);
}

inline bool build_rgb_scene_fixture(const rgb_scene_case& test, std::uint64_t generation,
    std::vector<std::byte>& stream, progpu_native_scene_header& header) {
    return build_rgb_scene_fixture(test, generation, stream, header,
        [](semantic_scene_builder&, progpu_native_scene_state&) { return true; });
}

inline std::uint8_t rgb_scene_sample_coverage(const rgb_scene_case& test,
    const progpu_native_scene_rgb_glyph_tile& tile, unsigned x, unsigned y, unsigned channel) {
    double stripe = 0.0;
    if (test.geometry != 0U) {
        stripe = (static_cast<int>(channel) - 1) / 3.0;
        if (test.geometry == 2U) stripe = -stripe;
    }
    unsigned count{};
    for (unsigned sy = 0U; sy < 8U; ++sy) {
        for (unsigned sx = 0U; sx < 8U; ++sx) {
            const double sample_x = (tile.x_start + static_cast<double>(x) + stripe +
                (2.0 * sx + 1.0) / 16.0 - tile.subpixel_x) / tile.scale;
            const double sample_y = -(tile.y_start + static_cast<double>(y) +
                (2.0 * sy + 1.0) / 16.0) / tile.scale;
            count += sample_x >= 7.0 / 32.0 && sample_x < test.right &&
                sample_y >= 7.0 / 32.0 && sample_y < 119.0 / 32.0;
        }
    }
    // The only half-integer among 65 possible quantized counts is 127.5;
    // nearest-even and nearest-up both produce 128. No FP rounding mode enters.
    return static_cast<std::uint8_t>((count * 255U + 32U) / 64U);
}

template<class Require, class MaskAlpha>
std::vector<std::uint8_t> rgb_scene_expected(const rgb_scene_case& test, Require require, MaskAlpha mask_alpha) {
    std::vector<std::uint8_t> pixels(64U * 64U * 4U, 0U);
    for (std::size_t i = 3U; i < pixels.size(); i += 4U) pixels[i] = 255U;
    const auto viewport_x = static_cast<int>(test.presentation.viewport_x);
    const auto viewport_y = static_cast<int>(test.presentation.viewport_y);
    const int left = static_cast<int>(8.0F * test.dpi) + viewport_x;
    const int top = static_cast<int>(8.0F * test.dpi) + viewport_y;
    const int right = static_cast<int>(32.0F * test.dpi) + viewport_x;
    const int bottom = static_cast<int>(32.0F * test.dpi) + viewport_y;
    const auto fill = [&](int x0, int y0, int x1, int y1, std::array<std::uint8_t, 3U> color) {
        for (int y = y0; y < y1; ++y) for (int x = x0; x < x1; ++x) {
            require(x >= 0 && x < 64 && y >= 0 && y < 64, "RGB reference rectangle escaped the physical target");
            const auto offset = (static_cast<std::size_t>(y) * 64U + static_cast<unsigned>(x)) * 4U;
            std::copy(color.begin(), color.end(), pixels.begin() + static_cast<std::ptrdiff_t>(offset));
        }
    };
    fill(left, top, right, bottom, {255U, 255U, 255U});
    const auto tiles = rgb_scene_tiles(test);
    const auto paint = [&](const progpu_native_scene_rgb_glyph_tile& tile) {
        const int dx = static_cast<int>(test.translation * test.dpi) + viewport_x;
        const int dy = static_cast<int>(test.translation * test.dpi) + viewport_y;
        const std::array color{tile.foreground.r, tile.foreground.g, tile.foreground.b};
        // The retained source multiplies the original two float opacities once.
        // Membership and blending below are otherwise an independent scalar
        // oracle, with no production winding, atlas, or scene-state calls.
        const float alpha = tile.foreground.a * test.opacity;
        for (unsigned y = 0U; y < tile.height; ++y) for (unsigned x = 0U; x < tile.width; ++x) {
            const int px = tile.target_x + dx + static_cast<int>(x);
            const int py = tile.target_y + dy + static_cast<int>(y);
            if (px < left || px >= right || py < top || py >= bottom) continue;
            if (test.clip && (px < 12 * test.dpi + viewport_x || px >= 20 * test.dpi + viewport_x ||
                    py < 12 * test.dpi + viewport_y || py >= 20 * test.dpi + viewport_y)) continue;
            const auto offset = (static_cast<std::size_t>(py) * 64U + static_cast<unsigned>(px)) * 4U;
            for (unsigned channel = 0U; channel < 3U; ++channel) {
                const auto coverage = rgb_scene_sample_coverage(test, tile, x, y, channel);
                const double amount = coverage / 255.0 * alpha * mask_alpha(px, py);
                const double value = color[channel] * 255.0 * amount + pixels[offset + channel] * (1.0 - amount);
                // These authored colors avoid half-byte blend ties. A future
                // edit must keep that property, not hide device quantization in
                // a tolerance or silently select a different rounding oracle.
                require(std::abs(value - std::floor(value) - 0.5) > 0.0001,
                    "RGB reference blend introduced an ambiguous UNORM midpoint");
                pixels[offset + channel] = static_cast<std::uint8_t>(std::floor(value + 0.5));
            }
        }
    };
    for (unsigned i = 0U; i < 3U; ++i) paint(tiles[i]);
    fill(static_cast<int>(16.0F * test.dpi) + viewport_x, top,
        static_cast<int>(20.0F * test.dpi) + viewport_x, bottom, {0U, 0U, 255U});
    paint(tiles[3]);
    return pixels;
}

template<class Require>
std::vector<std::uint8_t> rgb_scene_expected(const rgb_scene_case& test, Require require) {
    return rgb_scene_expected(test, require, [](int, int) { return 1.0; });
}

// Render selects an actual provider engine by immutable route, updates this
// original stream, and returns RGBA after its existing GPU completion/readback.
// Unsupported cases must fail in preflight without advancing submissions.
template<class Render, class Require>
void verify_rgb_glyph_scene_pixels(Render render, Require require) {
    const auto original = rgb_scene_case_for(1U);
    const auto original_tiles = rgb_scene_tiles(original);
    require(rgb_scene_sample_coverage(original, original_tiles[0], 1U, 1U, 0U) == 48U &&
        rgb_scene_sample_coverage(original, original_tiles[0], 1U, 1U, 1U) == 96U &&
        rgb_scene_sample_coverage(original, original_tiles[0], 1U, 1U, 2U) == 167U,
        "independent RGB sample membership control changed");
    require(rgb_scene_sample_coverage(original, original_tiles[0], 1U, 1U, 0U) !=
        (2U * rgb_scene_sample_coverage(original, original_tiles[0], 1U, 1U, 1U) +
            rgb_scene_sample_coverage(original, original_tiles[0], 0U, 1U, 1U) + 1U) / 3U,
        "RGB oracle became a shifted quantized grayscale sample");
    std::array<std::vector<std::uint8_t>, 2U> original_pixels;
    for (unsigned variant = 0U; variant < 24U; ++variant) {
        const auto test = rgb_scene_case_for(variant);
        std::vector<std::byte> stream;
        progpu_native_scene_header header{};
        require(build_rgb_scene_fixture(test, variant + 1U, stream, header), "RGB scene construction failed");
        const auto expected = test.accepted ? rgb_scene_expected(test, require) : std::vector<std::uint8_t>{};
        for (unsigned route = 0U; route < 2U; ++route) {
            for (unsigned replay = 0U; replay < 2U; ++replay) {
                progpu_native_scene_frame_metrics metrics{};
                metrics.struct_size = sizeof(metrics);
                const auto pixels = render(route, stream, header, test, metrics);
                if (pixels != expected) {
                    const auto extent = std::min(pixels.size(), expected.size());
                    std::size_t first{};
                    while (first < extent && pixels[first] == expected[first]) ++first;
                    std::fprintf(stderr, "RGB scene mismatch variant=%u route=%u replay=%u byte=%zu "
                        "xy=%zu,%zu channel=%zu actual=%u expected=%u sizes=%zu/%zu\n",
                        variant, route, replay, first, first / 4U % 64U, first / 256U, first % 4U,
                        first < pixels.size() ? pixels[first] : 999U,
                        first < expected.size() ? expected[first] : 999U, pixels.size(), expected.size());
                }
                require(pixels == expected, "RGB retained-scene full pixels differ from independent coverage/source order");
                if (!test.accepted) {
                    require(metrics.submission_count == 0U && metrics.draw_call_count == 0U,
                        "unsupported RGB scene encoded or submitted work");
                    continue;
                }
                require(metrics.command_count == 6U && metrics.draw_call_count == (route == 0U ? 9U : 13U) &&
                    metrics.submission_count == 1U && metrics.coverage_staging_bytes == 0U &&
                    metrics.vertex_upload_bytes > 0U && metrics.uniform_upload_bytes > 0U,
                    "RGB actual route/source-order draw or GPU-owned coverage metrics changed");
                if (variant == 1U && replay == 0U) original_pixels[route] = pixels;
                if (variant == 14U) require(pixels == original_pixels[route],
                    "RGB original generation failed to restore after state/phase/DPI mutations");
            }
        }
        std::fprintf(stderr, "RGB retained scene variant=%u: both GPU routes, cold/warm %s\n",
            variant, test.accepted ? "exact pixels" : "atomic rejection");
    }
    // Explicit unsupported engine policies must not fall back to a supported
    // route or submit before reporting failure. The same valid owned scene is
    // used so these controls cannot pass through malformed-packet rejection.
    auto unsupported = original;
    unsupported.accepted = false;
    std::vector<std::byte> stream;
    progpu_native_scene_header header{};
    require(build_rgb_scene_fixture(unsupported, 1U, stream, header), "RGB policy fixture construction failed");
    for (unsigned route = 2U; route < 5U; ++route) {
        progpu_native_scene_frame_metrics metrics{};
        metrics.struct_size = sizeof(metrics);
        require(render(route, stream, header, unsupported, metrics).empty() &&
            metrics.submission_count == 0U && metrics.draw_call_count == 0U,
            "RGB CPU/sRGB policy silently selected another renderer or submitted");
    }
}

} // namespace progpu::native::tests
