#ifndef PROGPU_NATIVE_SAMPLE_FONT_HPP
#define PROGPU_NATIVE_SAMPLE_FONT_HPP

#include "progpu_native.h"

#include <cstdint>
#include <string>
#include <vector>

namespace progpu::native::sample {

struct decoded_font_glyph final {
    std::vector<progpu_native_path_segment> segments{};
    float min_x = 0.0F;
    float min_y = 0.0F;
    float max_x = 0.0F;
    float max_y = 0.0F;
    std::uint16_t glyph_index = 0U;
    std::uint16_t units_per_em = 0U;
};

bool try_load_font_glyph(
    const std::string& font_path,
    std::uint32_t code_point,
    decoded_font_glyph& result,
    std::string& error);

} // namespace progpu::native::sample

#endif
