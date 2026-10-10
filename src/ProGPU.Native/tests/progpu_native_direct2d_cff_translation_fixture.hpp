#pragma once

#include "progpu_native_direct2d_cff_font_fixture.hpp"

namespace progpu::native::direct2d::tests {

template<class Observe>
void for_each_cff_translation_control(Observe observe)
{
    // Independent original SDK results. The long literals are adjacent binary32
    // values around +/-2^-17, not an epsilon chosen to fit prepared geometry.
    constexpr std::array<std::pair<std::string_view, bool>, 13U> translations{{
        {"0", true}, {"0.0000075", true}, {"-0.0000075", true},
        {"0.0000076293936217552982270717620849609375", true},
        {"0.00000762939407650264911353588104248046875", false},
        {"0.00000762939453125", false},
        {"-0.0000076293936217552982270717620849609375", true},
        {"-0.00000762939407650264911353588104248046875", false},
        {"-0.00000762939453125", false},
        {"0.0000152587890625", false}, {"-0.0000152587890625", false},
        {"16", false}, {"-16", false}
    }};
    std::size_t index = 0U;
    for (const auto kind : {cff_font_kind::cff1_affine, cff_font_kind::cff1_cid,
                           cff_font_kind::cff1_cid_inherited}) {
        for (std::size_t matrix = 0U; matrix < 3U; ++matrix) {
            for (std::size_t axis = 4U; axis < 6U; ++axis) {
                for (const auto& [translation, zero] : translations) {
                    cff_font_matrix_control control;
                    control.matrices[matrix][axis] = translation;
                    const bool top = matrix == 0U;
                    const bool first = top || (matrix == 1U && kind == cff_font_kind::cff1_cid);
                    // An explicit first-FD translation suppresses the entire
                    // original face. A later FD affects only its selected glyph.
                    const bool second = first || (matrix == 2U && kind != cff_font_kind::cff1_affine);
                    observe(index++, kind, control, std::array{true, zero || !first, zero || !second});
                }
            }
        }
    }
    // Equal and opposite translations do not cancel the source's per-DICT ink
    // decision. The selected FD and original composed matrix are both retained.
    cff_font_matrix_control cancellation;
    cancellation.matrices[0][4] = "-0.0078125";
    cancellation.matrices[1][4] = "8";
    observe(index++, cff_font_kind::cff1_cid, cancellation, std::array{true, false, false});
}

inline cff_glyph_expectation expected_cff_translation_glyph(std::uint16_t glyph,
    const std::array<bool, 3U>& emits)
{
    auto expected = expected_cff_glyph(cff_font_kind::cff1_default, 0U, glyph);
    if (!emits.at(glyph)) { expected.count = 0U; expected.segments = {}; }
    return expected;
}

} // namespace progpu::native::direct2d::tests
