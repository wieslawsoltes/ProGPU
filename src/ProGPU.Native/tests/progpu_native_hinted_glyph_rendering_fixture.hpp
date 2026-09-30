#pragma once

#include "progpu_native_hinted_shape_fixture.hpp"
#include "../src/Backend/progpu_native_hinted_glyph_execution.hpp"

#include <algorithm>
#include <array>
#include <cstdio>
#include <numeric>
#include <vector>

namespace progpu::native::tests {
namespace hinted_rendering {
using namespace progpu::native::text;

struct raw_frame final {
    std::vector<progpu_native_glyph_outline> outlines;
    std::vector<progpu_native_path_segment> segments;
    std::vector<progpu_native_positioned_glyph> glyphs;

    progpu_native_glyph_frame borrow(const hinted_glyph_target& target) const noexcept {
        progpu_native_glyph_frame frame{};
        frame.struct_size = sizeof(frame);
        frame.width = target.width; frame.height = target.height;
        frame.dpi_scale = target.dpi_scale; frame.target_view = target.target_view;
        frame.clear_color = target.clear_color;
        frame.outlines = outlines.data(); frame.outline_count = outlines.size();
        frame.segments = segments.data(); frame.segment_count = segments.size();
        frame.glyphs = glyphs.data(); frame.glyph_count = glyphs.size();
        return frame;
    }
};

// Independent unpacked reference for the original authored four-on-curve-point
// font. It calls the original measured paragraph writer directly and emits four
// literal edges per captured source descriptor; neither owned-frame packing nor
// hinted-outline conversion supplies its geometry or positioned instances.
template<class Require>
raw_frame unpack(const hinted_shaped_run& run, const hinted_text_layout& layout,
    const hinted_glyph_target& target, Require require) {
    std::vector<std::uint32_t> original(run.glyphs.size());
    std::iota(original.begin(), original.end(), 0U);
    std::stable_sort(original.begin(), original.end(), [&](auto left, auto right) {
        return run.glyphs[left].cluster < run.glyphs[right].cluster;
    });
    std::vector<shaping_glyph> logical;
    for (auto index : original) {
        auto glyph = run.glyphs[index];
        glyph.offset_y = -glyph.offset_y; glyph.advance_y = -glyph.advance_y;
        logical.push_back(glyph);
    }
    const float units = 1.0F / target.dpi_scale;
    const auto level = static_cast<std::int8_t>(run.direction == shaping_direction::right_to_left ? 1 : 0);
    const std::vector<text_line_break_kind> breaks(logical.size(), text_line_break_kind::opportunity);
    const std::vector<std::int8_t> levels(logical.size(), level);
    const std::vector<text_item_metrics> items(logical.size(), {14.0F * units, 4.0F * units});
    const std::vector<float> scales(logical.size(), units / 64.0F);
    text_layout_options options{};
    options.direction = run.direction; options.maximum_width = 50.0F * units; options.scale = units / 64.0F;
    require(layout.logical_units_per_physical_pixel == units && layout.options.scale == options.scale &&
        layout.breaks_after == breaks && layout.logical_bidi_levels == levels &&
        layout.item_metrics.size() == items.size() && layout.justification_classes.empty(),
        "hinted owned fitting changed caller metadata or physical units");
    for (std::size_t index = 0U; index < items.size(); ++index) {
        require(layout.item_metrics[index].ascent == items[index].ascent &&
            layout.item_metrics[index].descent == items[index].descent &&
            layout.logical_cluster_ends[index] == logical[index].cluster + 1,
            "hinted owned fitting changed original metrics or cluster ends");
    }
    text_layout_requirements needed{};
    require(try_get_scaled_text_layout_requirements(logical, breaks, scales,
        options, needed), "hinted raw reference layout requirements failed");
    std::vector<positioned_text_glyph> positioned(needed.glyph_capacity);
    std::vector<positioned_text_line> lines(needed.line_capacity);
    std::vector<text_visual_cluster_group> groups(needed.glyph_capacity);
    std::vector<std::uint32_t> indices(needed.glyph_capacity);
    std::uint32_t count{}, line_count{};
    require(try_layout_measured_logical_shaped_text(logical, breaks,
        levels, scales, level, options,
        {}, {}, {groups, indices}, positioned, lines, count, line_count,
        {}, items), "hinted raw reference original fitting failed");
    require(count == layout.glyphs.size() && line_count == layout.lines.size(),
        "hinted owned fitting changed original writer counts");
    text_layout_metrics measured{};
    require(try_measure_measured_text_lines(std::span<const positioned_text_line>(lines).first(line_count),
        options.maximum_width, measured) &&
        measured.content_width == layout.metrics.content_width &&
        measured.content_height == layout.metrics.content_height &&
        measured.measured_width == layout.metrics.measured_width &&
        measured.measured_height == layout.metrics.measured_height,
        "hinted owned fitting changed original writer extents");
    raw_frame result;
    std::vector<std::uint32_t> source(run.source_descriptor_count, hinted_no_outline);
    for (std::size_t descriptor = 0U; descriptor < run.source_descriptor_count; ++descriptor) {
        const auto& glyph = run.batch->glyphs[descriptor];
        if (glyph.points.empty()) {
            require(glyph.tags.empty() && glyph.contour_ends.empty(), "hinted raw no-ink topology changed");
            continue;
        }
        require(glyph.points.size() == 4U && glyph.tags.size() == 4U &&
            glyph.contour_ends == std::vector<std::int16_t>{3} &&
            std::all_of(glyph.tags.begin(), glyph.tags.end(), [](auto tag) { return (tag & 3U) == 1U; }),
            "hinted authored raw reference contour changed");
        std::array<progpu_native_point, 4U> corners{};
        for (std::size_t point = 0U; point < corners.size(); ++point) {
            corners[point] = {static_cast<float>(glyph.points[point].x_26_6) / 64.0F,
                static_cast<float>(glyph.points[point].y_26_6) / 64.0F};
        }
        auto bounds = corners.front();
        auto maximum = bounds;
        for (const auto point : corners) {
            bounds.x = std::min(bounds.x, point.x); bounds.y = std::min(bounds.y, point.y);
            maximum.x = std::max(maximum.x, point.x); maximum.y = std::max(maximum.y, point.y);
        }
        source[descriptor] = static_cast<std::uint32_t>(result.outlines.size());
        result.outlines.push_back({result.segments.size(), 4U,
            bounds.x, bounds.y, maximum.x, maximum.y, 1.0F, 0.0F});
        for (std::size_t edge = 0U; edge < corners.size(); ++edge) {
            result.segments.push_back({corners[edge], corners[(edge + 1U) % corners.size()],
                {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U});
        }
    }
    for (std::size_t index = 0U; index < count; ++index) {
        auto expected = positioned[index];
        expected.glyph_index = original[expected.glyph_index];
        const auto& actual = layout.glyphs[index];
        require(expected.glyph_index == actual.glyph_index && expected.glyph_id == actual.glyph_id &&
            expected.cluster == actual.cluster && expected.x == actual.x && expected.y == actual.y &&
            expected.advance_x == actual.advance_x && expected.advance_y == actual.advance_y,
            "hinted owned fitting changed original glyph identity or metrics");
        const auto descriptor = run.descriptor_indices[expected.glyph_index];
        require(layout.descriptor_indices[index] == descriptor,
            "hinted owned fitting changed exact source descriptor");
        if (source[descriptor] == hinted_no_outline) continue;
        result.glyphs.push_back({source[descriptor], 0U,
            {expected.x + target.logical_origin.x, expected.y + target.logical_origin.y},
            {1.0F, 0.0F}, {0.0F, 1.0F}, target.color, 1.0F, 0.0F, 0.0F, 0.0F});
    }
    return result;
}

inline bool same_metrics(const progpu_native_glyph_frame_metrics& a,
    const progpu_native_glyph_frame_metrics& b) noexcept {
    return a.struct_size == b.struct_size && a.draw_call_count == b.draw_call_count &&
        a.glyph_count == b.glyph_count && a.rasterized_glyph_count == b.rasterized_glyph_count &&
        a.atlas_width == b.atlas_width && a.atlas_height == b.atlas_height &&
        a.atlas_generation == b.atlas_generation && a.atlas_growth_count == b.atlas_growth_count &&
        a.instance_upload_bytes == b.instance_upload_bytes && a.outline_upload_bytes == b.outline_upload_bytes &&
        a.coverage_staging_bytes == b.coverage_staging_bytes && a.uniform_upload_bytes == b.uniform_upload_bytes &&
        a.submission_count == b.submission_count && a.payload_hash == b.payload_hash;
}
} // namespace hinted_rendering

// Render chooses independent persistent engines for subject/reference, creates a
// real same-device 64x64 target, invokes draw(engine, view) once and returns tight
// RGBA or BGRA pixels through its existing completion/readback contract. The
// fixture adds no completion wait, font execution in rendering or source Display
// admission. Flags remain zero: replay is fidelity evidence, not atlas retention.
template<class Render, class Require>
void verify_hinted_glyph_rendering(Render render, Require require) {
    using namespace hinted_rendering;
    std::array<std::uint64_t, 2U> submission_counts{};
    for (const auto interpreter : {font_hint_policy::truetype_35, font_hint_policy::truetype_40}) {
        std::vector<hinted_outline_point> unphased;
        std::vector<std::uint8_t> unphased_pixels;
        for (unsigned int variant = 0U; variant < 3U; ++variant) {
            const bool no_ink = variant == 2U;
            const float dpi = variant == 1U ? 2.0F : 1.0F;
            const float units = 1.0F / dpi;
            auto bytes = make_hinted_shape_font();
            progpu_native_text_context* context{};
            require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
                reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U, nullptr, 0U,
                &context) == PROGPU_NATIVE_STATUS_SUCCESS && context != nullptr,
                "hinted rendering font context creation failed");
            hinted_font_configuration configuration{};
            configuration.x_pixels_per_em_26_6 = 13U * 64U;
            configuration.y_pixels_per_em_26_6 = 13U * 64U;
            configuration.policy = interpreter;
            configuration.x_phase_26_6 = variant == 1U ? 7U : 0U;
            configuration.y_phase_26_6 = variant == 1U ? 11U : 0U;
            const std::array<unicode_scalar, 4U> input{{{no_ink ? 0x43U : 0x42U, 0U, 1U},
                {no_ink ? 0x43U : 0x41U, 1U, 1U}, {0x43U, 2U, 1U}, {no_ink ? 0x43U : 0x42U, 3U, 1U}}};
            const std::array features{open_type_tag::from_chars('l', 'i', 'g', 'a'),
                open_type_tag::from_chars('k', 'e', 'r', 'n')};
            open_type_shape_run_options shaping{};
            shaping.script = open_type_tag::from_chars('l', 'a', 't', 'n');
            shaping.direction = variant == 1U ? shaping_direction::right_to_left : shaping_direction::left_to_right;
            shaping.requested_features = features;
            std::shared_ptr<const hinted_shaped_run> run;
            hinted_shape_error shape_error{};
            require(try_shape_context_hinted(context, 0U, configuration, input, shaping, run, shape_error),
                "hinted rendering owned native shaping failed");
            require(run->glyphs.size() == input.size() && run->source_descriptor_count == input.size() &&
                run->batch->glyphs.size() == input.size(), "hinted rendering source slots changed");
            const auto& identity = *run->batch->identity;
            require(identity.source != nullptr && identity.source->bytes == bytes && identity.source->face_index == 0U &&
                identity.policy == interpreter && identity.x_pixels_per_em_26_6 == configuration.x_pixels_per_em_26_6 &&
                identity.y_pixels_per_em_26_6 == configuration.y_pixels_per_em_26_6 &&
                identity.x_phase_26_6 == configuration.x_phase_26_6 && identity.y_phase_26_6 == configuration.y_phase_26_6 &&
                identity.variation_coordinates_16_16.empty(), "hinted rendering changed exact immutable font configuration");
            for (std::size_t descriptor = 0U; descriptor < input.size(); ++descriptor) {
                const auto expected_id = no_ink || descriptor == 2U ? 0U : 1U;
                require(run->batch->glyphs[descriptor].glyph_index == expected_id,
                    "hinted rendering lost original post-GSUB descriptor order");
                const auto original = variant == 1U ? input.size() - 1U - descriptor : descriptor;
                require(run->descriptor_indices[descriptor] == original &&
                    run->glyphs[descriptor].cluster == static_cast<std::int32_t>(original),
                    "hinted rendering lost source identity during visual ordering");
            }
            if (variant == 0U) unphased = run->batch->glyphs[0].points;
            if (variant == 1U) {
                const auto& points = run->batch->glyphs[0].points;
                require(points.size() == unphased.size() && !points.empty(), "hinted rendering phase point count changed");
                for (std::size_t point = 0U; point < points.size(); ++point) {
                    require(points[point].x_26_6 == unphased[point].x_26_6 + 7 &&
                        points[point].y_26_6 == unphased[point].y_26_6 + 11,
                        "hinted rendering captured phase was omitted or applied twice");
                }
            }
            std::vector<text_line_break_kind> breaks(input.size(), text_line_break_kind::opportunity);
            const auto level = static_cast<std::int8_t>(variant == 1U ? 1 : 0);
            std::vector<std::int8_t> levels(input.size(), level);
            std::vector<std::int32_t> ends;
            for (const auto& glyph : run->glyphs) ends.push_back(glyph.cluster + 1);
            std::vector<text_item_metrics> items(input.size(), {14.0F * units, 4.0F * units});
            text_layout_options options{};
            options.direction = run->direction; options.maximum_width = 50.0F * units;
            std::shared_ptr<const hinted_text_layout> layout;
            require(try_layout_hinted_shaped_run(run, breaks, levels, ends, items, {}, level, units,
                options, layout), "hinted rendering retained original fitting failed");
            hinted_glyph_target target{64U, 64U, dpi, 0U, {4.0F * units, 8.0F * units},
                {1.0F, 0.25F, 0.0F, 1.0F}, {0.0F, 0.0F, 0.0F, 1.0F}};
            const auto reference = unpack(*run, *layout, target, require);
            require(reference.glyphs.size() == (no_ink ? 0U : 3U) &&
                reference.outlines.size() == (no_ink ? 0U : 3U), "hinted raw reference omitted source draws");
            std::weak_ptr<const hinted_shaped_run> weak_run = run;
            std::weak_ptr<const hinted_text_layout> weak_layout = layout;
            std::weak_ptr<const hinted_glyph_batch> weak_batch = run->batch;
            std::weak_ptr<const owned_font_source> weak_source = run->batch->identity->source;
            progpu_native_text_context_destroy(context);
            std::fill(bytes.begin(), bytes.end(), std::byte{0});
            breaks.clear(); levels.clear(); ends.clear(); items.clear();
            std::shared_ptr<const hinted_glyph_frame> retained;
            std::array<progpu_native_glyph_frame_metrics, 4U> metrics{};
            std::array<std::vector<std::uint8_t>, 4U> pixels;
            for (std::size_t attempt = 0U; attempt < pixels.size(); ++attempt) {
                const bool raw = (attempt & 1U) != 0U;
                pixels[attempt] = render(raw, dpi, [&](progpu_native_engine* engine, std::uintptr_t view) {
                    target.target_view = view;
                    std::uint64_t before{}, after{};
                    require(progpu_native_engine_get_last_submission(engine, &before) == PROGPU_NATIVE_STATUS_SUCCESS,
                        "hinted rendering original submission identity unavailable");
                    if (raw) {
                        const auto frame = reference.borrow(target);
                        metrics[attempt].struct_size = sizeof(metrics[attempt]);
                        require(progpu_native_engine_render_glyphs(engine, &frame, &metrics[attempt]) ==
                            PROGPU_NATIVE_STATUS_SUCCESS, "hinted independent raw renderer failed");
                    } else {
                        if (layout == nullptr) { layout = retained->layout(); run = layout->run; }
                        hinted_glyph_frame_error error{};
                        require(try_create_hinted_glyph_frame(layout, run, target, retained, &error),
                            "hinted rendering owned frame publication failed");
                        require(retained->glyphs().size() == reference.glyphs.size() &&
                            retained->source_outline_indices()[2U] == hinted_no_outline &&
                            retained->layout() == layout && retained->layout()->run == run,
                            "hinted rendering frame lost generation or no-ink ownership");
                        std::uint32_t outline_index = 0U;
                        for (std::size_t descriptor = 0U; descriptor < input.size(); ++descriptor) {
                            const auto expected = no_ink || descriptor == 2U ? hinted_no_outline : outline_index++;
                            require(retained->source_outline_indices()[descriptor] == expected,
                                "hinted rendering substituted an equal-ID source outline owner");
                        }
                        std::size_t draw_index = 0U;
                        for (std::size_t positioned = 0U; positioned < layout->glyphs.size(); ++positioned) {
                            const auto original = layout->glyphs[positioned].glyph_index;
                            const auto descriptor = run->descriptor_indices[original];
                            require(retained->run_outline_indices()[original] == retained->source_outline_indices()[descriptor],
                                "hinted rendering lost original run descriptor identity");
                            if (retained->source_outline_indices()[descriptor] == hinted_no_outline) continue;
                            require(retained->draw_layout_indices()[draw_index++] == positioned,
                                "hinted rendering changed visual source draw order");
                        }
                        layout.reset(); run.reset();
                        require(!weak_run.expired() && !weak_layout.expired() && !weak_batch.expired() && !weak_source.expired(),
                            "hinted renderer frame failed to retain retired source generation");
                        const auto rendered = execution::render_hinted_glyph_frame(engine, retained);
                        require(rendered.status == PROGPU_NATIVE_STATUS_SUCCESS, "hinted owned real GPU consumer failed");
                        metrics[attempt] = rendered.metrics;
                    }
                    // Native metrics count this engine's original submits. The
                    // actual provider token also includes other engines and
                    // harness readback copies, so it is not that counter.
                    const auto expected_submissions = ++submission_counts[raw ? 1U : 0U];
                    require(progpu_native_engine_get_last_submission(engine, &after) == PROGPU_NATIVE_STATUS_SUCCESS &&
                        after > before && metrics[attempt].submission_count == expected_submissions,
                        "hinted rendering changed original single submission boundary");
                });
            }
            require(pixels[0].size() == 64U * 64U * 4U && pixels[0] == pixels[1] &&
                pixels[0] == pixels[2] && pixels[0] == pixels[3],
                "hinted consumer cold/replay pixels differ from independent original records");
            require(same_metrics(metrics[0], metrics[1]) && same_metrics(metrics[2], metrics[3]),
                "hinted consumer raw renderer counters differ from independent records");
            require(metrics[0].glyph_count == (no_ink ? 0U : 3U) &&
                metrics[0].draw_call_count == (no_ink ? 0U : 1U) &&
                metrics[0].rasterized_glyph_count == (no_ink ? 0U : 1U) &&
                metrics[2].rasterized_glyph_count == metrics[0].rasterized_glyph_count,
                "hinted consumer merged source draws or repeated exact raster slots");
            bool painted = false;
            for (std::size_t pixel = 0U; pixel < pixels[0].size(); pixel += 4U) {
                painted |= pixels[0][pixel] != 0U || pixels[0][pixel + 1U] != 0U || pixels[0][pixel + 2U] != 0U;
                if (no_ink) require(pixels[0][pixel + 3U] == 255U, "hinted no-ink target clear alpha changed");
            }
            require(painted != no_ink, "hinted rendering compared empty images or painted a no-ink descriptor");
            if (!no_ink) require(metrics[0].instance_upload_bytes != 0U && metrics[2].instance_upload_bytes != 0U,
                "hinted flags-zero consumer did not upload current original instances");
            if (no_ink) require(metrics[0].instance_upload_bytes == 0U && metrics[0].outline_upload_bytes == 0U &&
                metrics[0].coverage_staging_bytes == 0U && metrics[0].uniform_upload_bytes == 0U,
                "hinted no-ink descriptors entered raster or instance uploads");
            if (variant == 0U) unphased_pixels = pixels[0];
            if (variant == 1U) require(pixels[0] != unphased_pixels,
                "hinted nonzero phase/direction produced stale original pixels");
            retained.reset();
            require(weak_run.expired() && weak_layout.expired() && weak_batch.expired() && weak_source.expired(),
                "hinted rendering frame retained source owners after retirement");
            std::fprintf(stderr, "Hinted real GPU consumer: interpreter=%u variant=%u dpi=%.1f glyphs=%u rasters=%u exact pixels/counters passed\n",
                static_cast<unsigned>(interpreter), variant, dpi, metrics[0].glyph_count, metrics[0].rasterized_glyph_count);
        }
    }
}
} // namespace progpu::native::tests
