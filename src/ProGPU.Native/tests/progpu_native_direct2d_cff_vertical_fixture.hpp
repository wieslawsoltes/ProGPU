#pragma once

#include "progpu_native_direct2d_vertical_font_fixture.hpp"

namespace progpu::native::direct2d::tests {

// Two independently authored symmetric cubic arches. Their maxima occur at
// exactly t=1/2: glyph1=300 (or301.5), glyph2=280. The control points reach
// 400/402 and380, so a control envelope cannot satisfy the origin oracle.
inline std::array<progpu_native_path_segment, 2U> cff_vertical_contours(std::uint16_t glyph, bool fractional)
{
    if (glyph == 1U) return {{{{20, 0}, {20, fractional ? 402.0F : 400.0F},
        {300, fractional ? 402.0F : 400.0F}, {300, 0}, PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0, 0, 0},
        {{300, 0}, {20, 0}, {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0, 0, 0}}};
    if (glyph == 2U) return {{{{0, -20}, {0, 380}, {200, 380}, {200, -20}, PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0, 0, 0},
        {{200, -20}, {0, -20}, {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0, 0, 0}}};
    throw std::out_of_range("authored CFF vertical contour glyph");
}

inline std::vector<std::byte> make_cff_vertical_font(bool fractional = false)
{
    using namespace cff_font_wire;
    std::vector<bytes> programs;
    for (std::uint16_t glyph = 0U; glyph < 3U; ++glyph) {
        bytes program; number(program, glyph == 0U ? 500 : glyph == 1U ? 600 : 700);
        if (glyph != 0U) {
            number(program, glyph == 1U ? 20 : 0); number(program, glyph == 1U ? 0 : -20); op(program, 21U);
            const auto height = glyph == 1U && fractional ? 402 : 400;
            for (const auto value : {0, height, glyph == 1U ? 280 : 200, 0, 0, -height}) number(program, value);
            op(program, 8U); // rrcurveto; endchar retains the closing straight edge.
        }
        op(program, 14U); programs.push_back(std::move(program));
    }
    std::uint32_t charset = 0U, charstrings = 0U;
    const auto prefix = [&] {
        bytes top;
        for (const auto value : {0, -20, 300, fractional ? 302 : 300}) number(top, value);
        op(top, 5U); offset(top, charset); op(top, 15U); offset(top, charstrings); op(top, 17U);
        number(top, 0); number(top, 0); op(top, 18U);
        bytes result{std::byte{1}, std::byte{0}, std::byte{4}, std::byte{4}};
        append(result, index({ascii("ProGPUContourOrigin-Regular")}, false));
        append(result, index({top}, false)); append(result, index({}, false)); append(result, index({}, false));
        return result;
    };
    charset = static_cast<std::uint32_t>(prefix().size()); charstrings = charset + 5U;
    auto cff = prefix(); cff.push_back(std::byte{0});
    const auto at = cff.size(); cff.resize(at + 4U); put16(cff, at, 34U); put16(cff, at + 2U, 35U);
    append(cff, index(programs, false));
    auto tables = vertical_font_wire::original_tables(make_cff_font(cff_font_kind::cff1_default));
    vertical_font_wire::set(tables, 0x43464620U, std::move(cff));
    auto vhea = vertical_font_wire::header(false, true);
    put16(vhea, 12U, static_cast<std::uint16_t>(-40)); put16(vhea, 16U, fractional ? 382U : 380U);
    vertical_font_wire::set(tables, 0x76686561U, std::move(vhea));
    auto vmtx = vertical_font_wire::metrics(false, true);
    put16(vmtx, 6U, 80U); put16(vmtx, 10U, static_cast<std::uint16_t>(-40));
    vertical_font_wire::set(tables, 0x766D7478U, std::move(vmtx));
    for (auto& entry : tables) {
        if (entry.tag == 0x68656164U) {
            put16(entry.data, 36U, 0U); put16(entry.data, 38U, static_cast<std::uint16_t>(-20));
            put16(entry.data, 40U, 300U); put16(entry.data, 42U, fractional ? 302U : 300U);
        } else if (entry.tag == 0x686D7478U) {
            put16(entry.data, 6U, 20U); put16(entry.data, 10U, 0U);
        }
    }
    // No VORG is included. Every SFNT table/file checksum is independently
    // assembled by the original fixture writer, not by a product font parser.
    return vertical_font_wire::assemble(std::move(tables), 0x4F54544FU);
}
} // namespace progpu::native::direct2d::tests
