#include "progpu_native_semantic_group_pages.hpp"

#include "progpu_native_scene.hpp"
#include "progpu_native_semantic_state.hpp"

#include <algorithm>
#include <array>
#include <cstring>
#include <unordered_map>

namespace progpu::native::semantic {
namespace {

// The side of a cell of the index of the rectangles on a page, in pixels.
constexpr std::uint32_t page_cell = 128U;

struct group_node final {
    scissor rect{};
    std::int32_t parent = -1;
    std::int32_t root = -1;
    std::uint32_t depth = 0U;
    bool pure = false;
};

// The rectangles on one page, found by the cells they touch.
class page_rectangles final {
public:
    // Adds the rectangle unless it overlaps one that is there.
    bool try_add(const scissor& rect) {
        const std::uint32_t right = (rect.x + rect.width - 1U) / page_cell;
        const std::uint32_t bottom = (rect.y + rect.height - 1U) / page_cell;
        for (std::uint32_t y = rect.y / page_cell; y <= bottom; ++y) {
            for (std::uint32_t x = rect.x / page_cell; x <= right; ++x) {
                const auto found = cells_.find(key(x, y));
                if (found == cells_.end()) {
                    continue;
                }
                for (const std::uint32_t index : found->second) {
                    if (overlap(rect, rects_[index])) {
                        return false;
                    }
                }
            }
        }
        const auto index = static_cast<std::uint32_t>(rects_.size());
        rects_.push_back(rect);
        for (std::uint32_t y = rect.y / page_cell; y <= bottom; ++y) {
            for (std::uint32_t x = rect.x / page_cell; x <= right; ++x) {
                cells_[key(x, y)].push_back(index);
            }
        }
        return true;
    }

private:
    static std::uint64_t key(std::uint32_t x, std::uint32_t y) noexcept {
        return (static_cast<std::uint64_t>(y) << 32U) | x;
    }

    static bool overlap(const scissor& a, const scissor& b) noexcept {
        return a.x < b.x + b.width && b.x < a.x + a.width &&
            a.y < b.y + b.height && b.y < a.y + a.height;
    }

    std::vector<scissor> rects_;
    std::unordered_map<std::uint64_t, std::vector<std::uint32_t>> cells_;
};

// The layer flags a group on a page may have.
constexpr std::uint32_t plain_layer_flags =
    PROGPU_NATIVE_SCENE_LAYER_BOUNDS | PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;

bool plain_layer(const progpu_native_scene_layer& layer) noexcept {
    return (layer.flags & ~plain_layer_flags) == 0U &&
        layer.blend_mode == PROGPU_NATIVE_BLEND_SRC_OVER &&
        layer.mask_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX &&
        layer.effect_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX;
}

// What may be drawn inside a group on a page: draws of the shared families, which depend on
// their target only through its extent.
bool plain_command(std::uint32_t kind) noexcept {
    switch (kind) {
    case PROGPU_NATIVE_SCENE_COMMAND_SAVE:
    case PROGPU_NATIVE_SCENE_COMMAND_RESTORE:
    case PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC:
    case PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH:
    case PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN:
    case PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE:
    case PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY:
    case PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH:
        return true;
    default:
        return false;
    }
}

} // namespace

bool group_pages_fit(std::uint32_t page_count, std::uint32_t frame_width, std::uint32_t frame_height) noexcept {
    const std::uint64_t page_bytes = static_cast<std::uint64_t>(frame_width) * frame_height * 4U;
    // Half of what groups may take: the groups with their own passes keep the other half.
    return page_bytes * page_count <= PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES / 2U;
}

void build_group_page_plan(
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    std::uint32_t frame_width,
    std::uint32_t frame_height,
    const progpu_native_scene_presentation& presentation,
    group_page_plan& plan) {
    plan.reset();
    semantic_state_cursor state_cursor(bytes, header, presentation.dpi_scale_x, presentation.dpi_scale_y);
    semantic_layer_target_cursor target_cursor(bytes, frame_width, frame_height, presentation);
    std::vector<group_node> nodes;
    std::array<bool, PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> scope_materialized{};
    std::array<std::int32_t, PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS> open{};
    std::uint32_t scope_depth = 0U;
    std::uint32_t open_depth = 0U;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        progpu_native_scene_command command{};
        std::memcpy(
            &command,
            bytes + header.command_offset + static_cast<std::size_t>(index) * header.command_stride,
            sizeof(command));
        const auto state = state_cursor.advance(command);
        const auto extent = target_cursor.advance(command);
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) {
            auto layer = semantic_default_layer();
            if (command.payload_size != 0U) {
                std::memcpy(&layer, bytes + command.payload_offset, sizeof(layer));
            }
            const bool materialized = scene::layer_requires_materialization(layer);
            scope_materialized[scope_depth++] = materialized;
            if (materialized) {
                group_node node{};
                node.rect = extent;
                node.parent = open_depth == 0U ? -1 : open[open_depth - 1U];
                node.pure = plain_layer(layer) && extent.drawable && extent.width != 0U && extent.height != 0U &&
                    (state.flags & PROGPU_NATIVE_SCENE_STATE_MASK) == 0U;
                open[open_depth++] = static_cast<std::int32_t>(nodes.size());
                nodes.push_back(node);
            }
            continue;
        }
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
            if (scope_materialized[--scope_depth]) {
                const auto& closed = nodes[static_cast<std::size_t>(open[--open_depth])];
                // A group that is not plain keeps the groups around it off the pages.
                if (!closed.pure && closed.parent >= 0) {
                    nodes[static_cast<std::size_t>(closed.parent)].pure = false;
                }
            }
            continue;
        }
        if (open_depth != 0U &&
            (!plain_command(command.kind) || (state.flags & PROGPU_NATIVE_SCENE_STATE_MASK) != 0U)) {
            nodes[static_cast<std::size_t>(open[open_depth - 1U])].pure = false;
        }
    }
    if (nodes.empty()) {
        return;
    }

    // A parent comes before its children. A group that does not fit its page takes the whole
    // tree of plain groups it belongs to off the pages.
    std::array<page_rectangles, group_page_count> page_rects;
    std::vector<bool> failed(nodes.size(), false);
    for (std::size_t index = 0U; index < nodes.size(); ++index) {
        auto& node = nodes[index];
        if (!node.pure) {
            continue;
        }
        const bool nested = node.parent >= 0 && nodes[static_cast<std::size_t>(node.parent)].pure;
        node.root = nested ? nodes[static_cast<std::size_t>(node.parent)].root : static_cast<std::int32_t>(index);
        node.depth = nested ? nodes[static_cast<std::size_t>(node.parent)].depth + 1U : 0U;
        if (node.depth >= group_page_count || !page_rects[node.depth].try_add(node.rect)) {
            failed[static_cast<std::size_t>(node.root)] = true;
        }
    }
    plan.pages.assign(nodes.size(), 0U);
    std::uint32_t page_count = 0U;
    for (std::size_t index = 0U; index < nodes.size(); ++index) {
        const auto& node = nodes[index];
        if (node.pure && !failed[static_cast<std::size_t>(node.root)]) {
            plan.pages[index] = static_cast<std::uint8_t>(node.depth + 1U);
            page_count = std::max(page_count, node.depth + 1U);
        }
    }
    if (page_count == 0U || !group_pages_fit(page_count, frame_width, frame_height)) {
        plan.reset();
        return;
    }
    plan.page_count = page_count;
    std::uint64_t hash = 14695981039346656037ULL;
    for (const std::uint8_t page : plan.pages) {
        hash = (hash ^ page) * 1099511628211ULL;
    }
    plan.hash = hash == 0U ? 1U : hash;
}

} // namespace progpu::native::semantic
