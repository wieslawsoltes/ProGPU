#pragma once

#include "progpu_native_open_type_gpos_internal.hpp"
#include "progpu_native_space_fallback_internal.hpp"
#include "progpu_native_fallback_marks_internal.hpp"
#include "progpu_native_arabic_stretch_internal.hpp"

namespace progpu::native::text::detail {
struct device_shape_run_frame final {
    gpos_device_frame gpos{};
    device_space_advances spaces{};
    device_mark_extents marks{};
    device_stretch_metrics stretch{};
    std::span<std::uint32_t> descriptor_mapping{};
    std::span<std::int32_t> stretch_widths{};
};

// Synchronous native-only services. Preparation captures the original final
// substituted IDs once, initializes device metrics and retains their owned
// generation until this whole run ends. No managed callback or GPU work.
struct device_shape_run_services final {
    void* owner = nullptr;
    bool (*prepare)(void*, const sfnt_font_view&, const open_type_shape_run_options&,
        std::span<shaping_glyph>, device_shape_run_frame&, font_error*) noexcept = nullptr;
    // Verification owns an independent fragment generation, never overwriting
    // the original prepared run or its descriptor mapping.
    bool (*shape_fragment)(void*, const sfnt_font_view&, std::span<const unicode_scalar>,
        const open_type_shape_run_options&, std::span<shaping_glyph>, open_type_shape_run_scratch,
        std::uint32_t&, font_error*, const open_type_shape_plan*) noexcept = nullptr;
};

bool try_shape_device_open_type_run(const sfnt_font_view&, std::span<const unicode_scalar>,
    const open_type_shape_run_options&, std::span<shaping_glyph>, open_type_shape_run_scratch,
    const device_shape_run_services&, std::uint32_t&, font_error* = nullptr,
    const open_type_shape_plan* = nullptr) noexcept;

// Shares the original script/feature policy for bounded staging capacity.
// A true value still requires a matching original GSUB stch lookup in the font.
bool device_shape_uses_arabic_stretch(const open_type_shape_run_options&) noexcept;
} // namespace progpu::native::text::detail
