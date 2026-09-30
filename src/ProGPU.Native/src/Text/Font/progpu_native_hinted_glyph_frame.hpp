#pragma once

#include "progpu_native_hinted_outline.hpp"
#include "progpu_native_hinted_text_layout.hpp"

namespace progpu::native::text {

struct hinted_glyph_target final {
    std::uint32_t width = 0U; // Actual target physical pixels.
    std::uint32_t height = 0U;
    float dpi_scale = 0.0F;
    std::uintptr_t target_view = 0U; // Borrowed actual same-device view.
    progpu_native_point logical_origin{}; // Y-down, added once to fitted positions.
    progpu_native_color color{};
    progpu_native_color clear_color{};
};

enum class hinted_glyph_frame_error_code : std::uint32_t {
    none, invalid_argument, invalid_layout, unsupported_mapping,
    outline_conversion_failed, insufficient_capacity, resource_exhausted,
};

struct hinted_glyph_frame_error final {
    hinted_glyph_frame_error_code code = hinted_glyph_frame_error_code::none;
    hinted_outline_error outline = hinted_outline_error::none;
    bool operator==(const hinted_glyph_frame_error&) const = default;
};

class hinted_glyph_frame final {
public:
    hinted_glyph_frame(const hinted_glyph_frame&) = delete;
    hinted_glyph_frame& operator=(const hinted_glyph_frame&) = delete;
    const std::shared_ptr<const hinted_text_layout>& layout() const noexcept { return layout_; }
    const hinted_glyph_target& target() const noexcept { return target_; }
    hinted_outline_coverage coverage() const noexcept { return coverage_; }
    std::span<const progpu_native_glyph_outline> outlines() const noexcept { return outlines_; }
    std::span<const progpu_native_path_segment> segments() const noexcept { return segments_; }
    std::span<const progpu_native_positioned_glyph> glyphs() const noexcept { return glyphs_; }
    std::span<const std::uint32_t> source_outline_indices() const noexcept { return source_outline_indices_; }
    std::span<const std::uint32_t> run_outline_indices() const noexcept { return run_outline_indices_; }
    std::span<const std::uint32_t> draw_layout_indices() const noexcept { return draw_layout_indices_; }

    // Borrowed views of owned arrays; retain this object throughout the render
    // call. Target/view/provider lifetime remains the original renderer contract.
    // Literal scale=1, phase=0, identity bases, flags/revision=0 and no draw state:
    // no source style/transform/factory/Display or grayscale/B/W parity admission.
    progpu_native_glyph_frame frame() const noexcept;

private:
    hinted_glyph_frame() = default;
    std::shared_ptr<const hinted_text_layout> layout_{};
    hinted_glyph_target target_{};
    hinted_outline_coverage coverage_ = hinted_outline_coverage::strict;
    std::vector<progpu_native_glyph_outline> outlines_{};
    std::vector<progpu_native_path_segment> segments_{};
    std::vector<progpu_native_positioned_glyph> glyphs_{};
    std::vector<std::uint32_t> source_outline_indices_{};
    std::vector<std::uint32_t> run_outline_indices_{};
    std::vector<std::uint32_t> draw_layout_indices_{};
    friend bool try_create_hinted_glyph_frame(std::shared_ptr<const hinted_text_layout>,
        std::shared_ptr<const hinted_shaped_run>, const hinted_glyph_target&,
        std::shared_ptr<const hinted_glyph_frame>&, hinted_glyph_frame_error*,
        hinted_projection_policy, hinted_outline_coverage) noexcept;
    friend bool hinted_glyph_frame_output_aliases(const hinted_glyph_frame&,
        std::span<std::byte>) noexcept;
};

// Exact layout/run pointer identity, never an ID/byte-based generation guess.
// Stored logical-units-per-pixel must equal the actual float 1/DPI conversion
// and its product with DPI must be literally 1.0f. This matches the explicit
// float shader frame; it is not independent pixel/UI/source qualification.
// Unsupported projection fails, without epsilon, snapping or late ink scaling.
// Complete original source slots convert once; fitted draws retain visual order
// and exact original-run/descriptor maps, skipping ONLY explicit no-ink slots.
// Factory publication is atomic. Error/result must be disjoint from complete
// input/previous-output owners and capacities. Unsafe aliases leave both intact.
// Coverage stays strict by default; an explicit vector contract is retained in
// the frame and changes only the admitted metadata, never raw captures/raster work.
bool try_create_hinted_glyph_frame(std::shared_ptr<const hinted_text_layout> layout,
    std::shared_ptr<const hinted_shaped_run> run, const hinted_glyph_target& target,
    std::shared_ptr<const hinted_glyph_frame>& result,
    hinted_glyph_frame_error* error = nullptr,
    hinted_projection_policy policy = hinted_projection_policy::automatic,
    hinted_outline_coverage coverage = hinted_outline_coverage::strict) noexcept;

bool hinted_glyph_frame_output_aliases(const hinted_glyph_frame& frame,
    std::span<std::byte> output) noexcept;

} // namespace progpu::native::text
