#include "progpu_native_hinted_text_layout.hpp"
#include "progpu_native_hinted_transport.hpp"
#include "../progpu_native_text_layout_origin_internal.hpp"

#include <algorithm>
#include <cmath>
#include <limits>
#include <new>

// Original ProGPU sources: append_logical_bidi_run in
// Text/Interop/progpu_native_text_shaping_interop.cpp restores cluster groups;
// Text/progpu_native_text_layout.cpp owns fitting, measured writing, alignment
// and RTL justified pen origins. This adapter adds ownership and units only.
namespace progpu::native::text {
namespace {
template<class T>
bool valid_span(std::span<const T> values) noexcept {
    const auto start = reinterpret_cast<std::uintptr_t>(values.data());
    constexpr auto maximum = std::numeric_limits<std::uintptr_t>::max();
    return values.size() <= maximum / sizeof(T) &&
        (values.empty() || (values.data() != nullptr && start % alignof(T) == 0U)) &&
        values.size_bytes() <= maximum - start;
}

bool overlaps(const void* output, std::size_t output_bytes,
    const void* input, std::size_t input_bytes) noexcept {
    if (output_bytes == 0U || input_bytes == 0U) return false;
    const auto first = reinterpret_cast<std::uintptr_t>(output);
    const auto second = reinterpret_cast<std::uintptr_t>(input);
    constexpr auto maximum = std::numeric_limits<std::uintptr_t>::max();
    if (output_bytes > maximum - first || input_bytes > maximum - second) return true;
    return first < second + input_bytes && second < first + output_bytes;
}

template<class T>
bool aliases_vector(const void* output, std::size_t bytes, const std::vector<T>& values) noexcept {
    if (values.capacity() > std::numeric_limits<std::size_t>::max() / sizeof(T)) return true;
    return overlaps(output, bytes, values.data(), values.capacity() * sizeof(T));
}

bool aliases_run(void* output, std::size_t bytes, const hinted_shaped_run& run) noexcept {
    if (overlaps(output, bytes, &run, sizeof(run)) || aliases_vector(output, bytes, run.glyphs) ||
        aliases_vector(output, bytes, run.descriptor_indices) || aliases_vector(output, bytes, run.normalized_coordinates))
        return true;
    if (run.batch == nullptr) return false;
    const auto& batch = *run.batch;
    if (hinted_batch_output_aliases(batch, std::span(static_cast<std::byte*>(output), bytes)) ||
        aliases_vector(output, bytes, batch.glyphs)) return true;
    if (batch.identity != nullptr && (aliases_vector(output, bytes, batch.identity->variation_coordinates_16_16) ||
        (batch.identity->source != nullptr && aliases_vector(output, bytes, batch.identity->source->bytes)))) return true;
    for (const auto& glyph : batch.glyphs)
        if (aliases_vector(output, bytes, glyph.points) || aliases_vector(output, bytes, glyph.tags) ||
            aliases_vector(output, bytes, glyph.contour_ends)) return true;
    return false;
}

bool aliases_layout(void* output, std::size_t bytes, const hinted_text_layout& layout) noexcept {
    return overlaps(output, bytes, &layout, sizeof(layout)) ||
        (layout.run != nullptr && aliases_run(output, bytes, *layout.run)) ||
        aliases_vector(output, bytes, layout.logical_glyphs) || aliases_vector(output, bytes, layout.logical_run_indices) ||
        aliases_vector(output, bytes, layout.breaks_after) || aliases_vector(output, bytes, layout.logical_bidi_levels) ||
        aliases_vector(output, bytes, layout.logical_cluster_ends) || aliases_vector(output, bytes, layout.justification_classes) ||
        aliases_vector(output, bytes, layout.item_metrics) || aliases_vector(output, bytes, layout.glyphs) ||
        aliases_vector(output, bytes, layout.descriptor_indices) || aliases_vector(output, bytes, layout.bidi_levels) ||
        aliases_vector(output, bytes, layout.cluster_ends) || aliases_vector(output, bytes, layout.lines) ||
        aliases_vector(output, bytes, layout.line_origins);
}

bool retain_line_origins(hinted_text_layout& layout) noexcept {
    for (std::size_t index = 0U; index < layout.lines.size(); ++index) {
        const auto& line = layout.lines[index];
        const bool rtl_justified = (line.flags &
            static_cast<std::uint8_t>(positioned_text_line_flags::right_to_left_justified)) != 0U;
        float trailing_width = 0.0F;
        if (rtl_justified) {
            if (line.glyph_count == 0U) return false;
            std::size_t first = layout.logical_glyphs.size(), last = 0U;
            for (std::size_t item = line.glyph_start; item <
                static_cast<std::size_t>(line.glyph_start) + line.glyph_count; ++item) {
                first = std::min(first, static_cast<std::size_t>(layout.glyphs[item].glyph_index));
                last = std::max(last, static_cast<std::size_t>(layout.glyphs[item].glyph_index) + 1U);
            }
            if (last > layout.logical_glyphs.size() || last - first != line.glyph_count) return false;
            std::size_t content_end = last;
            while (content_end > first && layout.justification_classes[content_end - 1U] !=
                text_justification_class::content) --content_end;
            // Exact source writer order, before visual reordering/expansion.
            for (std::size_t item = content_end; item < last; ++item)
                trailing_width += static_cast<float>(layout.logical_glyphs[item].advance_x) * layout.options.scale;
        }
        auto origin = detail::text_line_pen_origin(false, 0.0F, rtl_justified, trailing_width);
        const auto alignment = detail::text_line_alignment_shift(layout.options, line.width);
        if (alignment > 0.0F) origin += alignment;
        if (!std::isfinite(origin)) return false;
        layout.line_origins[index] = origin;
    }
    return true;
}
} // namespace

bool try_layout_hinted_shaped_run(std::shared_ptr<const hinted_shaped_run> run,
    std::span<const text_line_break_kind> breaks_after, std::span<const std::int8_t> bidi_levels,
    std::span<const std::int32_t> cluster_ends, std::span<const text_item_metrics> item_metrics,
    std::span<const text_justification_class> justification_classes,
    std::int8_t paragraph_level, float logical_units_per_physical_pixel,
    const text_layout_options& options, std::shared_ptr<const hinted_text_layout>& result,
    font_error* error) noexcept {
    // Protect both publication objects against complete borrow capacities and
    // retained owners before reading a potentially aliased result or writing.
    if (!valid_span(breaks_after) || !valid_span(bidi_levels) || !valid_span(cluster_ends) ||
        !valid_span(item_metrics) || !valid_span(justification_classes)) return false;
    const auto aliases_inputs = [&](void* output, std::size_t bytes) noexcept {
        return overlaps(output, bytes, breaks_after.data(), breaks_after.size_bytes()) ||
            overlaps(output, bytes, bidi_levels.data(), bidi_levels.size_bytes()) ||
            overlaps(output, bytes, cluster_ends.data(), cluster_ends.size_bytes()) ||
            overlaps(output, bytes, item_metrics.data(), item_metrics.size_bytes()) ||
            overlaps(output, bytes, justification_classes.data(), justification_classes.size_bytes()) ||
            overlaps(output, bytes, &options, sizeof(options)) ||
            overlaps(output, bytes, &run, sizeof(run)) ||
            (run != nullptr && aliases_run(output, bytes, *run));
    };
    if (aliases_inputs(&result, sizeof(result))) return false;
    if (result != nullptr && aliases_layout(&result, sizeof(result), *result)) return false;
    if (error != nullptr && (!valid_span(std::span<const font_error>(error, 1U)) ||
        overlaps(error, sizeof(font_error), &result, sizeof(result)) ||
        aliases_inputs(error, sizeof(font_error)) ||
        (result != nullptr && aliases_layout(error, sizeof(font_error), *result)))) return false;
    const auto fail = [error](font_error value) noexcept {
        if (error != nullptr) *error = value;
        return false;
    };
    try {
        if (run == nullptr || run->batch == nullptr || run->batch->identity == nullptr ||
            run->batch->identity->source == nullptr || run->batch->identity->source->bytes.empty() ||
            (run->direction != shaping_direction::left_to_right && run->direction != shaping_direction::right_to_left) ||
            run->glyphs.size() > std::numeric_limits<std::uint32_t>::max() ||
            run->descriptor_indices.size() != run->glyphs.size() ||
            run->source_descriptor_count > run->batch->glyphs.size() ||
            (paragraph_level != 0 && paragraph_level != 1) || options.direction != run->direction ||
            options.scale != 1.0F || options.trimming != text_trimming::none ||
            !std::isfinite(logical_units_per_physical_pixel) || logical_units_per_physical_pixel <= 0.0F)
            return fail(font_error::invalid_argument);
        const auto count = run->glyphs.size();
        if (breaks_after.size() < count || bidi_levels.size() < count || cluster_ends.size() < count ||
            item_metrics.size() < count || (!justification_classes.empty() && justification_classes.size() < count))
            return fail(font_error::insufficient_buffer);
        const auto scale = logical_units_per_physical_pixel / 64.0F;
        if (!std::isfinite(scale) || scale <= 0.0F) return fail(font_error::invalid_argument);
        for (std::size_t index = 0U; index < count; ++index) {
            const auto& glyph = run->glyphs[index];
            const auto descriptor = run->descriptor_indices[index];
            if (descriptor >= run->source_descriptor_count ||
                run->batch->glyphs[descriptor].glyph_index != glyph.glyph_id || glyph.cluster < 0 ||
                glyph.advance_y == std::numeric_limits<std::int32_t>::min() ||
                glyph.offset_y == std::numeric_limits<std::int32_t>::min() ||
                cluster_ends[index] <= glyph.cluster || bidi_levels[index] < 0 || bidi_levels[index] > 125 ||
                (bidi_levels[index] & 1) != (run->direction == shaping_direction::right_to_left ? 1 : 0) ||
                static_cast<std::uint8_t>(breaks_after[index]) > static_cast<std::uint8_t>(text_line_break_kind::mandatory))
                return fail(font_error::invalid_argument);
        }
        auto candidate = std::make_shared<hinted_text_layout>();
        candidate->run = std::move(run);
        candidate->logical_units_per_physical_pixel = logical_units_per_physical_pixel;
        candidate->paragraph_level = paragraph_level;
        candidate->options = options;
        candidate->options.scale = scale;
        candidate->logical_glyphs.reserve(count);
        candidate->logical_run_indices.reserve(count);
        candidate->breaks_after.reserve(count);
        candidate->logical_bidi_levels.reserve(count);
        candidate->logical_cluster_ends.reserve(count);
        candidate->item_metrics.reserve(count);
        if (!justification_classes.empty()) candidate->justification_classes.reserve(count);
        const auto append_group = [&](std::size_t first, std::size_t last) {
            for (std::size_t index = first; index < last; ++index) {
                auto glyph = candidate->run->glyphs[index];
                glyph.advance_y = -glyph.advance_y;
                glyph.offset_y = -glyph.offset_y;
                candidate->logical_glyphs.push_back(glyph);
                candidate->logical_run_indices.push_back(static_cast<std::uint32_t>(index));
                candidate->breaks_after.push_back(breaks_after[index]);
                candidate->logical_bidi_levels.push_back(bidi_levels[index]);
                candidate->logical_cluster_ends.push_back(cluster_ends[index]);
                candidate->item_metrics.push_back(item_metrics[index]);
                if (!justification_classes.empty()) candidate->justification_classes.push_back(justification_classes[index]);
            }
        };
        if (candidate->run->direction == shaping_direction::left_to_right) append_group(0U, count);
        else {
            std::size_t last = count;
            while (last != 0U) {
                std::size_t first = last - 1U;
                while (first != 0U && candidate->run->glyphs[first - 1U].cluster ==
                    candidate->run->glyphs[last - 1U].cluster) --first;
                append_group(first, last);
                last = first;
            }
        }
        // Logical cluster groups must remain contiguous and source ordered;
        // restored order owns the same glyphs and descriptors, never a reshape.
        for (std::size_t index = 1U; index < count; ++index)
            if (candidate->logical_glyphs[index].cluster < candidate->logical_glyphs[index - 1U].cluster ||
                (candidate->logical_glyphs[index].cluster > candidate->logical_glyphs[index - 1U].cluster &&
                 candidate->logical_cluster_ends[index - 1U] > candidate->logical_glyphs[index].cluster) ||
                (candidate->logical_glyphs[index].cluster == candidate->logical_glyphs[index - 1U].cluster &&
                 (candidate->logical_cluster_ends[index] != candidate->logical_cluster_ends[index - 1U] ||
                  candidate->logical_bidi_levels[index] != candidate->logical_bidi_levels[index - 1U])))
                return fail(font_error::invalid_argument);
        text_layout_requirements requirements{};
        font_error failure = font_error::none;
        // The scaled entrypoint preflights all four metric products with the
        // existing SIMD policy, including offsets, before writer publication.
        std::vector<float> scales(count, scale);
        if (!try_get_scaled_text_layout_requirements(candidate->logical_glyphs, candidate->breaks_after,
            scales, candidate->options, requirements, &failure)) return fail(failure);
        candidate->glyphs.resize(requirements.glyph_capacity);
        candidate->lines.resize(requirements.line_capacity);
        std::vector<text_visual_cluster_group> groups(requirements.glyph_capacity);
        std::vector<std::uint32_t> indices(requirements.glyph_capacity);
        std::uint32_t glyph_count = 0U, line_count = 0U;
        if (!try_layout_measured_logical_shaped_text(candidate->logical_glyphs, candidate->breaks_after,
            candidate->logical_bidi_levels, scales, paragraph_level, candidate->options, {}, {}, {groups, indices},
            candidate->glyphs, candidate->lines, glyph_count, line_count,
            candidate->justification_classes, candidate->item_metrics, &failure)) return fail(failure);
        candidate->glyphs.resize(glyph_count);
        candidate->lines.resize(line_count);
        candidate->line_origins.resize(line_count);
        if (!retain_line_origins(*candidate)) return fail(font_error::invalid_argument);
        candidate->descriptor_indices.reserve(glyph_count);
        candidate->bidi_levels.reserve(glyph_count);
        candidate->cluster_ends.reserve(glyph_count);
        for (auto& glyph : candidate->glyphs) {
            if (glyph.glyph_index >= candidate->logical_run_indices.size() ||
                !std::isfinite(glyph.x) || !std::isfinite(glyph.y) ||
                !std::isfinite(glyph.advance_x) || !std::isfinite(glyph.advance_y) ||
                !std::isfinite(glyph.x + glyph.advance_x) || !std::isfinite(glyph.y + glyph.advance_y))
                return fail(font_error::invalid_argument);
            const auto logical = glyph.glyph_index;
            const auto original = candidate->logical_run_indices[logical];
            candidate->descriptor_indices.push_back(candidate->run->descriptor_indices[original]);
            candidate->bidi_levels.push_back(candidate->logical_bidi_levels[logical]);
            candidate->cluster_ends.push_back(candidate->logical_cluster_ends[logical]);
            glyph.glyph_index = original;
        }
        if (!try_measure_measured_text_lines(candidate->lines, options.maximum_width,
            candidate->metrics, &failure)) return fail(failure);
        result = std::move(candidate);
        if (error != nullptr) *error = font_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return fail(font_error::insufficient_buffer);
    } catch (...) {
        return fail(font_error::invalid_argument);
    }
}
} // namespace progpu::native::text
