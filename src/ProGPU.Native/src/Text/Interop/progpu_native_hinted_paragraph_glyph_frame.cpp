#include "progpu_native_hinted_paragraph_glyph_frame.hpp"
#include "progpu_native_owned_allocation_internal.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>

namespace progpu::native::text {
namespace {
constexpr std::size_t maximum_outlines = 1U << 20U;
constexpr std::size_t maximum_segments = 1U << 24U;
constexpr std::size_t maximum_slots = 1U << 24U;

bool finite(progpu_native_point value) noexcept { return std::isfinite(value.x) && std::isfinite(value.y); }
bool finite(progpu_native_color value) noexcept {
    return std::isfinite(value.r) && std::isfinite(value.g) && std::isfinite(value.b) && std::isfinite(value.a);
}

bool valid_colors(std::span<const progpu_native_color> values) noexcept {
    const auto address = reinterpret_cast<std::uintptr_t>(values.data());
    if (values.size() > std::numeric_limits<std::uintptr_t>::max() / sizeof(values[0]) ||
        (!values.empty() && (values.data() == nullptr || address % alignof(progpu_native_color) != 0U)) ||
        values.size_bytes() > std::numeric_limits<std::uintptr_t>::max() - address) return false;
    for (const auto value : values) if (!finite(value)) return false;
    return true;
}

bool same_scalar(const unicode_scalar& a, const unicode_scalar& b) noexcept {
    return a.code_point == b.code_point && a.input_index == b.input_index && a.input_length == b.input_length &&
        a.canonical_combining_class == b.canonical_combining_class && a.reserved == b.reserved && a.script == b.script;
}

bool same_logical_glyph(const shaping_glyph& logical, const shaping_glyph& original,
    hinted_source_advance_policy policy) noexcept {
    shaping_glyph fitting{};
    if (!project_hinted_source_advance(original, policy, fitting)) return false;
    return original.advance_y != INT32_MIN && original.offset_y != INT32_MIN &&
        logical.glyph_id == original.glyph_id && logical.code_point == original.code_point &&
        logical.cluster == original.cluster && logical.flags == original.flags &&
        logical.advance_x == fitting.advance_x && logical.advance_y == -original.advance_y &&
        logical.offset_x == original.offset_x && logical.offset_y == -original.offset_y;
}

bool valid_paragraph(const hinted_paragraph_generation& paragraph,
    progpu_native_point logical_origin) noexcept {
    const auto logical_count = paragraph.logical_glyphs.size();
    const auto positioned_count = paragraph.glyphs.size();
    const auto scalar_count = paragraph.source_input.size();
    if (paragraph.layout.struct_size != sizeof(paragraph.layout) ||
        paragraph.layout.direction > PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT ||
        paragraph.layout.trimming != PROGPU_NATIVE_TEXT_TRIMMING_NONE ||
        (paragraph.paragraph_level != 0 && paragraph.paragraph_level != 1) ||
        paragraph.shaping.struct_size != sizeof(paragraph.shaping) || paragraph.shaping.input_count != scalar_count ||
        (scalar_count != 0U && paragraph.shaping.input != paragraph.source_input.data()) ||
        paragraph.shaping_input.size() != scalar_count || paragraph.scalar_levels.size() != scalar_count ||
        paragraph.line_break_classes.size() != scalar_count || paragraph.scalar_breaks.size() != scalar_count ||
        paragraph.source_metrics.size() != paragraph.styles.size() || paragraph.device_styles.size() != paragraph.styles.size() ||
        (!paragraph.source_styles.empty() && paragraph.source_styles.size() != paragraph.styles.size()) ||
        paragraph.logical_owners.size() != logical_count || paragraph.logical_bidi_levels.size() != logical_count ||
        paragraph.logical_cluster_ends.size() != logical_count || paragraph.logical_font_indices.size() != logical_count ||
        paragraph.logical_source_scales.size() != logical_count || paragraph.glyph_scales.size() != logical_count ||
        paragraph.breaks_after.size() != logical_count || paragraph.item_metrics.size() != logical_count ||
        (!paragraph.justification_classes.empty() && paragraph.justification_classes.size() != logical_count) ||
        paragraph.positioned_owners.size() != positioned_count || paragraph.bidi_levels.size() != positioned_count ||
        paragraph.cluster_ends.size() != positioned_count || paragraph.lines.size() != paragraph.line_origins.size()) return false;
    if (paragraph.has_source_geometry) {
        if (paragraph.source_styles.size() != paragraph.styles.size() ||
            paragraph.source_style_metrics.size() != paragraph.styles.size() ||
            paragraph.source_item_metrics.size() != logical_count || paragraph.source_logical_metrics.size() != logical_count ||
            paragraph.source_glyphs.size() != positioned_count || paragraph.source_lines.size() != paragraph.lines.size() ||
            !std::isfinite(paragraph.source_maximum_width) || paragraph.source_maximum_width < 0.0 ||
            !std::isfinite(paragraph.source_line_height) || paragraph.source_line_height < 0.0 ||
            static_cast<float>(paragraph.source_maximum_width) != paragraph.layout.maximum_width ||
            static_cast<float>(paragraph.source_line_height) != paragraph.layout.line_height ||
            paragraph.layout.alignment == PROGPU_NATIVE_TEXT_ALIGNMENT_JUSTIFY) return false;
    } else if (!paragraph.source_style_metrics.empty() || !paragraph.source_item_metrics.empty() ||
        !paragraph.source_logical_metrics.empty() || !paragraph.source_glyphs.empty() || !paragraph.source_lines.empty()) return false;
    std::uint64_t source_end = 0U;
    for (std::size_t i = 0U; i < scalar_count; ++i) {
        const auto& source = paragraph.source_input[i];
        const auto& admitted = paragraph.shaping_input[i];
        const auto& level = paragraph.scalar_levels[i];
        const auto end = static_cast<std::uint64_t>(source.input_index) + source.input_length;
        if (source.input_length == 0U || source.input_index < source_end || end > INT32_MAX ||
            admitted.input_index != source.input_index || admitted.input_length != source.input_length ||
            admitted.code_point == 9U || admitted.code_point == 0xFFFCU ||
            level.input_index != source.input_index || level.input_length != source.input_length ||
            level.level < 0 || level.level > 125) return false;
        source_end = end;
    }
    std::size_t style_end = 0U;
    for (std::size_t i = 0U; i < paragraph.styles.size(); ++i) {
        const auto& style = paragraph.styles[i];
        const auto& device = paragraph.device_styles[i];
        const auto metric = paragraph.source_metrics[i];
        if (style.scalar_start != style_end || style.scalar_count == 0U || style.scalar_count > scalar_count - style_end ||
            style.font_index >= paragraph.font_sources.size() || paragraph.font_sources[style.font_index] == nullptr ||
            device.font_index != style.font_index || device.source_scale != style.scale ||
            device.x_pixels_per_em_26_6 == 0U || device.y_pixels_per_em_26_6 == 0U ||
            device.x_pixels_per_em_26_6 > INT32_MAX || device.y_pixels_per_em_26_6 > INT32_MAX ||
            device.x_phase_26_6 >= 64U || device.y_phase_26_6 >= 64U ||
            (device.policy != font_hint_policy::truetype_35 && device.policy != font_hint_policy::truetype_40) ||
            !std::isfinite(style.scale) || style.scale <= 0.0F ||
            !std::isfinite(device.logical_units_per_physical_pixel) || device.logical_units_per_physical_pixel <= 0.0F ||
            !std::isfinite(device.logical_units_per_physical_pixel / 64.0F) || device.logical_units_per_physical_pixel / 64.0F <= 0.0F ||
            !std::isfinite(metric.ascent) || metric.ascent < 0.0F || !std::isfinite(metric.descent) || metric.descent < 0.0F ||
            !std::isfinite(metric.ascent + metric.descent)) return false;
        if (!paragraph.source_styles.empty()) {
            hinted_source_device_selection selected{};
            if (!resolve_hinted_source_device(paragraph.source_styles[i], selected) ||
                device.x_pixels_per_em_26_6 != selected.pixels_per_em_26_6 ||
                device.y_pixels_per_em_26_6 != selected.pixels_per_em_26_6 ||
                device.logical_units_per_physical_pixel != selected.logical_units_per_physical_pixel) return false;
        }
        if (paragraph.has_source_geometry) {
            const auto source = paragraph.source_style_metrics[i];
            if (!std::isfinite(source.ascent) || source.ascent < 0.0 ||
                !std::isfinite(source.descent) || source.descent < 0.0 ||
                static_cast<float>(source.ascent) != metric.ascent || static_cast<float>(source.descent) != metric.descent) return false;
        }
        style_end += style.scalar_count;
    }
    if (style_end != scalar_count) return false;
    std::size_t next_scalar = 0U, next_logical = 0U;
    for (std::size_t run_index = 0U; run_index < paragraph.runs.size(); ++run_index) {
        const auto& run = paragraph.runs[run_index];
        if (run.generation == nullptr || run.generation->batch == nullptr || run.generation->batch->identity == nullptr ||
            run.scalar_start != next_scalar || run.scalar_count == 0U || run.scalar_count > scalar_count - next_scalar ||
            run.logical_start != next_logical || run.logical_count > logical_count - next_logical ||
            run.logical_count != run.generation->glyphs.size() || run.style_index >= paragraph.styles.size() ||
            run.font_index >= paragraph.font_sources.size() || run.generation->shaping_input.size() != run.scalar_count ||
            run.generation->normalized_coordinates != paragraph.normalized_coordinates ||
            run.generation->descriptor_indices.size() != run.generation->glyphs.size()) return false;
        const auto& style = paragraph.styles[run.style_index];
        const auto& device = paragraph.device_styles[run.style_index];
        const auto& identity = *run.generation->batch->identity;
        const auto style_scalar_end = static_cast<std::uint64_t>(style.scalar_start) + style.scalar_count;
        if (run.font_index != style.font_index || run.source_scale != style.scale ||
            run.scalar_start < style.scalar_start || run.scalar_start > style_scalar_end ||
            run.scalar_count > style_scalar_end - run.scalar_start ||
            run.logical_units_per_physical_pixel != device.logical_units_per_physical_pixel ||
            identity.source != paragraph.font_sources[run.font_index] || identity.source == nullptr || identity.source->bytes.empty() ||
            identity.x_pixels_per_em_26_6 != device.x_pixels_per_em_26_6 || identity.y_pixels_per_em_26_6 != device.y_pixels_per_em_26_6 ||
            identity.policy != device.policy || identity.x_phase_26_6 != device.x_phase_26_6 || identity.y_phase_26_6 != device.y_phase_26_6 ||
            identity.variation_coordinates_16_16 != device.variation_coordinates_16_16 || run.bidi_level < 0 || run.bidi_level > 125 ||
            run.generation->direction != ((run.bidi_level & 1) == 0 ? shaping_direction::left_to_right : shaping_direction::right_to_left)) return false;
        if (!paragraph.source_styles.empty() && (identity.device_frame.units_per_em == 0U ||
            static_cast<float>(paragraph.source_styles[run.style_index].em_size) /
                static_cast<float>(identity.device_frame.units_per_em) != run.source_scale)) return false;
        for (std::size_t i = 0U; i < run.scalar_count; ++i)
            if (!same_scalar(run.generation->shaping_input[i], paragraph.shaping_input[next_scalar + i]) ||
                paragraph.scalar_levels[next_scalar + i].level != run.bidi_level) return false;
        // The exact original cluster-group restoration, not ID lookup/sorting.
        std::size_t logical = next_logical;
        const auto group = [&](std::size_t first, std::size_t last) noexcept {
            for (std::size_t original = first; original < last; ++original, ++logical) {
                const auto descriptor = run.generation->descriptor_indices[original];
                const auto& glyph = paragraph.logical_glyphs[logical];
                const auto metric = paragraph.item_metrics[logical];
                const auto source_metric = paragraph.source_metrics[run.style_index];
                if (paragraph.logical_owners[logical] != hinted_paragraph_glyph_owner{static_cast<std::uint32_t>(run_index),
                        static_cast<std::uint32_t>(original), descriptor} ||
                    descriptor >= run.generation->source_descriptor_count || descriptor >= run.generation->batch->glyphs.size() ||
                    run.generation->batch->glyphs[descriptor].glyph_index != glyph.glyph_id ||
                    !same_logical_glyph(glyph, run.generation->glyphs[original], paragraph.source_styles.empty()
                        ? hinted_source_advance_policy::unchanged : paragraph.source_styles[run.style_index].advance_policy) || glyph.cluster < 0 ||
                    paragraph.logical_cluster_ends[logical] <= glyph.cluster ||
                    paragraph.logical_bidi_levels[logical] != run.bidi_level || paragraph.logical_font_indices[logical] != run.font_index ||
                    paragraph.logical_source_scales[logical] != run.source_scale ||
                    paragraph.glyph_scales[logical] != run.logical_units_per_physical_pixel / 64.0F ||
                    metric.ascent != source_metric.ascent || metric.descent != source_metric.descent ||
                    static_cast<std::uint8_t>(paragraph.breaks_after[logical]) > static_cast<std::uint8_t>(text_line_break_kind::mandatory) ||
                    (!paragraph.justification_classes.empty() && static_cast<std::uint8_t>(paragraph.justification_classes[logical]) >
                        static_cast<std::uint8_t>(text_justification_class::word_space))) return false;
                if (paragraph.has_source_geometry) {
                    const auto projected = paragraph.source_logical_metrics[logical];
                    const auto source_item = paragraph.source_item_metrics[logical];
                    const auto source_style = paragraph.source_style_metrics[run.style_index];
                    const double dpi = paragraph.source_styles[run.style_index].pixels_per_dip;
                    if (projected.advance_x != (static_cast<double>(glyph.advance_x) / 64.0) / dpi ||
                        projected.advance_y != (static_cast<double>(glyph.advance_y) / 64.0) / dpi ||
                        projected.offset_x != (static_cast<double>(glyph.offset_x) / 64.0) / dpi ||
                        projected.offset_y != (static_cast<double>(glyph.offset_y) / 64.0) / dpi ||
                        source_item.ascent != source_style.ascent || source_item.descent != source_style.descent) return false;
                }
            }
            return true;
        };
        if ((run.bidi_level & 1) == 0) { if (!group(0U, run.generation->glyphs.size())) return false; }
        else {
            std::size_t last = run.generation->glyphs.size();
            while (last != 0U) {
                std::size_t first = last - 1U;
                while (first != 0U && run.generation->glyphs[first - 1U].cluster == run.generation->glyphs[last - 1U].cluster) --first;
                if (!group(first, last)) return false;
                last = first;
            }
        }
        next_scalar += run.scalar_count; next_logical += run.logical_count;
    }
    if (next_scalar != scalar_count || next_logical != logical_count) return false;
    for (std::size_t i = 1U; i < logical_count; ++i) {
        const auto previous = paragraph.logical_glyphs[i - 1U].cluster, current = paragraph.logical_glyphs[i].cluster;
        if (current < previous || (current > previous && paragraph.logical_cluster_ends[i - 1U] > current) ||
            (current == previous && paragraph.logical_cluster_ends[i] != paragraph.logical_cluster_ends[i - 1U])) return false;
    }
    for (std::size_t i = 0U; i < positioned_count; ++i) {
        const auto& glyph = paragraph.glyphs[i];
        const auto logical = glyph.glyph_index;
        if (logical >= logical_count || paragraph.positioned_owners[i] != paragraph.logical_owners[logical] ||
            glyph.glyph_id != paragraph.logical_glyphs[logical].glyph_id || glyph.cluster != paragraph.logical_glyphs[logical].cluster ||
            paragraph.cluster_ends[i] != paragraph.logical_cluster_ends[logical] || paragraph.bidi_levels[i] < 0 || paragraph.bidi_levels[i] > 125 ||
            !std::isfinite(glyph.x) || !std::isfinite(glyph.y) || !std::isfinite(glyph.advance_x) || !std::isfinite(glyph.advance_y) ||
            !finite(progpu_native_point{glyph.x + glyph.advance_x, glyph.y + glyph.advance_y}) ||
            !finite(progpu_native_point{glyph.x + logical_origin.x, glyph.y + logical_origin.y})) return false;
        if (paragraph.has_source_geometry) {
            const auto source = paragraph.source_glyphs[i];
            const auto original = paragraph.source_logical_metrics[logical];
            if (!std::isfinite(source.x) || !std::isfinite(source.y) ||
                source.cluster != glyph.cluster ||
                source.advance_x != original.advance_x || source.advance_y != original.advance_y ||
                static_cast<float>(source.x) != glyph.x || static_cast<float>(source.y) != glyph.y ||
                static_cast<float>(source.advance_x) != glyph.advance_x || static_cast<float>(source.advance_y) != glyph.advance_y)
                return false;
        }
    }
    std::size_t covered = 0U;
    for (std::size_t i = 0U; i < paragraph.lines.size(); ++i) {
        const auto& line = paragraph.lines[i];
        if (line.glyph_start != covered || line.glyph_count == 0U || line.glyph_count > positioned_count - covered ||
            !std::isfinite(line.width) || line.width < 0.0F || !std::isfinite(line.baseline_y) ||
            !std::isfinite(line.height) || line.height < 0.0F || !std::isfinite(paragraph.line_origins[i]) ||
            (line.flags & ~static_cast<std::uint8_t>(positioned_text_line_flags::right_to_left_justified)) != 0U ||
            line.reserved1 != 0U || line.reserved2 != 0U) return false;
        if (paragraph.has_source_geometry) {
            const auto source = paragraph.source_lines[i];
            if (!std::isfinite(source.width) || source.width < 0.0 || !std::isfinite(source.top) || source.top < 0.0 ||
                source.glyph_start != line.glyph_start || source.glyph_count != line.glyph_count ||
                !std::isfinite(source.height) || source.height < 0.0 || !std::isfinite(source.baseline_offset) || source.baseline_offset < 0.0 ||
                source.baseline_y != source.top + source.baseline_offset || !std::isfinite(source.origin_x) ||
                static_cast<float>(source.width) != line.width || static_cast<float>(source.baseline_y) != line.baseline_y ||
                static_cast<float>(source.height) != line.height || static_cast<float>(source.origin_x) != paragraph.line_origins[i]) return false;
        }
        covered += line.glyph_count;
    }
    return covered == positioned_count;
}

bool add_bounded(std::size_t& total, std::size_t count, std::size_t maximum) noexcept {
    if (count > maximum - total) return false;
    total += count; return true;
}

progpu_native_status outline_status(hinted_outline_error error) noexcept {
    return error == hinted_outline_error::unsupported_flags || error == hinted_outline_error::unsupported_coordinates ||
        error == hinted_outline_error::unsupported_policy ? PROGPU_NATIVE_STATUS_UNSUPPORTED : PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
}
} // namespace

bool hinted_paragraph_glyph_resource::allocation_aliases(const void* output, std::size_t bytes) const noexcept {
    const owned_output_range range{output, bytes};
    return range.overlaps(this, sizeof(*this)) || range.overlaps(outlines_) || range.overlaps(segments_) ||
        range.overlaps(run_slices_) || range.overlaps(source_outline_indices_) ||
        range.overlaps(run_outline_indices_) || range.overlaps(outline_owners_) ||
        range.overlaps(positioned_outline_indices_) || range.overlaps(positioned_owners_) ||
        range.overlaps(binding_fonts_) || range.overlaps(binding_font_bytes_) ||
        range.overlaps(binding_runs_) || range.overlaps(binding_glyphs_) || imported_allocation_aliases(output, bytes);
}

bool hinted_paragraph_glyph_frame::allocation_aliases(const void* output, std::size_t bytes) const noexcept {
    const owned_output_range range{output, bytes};
    return range.overlaps(this, sizeof(*this)) || range.overlaps(style_colors_) ||
        range.overlaps(glyphs_) || range.overlaps(draw_owners_) || resource_->allocation_aliases(output, bytes);
}

progpu_native_glyph_frame hinted_paragraph_glyph_frame::frame() const noexcept {
    progpu_native_glyph_frame result{};
    result.struct_size = sizeof(result); result.width = target_.width; result.height = target_.height;
    result.dpi_scale = target_.dpi_scale; result.target_view = target_.target_view; result.clear_color = target_.clear_color;
    result.outlines = outlines().data(); result.outline_count = outlines().size();
    result.segments = segments().data(); result.segment_count = segments().size();
    result.glyphs = glyphs_.data(); result.glyph_count = glyphs_.size();
    return result; // No synthetic draw state, revision, transforms or policy bits.
}

hinted_paragraph_glyph_resource_result create_hinted_paragraph_glyph_resource(
    std::shared_ptr<const hinted_paragraph_generation> paragraph, float dpi_scale,
    hinted_projection_policy policy, hinted_outline_coverage coverage) noexcept {
    hinted_paragraph_glyph_resource_result result{};
    const auto fail = [&](hinted_glyph_frame_error_code code, progpu_native_status status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        hinted_outline_error outline = hinted_outline_error::none) noexcept {
        result.status = status; result.error = {code, outline}; return result;
    };
    try {
        if (paragraph == nullptr || !std::isfinite(dpi_scale) || dpi_scale <= 0.0F)
            return fail(hinted_glyph_frame_error_code::invalid_argument);
        if ((coverage != hinted_outline_coverage::strict && coverage != hinted_outline_coverage::nonzero_vector &&
                coverage != hinted_outline_coverage::antialiased_vector) ||
            (policy != hinted_projection_policy::automatic && policy != hinted_projection_policy::intrinsic_simd &&
                policy != hinted_projection_policy::scalar_reference))
            return fail(hinted_glyph_frame_error_code::outline_conversion_failed, PROGPU_NATIVE_STATUS_UNSUPPORTED,
                hinted_outline_error::unsupported_policy);
        const float units = 1.0F / dpi_scale;
        if (!std::isfinite(units) || units <= 0.0F)
            return fail(hinted_glyph_frame_error_code::unsupported_mapping, PROGPU_NATIVE_STATUS_UNSUPPORTED);
        if (paragraph->runs.size() > maximum_slots || paragraph->logical_glyphs.size() > maximum_slots ||
            paragraph->glyphs.size() > maximum_slots || paragraph->source_input.size() > maximum_slots)
            return fail(hinted_glyph_frame_error_code::insufficient_capacity);
        if (!valid_paragraph(*paragraph, {})) return fail(hinted_glyph_frame_error_code::invalid_layout);
        std::vector<hinted_outline_requirements> requirements(paragraph->runs.size());
        std::size_t source_count = 0U, run_count = 0U, outline_count = 0U, segment_count = 0U, scratch_count = 0U;
        for (std::size_t i = 0U; i < paragraph->runs.size(); ++i) {
            const auto error = get_hinted_outline_requirements(*paragraph->runs[i].generation, requirements[i], policy, coverage);
            if (error != hinted_outline_error::none) return fail(hinted_glyph_frame_error_code::outline_conversion_failed, outline_status(error), error);
            const auto& needed = requirements[i];
            if (!add_bounded(source_count, needed.source_slots, maximum_slots) || !add_bounded(run_count, needed.positioned_slots, maximum_slots) ||
                !add_bounded(outline_count, needed.outlines, maximum_outlines) || !add_bounded(segment_count, needed.segments, maximum_segments))
                return fail(hinted_glyph_frame_error_code::insufficient_capacity);
            scratch_count = std::max(scratch_count, needed.scratch_points);
        }
        auto candidate = std::shared_ptr<hinted_paragraph_glyph_resource>(new hinted_paragraph_glyph_resource{});
        candidate->paragraph_ = std::move(paragraph); candidate->dpi_scale_ = dpi_scale;
        candidate->coverage_ = coverage; candidate->projection_policy_ = policy;
        candidate->binding_fonts_.reserve(candidate->paragraph_->font_sources.size());
        candidate->binding_runs_.reserve(candidate->paragraph_->runs.size());
        candidate->binding_glyphs_.reserve(candidate->paragraph_->glyphs.size());
        for (const auto& source : candidate->paragraph_->font_sources) {
            sfnt_font_view font{}; sfnt_header_metrics metrics{};
            if (source == nullptr || !sfnt_font_view::try_create(source->bytes, source->face_index, font) ||
                !font.try_get_header_metrics(metrics) || metrics.units_per_em == 0U ||
                source->bytes.size() > UINT32_MAX - candidate->binding_font_bytes_.size())
                return fail(hinted_glyph_frame_error_code::invalid_layout);
            candidate->binding_fonts_.push_back({static_cast<std::uint32_t>(candidate->binding_font_bytes_.size()),
                static_cast<std::uint32_t>(source->bytes.size()), source->face_index, metrics.units_per_em});
            const auto start = candidate->binding_font_bytes_.size();
            candidate->binding_font_bytes_.resize(start + source->bytes.size());
            // libc copies the exact immutable byte span using its existing
            // bulk path; no per-byte vector growth or font-table conversion.
            std::memcpy(candidate->binding_font_bytes_.data() + start, source->bytes.data(), source->bytes.size());
        }
        for (const auto& run : candidate->paragraph_->runs)
            candidate->binding_runs_.push_back({run.scalar_start, run.scalar_count, run.logical_start, run.logical_count,
                run.font_index, run.style_index, run.bidi_level, run.source_scale, run.logical_units_per_physical_pixel,
                static_cast<std::uint32_t>(run.generation->source_descriptor_count)});
        for (const auto& glyph : candidate->paragraph_->glyphs)
            candidate->binding_glyphs_.push_back({glyph.glyph_index, glyph.glyph_id,
                candidate->paragraph_->logical_font_indices[glyph.glyph_index], glyph.cluster,
                glyph.x, glyph.y, glyph.advance_x, glyph.advance_y});
        candidate->outlines_.resize(outline_count); candidate->segments_.resize(segment_count);
        candidate->source_outline_indices_.resize(source_count); candidate->run_outline_indices_.resize(run_count);
        candidate->outline_owners_.resize(outline_count); candidate->run_slices_.reserve(requirements.size());
        std::vector<sfnt_outline_point> topology(scratch_count);
        std::vector<progpu_native_point> physical(scratch_count);
        std::size_t source_start = 0U, run_start = 0U, outline_start = 0U, segment_start = 0U;
        for (std::size_t i = 0U; i < requirements.size(); ++i) {
            const auto& needed = requirements[i];
            hinted_outline_requirements written{};
            const auto error = write_hinted_run_outlines(*candidate->paragraph_->runs[i].generation, {topology, physical},
                std::span(candidate->outlines_).subspan(outline_start, needed.outlines),
                std::span(candidate->segments_).subspan(segment_start, needed.segments),
                std::span(candidate->source_outline_indices_).subspan(source_start, needed.source_slots),
                std::span(candidate->run_outline_indices_).subspan(run_start, needed.positioned_slots), written, policy, coverage);
            if (error != hinted_outline_error::none) return fail(hinted_glyph_frame_error_code::outline_conversion_failed, outline_status(error), error);
            if (written != needed) return fail(hinted_glyph_frame_error_code::outline_conversion_failed, PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                hinted_outline_error::invalid_run);
            for (std::size_t j = 0U; j < needed.outlines; ++j) candidate->outlines_[outline_start + j].segment_offset += segment_start;
            for (std::size_t j = 0U; j < needed.source_slots; ++j) {
                auto& outline = candidate->source_outline_indices_[source_start + j];
                if (outline == hinted_no_outline) continue;
                if (outline >= needed.outlines) return fail(hinted_glyph_frame_error_code::invalid_layout);
                outline += static_cast<std::uint32_t>(outline_start);
                candidate->outline_owners_[outline] = {static_cast<std::uint32_t>(i), static_cast<std::uint32_t>(j)};
            }
            for (std::size_t j = 0U; j < needed.positioned_slots; ++j) {
                auto& outline = candidate->run_outline_indices_[run_start + j];
                if (outline != hinted_no_outline) {
                    if (outline >= needed.outlines) return fail(hinted_glyph_frame_error_code::invalid_layout);
                    outline += static_cast<std::uint32_t>(outline_start);
                }
            }
            candidate->run_slices_.push_back({static_cast<std::uint32_t>(source_start), static_cast<std::uint32_t>(needed.source_slots),
                static_cast<std::uint32_t>(run_start), static_cast<std::uint32_t>(needed.positioned_slots),
                static_cast<std::uint32_t>(outline_start), static_cast<std::uint32_t>(needed.outlines),
                static_cast<std::uint32_t>(segment_start), static_cast<std::uint32_t>(needed.segments)});
            source_start += needed.source_slots; run_start += needed.positioned_slots;
            outline_start += needed.outlines; segment_start += needed.segments;
        }
        candidate->positioned_outline_indices_.reserve(candidate->paragraph_->glyphs.size());
        candidate->positioned_owners_.reserve(candidate->paragraph_->glyphs.size());
        for (std::size_t i = 0U; i < candidate->paragraph_->glyphs.size(); ++i) {
            const auto& glyph = candidate->paragraph_->glyphs[i];
            const auto owner = candidate->paragraph_->positioned_owners[i];
            const auto& run = candidate->paragraph_->runs[owner.run_index];
            const auto& slice = candidate->run_slices_[owner.run_index];
            const auto outline = candidate->source_outline_indices_[slice.source_start + owner.descriptor_index];
            if (run.logical_units_per_physical_pixel != units || run.logical_units_per_physical_pixel * dpi_scale != 1.0F)
                return fail(hinted_glyph_frame_error_code::unsupported_mapping, PROGPU_NATIVE_STATUS_UNSUPPORTED);
            if (candidate->run_outline_indices_[slice.run_start + owner.run_glyph_index] != outline)
                return fail(hinted_glyph_frame_error_code::invalid_layout);
            candidate->positioned_outline_indices_.push_back(outline);
            candidate->positioned_owners_.push_back({static_cast<std::uint32_t>(i), glyph.glyph_index,
                owner.run_index, owner.run_glyph_index, owner.descriptor_index, run.font_index, run.style_index});
        }
        result.status = PROGPU_NATIVE_STATUS_SUCCESS; result.error = {}; result.generation = std::move(candidate);
    } catch (const std::bad_alloc&) {
        return fail(hinted_glyph_frame_error_code::resource_exhausted, PROGPU_NATIVE_STATUS_OUT_OF_MEMORY);
    } catch (...) {
        return fail(hinted_glyph_frame_error_code::invalid_argument, PROGPU_NATIVE_STATUS_INTERNAL_ERROR);
    }
    return result;
}

hinted_paragraph_glyph_frame_result create_hinted_paragraph_glyph_frame(
    std::shared_ptr<const hinted_paragraph_generation> paragraph,
    hinted_paragraph_glyph_target target, std::span<const progpu_native_color> style_colors,
    hinted_projection_policy policy, hinted_outline_coverage coverage) noexcept {
    hinted_paragraph_glyph_frame_result result{};
    const auto fail = [&](hinted_glyph_frame_error_code code, progpu_native_status status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT,
        hinted_outline_error outline = hinted_outline_error::none) noexcept {
        result.status = status; result.error = {code, outline}; return result;
    };
    try {
        // Preserve the original frame's target/color, policy, projection and
        // layout validation order before acquiring the reusable geometry owner.
        if (paragraph == nullptr || style_colors.size() != paragraph->styles.size() || !valid_colors(style_colors) ||
            target.width == 0U || target.height == 0U || target.target_view == 0U ||
            !std::isfinite(target.dpi_scale) || target.dpi_scale <= 0.0F || !finite(target.logical_origin) || !finite(target.clear_color))
            return fail(hinted_glyph_frame_error_code::invalid_argument);
        if ((coverage != hinted_outline_coverage::strict && coverage != hinted_outline_coverage::nonzero_vector &&
                coverage != hinted_outline_coverage::antialiased_vector) ||
            (policy != hinted_projection_policy::automatic && policy != hinted_projection_policy::intrinsic_simd &&
                policy != hinted_projection_policy::scalar_reference))
            return fail(hinted_glyph_frame_error_code::outline_conversion_failed, PROGPU_NATIVE_STATUS_UNSUPPORTED,
                hinted_outline_error::unsupported_policy);
        const float units = 1.0F / target.dpi_scale;
        const float width = static_cast<float>(target.width) / target.dpi_scale, height = static_cast<float>(target.height) / target.dpi_scale;
        if (!std::isfinite(units) || units <= 0.0F || !std::isfinite(width) || width <= 0.0F ||
            !std::isfinite(height) || height <= 0.0F || !std::isfinite(2.0F / width) || !std::isfinite(2.0F / height))
            return fail(hinted_glyph_frame_error_code::unsupported_mapping, PROGPU_NATIVE_STATUS_UNSUPPORTED);
        if (paragraph->runs.size() > maximum_slots || paragraph->logical_glyphs.size() > maximum_slots ||
            paragraph->glyphs.size() > maximum_slots || paragraph->source_input.size() > maximum_slots)
            return fail(hinted_glyph_frame_error_code::insufficient_capacity);
        // The resource validates the full original generation once. Check only
        // the frame-dependent additions here, before any outline conversion,
        // retaining the original invalid-layout failure for origin overflow.
        for (const auto& glyph : paragraph->glyphs)
            if (!finite(progpu_native_point{glyph.x + target.logical_origin.x, glyph.y + target.logical_origin.y}))
                return fail(hinted_glyph_frame_error_code::invalid_layout);
        const auto geometry = create_hinted_paragraph_glyph_resource(std::move(paragraph), target.dpi_scale, policy, coverage);
        if (geometry.status != PROGPU_NATIVE_STATUS_SUCCESS) {
            result.status = geometry.status; result.error = geometry.error; return result;
        }
        auto candidate = std::shared_ptr<hinted_paragraph_glyph_frame>(new hinted_paragraph_glyph_frame{});
        candidate->resource_ = geometry.generation; candidate->target_ = target;
        candidate->style_colors_.assign(style_colors.begin(), style_colors.end());
        const auto& original = *candidate->paragraph();
        candidate->glyphs_.reserve(original.glyphs.size()); candidate->draw_owners_.reserve(original.glyphs.size());
        for (std::size_t i = 0U; i < original.glyphs.size(); ++i) {
            const auto outline = candidate->resource_->positioned_outline_indices()[i];
            if (outline == hinted_no_outline) continue;
            const auto& glyph = original.glyphs[i];
            const auto owner = candidate->resource_->positioned_owners()[i];
            candidate->glyphs_.push_back({outline, 0U, {glyph.x + target.logical_origin.x, glyph.y + target.logical_origin.y},
                {1.0F, 0.0F}, {0.0F, 1.0F}, candidate->style_colors_[owner.style_index], 1.0F, 0.0F, 0.0F, 0.0F});
            candidate->draw_owners_.push_back(owner);
        }
        result.status = PROGPU_NATIVE_STATUS_SUCCESS; result.error = {}; result.generation = std::move(candidate);
    } catch (const std::bad_alloc&) {
        return fail(hinted_glyph_frame_error_code::resource_exhausted, PROGPU_NATIVE_STATUS_OUT_OF_MEMORY);
    } catch (...) {
        return fail(hinted_glyph_frame_error_code::invalid_argument, PROGPU_NATIVE_STATUS_INTERNAL_ERROR);
    }
    return result;
}

} // namespace progpu::native::text
