#ifndef PROGPU_NATIVE_HINTED_FONT_HPP
#define PROGPU_NATIVE_HINTED_FONT_HPP

#include "progpu_native_font_source.hpp"

#include <cstddef>
#include <cstdint>
#include <memory>
#include <span>
#include <vector>

namespace progpu::native::text {

enum class font_hint_policy : std::uint32_t {
    truetype_35 = 35U,
    truetype_40 = 40U,
};

enum class hinted_font_error : std::uint32_t {
    none,
    invalid_argument,
    invalid_font,
    unsupported_font,
    dependency_mismatch,
    hinting_failed,
    resource_exhausted,
    dependency_unavailable,
};

struct hinted_font_configuration final {
    std::uint32_t x_pixels_per_em_26_6 = 0U;
    std::uint32_t y_pixels_per_em_26_6 = 0U;
    font_hint_policy policy = font_hint_policy::truetype_40;
    std::uint32_t x_phase_26_6 = 0U;
    std::uint32_t y_phase_26_6 = 0U;
    // Variable TrueType fonts require every axis in original fvar order.
    // Empty coordinates admit only a non-variable font, never a guessed default.
    std::span<const std::int32_t> variation_coordinates_16_16{};
};

// Actual selected native size, not requested em / UPM or rounded ppem inferred
// by a caller. Raw driver-wide metrics are metadata, not glyph ink or a source
// line-height policy. Keep design-unit values separate from 26.6 device values.
struct hinted_font_device_frame final {
    std::uint32_t units_per_em = 0U;
    std::uint32_t x_pixels_per_em = 0U;
    std::uint32_t y_pixels_per_em = 0U;
    long x_scale_16_16 = 0;
    long y_scale_16_16 = 0;
    long driver_ascender_26_6 = 0;
    long driver_descender_26_6 = 0;
    long driver_height_26_6 = 0;
    long driver_maximum_advance_26_6 = 0;
    std::int32_t design_ascender = 0;
    std::int32_t design_descender = 0;
    std::int32_t design_height = 0;
    std::int32_t design_maximum_advance = 0;
    bool operator==(const hinted_font_device_frame&) const = default;
};

struct hinted_font_identity final {
    std::shared_ptr<const owned_font_source> source{};
    std::vector<std::int32_t> variation_coordinates_16_16{};
    std::uint32_t x_pixels_per_em_26_6 = 0U;
    std::uint32_t y_pixels_per_em_26_6 = 0U;
    font_hint_policy policy = font_hint_policy::truetype_40;
    std::uint32_t x_phase_26_6 = 0U;
    std::uint32_t y_phase_26_6 = 0U;
    hinted_font_device_frame device_frame{};
};

// Private C++ representation, not a wire record. The native dependency's exact
// signed-long 26.6 coordinates are retained without narrowing or float rounding.
// A later transport must independently validate and convert its fixed-width ABI.
struct hinted_outline_point final {
    long x_26_6 = 0;
    long y_26_6 = 0;
    bool operator==(const hinted_outline_point&) const = default;
};

struct hinted_glyph final {
    std::uint32_t glyph_index = 0U;
    long advance_x_26_6 = 0;
    long advance_y_26_6 = 0;
    long horizontal_bearing_x_26_6 = 0;
    long horizontal_bearing_y_26_6 = 0;
    long width_26_6 = 0;
    long height_26_6 = 0;
    long horizontal_advance_26_6 = 0;
    long vertical_bearing_x_26_6 = 0;
    long vertical_bearing_y_26_6 = 0;
    long vertical_advance_26_6 = 0;
    long linear_horizontal_advance_16_16 = 0;
    long linear_vertical_advance_16_16 = 0;
    long left_side_bearing_delta_26_6 = 0;
    long right_side_bearing_delta_26_6 = 0;
    int outline_flags = 0;
    std::vector<hinted_outline_point> points{};
    std::vector<std::uint8_t> tags{};
    std::vector<std::int16_t> contour_ends{};
    bool operator==(const hinted_glyph&) const = default;
};

struct hinted_glyph_batch final {
    std::shared_ptr<const hinted_font_identity> identity{};
    // Exactly one retained descriptor per original input ID, including repeats.
    std::vector<hinted_glyph> glyphs{};
};

class hinted_font final {
public:
    ~hinted_font();
    hinted_font(const hinted_font&) = delete;
    hinted_font& operator=(const hinted_font&) = delete;

    static bool try_create(std::span<const std::byte> font_bytes,
        std::uint32_t face_index, const hinted_font_configuration& configuration,
        std::unique_ptr<hinted_font>& result, hinted_font_error& error) noexcept;

    // Context-owned immutable sources share their bytes across configurations;
    // source identity remains alive until every face and snapshot releases it.
    static bool try_create(std::shared_ptr<const owned_font_source> source,
        const hinted_font_configuration& configuration,
        std::unique_ptr<hinted_font>& result, hinted_font_error& error) noexcept;

    // Publication is whole-batch only. On any later invalid ID, hint fault or
    // allocation failure, result remains the caller's previous exact generation.
    bool try_capture(std::span<const std::uint32_t> glyph_indices,
        std::shared_ptr<const hinted_glyph_batch>& result,
        hinted_font_error& error) noexcept;

private:
    struct state;
    explicit hinted_font(std::unique_ptr<state> value) noexcept;
    std::unique_ptr<state> state_;
};

} // namespace progpu::native::text

#endif
