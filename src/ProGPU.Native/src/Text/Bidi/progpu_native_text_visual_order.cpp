#include "progpu_native_text.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct allocation-free port of ProGPU-owned
// TextLayout.GetVisualLineCandidates and BidiParagraph.GetVisualOrderIfNeeded.
// Logical wrapping stays separate; this stage applies UAX #9 L1/L2 per line.

namespace progpu::native::text {
namespace {

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) *error = value;
}

bool valid_levels(std::span<const std::int8_t> levels) noexcept {
    return std::all_of(levels.begin(), levels.end(), [](std::int8_t level) {
        return level >= 0 && level <= 125;
    });
}

bool is_space(const shaping_glyph& glyph) noexcept {
    return glyph.code_point == 0x20U || glyph.code_point == 0x09U;
}

bool try_prepare_visual_groups(
    std::span<const shaping_glyph> logical_glyphs,
    std::span<const std::int8_t> bidi_levels,
    std::int8_t paragraph_level,
    std::span<text_visual_cluster_group> group_scratch,
    std::uint32_t& group_count,
    text_visual_order_requirements& requirements,
    font_error* error) noexcept {
    group_count = 0U;
    if (!try_get_text_visual_order_requirements(
            logical_glyphs, bidi_levels, requirements, error) ||
        (paragraph_level != 0 && paragraph_level != 1)) {
        if (paragraph_level != 0 && paragraph_level != 1) {
            set_error(error, font_error::invalid_argument);
        }
        return false;
    }
    if (group_scratch.size() < requirements.group_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    for (std::size_t start = 0U; start < logical_glyphs.size();) {
        std::size_t end = start + 1U;
        while (end < logical_glyphs.size() &&
            logical_glyphs[end].cluster == logical_glyphs[start].cluster) {
            ++end;
        }
        group_scratch[group_count++] = text_visual_cluster_group{
            static_cast<std::uint32_t>(start),
            static_cast<std::uint32_t>(end - start),
            bidi_levels[start]};
        start = end;
    }
    auto groups = group_scratch.first(group_count);

    // UAX #9 L1: reset trailing segment separators and whitespace to the
    // paragraph level before resolving the line's visual order.
    for (std::size_t index = groups.size(); index > 0U; --index) {
        auto& group = groups[index - 1U];
        const auto begin = logical_glyphs.begin() + group.glyph_start;
        const auto end = begin + group.glyph_count;
        if (!std::all_of(begin, end, is_space)) break;
        group.bidi_level = paragraph_level;
    }

    // UAX #9 L2: reverse whole cluster groups from the highest level down to
    // the lowest odd level. Glyphs within a shaper cluster stay ordered.
    std::int8_t maximum = 0;
    std::int8_t lowest_odd = std::numeric_limits<std::int8_t>::max();
    for (const auto& group : groups) {
        maximum = std::max(maximum, group.bidi_level);
        if ((group.bidi_level & 1) != 0) {
            lowest_odd = std::min(lowest_odd, group.bidi_level);
        }
    }
    if (lowest_odd != std::numeric_limits<std::int8_t>::max()) {
        for (int level = maximum; level >= lowest_odd; --level) {
            std::size_t start = 0U;
            while (start < groups.size()) {
                while (start < groups.size() &&
                    groups[start].bidi_level < level) {
                    ++start;
                }
                std::size_t end = start;
                while (end < groups.size() &&
                    groups[end].bidi_level >= level) {
                    ++end;
                }
                std::reverse(groups.begin() + start, groups.begin() + end);
                start = end;
            }
        }
    }
    return true;
}

} // namespace

bool try_get_text_visual_order_requirements(
    std::span<const shaping_glyph> logical_glyphs,
    std::span<const std::int8_t> bidi_levels,
    text_visual_order_requirements& result,
    font_error* error) noexcept {
    result = {};
    if (logical_glyphs.size() != bidi_levels.size() ||
        logical_glyphs.size() > std::numeric_limits<std::uint32_t>::max() ||
        !valid_levels(bidi_levels)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::uint32_t groups = 0U;
    for (std::size_t index = 0U; index < logical_glyphs.size();) {
        ++groups;
        const auto cluster = logical_glyphs[index].cluster;
        do {
            ++index;
        } while (index < logical_glyphs.size() &&
            logical_glyphs[index].cluster == cluster);
    }
    result = text_visual_order_requirements{
        static_cast<std::uint32_t>(logical_glyphs.size()), groups};
    set_error(error, font_error::none);
    return true;
}

bool try_reorder_text_line_visual(
    std::span<const shaping_glyph> logical_glyphs,
    std::span<const std::int8_t> bidi_levels,
    std::int8_t paragraph_level,
    std::span<text_visual_cluster_group> group_scratch,
    std::span<shaping_glyph> visual_glyphs,
    std::uint32_t& written,
    font_error* error) noexcept {
    written = 0U;
    text_visual_order_requirements requirements{};
    std::uint32_t group_count = 0U;
    if (!try_prepare_visual_groups(
            logical_glyphs,
            bidi_levels,
            paragraph_level,
            group_scratch,
            group_count,
            requirements,
            error)) {
        return false;
    }
    if (visual_glyphs.size() < requirements.glyph_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    auto groups = group_scratch.first(group_count);
    for (const auto& group : groups) {
        const auto begin = logical_glyphs.begin() + group.glyph_start;
        const auto end = begin + group.glyph_count;
        for (auto source = begin; source != end; ++source) {
            visual_glyphs[written++] = *source;
        }
    }
    set_error(error, font_error::none);
    return true;
}

bool try_get_text_line_visual_indices(
    std::span<const shaping_glyph> logical_glyphs,
    std::span<const std::int8_t> bidi_levels,
    std::int8_t paragraph_level,
    std::span<text_visual_cluster_group> group_scratch,
    std::span<std::uint32_t> visual_indices,
    std::uint32_t& written,
    font_error* error) noexcept {
    written = 0U;
    text_visual_order_requirements requirements{};
    std::uint32_t group_count = 0U;
    if (!try_prepare_visual_groups(
            logical_glyphs,
            bidi_levels,
            paragraph_level,
            group_scratch,
            group_count,
            requirements,
            error)) {
        return false;
    }
    if (visual_indices.size() < requirements.glyph_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    for (const auto& group : group_scratch.first(group_count)) {
        for (std::uint32_t index = 0U; index < group.glyph_count; ++index) {
            visual_indices[written++] = group.glyph_start + index;
        }
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
