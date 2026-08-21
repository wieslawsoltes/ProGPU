#pragma once

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>

namespace progpu::native::semantic {

// Content identities deliberately exclude scene generation and resource
// payload offsets. Stable resource id/generation pairs are the retained
// ownership boundary. Each compiled draw-family identity includes only that
// family's draw records/payloads plus the effective state and active layers at
// its draws. Closed and trailing scopes do not affect unrelated retained
// pages. Full-scene replay ordering remains owned by the independent immutable
// stream hash.
struct semantic_content_hashes final {
    std::uint64_t brush = 0U;
    std::uint64_t text_style = 0U;
    std::uint64_t analytic = 0U;
    std::uint64_t path = 0U;
    std::uint64_t glyph = 0U;
    std::uint64_t image = 0U;
    std::uint64_t three_d = 0U;
    std::uint64_t hit_test = 0U;
};

semantic_content_hashes compute_content_hashes(
    const std::byte* bytes,
    const progpu_native_scene_header& header) noexcept;

} // namespace progpu::native::semantic
