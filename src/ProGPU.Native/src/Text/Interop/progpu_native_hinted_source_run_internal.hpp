#pragma once
#include "progpu_native_hinted_paragraph_internal.hpp"
#include "progpu_native_text_source.h"

namespace progpu::native::text {
// Original-library resource only; the C adapter owns pointer/capacity alias checks.
progpu_native_status copy_hinted_source_metrics(const hinted_paragraph_generation& paragraph,
    const progpu_native_hinted_glyph_resource_view& resource,
    std::span<const progpu_native_hinted_glyph_nominal_metrics> nominal,
    std::span<const std::uint32_t> indices, double em, double dpi,
    std::span<double> advances, std::span<progpu_native_hinted_source_glyph_offset> offsets) noexcept;
progpu_native_status validate_hinted_source_run(const hinted_paragraph_generation& paragraph,
    const progpu_native_hinted_glyph_resource_view& resource,
    std::span<const progpu_native_hinted_glyph_nominal_metrics> nominal,
    std::span<const std::uint32_t> indices, double em, double dpi,
    progpu_native_hinted_source_glyph_offset baseline,
    std::span<const double> advances, std::span<const progpu_native_hinted_source_glyph_offset> offsets,
    progpu_native_hinted_source_run_frame& frame) noexcept;
} // namespace progpu::native::text
