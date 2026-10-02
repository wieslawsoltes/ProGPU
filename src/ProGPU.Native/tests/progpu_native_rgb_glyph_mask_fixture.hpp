#pragma once

#include "progpu_native_rgb_glyph_scene_fixture.hpp"

namespace progpu::native::tests {

enum class rgb_scene_mask_kind : unsigned {
    bitmap, analytic, chain, gradient, vector, geometry, picture, composite
};

struct rgb_scene_mask_case final {
    rgb_scene_case scene;
    rgb_scene_mask_kind kind{};
    unsigned phase{};
    bool changed{};
};

inline rgb_scene_mask_case rgb_mask_case_for(rgb_scene_mask_kind kind, unsigned phase) {
    rgb_scene_mask_case test{rgb_scene_case_for(phase == 2U ? 11U : phase == 4U ? 12U : 1U), kind,
        phase, phase == 1U};
    test.scene.variant = 100U + static_cast<unsigned>(kind) * 5U + phase;
    // Exercise source color and scope opacity simultaneously with independent
    // mask alpha. The original no-midpoint guard still owns exact UNORM checks.
    test.scene.opacity = 0.61F;
    test.scene.foreground_alpha = 0.37F;
    if (phase == 2U) test.scene.translation = 1;
    if (phase == 4U) test.scene.clip = true;
    return test;
}

inline progpu_native_scene_layer_mask rgb_mask_strip(bool horizontal, bool changed) {
    progpu_native_scene_layer_mask mask{};
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE;
    // Only straight strip edges intersect a glyph. Rounded-rectangle corner
    // derivatives are deliberately absent from this independent 1D oracle.
    const float start = changed ? 16.25F : 14.25F;
    mask.bounds = horizontal ? progpu_native_image_rect{0, start, 64, 8}
                             : progpu_native_image_rect{start, 0, 8, 64};
    mask.transform = semantic_scene_builder::identity_transform();
    mask.opacity = 1.0F;
    return mask;
}

inline progpu_native_scene_layer_brush_mask rgb_gradient_mask(bool changed) {
    progpu_native_scene_layer_brush_mask mask{};
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH;
    mask.gradient_stop_count = 2U;
    mask.bounds = {8, 8, 24, 24};
    mask.transform = semantic_scene_builder::identity_transform();
    mask.opacity = 1;
    mask.brush.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
    mask.brush.opacity = 1;
    mask.brush.start_point = changed ? progpu_native_point{32, 8} : progpu_native_point{8, 8};
    mask.brush.end_point = changed ? progpu_native_point{8, 8} : progpu_native_point{32, 8};
    mask.brush.stop_count = 2U;
    // RGB deliberately disagrees with alpha: this is an opacity mask, not a
    // luminance mask or a reused unmasked brush-color result.
    mask.brush.colors[0] = {1, 0, 0, 0};
    mask.brush.colors[1] = {0, 1, 0, 1};
    mask.brush.offsets0[1] = 1;
    mask.brush.coordinate_transform0[0] = mask.brush.coordinate_transform1[1] = 1;
    return mask;
}

inline std::array<progpu_native_image_rect, 2U> rgb_mask_l_rectangles(bool changed) {
    // Two disjoint rectangles define an L (or its vertical reversal), not its
    // enclosing box. Several original glyph samples occupy the empty counter.
    return {progpu_native_image_rect{12, changed ? 24.0F : 12.0F, 12, 4},
        progpu_native_image_rect{12, changed ? 12.0F : 16.0F, 4, 12}};
}

inline std::array<progpu_native_path_segment, 8U> rgb_mask_l_segments(bool changed) {
    std::array<progpu_native_path_segment, 8U> segments{};
    const auto rectangles = rgb_mask_l_rectangles(changed);
    for (std::size_t i = 0U; i < rectangles.size(); ++i) {
        const auto& r = rectangles[i];
        const std::array<progpu_native_point, 4U> points{{{r.x, r.y}, {r.x + r.width, r.y},
            {r.x + r.width, r.y + r.height}, {r.x, r.y + r.height}}};
        for (std::size_t j = 0U; j < points.size(); ++j)
            segments[i * 4U + j] = {points[j], points[(j + 1U) % 4U], {}, {},
                PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U};
    }
    return segments;
}

inline bool record_rgb_scene_mask(semantic_scene_builder& builder, progpu_native_scene_state& state,
    const rgb_scene_mask_case& test, std::uint64_t generation) {
    const auto identity = semantic_scene_builder::identity_transform();
    std::uint32_t resource{};
    bool recorded = false;
    if (test.kind == rgb_scene_mask_kind::bitmap) {
        progpu_native_scene_layer_coverage_mask mask{};
        mask.struct_size = sizeof(mask);
        mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP;
        mask.width = mask.height = mask.row_bytes = 24U;
        mask.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
        mask.bounds = {8, 8, 24, 24};
        mask.transform = identity;
        mask.opacity = 1;
        std::array<std::byte, 24U * 24U> samples{};
        for (unsigned y = 0U; y < 24U; ++y) for (unsigned x = 0U; x < 24U; ++x)
            samples[y * 24U + x] = static_cast<std::byte>(((x + 2U * y + (test.changed ? 1U : 0U)) % 4U) * 85U);
        recorded = builder.add_coverage_mask(mask, samples, resource);
    } else if (test.kind == rgb_scene_mask_kind::analytic) {
        recorded = builder.add_rounded_rectangle_mask(rgb_mask_strip(false, test.changed), resource);
    } else if (test.kind == rgb_scene_mask_kind::chain) {
        const std::array masks{rgb_mask_strip(false, test.changed), rgb_mask_strip(true, false)};
        recorded = builder.add_analytic_mask_chain(masks, resource);
    } else if (test.kind == rgb_scene_mask_kind::gradient || test.kind == rgb_scene_mask_kind::composite) {
        const auto mask = rgb_gradient_mask(test.changed);
        constexpr std::array<progpu_native_scene_gradient_stop, 2U> stops{{
            {{1, 0, 0, 0}, 0, 0U, 0U, 0U}, {{0, 1, 0, 1}, 1, 0U, 0U, 0U}}};
        if (test.kind == rgb_scene_mask_kind::gradient) {
            recorded = builder.add_brush_mask(mask, stops, resource);
        } else {
            const auto segments = rgb_mask_l_segments(false);
            const progpu_native_scene_clip_path path{0U, segments.size(), 0U, 0U, 12, 12, 24, 28,
                identity, PROGPU_NATIVE_FILL_RULE_NON_ZERO, 8U, PROGPU_NATIVE_CLIP_INTERSECT, 0U};
            recorded = builder.add_composite_mask(std::span(&mask, 1U), {}, {}, {}, {},
                std::span(&path, 1U), segments, {}, stops, 1.0F, resource);
        }
    } else if (test.kind == rgb_scene_mask_kind::vector) {
        const auto segments = rgb_mask_l_segments(test.changed);
        const progpu_native_scene_clip_path path{0U, segments.size(), 0U, 0U, 12, 12, 24, 28,
            identity, PROGPU_NATIVE_FILL_RULE_NON_ZERO, 8U, PROGPU_NATIVE_CLIP_INTERSECT, 0U};
        recorded = builder.add_vector_clip_mask(std::span(&path, 1U), segments, 1.0F, resource);
    } else if (test.kind == rgb_scene_mask_kind::geometry) {
        progpu_native_scene_layer_geometry_mask mask{};
        mask.struct_size = sizeof(mask);
        mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY;
        mask.primitive_count = 2U;
        mask.bounds = {8, 8, 24, 24};
        mask.transform = identity;
        mask.opacity = 1;
        mask.brush.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
        mask.brush.opacity = 1;
        mask.brush.colors[0] = {0, 0, 0, 1};
        mask.brush.coordinate_transform0[0] = mask.brush.coordinate_transform1[1] = 1;
        std::array<progpu_native_geometry_primitive, 2U> primitives{};
        const auto rectangles = rgb_mask_l_rectangles(test.changed);
        for (std::size_t i = 0U; i < primitives.size(); ++i) {
            const auto& r = rectangles[i];
            primitives[i] = {PROGPU_NATIVE_GEOMETRY_QUADRILATERAL, PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED,
                {r.x, r.y}, {r.x + r.width, r.y}, {r.x + r.width, r.y + r.height}, {r.x, r.y + r.height},
                0, 0, {1, 1, 1, 1}, identity};
        }
        recorded = builder.add_geometry_mask(mask, primitives, {}, resource);
    } else if (test.kind == rgb_scene_mask_kind::picture) {
        semantic_scene_builder child(0x9683U, generation);
        std::uint32_t brush{};
        if (!child.add_solid_brush({0, 0, 0, 1}, 1, brush)) return false;
        for (const auto& r : rgb_mask_l_rectangles(test.changed)) {
            progpu_native_analytic_primitive primitive{};
            primitive.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
            primitive.flags = PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED;
            primitive.x = r.x; primitive.y = r.y;
            primitive.width = r.width; primitive.height = r.height;
            primitive.color = {1, 1, 1, 1};
            primitive.transform = identity;
            if (!child.draw_analytic(std::span(&primitive, 1U), std::span(&brush, 1U), r)) return false;
        }
        std::vector<std::byte> nested;
        if (!child.build(nested)) return false;
        progpu_native_scene_layer_picture_mask mask{};
        mask.struct_size = sizeof(mask);
        mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE;
        mask.stream_size = static_cast<std::uint32_t>(nested.size());
        mask.bounds = {8, 8, 24, 24};
        mask.transform = identity;
        mask.opacity = 1;
        recorded = builder.add_picture_mask(mask, nested, resource);
    }
    if (!recorded) return false;
    state.flags |= PROGPU_NATIVE_SCENE_STATE_MASK;
    state.mask_resource_index = resource;
    return true;
}

inline double rgb_mask_expected_alpha(const rgb_scene_mask_case& test, int px, int py) {
    const double x = (px + 0.5 - test.scene.presentation.viewport_x) / test.scene.dpi;
    const double y = (py + 0.5 - test.scene.presentation.viewport_y) / test.scene.dpi;
    const auto strip = [&](double coordinate, bool changed) {
        const double start = changed ? 16.25 : 14.25;
        // Independent pixel/straight-edge intersection. Derivatives of this
        // 1D edge are exactly one physical pixel in the admitted positive axes.
        return std::clamp((coordinate - start) * test.scene.dpi + 0.5, 0.0, 1.0) *
            std::clamp((start + 8.0 - coordinate) * test.scene.dpi + 0.5, 0.0, 1.0);
    };
    if (test.kind == rgb_scene_mask_kind::analytic) return strip(x, test.changed);
    if (test.kind == rgb_scene_mask_kind::chain) return strip(x, test.changed) * strip(y, false);
    if (x < 8 || x >= 32 || y < 8 || y >= 32) return 0;
    if (test.kind == rgb_scene_mask_kind::bitmap) {
        const auto sx = static_cast<unsigned>(std::floor(x - 8));
        const auto sy = static_cast<unsigned>(std::floor(y - 8));
        return ((sx + 2U * sy + (test.changed ? 1U : 0U)) % 4U) / 3.0;
    }
    const auto inside_l = [&](bool changed) {
        return (x >= 12 && x < 16 && y >= 12 && y < 28) ||
            (x >= 16 && x < 24 && y >= (changed ? 24 : 12) && y < (changed ? 28 : 16));
    };
    if (test.kind == rgb_scene_mask_kind::gradient || test.kind == rgb_scene_mask_kind::composite) {
        const double amount = test.changed ? (32.0 - x) / 24.0 : (x - 8.0) / 24.0;
        // The actual gradient mask owns an R8 intermediate. Its transparent
        // and opaque original stop alphas are independently interpolated here;
        // retain that one quantization, never borrow the product texture.
        const double alpha = std::floor(std::clamp(amount, 0.0, 1.0) * 255.0 + 0.5) / 255.0;
        return test.kind == rgb_scene_mask_kind::composite && !inside_l(false) ? 0.0 : alpha;
    }
    return inside_l(test.changed) ? 1.0 : 0.0;
}

template<class Render, class Require>
void verify_rgb_glyph_mask_scene_pixels(Render render, Require require) {
    for (unsigned kind = 0U; kind < 8U; ++kind) {
        std::array<std::vector<std::uint8_t>, 2U> original;
        for (unsigned phase = 0U; phase < 5U; ++phase) {
            const auto test = rgb_mask_case_for(static_cast<rgb_scene_mask_kind>(kind), phase);
            const auto generation = 100U + kind * 5U + phase;
            std::vector<std::byte> stream;
            progpu_native_scene_header header{};
            require(build_rgb_scene_fixture(test.scene, generation, stream, header,
                [&](semantic_scene_builder& builder, progpu_native_scene_state& state) {
                    return record_rgb_scene_mask(builder, state, test, generation);
                }), "RGB source-mask scene construction failed");
            const auto expected = rgb_scene_expected(test.scene, require,
                [&](int x, int y) { return rgb_mask_expected_alpha(test, x, y); });
            const auto absent = rgb_scene_expected(test.scene, require, [](int, int) { return 0.0; });
            const auto unmasked = rgb_scene_expected(test.scene, require);
            require(expected != absent && expected != unmasked,
                "RGB spatial mask oracle failed to distinguish ink and excluded source coverage");
            for (unsigned route = 0U; route < 2U; ++route) {
                for (unsigned replay = 0U; replay < 2U; ++replay) {
                    // Two distinct RGB operations retain their own mask binding.
                    // A second vector preparation fences shared clip scratch.
                    // A picture renders one owned child on a cold identity; the
                    // second operation and reset reuse that exact source raster.
                    const bool vector_preparation = test.kind == rgb_scene_mask_kind::vector ||
                        test.kind == rgb_scene_mask_kind::composite;
                    const bool new_picture = test.kind == rgb_scene_mask_kind::picture && phase != 3U;
                    const std::uint64_t submissions = replay == 0U && (vector_preparation || new_picture) ? 2U : 1U;
                    progpu_native_scene_frame_metrics metrics{};
                    metrics.struct_size = sizeof(metrics);
                    const auto pixels = render(route, stream, header, test.scene, metrics, submissions);
                    if (pixels != expected) {
                        std::size_t first{};
                        while (first < std::min(pixels.size(), expected.size()) && pixels[first] == expected[first]) ++first;
                        std::fprintf(stderr, "RGB mask mismatch kind=%u phase=%u route=%u replay=%u "
                            "xy=%zu,%zu channel=%zu actual=%u expected=%u sizes=%zu/%zu\n",
                            kind, phase, route, replay, first / 4U % 64U, first / 256U, first % 4U,
                            first < pixels.size() ? pixels[first] : 999U,
                            first < expected.size() ? expected[first] : 999U, pixels.size(), expected.size());
                    }
                    require(pixels == expected, "RGB source mask changed exact channel coverage/order/alpha");
                    require(metrics.command_count == 6U && metrics.draw_call_count == (route == 0U ? 9U : 13U) &&
                        metrics.submission_count == submissions && metrics.coverage_staging_bytes == 0U &&
                        metrics.vertex_upload_bytes > 0U && metrics.uniform_upload_bytes > 0U,
                        "RGB source mask lost actual replay or preparation counters");
                    if (phase == 0U && replay == 0U) original[route] = pixels;
                    if (phase == 1U) require(pixels != original[route], "RGB changed mask reused stale source content");
                    if (phase == 3U) require(pixels == original[route], "RGB original mask failed to restore after mutations");
                }
            }
            std::fprintf(stderr, "RGB source mask kind=%u phase=%u: compute/fragment cold/warm exact full pixels\n", kind, phase);
        }
    }
}

} // namespace progpu::native::tests
