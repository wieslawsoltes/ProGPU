#pragma once

#include "progpu_native_hinted_shaper.hpp"

#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::text {

enum class hinted_outline_error : std::uint32_t {
    none, invalid_argument, invalid_run, invalid_topology,
    unsupported_flags, unsupported_coordinates, unsupported_policy,
    insufficient_capacity,
};

inline constexpr std::uint32_t hinted_no_outline = UINT32_MAX;

// Strict preserves the original scan/raster-policy rejection. The explicit
// vector contract consumes original nonzero geometry with ProGPU coverage, not
// FreeType grayscale/B/W raster parity. It retains raw metadata while admitting
// only IGNORE_DROPOUTS/HIGH_PRECISION and contour-start SCANTYPE mode zero.
enum class hinted_outline_coverage : std::uint32_t { strict, nonzero_vector };

struct hinted_outline_requirements final {
    std::size_t source_slots = 0U;
    std::size_t positioned_slots = 0U;
    std::size_t outlines = 0U;
    std::size_t segments = 0U;
    std::size_t scratch_points = 0U;
    bool operator==(const hinted_outline_requirements&) const = default;
};

struct hinted_outline_scratch final {
    std::span<sfnt_outline_point> topology{};
    std::span<progpu_native_point> physical_points{};
};

// One slice per original source descriptor, including repeated IDs; auxiliaries
// are not outlines. Positioned repetitions/reversal use descriptor_indices.
// No allocation, font interpreter, shaping, renderer submission or source
// Display admission. Borrow the exact owned run throughout either call.
hinted_outline_error get_hinted_outline_requirements(const hinted_shaped_run& run,
    hinted_outline_requirements& requirements,
    hinted_projection_policy policy = hinted_projection_policy::automatic,
    hinted_outline_coverage coverage = hinted_outline_coverage::strict) noexcept;

// Shared native renderer records use physical Y-up points, raster_scale=1 and
// subpixel_x=0: the captured integer phase is already present, exactly once.
// No-ink source/positioned slots map to hinted_no_outline. All output and scratch
// spans, including caller tails, must be mutually disjoint and disjoint from the
// entire retained run/batch, including spare owned vector capacity. Complete
// preflight precedes any publication; output
// and scratch tails and every output on failure remain untouched. The original
// batch retains exact raw tags/outline flags. Strict rejects even-odd, scan/dropout
// flags and SCANTYPE overrides. Explicit nonzero_vector admits only the observed
// raster-only bits documented above; other policies and misplaced/nonzero-mode
// markers still reject. Neither contract admits grayscale/B/W raster parity.
hinted_outline_error write_hinted_run_outlines(const hinted_shaped_run& run,
    hinted_outline_scratch scratch,
    std::span<progpu_native_glyph_outline> outlines,
    std::span<progpu_native_path_segment> segments,
    std::span<std::uint32_t> source_outline_indices,
    std::span<std::uint32_t> positioned_outline_indices,
    hinted_outline_requirements& written,
    hinted_projection_policy policy = hinted_projection_policy::automatic,
    hinted_outline_coverage coverage = hinted_outline_coverage::strict) noexcept;

} // namespace progpu::native::text
