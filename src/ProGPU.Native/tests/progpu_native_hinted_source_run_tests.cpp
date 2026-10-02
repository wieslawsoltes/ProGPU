#include "../src/Text/Interop/progpu_native_hinted_source_run_internal.hpp"

#include <array>
#include <cmath>
#include <cstring>
#include <iostream>
#include <source_location>
#include <stdexcept>
#include <string>

namespace {
using namespace progpu::native::text;
void require(bool value, std::source_location at = std::source_location::current()) {
    if (!value) throw std::runtime_error("double source run control at " + std::to_string(at.line()));
}
void controls() {
    // Double source geometry deliberately cannot equal its float raster shadow.
    // hmtx remains independent of the device advance, including a zero-width mark.
    hinted_paragraph_generation p;
    p.has_source_geometry = true; p.layout.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
    p.source_styles.push_back({10.0, 1.5, hinted_source_em_policy::exact_26_6, hinted_source_advance_policy::unchanged});
    p.runs.resize(1U); p.lines.resize(1U); p.glyphs.resize(3U); p.source_glyphs.resize(3U);
    p.source_lines.push_back({14.0 / 1.5, 0.0, 12.0, 10.0 / 1.5, 10.0 / 1.5, 0.0, 0U, 3U});
    p.positioned_owners.resize(3U); p.logical_owners.resize(3U); p.logical_font_indices.resize(3U); p.bidi_levels.assign(3U, 1);
    std::array<progpu_native_hinted_glyph_nominal_metrics, 3U> nominal{};
    for (std::uint32_t i = 0U; i < 3U; ++i) {
        auto& g = p.glyphs[i]; g.glyph_index = i; g.glyph_id = i; g.cluster = static_cast<std::int32_t>(i);
        auto& s = p.source_glyphs[i]; s.x = i == 0U ? 12.0 / 1.5 : 4.0 / 1.5;
        s.y = p.source_lines[0].baseline_y + (i == 1U ? 1.25 : 0.0);
        s.advance_x = i == 1U ? 0.0 : 7.0 / 1.5; s.cluster = g.cluster;
        g.x = static_cast<float>(s.x); g.y = static_cast<float>(s.y); g.advance_x = static_cast<float>(s.advance_x);
        nominal[i] = {i, 0U, i, i == 1U ? 0U : 500U};
    }
    progpu_native_hinted_glyph_font_source font{}; font.units_per_em = 1000U;
    progpu_native_hinted_glyph_resource_view view{};
    view.font_sources = &font; view.font_source_count = 1U; view.counts.positioned_glyph_count = 3U; view.dpi_scale = 1.5F;
    std::array<std::uint32_t, 3U> indices{2U, 1U, 2U}; // Original repeat/permutation stays source-owned.
    std::array<double, 4U> advances{71.0, 72.0, 73.0, 74.0};
    std::array<progpu_native_hinted_source_glyph_offset, 4U> offsets{{{81, 82}, {83, 84}, {85, 86}, {87, 88}}};
    require(copy_hinted_source_metrics(p, view, nominal, indices, 10.0, 1.5, advances, offsets) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(advances[0] == 7.0 / 1.5 && advances[1] == 0.0 && advances[2] == 7.0 / 1.5 && advances[3] == 74.0);
    require(advances[0] != static_cast<double>(p.glyphs[2].advance_x));
    require(offsets[0].x == -5.0 - 4.0 / 1.5 && offsets[1].y == -1.25 && offsets[3].x == 87.0 && offsets[3].y == 88.0);
    progpu_native_hinted_source_run_frame frame{};
    double source_em = 10.0, source_dpi = 1.5;
    progpu_native_hinted_source_glyph_offset baseline{5.0, p.source_lines[0].baseline_y};
    const auto invoke = [&] { return validate_hinted_source_run(p, view, nominal, indices, source_em, source_dpi, baseline,
        std::span(advances).first(3U), std::span(offsets).first(3U), frame); };
    require(invoke() == PROGPU_NATIVE_STATUS_SUCCESS);
    require(frame.paragraph_baseline_y == 10.0 / 1.5 && frame.bidi_level == 1 && frame.paragraph_origin.y == 0.0 &&
        frame.raster_paragraph_origin.x == 5.0F && frame.raster_paragraph_origin.y == 0.0F);
    const auto saved_frame = frame;
    const auto reject = [&](progpu_native_status expected) {
        require(invoke() == expected && std::memcmp(&frame, &saved_frame, sizeof(frame)) == 0);
    };
    const auto saved_advances = advances; const auto saved_offsets = offsets;
    indices.back() = 3U;
    require(copy_hinted_source_metrics(p, view, nominal, indices, 10.0, 1.5, advances, offsets) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT &&
        std::memcmp(advances.data(), saved_advances.data(), sizeof(advances)) == 0 &&
        std::memcmp(offsets.data(), saved_offsets.data(), sizeof(offsets)) == 0);
    indices.back() = 2U;
    advances.back() = 74.0; advances[2] = static_cast<double>(p.glyphs[2].advance_x);
    reject(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); advances = saved_advances;
    offsets[2].x = std::nextafter(offsets[2].x, 0.0); reject(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); offsets = saved_offsets;
    source_em = std::nextafter(10.0, 11.0); reject(PROGPU_NATIVE_STATUS_UNSUPPORTED); source_em = 10.0;
    source_dpi = std::nextafter(1.5, 2.0); reject(PROGPU_NATIVE_STATUS_UNSUPPORTED); source_dpi = 1.5;
    p.bidi_levels[1] = 3; reject(PROGPU_NATIVE_STATUS_UNSUPPORTED); p.bidi_levels[1] = 1; // Same parity is insufficient.
    p.runs.push_back(p.runs[0]); p.positioned_owners[1].run_index = 1U;
    reject(PROGPU_NATIVE_STATUS_UNSUPPORTED); p.positioned_owners[1].run_index = 0U;
    nominal[1].glyph_id = 99U; reject(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT); nominal[1].glyph_id = 1U;
    baseline.y = 0.0; reject(PROGPU_NATIVE_STATUS_UNSUPPORTED); baseline.y = p.source_lines[0].baseline_y;
    baseline.x = 0.1; reject(PROGPU_NATIVE_STATUS_UNSUPPORTED); baseline.x = 5.0;
    p.has_source_geometry = false; reject(PROGPU_NATIVE_STATUS_UNSUPPORTED); p.has_source_geometry = true;
    p.bidi_levels.assign(3U, 0);
    require(copy_hinted_source_metrics(p, view, nominal, indices, 10.0, 1.5, advances, offsets) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(offsets[0].x == 4.0 / 1.5 && offsets[1].x == 4.0 / 1.5 - 7.0 / 1.5 && invoke() == PROGPU_NATIVE_STATUS_SUCCESS);
}
} // namespace
int main() {
    try { controls(); return 0; }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
