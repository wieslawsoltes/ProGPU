#pragma once

#include "progpu_native_hinted_glyph_rendering_fixture.hpp"
#include "../src/Text/Interop/progpu_native_hinted_paragraph_glyph_frame.hpp"
#include "../src/Text/Interop/progpu_native_hinted_paragraph_interaction.hpp"

namespace progpu::native::tests {
namespace hinted_paragraph_rendering {
using namespace progpu::native::text;

struct raw_frame final {
    hinted_rendering::raw_frame records;
    std::vector<hinted_paragraph_draw_owner> owners;
    progpu_native_glyph_frame borrow(const hinted_paragraph_glyph_target& target) const noexcept {
        return records.borrow({target.width, target.height, target.dpi_scale, target.target_view,
            target.logical_origin, {}, target.clear_color});
    }
};

// Independently restore the retained runs, use the original public measured
// writer, then unpack authored four-point contours directly. Neither paragraph
// frame packing nor outline conversion supplies this reference's records/maps.
template<class Require>
raw_frame unpack(const hinted_paragraph_generation& paragraph,
    const hinted_paragraph_glyph_target& target, std::span<const progpu_native_color> colors,
    Require require) {
    std::vector<shaping_glyph> logical;
    std::vector<hinted_paragraph_glyph_owner> owners;
    std::vector<std::int8_t> levels;
    std::vector<float> scales;
    std::vector<text_item_metrics> items;
    std::vector<std::vector<std::uint32_t>> outlines;
    raw_frame result;
    for (std::size_t run_index = 0U; run_index < paragraph.runs.size(); ++run_index) {
        const auto& run = paragraph.runs[run_index];
        const auto& shaped = *run.generation;
        std::vector<std::uint32_t> order(shaped.glyphs.size());
        std::iota(order.begin(), order.end(), 0U);
        std::stable_sort(order.begin(), order.end(), [&](auto a, auto b) {
            return shaped.glyphs[a].cluster < shaped.glyphs[b].cluster;
        });
        require(run.logical_start == logical.size(), "hinted paragraph raw logical partition changed");
        for (auto original : order) {
            auto glyph = shaped.glyphs[original];
            glyph.advance_y = -glyph.advance_y; glyph.offset_y = -glyph.offset_y;
            logical.push_back(glyph);
            owners.push_back({static_cast<std::uint32_t>(run_index), original, shaped.descriptor_indices[original]});
            levels.push_back(run.bidi_level);
            scales.push_back(run.logical_units_per_physical_pixel / 64.0F);
            const auto& metric = paragraph.source_metrics[run.style_index];
            items.push_back({metric.ascent, metric.descent});
        }
        auto& map = outlines.emplace_back(shaped.source_descriptor_count, hinted_no_outline);
        for (std::size_t descriptor = 0U; descriptor < shaped.source_descriptor_count; ++descriptor) {
            const auto& raw = shaped.batch->glyphs[descriptor];
            if (raw.points.empty()) {
                require(raw.tags.empty() && raw.contour_ends.empty(), "hinted paragraph raw no-ink topology changed");
                continue;
            }
            require(raw.points.size() == 4U && raw.tags.size() == 4U &&
                raw.contour_ends == std::vector<std::int16_t>{3} &&
                std::all_of(raw.tags.begin(), raw.tags.end(), [](auto value) { return (value & 3U) == 1U; }),
                "hinted paragraph raw authored contours changed");
            std::array<progpu_native_point, 4U> points{};
            for (std::size_t point = 0U; point < points.size(); ++point)
                points[point] = {static_cast<float>(raw.points[point].x_26_6) / 64.0F,
                    static_cast<float>(raw.points[point].y_26_6) / 64.0F};
            auto minimum = points.front(), maximum = points.front();
            for (auto point : points) {
                minimum.x = std::min(minimum.x, point.x); minimum.y = std::min(minimum.y, point.y);
                maximum.x = std::max(maximum.x, point.x); maximum.y = std::max(maximum.y, point.y);
            }
            map[descriptor] = static_cast<std::uint32_t>(result.records.outlines.size());
            result.records.outlines.push_back({result.records.segments.size(), 4U,
                minimum.x, minimum.y, maximum.x, maximum.y, 1.0F, 0.0F});
            for (std::size_t edge = 0U; edge < points.size(); ++edge)
                result.records.segments.push_back({points[edge], points[(edge + 1U) % points.size()],
                    {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U});
        }
    }
    require(owners == paragraph.logical_owners && levels == paragraph.logical_bidi_levels &&
        scales == paragraph.glyph_scales, "hinted paragraph raw original source restoration changed");
    text_layout_options options{};
    options.scale = paragraph.layout.scale; options.maximum_width = paragraph.layout.maximum_width;
    options.line_height = paragraph.layout.line_height; options.maximum_lines = paragraph.layout.maximum_lines;
    options.direction = static_cast<shaping_direction>(paragraph.layout.direction);
    options.alignment = static_cast<text_alignment>(paragraph.layout.alignment);
    text_layout_requirements needed{};
    require(try_get_scaled_text_layout_requirements(logical, paragraph.breaks_after, scales, options, needed),
        "hinted paragraph raw measured requirements failed");
    std::vector<positioned_text_glyph> positioned(needed.glyph_capacity);
    std::vector<positioned_text_line> lines(needed.line_capacity);
    std::vector<text_visual_cluster_group> groups(needed.glyph_capacity);
    std::vector<std::uint32_t> indices(needed.glyph_capacity);
    std::uint32_t count{}, line_count{};
    require(try_layout_measured_logical_shaped_text(logical, paragraph.breaks_after, levels, scales,
        paragraph.paragraph_level, options, {}, {}, {groups, indices}, positioned, lines, count, line_count,
        paragraph.justification_classes, items) && count == paragraph.glyphs.size() && line_count == paragraph.lines.size(),
        "hinted paragraph raw original measured writer failed");
    for (std::size_t index = 0U; index < count; ++index) {
        const auto& expected = positioned[index]; const auto& actual = paragraph.glyphs[index];
        require(expected.glyph_index == actual.glyph_index && expected.glyph_id == actual.glyph_id &&
            expected.cluster == actual.cluster && expected.x == actual.x && expected.y == actual.y &&
            expected.advance_x == actual.advance_x && expected.advance_y == actual.advance_y,
            "hinted paragraph retained original writer output changed");
        const auto owner = owners[expected.glyph_index];
        const auto& run = paragraph.runs[owner.run_index];
        const auto outline = outlines[owner.run_index][owner.descriptor_index];
        if (outline == hinted_no_outline) continue;
        result.records.glyphs.push_back({outline, 0U,
            {expected.x + target.logical_origin.x, expected.y + target.logical_origin.y},
            {1.0F, 0.0F}, {0.0F, 1.0F}, colors[run.style_index], 1.0F, 0.0F, 0.0F, 0.0F});
        result.owners.push_back({static_cast<std::uint32_t>(index), expected.glyph_index,
            owner.run_index, owner.run_glyph_index, owner.descriptor_index, run.font_index, run.style_index});
    }
    return result;
}
} // namespace hinted_paragraph_rendering

// Dedicated fresh subject/reference engines retain identical call histories.
// Existing harnesses own actual completion/readback and original deadlines.
template<class Render, class Require>
void verify_hinted_paragraph_glyph_rendering(Render render, Require require) {
    using namespace hinted_paragraph_rendering;
    std::array<std::uint64_t, 2U> submission_counts{};
    for (auto policy : {font_hint_policy::truetype_35, font_hint_policy::truetype_40}) {
        for (unsigned variant = 0U; variant < 3U; ++variant) {
            const bool no_ink = variant == 2U;
            const float dpi = variant == 1U ? 2.0F : 1.0F, units = 1.0F / dpi;
            auto bytes = make_hinted_shape_font();
            progpu_native_text_context* context{};
            require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
                reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U, nullptr, 0U, &context) ==
                PROGPU_NATIVE_STATUS_SUCCESS && context != nullptr, "hinted paragraph GPU context creation failed");
            std::uint32_t second = UINT32_MAX;
            require(progpu_native_text_context_add_fallback_font(context,
                reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U, 0x7712U, &second) ==
                PROGPU_NATIVE_STATUS_SUCCESS && second == 1U, "hinted paragraph GPU second source creation failed");
            std::array<progpu_native_text_scalar, 7U> input{};
            const std::array<std::uint32_t, 7U> values{'B', 'A', 'C', 0x0627U, 'B', 'A', 'B'};
            for (std::size_t i = 0U; i < input.size(); ++i)
                input[i] = {no_ink ? 0x43U : values[i], static_cast<std::uint32_t>(9U + i * 2U), 1U, 0U, 0U, 0U};
            const std::array<progpu_native_text_feature, 2U> features{{
                {0x6C696761U, 1U, 0U, UINT32_MAX}, {0x6B65726EU, 1U, 0U, UINT32_MAX}}};
            std::array<progpu_native_text_style_run, 2U> styles{{
                {0U, 3U, 0U, 0.013F, 0U, 2U, 0U, 0U, 0U, 0U, 0U},
                {3U, 4U, 1U, 0.017F, 0U, 2U, 0U, 0U, 0U, 0U, 0U}}};
            std::array<progpu_native_text_style_metrics, 2U> metrics{{{14.0F * units, 4.0F * units},
                {18.0F * units, 4.0F * units}}};
            std::array<hinted_paragraph_style_configuration, 2U> device{{
                {0U, styles[0].scale, {13U * 64U, 13U * 64U, policy, variant == 1U ? 7U : 0U,
                    variant == 1U ? 11U : 0U, {}}, units},
                {1U, styles[1].scale, {17U * 64U, 17U * 64U, policy, variant == 1U ? 19U : 0U,
                    variant == 1U ? 23U : 0U, {}}, units}}};
            progpu_native_text_shape_request shaping{};
            shaping.struct_size = sizeof(shaping); shaping.abi_version = PROGPU_NATIVE_ABI_VERSION;
            shaping.input = input.data(); shaping.input_count = input.size();
            shaping.features = features.data(); shaping.feature_count = features.size();
            shaping.direction = variant == 1U ? PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT : PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
            progpu_native_text_layout_options layout{};
            layout.struct_size = sizeof(layout); layout.scale = 1.0F; layout.direction = shaping.direction;
            layout.maximum_width = 34.0F * units; layout.alignment = PROGPU_NATIVE_TEXT_ALIGNMENT_CENTER;
            std::shared_ptr<const hinted_paragraph_generation> paragraph;
            progpu_native_text_paragraph_result diagnostic{}; diagnostic.struct_size = sizeof(diagnostic);
            require(try_layout_context_hinted_paragraph(context, shaping, layout, styles, metrics, device, paragraph, diagnostic) ==
                PROGPU_NATIVE_STATUS_SUCCESS && paragraph != nullptr, "hinted paragraph GPU retained formatting failed");
            require(paragraph->font_sources.size() == 2U && paragraph->font_sources[0] != paragraph->font_sources[1],
                "hinted paragraph GPU merged original font source identities");
            std::array<progpu_native_color, 2U> colors{{{1.0F, 0.0F, 0.0F, 1.0F}, {0.0F, 0.0F, 1.0F, 1.0F}}};
            hinted_paragraph_glyph_target target{64U, 64U, dpi, 0U, {4.0F * units, 4.0F * units}, {0.0F, 0.0F, 0.0F, 1.0F}};
            const auto reference = unpack(*paragraph, target, colors, require);
            auto interaction = create_hinted_paragraph_interaction(paragraph);
            require(interaction.status == PROGPU_NATIVE_STATUS_SUCCESS && interaction.generation->paragraph() == paragraph,
                "hinted paragraph GPU interaction did not retain original formatted generation");
            std::weak_ptr<const hinted_paragraph_generation> weak_paragraph = paragraph;
            std::weak_ptr<const owned_font_source> weak_source = paragraph->font_sources[1];
            progpu_native_text_context_destroy(context);
            std::fill(bytes.begin(), bytes.end(), std::byte{0}); input.fill({}); styles.fill({}); metrics.fill({}); device.fill({});
            std::shared_ptr<const hinted_paragraph_glyph_frame> retained;
            std::array<progpu_native_glyph_frame_metrics, 4U> rendered_metrics{};
            std::array<std::vector<std::uint8_t>, 4U> pixels;
            for (std::size_t attempt = 0U; attempt < pixels.size(); ++attempt) {
                const bool raw = (attempt & 1U) != 0U;
                pixels[attempt] = render(raw, dpi, [&](progpu_native_engine* engine, std::uintptr_t view) {
                    target.target_view = view;
                    std::uint64_t before{}, after{};
                    require(progpu_native_engine_get_last_submission(engine, &before) == PROGPU_NATIVE_STATUS_SUCCESS,
                        "hinted paragraph GPU submission identity unavailable");
                    if (raw) {
                        const auto frame = reference.borrow(target);
                        rendered_metrics[attempt].struct_size = sizeof(rendered_metrics[attempt]);
                        require(progpu_native_engine_render_glyphs(engine, &frame, &rendered_metrics[attempt]) ==
                            PROGPU_NATIVE_STATUS_SUCCESS, "hinted paragraph independent raw GPU rendering failed");
                    } else {
                        if (paragraph == nullptr) paragraph = retained->paragraph();
                        const auto created = create_hinted_paragraph_glyph_frame(paragraph, target, colors,
                            hinted_projection_policy::automatic, hinted_outline_coverage::nonzero_vector);
                        if (created.status != PROGPU_NATIVE_STATUS_SUCCESS)
                            std::fprintf(stderr, "Hinted paragraph frame failure: policy=%u variant=%u status=%u error=%u outline=%u\n",
                                static_cast<unsigned>(policy), variant, static_cast<unsigned>(created.status),
                                static_cast<unsigned>(created.error.code), static_cast<unsigned>(created.error.outline));
                        require(created.status == PROGPU_NATIVE_STATUS_SUCCESS && created.generation != nullptr,
                            "hinted paragraph owned GPU frame creation failed");
                        retained = created.generation;
                        require(retained->paragraph() == interaction.generation->paragraph() &&
                            std::equal(retained->draw_owners().begin(), retained->draw_owners().end(),
                                reference.owners.begin(), reference.owners.end()),
                            "hinted paragraph GPU draw/interaction lost exact original owners");
                        paragraph.reset();
                        const auto executed = execution::render_hinted_paragraph_glyph_frame(engine, retained);
                        require(executed.status == PROGPU_NATIVE_STATUS_SUCCESS, "hinted paragraph owned real GPU consumer failed");
                        rendered_metrics[attempt] = executed.metrics;
                    }
                    require(progpu_native_engine_get_last_submission(engine, &after) == PROGPU_NATIVE_STATUS_SUCCESS && after > before &&
                        rendered_metrics[attempt].submission_count == ++submission_counts[raw ? 1U : 0U],
                        "hinted paragraph GPU changed original single submission boundary");
                });
            }
            require(pixels[0].size() == 64U * 64U * 4U && pixels[0] == pixels[1] && pixels[0] == pixels[2] && pixels[0] == pixels[3] &&
                hinted_rendering::same_metrics(rendered_metrics[0], rendered_metrics[1]) &&
                hinted_rendering::same_metrics(rendered_metrics[2], rendered_metrics[3]),
                "hinted paragraph GPU full pixels/counters differ from independent original records");
            require(rendered_metrics[0].glyph_count == reference.records.glyphs.size() &&
                (no_ink ? reference.records.glyphs.empty() : reference.records.glyphs.size() == 5U),
                "hinted paragraph GPU merged repeated draws or painted a no-ink descriptor");
            bool painted = false, first_color = false, second_color = false;
            for (std::size_t pixel = 0U; pixel < pixels[0].size(); pixel += 4U) {
                painted |= pixels[0][pixel] != 0U || pixels[0][pixel + 1U] != 0U || pixels[0][pixel + 2U] != 0U;
                // Both pure style colors must be visible in either RGBA or
                // BGRA readback; their order differs, not these two controls.
                first_color |= pixels[0][pixel] != 0U && pixels[0][pixel + 2U] == 0U;
                second_color |= pixels[0][pixel + 2U] != 0U && pixels[0][pixel] == 0U;
                if (no_ink) require(pixels[0][pixel + 3U] == 255U, "hinted paragraph no-ink clear alpha changed");
            }
            require(painted != no_ink, "hinted paragraph GPU compared empty images or painted no-ink input");
            if (!no_ink) require(first_color && second_color && rendered_metrics[0].instance_upload_bytes != 0U &&
                rendered_metrics[2].instance_upload_bytes != 0U, "hinted paragraph GPU omitted a visible style or current instances");
            else require(rendered_metrics[0].instance_upload_bytes == 0U && rendered_metrics[0].outline_upload_bytes == 0U &&
                rendered_metrics[0].coverage_staging_bytes == 0U && rendered_metrics[0].uniform_upload_bytes == 0U,
                "hinted paragraph no-ink sources entered raster or instance uploads");
            retained.reset();
            require(!weak_paragraph.expired() && !weak_source.expired(), "hinted paragraph interaction lost retired source ownership");
            interaction.generation.reset();
            require(weak_paragraph.expired() && weak_source.expired(), "hinted paragraph frame/interaction leaked retired ownership");
            std::fprintf(stderr, "Hinted paragraph real GPU: policy=%u variant=%u glyphs=%u full pixels/counters passed\n",
                static_cast<unsigned>(policy), variant, rendered_metrics[0].glyph_count);
        }
    }
}
} // namespace progpu::native::tests
