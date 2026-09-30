#include "../src/Text/Interop/progpu_native_text_font_source.hpp"
#include "progpu_native_hint_fault_fixture.hpp"
#include "progpu_native_hinted_transport_controls.hpp"
#include "progpu_native_hinted_gpos.hpp"
#include "progpu_native_hinted_run_metrics.hpp"
#include "progpu_native_hinted_shaper.hpp"
#include "progpu_native_hinted_shape_fixture.hpp"
#include "../src/Text/Shaping/progpu_native_open_type_device_shaper_internal.hpp"

#include <algorithm>
#include <array>
#include <cstring>
#include <iostream>
#include <vector>
#include <limits>

namespace {
struct batch_owner final {
    progpu_native_hinted_batch* value = nullptr;
    ~batch_owner() { progpu_native_hinted_batch_destroy(value); }
};

#if defined(PROGPU_NATIVE_FONT_HINTING)
void verify_shaping_input(const progpu::native::text::hinted_shaped_run& run,
    std::span<const progpu::native::text::unicode_scalar> admitted) {
    using progpu::native::text::tests::transport_require;
    transport_require(run.shaping_input.size() == admitted.size());
    for (std::size_t index = 0U; index < admitted.size(); ++index) {
        const auto& expected = admitted[index];
        const auto& actual = run.shaping_input[index];
        transport_require(actual.code_point == expected.code_point && actual.input_index == expected.input_index &&
            actual.input_length == expected.input_length && actual.canonical_combining_class == expected.canonical_combining_class &&
            actual.reserved == expected.reserved && actual.script == expected.script);
    }
}

void verify_shaping_map(std::span<const std::byte> bytes) {
    using namespace progpu::native::text;
    sfnt_font_view font{};
    tests::transport_require(sfnt_font_view::try_create(bytes, 0U, font));
    for (std::uint32_t code_point = 0U; code_point < 256U; ++code_point) {
        const std::uint16_t expected = code_point == 0x20U || code_point == 0x2EU ||
            code_point == 0x30U || code_point == 0x41U ? std::uint16_t{1U} :
            code_point == 0x2CU || code_point == 0x31U || code_point == 0x42U ? std::uint16_t{2U} : std::uint16_t{0U};
        std::uint16_t actual = 0xFFFFU;
        tests::transport_require(font.try_get_glyph_index(code_point, actual) && actual == expected);
    }
    for (const std::uint32_t code_point : {0x2007U, 0x2008U, 0xFFFFU}) {
        std::uint16_t actual = 0xFFFFU;
        tests::transport_require(font.try_get_glyph_index(code_point, actual) && actual == 0U);
    }
}

void verify_owned_shaping(const progpu::native::text::hinted_font_configuration& configuration) {
    using namespace progpu::native::text;
    using tests::transport_require;
    struct context_owner final {
        progpu_native_text_context* value = nullptr;
        ~context_owner() { progpu_native_text_context_destroy(value); }
    } context;
    const auto bytes = progpu::native::tests::make_hinted_shape_font();
    verify_shaping_map(bytes);
    transport_require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
        reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U,
        nullptr, 0U, &context.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    const auto source = select_context_font_source(context.value, 0U);
    const auto latin = open_type_tag::from_chars('l', 'a', 't', 'n');
    const auto liga = open_type_tag::from_chars('l', 'i', 'g', 'a');
    const auto kern = open_type_tag::from_chars('k', 'e', 'r', 'n');
    const std::array features{liga, kern};
    const std::array<unicode_scalar, 3U> input{unicode_scalar{0x42U, 0U, 1U},
        unicode_scalar{0x41U, 1U, 1U}, unicode_scalar{0x42U, 2U, 1U}};
    std::shared_ptr<const hinted_shaped_run> retained;
    hinted_shape_error error{};
    for (const auto policy : {hinted_projection_policy::automatic, hinted_projection_policy::scalar_reference,
        hinted_projection_policy::intrinsic_simd}) {
        for (const auto direction : {shaping_direction::left_to_right, shaping_direction::right_to_left,
            shaping_direction::top_to_bottom, shaping_direction::bottom_to_top}) {
            auto options = open_type_shape_run_options{};
            options.script = latin; options.direction = direction; options.requested_features = features;
            for (const bool ranged : {false, true}) {
                const std::array values{shaping_feature{kern, 0U, 0U, 0xFFFFFFFFU}, shaping_feature{kern, 1U, 1U, 2U}};
                options.feature_settings = ranged ? std::span<const shaping_feature>(values) : std::span<const shaping_feature>{};
                error.resource_exhausted = true; // A previous diagnostic never survives a new successful request.
                const bool shaped = try_shape_context_hinted(context.value, 0U, configuration, input, options, retained, error, policy);
                if (!shaped) std::cerr << "Owned hinted shaping failed: interpreter=" << static_cast<std::uint32_t>(configuration.policy)
                    << " direction=" << static_cast<std::uint32_t>(direction) << " projection_policy=" << static_cast<std::uint32_t>(policy)
                    << " ranged=" << ranged << " shaping=" << static_cast<std::uint32_t>(error.shaping)
                    << " capture=" << static_cast<std::uint32_t>(error.capture) << " projection=" << static_cast<std::uint32_t>(error.projection) << '\n';
                transport_require(shaped && !error.resource_exhausted);
                verify_shaping_input(*retained, input); // Admitted order, never visual-order reversal.
                transport_require(retained->source_descriptor_count == 3U && retained->batch->glyphs.size() == 3U &&
                    retained->glyphs.size() == 3U && retained->descriptor_indices.size() == 3U &&
                    retained->batch->identity->source == source && retained->batch->identity->policy == configuration.policy &&
                    retained->batch->identity->x_pixels_per_em_26_6 == configuration.x_pixels_per_em_26_6 &&
                    retained->normalized_coordinates.empty());
                const std::array<hinted_design_vector, 2U> design{{{3, 0}, {12, 0}}};
                std::array<hinted_outline_point, 2U> projected{};
                transport_require(project_hinted_design_vectors(*retained->batch, design, projected,
                    hinted_projection_policy::scalar_reference).error == hinted_projection_error::none);
                const bool vertical = direction == shaping_direction::top_to_bottom || direction == shaping_direction::bottom_to_top;
                const bool backward = direction == shaping_direction::right_to_left || direction == shaping_direction::bottom_to_top;
                for (std::size_t index = 0U; index < 3U; ++index) {
                    const auto original = backward ? 2U - index : index;
                    const auto& raw = retained->batch->glyphs[original];
                    const auto& glyph = retained->glyphs[index];
                    const bool positioned = !ranged || original == 1U;
                    const auto advance_x = (vertical ? 0L : raw.advance_x_26_6) + (positioned ? projected[1U].x_26_6 : 0L);
                    const auto offset_x = (vertical ? raw.vertical_bearing_x_26_6 - raw.horizontal_bearing_x_26_6 : 0L) +
                        (positioned ? projected[0U].x_26_6 : 0L);
                    transport_require(raw.glyph_index == 1U && glyph.glyph_id == 1U &&
                        glyph.code_point == input[original].code_point && glyph.cluster == static_cast<std::int32_t>(original) &&
                        retained->descriptor_indices[index] == original && glyph.advance_x == advance_x && glyph.offset_x == offset_x &&
                        glyph.advance_y == (vertical ? -raw.vertical_advance_26_6 : raw.advance_y_26_6) &&
                        glyph.offset_y == (vertical ? -raw.horizontal_bearing_y_26_6 - raw.vertical_bearing_y_26_6 : 0L));
                }
                const auto unverified = retained;
                options.buffer_flags = shaping_buffer_flags::verify;
                transport_require(try_shape_context_hinted(context.value, 0U, configuration, input, options, retained, error, policy));
                transport_require(retained->batch->glyphs.size() == 3U && retained->source_descriptor_count == 3U &&
                    retained->descriptor_indices == unverified->descriptor_indices &&
                    std::memcmp(retained->glyphs.data(), unverified->glyphs.data(), 3U * sizeof(shaping_glyph)) == 0 &&
                    unverified->batch->glyphs.size() == 3U);
                options.buffer_flags = shaping_buffer_flags::none;
            }
            options.feature_settings = {};
            const std::array<unicode_scalar, 3U> spaces{unicode_scalar{0x41U, 0U, 1U},
                unicode_scalar{0x2007U, 1U, 1U}, unicode_scalar{0x2008U, 2U, 1U}};
            transport_require(try_shape_context_hinted(context.value, 0U, configuration, spaces, options, retained, error, policy));
            verify_shaping_input(*retained, spaces);
            transport_require(retained->source_descriptor_count == 3U && retained->batch->glyphs.size() == 5U &&
                retained->figure_descriptor_start == 3U && retained->figure_descriptor_count == 1U &&
                retained->punctuation_descriptor_start == 4U && retained->punctuation_descriptor_count == 1U);
            for (const auto& glyph : retained->batch->glyphs) transport_require(glyph.glyph_index == 1U);
            const bool backward = direction == shaping_direction::right_to_left || direction == shaping_direction::bottom_to_top;
            for (std::size_t index = 0U; index < 3U; ++index) {
                const auto original = backward ? 2U - index : index;
                transport_require(retained->descriptor_indices[index] == original && retained->glyphs[index].code_point == spaces[original].code_point);
            }
        }
    }
    auto options = open_type_shape_run_options{};
    options.script = latin; options.requested_features = features;
    const auto saved = retained;
    const std::array only_kern{kern};
    options.requested_features = only_kern; // Fault glyph B is no longer substituted before capture.
    transport_require(!try_shape_context_hinted(context.value, 0U, configuration, input, options, retained, error) &&
        error.capture == hinted_font_error::hinting_failed && !error.resource_exhausted && retained == saved);
    options.requested_features = features;
    transport_require(!try_shape_context_hinted(context.value, 99U, configuration, input, options, retained, error) &&
        error.shaping == font_error::invalid_argument && !error.resource_exhausted && retained == saved);
    transport_require(!try_shape_context_hinted(context.value, 0U, configuration, input, options, retained, error,
        hinted_projection_policy::gpu_shader) && error.projection == hinted_projection_error::unsupported_policy && retained == saved);
    const std::array<std::int16_t, 1U> invalid_normalized{1};
    options.normalized_coordinates = invalid_normalized;
    transport_require(!try_shape_context_hinted(context.value, 0U, configuration, input, options, retained, error) &&
        error.projection == hinted_projection_error::unsupported_frame && retained == saved);
    options.normalized_coordinates = std::span<const std::int16_t>(static_cast<const std::int16_t*>(nullptr), 1U);
    transport_require(!try_shape_context_hinted(context.value, 0U, configuration, input, options, retained, error) &&
        error.shaping == font_error::invalid_argument && retained == saved);
    options.normalized_coordinates = {};
    std::shared_ptr<const hinted_shaped_run> empty_shape;
    transport_require(try_shape_context_hinted(context.value, 0U, configuration,
        std::span<const unicode_scalar>{}, options, empty_shape, error) &&
        empty_shape->batch != nullptr && empty_shape->batch->glyphs.empty() &&
        empty_shape->glyphs.empty() && empty_shape->descriptor_indices.empty() && empty_shape->shaping_input.empty() &&
        empty_shape->source_descriptor_count == 0U && retained == saved);
    auto late_bytes = bytes;
    sfnt_font_view late_font{};
    sfnt_table_view late_gpos{};
    transport_require(sfnt_font_view::try_create(late_bytes, 0U, late_font) &&
        late_font.try_get_table(open_type_tag::from_chars('G', 'P', 'O', 'S'), late_gpos));
    const auto gpos_offset = static_cast<std::size_t>(late_gpos.bytes.data() - late_bytes.data());
    // Add an unknown ValueFormat bit, failing only during GPOS after capture.
    // Flipping the directory checksum's same high bit preserves the whole
    // font checksum modulo 2^32, including the original head adjustment.
    late_bytes[gpos_offset + 60U] = std::byte{0x80U};
    const auto table_count = (std::to_integer<std::uint32_t>(late_bytes[4U]) << 8U) |
        std::to_integer<std::uint32_t>(late_bytes[5U]);
    for (std::size_t index = 0U; index < table_count; ++index) {
        const auto record = 12U + index * 16U;
        if (late_bytes[record] == std::byte{0x47U} && late_bytes[record + 1U] == std::byte{0x50U} &&
            late_bytes[record + 2U] == std::byte{0x4FU} && late_bytes[record + 3U] == std::byte{0x53U})
            late_bytes[record + 4U] ^= std::byte{0x80U};
    }
    context_owner late_context;
    transport_require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
        reinterpret_cast<const std::uint8_t*>(late_bytes.data()), late_bytes.size(), 0U,
        nullptr, 0U, &late_context.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    transport_require(!try_shape_context_hinted(late_context.value, 0U, configuration, input, options, retained, error) &&
        error.shaping != font_error::none && error.capture == hinted_font_error::none &&
        error.projection == hinted_projection_error::none && retained == saved);
    const std::array only_stretch{open_type_tag::from_chars('s', 't', 'c', 'h')};
    options.requested_features = only_stretch;
    transport_require(!detail::device_shape_uses_arabic_stretch(options));
    options.script = open_type_tag::from_chars('a', 'r', 'a', 'b');
    transport_require(detail::device_shape_uses_arabic_stretch(options));
    const std::array<unicode_scalar, 1U> only_a{unicode_scalar{0x41U, 0U, 1U}};
    transport_require(try_shape_context_hinted(context.value, 0U, configuration, only_a, options, retained, error));
    transport_require(retained->source_descriptor_count == 1U && retained->batch->glyphs.size() == 1U &&
        retained->glyphs.capacity() < 257U); // No selected stch lookup: no blanket expansion reservation.
    auto mutable_input = input;
    mutable_input[0].input_index = 9U; mutable_input[0].input_length = 2U;
    mutable_input[1].input_index = 11U; mutable_input[1].input_length = 3U;
    mutable_input[2].input_index = 14U; mutable_input[2].input_length = 1U;
    for (auto& scalar : mutable_input) scalar.script = latin;
    const auto admitted = mutable_input;
    options.script = latin; options.direction = shaping_direction::right_to_left; options.requested_features = features;
    std::shared_ptr<const hinted_shaped_run> independent;
    transport_require(try_shape_context_hinted(context.value, 0U, configuration, mutable_input, options, independent, error));
    const auto original_glyphs = independent->glyphs;
    const auto original_descriptors = independent->descriptor_indices;
    std::fill(mutable_input.begin(), mutable_input.end(), unicode_scalar{});
    verify_shaping_input(*independent, admitted);
    transport_require(independent->shaping_input.data() != mutable_input.data() &&
        independent->descriptor_indices == original_descriptors &&
        std::memcmp(independent->glyphs.data(), original_glyphs.data(), original_glyphs.size() * sizeof(shaping_glyph)) == 0);
    progpu_native_text_context_destroy(context.value);
    context.value = nullptr;
    transport_require(retained->batch->identity->source == source && retained->batch->identity->source->bytes == bytes);
    verify_shaping_input(*retained, only_a);
    verify_shaping_input(*independent, admitted);
    tests::verify_hinted_transport(*saved->batch);
    tests::verify_hinted_transport(*retained->batch);
    transport_require(saved->batch->glyphs.size() == 5U && saved->descriptor_indices.size() == 3U);
}

void verify_owned_run_metrics(const progpu::native::text::hinted_glyph_batch& batch) {
    using namespace progpu::native::text;
    using tests::transport_require;
    transport_require(!batch.glyphs.empty());
    sfnt_font_view font{};
    transport_require(sfnt_font_view::try_create(batch.identity->source->bytes,
        batch.identity->source->face_index, font));
    for (const auto policy : {hinted_projection_policy::automatic, hinted_projection_policy::intrinsic_simd,
        hinted_projection_policy::scalar_reference}) {
        for (const auto direction : {shaping_direction::left_to_right, shaping_direction::right_to_left,
            shaping_direction::top_to_bottom, shaping_direction::bottom_to_top}) {
            std::vector<shaping_glyph> glyphs(batch.glyphs.size() + 1U);
            for (std::size_t index = 0U; index < batch.glyphs.size(); ++index)
                glyphs[index] = {batch.glyphs[index].glyph_index, static_cast<std::uint32_t>(0x41U + index),
                    static_cast<std::int32_t>(index), shaping_glyph_flags::unsafe_to_break, 77, 78, 79, 80};
            glyphs.back() = {0xFFFFFFFFU, 92U, -91, shaping_glyph_flags::unsafe_to_concat, -93, -94, -95, -96};
            auto expected = glyphs;
            const bool vertical = direction == shaping_direction::top_to_bottom || direction == shaping_direction::bottom_to_top;
            for (std::size_t index = 0U; index < batch.glyphs.size(); ++index) {
                const auto& retained = batch.glyphs[index];
                expected[index].advance_x = vertical ? 0 : static_cast<std::int32_t>(retained.advance_x_26_6);
                expected[index].advance_y = static_cast<std::int32_t>(vertical ? -retained.vertical_advance_26_6 : retained.advance_y_26_6);
                expected[index].offset_x = static_cast<std::int32_t>(vertical ? retained.vertical_bearing_x_26_6 - retained.horizontal_bearing_x_26_6 : 0);
                expected[index].offset_y = static_cast<std::int32_t>(vertical ? -retained.horizontal_bearing_y_26_6 - retained.vertical_bearing_y_26_6 : 0);
            }
            const auto result = initialize_hinted_run_metrics(batch, font, direction, glyphs, policy);
            transport_require(result.error == hinted_projection_error::none && result.frame.owner == &batch &&
                std::memcmp(glyphs.data(), expected.data(), glyphs.size() * sizeof(glyphs[0])) == 0);
            transport_require(initialize_hinted_run_metrics(batch, font, direction,
                std::span<shaping_glyph>(glyphs).first(batch.glyphs.size() - 1U), policy).error ==
                hinted_projection_error::insufficient_capacity);
            glyphs[batch.glyphs.size() - 1U].glyph_id = 0xFFFFFFFFU;
            const auto before = glyphs;
            transport_require(initialize_hinted_run_metrics(batch, font, direction, glyphs, policy).error ==
                hinted_projection_error::unsupported_frame &&
                std::memcmp(glyphs.data(), before.data(), glyphs.size() * sizeof(glyphs[0])) == 0);
        }
    }
    std::vector<shaping_glyph> glyphs(batch.glyphs.size());
    for (std::size_t index = 0U; index < glyphs.size(); ++index) glyphs[index].glyph_id = batch.glyphs[index].glyph_index;
    const auto unchanged = glyphs;
    transport_require(initialize_hinted_run_metrics(batch, font, shaping_direction::unspecified, glyphs).error ==
        hinted_projection_error::invalid_argument &&
        std::memcmp(glyphs.data(), unchanged.data(), glyphs.size() * sizeof(glyphs[0])) == 0);
    transport_require(initialize_hinted_run_metrics(batch, font, shaping_direction::left_to_right, glyphs,
        hinted_projection_policy::gpu_shader).error == hinted_projection_error::unsupported_policy);
    auto invalid = batch;
    invalid.glyphs.back().vertical_advance_26_6 = std::numeric_limits<std::int32_t>::min();
    transport_require(initialize_hinted_run_metrics(invalid, font, shaping_direction::top_to_bottom, glyphs).error ==
        hinted_projection_error::unsupported_frame &&
        std::memcmp(glyphs.data(), unchanged.data(), glyphs.size() * sizeof(glyphs[0])) == 0);
    if constexpr (sizeof(long) > sizeof(std::int32_t)) {
        invalid = batch;
        invalid.glyphs.back().advance_x_26_6 = std::numeric_limits<long>::max();
        transport_require(initialize_hinted_run_metrics(invalid, font, shaping_direction::left_to_right, glyphs).error ==
            hinted_projection_error::unsupported_frame &&
            std::memcmp(glyphs.data(), unchanged.data(), glyphs.size() * sizeof(glyphs[0])) == 0);
    }
    auto empty = batch;
    empty.glyphs.clear();
    transport_require(initialize_hinted_run_metrics(empty, font, shaping_direction::left_to_right, glyphs).error ==
        hinted_projection_error::none &&
        std::memcmp(glyphs.data(), unchanged.data(), glyphs.size() * sizeof(glyphs[0])) == 0);
    transport_require(initialize_hinted_run_metrics(batch, font, shaping_direction::left_to_right,
        std::span<shaping_glyph>(reinterpret_cast<shaping_glyph*>(const_cast<hinted_glyph*>(batch.glyphs.data())),
            batch.glyphs.size())).error == hinted_projection_error::invalid_argument);
}

void verify_owned_gpos_frame(const progpu::native::text::hinted_glyph_batch& batch) {
    using namespace progpu::native::text;
    using tests::transport_require;
    sfnt_font_view font{};
    font_error parse_error = font_error::none;
    transport_require(sfnt_font_view::try_create(batch.identity->source->bytes,
        batch.identity->source->face_index, font, &parse_error));
    // Original independently authored raw SinglePos fixture, not source UI.
    std::array<std::byte, 42> table{};
    const auto put = [&](std::size_t at, std::uint16_t value) {
        table[at] = static_cast<std::byte>(value >> 8U);
        table[at + 1U] = static_cast<std::byte>(value);
    };
    put(0U, 1U); put(4U, 10U); put(6U, 12U); put(8U, 14U);
    put(14U, 1U); put(16U, 4U); put(18U, 1U); put(22U, 1U); put(24U, 8U);
    put(26U, 1U); put(28U, 10U); put(30U, 5U); put(32U, 3U); put(34U, 0xFFFEU);
    put(36U, 1U); put(38U, 1U); put(40U, 1U);
    open_type_layout_table_view gpos{};
    transport_require(open_type_layout_table_view::try_create(table, gpos, &parse_error));
    const std::array<hinted_design_vector, 2> design{{{3, 0}, {-2, 0}}};
    std::array<hinted_outline_point, 2> reference{};
    transport_require(project_hinted_design_vectors(batch, design, reference,
        hinted_projection_policy::scalar_reference).error == hinted_projection_error::none);
    for (const auto policy : {hinted_projection_policy::automatic, hinted_projection_policy::intrinsic_simd,
        hinted_projection_policy::scalar_reference}) {
        const auto result = bind_hinted_gpos_frame(batch, font, policy);
        transport_require(result.error == hinted_projection_error::none && result.frame.owner == &batch && result.frame.font == &font);
        transport_require(result.frame.pixels_per_em_x == batch.identity->device_frame.x_pixels_per_em &&
            result.frame.pixels_per_em_y == batch.identity->device_frame.y_pixels_per_em);
        std::array<shaping_glyph, 3> glyphs{};
        for (std::size_t index = 0U; index < glyphs.size(); ++index) {
            glyphs[index].glyph_id = batch.glyphs[index].glyph_index;
            glyphs[index].advance_x = 500;
        }
        auto options = open_type_gpos_apply_options{};
        options.font = &font;
        bool applied = false;
        transport_require(detail::try_apply_device_gpos_lookup(gpos, 0U, glyphs, options, result.frame, applied, &parse_error));
        transport_require(applied && glyphs[0].offset_x == reference[0].x_26_6 &&
            glyphs[0].advance_x == 500 + reference[1].x_26_6 && glyphs[1].advance_x == glyphs[0].advance_x &&
            glyphs[2].advance_x == 500 && glyphs[2].offset_x == 0);
        detail::gpos_metric_vector point{};
        transport_require(result.frame.contour_point(&batch, 0U, batch.glyphs[0].glyph_index, 0U, point));
        const auto anchor = get_hinted_anchor_point(batch, 0U, 0U);
        transport_require(point.x == anchor.point.x_26_6 && point.y == anchor.point.y_26_6);
        point = {-77, -91};
        transport_require(!result.frame.contour_point(&batch, 0U, 0xFFFFFFFFU, 0U, point) && point.x == -77 && point.y == -91);
        glyphs[0].offset_x = std::numeric_limits<std::int32_t>::max();
        const auto before = glyphs;
        transport_require(!detail::try_apply_device_gpos_lookup(gpos, 0U, glyphs, options, result.frame, applied, &parse_error) &&
            std::memcmp(glyphs.data(), before.data(), sizeof(glyphs)) == 0);
    }
    const auto copy = batch.identity->source->bytes;
    sfnt_font_view unrelated{};
    transport_require(sfnt_font_view::try_create(copy, batch.identity->source->face_index, unrelated, &parse_error));
    transport_require(bind_hinted_gpos_frame(batch, unrelated).error == hinted_projection_error::invalid_argument);
    const std::array<std::int16_t, 1> invalid_normalized{1};
    transport_require(bind_hinted_gpos_frame(batch, font, hinted_projection_policy::automatic,
        invalid_normalized).error == hinted_projection_error::unsupported_frame);
    auto invalid_axes = std::make_shared<hinted_font_identity>(*batch.identity);
    invalid_axes->variation_coordinates_16_16 = {1};
    auto mismatched = batch;
    mismatched.identity = invalid_axes;
    transport_require(bind_hinted_gpos_frame(mismatched, font).error == hinted_projection_error::unsupported_frame);
    for (const auto policy : {hinted_projection_policy::native_compute, hinted_projection_policy::gpu_shader,
        static_cast<hinted_projection_policy>(0xFFFFFFFFU)}) {
        const auto rejected = bind_hinted_gpos_frame(batch, font, policy);
        transport_require(rejected.error == hinted_projection_error::unsupported_policy && rejected.frame.owner == nullptr);
    }
}

void verify_public_batch(const progpu_native_hinted_batch* batch,
    const progpu::native::text::hinted_glyph_batch& original) {
    using namespace progpu::native::text;
    using tests::transport_require;
    progpu_native_hinted_batch_counts counts{91U, 92U, 93U};
    transport_require(progpu_native_hinted_batch_get_counts(batch, &counts) == PROGPU_NATIVE_STATUS_SUCCESS);
    progpu_native_hinted_batch_counts expected{};
    transport_require(get_hinted_batch_counts(original, expected) == hinted_transport_error::none &&
        counts.glyphs == expected.glyphs && counts.points == expected.points && counts.contours == expected.contours);
    std::vector<progpu_native_hinted_glyph> glyphs(counts.glyphs + 1U), reference_glyphs(glyphs.size());
    std::vector<progpu_native_hinted_point> points(counts.points + 1U), reference_points(points.size());
    std::vector<std::uint8_t> tags(points.size(), 0xA5U), reference_tags(tags);
    std::vector<std::int32_t> contours(counts.contours + 1U, -77), reference_contours(contours);
    std::memset(glyphs.data(), 0xA5, glyphs.size() * sizeof(glyphs[0]));
    std::memset(points.data(), 0xA5, points.size() * sizeof(points[0]));
    reference_glyphs = glyphs;
    reference_points = points;
    const auto same = [&] {
        return std::memcmp(glyphs.data(), reference_glyphs.data(), glyphs.size() * sizeof(glyphs[0])) == 0 &&
            std::memcmp(points.data(), reference_points.data(), points.size() * sizeof(points[0])) == 0 &&
            tags == reference_tags && contours == reference_contours;
    };
    const auto copy = [&](std::uint32_t capacity) {
        return progpu_native_hinted_batch_copy(batch, glyphs.data(), static_cast<std::uint32_t>(glyphs.size()),
            points.data(), static_cast<std::uint32_t>(points.size()), tags.data(), capacity,
            contours.data(), static_cast<std::uint32_t>(contours.size()));
    };
    transport_require(counts.points != 0U && copy(counts.points - 1U) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && same());
    transport_require(progpu_native_hinted_batch_copy(batch, glyphs.data(), static_cast<std::uint32_t>(glyphs.size()),
        points.data(), static_cast<std::uint32_t>(points.size()), reinterpret_cast<std::uint8_t*>(points.data()),
        static_cast<std::uint32_t>(points.size()), contours.data(), static_cast<std::uint32_t>(contours.size())) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && same());
    transport_require(copy(static_cast<std::uint32_t>(tags.size())) == PROGPU_NATIVE_STATUS_SUCCESS);
    transport_require(static_cast<bool>(copy_hinted_batch(original, reference_glyphs, reference_points,
        reference_tags, reference_contours, hinted_transport_policy::scalar_reference)) && same());
    transport_require(progpu_native_hinted_batch_get_counts(batch,
        reinterpret_cast<progpu_native_hinted_batch_counts*>(const_cast<progpu_native_hinted_batch*>(batch))) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    transport_require(copy(static_cast<std::uint32_t>(tags.size())) == PROGPU_NATIVE_STATUS_SUCCESS && same());
}
#endif
}

int main()
{
    using namespace progpu::native::text;
    using tests::transport_require;
    struct context_owner final {
        progpu_native_text_context* value = nullptr;
        ~context_owner() { progpu_native_text_context_destroy(value); }
    };
    try {
        const auto original = progpu::native::tests::make_hint_fault_font();
        std::shared_ptr<const hinted_glyph_batch> retained;
        const std::array<std::uint32_t, 3> ids{1U, 1U, 0U};
        hinted_font_error error = hinted_font_error::none;
        batch_owner public_batch;
#if defined(PROGPU_NATIVE_FONT_HINTING)
        std::shared_ptr<const hinted_shaped_run> owned_shape;
        const auto mapping_original = progpu::native::tests::make_hinted_mapping_font();
        verify_shaping_map(mapping_original);
#endif
        for (const auto policy : {font_hint_policy::truetype_35, font_hint_policy::truetype_40}) {
            context_owner context;
            transport_require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
                reinterpret_cast<const std::uint8_t*>(original.data()), original.size(), 0U,
                nullptr, 0U, &context.value) == PROGPU_NATIVE_STATUS_SUCCESS);
            const auto source = select_context_font_source(context.value, 0U);
            const hinted_font_configuration configuration{13U * 64U, 13U * 64U, policy, 0U, 0U, {}};
            alignas(std::max_align_t) progpu_native_hinted_font_request request{PROGPU_NATIVE_ABI_VERSION,
                sizeof(progpu_native_hinted_font_request), 0U, 13U * 64U, 13U * 64U,
                static_cast<std::uint32_t>(policy), 0U, 0U, 0U, 0U};
            progpu_native_hinted_batch* candidate = nullptr;
            request.reserved = 1U;
            transport_require(progpu_native_text_context_capture_hinted_batch(context.value, &request, nullptr,
                ids.data(), static_cast<std::uint32_t>(ids.size()), &candidate) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
                candidate == nullptr);
            request.reserved = 0U;
            request.x_phase_26_6 = 64U;
            transport_require(progpu_native_text_context_capture_hinted_batch(context.value, &request, nullptr,
                ids.data(), static_cast<std::uint32_t>(ids.size()), &candidate) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
                candidate == nullptr);
            request.x_phase_26_6 = 0U;
            transport_require(progpu_native_text_context_capture_hinted_batch(context.value, &request, nullptr,
                ids.data(), static_cast<std::uint32_t>(ids.size()), reinterpret_cast<progpu_native_hinted_batch**>(&request)) ==
                PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && request.abi_version == PROGPU_NATIVE_ABI_VERSION);
#if defined(PROGPU_NATIVE_FONT_HINTING)
            transport_require(capture_context_hinted(context.value, 0U, configuration, ids, retained, error));
            transport_require(retained->identity->source == source && retained->identity->policy == policy);
            verify_owned_gpos_frame(*retained);
            verify_owned_run_metrics(*retained);
            verify_owned_shaping(configuration);
            context_owner shape_context;
            transport_require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
                reinterpret_cast<const std::uint8_t*>(mapping_original.data()), mapping_original.size(), 0U,
                nullptr, 0U, &shape_context.value) == PROGPU_NATIVE_STATUS_SUCCESS);
            const std::array<unicode_scalar, 2U> repeated_a{unicode_scalar{0x41U, 0U, 1U}, unicode_scalar{0x41U, 1U, 1U}};
            auto shape_options = open_type_shape_run_options{};
            shape_options.script = open_type_tag::from_chars('l', 'a', 't', 'n');
            hinted_shape_error shape_error{};
            transport_require(try_shape_context_hinted(shape_context.value, 0U, configuration, repeated_a,
                shape_options, owned_shape, shape_error) && !shape_error.resource_exhausted && owned_shape->batch->glyphs.size() == 2U &&
                owned_shape->descriptor_indices == std::vector<std::uint32_t>{0U, 1U});
            const auto saved_shape = owned_shape;
            const std::array<unicode_scalar, 2U> faulty_shape{unicode_scalar{0x41U, 0U, 1U}, unicode_scalar{0x42U, 1U, 1U}};
            transport_require(!try_shape_context_hinted(shape_context.value, 0U, configuration, faulty_shape,
                shape_options, owned_shape, shape_error) && shape_error.capture == hinted_font_error::hinting_failed && !shape_error.resource_exhausted &&
                owned_shape == saved_shape);
            const auto saved = retained;
            transport_require(progpu_native_text_context_capture_hinted_batch(context.value, &request, nullptr,
                ids.data(), static_cast<std::uint32_t>(ids.size()), &candidate) == PROGPU_NATIVE_STATUS_SUCCESS);
            progpu_native_hinted_batch_destroy(public_batch.value);
            public_batch.value = candidate;
            verify_public_batch(public_batch.value, *saved);
            const std::array<std::uint32_t, 3> faulty{1U, 1U, 2U};
            transport_require(progpu_native_text_context_capture_hinted_batch(context.value, &request, nullptr,
                faulty.data(), static_cast<std::uint32_t>(faulty.size()), &candidate) == PROGPU_NATIVE_STATUS_INTERNAL_ERROR &&
                candidate == public_batch.value);
            verify_public_batch(public_batch.value, *saved);
            transport_require(!capture_context_hinted(context.value, 0U, configuration, faulty, retained, error) &&
                error == hinted_font_error::hinting_failed && retained == saved);
            transport_require(capture_context_hinted(context.value, 0U, configuration, ids, retained, error) && retained == saved);
            transport_require(!capture_context_hinted(context.value, 99U, configuration, ids, retained, error) &&
                error == hinted_font_error::invalid_argument && retained == saved);
            for (std::uint32_t index = 1U; index <= 20U; ++index) {
                std::uint32_t palette = 0U;
                transport_require(progpu_native_text_context_add_fallback_font(context.value,
                    reinterpret_cast<const std::uint8_t*>(original.data()), original.size(), 0U, index,
                    &palette) == PROGPU_NATIVE_STATUS_SUCCESS && palette == index);
                std::shared_ptr<const hinted_glyph_batch> fallback;
                transport_require(capture_context_hinted(context.value, palette, configuration, ids, fallback, error));
                transport_require(fallback->identity->source == select_context_font_source(context.value, palette) &&
                    fallback->identity->source != source && fallback->glyphs == saved->glyphs);
                batch_owner published_fallback;
                request.font_index = palette;
                transport_require(progpu_native_text_context_capture_hinted_batch(context.value, &request, nullptr,
                    ids.data(), static_cast<std::uint32_t>(ids.size()), &published_fallback.value) == PROGPU_NATIVE_STATUS_SUCCESS);
                verify_public_batch(published_fallback.value, *fallback);
            }
            request.font_index = 0U;
            transport_require(retained == saved && retained->identity->source == source);
            tests::verify_hinted_transport(*retained);
#else
            transport_require(progpu_native_text_context_capture_hinted_batch(context.value, &request, nullptr,
                ids.data(), static_cast<std::uint32_t>(ids.size()), &candidate) == PROGPU_NATIVE_STATUS_UNSUPPORTED &&
                candidate == nullptr);
            transport_require(source != nullptr && !capture_context_hinted(context.value, 0U, configuration, ids, retained, error) &&
                error == hinted_font_error::dependency_unavailable && retained == nullptr);
#endif
        }
#if defined(PROGPU_NATIVE_FONT_HINTING)
        tests::verify_hinted_transport(*retained); // both contexts and native faces have retired
        verify_owned_gpos_frame(*retained);
        verify_owned_run_metrics(*retained);
        verify_public_batch(public_batch.value, *retained);
        tests::verify_hinted_transport(*owned_shape->batch);
        transport_require(owned_shape->glyphs.size() == 2U && owned_shape->batch->glyphs.size() == 2U &&
            owned_shape->descriptor_indices == std::vector<std::uint32_t>{0U, 1U});
#endif
        std::cout << "native hinted context ownership controls passed\n";
        return 0;
    } catch (const std::exception& failure) {
        std::cerr << failure.what() << '\n';
        return 1;
    }
}
