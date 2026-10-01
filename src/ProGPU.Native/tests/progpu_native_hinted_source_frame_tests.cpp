#include "../src/Text/Interop/progpu_native_hinted_paragraph_transport_internal.hpp"

#include <array>
#include <cmath>
#include <cstring>
#include <iostream>
#include <source_location>
#include <stdexcept>

namespace {
using namespace progpu::native::text;
void require(bool value, std::source_location at = std::source_location::current()) {
    if (!value) throw std::runtime_error("source frame control at " + std::to_string(at.line()));
}
void controls() {
    // Independent source controls: nominal hmtx is 500/0, positioned advance
    // is 7/0, and the native baseline is 20 (not the source baseline of 7).
    std::array<progpu_native_positioned_text_glyph, 3U> glyphs{};
    std::array<progpu_native_hinted_paragraph_glyph_owner, 3U> owners{};
    std::array<std::int8_t, 3U> levels{1, 1, 1};
    std::array<progpu_native_hinted_glyph_nominal_metrics, 3U> nominal{};
    for (std::size_t i = 0U; i < glyphs.size(); ++i) {
        glyphs[i].glyph_id = static_cast<std::uint32_t>(i);
        glyphs[i].x = i == 0U ? 12.0F : 4.0F;
        glyphs[i].y = i == 1U ? 21.5F : 20.0F;
        glyphs[i].advance_x = i == 1U ? 0.0F : 7.0F;
        nominal[i] = {static_cast<std::uint32_t>(i), 0U, static_cast<std::uint32_t>(i), i == 1U ? 0U : 500U};
    }
    progpu_native_hinted_glyph_font_source font{}; font.units_per_em = 1000U;
    progpu_native_hinted_paragraph_run run{}; run.source_scale = 0.01F;
    std::array<progpu_native_positioned_text_line, 2U> lines{};
    lines[0].glyph_count = 3U; lines[0].baseline_y = 20.0F;
    lines[1].glyph_start = 2U; lines[1].glyph_count = 1U; lines[1].baseline_y = 40.0F;
    progpu_native_hinted_glyph_resource_view view{};
    view.layout.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
    view.counts.positioned_glyph_count = 3U; view.counts.line_count = 1U; view.counts.run_count = 1U;
    view.font_source_count = 1U; view.font_sources = &font; view.runs = &run;
    view.positioned_glyphs = glyphs.data(); view.positioned_owners = owners.data();
    view.positioned_bidi_levels = levels.data(); view.lines = lines.data();
    std::array<std::uint32_t, 3U> indices{2U, 1U, 2U}; // Original repeats and source permutation.
    std::array<double, 3U> advances{7.0, 0.0, 7.0};
    std::array<progpu_native_hinted_source_glyph_offset, 3U> offsets{{{-9.0, 0.0}, {-11.0, -1.5}, {-16.0, 0.0}}};
    progpu_native_hinted_source_glyph_frame output{};
    const auto invoke = [&] { return validate_hinted_source_frame(view, nominal, indices, 10.0F, {5.0F, 7.0F}, advances, offsets, output); };
    require(invoke() == PROGPU_NATIVE_STATUS_SUCCESS);
    require(output.line_index == 0U && output.font_index == 0U && output.bidi_level == 1 && output.paragraph_baseline_y == 20.0F &&
        output.source_baseline_origin.x == 5.0F && output.source_baseline_origin.y == 7.0F &&
        output.paragraph_origin.x == 5.0F && output.paragraph_origin.y == -13.0F &&
        output.baseline_relative_origin.x == 0.0F && output.baseline_relative_origin.y == -20.0F);
    const auto sentinel = output;
    const auto rejected = [&](progpu_native_status expected) {
        require(invoke() == expected && std::memcmp(&output, &sentinel, sizeof(output)) == 0);
    };
    offsets.back().x = std::nextafter(-16.0, 0.0); rejected(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); offsets.back().x = -16.0;
    advances.back() = std::nextafter(7.0, 0.0); rejected(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); advances.back() = 7.0;
    indices.back() = 3U; rejected(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); indices.back() = 2U;
    levels.back() = 0; rejected(PROGPU_NATIVE_STATUS_UNSUPPORTED); levels.back() = 1;
    view.layout.direction = PROGPU_NATIVE_TEXT_DIRECTION_TOP_TO_BOTTOM;
    rejected(PROGPU_NATIVE_STATUS_UNSUPPORTED); view.layout.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
    glyphs.back().advance_y = 1.0F; rejected(PROGPU_NATIVE_STATUS_UNSUPPORTED); glyphs.back().advance_y = 0.0F;
    view.counts.line_count = 2U; lines[0].glyph_count = 2U;
    rejected(PROGPU_NATIVE_STATUS_UNSUPPORTED); view.counts.line_count = 1U; lines[0].glyph_count = 3U;
    lines[0].baseline_y = 0.1F; rejected(PROGPU_NATIVE_STATUS_UNSUPPORTED); lines[0].baseline_y = 20.0F;
    // LTR uses the same original selected glyphs and exact measured prefix.
    levels.fill(0); offsets = {{{4.0, 0.0}, {-3.0, -1.5}, {-3.0, 0.0}}};
    require(invoke() == PROGPU_NATIVE_STATUS_SUCCESS && output.bidi_level == 0);
}
}
int main() {
    try { controls(); return 0; }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
