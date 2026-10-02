#include "../src/Text/Interop/progpu_native_hinted_paragraph_internal.hpp"
#include "../src/Text/Interop/progpu_native_hinted_paragraph_interaction.hpp"
#include "../src/Text/Interop/progpu_native_hinted_paragraph_glyph_frame.hpp"
#include "../src/Text/Interop/progpu_native_text_font_source.hpp"
#include "progpu_native_hinted_shape_fixture.hpp"
#include "../src/Text/progpu_native_text_layout_retained_internal.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <iostream>
#include <limits>
#include <numeric>
#include <source_location>
#include <stdexcept>
#include <string>
#include <type_traits>

// Actual context/interpreter controls over the repository's authored font.
// Independent reference producers below are test-only: the retained production
// paragraph may not shape or resolve bidi a second time. These controls admit
// neither source Display, empty-hard-row carets nor GPU/package/UI parity.
namespace {
using namespace progpu::native::text;
static_assert(!std::is_copy_constructible_v<hinted_paragraph_generation> &&
    !std::is_move_constructible_v<hinted_paragraph_generation>);

void require(bool value, std::source_location at = std::source_location::current()) {
    if (!value) throw std::runtime_error("hinted paragraph control at " + std::to_string(at.line()));
}

#if defined(PROGPU_NATIVE_FONT_HINTING)
struct context_owner final {
    progpu_native_text_context* value = nullptr;
    context_owner() = default;
    context_owner(const context_owner&) = delete;
    context_owner& operator=(const context_owner&) = delete;
    ~context_owner() { progpu_native_text_context_destroy(value); }
};

struct fixture final {
    std::vector<std::byte> bytes = progpu::native::tests::make_hinted_shape_font();
    context_owner context{};
    std::uint32_t second_font = UINT32_MAX;
    fixture() {
        require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
            reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U,
            nullptr, 0U, &context.value) == PROGPU_NATIVE_STATUS_SUCCESS);
        require(progpu_native_text_context_add_fallback_font(context.value,
            reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U,
            0x7711U, &second_font) == PROGPU_NATIVE_STATUS_SUCCESS && second_font == 1U);
        require(select_context_font_source(context.value, 0U) != select_context_font_source(context.value, second_font));
    }
};

std::vector<progpu_native_text_scalar> scalars(std::span<const std::uint32_t> values,
    std::uint32_t first = 9U) {
    std::vector<progpu_native_text_scalar> result;
    for (const auto value : values) {
        const auto length = value == 'A' ? std::uint16_t{2U} : std::uint16_t{1U};
        result.push_back({value, first, length, 0U, 0U, 0U});
        first += length;
    }
    return result;
}

progpu_native_text_shape_request shape_request(std::span<const progpu_native_text_scalar> input,
    std::span<const progpu_native_text_feature> features = {}) {
    progpu_native_text_shape_request result{};
    result.struct_size = sizeof(result); result.abi_version = PROGPU_NATIVE_ABI_VERSION;
    result.input = input.data(); result.input_count = static_cast<std::uint32_t>(input.size());
    result.features = features.data(); result.feature_count = static_cast<std::uint32_t>(features.size());
    result.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
    return result;
}

progpu_native_text_layout_options layout_options() {
    progpu_native_text_layout_options result{};
    result.struct_size = sizeof(result); result.scale = 1.0F;
    result.maximum_width = 200.0F; result.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
    return result;
}

struct bidi_reference final {
    std::vector<progpu_native_text_bidi_level> levels{};
    std::int32_t paragraph_level = -1;
};

bidi_reference resolve_reference_bidi(std::span<const progpu_native_text_scalar> input,
    std::span<const progpu_native_text_style_run> styles, std::int32_t requested_level) {
    progpu_native_text_bidi_requirements required{}; required.struct_size = sizeof(required);
    require(progpu_native_text_get_bidi_requirements(input.data(), static_cast<std::uint32_t>(input.size()),
        &required) == PROGPU_NATIVE_STATUS_SUCCESS);
    bidi_reference result;
    result.levels.resize(required.level_capacity + 1U);
    result.levels.back() = {0xFFFFU, 17U, -77, 91U};
    const auto tail = result.levels.back();
    std::vector<std::byte> scratch(required.scratch_bytes + 1U, std::byte{0x71U});
    progpu_native_text_bidi_result diagnostic{}; diagnostic.struct_size = sizeof(diagnostic);
    require(progpu_native_text_resolve_styled_bidi(input.data(), static_cast<std::uint32_t>(input.size()),
        requested_level, styles.data(), static_cast<std::uint32_t>(styles.size()), result.levels.data(),
        required.level_capacity, scratch.data(), required.scratch_bytes, &diagnostic) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(diagnostic.level_count == input.size() && scratch.back() == std::byte{0x71U} &&
        std::memcmp(&result.levels.back(), &tail, sizeof(tail)) == 0);
    result.levels.resize(diagnostic.level_count); result.paragraph_level = diagnostic.paragraph_level;
    return result;
}

struct paragraph_request final {
    std::vector<progpu_native_text_scalar> input{};
    std::vector<progpu_native_text_feature> features{{0x6C696761U, 1U, 0U, UINT32_MAX},
        {0x6B65726EU, 1U, 0U, UINT32_MAX}};
    std::vector<progpu_native_text_style_run> styles{};
    std::vector<progpu_native_text_style_metrics> metrics{};
    std::vector<hinted_paragraph_style_configuration> configurations{};
    progpu_native_text_shape_request shaping{};
    progpu_native_text_layout_options layout = layout_options();

    paragraph_request(font_hint_policy policy, bool source_bidi) {
        const std::array<std::uint32_t, 9U> values{'A', 'B', ' ', '1', '2', 0x0627U, 'A', 0x03A9U, 'B'};
        input = scalars(values);
        const auto digits = 0x0660U | (source_bidi ? PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_SOURCE_BIDI : 0U);
        styles = {{0U, 5U, 0U, 0.0066328125F, 0U, 2U, 0U, digits, 0U, 0U, 0U},
            {5U, 4U, 1U, 0.0138875F, 0U, 2U, 0U, digits, 0U, 0U, 0U}};
        metrics = {{5.5F, 1.75F}, {9.25F, 2.5F}}; // Explicit source values, never driver ascent/descent.
        configurations = {{0U, styles[0].scale,
            {13U * 64U + 17U, 13U * 64U + 17U, policy, 7U, 11U, {}}, 0.5F},
            {1U, styles[1].scale,
            {17U * 64U + 23U, 17U * 64U + 23U, policy, 19U, 23U, {}}, 0.8F}};
        refresh();
    }

    void refresh() { shaping = shape_request(input, features); }
};

std::shared_ptr<const hinted_paragraph_generation> produce(fixture& font, const paragraph_request& request) {
    std::shared_ptr<const hinted_paragraph_generation> result;
    progpu_native_text_paragraph_result diagnostic{}; diagnostic.struct_size = sizeof(diagnostic);
    const auto status = try_layout_context_hinted_paragraph(font.context.value, request.shaping, request.layout,
        request.styles, request.metrics, request.configurations, result, diagnostic);
    if (status != PROGPU_NATIVE_STATUS_SUCCESS)
        std::cerr << "hinted paragraph status=" << static_cast<unsigned int>(status) << " stage=" << diagnostic.error_stage
            << " error=" << diagnostic.error_code << '\n';
    require(status == PROGPU_NATIVE_STATUS_SUCCESS && result != nullptr && diagnostic.error_code == 0U);
    return result;
}

bool equal_glyph(const shaping_glyph& a, const shaping_glyph& b) {
    return a.glyph_id == b.glyph_id && a.code_point == b.code_point && a.cluster == b.cluster && a.flags == b.flags &&
        a.advance_x == b.advance_x && a.advance_y == b.advance_y && a.offset_x == b.offset_x && a.offset_y == b.offset_y;
}

void verify_raw_runs(fixture& font, const paragraph_request& request, const hinted_paragraph_generation& retained) {
    require(retained.runs.size() > 2U && retained.source_input.size() == request.input.size() &&
        retained.shaping_input.size() == request.input.size() && retained.scalar_levels.size() == request.input.size());
    require(std::memcmp(retained.source_input.data(), request.input.data(), request.input.size() * sizeof(request.input[0])) == 0 &&
        retained.shaping.input == retained.source_input.data() && retained.shaping.features == retained.features.data());
    const auto bidi = resolve_reference_bidi(request.input, request.styles,
        request.layout.direction == PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT ? 1 : 0);
    require(retained.paragraph_level == bidi.paragraph_level);
    for (std::size_t index = 0U; index < request.input.size(); ++index) {
        require(retained.scalar_levels[index].level == bidi.levels[index].level &&
            retained.scalar_levels[index].input_index == request.input[index].input_index &&
            retained.scalar_levels[index].input_length == request.input[index].input_length &&
            retained.shaping_input[index].input_index == request.input[index].input_index &&
            retained.shaping_input[index].input_length == request.input[index].input_length);
        const auto rendered = index == 3U ? 0x0661U : index == 4U ? 0x0662U : request.input[index].code_point;
        require(retained.shaping_input[index].code_point == rendered);
    }
    std::vector<unicode_script_run> script_reference(retained.shaping_input.size());
    std::uint32_t scripts = 0U;
    require(try_itemize_unicode_scripts(retained.shaping_input, script_reference, scripts) && scripts == retained.script_runs.size());
    for (std::size_t index = 0U; index < scripts; ++index) {
        const auto& a = script_reference[index]; const auto& b = retained.script_runs[index];
        require(a.scalar_start == b.scalar_start && a.scalar_count == b.scalar_count &&
            a.input_start == b.input_start && a.input_length == b.input_length && a.script == b.script);
    }
    std::size_t logical = 0U, covered = 0U;
    std::array<bool, 2U> fonts{};
    for (std::size_t run_index = 0U; run_index < retained.runs.size(); ++run_index) {
        const auto& run = retained.runs[run_index];
        require(run.generation != nullptr && run.scalar_start == covered && run.scalar_count != 0U &&
            run.scalar_count <= retained.shaping_input.size() - covered && run.style_index < request.styles.size() &&
            run.logical_start == logical && run.logical_count == run.generation->glyphs.size() &&
            run.font_index < fonts.size() && run.generation->batch != nullptr && run.generation->batch->identity != nullptr);
        const auto& configuration = request.configurations[run.style_index];
        const auto& source_style = request.styles[run.style_index];
        const auto& identity = *run.generation->batch->identity;
        const auto& owned = retained.device_styles[run.style_index];
        fonts[run.font_index] = true;
        require(run.font_index == source_style.font_index && run.source_scale == source_style.scale &&
            run.logical_units_per_physical_pixel == configuration.logical_units_per_physical_pixel &&
            owned.font_index == configuration.font_index && owned.source_scale == configuration.source_scale &&
            owned.x_pixels_per_em_26_6 == configuration.hinting.x_pixels_per_em_26_6 &&
            owned.y_pixels_per_em_26_6 == configuration.hinting.y_pixels_per_em_26_6 && owned.policy == configuration.hinting.policy &&
            owned.x_phase_26_6 == configuration.hinting.x_phase_26_6 && owned.y_phase_26_6 == configuration.hinting.y_phase_26_6 &&
            owned.logical_units_per_physical_pixel == configuration.logical_units_per_physical_pixel &&
            owned.variation_coordinates_16_16.empty() && identity.variation_coordinates_16_16.empty() &&
            identity.source == select_context_font_source(font.context.value, run.font_index) &&
            identity.source == retained.font_sources[run.font_index] && identity.policy == configuration.hinting.policy &&
            identity.x_pixels_per_em_26_6 == configuration.hinting.x_pixels_per_em_26_6 &&
            identity.y_pixels_per_em_26_6 == configuration.hinting.y_pixels_per_em_26_6 &&
            identity.x_phase_26_6 == configuration.hinting.x_phase_26_6 && identity.y_phase_26_6 == configuration.hinting.y_phase_26_6);
        const auto input = std::span<const unicode_scalar>(retained.shaping_input).subspan(run.scalar_start, run.scalar_count);
        require(run.generation->shaping_input.size() == input.size());
        for (std::size_t index = 0U; index < input.size(); ++index) {
            const auto& a = run.generation->shaping_input[index]; const auto& b = input[index];
            require(a.code_point == b.code_point && a.input_index == b.input_index && a.input_length == b.input_length);
        }
        // Separate test-only producer, not a second production shape. Resolve
        // original feature/script policy from the owned source and request.
        const auto script = std::find_if(script_reference.begin(), script_reference.begin() + scripts,
            [&](const auto& value) { return run.scalar_start >= value.scalar_start &&
                run.scalar_start < value.scalar_start + value.scalar_count; });
        require(script != script_reference.begin() + scripts);
        std::vector<shaping_feature> features;
        for (std::size_t index = source_style.feature_start; index < source_style.feature_start + source_style.feature_count; ++index) {
            const auto& feature = request.features[index];
            features.push_back({open_type_tag{feature.tag}, feature.value, feature.start, feature.end});
        }
        const auto context_input = std::span<const unicode_scalar>(retained.shaping_input);
        const open_type_shape_configuration_request configuration_request{script->script, {},
            (run.bidi_level & 1) == 0 ? shaping_direction::left_to_right : shaping_direction::right_to_left,
            features, retained.normalized_coordinates, request.shaping.alternate_value,
            (request.shaping.flags & PROGPU_NATIVE_TEXT_SHAPE_ZERO_MARK_ADVANCES) != 0U,
            static_cast<shaping_cluster_level>(request.shaping.cluster_level),
            static_cast<shaping_buffer_flags>(request.shaping.buffer_flags), nullptr,
            context_input.first(run.scalar_start), context_input.subspan(run.scalar_start + run.scalar_count),
            open_type_tag{source_style.language}};
        sfnt_font_view selected_font{};
        require(sfnt_font_view::try_create(identity.source->bytes, identity.source->face_index, selected_font));
        open_type_shape_configuration_requirements planning{};
        require(try_get_open_type_shape_configuration_requirements(selected_font, input, configuration_request, planning));
        std::vector<open_type_feature_setting> base_features(planning.base_feature_capacity);
        std::vector<open_type_tag> explicit_features(planning.explicit_feature_capacity), requested_features(planning.requested_feature_capacity);
        std::vector<shaping_feature> feature_settings(planning.feature_setting_capacity);
        open_type_shape_configuration reference_configuration{};
        require(try_prepare_open_type_shape_configuration(selected_font, input, configuration_request,
            base_features, explicit_features, requested_features, feature_settings, reference_configuration));
        const auto& options = reference_configuration.options;
        std::shared_ptr<const hinted_shaped_run> reference;
        hinted_shape_error error{};
        require(try_shape_context_hinted(font.context.value, run.font_index, configuration.hinting, input,
            options, reference, error, hinted_projection_policy::scalar_reference));
        require(reference->descriptor_indices == run.generation->descriptor_indices &&
            reference->batch->glyphs == run.generation->batch->glyphs && reference->glyphs.size() == run.generation->glyphs.size());
        require(reference->source_descriptor_count == reference->glyphs.size()); // authored fixture has only single-glyph substitution
        std::vector<std::uint32_t> original(reference->glyphs.size());
        std::iota(original.begin(), original.end(), 0U);
        std::stable_sort(original.begin(), original.end(), [&](auto a, auto b) {
            return reference->glyphs[a].cluster < reference->glyphs[b].cluster;
        });
        for (const auto original_index : original) {
            auto expected = reference->glyphs[original_index];
            require(equal_glyph(expected, run.generation->glyphs[original_index]));
            const auto descriptor = reference->descriptor_indices[original_index];
            require(descriptor == logical - run.logical_start && descriptor < reference->source_descriptor_count &&
                reference->batch->glyphs[descriptor].glyph_index == expected.glyph_id);
            // Actual hinted advance, plus the fixture's original projected
            // 12-design-unit GPOS advance; never a design-width approximation.
            const auto& raw = reference->batch->glyphs[descriptor];
            if (expected.glyph_id == 1U && options.script == open_type_tag{0x6C61746EU}) {
                const std::array<hinted_design_vector, 2U> design{{{3, 0}, {12, 0}}};
                std::array<hinted_outline_point, 2U> device{};
                require(project_hinted_design_vectors(*reference->batch, design, device,
                    hinted_projection_policy::scalar_reference).error == hinted_projection_error::none);
                require(expected.advance_x == raw.advance_x_26_6 + device[1].x_26_6 && expected.offset_x == device[0].x_26_6);
            }
            expected.advance_y = -expected.advance_y; expected.offset_y = -expected.offset_y;
            require(equal_glyph(retained.logical_glyphs[logical], expected) &&
                retained.logical_owners[logical] == hinted_paragraph_glyph_owner{
                    static_cast<std::uint32_t>(run_index), original_index, descriptor} &&
                retained.logical_font_indices[logical] == run.font_index && retained.logical_bidi_levels[logical] == run.bidi_level &&
                retained.logical_source_scales[logical] == source_style.scale &&
                retained.glyph_scales[logical] == configuration.logical_units_per_physical_pixel / 64.0F &&
                retained.item_metrics[logical].ascent == request.metrics[run.style_index].ascent &&
                retained.item_metrics[logical].descent == request.metrics[run.style_index].descent);
            ++logical;
        }
        covered += run.scalar_count;
    }
    require(covered == retained.shaping_input.size() && logical == retained.logical_glyphs.size() && fonts[0] && fonts[1]);
}

text_layout_options writer_options(const progpu_native_text_layout_options& wire) {
    text_layout_options result{};
    result.scale = wire.scale; result.maximum_width = wire.maximum_width; result.line_height = wire.line_height;
    result.maximum_lines = wire.maximum_lines; result.direction = static_cast<shaping_direction>(wire.direction);
    result.trimming = static_cast<text_trimming>(wire.trimming); result.alignment = static_cast<text_alignment>(wire.alignment);
    result.ellipsis_glyph_id = wire.ellipsis_glyph_id; result.ellipsis_advance = wire.ellipsis_advance;
    return result;
}

void verify_writer(const hinted_paragraph_generation& retained) {
    const auto options = writer_options(retained.layout);
    text_layout_requirements required{};
    require(try_get_scaled_text_layout_requirements(retained.logical_glyphs, retained.breaks_after,
        retained.glyph_scales, options, required));
    std::vector<positioned_text_glyph> glyphs(required.glyph_capacity + 1U);
    glyphs.back() = {0xFFFFU, 0xFFFEU, -71, 73.0F, 79.0F, 83.0F, 89.0F};
    const auto glyph_tail = glyphs.back();
    std::vector<positioned_text_line> lines(required.line_capacity + 1U);
    lines.back().glyph_start = 997U; lines.back().width = -991.0F;
    const auto line_tail = lines.back();
    std::vector<text_visual_cluster_group> groups(required.glyph_capacity);
    std::vector<std::uint32_t> indices(required.glyph_capacity);
    std::uint32_t glyph_count = 0U, line_count = 0U;
    require(try_layout_measured_logical_shaped_text(retained.logical_glyphs, retained.breaks_after,
        retained.logical_bidi_levels, retained.glyph_scales, retained.paragraph_level, options, {}, {}, {groups, indices},
        std::span(glyphs).first(required.glyph_capacity), std::span(lines).first(required.line_capacity), glyph_count, line_count,
        retained.justification_classes, retained.item_metrics));
    require(glyph_count == retained.glyphs.size() && line_count == retained.lines.size() &&
        std::memcmp(&glyphs.back(), &glyph_tail, sizeof(glyph_tail)) == 0 && lines.back().glyph_start == line_tail.glyph_start &&
        lines.back().width == line_tail.width);
    for (std::size_t index = 0U; index < glyph_count; ++index) {
        const auto& a = glyphs[index]; const auto& b = retained.glyphs[index];
        require(a.glyph_index == b.glyph_index && a.glyph_id == b.glyph_id && a.cluster == b.cluster && a.x == b.x && a.y == b.y &&
            a.advance_x == b.advance_x && a.advance_y == b.advance_y &&
            retained.positioned_owners[index] == retained.logical_owners[a.glyph_index] &&
            retained.cluster_ends[index] == retained.logical_cluster_ends[a.glyph_index]);
    }
    require(retained.line_frames.size() == line_count);
    double expected_top = 0.0;
    for (std::size_t index = 0U; index < line_count; ++index) {
        const auto& a = lines[index]; const auto& b = retained.lines[index];
        require(a.glyph_start == b.glyph_start && a.glyph_count == b.glyph_count && a.input_start == b.input_start &&
            a.input_end == b.input_end && a.width == b.width && a.baseline_y == b.baseline_y && a.height == b.height &&
            a.clipped == b.clipped && a.flags == b.flags);
        std::size_t first = retained.logical_glyphs.size(), last = 0U;
        for (std::size_t offset = 0U; offset < a.glyph_count; ++offset) {
            const auto logical = glyphs[a.glyph_start + offset].glyph_index;
            first = std::min(first, static_cast<std::size_t>(logical)); last = std::max(last, static_cast<std::size_t>(logical) + 1U);
        }
        require(last - first == a.glyph_count);
        float expected_ascent = 0.0F;
        for (std::size_t item = first; item < last; ++item)
            expected_ascent = std::max(expected_ascent, retained.item_metrics[item].ascent);
        require(retained.line_frames[index].measured && retained.line_frames[index].top == expected_top &&
            retained.line_frames[index].baseline_offset == expected_ascent &&
            a.baseline_y == static_cast<float>(expected_top + expected_ascent));
        expected_top += a.height;
        const auto logical_line = std::span(retained.logical_glyphs).subspan(first, last - first);
        std::vector<text_visual_cluster_group> visual_groups(a.glyph_count);
        std::vector<std::uint32_t> visual_indices(a.glyph_count);
        std::uint32_t visual_count = 0U;
        require(try_get_text_line_visual_indices(logical_line,
            std::span(retained.logical_bidi_levels).subspan(first, last - first), retained.paragraph_level,
            visual_groups, visual_indices, visual_count) && visual_count == a.glyph_count);
        // Separate original visual groups expose L1's actual trailing-space
        // levels before L2; never re-resolve bidi or copy pre-L1 logical levels.
        std::size_t visual = 0U;
        for (const auto& group : visual_groups) {
            if (visual == visual_count) break;
            require(group.glyph_count != 0U && group.glyph_count <= visual_count - visual);
            for (std::uint32_t inside = 0U; inside < group.glyph_count; ++inside, ++visual) {
                require(visual_indices[visual] == group.glyph_start + inside &&
                    retained.glyphs[a.glyph_start + visual].glyph_index == first + visual_indices[visual] &&
                    retained.bidi_levels[a.glyph_start + visual] == group.bidi_level);
            }
        }
        require(visual == visual_count);
        const auto excess = std::max(0.0F, options.maximum_width - a.width);
        auto origin = options.alignment == text_alignment::center ? excess * 0.5F :
            options.alignment == text_alignment::right ? excess : 0.0F;
        if ((a.flags & static_cast<std::uint8_t>(positioned_text_line_flags::right_to_left_justified)) != 0U) {
            std::size_t content_end = last;
            while (content_end > first && retained.justification_classes[content_end - 1U] != text_justification_class::content) --content_end;
            float trailing = 0.0F;
            for (std::size_t logical = content_end; logical < last; ++logical)
                trailing += static_cast<float>(retained.logical_glyphs[logical].advance_x) * retained.glyph_scales[logical];
            origin -= trailing;
        }
        require(retained.line_origins[index] == origin);
    }
    text_layout_metrics measured{};
    require(try_measure_measured_text_lines(std::span(lines).first(line_count), options.maximum_width, measured));
    require(retained.metrics.content_width == measured.content_width && retained.metrics.content_height == measured.content_height &&
        retained.metrics.measured_width == measured.measured_width && retained.metrics.measured_height == measured.measured_height);
    std::vector<std::int8_t> used_levels(required.glyph_capacity + 2U, -77);
    std::vector<float> used_origins(required.line_capacity + 2U, -717.0F);
    const text_layout_line_frame frame_tail{-717.0, -713.0F, false};
    std::vector<text_layout_line_frame> used_frames(required.line_capacity + 2U, frame_tail);
    font_error sink_error = font_error::verification_failed;
    require(try_layout_measured_logical_shaped_text_retained(retained.logical_glyphs, retained.breaks_after,
        retained.logical_bidi_levels, retained.glyph_scales, retained.paragraph_level, options, {}, {}, {groups, indices},
        std::span(glyphs).first(required.glyph_capacity), std::span(lines).first(required.line_capacity), glyph_count, line_count,
        retained.justification_classes, retained.item_metrics, {used_levels, used_origins, used_frames}, &sink_error) && sink_error == font_error::none);
    require(std::equal(retained.bidi_levels.begin(), retained.bidi_levels.end(), used_levels.begin()) &&
        std::equal(retained.line_origins.begin(), retained.line_origins.end(), used_origins.begin()));
    require(std::equal(retained.line_frames.begin(), retained.line_frames.end(), used_frames.begin()) &&
        std::all_of(used_frames.begin() + line_count, used_frames.end(), [&](const auto& frame) { return frame == frame_tail; }));
    require(std::all_of(used_levels.begin() + glyph_count, used_levels.end(), [](auto value) { return value == -77; }) &&
        std::all_of(used_origins.begin() + line_count, used_origins.end(), [](auto value) { return value == -717.0F; }));
    const auto saved_glyphs = glyphs; const auto saved_lines = lines; const auto saved_groups = groups; const auto saved_indices = indices;
    const auto saved_levels = used_levels; const auto saved_origins = used_origins;
    const auto saved_frames = used_frames;
    if (required.line_capacity > 1U) {
        require(!try_layout_measured_logical_shaped_text_retained(retained.logical_glyphs, retained.breaks_after,
            retained.logical_bidi_levels, retained.glyph_scales, retained.paragraph_level, options, {}, {}, {groups, indices},
            std::span(glyphs).first(required.glyph_capacity), std::span(lines).first(required.line_capacity), glyph_count, line_count,
            retained.justification_classes, retained.item_metrics,
            {used_levels, used_origins, std::span(used_frames).first(required.line_capacity - 1U)}, &sink_error) &&
            sink_error == font_error::insufficient_buffer && glyph_count == 0U && line_count == 0U &&
            used_frames == saved_frames && used_levels == saved_levels && used_origins == saved_origins &&
            std::memcmp(glyphs.data(), saved_glyphs.data(), glyphs.size() * sizeof(glyphs[0])) == 0 &&
            std::memcmp(lines.data(), saved_lines.data(), lines.size() * sizeof(lines[0])) == 0);
    }
    for (const bool short_levels : {false, true}) {
        require(required.glyph_capacity != 0U && required.line_capacity != 0U);
        glyph_count = 71U; line_count = 73U;
        const text_layout_retained_metadata short_sink{
            std::span(used_levels).first(short_levels ? required.glyph_capacity - 1U : required.glyph_capacity),
            std::span(used_origins).first(short_levels ? required.line_capacity : required.line_capacity - 1U)};
        require(!try_layout_measured_logical_shaped_text_retained(retained.logical_glyphs, retained.breaks_after,
            retained.logical_bidi_levels, retained.glyph_scales, retained.paragraph_level, options, {}, {}, {groups, indices},
            std::span(glyphs).first(required.glyph_capacity), std::span(lines).first(required.line_capacity), glyph_count, line_count,
            retained.justification_classes, retained.item_metrics, short_sink, &sink_error) &&
            sink_error == font_error::insufficient_buffer && glyph_count == 0U && line_count == 0U &&
            std::memcmp(glyphs.data(), saved_glyphs.data(), glyphs.size() * sizeof(glyphs[0])) == 0 &&
            std::memcmp(lines.data(), saved_lines.data(), lines.size() * sizeof(lines[0])) == 0 &&
            std::memcmp(groups.data(), saved_groups.data(), groups.size() * sizeof(groups[0])) == 0 && indices == saved_indices &&
            used_levels == saved_levels && used_origins == saved_origins);
    }
}

void verify_source_ends(const hinted_paragraph_generation& retained) {
    const auto hard_boundary = [](std::uint32_t code_point) {
        const auto kind = get_unicode_line_break_class(code_point);
        return kind == unicode_line_break_class::mandatory || kind == unicode_line_break_class::next_line ||
            kind == unicode_line_break_class::carriage_return || kind == unicode_line_break_class::line_feed;
    };
    for (std::size_t first = 0U; first < retained.logical_glyphs.size();) {
        std::size_t last = first + 1U;
        while (last < retained.logical_glyphs.size() && retained.logical_glyphs[last].cluster == retained.logical_glyphs[first].cluster) ++last;
        const auto cluster = retained.logical_glyphs[first].cluster;
        const auto start = std::find_if(retained.source_input.begin(), retained.source_input.end(),
            [&](const auto& scalar) { return scalar.input_index == static_cast<std::uint32_t>(cluster); });
        require(start != retained.source_input.end());
        const auto next = last == retained.logical_glyphs.size() ? std::uint64_t{UINT32_MAX} :
            static_cast<std::uint64_t>(retained.logical_glyphs[last].cluster);
        std::uint64_t end = 0U;
        const auto source_offset = static_cast<std::size_t>(start - retained.source_input.begin());
        const auto first_code_point = retained.shaping_input[source_offset].code_point;
        if (hard_boundary(first_code_point)) {
            end = static_cast<std::uint64_t>(start->input_index) + start->input_length;
            const auto lf = start + 1;
            if (first_code_point == '\r' && lf != retained.source_input.end() &&
                retained.shaping_input[source_offset + 1U].code_point == '\n' && lf->input_index < next)
                end = static_cast<std::uint64_t>(lf->input_index) + lf->input_length;
        } else {
            for (auto scalar = start; scalar != retained.source_input.end() && scalar->input_index < next; ++scalar) {
                const auto code_point = retained.shaping_input[static_cast<std::size_t>(scalar - retained.source_input.begin())].code_point;
                if (hard_boundary(code_point)) break;
                end = static_cast<std::uint64_t>(scalar->input_index) + scalar->input_length;
            }
        }
        require(end > static_cast<std::uint32_t>(cluster) && end <= INT32_MAX);
        for (auto index = first; index < last; ++index) require(retained.logical_cluster_ends[index] == static_cast<std::int32_t>(end));
        first = last;
    }
}

std::shared_ptr<hinted_paragraph_generation> unpack_interaction_fixture(const hinted_paragraph_generation& paragraph) {
    // Raw interaction-only fixture, NOT a copied/produced source generation.
    // Its shaping request stays default: never copy self-referential pointers.
    auto result = std::make_shared<hinted_paragraph_generation>();
    result->layout = paragraph.layout;
    result->logical_glyphs = paragraph.logical_glyphs; result->logical_owners = paragraph.logical_owners;
    result->logical_bidi_levels = paragraph.logical_bidi_levels; result->logical_cluster_ends = paragraph.logical_cluster_ends;
    result->logical_font_indices = paragraph.logical_font_indices; result->runs = paragraph.runs;
    result->glyphs = paragraph.glyphs; result->lines = paragraph.lines; result->positioned_owners = paragraph.positioned_owners;
    result->bidi_levels = paragraph.bidi_levels; result->cluster_ends = paragraph.cluster_ends; result->line_origins = paragraph.line_origins;
    return result;
}

void verify_interaction(std::shared_ptr<const hinted_paragraph_generation> paragraph) {
    const auto result = create_hinted_paragraph_interaction(paragraph);
    require(result.status == PROGPU_NATIVE_STATUS_SUCCESS && result.error == font_error::none &&
        result.generation != nullptr && result.generation->paragraph() == paragraph);
    std::vector<text_cluster_box> expected;
    double top = 0.0;
    for (std::size_t line_index = 0U; line_index < paragraph->lines.size(); ++line_index) {
        const auto& line = paragraph->lines[line_index];
        // A separately unpacked visual advance prefix owns interaction; ink
        // X/Y and baseline are not pen origins or measured row tops.
        std::vector<float> pen{paragraph->line_origins[line_index]};
        for (std::size_t offset = 0U; offset < line.glyph_count; ++offset)
            pen.push_back(pen.back() + paragraph->glyphs[line.glyph_start + offset].advance_x);
        for (std::size_t offset = 0U; offset < line.glyph_count;) {
            const auto first = offset;
            const auto cluster = paragraph->glyphs[line.glyph_start + offset].cluster;
            auto source_end = paragraph->cluster_ends[line.glyph_start + offset];
            do {
                source_end = std::max(source_end, paragraph->cluster_ends[line.glyph_start + offset]);
                ++offset;
            } while (offset < line.glyph_count && paragraph->glyphs[line.glyph_start + offset].cluster == cluster);
            const auto extrema = std::minmax_element(pen.begin() + static_cast<std::ptrdiff_t>(first),
                pen.begin() + static_cast<std::ptrdiff_t>(offset + 1U));
            expected.push_back({cluster, source_end, static_cast<std::uint32_t>(line_index),
                paragraph->bidi_levels[line.glyph_start + first], 0U, 0U, 0U,
                *extrema.first, static_cast<float>(top), *extrema.second - *extrema.first, line.height});
        }
        top += static_cast<double>(line.height);
    }
    require(expected.size() == result.generation->boxes().size() && result.generation->carets().size() <= expected.size() * 2U);
    std::vector<text_caret_stop> expected_carets;
    for (std::size_t index = 0U; index < expected.size(); ++index) {
        const auto& a = expected[index]; const auto& b = result.generation->boxes()[index];
        require(a.input_start == b.input_start && a.input_end == b.input_end && a.line_index == b.line_index &&
            a.bidi_level == b.bidi_level && a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height &&
            b.reserved0 == 0U && b.reserved1 == 0U && b.reserved2 == 0U);
        const bool rtl = (a.bidi_level & 1) != 0;
        for (const bool visual_right : {false, true}) {
            const text_caret_stop caret{(visual_right != rtl) ? a.input_end : a.input_start, a.line_index,
                visual_right ? a.x + a.width : a.x, a.y, a.height, a.bidi_level, visual_right != rtl, 0U, 0U};
            // Preserve the original adjacent-caret identity convention; this
            // is not a tolerance on independently unpacked geometry.
            if (!expected_carets.empty()) {
                const auto& previous = expected_carets.back();
                if (previous.input_position == caret.input_position && previous.trailing == caret.trailing &&
                    std::abs(previous.x - caret.x) < 0.0001F && std::abs(previous.y - caret.y) < 0.0001F) continue;
            }
            expected_carets.push_back(caret);
        }
    }
    require(expected_carets.size() == result.generation->carets().size());
    for (std::size_t index = 0U; index < expected_carets.size(); ++index) {
        const auto& a = expected_carets[index]; const auto& b = result.generation->carets()[index];
        require(a.input_position == b.input_position && a.line_index == b.line_index && a.x == b.x && a.y == b.y &&
            a.height == b.height && a.bidi_level == b.bidi_level && a.trailing == b.trailing && b.reserved0 == 0U && b.reserved1 == 0U);
    }
    // Test-only draw perturbation retains every source/run/descriptor and
    // advance. It is not a source factory, reshaping or font metric substitute.
    auto displaced = unpack_interaction_fixture(*paragraph);
    for (auto& glyph : displaced->glyphs) { glyph.x += 4096.0F; glyph.y -= 8192.0F; }
    const auto shifted = create_hinted_paragraph_interaction(displaced);
    require(shifted.status == PROGPU_NATIVE_STATUS_SUCCESS && shifted.generation->boxes().size() == expected.size() &&
        shifted.generation->carets().size() == result.generation->carets().size() &&
        std::memcmp(shifted.generation->boxes().data(), result.generation->boxes().data(), result.generation->boxes().size_bytes()) == 0 &&
        std::memcmp(shifted.generation->carets().data(), result.generation->carets().data(), result.generation->carets().size_bytes()) == 0);
    auto wrong = unpack_interaction_fixture(*paragraph);
    wrong->positioned_owners.front().descriptor_index = UINT32_MAX;
    require(create_hinted_paragraph_interaction(wrong).status == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(create_hinted_paragraph_interaction({}).status == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
}

void actual_paragraph_controls() {
    for (const auto policy : {font_hint_policy::truetype_35, font_hint_policy::truetype_40}) {
        fixture font;
        std::array<std::shared_ptr<const hinted_paragraph_generation>, 2U> generations;
        for (std::size_t source_bidi = 0U; source_bidi < generations.size(); ++source_bidi) {
            paragraph_request request(policy, source_bidi != 0U);
            generations[source_bidi] = produce(font, request);
            const auto& retained = *generations[source_bidi];
            require(retained.source_digit_bidi == (source_bidi != 0U));
            verify_raw_runs(font, request, retained); verify_source_ends(retained); verify_writer(retained);
            verify_interaction(generations[source_bidi]);
            for (const auto alignment : {1U, 2U, 3U}) {
                request.layout.alignment = alignment;
                request.layout.maximum_width = 7.0F;
                const auto aligned = produce(font, request);
                verify_writer(*aligned); verify_source_ends(*aligned); verify_interaction(aligned);
                require(alignment != 3U || aligned->justification_classes.size() == aligned->logical_glyphs.size());
            }
        }
        // Same substituted glyph scalars; the selected original-source versus
        // substituted-scalar bidi policies are genuinely distinct.
        require(generations[0]->scalar_levels[3].level == 2 && generations[0]->scalar_levels[4].level == 2 &&
            generations[1]->scalar_levels[3].level == 0 && generations[1]->scalar_levels[4].level == 0);
        for (std::size_t index = 0U; index < generations[0]->shaping_input.size(); ++index)
            require(generations[0]->shaping_input[index].code_point == generations[1]->shaping_input[index].code_point);

        paragraph_request hard(policy, true);
        const std::array<std::uint32_t, 8U> hard_values{'A', '\n', 'B', '\r', 'A', '\r', '\n', 'B'};
        hard.input = scalars(hard_values);
        for (std::size_t index = 1U; index < hard.input.size(); ++index) hard.input[index].input_index += 3U;
        hard.styles[0].scalar_count = 2U; hard.styles[1].scalar_start = 2U; hard.styles[1].scalar_count = 6U;
        hard.refresh();
        const auto complete = produce(font, hard);
        verify_source_ends(*complete); verify_writer(*complete); verify_interaction(complete);
        require(complete->logical_cluster_ends.front() == static_cast<std::int32_t>(hard.input.front().input_index + hard.input.front().input_length));
        for (const auto& glyph : complete->logical_glyphs)
            require(glyph.glyph_id != UINT32_MAX && glyph.glyph_id != UINT32_MAX - 1U); // no synthetic tab/object/caret glyph
        hard.layout.maximum_lines = 1U;
        const auto limited = produce(font, hard);
        verify_writer(*limited); verify_source_ends(*limited);
        require(limited->lines.size() == 1U && limited->glyphs.size() < complete->glyphs.size() &&
            limited->source_input.size() == complete->source_input.size() && limited->runs.size() == complete->runs.size() &&
            limited->logical_glyphs.size() == complete->logical_glyphs.size() && limited->logical_owners == complete->logical_owners);
        for (std::size_t index = 0U; index < limited->runs.size(); ++index)
            require(limited->runs[index].generation->source_descriptor_count == complete->runs[index].generation->source_descriptor_count);

        paragraph_request separator(policy, true);
        const std::array<std::uint32_t, 3U> separator_values{'A', 0x2028U, 'B'};
        separator.input = scalars(separator_values);
        separator.styles[0].scalar_count = 1U; separator.styles[1].scalar_start = 1U; separator.styles[1].scalar_count = 2U;
        separator.refresh();
        const auto separated = produce(font, separator);
        require(get_unicode_line_break_class(0x2028U) == unicode_line_break_class::mandatory &&
            separated->source_input[1].input_index == 11U && separated->source_input[1].input_length == 1U &&
            separated->logical_cluster_ends.front() == 11);
        verify_source_ends(*separated); verify_writer(*separated);

        paragraph_request contextual(policy, true);
        const std::array<std::uint32_t, 7U> context_values{'A', '0', 0x0627U, '0', 'A', '\n', '0'};
        contextual.input = scalars(context_values);
        contextual.styles[0].scalar_count = 3U; contextual.styles[1].scalar_start = 3U; contextual.styles[1].scalar_count = 4U;
        for (auto& style : contextual.styles) style.digit_substitution |= PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_CONTEXTUAL;
        contextual.refresh(); contextual.shaping.direction = PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT;
        contextual.layout.direction = PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT;
        const auto carried = produce(font, contextual);
        require(carried->shaping_input[1].code_point == '0' && carried->shaping_input[3].code_point == 0x0660U &&
            carried->shaping_input[6].code_point == 0x0660U);
        const auto reference = resolve_reference_bidi(contextual.input, contextual.styles, 1);
        for (std::size_t index = 0U; index < contextual.input.size(); ++index)
            require(carried->scalar_levels[index].level == reference.levels[index].level);
        verify_source_ends(*carried); verify_writer(*carried); verify_interaction(carried);
    }
}

void original_source_policy_controls() {
    for (const auto interpreter : {font_hint_policy::truetype_35, font_hint_policy::truetype_40}) {
        for (const auto direction : {PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT, PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT}) {
            fixture font;
            paragraph_request request(interpreter, true);
            request.shaping.direction = direction;
            request.layout.direction = direction;
            std::array<hinted_source_style, 2U> source{{
                {13.25, 1.5, hinted_source_em_policy::nearest_half_up, hinted_source_advance_policy::physical_ties_to_even},
                {std::nextafter(17.0, 18.0), 1.5, hinted_source_em_policy::nearest_half_up, hinted_source_advance_policy::physical_ties_to_even}}};
            const auto original_source = source;
            for (std::size_t i = 0U; i < source.size(); ++i) {
                auto& configuration = request.configurations[i];
                hinted_source_device_selection selected{};
                require(resolve_hinted_source_device(source[i], selected));
                require(selected.pixels_per_em_26_6 == (i == 0U ? 20U : 26U) * 64U);
                configuration.hinting.x_pixels_per_em_26_6 = selected.pixels_per_em_26_6;
                configuration.hinting.y_pixels_per_em_26_6 = selected.pixels_per_em_26_6;
                configuration.logical_units_per_physical_pixel = selected.logical_units_per_physical_pixel;
                request.styles[i].scale = static_cast<float>(source[i].em_size) / 1000.0F;
                configuration.source_scale = request.styles[i].scale;
            }
            // The independent raw producer has identical effective capture,
            // original text/font/features and no additional fitting policy.
            const auto raw = produce(font, request);
            verify_raw_runs(font, request, *raw);
            std::shared_ptr<const hinted_paragraph_generation> rounded;
            progpu_native_text_paragraph_result diagnostic{};
            require(try_layout_context_hinted_paragraph(font.context.value, request.shaping, request.layout,
                request.styles, request.metrics, request.configurations, rounded, diagnostic, source) == PROGPU_NATIVE_STATUS_SUCCESS);
            require(raw->source_styles.empty() && rounded->source_styles.size() == 2U &&
                rounded->source_styles[1].em_size == original_source[1].em_size &&
                rounded->source_styles[1].em_size != static_cast<double>(static_cast<float>(original_source[1].em_size)));
            require(rounded->logical_owners == raw->logical_owners && rounded->logical_bidi_levels == raw->logical_bidi_levels &&
                rounded->logical_cluster_ends == raw->logical_cluster_ends && rounded->breaks_after == raw->breaks_after &&
                rounded->runs.size() == raw->runs.size() && rounded->logical_glyphs.size() == raw->logical_glyphs.size());
            bool changed = false;
            for (std::size_t i = 0U; i < raw->logical_glyphs.size(); ++i) {
                auto expected = raw->logical_glyphs[i];
                const double pixels = static_cast<double>(expected.advance_x) / 64.0;
                double whole = std::floor(pixels);
                if (pixels - whole > 0.5 || (pixels - whole == 0.5 && std::fmod(whole, 2.0) != 0.0)) whole += 1.0;
                expected.advance_x = static_cast<std::int32_t>(whole * 64.0);
                require(equal_glyph(rounded->logical_glyphs[i], expected));
                changed |= expected.advance_x != raw->logical_glyphs[i].advance_x;
            }
            require(changed); // A real authored GPOS delta, not an integer-only no-op fixture.
            for (std::size_t i = 0U; i < rounded->runs.size(); ++i) {
                const auto& a = *raw->runs[i].generation;
                const auto& b = *rounded->runs[i].generation;
                require(a.batch->identity->source == b.batch->identity->source && a.batch->glyphs == b.batch->glyphs &&
                    a.descriptor_indices == b.descriptor_indices && a.glyphs.size() == b.glyphs.size());
                for (std::size_t j = 0U; j < a.glyphs.size(); ++j) require(equal_glyph(a.glyphs[j], b.glyphs[j]));
            }
            verify_writer(*rounded); verify_source_ends(*rounded); verify_interaction(rounded);
            const auto geometry = create_hinted_paragraph_glyph_resource(rounded, 1.5F,
                hinted_projection_policy::scalar_reference, hinted_outline_coverage::nonzero_vector);
            require(geometry.status == PROGPU_NATIVE_STATUS_SUCCESS && geometry.generation != nullptr);
            const auto previous = rounded;
            source[1].em_size = 19.0;
            require(try_layout_context_hinted_paragraph(font.context.value, request.shaping, request.layout,
                request.styles, request.metrics, request.configurations, rounded, diagnostic, source) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
                rounded == previous);
            source = original_source;
            source[1].advance_policy = static_cast<hinted_source_advance_policy>(99U);
            require(try_layout_context_hinted_paragraph(font.context.value, request.shaping, request.layout,
                request.styles, request.metrics, request.configurations, rounded, diagnostic, source) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
                rounded == previous);
            source = original_source;
            require(try_layout_context_hinted_paragraph(font.context.value, request.shaping, request.layout,
                request.styles, request.metrics, request.configurations, rounded, diagnostic, std::span(source).first(1U)) ==
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && rounded == previous);
            source.fill({});
            request.configurations.clear();
            progpu_native_text_context_destroy(font.context.value); font.context.value = nullptr;
            const auto reflow = reflow_hinted_paragraph(*rounded, rounded->lines.front().input_start, 80.0F);
            require(reflow.status == PROGPU_NATIVE_STATUS_SUCCESS && reflow.generation != nullptr &&
                reflow.generation->source_styles.size() == original_source.size());
            for (std::size_t i = 0U; i < original_source.size(); ++i) {
                const auto& retained = reflow.generation->source_styles[i];
                require(retained.em_size == original_source[i].em_size && retained.pixels_per_dip == original_source[i].pixels_per_dip &&
                    retained.em_policy == original_source[i].em_policy && retained.advance_policy == original_source[i].advance_policy);
            }
            require(reflow.generation->logical_owners == rounded->logical_owners);
            for (std::size_t i = 0U; i < rounded->runs.size(); ++i)
                require(reflow.generation->runs[i].generation == rounded->runs[i].generation);
            verify_writer(*reflow.generation); verify_interaction(reflow.generation);
        }
    }
}

void l1_and_rtl_origin_controls() {
    fixture font;
    for (const bool source_bidi : {false, true}) {
        for (const bool rtl : {false, true}) {
            paragraph_request request(font_hint_policy::truetype_40, source_bidi);
            const std::array<std::uint32_t, 6U> values{'A', 0x0627U, ' ', '1', ' ', 0x0627U};
            request.input = scalars(values); request.styles[0].scalar_count = 1U;
            request.styles[1].scalar_start = 1U; request.styles[1].scalar_count = 5U; request.refresh();
            request.shaping.direction = request.layout.direction = rtl ? PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT :
                PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
            const auto unwrapped = produce(font, request);
            require(unwrapped->logical_glyphs.size() == values.size());
            float prefix = 0.0F;
            for (std::size_t index = 0U; index < 5U; ++index)
                prefix += static_cast<float>(unwrapped->logical_glyphs[index].advance_x) * unwrapped->glyph_scales[index];
            const float last = static_cast<float>(unwrapped->logical_glyphs.back().advance_x) * unwrapped->glyph_scales.back();
            require(last > 0.0F && unwrapped->logical_glyphs[4].code_point == ' ' && unwrapped->logical_bidi_levels[4] == 1);
            request.layout.maximum_width = prefix + last * 0.5F;
            request.layout.alignment = rtl ? PROGPU_NATIVE_TEXT_ALIGNMENT_JUSTIFY : PROGPU_NATIVE_TEXT_ALIGNMENT_LEFT;
            const auto wrapped = produce(font, request);
            require(wrapped->lines.size() == 2U && wrapped->lines[0].glyph_count == 5U);
            verify_writer(*wrapped); verify_interaction(wrapped);
            const auto space = std::find_if(wrapped->glyphs.begin(), wrapped->glyphs.end(),
                [](const auto& glyph) { return glyph.glyph_index == 4U; });
            require(space != wrapped->glyphs.end());
            const auto position = static_cast<std::size_t>(space - wrapped->glyphs.begin());
            require(wrapped->logical_bidi_levels[4] == 1 && wrapped->bidi_levels[position] == (rtl ? 1 : 0));
            if (rtl) {
                const float trailing = static_cast<float>(wrapped->logical_glyphs[4].advance_x) * wrapped->glyph_scales[4];
                require(trailing > 0.0F && wrapped->line_origins[0] == -trailing &&
                    (wrapped->lines[0].flags & static_cast<std::uint8_t>(positioned_text_line_flags::right_to_left_justified)) != 0U);
            } else require(wrapped->line_origins[0] == 0.0F);
        }
    }
}

void mutation_and_retirement_controls() {
    std::shared_ptr<const hinted_paragraph_generation> paragraph;
    hinted_paragraph_interaction_result interaction;
    std::weak_ptr<const hinted_paragraph_generation> weak_paragraph;
    std::weak_ptr<const hinted_shaped_run> weak_run;
    std::weak_ptr<const owned_font_source> weak_font;
    {
        fixture font;
        paragraph_request request(font_hint_policy::truetype_35, true);
        std::array<progpu_native_text_scalar, 1U> pre{{{'A', 2U, 2U, 0U, 0U, 0U}}}, post{{{'A', 101U, 2U, 0U, 0U, 0U}}};
        request.shaping.pre_context = pre.data(); request.shaping.pre_context_count = 1U;
        request.shaping.post_context = post.data(); request.shaping.post_context_count = 1U;
        paragraph = produce(font, request);
        interaction = create_hinted_paragraph_interaction(paragraph);
        require(interaction.status == PROGPU_NATIVE_STATUS_SUCCESS);
        weak_paragraph = paragraph; weak_run = paragraph->runs.front().generation; weak_font = paragraph->font_sources.front();
        const auto source_before = paragraph->source_input;
        const auto glyphs_before = paragraph->glyphs;
        const auto styles_before = paragraph->styles;
        require(paragraph->shaping.pre_context == paragraph->pre_context.data() && paragraph->shaping.post_context == paragraph->post_context.data() &&
            paragraph->source_input.data() != request.input.data() && paragraph->features.data() != request.features.data());
        std::fill(request.input.begin(), request.input.end(), progpu_native_text_scalar{});
        std::fill(request.features.begin(), request.features.end(), progpu_native_text_feature{});
        std::fill(request.styles.begin(), request.styles.end(), progpu_native_text_style_run{});
        std::fill(request.metrics.begin(), request.metrics.end(), progpu_native_text_style_metrics{});
        std::fill(request.configurations.begin(), request.configurations.end(), hinted_paragraph_style_configuration{});
        request.layout.maximum_width = -1.0F; pre.fill({}); post.fill({});
        std::fill(font.bytes.begin(), font.bytes.end(), std::byte{});
        progpu_native_text_context_destroy(font.context.value); font.context.value = nullptr;
        require(std::memcmp(paragraph->source_input.data(), source_before.data(), source_before.size() * sizeof(source_before[0])) == 0 &&
            std::memcmp(paragraph->glyphs.data(), glyphs_before.data(), glyphs_before.size() * sizeof(glyphs_before[0])) == 0 &&
            std::memcmp(paragraph->styles.data(), styles_before.data(), styles_before.size() * sizeof(styles_before[0])) == 0 &&
            paragraph->source_metrics[0].ascent == 5.5F && paragraph->source_metrics[1].descent == 2.5F &&
            paragraph->device_styles[0].policy == font_hint_policy::truetype_35 && paragraph->pre_context[0].code_point == 'A' &&
            paragraph->post_context[0].input_index == 101U && paragraph->font_sources.front()->bytes == progpu::native::tests::make_hinted_shape_font());
        verify_writer(*paragraph); verify_interaction(paragraph);
    }
    paragraph.reset();
    require(!weak_paragraph.expired() && !weak_run.expired() && !weak_font.expired() && !interaction.generation->carets().empty());
    interaction.generation.reset();
    require(weak_paragraph.expired() && weak_run.expired() && weak_font.expired());
}

void atomic_failure_and_alias_controls() {
    fixture font;
    paragraph_request valid(font_hint_policy::truetype_40, true);
    auto result = produce(font, valid);
    const auto previous = result;
    const auto old_positions = result->glyphs;
    const auto reject = [&](paragraph_request& request) {
        progpu_native_text_paragraph_result diagnostic{}; diagnostic.struct_size = sizeof(diagnostic);
        require(try_layout_context_hinted_paragraph(font.context.value, request.shaping, request.layout, request.styles,
            request.metrics, request.configurations, result, diagnostic) != PROGPU_NATIVE_STATUS_SUCCESS && result == previous &&
            std::memcmp(result->glyphs.data(), old_positions.data(), old_positions.size() * sizeof(old_positions[0])) == 0);
    };
    for (unsigned int invalid = 0U; invalid < 15U; ++invalid) {
        paragraph_request request(font_hint_policy::truetype_40, true);
        if (invalid == 0U) request.configurations[1].font_index = 0U;
        if (invalid == 1U) request.configurations[1].source_scale = std::nextafter(request.styles[1].scale, 1.0F);
        if (invalid == 2U) request.configurations[1].logical_units_per_physical_pixel = 0.0F;
        if (invalid == 3U) request.configurations[1].logical_units_per_physical_pixel = std::numeric_limits<float>::denorm_min();
        if (invalid == 4U) request.configurations[1].hinting.x_phase_26_6 = 64U;
        if (invalid == 5U) request.configurations[1].hinting.policy = static_cast<font_hint_policy>(99U);
        if (invalid == 6U) request.metrics[1].ascent = std::numeric_limits<float>::quiet_NaN();
        if (invalid == 7U) request.metrics.pop_back();
        if (invalid == 8U) request.input[1].input_index = request.input[0].input_index;
        if (invalid == 9U) request.input.back().input_index = INT32_MAX;
        if (invalid == 10U) request.layout.trimming = PROGPU_NATIVE_TEXT_TRIMMING_CHARACTER_ELLIPSIS;
        if (invalid == 11U) request.shaping.direction = PROGPU_NATIVE_TEXT_DIRECTION_TOP_TO_BOTTOM;
        if (invalid == 12U) request.input[3].code_point = 9U;
        if (invalid == 13U) request.input[3].code_point = 0xFFFCU;
        if (invalid == 14U) request.layout.direction = PROGPU_NATIVE_TEXT_DIRECTION_TOP_TO_BOTTOM;
        // Mutating records does not invalidate their stable vector pointers.
        reject(request);
    }
    // The original styled entrypoint still admits its own synthetic trimming
    // and tab/object scalar input; this new seam adds no default policy change.
    for (const auto value : {std::uint32_t{9U}, std::uint32_t{0xFFFCU}}) {
        paragraph_request request(font_hint_policy::truetype_40, false);
        request.input[3].code_point = value;
        request.layout.trimming = PROGPU_NATIVE_TEXT_TRIMMING_CHARACTER_ELLIPSIS;
        progpu_native_text_paragraph_requirements required{}; required.struct_size = sizeof(required);
        require(progpu_native_text_context_get_styled_paragraph_requirements(font.context.value, &request.shaping,
            &request.layout, request.styles.data(), static_cast<std::uint32_t>(request.styles.size()), &required) == PROGPU_NATIVE_STATUS_SUCCESS);
    }
    for (const bool source_bidi : {false, true}) {
        for (const auto symbol : {std::uint32_t{'\n'}, std::uint32_t{'\r'}, std::uint32_t{9U}, std::uint32_t{0xFFFCU}}) {
            paragraph_request request(font_hint_policy::truetype_40, source_bidi);
            const std::array<std::uint32_t, 3U> values{'A', '%', 'B'};
            request.input = scalars(values); request.styles[0].scalar_count = 1U;
            request.styles[1].scalar_start = 1U; request.styles[1].scalar_count = 2U;
            for (auto& style : request.styles) style.percent = symbol;
            request.refresh();
            if (symbol == 9U || symbol == 0xFFFCU) { reject(request); continue; }
            const auto mapped = produce(font, request);
            require(mapped->source_input[1].code_point == '%' && mapped->shaping_input[1].code_point == symbol &&
                mapped->source_input[1].input_index == mapped->shaping_input[1].input_index &&
                mapped->source_input[1].input_length == mapped->shaping_input[1].input_length);
            verify_source_ends(*mapped); verify_writer(*mapped);
        }
    }
    const auto alias_reject = [&](progpu_native_text_paragraph_result* diagnostic) {
        std::array<std::byte, sizeof(progpu_native_text_paragraph_result)> saved{};
        std::memcpy(saved.data(), diagnostic, saved.size());
        require(try_layout_context_hinted_paragraph(font.context.value, valid.shaping, valid.layout, valid.styles,
            valid.metrics, valid.configurations, result, *diagnostic) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && result == previous &&
            std::memcmp(saved.data(), diagnostic, saved.size()) == 0);
    };
    alias_reject(reinterpret_cast<progpu_native_text_paragraph_result*>(valid.input.data()));
    alias_reject(reinterpret_cast<progpu_native_text_paragraph_result*>(valid.styles.data()));
    alias_reject(reinterpret_cast<progpu_native_text_paragraph_result*>(valid.configurations.data()));
    alias_reject(const_cast<progpu_native_text_paragraph_result*>(&previous->paragraph_result));
    alias_reject(reinterpret_cast<progpu_native_text_paragraph_result*>(const_cast<shaping_glyph*>(previous->logical_glyphs.data())));
    alias_reject(reinterpret_cast<progpu_native_text_paragraph_result*>(const_cast<std::byte*>(previous->font_sources[0]->bytes.data())));
    progpu_native_text_paragraph_result separate_diagnostic{};
    require(hinted_paragraph_publication_disjoint(&result, &separate_diagnostic));
    // The diagnostic object is larger than shared_ptr. Check the same actual
    // publication guard with pointers: binding such a reference already fails
    // UBSan in the caller, before the callee can reject the overlap.
    require(!hinted_paragraph_publication_disjoint(&result,
        reinterpret_cast<const progpu_native_text_paragraph_result*>(&result)) && result == previous);

    // Raw old-output capacity fixture owns actual shared runs, but does not
    // pretend its default shaping request is a copied source generation.
    auto capacity = unpack_interaction_fixture(*previous);
    capacity->shaping_input = previous->shaping_input;
    const auto used = capacity->shaping_input.size();
    capacity->shaping_input.resize(used + 16U);
    capacity->shaping_input.resize(capacity->shaping_input.capacity());
    for (std::size_t index = used; index < capacity->shaping_input.size(); ++index)
        capacity->shaping_input[index] = {static_cast<std::uint32_t>(sizeof(progpu_native_text_paragraph_result)), 71U, 1U};
    capacity->shaping_input.resize(used);
    std::shared_ptr<const hinted_paragraph_generation> capacity_result = capacity;
    auto* spare = reinterpret_cast<progpu_native_text_paragraph_result*>(capacity->shaping_input.data() + used);
    std::array<std::byte, sizeof(*spare)> spare_before{}; std::memcpy(spare_before.data(), spare, spare_before.size());
    require(try_layout_context_hinted_paragraph(font.context.value, valid.shaping, valid.layout, valid.styles, valid.metrics,
        valid.configurations, capacity_result, *spare) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && capacity_result == capacity &&
        std::memcmp(spare_before.data(), spare, spare_before.size()) == 0);
    std::array<std::int32_t, 32U> axes{};
    axes[0] = static_cast<std::int32_t>(sizeof(progpu_native_text_paragraph_result));
    valid.configurations[1].hinting.variation_coordinates_16_16 = axes;
    alias_reject(reinterpret_cast<progpu_native_text_paragraph_result*>(axes.data()));
    valid.configurations[1].hinting.variation_coordinates_16_16 = {};

    struct diagnostic_tail final {
        progpu_native_text_paragraph_result value{};
        std::array<std::uint32_t, 4U> tail{71U, 73U, 79U, 83U};
    } diagnostic;
    diagnostic.value.struct_size = sizeof(diagnostic.value);
    const auto saved_tail = diagnostic.tail;
    valid.input.push_back({UINT32_MAX, UINT32_MAX, 0U, 0U, 0U, UINT32_MAX});
    valid.refresh(); --valid.shaping.input_count; // Declared used input, with an invalid untouched caller tail.
    const auto input_tail = valid.input.back();
    require(try_layout_context_hinted_paragraph(font.context.value, valid.shaping, valid.layout, valid.styles, valid.metrics,
        valid.configurations, result, diagnostic.value) == PROGPU_NATIVE_STATUS_SUCCESS && diagnostic.tail == saved_tail &&
        std::memcmp(&valid.input.back(), &input_tail, sizeof(input_tail)) == 0 && result->source_input.size() + 1U == valid.input.size());
    const auto tail_success = result;
    valid.configurations[1].font_index = 0U;
    require(try_layout_context_hinted_paragraph(font.context.value, valid.shaping, valid.layout, valid.styles, valid.metrics,
        valid.configurations, result, diagnostic.value) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && result == tail_success && diagnostic.tail == saved_tail &&
        std::memcmp(&valid.input.back(), &input_tail, sizeof(input_tail)) == 0);
    const auto empty_shape = shape_request({});
    const auto empty_layout = layout_options();
    std::shared_ptr<const hinted_paragraph_generation> empty;
    require(try_layout_context_hinted_paragraph(font.context.value, empty_shape, empty_layout, {}, {}, {}, empty,
        diagnostic.value) == PROGPU_NATIVE_STATUS_SUCCESS && empty != nullptr && empty->source_input.empty() &&
        empty->logical_glyphs.empty() && empty->glyphs.empty() && empty->lines.empty() && diagnostic.tail == saved_tail);
    const auto empty_interaction = create_hinted_paragraph_interaction(empty);
    require(empty_interaction.status == PROGPU_NATIVE_STATUS_SUCCESS && empty_interaction.generation->boxes().empty() &&
        empty_interaction.generation->carets().empty()); // no manufactured empty row/stop
}
#endif
} // namespace

int main() {
    try {
#if defined(PROGPU_NATIVE_FONT_HINTING)
        actual_paragraph_controls(); original_source_policy_controls(); l1_and_rtl_origin_controls();
        mutation_and_retirement_controls(); atomic_failure_and_alias_controls();
#else
        const auto bytes = progpu::native::tests::make_hinted_shape_font();
        progpu_native_text_context* context = nullptr;
        require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
            reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U, nullptr, 0U, &context) == PROGPU_NATIVE_STATUS_SUCCESS);
        progpu_native_text_shape_request shaping{}; shaping.struct_size = sizeof(shaping); shaping.abi_version = PROGPU_NATIVE_ABI_VERSION;
        progpu_native_text_layout_options layout{}; layout.struct_size = sizeof(layout); layout.scale = 1.0F;
        std::shared_ptr<const hinted_paragraph_generation> result;
        progpu_native_text_paragraph_result diagnostic{}; diagnostic.struct_size = sizeof(diagnostic);
        const auto status = try_layout_context_hinted_paragraph(context, shaping, layout, {}, {}, {}, result, diagnostic);
        progpu_native_text_context_destroy(context);
        require(status == PROGPU_NATIVE_STATUS_UNSUPPORTED && result == nullptr);
#endif
        std::cout << "retained hinted paragraph CPU controls passed\n"; return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n'; return 1;
    }
}
