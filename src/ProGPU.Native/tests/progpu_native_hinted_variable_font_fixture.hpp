#pragma once

#include "../include/progpu_native_text_hinting.h"
#include "../src/Text/Interop/progpu_native_hinted_paragraph_transport_internal.hpp"

#include <algorithm>
#include <array>
#include <fstream>
#include <iterator>
#include <stdexcept>
#include <vector>

namespace progpu::native::tests {
#if defined(PROGPU_NATIVE_FONT_HINTING)
// Reviewed, checked-in font provenance and exact fvar/avar expectations:
// progpu_native_text_tests.cpp::production_inter_variable_font_matches_fvar_axes.
// These are real non-default Inter opsz/wght settings, never synthetic tables.
struct hinted_variable_font_fixture final {
    std::vector<std::byte> font;
    progpu_native_text_context* context = nullptr;
    std::array<std::int32_t, 4U> axes{23 * 65536, 700 * 65536, 23 * 65536, 700 * 65536};
    std::array<std::int16_t, 2U> normalized{8192, 8847};
    std::array<progpu_native_text_scalar, 6U> input{};
    std::array<progpu_native_text_feature, 2U> features{{
        {0x6C696761U, 1U, 0U, UINT32_MAX}, {0x6B65726EU, 1U, 0U, UINT32_MAX}}};
    std::array<progpu_native_text_style_run, 2U> styles{};
    std::array<progpu_native_text_style_metrics, 2U> metrics{{{5.5F, 1.75F}, {5.5F, 1.75F}}};
    std::array<progpu_native_hinted_paragraph_device_style, 2U> devices{};
    progpu_native_text_shape_request shaping{};
    progpu_native_text_layout_options layout{};

    static void require(bool value) {
        if (!value) throw std::runtime_error("reviewed Inter variable hinted fixture");
    }
    hinted_variable_font_fixture() {
        using namespace progpu::native::text;
        std::ifstream stream(PROGPU_NATIVE_TEST_INTER_VARIABLE_FONT, std::ios::binary);
        require(stream.good());
        const std::vector<char> source{std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>()};
        font.resize(source.size());
        for (std::size_t i = 0U; i < source.size(); ++i) font[i] = static_cast<std::byte>(source[i]);
        sfnt_font_view parsed{}; sfnt_header_metrics header{}; std::uint16_t count = 0U, written = 0U;
        require(sfnt_font_view::try_create(font, 0U, parsed) && parsed.try_get_header_metrics(header) &&
            header.units_per_em == 2048U && parsed.try_get_variation_axis_count(count) && count == 2U);
        std::array<sfnt_variation_axis, 2U> decoded{};
        require(parsed.try_decode_variation_axes(decoded, written) && written == 2U &&
            decoded[0U].tag == open_type_tag::from_chars('o', 'p', 's', 'z') &&
            decoded[1U].tag == open_type_tag::from_chars('w', 'g', 'h', 't'));
        for (std::uint16_t i = 0U; i < 2U; ++i) {
            std::int16_t actual = 0;
            require(axes[i] != decoded[i].default_fixed &&
                parsed.try_normalize_variation_coordinate(i, axes[i], actual) && actual == normalized[i] && actual != 0);
        }
        constexpr std::array<std::uint32_t, 6U> scalars{'A', 'B', ' ', 'A', 'B', ' '};
        for (std::uint32_t i = 0U; i < input.size(); ++i) input[i] = {scalars[i], 9U + i, 1U, 0U, 0U, 0U};
        for (std::uint32_t i = 0U; i < styles.size(); ++i) {
            styles[i] = {i * 3U, 3U, 0U, 8.0F / 2048.0F, 0U, 2U, 0U, 0U, 0U, 0U, 0U};
            devices[i] = {0U, styles[i].scale, 0.8F, 10U * 64U, 10U * 64U,
                static_cast<std::uint32_t>(font_hint_policy::truetype_40), 7U + i * 12U, 11U + i * 12U, i * 2U, 2U, 0U};
        }
        shaping.struct_size = sizeof(shaping); shaping.abi_version = PROGPU_NATIVE_ABI_VERSION;
        shaping.input = input.data(); shaping.input_count = static_cast<std::uint32_t>(input.size());
        shaping.features = features.data(); shaping.feature_count = static_cast<std::uint32_t>(features.size());
        shaping.normalized_coordinates = normalized.data(); shaping.normalized_coordinate_count = 2U;
        shaping.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
        layout.struct_size = sizeof(layout); layout.scale = 1.0F; layout.maximum_width = 200.0F;
        layout.direction = shaping.direction;
        require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
            reinterpret_cast<const std::uint8_t*>(font.data()), font.size(), 0U, nullptr, 0U,
            &context) == PROGPU_NATIVE_STATUS_SUCCESS);
    }
    hinted_variable_font_fixture(const hinted_variable_font_fixture&) = delete;
    hinted_variable_font_fixture& operator=(const hinted_variable_font_fixture&) = delete;
    ~hinted_variable_font_fixture() { progpu_native_text_context_destroy(context); }
    void produce(progpu_native_hinted_paragraph*& paragraph) {
        progpu_native_text_paragraph_result result{}; result.struct_size = sizeof(result);
        require(progpu_native_text_context_layout_hinted_paragraph(context, &shaping, &layout, styles.data(), 2U,
            metrics.data(), devices.data(), 2U, axes.data(), static_cast<std::uint32_t>(axes.size()),
            &paragraph, &result) == PROGPU_NATIVE_STATUS_SUCCESS && paragraph != nullptr);
    }
    void retire_inputs() noexcept {
        progpu_native_text_context_destroy(context); context = nullptr;
        std::fill(font.begin(), font.end(), std::byte{0}); input.fill({}); features.fill({});
        styles.fill({}); metrics.fill({}); devices.fill({}); axes.fill(0); normalized.fill(0);
    }
};
#endif
} // namespace progpu::native::tests
