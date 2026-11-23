#pragma once

// Groups rendered before the root pass.
//
// A translucent group is normally two passes in the middle of its parent: its content, then its
// composite, with the parent's target loaded and stored around them. The plan below puts the
// groups that read nothing of their surroundings on pages, one texture of the size of the frame
// for each nesting depth. A page is drawn once, deepest first and before the root pass, each group
// at its own place on the frame, so that a frame is 1 + (nesting depth) passes for those groups
// however many there are, and their composites are draws of the parent's pass.
//
// Everything else keeps its own passes: groups with a backdrop, an effect, a mask, a cache, a blend
// other than source-over or masked content, and groups that contain one of those. Two groups of
// one depth that overlap cannot share a page: the outermost group above them keeps its own passes.

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>
#include <vector>

namespace progpu::native::semantic {

inline constexpr std::uint32_t group_page_count = PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS;

struct group_page_plan final {
    // By the ordinal of a materialized layer in command order: its page + 1, or zero.
    std::vector<std::uint8_t> pages;
    std::uint32_t page_count = 0U;
    // Zero for a plan without pages; what the compiled family pages depend on otherwise.
    std::uint64_t hash = 0U;

    bool active() const noexcept {
        return page_count != 0U;
    }

    std::uint32_t page_of(std::uint32_t ordinal) const noexcept {
        return ordinal < pages.size() ? pages[ordinal] : 0U;
    }

    void reset() noexcept {
        pages.clear();
        page_count = 0U;
        hash = 0U;
    }
};

// Whether the bytes of the pages of a frame stay within what groups may take.
bool group_pages_fit(std::uint32_t page_count, std::uint32_t frame_width, std::uint32_t frame_height) noexcept;

// Plans the pages of a validated scene for a frame without a mapped presentation. May throw
// std::bad_alloc; the plan is then left without pages by the caller.
void build_group_page_plan(
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    std::uint32_t frame_width,
    std::uint32_t frame_height,
    const progpu_native_scene_presentation& presentation,
    group_page_plan& plan);

} // namespace progpu::native::semantic
