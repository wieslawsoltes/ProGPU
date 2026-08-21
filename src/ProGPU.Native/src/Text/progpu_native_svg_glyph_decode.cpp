#include "progpu_native_svg_document_internal.hpp"

#include <algorithm>

// The public two-pass boundary keeps caller output transactional. XML parsing
// and reference indexing are bounded cold-cache work; retained replay consumes
// only the emitted pointer-free canonical records.
namespace progpu::native::text {
namespace {

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

svg_glyph_requirements requirements_of(
    const svg_document_detail::decoded_glyph& glyph) noexcept {
    return {glyph.layers.size(), glyph.segments.size(), glyph.brushes.size(),
        glyph.gradient_stops.size()};
}

} // namespace

bool try_get_svg_glyph_requirements(
    std::string_view xml,
    std::uint16_t glyph_index,
    std::uint16_t units_per_em,
    svg_glyph_requirements& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    svg_document_detail::decoded_glyph glyph{};
    if (!svg_document_detail::decode_glyph(
            xml, glyph_index, units_per_em, glyph)) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    result = requirements_of(glyph);
    return true;
}

bool try_decode_svg_glyph(
    std::string_view xml,
    std::uint16_t glyph_index,
    std::uint16_t units_per_em,
    std::span<svg_glyph_layer> layers,
    std::span<progpu_native_path_segment> segments,
    std::span<progpu_native_scene_brush> brushes,
    std::span<progpu_native_scene_gradient_stop> gradient_stops,
    svg_glyph_requirements& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    svg_document_detail::decoded_glyph glyph{};
    if (!svg_document_detail::decode_glyph(
            xml, glyph_index, units_per_em, glyph)) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    result = requirements_of(glyph);
    if (layers.size() < result.layer_count ||
        segments.size() < result.segment_count ||
        brushes.size() < result.brush_count ||
        gradient_stops.size() < result.gradient_stop_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    std::copy(glyph.layers.begin(), glyph.layers.end(), layers.begin());
    std::copy(glyph.segments.begin(), glyph.segments.end(), segments.begin());
    std::copy(glyph.brushes.begin(), glyph.brushes.end(), brushes.begin());
    std::copy(glyph.gradient_stops.begin(), glyph.gradient_stops.end(),
        gradient_stops.begin());
    return true;
}

} // namespace progpu::native::text
