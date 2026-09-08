#include "progpu_native_document_flow.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <limits>
#include <vector>

namespace {
using block = progpu_native_document_block;
using box = progpu_native_document_box;
using line = progpu_native_document_line;
using position = progpu_native_document_line_position;
using result = progpu_native_document_flow_result;
constexpr auto success = PROGPU_NATIVE_STATUS_SUCCESS;
constexpr auto invalid = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
void require(bool condition) { if (!condition) std::abort(); }
block leaf(std::uint32_t parent, std::uint32_t end, std::uint32_t start,
    double top, double bottom, std::uint32_t count = 1U) {
    block b{};
    b.parent_index = parent; b.subtree_end = end; b.line_start = start; b.line_count = count;
    b.margin_top = top; b.margin_bottom = bottom;
    return b;
}
result fresh() { result r{}; r.struct_size = sizeof(r); return r; }
}

int main() {
    static_assert(sizeof(block) == 80U && offsetof(block, margin_left) == 16U);
    static_assert(offsetof(block, inset_bottom) == 72U);
    static_assert(sizeof(box) == 32U && sizeof(line) == 16U && sizeof(position) == 16U);
    static_assert(sizeof(result) == 32U && offsetof(result, width) == 16U);
    auto r = fresh();
    require(progpu_native_document_arrange(nullptr, 0U, 200.0, nullptr, 0U, nullptr, 0U, nullptr, 0U, &r) == success);
    require(r.width == 200.0 && r.height == 0.0 && r.block_count == 0U && r.line_count == 0U);

    // Heading, paragraph and list container: source metrics, not reshaped text.
    std::array blocks{leaf(UINT32_MAX, 1U, 0U, 10.0, 20.0),
        leaf(UINT32_MAX, 2U, 1U, 8.0, 12.0, 2U),
        leaf(UINT32_MAX, 5U, 3U, 6.0, 4.0, 0U),
        leaf(2U, 4U, 3U, 0.0, 0.0), leaf(2U, 5U, 4U, 0.0, 0.0)};
    blocks[2].inset_left = 24.0;
    std::array<line, 5> lines{{{100.0, 30.0}, {140.0, 14.0}, {80.0, 14.0}, {180.0, 18.0}, {100.0, 18.0}}};
    std::array<box, 5> boxes{};
    std::array<position, 5> positions{};
    require(progpu_native_document_resolve_widths(blocks.data(), 5U, 200.0, boxes.data(), 5U) == success);
    require(boxes[3].x == 24.0 && boxes[3].width == 176.0 && boxes[3].height == 0.0);
    require(progpu_native_document_arrange(blocks.data(), 5U, 200.0, lines.data(), 5U,
        boxes.data(), 5U, positions.data(), 5U, &r) == success);
    require(positions[0].y == 10.0 && positions[1].y == 60.0 && positions[2].y == 74.0);
    require(positions[3].y == 100.0 && positions[4].y == 118.0 && r.height == 140.0);
    require(r.width == 204.0 && boxes[2].height == 36.0 && boxes[3].width == 176.0);

    // Transparent ancestor edges collapse; border/padding stops that collapse.
    std::array nested{leaf(UINT32_MAX, 2U, 0U, 5.0, 7.0, 0U), leaf(0U, 2U, 0U, 11.0, 13.0)};
    std::array<line, 1> one_line{{{250.0, 20.0}}};
    std::array<box, 2> nested_boxes{};
    std::array<position, 1> one_position{};
    nested[0].inset_left = 4.0; nested[0].inset_right = 6.0;
    nested[1].margin_left = 2.0; nested[1].margin_right = 3.0;
    require(progpu_native_document_arrange(nested.data(), 2U, 200.0, one_line.data(), 1U,
        nested_boxes.data(), 2U, one_position.data(), 1U, &r) == success);
    require(one_position[0].x == 6.0 && one_position[0].y == 11.0 && r.height == 44.0 && r.width == 265.0);
    nested[0].inset_top = 2.0; nested[0].inset_bottom = 3.0;
    require(progpu_native_document_arrange(nested.data(), 2U, 200.0, one_line.data(), 1U,
        nested_boxes.data(), 2U, one_position.data(), 1U, &r) == success);
    require(one_position[0].y == 18.0 && r.height == 61.0 && nested_boxes[0].height == 44.0);

    // Empty blocks collapse through, including their descendants.
    std::array empty{leaf(UINT32_MAX, 1U, 0U, 3.0, 4.0),
        leaf(UINT32_MAX, 3U, 1U, 7.0, 8.0, 0U), leaf(1U, 3U, 1U, 17.0, 19.0, 0U),
        leaf(UINT32_MAX, 4U, 1U, 6.0, 5.0)};
    std::array<line, 2> short_lines{{{5.0, 10.0}, {6.0, 12.0}}};
    std::array<box, 4> empty_boxes{};
    std::array<position, 2> short_positions{};
    require(progpu_native_document_arrange(empty.data(), 4U, 100.0, short_lines.data(), 2U,
        empty_boxes.data(), 4U, short_positions.data(), 2U, &r) == success);
    require(short_positions[0].y == 3.0 && short_positions[1].y == 32.0 && r.height == 49.0);

    // Width exhaustion is explicit, and formatting may still report overflow.
    auto exhausted = leaf(UINT32_MAX, 1U, 0U, 0.0, 0.0);
    exhausted.inset_left = 20.0; exhausted.inset_right = 30.0;
    box exhausted_box{};
    require(progpu_native_document_resolve_widths(&exhausted, 1U, 5.0, &exhausted_box, 1U) == success);
    require(exhausted_box.width == 0.0 && exhausted_box.x == 20.0);

    // All failure paths publish nothing, including late overflow and bad topology.
    box sentinel{91.0, 92.0, 93.0, 94.0};
    position marker{95.0, 96.0};
    auto bad = leaf(UINT32_MAX, 1U, 0U, 0.0, 0.0);
    auto metric = line{2.0, 3.0};
    r = fresh(); r.height = 97.0;
    const auto rejected = [&] {
        require(progpu_native_document_arrange(&bad, 1U, 10.0, &metric, 1U,
            &sentinel, 1U, &marker, 1U, &r) == invalid);
        require(sentinel.x == 91.0 && sentinel.height == 94.0 && marker.y == 96.0 && r.height == 97.0);
    };
    bad.parent_index = 0U; rejected(); bad.parent_index = UINT32_MAX;
    bad.subtree_end = 2U; rejected(); bad.subtree_end = 1U;
    bad.line_start = 1U; rejected(); bad.line_start = 0U;
    bad.line_count = 0U; rejected(); bad.line_count = 1U;
    metric.height = 0.0; rejected(); metric.height = 3.0;
    metric.width = -1.0; rejected(); metric.width = 2.0;
    bad.inset_top = std::numeric_limits<double>::max();
    bad.inset_bottom = std::numeric_limits<double>::max(); rejected();
    bad.inset_top = bad.inset_bottom = 0.0;

    // Scalar oracle for every lane of the SIMD metric predicate. This is test-only
    // reference work; the product keeps ordered prefixes plus intrinsic pairs.
    constexpr std::array values{0.0, -0.0, 1.0, -1.0,
        std::numeric_limits<double>::denorm_min(), std::numeric_limits<double>::quiet_NaN(),
        std::numeric_limits<double>::infinity(), -std::numeric_limits<double>::infinity()};
    constexpr std::array members{&block::margin_left, &block::margin_top, &block::margin_right,
        &block::margin_bottom, &block::inset_left, &block::inset_top, &block::inset_right, &block::inset_bottom};
    for (const auto member : members) for (const auto value : values) {
        bad.*member = value;
        const auto status = progpu_native_document_resolve_widths(&bad, 1U, 100.0, &exhausted_box, 1U);
        require((status == success) == (std::isfinite(value) && value >= 0.0));
        bad.*member = 0.0;
    }
    require(progpu_native_document_resolve_widths(&bad, 1U, 10.0, reinterpret_cast<box*>(&bad), 1U) == invalid);
    require(progpu_native_document_arrange(&bad, 1U, 10.0, &metric, 1U,
        &sentinel, 1U, &marker, 0U, &r) == invalid);
    require(progpu_native_document_arrange(&bad, 1U, 10.0, &metric, 1U,
        &sentinel, 1U, reinterpret_cast<position*>(&sentinel), 1U, &r) == invalid);
    require(progpu_native_document_resolve_widths(nullptr, 1U, 10.0, &sentinel, 1U) == invalid);

    std::vector<block> deep(128U);
    std::vector<box> deep_boxes(128U);
    for (std::uint32_t i = 0U; i < 128U; ++i) deep[i] = leaf(i == 0U ? UINT32_MAX : i - 1U, 128U, 0U, 0.0, 0.0, 0U);
    require(progpu_native_document_resolve_widths(deep.data(), 128U, 100.0, deep_boxes.data(), 128U) == success);
    for (auto& b : deep) b.subtree_end = 129U;
    deep.push_back(leaf(127U, 129U, 0U, 0.0, 0.0, 0U)); deep_boxes.resize(129U);
    require(progpu_native_document_resolve_widths(deep.data(), 129U, 100.0, deep_boxes.data(), 129U) == invalid);

    // Independent flat-layout scalar oracle, including fractional line advances.
    for (std::uint32_t count = 1U; count <= 64U; ++count) {
        std::vector<block> flat(count); std::vector<line> metrics(count);
        std::vector<box> output(count); std::vector<position> placed(count);
        double expected = 0.0, pending = 0.0;
        for (std::uint32_t i = 0U; i < count; ++i) {
            flat[i] = leaf(UINT32_MAX, i + 1U, i, static_cast<double>((i * 7U) % 13U), static_cast<double>((i * 3U) % 17U));
            metrics[i] = {20.0, 10.0 + static_cast<double>(i % 3U) * 0.25};
        }
        require(progpu_native_document_arrange(flat.data(), count, 100.0, metrics.data(), count,
            output.data(), count, placed.data(), count, &r) == success);
        for (std::uint32_t i = 0U; i < count; ++i) {
            expected += std::max(pending, flat[i].margin_top);
            require(placed[i].y == expected && output[i].height == metrics[i].height);
            expected += metrics[i].height; pending = flat[i].margin_bottom;
        }
        require(r.height == expected + pending);
    }
}
