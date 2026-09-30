#pragma once

#include "progpu_native_hinted_paragraph_internal.hpp"
#include "../Font/progpu_native_hinted_glyph_frame.hpp"

namespace progpu::native::text {

struct hinted_paragraph_glyph_target final {
    std::uint32_t width = 0U, height = 0U; // Actual physical target pixels.
    float dpi_scale = 0.0F;
    std::uintptr_t target_view = 0U; // Borrowed same-device renderer view.
    progpu_native_point logical_origin{}; // Added once, Y-down.
    progpu_native_color clear_color{};
};

struct hinted_paragraph_outline_owner final {
    std::uint32_t run_index = 0U, descriptor_index = 0U;
    bool operator==(const hinted_paragraph_outline_owner&) const = default;
};

struct hinted_paragraph_draw_owner final {
    std::uint32_t positioned_index = 0U, logical_index = 0U;
    std::uint32_t run_index = 0U, run_glyph_index = 0U, descriptor_index = 0U;
    std::uint32_t font_index = 0U, style_index = 0U;
    bool operator==(const hinted_paragraph_draw_owner&) const = default;
};

struct hinted_paragraph_run_outline_slice final {
    std::uint32_t source_start = 0U, source_count = 0U;
    std::uint32_t run_start = 0U, run_count = 0U;
    std::uint32_t outline_start = 0U, outline_count = 0U;
    std::uint32_t segment_start = 0U, segment_count = 0U;
    bool operator==(const hinted_paragraph_run_outline_slice&) const = default;
};

struct hinted_paragraph_glyph_frame_result;

class hinted_paragraph_glyph_frame final {
public:
    hinted_paragraph_glyph_frame(const hinted_paragraph_glyph_frame&) = delete;
    hinted_paragraph_glyph_frame& operator=(const hinted_paragraph_glyph_frame&) = delete;
    const std::shared_ptr<const hinted_paragraph_generation>& paragraph() const noexcept { return paragraph_; }
    const hinted_paragraph_glyph_target& target() const noexcept { return target_; }
    hinted_outline_coverage coverage() const noexcept { return coverage_; }
    hinted_projection_policy projection_policy() const noexcept { return projection_policy_; }
    std::span<const progpu_native_color> style_colors() const noexcept { return style_colors_; }
    std::span<const progpu_native_glyph_outline> outlines() const noexcept { return outlines_; }
    std::span<const progpu_native_path_segment> segments() const noexcept { return segments_; }
    std::span<const progpu_native_positioned_glyph> glyphs() const noexcept { return glyphs_; }
    std::span<const hinted_paragraph_run_outline_slice> run_slices() const noexcept { return run_slices_; }
    // Global outline slots; no-ink remains hinted_no_outline. Source slices
    // include every source descriptor, but never auxiliary capture descriptors.
    std::span<const std::uint32_t> source_outline_indices() const noexcept { return source_outline_indices_; }
    std::span<const std::uint32_t> run_outline_indices() const noexcept { return run_outline_indices_; }
    std::span<const hinted_paragraph_outline_owner> outline_owners() const noexcept { return outline_owners_; }
    std::span<const hinted_paragraph_draw_owner> draw_owners() const noexcept { return draw_owners_; }
    // Borrow arrays only while retaining this frame through the original render
    // call. View/provider/device lifetime remains the original renderer contract.
    progpu_native_glyph_frame frame() const noexcept;

private:
    hinted_paragraph_glyph_frame() = default;
    std::shared_ptr<const hinted_paragraph_generation> paragraph_{};
    hinted_paragraph_glyph_target target_{};
    hinted_outline_coverage coverage_ = hinted_outline_coverage::strict;
    hinted_projection_policy projection_policy_ = hinted_projection_policy::automatic;
    std::vector<progpu_native_color> style_colors_{};
    std::vector<progpu_native_glyph_outline> outlines_{};
    std::vector<progpu_native_path_segment> segments_{};
    std::vector<progpu_native_positioned_glyph> glyphs_{};
    std::vector<hinted_paragraph_run_outline_slice> run_slices_{};
    std::vector<std::uint32_t> source_outline_indices_{}, run_outline_indices_{};
    std::vector<hinted_paragraph_outline_owner> outline_owners_{};
    std::vector<hinted_paragraph_draw_owner> draw_owners_{};
    friend hinted_paragraph_glyph_frame_result create_hinted_paragraph_glyph_frame(
        std::shared_ptr<const hinted_paragraph_generation>, hinted_paragraph_glyph_target,
        std::span<const progpu_native_color>, hinted_projection_policy, hinted_outline_coverage) noexcept;
};

struct hinted_paragraph_glyph_frame_result final {
    progpu_native_status status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    hinted_glyph_frame_error error{hinted_glyph_frame_error_code::invalid_argument, hinted_outline_error::none};
    std::shared_ptr<const hinted_paragraph_glyph_frame> generation{};
};

// One conversion per original retained run, no reshape or font execution. Every
// positioned source occurrence retains its order/owner/solid style paint; only
// explicit no-ink draws are omitted. Units for each positioned run, including
// no-ink items, must equal
// literal float 1/DPI and multiply by DPI to literal 1.0f. Physical Y-up outlines
// include captured phase once; fitted Y-down positions receive origin once.
// BY-VALUE ownership/status avoids caller publication aliases. No arbitrary
// transform, dropped outline policy, source Display/default or public ABI change.
hinted_paragraph_glyph_frame_result create_hinted_paragraph_glyph_frame(
    std::shared_ptr<const hinted_paragraph_generation> paragraph,
    hinted_paragraph_glyph_target target,
    std::span<const progpu_native_color> style_colors,
    hinted_projection_policy policy = hinted_projection_policy::automatic,
    hinted_outline_coverage coverage = hinted_outline_coverage::strict) noexcept;

} // namespace progpu::native::text
