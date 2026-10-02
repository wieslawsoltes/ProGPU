#include "progpu_native_hinted_source_resource_cache.hpp"
#include "progpu_native_owned_allocation_internal.hpp"

#include <algorithm>
#include <limits>

namespace progpu::native::text {
namespace {
bool wire_glyph(const shaping_glyph& value, progpu_native_text_shaping_glyph& output) noexcept {
    if (value.advance_y == INT32_MIN || value.offset_y == INT32_MIN) return false;
    output = {value.glyph_id, value.code_point, value.cluster, static_cast<std::uint32_t>(value.flags),
        value.advance_x, -value.advance_y, value.offset_x, -value.offset_y};
    return true;
}
template<class T> const T* data(const std::vector<T>& values) noexcept { return values.empty() ? nullptr : values.data(); }
} // namespace

bool hinted_source_resource_cache::allocation_aliases(const void* output, std::size_t bytes) const noexcept {
    const owned_output_range range{output, bytes};
    return range.overlaps(this, sizeof(*this)) || range.overlaps(raw) || range.overlaps(effective) ||
        range.overlaps(prepared) || range.overlaps(positioning_runs) || range.overlaps(slices) ||
        range.overlaps(slice_indices) || range.overlaps(lines);
}

bool cache_hinted_source_resource(const hinted_paragraph_generation& paragraph,
    progpu_native_hinted_source_paragraph_view source, std::shared_ptr<const hinted_source_resource_cache>& result) {
    if (!paragraph.has_source_geometry || paragraph.source_fitting == nullptr ||
        !validate_hinted_source_fitting(paragraph, *paragraph.source_fitting) ||
        paragraph.logical_glyphs.size() > (1U << 24U) || paragraph.runs.size() > (1U << 24U) ||
        source.logical_count != paragraph.logical_glyphs.size() || source.style_count != paragraph.styles.size() ||
        source.glyph_count != paragraph.glyphs.size() || source.line_count != paragraph.source_lines.size()) return false;
    const auto& fitting = *paragraph.source_fitting;
    if (fitting.slices.size() > (1U << 24U) || fitting.lines.size() != source.line_count) return false;
    auto cache = std::make_shared<hinted_source_resource_cache>();
    const auto count = paragraph.logical_glyphs.size();
    cache->raw.resize(count); cache->effective.resize(count);
    cache->slice_indices = fitting.slice_indices;
    cache->slices.resize(fitting.slices.size());
    std::vector<std::uint32_t> slice_starts(fitting.slices.size(), UINT32_MAX), slice_counts(fitting.slices.size(), 0U);
    for (std::size_t i = 0U; i < count; ++i) {
        const auto owner = paragraph.logical_owners[i];
        const auto& raw = paragraph.runs[owner.run_index].generation->glyphs[owner.run_glyph_index];
        if (!wire_glyph(raw, cache->raw[i])) return false;
        const auto slice_index = fitting.slice_indices[i];
        if (slice_index == UINT32_MAX) cache->effective[i] = cache->raw[i];
        else {
            if (slice_index >= fitting.slices.size()) return false;
            const auto& slice = *fitting.slices[slice_index].placement;
            const auto index = fitting.slice_glyph_indices[i];
            if (index >= slice.glyphs.size() || !wire_glyph(slice.glyphs[index], cache->effective[i])) return false;
            if (slice_starts[slice_index] == UINT32_MAX) slice_starts[slice_index] = static_cast<std::uint32_t>(i);
            if (i != static_cast<std::size_t>(slice_starts[slice_index]) + slice_counts[slice_index]) return false;
            ++slice_counts[slice_index];
        }
    }
    for (const auto& run : paragraph.runs) {
        const auto first = static_cast<std::uint32_t>(cache->prepared.size());
        const auto& recipe = run.generation->positioning;
        const auto prepared_count = recipe == nullptr ? 0U : recipe->prepared_glyphs.size();
        if (prepared_count > count - cache->prepared.size() || (recipe != nullptr && prepared_count != run.logical_count)) return false;
        for (std::size_t i = 0U; i < prepared_count; ++i) {
            progpu_native_text_shaping_glyph glyph{};
            if (!wire_glyph(recipe->prepared_glyphs[i], glyph)) return false;
            cache->prepared.push_back(glyph);
        }
        cache->positioning_runs.push_back({first, static_cast<std::uint32_t>(prepared_count), 0U, 0U});
    }
    for (std::size_t i = 0U; i < fitting.slices.size(); ++i) {
        const auto& slice = fitting.slices[i];
        if (slice_starts[i] == UINT32_MAX || slice_counts[i] != slice.placement->prepared_count) return false;
        cache->slices[i] = {slice.run_index, slice.placement->prepared_start, slice.placement->prepared_count,
            slice_starts[i], slice_counts[i], 0U};
    }
    for (const auto& line : fitting.lines)
        cache->lines.push_back({line.glyph_start, line.glyph_count, line.width, line.clipped ? 1U : 0U, 0U});
    auto& view = cache->view;
    view.abi_version = PROGPU_NATIVE_ABI_VERSION; view.struct_size = sizeof(view); view.version = 2U;
    view.source = source; view.first_logical_glyph = fitting.first_logical_glyph;
    view.prepared_count = static_cast<std::uint32_t>(cache->prepared.size());
    view.slice_count = static_cast<std::uint32_t>(cache->slices.size());
    view.intrinsic_widths = {paragraph.source_minimum_intrinsic_width, paragraph.source_maximum_intrinsic_width};
    view.raw_logical_glyphs = data(cache->raw); view.effective_logical_glyphs = data(cache->effective);
    view.positioning_runs = data(cache->positioning_runs); view.prepared_glyphs = data(cache->prepared);
    view.slices = data(cache->slices); view.slice_indices = data(cache->slice_indices); view.fitted_lines = data(cache->lines);
    result = std::move(cache);
    return true;
}
} // namespace progpu::native::text
