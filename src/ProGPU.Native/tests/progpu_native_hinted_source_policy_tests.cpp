#include "../src/Text/Font/progpu_native_hinted_source_policy.hpp"

#include <array>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <limits>
#include <source_location>
#include <stdexcept>
#include <string>

namespace {
using namespace progpu::native::text;
std::uint32_t checks = 0U;
void require(bool value, std::source_location at = std::source_location::current()) {
    ++checks;
    if (!value) throw std::runtime_error("source device policy control at " + std::to_string(at.line()));
}

bool same(const shaping_glyph& a, const shaping_glyph& b) {
    return a.glyph_id == b.glyph_id && a.code_point == b.code_point && a.cluster == b.cluster &&
        a.flags == b.flags && a.advance_x == b.advance_x && a.advance_y == b.advance_y &&
        a.offset_x == b.offset_x && a.offset_y == b.offset_y;
}

void capture_controls() {
    struct sample final { double em, dpi; std::uint32_t exact, half_up, even; };
    constexpr std::array samples{
        sample{13.0, 1.0, 832U, 832U, 832U},
        sample{13.0, 1.5, 1248U, 1280U, 1280U},
        sample{17.0, 1.5, 1632U, 1664U, 1664U},
        sample{18.5, 1.0, 1184U, 1216U, 1152U},
        sample{20.5, 1.0, 1312U, 1344U, 1280U},
        sample{13.25, 1.25, 1060U, 1088U, 1088U},
        sample{13.125, 2.0, 1680U, 1664U, 1664U},
        sample{0.5, 1.0, 32U, 64U, 0U}};
    for (const auto value : samples) {
        for (const auto mode : {hinted_source_em_policy::exact_26_6,
            hinted_source_em_policy::nearest_half_up, hinted_source_em_policy::nearest_ties_to_even}) {
            const auto expected = mode == hinted_source_em_policy::exact_26_6 ? value.exact :
                mode == hinted_source_em_policy::nearest_half_up ? value.half_up : value.even;
            const hinted_source_style source{value.em, value.dpi, mode,
                hinted_source_advance_policy::physical_ties_to_even};
            hinted_source_device_selection selected{71U, 73.0F};
            const auto accepted = resolve_hinted_source_device(source, selected);
            require(accepted == (expected != 0U));
            require(expected == 0U ? selected.pixels_per_em_26_6 == 71U && selected.logical_units_per_physical_pixel == 73.0F :
                selected.pixels_per_em_26_6 == expected && selected.logical_units_per_physical_pixel == static_cast<float>(1.0 / value.dpi));
            require(source.em_size == value.em && source.pixels_per_dip == value.dpi);
        }
    }
    // Capture selection does not quantize the original arbitrary double em.
    const auto fractional = std::nextafter(17.0, 18.0);
    hinted_source_style source{fractional, 1.25, hinted_source_em_policy::nearest_half_up,
        hinted_source_advance_policy::physical_ties_to_even};
    hinted_source_device_selection selected{};
    require(resolve_hinted_source_device(source, selected) && selected.pixels_per_em_26_6 == 21U * 64U &&
        source.em_size == fractional && source.em_size != static_cast<double>(static_cast<float>(source.em_size)));
    source.em_policy = hinted_source_em_policy::exact_26_6;
    require(!resolve_hinted_source_device(source, selected) && selected.pixels_per_em_26_6 == 21U * 64U);
    for (unsigned int invalid = 0U; invalid < 13U; ++invalid) {
        auto bad = hinted_source_style{13.0, 1.5, hinted_source_em_policy::nearest_half_up,
            hinted_source_advance_policy::physical_ties_to_even};
        if (invalid == 0U) bad.em_size = 0.0;
        if (invalid == 1U) bad.em_size = -1.0;
        if (invalid == 2U) bad.em_size = std::numeric_limits<double>::infinity();
        if (invalid == 3U) bad.em_size = std::numeric_limits<double>::quiet_NaN();
        if (invalid == 4U) bad.pixels_per_dip = 0.0;
        if (invalid == 5U) bad.pixels_per_dip = -1.0;
        if (invalid == 6U) bad.pixels_per_dip = std::numeric_limits<double>::infinity();
        if (invalid == 7U) bad.pixels_per_dip = std::numeric_limits<double>::quiet_NaN();
        if (invalid == 8U) bad.em_size = std::numeric_limits<double>::max();
        if (invalid == 9U) bad.em_policy = static_cast<hinted_source_em_policy>(3U);
        if (invalid == 10U) bad.advance_policy = static_cast<hinted_source_advance_policy>(2U);
        if (invalid == 11U) bad.em_size = 0.125;
        if (invalid == 12U) bad.em_size = static_cast<double>(INT32_MAX) / 64.0;
        selected = {71U, 73.0F};
        require(!resolve_hinted_source_device(bad, selected) && selected.pixels_per_em_26_6 == 71U &&
            selected.logical_units_per_physical_pixel == 73.0F);
    }
}

void advance_controls() {
    const shaping_glyph identity{71U, 0x0627U, 19, shaping_glyph_flags::unsafe_to_break, 83, -89, 97, -101};
    // Independent arithmetic oracle: the exact midpoint is compared in pixels,
    // using floor on signed values rather than production's magnitude quotient.
    for (std::int32_t advance = -257; advance <= 257; ++advance) {
        auto input = identity; input.advance_x = advance;
        auto unchanged = identity;
        require(project_hinted_source_advance(input, hinted_source_advance_policy::unchanged, unchanged) && same(input, unchanged));
        const double pixels = static_cast<double>(advance) / 64.0;
        double expected = std::floor(pixels);
        const double remainder = pixels - expected;
        if (remainder > 0.5 || (remainder == 0.5 && std::fmod(expected, 2.0) != 0.0)) expected += 1.0;
        auto expected_glyph = input; expected_glyph.advance_x = static_cast<std::int32_t>(expected * 64.0);
        auto rounded = identity;
        require(project_hinted_source_advance(input, hinted_source_advance_policy::physical_ties_to_even, rounded) &&
            same(rounded, expected_glyph) && input.advance_x == advance);
    }
    for (const auto value : {INT32_MIN, INT32_MIN + 32, INT32_MAX - 32}) {
        auto input = identity; input.advance_x = value;
        auto output = identity;
        require(project_hinted_source_advance(input, hinted_source_advance_policy::physical_ties_to_even, output));
        const auto expected = value < 0 ? INT32_MIN : INT32_MAX - 63;
        require(output.advance_x == expected && output.flags == input.flags && output.offset_x == input.offset_x);
    }
    for (const auto value : {INT32_MAX - 31, INT32_MAX}) {
        auto input = identity; input.advance_x = value;
        auto output = identity;
        require(!project_hinted_source_advance(input, hinted_source_advance_policy::physical_ties_to_even, output) && same(output, identity));
    }
    auto output = identity;
    require(!project_hinted_source_advance(identity, static_cast<hinted_source_advance_policy>(2U), output) && same(output, identity));
}
} // namespace

int main() {
    try {
        capture_controls(); advance_controls();
        std::cout << checks << " native source capture/advance policy controls passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
