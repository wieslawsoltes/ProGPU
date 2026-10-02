#pragma once

#include "progpu_native_text.hpp"

#include <cstdint>

namespace progpu::native::text {

// Explicit capture choices, not an automatic WPF/Display policy. In particular,
// independent source observations must establish midpoint selection before a
// source provider can advertise one of these choices as Display-compatible.
enum class hinted_source_em_policy : std::uint32_t {
    exact_26_6,
    nearest_half_up,
    nearest_ties_to_even
};

enum class hinted_source_advance_policy : std::uint32_t {
    unchanged,
    physical_ties_to_even
};

struct hinted_source_style final {
    double em_size = 0.0;
    double pixels_per_dip = 0.0;
    hinted_source_em_policy em_policy = hinted_source_em_policy::exact_26_6;
    hinted_source_advance_policy advance_policy = hinted_source_advance_policy::unchanged;
};

struct hinted_source_device_selection final {
    std::uint32_t pixels_per_em_26_6 = 0U;
    float logical_units_per_physical_pixel = 0.0F;
};

// Resolves only the native capture frame. Original source doubles remain owned
// separately and must never be replaced by the selected physical em or its
// float conversion. Failure leaves the output unchanged.
bool resolve_hinted_source_device(const hinted_source_style& source,
    hinted_source_device_selection& result) noexcept;

// Raw post-GPOS glyphs remain immutable. This derives an explicitly selected
// fitting advance, retaining every identity, flag, offset and Y convention.
// Offset/source-GlyphRun conversion and unsafe-boundary recomposition are
// separate required contracts; this helper does not admit either one.
bool project_hinted_source_advance(const shaping_glyph& original,
    hinted_source_advance_policy policy, shaping_glyph& result) noexcept;

} // namespace progpu::native::text
