#include "progpu_native_document_flow.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <limits>
#include <new>
#include <span>
#include <vector>
#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#endif

// Original ProGPU block-flow placement over already formatted paragraph lines.
// Tree traversal and vertical prefixes are ordered dependencies, not GPU work.
// Independent metric validation uses the desktop double-precision SIMD baseline.
// O(B + L + C + R + E) time/storage per changed layout, including shared column
// tracks, rows and cells. Replay retains the resulting boxes.
namespace {
constexpr std::uint32_t root = UINT32_MAX;
constexpr std::uint32_t budget = 1U << 20U;
using block = progpu_native_document_block;
using box = progpu_native_document_box;
using line = progpu_native_document_line;
using measured_object = progpu_native_document_object;
using row = progpu_native_document_row;
using cell = progpu_native_document_cell;
using position = progpu_native_document_line_position;
struct state final { double height{}, content_height{}, before{}, after{}; bool through{}; };
struct region final { std::uintptr_t start{}; std::size_t bytes{}; };

bool finite_nonnegative_pair(double a, double b) noexcept {
#if defined(__aarch64__) || defined(_M_ARM64)
    const double lanes[2]{a, b};
    const auto v = vld1q_f64(lanes);
    const auto valid = vandq_u64(vcgeq_f64(v, vdupq_n_f64(0.0)),
        vcleq_f64(v, vdupq_n_f64(std::numeric_limits<double>::max())));
    return vgetq_lane_u64(valid, 0) != 0U && vgetq_lane_u64(valid, 1) != 0U;
#elif defined(__SSE2__) || defined(_M_X64)
    const auto v = _mm_set_pd(b, a);
    return _mm_movemask_pd(_mm_and_pd(_mm_cmpge_pd(v, _mm_setzero_pd()),
        _mm_cmple_pd(v, _mm_set1_pd(std::numeric_limits<double>::max())))) == 3;
#else
    // Fixed two-value reference on targets without the desktop SIMD baseline.
    return std::isfinite(a) && a >= 0.0 && std::isfinite(b) && b >= 0.0;
#endif
}
template<class T> bool valid_buffer(const T* data, std::uint32_t count) noexcept {
    return count <= budget && (count == 0U || (data != nullptr &&
        reinterpret_cast<std::uintptr_t>(data) % alignof(T) == 0U));
}
template<class T> region bytes(const T* data, std::uint32_t count) noexcept {
    return {reinterpret_cast<std::uintptr_t>(data), sizeof(T) * static_cast<std::size_t>(count)};
}
bool disjoint(std::span<const region> regions) noexcept {
    for (std::size_t i = 0; i < regions.size(); ++i) {
        const auto a = regions[i];
        if (a.bytes == 0U) continue;
        if (a.start > UINTPTR_MAX - a.bytes) return false;
        for (std::size_t j = 0; j < i; ++j) {
            const auto b = regions[j];
            if (b.bytes != 0U && a.start < b.start + b.bytes && b.start < a.start + a.bytes) return false;
        }
    }
    return true;
}

// Column prefixes reset for each distinct shared slice, avoiding cancellation
// between unrelated tables. Each track is visited once, not once per row.
struct row_layout final {
    std::span<const row> rows;
    std::span<const cell> cells;
    std::span<const double> columns;
    std::vector<std::uint32_t> row_indices, cell_indices;
    std::vector<double> column_x;

    const row* row_at(std::size_t index) const noexcept {
        return row_indices.empty() || row_indices[index] == root ? nullptr : &rows[row_indices[index]];
    }
    const cell* cell_at(std::size_t index) const noexcept {
        return cell_indices.empty() || cell_indices[index] == root ? nullptr : &cells[cell_indices[index]];
    }
    double track_width(std::uint32_t start, std::uint32_t count) const noexcept {
        const auto last = start + count - 1U;
        return column_x[last] + columns[last] - column_x[start];
    }
    bool initialize(std::span<const block> blocks) {
        if (rows.empty()) return cells.empty() && columns.empty();
        row_indices.assign(blocks.size(), root); cell_indices.assign(blocks.size(), root);
        column_x.resize(columns.size());
        std::vector<std::uint32_t> owners(columns.size(), root), ends(columns.size(), root), next(rows.size(), 0U);
        for (std::size_t i = 0; i < columns.size(); i += 2U)
            if (!finite_nonnegative_pair(columns[i], i + 1U < columns.size() ? columns[i + 1U] : 0.0)) return false;
        for (std::uint32_t i = 0; i < rows.size(); ++i) {
            const auto& r = rows[i];
            if (r.block_index >= blocks.size() || r.reserved != 0U || r.column_count == 0U ||
                r.column_start >= columns.size() || r.column_count > columns.size() - r.column_start ||
                (i != 0U && rows[i - 1U].block_index >= r.block_index) ||
                blocks[r.block_index].line_count != 0U || !finite_nonnegative_pair(r.cell_spacing, 0.0)) return false;
            row_indices[r.block_index] = i;
            const auto end = r.column_start + r.column_count;
            if (ends[r.column_start] != root) {
                if (ends[r.column_start] != end) return false;
            } else {
                ends[r.column_start] = end;
                double x = 0.0;
                for (auto column = r.column_start; column < end; ++column) {
                    if (owners[column] != root) return false;
                    owners[column] = r.column_start; column_x[column] = x; x += columns[column];
                    if (!std::isfinite(x)) return false;
                }
            }
            if (!std::isfinite(track_width(r.column_start, r.column_count) + r.cell_spacing * r.column_count)) return false;
        }
        for (std::uint32_t i = 0; i < cells.size(); ++i) {
            const auto& c = cells[i];
            if (c.block_index >= blocks.size() || c.row_index >= rows.size() ||
                (i != 0U && cells[i - 1U].block_index >= c.block_index) || row_at(c.block_index) != nullptr) return false;
            const auto& r = rows[c.row_index];
            if (blocks[c.block_index].parent_index != r.block_index || c.column_count == 0U ||
                c.column_start < next[c.row_index] || c.column_start >= r.column_count ||
                c.column_count > r.column_count - c.column_start) return false;
            next[c.row_index] = c.column_start + c.column_count;
            cell_indices[c.block_index] = i;
        }
        for (std::size_t i = 0; i < blocks.size(); ++i) {
            const auto parent = blocks[i].parent_index;
            if (parent != root && parent < blocks.size() && row_at(parent) != nullptr && cell_at(i) == nullptr) return false;
        }
        return true;
    }
};

bool widths(std::span<const block> blocks, double width, std::span<box> output, const row_layout& layout) noexcept {
    std::array<std::uint32_t, 128> ancestors{};
    std::size_t depth = 0U;
    for (std::uint32_t i = 0; i < blocks.size(); ++i) {
        while (depth != 0U && i == blocks[ancestors[depth - 1U]].subtree_end) --depth;
        if (depth >= ancestors.size()) return false;
        const auto& b = blocks[i];
        const auto parent = depth == 0U ? root : ancestors[depth - 1U];
        if (b.parent_index != parent || b.subtree_end <= i || b.subtree_end > blocks.size() ||
            (parent != root && b.subtree_end > blocks[parent].subtree_end) ||
            !finite_nonnegative_pair(b.margin_left, b.margin_right) ||
            !finite_nonnegative_pair(b.margin_top, b.margin_bottom) ||
            !finite_nonnegative_pair(b.inset_left, b.inset_right) ||
            !finite_nonnegative_pair(b.inset_top, b.inset_bottom)) return false;
        auto parent_box = parent == root ? box{0.0, 0.0, width, 0.0} : output[parent];
        if (const auto* c = layout.cell_at(i)) {
            const auto& r = layout.rows[c->row_index];
            const auto column = r.column_start + c->column_start;
            parent_box.x += layout.column_x[column] + r.cell_spacing * (static_cast<double>(c->column_start) + 0.5);
            parent_box.width = layout.track_width(column, c->column_count) + r.cell_spacing * (c->column_count - 1U);
        }
        const double left = b.margin_left + b.inset_left;
        const double right = b.margin_right + b.inset_right;
        const double used = left + right;
        const double x = parent_box.x + left;
        if (!std::isfinite(used) || !std::isfinite(x)) return false;
        output[i] = {x, 0.0, std::max(0.0, parent_box.width - used), 0.0};
        if (const auto* r = layout.row_at(i))
            output[i].width = layout.track_width(r->column_start, r->column_count) + r->cell_spacing * r->column_count;
        if (b.subtree_end != i + 1U) {
            if (depth == ancestors.size()) return false;
            ancestors[depth++] = i;
        }
    }
    return true;
}

bool measure(std::span<const block> blocks, std::span<const line> lines, std::span<state> states,
    std::span<const measured_object> objects, const row_layout& layout) noexcept {
    std::uint32_t cursor = 0U;
    for (std::uint32_t i = 0U; i < blocks.size(); ++i) {
        const auto& b = blocks[i];
        if (b.subtree_end != i + 1U && b.line_count != 0U) return false;
        if (b.line_start != cursor || b.line_count > lines.size() - cursor) return false;
        cursor += b.line_count;
    }
    if (cursor != lines.size()) return false;
    for (const auto& l : lines)
        if (!finite_nonnegative_pair(l.width, l.height) || l.height == 0.0) return false;
    for (std::size_t i = 0U; i < objects.size(); ++i) {
        const auto& object = objects[i];
        if (object.block_index >= blocks.size() || object.reserved != 0U ||
            (i != 0U && objects[i - 1U].block_index >= object.block_index) ||
            blocks[object.block_index].subtree_end != object.block_index + 1U ||
            blocks[object.block_index].line_count != 0U ||
            layout.row_at(object.block_index) != nullptr ||
            !finite_nonnegative_pair(object.width, object.height)) return false;
    }
    std::size_t object_cursor = objects.size();
    for (std::size_t index = blocks.size(); index-- != 0U;) {
        const auto& b = blocks[index];
        auto& s = states[index];
        s.before = b.margin_top; s.after = b.margin_bottom;
        if (const auto* r = layout.row_at(index)) {
            double tallest = 0.0;
            for (std::size_t child = index + 1U; child < b.subtree_end; child = blocks[child].subtree_end) {
                const auto& c = states[child];
                tallest = std::max(tallest, c.before + c.height + c.after);
            }
            s.content_height = tallest + r->cell_spacing;
            s.height = b.inset_top + s.content_height + b.inset_bottom;
            s.through = false;
            if (!std::isfinite(s.height)) return false;
            continue;
        }
        const bool has_object = object_cursor != 0U && objects[object_cursor - 1U].block_index == index;
        double content_height = 0.0, pending = 0.0;
        if (has_object) {
            const auto& object = objects[--object_cursor];
            content_height = object.height;
        }
        bool content = b.line_count != 0U || has_object;
        const bool collapse_edges = layout.cell_at(index) == nullptr;
        for (std::uint32_t j = 0; j < b.line_count; ++j) content_height += lines[b.line_start + j].height;
        for (std::size_t child = index + 1U; child < b.subtree_end; child = blocks[child].subtree_end) {
            const auto& c = states[child];
            pending = std::max(pending, c.before);
            if (c.through) { pending = std::max(pending, c.after); continue; }
            if (!content && b.inset_top == 0.0 && collapse_edges) s.before = std::max(s.before, pending);
            else content_height += pending;
            content_height += c.height;
            content = true; pending = c.after;
        }
        if (!content && b.inset_top == 0.0 && collapse_edges) {
            s.before = std::max(s.before, pending); pending = 0.0;
        }
        if (b.inset_bottom == 0.0 && collapse_edges) s.after = std::max(s.after, pending);
        else content_height += pending;
        s.content_height = content_height;
        s.height = b.inset_top + content_height + b.inset_bottom;
        s.through = !content && b.inset_top == 0.0 && b.inset_bottom == 0.0 && collapse_edges;
        if (s.through) s.before = s.after = std::max(s.before, s.after);
        if (!std::isfinite(s.height)) return false;
    }
    return true;
}

bool place(std::span<const block> blocks, std::span<const line> lines, std::span<const state> states,
    std::span<const measured_object> objects, std::span<box> boxes, std::span<position> positions,
    double& extent_width, double& height, const row_layout& layout) {
    struct cursor final { double y{}, pending{}, trailing{}; bool content{}; };
    // Preorder traversal lets each parent retain its next-child cursor. Empty
    // transparent boxes share the collapsed gap and do not advance this cursor.
    std::vector<cursor> cursors(blocks.size());
    cursor forest{};
    std::size_t object_cursor = 0U;
    for (std::size_t i = 0; i < blocks.size(); ++i) {
        const auto& b = blocks[i]; const auto& s = states[i];
        auto& p = b.parent_index == root ? forest : cursors[b.parent_index];
        double y = p.y;
        const auto* c = layout.cell_at(i);
        if (c != nullptr) {
            y = boxes[b.parent_index].y + layout.rows[c->row_index].cell_spacing * 0.5 + s.before;
        } else {
            p.pending = std::max(p.pending, s.before);
            const bool top_collapse = b.parent_index != root && blocks[b.parent_index].inset_top == 0.0 &&
                layout.cell_at(b.parent_index) == nullptr;
            if (s.through) p.pending = std::max(p.pending, s.after);
            else {
                if (p.content || !top_collapse) y += p.pending;
                p.y = y + s.height; p.pending = s.after; p.content = true;
            }
        }
        boxes[i].y = y + b.inset_top;
        boxes[i].height = c == nullptr ? s.content_height : std::max(0.0,
            states[b.parent_index].content_height - layout.rows[c->row_index].cell_spacing -
                s.before - s.after - b.inset_top - b.inset_bottom);
        cursors[i].y = boxes[i].y;
        cursors[i].trailing = p.trailing + b.inset_right + b.margin_right;
        extent_width = std::max(extent_width, boxes[i].x + boxes[i].width + cursors[i].trailing);
        if (object_cursor < objects.size() && objects[object_cursor].block_index == i)
            extent_width = std::max(extent_width, boxes[i].x + objects[object_cursor++].width + cursors[i].trailing);
        double line_y = boxes[i].y;
        for (std::uint32_t j = 0; j < b.line_count; ++j) {
            const auto index = b.line_start + j;
            positions[index] = {boxes[i].x, line_y};
            line_y += lines[index].height;
            extent_width = std::max(extent_width, boxes[i].x + lines[index].width + cursors[i].trailing);
        }
        if (!std::isfinite(boxes[i].y) || !std::isfinite(p.y) || !std::isfinite(line_y)) return false;
    }
    height = forest.y + forest.pending;
    return std::isfinite(height) && std::isfinite(extent_width);
}
}

extern "C" progpu_native_status progpu_native_document_resolve_widths(
    const block* blocks, std::uint32_t count, double width, box* output, std::uint32_t capacity) {
    if (!valid_buffer(blocks, count) || !valid_buffer(output, count) || capacity < count ||
        !std::isfinite(width) || width < 0.0 ||
        !disjoint(std::array{bytes(blocks, count), bytes(output, count)})) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        std::vector<box> boxes(count);
        if (!widths({blocks, count}, width, boxes, {})) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        if (!boxes.empty()) std::copy(boxes.begin(), boxes.end(), output);
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) { return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY; }
    catch (...) { return PROGPU_NATIVE_STATUS_INTERNAL_ERROR; }
}

static progpu_native_status arrange_document(
    const block* blocks, std::uint32_t count, double width, const line* lines, std::uint32_t line_count,
    const measured_object* objects, std::uint32_t object_count,
    box* output, std::uint32_t capacity, position* positions, std::uint32_t position_capacity,
    progpu_native_document_flow_result* result,
    const row* rows = nullptr, std::uint32_t row_count = 0U,
    const double* columns = nullptr, std::uint32_t column_count = 0U,
    const cell* cells = nullptr, std::uint32_t cell_count = 0U) {
    if (!valid_buffer(blocks, count) || !valid_buffer(lines, line_count) || !valid_buffer(output, count) ||
        !valid_buffer(objects, object_count) || !valid_buffer(rows, row_count) ||
        !valid_buffer(columns, column_count) || !valid_buffer(cells, cell_count) ||
        !valid_buffer(positions, line_count) || !valid_buffer(result, 1) || capacity < count || position_capacity < line_count ||
        !std::isfinite(width) || width < 0.0 ||
        !disjoint(std::array{bytes(blocks, count), bytes(lines, line_count), bytes(objects, object_count), bytes(output, count),
            bytes(positions, line_count), bytes(result, 1), bytes(rows, row_count),
            bytes(columns, column_count), bytes(cells, cell_count)}) || result->struct_size != sizeof(*result))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        std::vector<box> boxes(count);
        std::vector<state> states(count);
        std::vector<position> placed(line_count);
        row_layout layout{{rows, row_count}, {cells, cell_count}, {columns, column_count}, {}, {}, {}};
        double height = 0.0, extent_width = width;
        if (!layout.initialize({blocks, count}) || !widths({blocks, count}, width, boxes, layout) ||
            !measure({blocks, count}, {lines, line_count}, states, {objects, object_count}, layout) ||
            !place({blocks, count}, {lines, line_count}, states, {objects, object_count}, boxes, placed, extent_width, height, layout))
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        if (!boxes.empty()) std::copy(boxes.begin(), boxes.end(), output);
        if (!placed.empty()) std::copy(placed.begin(), placed.end(), positions);
        *result = {static_cast<std::uint32_t>(sizeof(*result)), count, line_count, 0U, extent_width, height};
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) { return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY; }
    catch (...) { return PROGPU_NATIVE_STATUS_INTERNAL_ERROR; }
}

extern "C" progpu_native_status progpu_native_document_arrange(
    const block* blocks, std::uint32_t count, double width, const line* lines, std::uint32_t line_count,
    box* output, std::uint32_t capacity, position* positions, std::uint32_t position_capacity,
    progpu_native_document_flow_result* result) {
    return arrange_document(blocks, count, width, lines, line_count, nullptr, 0U,
        output, capacity, positions, position_capacity, result);
}

extern "C" progpu_native_status progpu_native_document_arrange_with_objects(
    const block* blocks, std::uint32_t count, double width, const line* lines, std::uint32_t line_count,
    const measured_object* objects, std::uint32_t object_count,
    box* output, std::uint32_t capacity, position* positions, std::uint32_t position_capacity,
    progpu_native_document_flow_result* result) {
    return arrange_document(blocks, count, width, lines, line_count, objects, object_count,
        output, capacity, positions, position_capacity, result);
}

extern "C" progpu_native_status progpu_native_document_resolve_widths_with_rows(
    const block* blocks, std::uint32_t count, double width,
    const row* rows, std::uint32_t row_count, const double* columns, std::uint32_t column_count,
    const cell* cells, std::uint32_t cell_count, box* output, std::uint32_t capacity) {
    if (!valid_buffer(blocks, count) || !valid_buffer(rows, row_count) || !valid_buffer(columns, column_count) ||
        !valid_buffer(cells, cell_count) || !valid_buffer(output, count) || capacity < count ||
        !std::isfinite(width) || width < 0.0 ||
        !disjoint(std::array{bytes(blocks, count), bytes(rows, row_count), bytes(columns, column_count),
            bytes(cells, cell_count), bytes(output, count)})) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        std::vector<box> boxes(count);
        row_layout layout{{rows, row_count}, {cells, cell_count}, {columns, column_count}, {}, {}, {}};
        if (!layout.initialize({blocks, count}) || !widths({blocks, count}, width, boxes, layout))
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        if (!boxes.empty()) std::copy(boxes.begin(), boxes.end(), output);
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) { return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY; }
    catch (...) { return PROGPU_NATIVE_STATUS_INTERNAL_ERROR; }
}

extern "C" progpu_native_status progpu_native_document_arrange_with_rows(
    const block* blocks, std::uint32_t count, double width, const line* lines, std::uint32_t line_count,
    const measured_object* objects, std::uint32_t object_count,
    const row* rows, std::uint32_t row_count, const double* columns, std::uint32_t column_count,
    const cell* cells, std::uint32_t cell_count,
    box* output, std::uint32_t capacity, position* positions, std::uint32_t position_capacity,
    progpu_native_document_flow_result* result) {
    return arrange_document(blocks, count, width, lines, line_count, objects, object_count,
        output, capacity, positions, position_capacity, result, rows, row_count, columns, column_count, cells, cell_count);
}

extern "C" progpu_native_status progpu_native_document_paginate(
    const progpu_native_document_fragment_line* lines, std::uint32_t count,
    double height, std::uint32_t columns,
    progpu_native_document_fragment_position* positions, std::uint32_t capacity,
    progpu_native_document_pagination_result* result) {
    if (!valid_buffer(lines, count) || !valid_buffer(positions, count) || !valid_buffer(result, 1U) ||
        capacity < count || !std::isfinite(height) || height <= 0.0 || columns == 0U || columns > 1024U ||
        !disjoint(std::array{bytes(lines, count), bytes(positions, count), bytes(result, 1U)}) ||
        result->struct_size != sizeof(*result)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        // Prefix sums plus predecessor/next-force indices avoid repeatedly
        // scanning long kept ranges after a rollback. O(N + F log N) time,
        // O(N) temporary storage; prefixes/break decisions are ordered, not
        // independent SIMD lanes. Metric validation keeps the shared SIMD path.
        std::vector<double> prefix(static_cast<std::size_t>(count) + 1U);
        std::vector<std::uint32_t> previous(static_cast<std::size_t>(count) + 1U);
        std::vector<std::uint32_t> forced(static_cast<std::size_t>(count) + 1U, count);
        std::vector<progpu_native_document_fragment_position> placed(count);
        for (std::uint32_t i = 0U; i < count; ++i) {
            const auto& l = lines[i];
            if (l.allow_break_before > 1U || l.force_column_before > 1U || l.force_page_before > 1U ||
                l.force_column_before + l.force_page_before > 1U || l.reserved != 0U ||
                !finite_nonnegative_pair(l.height, l.space_before) || l.height == 0.0 ||
                !finite_nonnegative_pair(l.leading_space, 0.0)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            const double top = prefix[i] + l.space_before;
            prefix[i + 1U] = top + l.height;
            if (!std::isfinite(prefix[i + 1U]) || prefix[i + 1U] <= top)
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            previous[i] = (l.allow_break_before != 0U || l.force_column_before != 0U || l.force_page_before != 0U)
                ? i : (i == 0U ? 0U : previous[i - 1U]);
        }
        previous[count] = count;
        for (std::uint32_t i = count; i-- > 0U;)
            forced[i] = lines[i].force_column_before != 0U || lines[i].force_page_before != 0U ? i : forced[i + 1U];
        std::uint32_t start = 0U, page = 0U, column = 0U, fragments = 0U;
        while (start < count) {
            // Compare local differences rather than adding height to a possibly
            // large prefix. Leading space is explicit source fragmentation policy.
            const auto fits = [&](std::uint32_t end) noexcept {
                return prefix[end] - prefix[start] - lines[start].space_before <= height - lines[start].leading_space;
            };
            const std::uint32_t stop = forced[start + 1U];
            std::uint32_t low = start, high = stop;
            while (low < high) {
                const std::uint32_t middle = low + (high - low + 1U) / 2U;
                if (fits(middle)) low = middle; else high = middle - 1U;
            }
            const std::uint32_t end = previous[low];
            if (end <= start) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
            for (std::uint32_t i = start; i < end; ++i) {
                const double y = lines[start].leading_space + (prefix[i] - prefix[start]) +
                    (lines[i].space_before - lines[start].space_before);
                if (!std::isfinite(y) || y < 0.0 || y + lines[i].height > height)
                    return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
                placed[i] = {page, column, y};
            }
            ++fragments;
            start = end;
            if (start < count) {
                if (lines[start].force_page_before != 0U || column + 1U == columns) { ++page; column = 0U; }
                else ++column;
            }
        }
        if (!placed.empty()) std::copy(placed.begin(), placed.end(), positions);
        *result = {static_cast<std::uint32_t>(sizeof(*result)), count, fragments, count == 0U ? 0U : page + 1U};
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) { return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY; }
    catch (...) { return PROGPU_NATIVE_STATUS_INTERNAL_ERROR; }
}
