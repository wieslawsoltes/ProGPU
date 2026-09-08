#include "progpu_native_text_shaping_showcase.hpp"
#include "progpu_native_text_styles.h"
#include "progpu_native_text_flow.h"

#include <cstddef>
#include <cstdint>
#include <fstream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <vector>
#include <array>
#include <algorithm>
#include <cmath>
#include <memory>

namespace {

using progpu::native::samples::text_shaping_showcase_metrics;
using progpu::native::samples::text_shaping_showcase_scene;

void require_impl(bool condition, int line) {
    if (!condition) {
        throw std::runtime_error(
            "native text shaping showcase assertion failed at line " +
            std::to_string(line));
    }
}

#define require(condition) require_impl((condition), __LINE__)

std::vector<std::byte> read_font() {
    std::ifstream input(PROGPU_NATIVE_TEST_INTER_FONT, std::ios::binary);
    require(input.good());
    const std::vector<char> chars{
        std::istreambuf_iterator<char>(input),
        std::istreambuf_iterator<char>()};
    std::vector<std::byte> bytes(chars.size());
    for (std::size_t index = 0U; index < chars.size(); ++index) {
        bytes[index] = static_cast<std::byte>(
            static_cast<unsigned char>(chars[index]));
    }
    return bytes;
}

void managed_feature_wall_port_is_retained_and_dpi_sensitive() {
    const auto font = read_font();
    text_shaping_showcase_scene scene;
    require(scene.load_font(font));
    require(scene.ready());
    require(scene.resize(960.0F, 640.0F, 1.0F));

    std::vector<std::byte> stream;
    text_shaping_showcase_metrics metrics{};
    require(scene.compile(stream, metrics));
    require(metrics.preset_index == 0U);
    require(metrics.shaped_glyph_count > metrics.visible_glyph_count);
    require(metrics.visible_glyph_count > 0U);
    require(metrics.unique_outline_count > 16U);
    require(metrics.feature_off_glyph_count > 0U);
    require(metrics.feature_on_glyph_count > 0U);
    require(metrics.command_count == 11U);
    require(metrics.resource_count >= 2U);
    require(metrics.stream_bytes == stream.size());
    const auto first_stream = stream;
    const auto first_generation = metrics.generation;

    require(!scene.compile(stream, metrics));
    require(scene.generation() == first_generation);

    require(scene.set_preset(1U));
    require(scene.compile(stream, metrics));
    require(metrics.preset_index == 1U);
    require(metrics.feature_on_glyph_count == metrics.feature_off_glyph_count);
    require(metrics.feature_on_advance != metrics.feature_off_advance);
    require(stream != first_stream);

    for (std::uint32_t preset = 2U;
         preset < text_shaping_showcase_scene::preset_count();
         ++preset) {
        require(scene.set_preset(preset));
        require(scene.compile(stream, metrics));
        require(metrics.preset_index == preset);
        require(metrics.visible_glyph_count > 0U);
        require(metrics.unique_outline_count > 0U);
    }

    const std::uint32_t glyph_count = metrics.shaped_glyph_count;
    const std::uint32_t outline_count = metrics.unique_outline_count;
    require(scene.resize(960.0F, 640.0F, 2.0F));
    require(scene.compile(stream, metrics));
    require(metrics.shaped_glyph_count == glyph_count);
    require(metrics.unique_outline_count == outline_count);
}

} // namespace

static void styled_context_preserves_font_scale_and_atomic_failure() {
    const auto font = read_font();
    progpu_native_text_context* raw = nullptr;
    require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
        reinterpret_cast<const std::uint8_t*>(font.data()), font.size(), 0, nullptr, 0, &raw) == PROGPU_NATIVE_STATUS_SUCCESS);
    std::unique_ptr<progpu_native_text_context, decltype(&progpu_native_text_context_destroy)> context(raw, progpu_native_text_context_destroy);
    std::uint32_t second_font = 0;
    require(progpu_native_text_context_add_fallback_font(context.get(),
        reinterpret_cast<const std::uint8_t*>(font.data()), font.size(), 0, 42, &second_font) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(second_font != 0);
    const std::array<progpu_native_text_scalar, 3> text{{{'M', 0, 1, 0, 0, 0}, {' ', 1, 1, 0, 0, 0}, {'M', 2, 1, 0, 0, 0}}};
    progpu_native_text_shape_request shaping{};
    shaping.struct_size = sizeof(shaping); shaping.abi_version = PROGPU_NATIVE_ABI_VERSION;
    shaping.input = text.data(); shaping.input_count = static_cast<std::uint32_t>(text.size());
    shaping.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
    shaping.alternate_value = 1;
    progpu_native_text_layout_options layout{};
    layout.struct_size = sizeof(layout); layout.scale = 0.01F; layout.line_height = 24;
    std::array<progpu_native_text_style_run, 2> styles{{{0, 2, 0, 0.01F, 0, 0, 0, 0}, {2, 1, second_font, 0.02F, 0, 0, 0, 0}}};
    progpu_native_text_paragraph_requirements required{}; required.struct_size = sizeof(required);
    require(progpu_native_text_context_get_styled_paragraph_requirements(context.get(), &shaping, &layout,
        styles.data(), static_cast<std::uint32_t>(styles.size()), &required) == PROGPU_NATIVE_STATUS_SUCCESS);
    std::vector<progpu_native_positioned_text_glyph> glyphs(required.glyph_capacity);
    std::vector<progpu_native_positioned_text_line> lines(required.line_capacity);
    std::vector<std::byte> scratch(required.scratch_bytes);
    progpu_native_text_paragraph_result result{}; result.struct_size = sizeof(result);
    auto run = [&] { return progpu_native_text_context_layout_styled_paragraph(context.get(), &shaping, &layout,
        styles.data(), static_cast<std::uint32_t>(styles.size()), glyphs.data(), static_cast<std::uint32_t>(glyphs.size()),
        lines.data(), static_cast<std::uint32_t>(lines.size()), scratch.data(), scratch.size(), &result); };
    require(run() == PROGPU_NATIVE_STATUS_SUCCESS);
    require(result.glyph_count == 3 && result.line_count == 1);
    require(glyphs[0].font_index == 0 && glyphs[2].font_index == second_font);
    require(glyphs[0].glyph_id == glyphs[2].glyph_id);
    require(std::abs(glyphs[2].advance_x - 2 * glyphs[0].advance_x) < 0.0001F);
    const float first_width = glyphs[0].advance_x + glyphs[1].advance_x;
    layout.maximum_width = first_width + 0.1F;
    require(run() == PROGPU_NATIVE_STATUS_SUCCESS && result.line_count == 2);
    require(lines[0].glyph_count == 2 && glyphs[2].font_index == second_font && glyphs[2].y == 24);
    glyphs[0].x = 123;
    styles[1].scalar_start = 1; // overlapping style coverage must not publish a paragraph
    require(run() == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(result.glyph_count == 0 && result.line_count == 0 && glyphs[0].x == 123);
    required.glyph_capacity = 123;
    require(progpu_native_text_context_get_styled_paragraph_requirements(context.get(), &shaping, &layout,
        styles.data(), static_cast<std::uint32_t>(styles.size()), &required) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(required.glyph_capacity == 0);
    styles[1].scalar_start = 2;
    auto tab_text = text; tab_text[1].code_point = 9;
    shaping.input = tab_text.data(); layout.maximum_width = 0;
    progpu_native_text_flow_options flow{sizeof(flow), 40, 3, 0};
    require(progpu_native_text_context_get_flow_paragraph_requirements(context.get(), &shaping, &layout,
        styles.data(), static_cast<std::uint32_t>(styles.size()), &flow, &required) == PROGPU_NATIVE_STATUS_SUCCESS);
    scratch.resize(required.scratch_bytes);
    auto flow_run = [&] { return progpu_native_text_context_layout_flow_paragraph(context.get(), &shaping, &layout,
        styles.data(), static_cast<std::uint32_t>(styles.size()), &flow, glyphs.data(), static_cast<std::uint32_t>(glyphs.size()),
        lines.data(), static_cast<std::uint32_t>(lines.size()), scratch.data(), scratch.size(), &result); };
    require(flow_run() == PROGPU_NATIVE_STATUS_SUCCESS && result.glyph_count == 3 && result.line_count == 1);
    require(glyphs[1].glyph_id == UINT32_MAX && glyphs[1].cluster == 1 && glyphs[1].advance_x > 0);
    require(std::abs(glyphs[2].x - 37) < 0.0001F && glyphs[2].font_index == second_font);
    const float tab_advance = glyphs[1].advance_x;
    shaping.direction = PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT;
    require(flow_run() == PROGPU_NATIVE_STATUS_SUCCESS && glyphs[1].glyph_id == UINT32_MAX);
    require(std::abs(glyphs[1].advance_x - tab_advance) < 0.0001F);
    progpu_native_text_intrinsic_widths widths{sizeof(widths), 0, 0, 0};
    auto measured_run = [&] { return progpu_native_text_context_layout_configured_flow_paragraph(context.get(), &shaping, &layout,
        styles.data(), static_cast<std::uint32_t>(styles.size()), &flow, glyphs.data(), static_cast<std::uint32_t>(glyphs.size()),
        lines.data(), static_cast<std::uint32_t>(lines.size()), scratch.data(), scratch.size(), &result,
        PROGPU_NATIVE_TEXT_WRAPPING_WHOLE_WORD, &widths); };
    require(measured_run() == PROGPU_NATIVE_STATUS_SUCCESS);
    require(std::abs(widths.minimum - std::max(glyphs[0].advance_x, glyphs[2].advance_x)) < 0.0001F);
    require(std::abs(widths.maximum - lines[0].width) < 0.0001F && widths.minimum < widths.maximum);
    const float intrinsic_maximum = widths.maximum;
    layout.maximum_width = 1;
    require(measured_run() == PROGPU_NATIVE_STATUS_SUCCESS && std::abs(widths.maximum - intrinsic_maximum) < 0.0001F);
    layout.maximum_lines = 1;
    require(measured_run() == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && widths.maximum == 0 && widths.minimum == 0);
    layout.maximum_lines = 0; layout.maximum_width = 0;
    flow.reserved = 1; glyphs[0].x = 123;
    require(flow_run() == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && result.glyph_count == 0 && glyphs[0].x == 123);
}

int main() {
    styled_context_preserves_font_scale_and_atomic_failure();
    managed_feature_wall_port_is_retained_and_dpi_sensitive();
    return 0;
}
