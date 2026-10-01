#include "progpu_native_text_hinting.h"
#include "progpu_native_hinted_shape_fixture.hpp"
#include "progpu_native_hinted_transport_controls.hpp"
#include "../src/Text/Font/progpu_native_hinted_shaper.hpp"
#include "../src/Text/Interop/progpu_native_text_font_source.hpp"

#include <array>
#include <cstring>
#include <iostream>
#include <limits>
#include <vector>

namespace {
using progpu::native::text::tests::transport_require;
struct context_owner final {
    progpu_native_text_context* value = nullptr;
    ~context_owner() { progpu_native_text_context_destroy(value); }
};
struct run_owner final {
    progpu_native_hinted_run* value = nullptr;
    ~run_owner() { progpu_native_hinted_run_destroy(value); }
};

progpu_native_text_shape_request shape_request(std::span<const progpu_native_text_scalar> input,
    std::span<const progpu_native_text_feature> features, std::uint32_t direction) {
    progpu_native_text_shape_request result{};
    result.struct_size = sizeof(result); result.abi_version = PROGPU_NATIVE_ABI_VERSION;
    result.flags = PROGPU_NATIVE_TEXT_SHAPE_ZERO_MARK_ADVANCES;
    result.input = input.data(); result.input_count = static_cast<std::uint32_t>(input.size());
    result.features = features.data(); result.feature_count = static_cast<std::uint32_t>(features.size());
    result.unicode_script = 0x6C61746EU; result.direction = direction; result.alternate_value = 1U;
    return result;
}

template<class T>
bool same(const std::vector<T>& left, const std::vector<T>& right) {
    return left.size() == right.size() && (left.empty() || std::memcmp(left.data(), right.data(), left.size() * sizeof(T)) == 0);
}

void verify_boundary(std::uint32_t interpreter) {
    using namespace progpu::native::text;
    const auto bytes = progpu::native::tests::make_hinted_shape_font();
    context_owner context;
    transport_require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
        reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U, nullptr, 0U, &context.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    const auto source = select_context_font_source(context.value, 0U);
    progpu_native_hinted_font_request hint{PROGPU_NATIVE_ABI_VERSION, sizeof(progpu_native_hinted_font_request),
        0U, 13U * 64U, 13U * 64U, interpreter, 0U, 0U, 0U, 0U};
    const std::array input{progpu_native_text_scalar{0x42U, 0U, 1U, 0U, 0U, 0U},
        progpu_native_text_scalar{0x41U, 1U, 1U, 0U, 0U, 0U}, progpu_native_text_scalar{0x42U, 2U, 1U, 0U, 0U, 0U}};
    const std::array features{progpu_native_text_feature{0x6C696761U, 1U, 0U, 0xFFFFFFFFU},
        progpu_native_text_feature{0x6B65726EU, 1U, 0U, 0xFFFFFFFFU}};
    auto request = shape_request(input, features, PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT);
    auto* sentinel = reinterpret_cast<progpu_native_hinted_run*>(std::uintptr_t{1U});
    auto* unchanged = sentinel;
    auto invalid = request; invalid.font_data = reinterpret_cast<const std::uint8_t*>(bytes.data()); invalid.font_size = bytes.size();
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &invalid, &unchanged) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && unchanged == sentinel);
    invalid = request; invalid.face_index = 1U;
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &invalid, &unchanged) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && unchanged == sentinel);
    invalid = request; invalid.normalization_data = reinterpret_cast<const std::uint8_t*>(bytes.data()); invalid.normalization_data_size = bytes.size();
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &invalid, &unchanged) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && unchanged == sentinel);
    invalid = request; invalid.input = nullptr;
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &invalid, &unchanged) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && unchanged == sentinel);
    invalid = request; invalid.normalized_coordinate_count = 1U;
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &invalid, &unchanged) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && unchanged == sentinel);
    invalid = request; invalid.flags = 0x80000000U;
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &invalid, &unchanged) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && unchanged == sentinel);
    invalid = request; invalid.direction = 5U;
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &invalid, &unchanged) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && unchanged == sentinel);
    auto malformed_hint = hint; malformed_hint.interpreter = 0U;
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &malformed_hint, nullptr, &request, &unchanged) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && unchanged == sentinel);
    malformed_hint = hint; malformed_hint.variation_count = 1U;
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &malformed_hint, nullptr, &request, &unchanged) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && unchanged == sentinel);
    invalid = request; invalid.input = reinterpret_cast<const progpu_native_text_scalar*>(std::numeric_limits<std::uintptr_t>::max() &
        ~(std::uintptr_t{alignof(progpu_native_text_scalar)} - 1U));
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &invalid, &unchanged) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && unchanged == sentinel);
    const auto before_hint = hint;
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &request,
        reinterpret_cast<progpu_native_hinted_run**>(&hint)) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && std::memcmp(&hint, &before_hint, sizeof(hint)) == 0);
    const auto before_request = request;
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &request,
        reinterpret_cast<progpu_native_hinted_run**>(&request)) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
        std::memcmp(&request, &before_request, sizeof(request)) == 0);
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &request,
        reinterpret_cast<progpu_native_hinted_run**>(context.value)) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    const auto source_bytes = source->bytes;
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &request,
        reinterpret_cast<progpu_native_hinted_run**>(const_cast<std::byte*>(source->bytes.data()))) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
        source->bytes == source_bytes);
#if !defined(PROGPU_NATIVE_FONT_HINTING)
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &request, &unchanged) ==
        PROGPU_NATIVE_STATUS_UNSUPPORTED && unchanged == sentinel);
#else
    const hinted_font_configuration configuration{hint.x_pixels_per_em_26_6, hint.y_pixels_per_em_26_6,
        static_cast<font_hint_policy>(interpreter), 0U, 0U, {}};
    const std::array<std::uint32_t, 3U> ids{1U, 1U, 1U};
    std::shared_ptr<const hinted_glyph_batch> reference;
    hinted_font_error capture_error{};
    transport_require(capture_context_hinted(context.value, 0U, configuration, ids, reference, capture_error));
    const std::array<hinted_design_vector, 2U> design{{{3, 0}, {12, 0}}};
    std::array<hinted_outline_point, 2U> projected{};
    transport_require(project_hinted_design_vectors(*reference, design, projected, hinted_projection_policy::scalar_reference).error == hinted_projection_error::none);
    run_owner retained;
    for (const std::uint32_t direction : {static_cast<std::uint32_t>(PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT),
        static_cast<std::uint32_t>(PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT), static_cast<std::uint32_t>(PROGPU_NATIVE_TEXT_DIRECTION_TOP_TO_BOTTOM),
        static_cast<std::uint32_t>(PROGPU_NATIVE_TEXT_DIRECTION_BOTTOM_TO_TOP)}) {
        for (const bool ranged : {false, true}) {
            const std::array ranged_features{features[0U], progpu_native_text_feature{0x6B65726EU, 0U, 0U, 0xFFFFFFFFU},
                progpu_native_text_feature{0x6B65726EU, 1U, 1U, 2U}};
            request = shape_request(input, ranged ? std::span<const progpu_native_text_feature>(ranged_features) : std::span<const progpu_native_text_feature>(features), direction);
            for (const bool verify : {false, true}) {
                request.buffer_flags = verify ? static_cast<std::uint32_t>(PROGPU_NATIVE_TEXT_BUFFER_VERIFY) : 0U;
                auto* candidate = sentinel;
                transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &request, &candidate) == PROGPU_NATIVE_STATUS_SUCCESS);
                progpu_native_hinted_run_destroy(retained.value); retained.value = candidate;
                std::uint32_t count = 91U;
                progpu_native_hinted_batch_counts outline_counts{92U, 93U, 94U};
                transport_require(progpu_native_hinted_run_get_counts(retained.value, &count, &outline_counts) == PROGPU_NATIVE_STATUS_SUCCESS &&
                    count == 3U && outline_counts.glyphs == 3U && outline_counts.points == 12U && outline_counts.contours == 3U);
                std::vector<progpu_native_text_shaping_glyph> glyphs(5U);
                std::vector<std::uint32_t> mapping(5U, 0xA5A5A5A5U);
                std::memset(glyphs.data(), 0xA5, glyphs.size() * sizeof(glyphs[0U]));
                const auto glyph_tail = glyphs.back();
                transport_require(progpu_native_hinted_run_copy_glyphs(retained.value, glyphs.data(), static_cast<std::uint32_t>(glyphs.size()),
                    mapping.data(), static_cast<std::uint32_t>(mapping.size())) == PROGPU_NATIVE_STATUS_SUCCESS);
                const bool vertical = direction == PROGPU_NATIVE_TEXT_DIRECTION_TOP_TO_BOTTOM || direction == PROGPU_NATIVE_TEXT_DIRECTION_BOTTOM_TO_TOP;
                const bool backward = direction == PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT || direction == PROGPU_NATIVE_TEXT_DIRECTION_BOTTOM_TO_TOP;
                const auto generation = select_hinted_run_generation(retained.value);
                transport_require(generation->shaping_input.size() == input.size());
                for (std::size_t index = 0U; index < input.size(); ++index)
                    transport_require(generation->shaping_input[index].code_point == input[index].code_point &&
                        generation->shaping_input[index].input_index == input[index].input_index &&
                        generation->shaping_input[index].input_length == input[index].input_length);
                for (std::size_t index = 0U; index < 3U; ++index) {
                    const auto original = backward ? 2U - index : index;
                    const auto& raw = reference->glyphs[original];
                    const auto& glyph = glyphs[index];
                    // Public directional feature planning omits horizontal
                    // kern for vertical runs; direct private controls exercise
                    // explicit GPOS in every direction independently.
                    const bool positioned = !vertical && (!ranged || original == 1U);
                    const auto expected_advance_x = (vertical ? 0L : raw.advance_x_26_6) + (positioned ? projected[1U].x_26_6 : 0L);
                    const auto expected_advance_y = vertical ? raw.vertical_advance_26_6 : -raw.advance_y_26_6;
                    const auto expected_offset_x = (vertical ? raw.vertical_bearing_x_26_6 - raw.horizontal_bearing_x_26_6 : 0L) +
                        (positioned ? projected[0U].x_26_6 : 0L);
                    const auto expected_offset_y = vertical ? raw.horizontal_bearing_y_26_6 + raw.vertical_bearing_y_26_6 : 0L;
                    const auto expected_flags = static_cast<std::uint32_t>(generation->glyphs[index].flags);
                    const bool matches = glyph.glyph_id == 1U && glyph.code_point == input[original].code_point && glyph.cluster == static_cast<std::int32_t>(original) &&
                        mapping[index] == original && glyph.flags == static_cast<std::uint32_t>(generation->glyphs[index].flags) &&
                        glyph.advance_x == expected_advance_x && glyph.advance_y == expected_advance_y &&
                        glyph.offset_x == expected_offset_x && glyph.offset_y == expected_offset_y;
                    if (!matches) std::cerr << "Hinted run glyph mismatch: interpreter=" << interpreter << " direction=" << direction
                        << " ranged=" << ranged << " verify=" << verify << " index=" << index << " original=" << original
                        << " glyph_id=" << glyph.glyph_id << "/1 code_point=" << glyph.code_point << '/' << input[original].code_point
                        << " cluster=" << glyph.cluster << '/' << original << " descriptor=" << mapping[index] << '/' << original
                        << " flags=" << glyph.flags << '/' << expected_flags << " advance_x=" << glyph.advance_x << '/' << expected_advance_x
                        << " advance_y=" << glyph.advance_y << '/' << expected_advance_y << " offset_x=" << glyph.offset_x << '/' << expected_offset_x
                        << " offset_y=" << glyph.offset_y << '/' << expected_offset_y << '\n';
                    transport_require(matches);
                }
                for (std::size_t index = 3U; index < 5U; ++index) transport_require(std::memcmp(&glyphs[index], &glyph_tail, sizeof(glyph_tail)) == 0 && mapping[index] == 0xA5A5A5A5U);
                const auto before_glyphs = glyphs; const auto before_mapping = mapping;
                const auto admitted_input = generation->shaping_input;
                auto* admitted_storage = const_cast<unicode_scalar*>(generation->shaping_input.data());
                transport_require(progpu_native_hinted_run_copy_glyphs(retained.value, glyphs.data(), 5U,
                    reinterpret_cast<std::uint32_t*>(admitted_storage), 3U) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
                    same(glyphs, before_glyphs) && mapping == before_mapping && same(generation->shaping_input, admitted_input));
                transport_require(progpu_native_hinted_run_get_counts(retained.value,
                    reinterpret_cast<std::uint32_t*>(admitted_storage), &outline_counts) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
                    outline_counts.glyphs == 3U && same(generation->shaping_input, admitted_input));
                transport_require(progpu_native_hinted_run_copy_glyphs(retained.value, glyphs.data(), 2U, mapping.data(), 5U) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
                    same(glyphs, before_glyphs) && mapping == before_mapping);
                transport_require(progpu_native_hinted_run_copy_glyphs(retained.value, glyphs.data(), 5U, mapping.data(), 2U) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
                    same(glyphs, before_glyphs) && mapping == before_mapping);
                transport_require(progpu_native_hinted_run_copy_glyphs(retained.value, glyphs.data(), 5U,
                    reinterpret_cast<std::uint32_t*>(&glyphs[3U]), 3U) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && same(glyphs, before_glyphs));
                transport_require(progpu_native_hinted_run_copy_glyphs(retained.value,
                    reinterpret_cast<progpu_native_text_shaping_glyph*>(retained.value), 3U, mapping.data(), 5U) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && mapping == before_mapping);
                transport_require(progpu_native_hinted_run_copy_glyphs(retained.value,
                    reinterpret_cast<progpu_native_text_shaping_glyph*>(const_cast<shaping_glyph*>(generation->glyphs.data())), 3U,
                    mapping.data(), 5U) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && mapping == before_mapping);
                transport_require(generation->glyphs.capacity() >= generation->glyphs.size() + 3U);
                transport_require(progpu_native_hinted_run_copy_glyphs(retained.value,
                    reinterpret_cast<progpu_native_text_shaping_glyph*>(const_cast<shaping_glyph*>(generation->glyphs.data() + generation->glyphs.size())), 3U,
                    mapping.data(), 5U) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && mapping == before_mapping);
                transport_require(progpu_native_hinted_run_get_counts(retained.value, &count,
                    reinterpret_cast<progpu_native_hinted_batch_counts*>(&count)) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && count == 3U);
                transport_require(progpu_native_hinted_run_get_counts(retained.value,
                    reinterpret_cast<std::uint32_t*>(retained.value), &outline_counts) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && outline_counts.glyphs == 3U);
                std::vector<progpu_native_hinted_glyph> outlines(5U), expected_outlines(5U);
                std::vector<progpu_native_hinted_point> points(14U), expected_points(14U);
                std::vector<std::uint8_t> tags(14U, 0xA5U), expected_tags(tags);
                std::vector<std::int32_t> contours(5U, -77), expected_contours(contours);
                std::memset(outlines.data(), 0xA5, outlines.size() * sizeof(outlines[0U])); expected_outlines = outlines;
                std::memset(points.data(), 0xA5, points.size() * sizeof(points[0U])); expected_points = points;
                transport_require(progpu_native_hinted_run_copy_outlines(retained.value, outlines.data(), 5U, points.data(), 14U, tags.data(), 14U, contours.data(), 5U) == PROGPU_NATIVE_STATUS_SUCCESS);
                transport_require(static_cast<bool>(copy_hinted_batch(*reference, expected_outlines, expected_points, expected_tags, expected_contours, hinted_transport_policy::scalar_reference)) &&
                    same(outlines, expected_outlines) && same(points, expected_points) && tags == expected_tags && contours == expected_contours);
                transport_require(progpu_native_hinted_run_copy_outlines(retained.value, outlines.data(), 5U, points.data(), 14U,
                    reinterpret_cast<std::uint8_t*>(admitted_storage), 14U, contours.data(), 5U) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
                    same(outlines, expected_outlines) && same(points, expected_points) && tags == expected_tags &&
                    contours == expected_contours && same(generation->shaping_input, admitted_input));
                transport_require(progpu_native_hinted_run_copy_outlines(retained.value, outlines.data(), 5U, points.data(), 11U, tags.data(), 14U, contours.data(), 5U) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
                    same(outlines, expected_outlines) && same(points, expected_points) && tags == expected_tags && contours == expected_contours);
                transport_require(progpu_native_hinted_run_copy_outlines(retained.value, outlines.data(), 5U, points.data(), 14U,
                    reinterpret_cast<std::uint8_t*>(&points[12U]), 12U, contours.data(), 5U) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
                    same(outlines, expected_outlines) && same(points, expected_points) && contours == expected_contours);
            }
        }
    }
    const auto saved = retained.value;
    const std::array disabled_liga{progpu_native_text_feature{0x6C696761U, 0U, 0U, 0xFFFFFFFFU}};
    request = shape_request(input, disabled_liga, PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT);
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &request, &retained.value) == PROGPU_NATIVE_STATUS_INTERNAL_ERROR && retained.value == saved);
    auto invalid_hint = hint; invalid_hint.font_index = 99U;
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &invalid_hint, nullptr, &request, &retained.value) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && retained.value == saved);
    invalid = request; invalid.reserved0 = 1U;
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &invalid, &retained.value) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && retained.value == saved);
    run_owner empty;
    request = shape_request({}, features, PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT);
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &request, &empty.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    std::uint32_t empty_count = 91U; progpu_native_hinted_batch_counts empty_outlines{92U, 93U, 94U};
    transport_require(progpu_native_hinted_run_get_counts(empty.value, &empty_count, &empty_outlines) == PROGPU_NATIVE_STATUS_SUCCESS &&
        empty_count == 0U && empty_outlines.glyphs == 0U && empty_outlines.points == 0U && empty_outlines.contours == 0U);
    transport_require(progpu_native_hinted_run_copy_glyphs(empty.value, nullptr, 0U, nullptr, 0U) == PROGPU_NATIVE_STATUS_SUCCESS &&
        progpu_native_hinted_run_copy_outlines(empty.value, nullptr, 0U, nullptr, 0U, nullptr, 0U, nullptr, 0U) == PROGPU_NATIVE_STATUS_SUCCESS);
    const std::array<progpu_native_text_scalar, 3U> spaces{progpu_native_text_scalar{0x41U, 0U, 1U, 0U, 0U, 0U},
        progpu_native_text_scalar{0x2007U, 1U, 1U, 0U, 0U, 0U}, progpu_native_text_scalar{0x2008U, 2U, 1U, 0U, 0U, 0U}};
    request = shape_request(spaces, features, PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT);
    run_owner auxiliary;
    transport_require(progpu_native_text_context_shape_hinted_run(context.value, &hint, nullptr, &request, &auxiliary.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    std::uint32_t count = 0U; progpu_native_hinted_batch_counts outline_counts{};
    transport_require(progpu_native_hinted_run_get_counts(auxiliary.value, &count, &outline_counts) == PROGPU_NATIVE_STATUS_SUCCESS && count == 3U && outline_counts.glyphs == 5U);
    const auto generation = select_hinted_run_generation(auxiliary.value);
    transport_require(generation->shaping_input.size() == spaces.size());
    transport_require(generation->figure_descriptor_start == 3U && generation->punctuation_descriptor_start == 4U);
    for (const auto& glyph : generation->batch->glyphs) transport_require(glyph.glyph_index == 1U); // Unused digit/comma fault2 never captured.
    progpu_native_text_context_destroy(context.value); context.value = nullptr;
    transport_require(progpu_native_hinted_run_get_counts(retained.value, &count, &outline_counts) == PROGPU_NATIVE_STATUS_SUCCESS && count == 3U && outline_counts.glyphs == 3U);
    std::array<progpu_native_text_shaping_glyph, 3U> after_retirement{}; std::array<std::uint32_t, 3U> after_mapping{};
    transport_require(progpu_native_hinted_run_copy_glyphs(auxiliary.value, after_retirement.data(), 3U, after_mapping.data(), 3U) == PROGPU_NATIVE_STATUS_SUCCESS &&
        after_mapping == std::array<std::uint32_t, 3U>{2U, 1U, 0U} && generation->batch->identity->source == source);
    for (std::size_t index = 0U; index < spaces.size(); ++index)
        transport_require(generation->shaping_input[index].code_point == spaces[index].code_point &&
            generation->shaping_input[index].input_index == spaces[index].input_index &&
            generation->shaping_input[index].input_length == spaces[index].input_length);
    tests::verify_hinted_transport(*generation->batch);
#endif
}
}

int main() {
    try {
        verify_boundary(35U); verify_boundary(40U);
        progpu_native_hinted_run_destroy(nullptr);
        std::uint32_t count = 77U; progpu_native_hinted_batch_counts outlines{78U, 79U, 80U};
        transport_require(progpu_native_hinted_run_get_counts(nullptr, &count, &outlines) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && count == 77U && outlines.glyphs == 78U);
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n'; return 1;
    }
}
