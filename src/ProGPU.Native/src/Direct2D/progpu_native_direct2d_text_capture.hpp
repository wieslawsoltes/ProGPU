#pragma once

#include "progpu_native_com.hpp"

#include <cmath>
#include <cstdint>
#include <new>
#include <utility>
#include <vector>

namespace progpu::native::direct2d {

// Source values, not an OS-default policy or a rasterizer profile. In particular,
// an absent rendering-parameters object must not manufacture monitor settings.
struct text_rendering_values final {
    float gamma = 0.0F;
    float enhanced_contrast = 0.0F;
    float cleartype_level = 0.0F;
    std::uint32_t pixel_geometry = 0U;
    std::uint32_t rendering_mode = 0U;
    bool supplied = false;

    [[nodiscard]] bool valid() const noexcept
    {
        return !supplied || (std::isfinite(gamma) && gamma > 0.0F &&
            std::isfinite(enhanced_contrast) && enhanced_contrast >= 0.0F &&
            std::isfinite(cleartype_level) && cleartype_level >= 0.0F &&
            cleartype_level <= 1.0F && pixel_geometry <= 2U &&
            rendering_mode <= 6U);
    }
};

// Both canonical IDWriteRenderingParams and the portable vtable have these
// original getters. The caller retains the COM object and calls outside its
// recorder mutex: these are source callbacks, not safe field reads.
template<typename Parameters>
[[nodiscard]] com::result capture_text_rendering_values(
    Parameters* parameters, text_rendering_values& output) noexcept
{
    text_rendering_values candidate{};
    if (parameters != nullptr) {
        candidate.gamma = parameters->GetGamma();
        candidate.enhanced_contrast = parameters->GetEnhancedContrast();
        candidate.cleartype_level = parameters->GetClearTypeLevel();
        candidate.pixel_geometry = static_cast<std::uint32_t>(parameters->GetPixelGeometry());
        candidate.rendering_mode = static_cast<std::uint32_t>(parameters->GetRenderingMode());
        candidate.supplied = true;
    }
    if (!candidate.valid()) return com::invalid_argument;
    output = candidate;
    return com::ok;
}

// One original shaped run, copied before any external font/parameter callback.
// Optional advances/offsets remain absent, not replaced with guessed metrics.
// Storage is O(G), bounded by the existing Direct2D one-million-glyph budget;
// no shaping, font-table reads, rasterization or GPU initialization occurs here.
template<typename Offset>
class glyph_run_capture final {
public:
    static constexpr std::uint32_t maximum_glyph_count = 1U << 20U;

    template<typename ValidateOffset>
    [[nodiscard]] com::result capture(
        const std::uint16_t* indices, const float* advances,
        const Offset* offsets, std::uint32_t count,
        ValidateOffset&& valid_offset) noexcept
    {
        if (count > maximum_glyph_count || (count != 0U && indices == nullptr))
            return com::invalid_argument;
        for (std::uint32_t index = 0U; index < count; ++index) {
            if ((advances != nullptr && !std::isfinite(advances[index])) ||
                (offsets != nullptr && !valid_offset(offsets[index])))
                return com::invalid_argument;
        }
        try {
            glyph_run_capture candidate;
            if (count != 0U) {
                candidate.indices_.assign(indices, indices + count);
                if (advances != nullptr) candidate.advances_.assign(advances, advances + count);
                if (offsets != nullptr) candidate.offsets_.assign(offsets, offsets + count);
            }
            *this = std::move(candidate);
            return com::ok;
        } catch (const std::bad_alloc&) {
            return com::out_of_memory;
        } catch (...) {
            return com::invalid_argument;
        }
    }

    [[nodiscard]] const std::uint16_t* indices() const noexcept { return indices_.data(); }
    [[nodiscard]] const float* advances() const noexcept { return advances_.empty() ? nullptr : advances_.data(); }
    [[nodiscard]] const Offset* offsets() const noexcept { return offsets_.empty() ? nullptr : offsets_.data(); }
    [[nodiscard]] std::uint32_t count() const noexcept { return static_cast<std::uint32_t>(indices_.size()); }

private:
    std::vector<std::uint16_t> indices_;
    std::vector<float> advances_;
    std::vector<Offset> offsets_;
};

} // namespace progpu::native::direct2d
