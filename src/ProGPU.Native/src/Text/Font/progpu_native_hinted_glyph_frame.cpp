#include "progpu_native_hinted_glyph_frame.hpp"

#include <cmath>
#include <limits>
#include <new>

// Owned packing only. Original outline conversion, fitting and actual renderer
// queue/resource ownership remain their existing independent implementations.
namespace progpu::native::text {
namespace {
struct memory_range final { std::uintptr_t begin = 0U, end = 0U; };

template<class T>
bool range(const T* data, std::size_t count, memory_range& result) noexcept {
    constexpr auto maximum = std::numeric_limits<std::uintptr_t>::max();
    const auto begin = reinterpret_cast<std::uintptr_t>(data);
    if (count > maximum / sizeof(T) ||
        (count != 0U && (data == nullptr || begin % alignof(T) != 0U))) return false;
    const auto bytes = count * sizeof(T);
    if (bytes > maximum - begin) return false;
    result = {begin, begin + bytes}; return true;
}

bool overlaps(memory_range a, memory_range b) noexcept {
    return a.begin != a.end && b.begin != b.end && a.begin < b.end && b.begin < a.end;
}

template<class T>
bool aliases(memory_range output, const T* data, std::size_t count = 1U) noexcept {
    memory_range input{};
    return !range(data, count, input) || overlaps(output, input);
}

template<class T>
bool aliases(memory_range output, const std::vector<T>& values) noexcept {
    return aliases(output, values.data(), values.capacity());
}

bool aliases_run(memory_range output, const hinted_shaped_run& run) noexcept {
    if (aliases(output, &run) || aliases(output, run.shaping_input) || aliases(output, run.glyphs) ||
        aliases(output, run.descriptor_indices) || aliases(output, run.normalized_coordinates)) return true;
    if (run.batch == nullptr) return false;
    const auto& batch = *run.batch;
    if (aliases(output, &batch) || aliases(output, batch.glyphs)) return true;
    if (batch.identity != nullptr) {
        const auto& identity = *batch.identity;
        if (aliases(output, &identity) || aliases(output, identity.variation_coordinates_16_16)) return true;
        if (identity.source != nullptr && (aliases(output, identity.source.get()) ||
            aliases(output, identity.source->bytes))) return true;
    }
    for (const auto& glyph : batch.glyphs)
        if (aliases(output, glyph.points) || aliases(output, glyph.tags) || aliases(output, glyph.contour_ends)) return true;
    return false;
}

bool aliases_layout(memory_range output, const hinted_text_layout& layout) noexcept {
    return aliases(output, &layout) || (layout.run != nullptr && aliases_run(output, *layout.run)) ||
        aliases(output, layout.logical_glyphs) || aliases(output, layout.logical_run_indices) ||
        aliases(output, layout.breaks_after) || aliases(output, layout.logical_bidi_levels) ||
        aliases(output, layout.logical_cluster_ends) || aliases(output, layout.justification_classes) ||
        aliases(output, layout.item_metrics) || aliases(output, layout.glyphs) || aliases(output, layout.descriptor_indices) ||
        aliases(output, layout.bidi_levels) || aliases(output, layout.cluster_ends) || aliases(output, layout.lines) ||
        aliases(output, layout.line_origins);
}

bool finite(progpu_native_point point) noexcept { return std::isfinite(point.x) && std::isfinite(point.y); }
bool finite(progpu_native_color color) noexcept {
    return std::isfinite(color.r) && std::isfinite(color.g) && std::isfinite(color.b) && std::isfinite(color.a);
}

bool valid_layout(const hinted_text_layout& layout, const hinted_shaped_run& run,
    const hinted_glyph_target& target, std::span<std::uint32_t> logical_by_run,
    std::span<std::uint8_t> positioned_by_run) noexcept {
    const auto count = run.glyphs.size(), fitted = layout.glyphs.size();
    if ((run.direction != shaping_direction::left_to_right && run.direction != shaping_direction::right_to_left) ||
        layout.options.direction != run.direction || layout.options.trimming != text_trimming::none ||
        (layout.paragraph_level != 0 && layout.paragraph_level != 1) ||
        !std::isfinite(layout.logical_units_per_physical_pixel) || layout.logical_units_per_physical_pixel <= 0.0F ||
        !std::isfinite(layout.options.scale) || layout.options.scale <= 0.0F ||
        layout.options.scale != layout.logical_units_per_physical_pixel / 64.0F ||
        layout.logical_glyphs.size() != count || layout.logical_run_indices.size() != count ||
        layout.breaks_after.size() != count || layout.logical_bidi_levels.size() != count ||
        layout.logical_cluster_ends.size() != count || layout.item_metrics.size() != count ||
        (!layout.justification_classes.empty() && layout.justification_classes.size() != count) ||
        layout.descriptor_indices.size() != fitted || layout.bidi_levels.size() != fitted ||
        layout.cluster_ends.size() != fitted || fitted > count || layout.lines.size() != layout.line_origins.size()) return false;
    // Original cluster-group restoration, as used by the retained fitter. This
    // verifies a complete one-to-one logical→original map without ID searches.
    std::size_t logical = 0U;
    const auto group = [&](std::size_t first, std::size_t last) noexcept {
        for (std::size_t original = first; original < last; ++original, ++logical) {
            const auto& source = run.glyphs[original];
            const auto& restored = layout.logical_glyphs[logical];
            if (layout.logical_run_indices[logical] != original || source.advance_y == INT32_MIN || source.offset_y == INT32_MIN ||
                restored.glyph_id != source.glyph_id || restored.code_point != source.code_point || restored.cluster != source.cluster ||
                restored.flags != source.flags || restored.advance_x != source.advance_x || restored.offset_x != source.offset_x ||
                restored.advance_y != -source.advance_y || restored.offset_y != -source.offset_y ||
                source.cluster < 0 || layout.logical_cluster_ends[logical] <= source.cluster ||
                layout.logical_bidi_levels[logical] < 0 || layout.logical_bidi_levels[logical] > 125 ||
                (layout.logical_bidi_levels[logical] & 1) != (run.direction == shaping_direction::right_to_left ? 1 : 0) ||
                static_cast<std::uint8_t>(layout.breaks_after[logical]) > static_cast<std::uint8_t>(text_line_break_kind::mandatory) ||
                !std::isfinite(layout.item_metrics[logical].ascent) || layout.item_metrics[logical].ascent < 0.0F ||
                !std::isfinite(layout.item_metrics[logical].descent) || layout.item_metrics[logical].descent < 0.0F ||
                !std::isfinite(layout.item_metrics[logical].ascent + layout.item_metrics[logical].descent) ||
                (!layout.justification_classes.empty() && static_cast<std::uint8_t>(layout.justification_classes[logical]) >
                    static_cast<std::uint8_t>(text_justification_class::word_space))) return false;
            logical_by_run[original] = static_cast<std::uint32_t>(logical);
        }
        return true;
    };
    if (run.direction == shaping_direction::left_to_right) { if (!group(0U, count)) return false; }
    else {
        std::size_t last = count;
        while (last != 0U) {
            std::size_t first = last - 1U;
            while (first != 0U && run.glyphs[first - 1U].cluster == run.glyphs[last - 1U].cluster) --first;
            if (!group(first, last)) return false;
            last = first;
        }
    }
    for (std::size_t index = 1U; index < count; ++index) {
        const auto previous = layout.logical_glyphs[index - 1U].cluster, current = layout.logical_glyphs[index].cluster;
        if (current < previous || (current > previous && layout.logical_cluster_ends[index - 1U] > current) ||
            (current == previous && (layout.logical_cluster_ends[index] != layout.logical_cluster_ends[index - 1U] ||
                layout.logical_bidi_levels[index] != layout.logical_bidi_levels[index - 1U]))) return false;
    }
    for (std::size_t index = 0U; index < fitted; ++index) {
        const auto& glyph = layout.glyphs[index];
        const auto original = glyph.glyph_index;
        if (original >= count || original >= run.descriptor_indices.size() ||
            glyph.glyph_id != run.glyphs[original].glyph_id || glyph.cluster != run.glyphs[original].cluster ||
            layout.descriptor_indices[index] != run.descriptor_indices[original] ||
            positioned_by_run[original] != 0U ||
            layout.bidi_levels[index] != layout.logical_bidi_levels[logical_by_run[original]] ||
            layout.cluster_ends[index] != layout.logical_cluster_ends[logical_by_run[original]] ||
            !std::isfinite(glyph.x) || !std::isfinite(glyph.y) || !std::isfinite(glyph.advance_x) || !std::isfinite(glyph.advance_y) ||
            !std::isfinite(glyph.x + glyph.advance_x) || !std::isfinite(glyph.y + glyph.advance_y) ||
            !finite(progpu_native_point{glyph.x + target.logical_origin.x, glyph.y + target.logical_origin.y})) return false;
        positioned_by_run[original] = 1U;
    }
    std::size_t covered = 0U;
    for (std::size_t index = 0U; index < layout.lines.size(); ++index) {
        const auto& line = layout.lines[index];
        if (line.glyph_start != covered || line.glyph_count == 0U || line.glyph_count > fitted - covered ||
            !std::isfinite(line.width) || line.width < 0.0F || !std::isfinite(line.baseline_y) ||
            !std::isfinite(line.height) || line.height < 0.0F || !std::isfinite(layout.line_origins[index]) ||
            (line.flags & ~static_cast<std::uint8_t>(positioned_text_line_flags::right_to_left_justified)) != 0U ||
            line.reserved1 != 0U || line.reserved2 != 0U) return false;
        covered += line.glyph_count;
    }
    return covered == fitted;
}
} // namespace

progpu_native_glyph_frame hinted_glyph_frame::frame() const noexcept {
    progpu_native_glyph_frame result{};
    result.struct_size = sizeof(result); result.width = target_.width; result.height = target_.height;
    result.dpi_scale = target_.dpi_scale; result.target_view = target_.target_view; result.clear_color = target_.clear_color;
    result.outlines = outlines_.data(); result.outline_count = outlines_.size();
    result.segments = segments_.data(); result.segment_count = segments_.size();
    result.glyphs = glyphs_.data(); result.glyph_count = glyphs_.size();
    return result; // flags/revision zero; draw_state null, deliberately.
}

bool hinted_glyph_frame_output_aliases(const hinted_glyph_frame& frame, std::span<std::byte> output) noexcept {
    memory_range range_out{};
    return !range(output.data(), output.size(), range_out) || aliases(range_out, &frame) ||
        (frame.layout_ != nullptr && aliases_layout(range_out, *frame.layout_)) ||
        aliases(range_out, frame.outlines_) || aliases(range_out, frame.segments_) || aliases(range_out, frame.glyphs_) ||
        aliases(range_out, frame.source_outline_indices_) || aliases(range_out, frame.run_outline_indices_) ||
        aliases(range_out, frame.draw_layout_indices_);
}

bool try_create_hinted_glyph_frame(std::shared_ptr<const hinted_text_layout> layout,
    std::shared_ptr<const hinted_shaped_run> run, const hinted_glyph_target& target,
    std::shared_ptr<const hinted_glyph_frame>& result, hinted_glyph_frame_error* error,
    hinted_projection_policy policy, hinted_outline_coverage coverage) noexcept {
    memory_range result_range{}, error_range{};
    const auto input_alias = [&](memory_range output) noexcept {
        return aliases(output, &target) || aliases(output, &layout) || aliases(output, &run) ||
            (layout != nullptr && aliases_layout(output, *layout)) || (run != nullptr && aliases_run(output, *run));
    };
    if (!range(&result, 1U, result_range) || input_alias(result_range)) return false;
    const auto old_alias = [&](memory_range output) noexcept {
        return result != nullptr && hinted_glyph_frame_output_aliases(*result,
            {reinterpret_cast<std::byte*>(output.begin), output.end - output.begin});
    };
    if (old_alias(result_range) || (error != nullptr && (!range(error, 1U, error_range) ||
        overlaps(error_range, result_range) || input_alias(error_range) || old_alias(error_range)))) return false;
    const auto fail = [&](hinted_glyph_frame_error_code code, hinted_outline_error outline = hinted_outline_error::none) noexcept {
        if (error != nullptr) *error = {code, outline};
        return false;
    };
    try {
        if (layout == nullptr || run == nullptr || layout->run != run || run->batch == nullptr ||
            run->batch->identity == nullptr || run->batch->identity->source == nullptr ||
            run->batch->identity->source->bytes.empty() || target.width == 0U || target.height == 0U ||
            target.target_view == 0U || !std::isfinite(target.dpi_scale) || target.dpi_scale <= 0.0F ||
            !finite(target.logical_origin) || !finite(target.color) || !finite(target.clear_color))
            return fail(hinted_glyph_frame_error_code::invalid_argument);
        const float atlas_ratio = layout->logical_units_per_physical_pixel * target.dpi_scale;
        const float units_per_pixel = 1.0F / target.dpi_scale;
        const float logical_width = static_cast<float>(target.width) / target.dpi_scale;
        const float logical_height = static_cast<float>(target.height) / target.dpi_scale;
        if (!std::isfinite(atlas_ratio) || atlas_ratio != 1.0F ||
            layout->logical_units_per_physical_pixel != units_per_pixel ||
            !std::isfinite(logical_width) || logical_width <= 0.0F ||
            !std::isfinite(logical_height) || logical_height <= 0.0F ||
            !std::isfinite(2.0F / logical_width) || !std::isfinite(2.0F / logical_height))
            return fail(hinted_glyph_frame_error_code::unsupported_mapping);
        if (run->glyphs.size() > (1U << 24U) || layout->glyphs.size() > (1U << 24U))
            return fail(hinted_glyph_frame_error_code::insufficient_capacity);
        std::vector<std::uint32_t> logical_by_run(run->glyphs.size());
        std::vector<std::uint8_t> positioned_by_run(run->glyphs.size(), 0U);
        if (!valid_layout(*layout, *run, target, logical_by_run, positioned_by_run))
            return fail(hinted_glyph_frame_error_code::invalid_layout);
        hinted_outline_requirements required{};
        auto outline_error = get_hinted_outline_requirements(*run, required, policy, coverage);
        if (outline_error != hinted_outline_error::none)
            return fail(hinted_glyph_frame_error_code::outline_conversion_failed, outline_error);
        // Original executor bounds, plus source/run map slots at its glyph bound.
        if (required.outlines > (1U << 20U) || required.segments > (1U << 24U) ||
            required.source_slots > (1U << 24U) || required.positioned_slots > (1U << 24U) ||
            layout->glyphs.size() > (1U << 24U)) return fail(hinted_glyph_frame_error_code::insufficient_capacity);
        auto candidate = std::shared_ptr<hinted_glyph_frame>(new hinted_glyph_frame{});
        candidate->layout_ = std::move(layout); candidate->target_ = target;
        candidate->coverage_ = coverage;
        candidate->outlines_.resize(required.outlines); candidate->segments_.resize(required.segments);
        candidate->source_outline_indices_.resize(required.source_slots);
        candidate->run_outline_indices_.resize(required.positioned_slots);
        std::vector<sfnt_outline_point> topology(required.scratch_points);
        std::vector<progpu_native_point> physical(required.scratch_points);
        hinted_outline_requirements written{};
        outline_error = write_hinted_run_outlines(*run, {topology, physical}, candidate->outlines_, candidate->segments_,
            candidate->source_outline_indices_, candidate->run_outline_indices_, written, policy, coverage);
        if (outline_error != hinted_outline_error::none)
            return fail(hinted_glyph_frame_error_code::outline_conversion_failed, outline_error);
        candidate->glyphs_.reserve(candidate->layout_->glyphs.size());
        candidate->draw_layout_indices_.reserve(candidate->layout_->glyphs.size());
        for (std::size_t index = 0U; index < candidate->layout_->glyphs.size(); ++index) {
            const auto& glyph = candidate->layout_->glyphs[index];
            const auto descriptor = candidate->layout_->descriptor_indices[index];
            const auto outline = candidate->source_outline_indices_[descriptor];
            if (outline == hinted_no_outline) continue;
            // Exact source slot, not first matching glyph ID. Preserve every
            // positioned repetition/order; coverage sharing remains executor-owned.
            candidate->glyphs_.push_back({outline, 0U,
                {glyph.x + target.logical_origin.x, glyph.y + target.logical_origin.y},
                {1.0F, 0.0F}, {0.0F, 1.0F}, target.color, 1.0F, 0.0F, 0.0F, 0.0F});
            candidate->draw_layout_indices_.push_back(static_cast<std::uint32_t>(index));
        }
        result = std::move(candidate);
        if (error != nullptr) *error = {};
        return true;
    } catch (const std::bad_alloc&) {
        return fail(hinted_glyph_frame_error_code::resource_exhausted);
    } catch (...) {
        return fail(hinted_glyph_frame_error_code::invalid_argument);
    }
}
} // namespace progpu::native::text
