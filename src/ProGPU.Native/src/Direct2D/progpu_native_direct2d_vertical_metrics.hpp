#pragma once

#include "progpu_native_direct2d_font_capture.hpp"

#include <cstdint>
#include <memory>

namespace progpu::native::direct2d {

enum class original_vertical_origin_kind : std::uint8_t {
    unavailable, true_type_bounds, cff_vorg
};

struct original_vertical_glyph_metrics final {
    std::uint16_t advance_height = 0U;
    std::int16_t top_side_bearing = 0;
    std::int32_t top_origin = 0;
    std::int32_t bottom_origin = 0;
    bool has_origin = false;
    original_vertical_origin_kind origin_kind = original_vertical_origin_kind::unavailable;
};

// Source-strict default-instance metadata under the exact original file owner.
// This class neither selects sideways rendering nor applies variable metrics.
// Create it only when the caller needs vertical metadata; malformed vertical
// tables must not change ordinary horizontal font preparation.
class retained_original_vertical_metrics final {
public:
    ~retained_original_vertical_metrics();
    retained_original_vertical_metrics(const retained_original_vertical_metrics&) = delete;
    retained_original_vertical_metrics& operator=(const retained_original_vertical_metrics&) = delete;
    [[nodiscard]] static com::result create(std::shared_ptr<const original_font_capture> source,
        std::shared_ptr<const retained_original_vertical_metrics>& output) noexcept;
    [[nodiscard]] const std::shared_ptr<const original_font_capture>& source() const noexcept;
    // Both absent tables are a distinct successful metadata result, never
    // hhea-derived synthetic metrics. One missing table is malformed.
    [[nodiscard]] bool has_metrics() const noexcept;
    // Missing metrics: not_implemented. Missing original origin: success with
    // has_origin=false and the actual advance/bearing. Caller output is atomic.
    [[nodiscard]] com::result read_base(std::uint16_t glyph,
        original_vertical_glyph_metrics& output) const noexcept;
private:
    struct state;
    explicit retained_original_vertical_metrics(std::unique_ptr<state> value) noexcept;
    std::unique_ptr<state> state_;
};

} // namespace progpu::native::direct2d
