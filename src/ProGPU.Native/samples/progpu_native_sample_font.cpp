#include "progpu_native_sample_font.hpp"

#include "progpu_native_text.hpp"

#include <cstddef>
#include <fstream>
#include <limits>
#include <span>
#include <utility>
#include <vector>

namespace progpu::native::sample {
namespace {

bool try_read_file(
    const std::string& path,
    std::vector<std::byte>& bytes) {
    std::ifstream input(path, std::ios::binary | std::ios::ate);
    if (!input) {
        return false;
    }
    const auto end = static_cast<std::streamoff>(input.tellg());
    if (end <= 0 ||
        static_cast<std::uintmax_t>(end) >
            std::numeric_limits<std::size_t>::max() ||
        end > std::numeric_limits<std::streamsize>::max()) {
        return false;
    }
    bytes.resize(static_cast<std::size_t>(end));
    input.seekg(0, std::ios::beg);
    input.read(
        reinterpret_cast<char*>(bytes.data()),
        static_cast<std::streamsize>(bytes.size()));
    return input.good();
}

const char* error_name(text::font_error error) noexcept {
    switch (error) {
        case text::font_error::none:
            return "none";
        case text::font_error::invalid_argument:
            return "invalid argument";
        case text::font_error::unsupported_container:
            return "unsupported container";
        case text::font_error::invalid_collection:
            return "invalid collection";
        case text::font_error::invalid_face:
            return "invalid face";
        case text::font_error::truncated_directory:
            return "truncated directory";
        case text::font_error::invalid_glyph:
            return "invalid glyph";
        case text::font_error::insufficient_buffer:
            return "insufficient buffer";
        case text::font_error::invalid_container:
            return "invalid container";
        case text::font_error::invalid_compressed_data:
            return "invalid compressed data";
        case text::font_error::verification_failed:
            return "shaping verification failed";
    }
    return "unknown font error";
}

} // namespace

bool try_load_font_glyph(
    const std::string& font_path,
    std::uint32_t code_point,
    decoded_font_glyph& result,
    std::string& error) {
    std::vector<std::byte> font_bytes;
    if (!try_read_file(font_path, font_bytes)) {
        error = "could not read the font file";
        return false;
    }

    text::font_error font_error = text::font_error::none;
    text::sfnt_font_view font;
    if (!text::sfnt_font_view::try_create(
            font_bytes,
            0U,
            font,
            &font_error)) {
        error = error_name(font_error);
        return false;
    }

    text::sfnt_header_metrics header{};
    std::uint16_t glyph_index = 0U;
    text::sfnt_glyph_data_view glyph_data{};
    text::sfnt_expanded_glyph_requirements requirements{};
    if (!font.try_get_header_metrics(header) ||
        !font.try_get_glyph_index(code_point, glyph_index) ||
        glyph_index == 0U ||
        !font.try_get_glyph_data(glyph_index, glyph_data) ||
        glyph_data.empty() ||
        !font.try_get_expanded_glyph_requirements(
            glyph_index,
            requirements,
            &font_error)) {
        error = font_error == text::font_error::none
            ? "the requested glyph is unavailable"
            : error_name(font_error);
        return false;
    }

    std::vector<std::uint16_t> contour_scratch(
        requirements.simple_contour_scratch_count);
    std::vector<text::sfnt_outline_point> point_scratch(
        requirements.simple_point_scratch_count);
    std::vector<progpu_native_point> points(requirements.point_count);
    std::vector<progpu_native_path_segment> segments(
        requirements.path_segment_count);
    std::uint32_t points_written = 0U;
    std::uint32_t segments_written = 0U;
    if (!font.try_decode_glyph_outline(
            glyph_index,
            contour_scratch,
            point_scratch,
            points,
            segments,
            points_written,
            segments_written,
            &font_error) ||
        points_written != points.size() ||
        segments_written != segments.size()) {
        error = error_name(font_error);
        return false;
    }

    decoded_font_glyph decoded{};
    decoded.segments = std::move(segments);
    decoded.min_x = static_cast<float>(glyph_data.x_min);
    decoded.min_y = static_cast<float>(glyph_data.y_min);
    decoded.max_x = static_cast<float>(glyph_data.x_max);
    decoded.max_y = static_cast<float>(glyph_data.y_max);
    decoded.glyph_index = glyph_index;
    decoded.units_per_em = header.units_per_em;
    result = std::move(decoded);
    error.clear();
    return true;
}

} // namespace progpu::native::sample
