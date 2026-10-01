#include "progpu_native_hinted_paragraph_glyph_frame.hpp"
#include "progpu_native_owned_allocation_internal.hpp"
#include "../progpu_native_text_interaction_impl.hpp"

#include <algorithm>
#include <cmath>
#include <limits>
#include <new>

namespace progpu::native::text {

// No producer pointers or native driver objects survive this synchronous copy.
// Geometry lives in the resource's existing vectors; these are the complete
// original source, device, formatting and interaction records behind it.
struct owned_hinted_glyph_import final {
    progpu_native_hinted_glyph_resource_view metadata{};
    std::vector<progpu_native_hinted_glyph_font_source> font_sources;
    std::vector<std::uint8_t> font_bytes;
    std::vector<progpu_native_hinted_paragraph_device_style> device_styles;
    std::vector<std::int32_t> variation_coordinates_16_16;
    std::vector<std::int16_t> normalized_coordinates;
    std::vector<progpu_native_text_scalar> source_scalars, admitted_scalars, pre_context, post_context;
    std::vector<progpu_native_text_bidi_level> scalar_levels;
    std::vector<progpu_native_text_style_run> styles;
    std::vector<progpu_native_text_style_metrics> source_metrics;
    std::vector<progpu_native_hinted_paragraph_run> runs;
    std::vector<progpu_native_text_shaping_glyph> logical_glyphs;
    std::vector<progpu_native_hinted_paragraph_glyph_owner> logical_owners, positioned_owners;
    std::vector<std::int32_t> logical_cluster_ends, positioned_cluster_ends;
    std::vector<std::int8_t> logical_bidi_levels, positioned_bidi_levels;
    std::vector<float> glyph_scales, line_origins;
    std::vector<progpu_native_positioned_text_glyph> positioned_glyphs;
    std::vector<progpu_native_positioned_text_line> lines;
    std::vector<progpu_native_text_cluster_box> boxes;
    std::vector<progpu_native_text_caret_stop> carets;
    std::vector<progpu_native_text_feature> features;

    bool allocation_aliases(const void* output, std::size_t bytes) const noexcept {
        const owned_output_range r{output, bytes};
        return r.overlaps(this, sizeof(*this)) || r.overlaps(font_sources) || r.overlaps(font_bytes) ||
            r.overlaps(device_styles) || r.overlaps(variation_coordinates_16_16) || r.overlaps(normalized_coordinates) ||
            r.overlaps(source_scalars) || r.overlaps(admitted_scalars) || r.overlaps(pre_context) || r.overlaps(post_context) ||
            r.overlaps(scalar_levels) || r.overlaps(styles) || r.overlaps(source_metrics) || r.overlaps(runs) ||
            r.overlaps(logical_glyphs) || r.overlaps(logical_owners) || r.overlaps(positioned_owners) ||
            r.overlaps(logical_cluster_ends) || r.overlaps(positioned_cluster_ends) || r.overlaps(logical_bidi_levels) ||
            r.overlaps(positioned_bidi_levels) || r.overlaps(glyph_scales) || r.overlaps(line_origins) ||
            r.overlaps(positioned_glyphs) || r.overlaps(lines) || r.overlaps(boxes) || r.overlaps(carets) || r.overlaps(features);
    }
};

hinted_glyph_binding_view hinted_paragraph_glyph_resource::binding_view() const noexcept {
    if (imported_ != nullptr)
        return {imported_->font_sources, imported_->font_bytes, imported_->runs, imported_->positioned_glyphs};
    return {binding_fonts_, binding_font_bytes_, binding_runs_, binding_glyphs_};
}

bool hinted_paragraph_glyph_resource::imported_allocation_aliases(const void* output, std::size_t bytes) const noexcept {
    return imported_ != nullptr && imported_->allocation_aliases(output, bytes);
}

namespace {
constexpr std::uint32_t maximum_slots = 1U << 24U;
constexpr std::uint32_t maximum_outlines = 1U << 20U;
constexpr std::uint32_t maximum_font_bytes = 256U * 1024U * 1024U;

template<class T> bool readable(const T* data, std::uint32_t count, std::uint32_t limit = maximum_slots) noexcept {
    const auto address = reinterpret_cast<std::uintptr_t>(data);
    return count <= limit && (count == 0U || (data != nullptr && address % alignof(T) == 0U)) &&
        count <= (std::numeric_limits<std::uintptr_t>::max() - address) / sizeof(T);
}
template<class T> void copy(std::vector<T>& to, const T* from, std::uint32_t count) {
    if (count != 0U) to.assign(from, from + count);
}
bool scalar(std::uint32_t value) noexcept { return value <= 0x10FFFFU && (value < 0xD800U || value > 0xDFFFU); }
bool feature_tag(std::uint32_t value) noexcept {
    // Exact feature admission from text_shaping_interop valid_request/valid_tag:
    // zero is not a feature tag; all four tag bytes must be printable ASCII.
    if (value == 0U) return false;
    for (std::uint32_t shift = 0U; shift <= 24U; shift += 8U) {
        const auto character = static_cast<std::uint8_t>(value >> shift);
        if (character < 0x20U || character > 0x7EU) return false;
    }
    return true;
}
bool finite(progpu_native_point p) noexcept { return std::isfinite(p.x) && std::isfinite(p.y); }
bool valid_segment(const progpu_native_path_segment& s) noexcept {
    return s.kind <= PROGPU_NATIVE_PATH_SEGMENT_CUBIC && s.pad0 == 0U && s.pad1 == 0U && s.pad2 == 0U &&
        finite(s.p0) && finite(s.p1) && finite(s.p2) && finite(s.p3);
}
bool same(progpu_native_point a, progpu_native_point b) noexcept { return a.x == b.x && a.y == b.y; }
bool same(progpu_native_hinted_paragraph_glyph_owner a, progpu_native_hinted_paragraph_glyph_owner b) noexcept {
    return a.run_index == b.run_index && a.run_glyph_index == b.run_glyph_index && a.descriptor_index == b.descriptor_index;
}
bool valid_scalar_records(std::span<const progpu_native_text_scalar> input) noexcept {
    std::uint64_t end = 0U;
    for (const auto& s : input) {
        if (!scalar(s.code_point) || s.reserved != 0U || s.input_length == 0U || s.input_index < end ||
            static_cast<std::uint64_t>(s.input_index) + s.input_length > INT32_MAX) return false;
        end = static_cast<std::uint64_t>(s.input_index) + s.input_length;
    }
    return true;
}
bool valid_digit(std::uint32_t policy) noexcept {
    if (policy == 0U) return true;
    if ((policy & ~(PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_SCALAR_MASK |
            PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_CONTEXTUAL | PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_SOURCE_BIDI)) != 0U) return false;
    const auto zero = policy & PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_SCALAR_MASK;
    if (zero == 0U || zero > 0x10FFF6U) return false;
    for (std::uint32_t i = 0U; i < 10U; ++i)
        if (get_unicode_decimal_digit_value(zero + i) != static_cast<std::int8_t>(i)) return false;
    return true;
}
bool finite_extent(float x, float y, float width, float height) noexcept {
    return std::isfinite(x) && std::isfinite(y) && std::isfinite(width) && width >= 0.0F &&
        std::isfinite(height) && height >= 0.0F && std::isfinite(x + width) && std::isfinite(y + height);
}

bool valid_records(const progpu_native_hinted_glyph_resource_view& v) {
    const auto& c = v.counts;
    const auto& l = v.layout;
    if (v.abi_version != PROGPU_NATIVE_ABI_VERSION || v.struct_size != sizeof(v) ||
        !std::isfinite(v.dpi_scale) || v.dpi_scale <= 0.0F || !std::isfinite(1.0F / v.dpi_scale) ||
        (v.projection_policy != PROGPU_NATIVE_HINTED_PROJECTION_AUTOMATIC &&
         v.projection_policy != PROGPU_NATIVE_HINTED_PROJECTION_INTRINSIC_SIMD &&
         v.projection_policy != PROGPU_NATIVE_HINTED_PROJECTION_SCALAR_REFERENCE) ||
        (v.coverage != PROGPU_NATIVE_HINTED_COVERAGE_STRICT && v.coverage != PROGPU_NATIVE_HINTED_COVERAGE_NONZERO_VECTOR &&
         v.coverage != PROGPU_NATIVE_HINTED_COVERAGE_ANTIALIASED_VECTOR) ||
        v.source_digit_bidi > 1U || (v.paragraph_level != 0 && v.paragraph_level != 1) ||
        v.shaping_direction > PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT ||
        (v.shaping_flags & ~PROGPU_NATIVE_TEXT_SHAPE_ZERO_MARK_ADVANCES) != 0U ||
        c.admitted_scalar_count != c.source_scalar_count || l.struct_size != sizeof(l) ||
        !std::isfinite(l.scale) || l.scale <= 0.0F || !std::isfinite(l.maximum_width) || l.maximum_width < 0.0F ||
        !std::isfinite(l.line_height) || l.line_height < 0.0F || l.direction > PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT ||
        l.trimming != PROGPU_NATIVE_TEXT_TRIMMING_NONE || l.alignment > PROGPU_NATIVE_TEXT_ALIGNMENT_JUSTIFY ||
        !std::isfinite(l.ellipsis_advance) || l.ellipsis_advance < 0.0F || l.reserved0 != 0U || l.reserved1 != 0U ||
        v.result.struct_size != sizeof(v.result) || v.result.error_code != 0U || v.result.error_stage != 0U ||
        v.result.glyph_count != c.positioned_glyph_count || v.result.shaped_glyph_count != c.logical_glyph_count ||
        v.result.line_count != c.line_count || v.result.paragraph_level != v.paragraph_level ||
        !finite_extent(0.0F, 0.0F, v.result.content_width, v.result.content_height) ||
        !finite_extent(0.0F, 0.0F, v.result.measured_width, v.result.measured_height)) return false;
    // Every span is checked before any record is read. Unlike writable copy
    // capacities these are immutable readable counts; no caller tail is touched.
#define CHECK(field, count) if (!readable(v.field, (count))) return false
    if (!readable(v.font_sources, v.font_source_count, maximum_outlines) ||
        v.font_source_count > v.font_byte_count / 12U) return false;
    if (!readable(v.font_bytes, v.font_byte_count, maximum_font_bytes)) return false;
    if (v.font_byte_count > maximum_font_bytes || v.outline_count > maximum_outlines) return false;
    CHECK(device_styles, c.style_count); CHECK(variation_coordinates_16_16, v.variation_coordinate_count);
    CHECK(normalized_coordinates, v.normalized_coordinate_count); CHECK(outlines, v.outline_count);
    CHECK(segments, v.segment_count); CHECK(run_slices, c.run_count); CHECK(source_outline_indices, v.source_outline_count);
    CHECK(run_outline_indices, v.run_outline_count); CHECK(outline_owners, v.outline_count);
    CHECK(positioned_outline_indices, c.positioned_glyph_count); CHECK(source_scalars, c.source_scalar_count);
    CHECK(admitted_scalars, c.admitted_scalar_count); CHECK(scalar_levels, c.source_scalar_count); CHECK(styles, c.style_count);
    CHECK(source_metrics, c.style_count); CHECK(runs, c.run_count); CHECK(logical_glyphs, c.logical_glyph_count);
    CHECK(logical_owners, c.logical_glyph_count); CHECK(logical_cluster_ends, c.logical_glyph_count);
    CHECK(logical_bidi_levels, c.logical_glyph_count); CHECK(glyph_scales, c.logical_glyph_count);
    CHECK(positioned_glyphs, c.positioned_glyph_count); CHECK(positioned_owners, c.positioned_glyph_count);
    CHECK(positioned_cluster_ends, c.positioned_glyph_count); CHECK(positioned_bidi_levels, c.positioned_glyph_count);
    CHECK(lines, c.line_count); CHECK(line_origins, c.line_count); CHECK(boxes, c.cluster_box_count); CHECK(carets, c.caret_stop_count);
    CHECK(pre_context, v.pre_context_count); CHECK(post_context, v.post_context_count); CHECK(features, v.feature_count);
#undef CHECK
    if (!valid_scalar_records({v.source_scalars, c.source_scalar_count}) ||
        !valid_scalar_records({v.admitted_scalars, c.admitted_scalar_count}) ||
        !valid_scalar_records({v.pre_context, v.pre_context_count}) || !valid_scalar_records({v.post_context, v.post_context_count})) return false;
    for (std::uint32_t i = 0U; i < c.source_scalar_count; ++i) {
        const auto& s = v.source_scalars[i]; const auto& a = v.admitted_scalars[i]; const auto& b = v.scalar_levels[i];
        if (a.input_index != s.input_index || a.input_length != s.input_length || a.code_point == 9U || a.code_point == 0xFFFCU ||
            b.input_index != s.input_index || b.input_length != s.input_length || b.level < 0 || b.level > 125 || b.reserved != 0U) return false;
    }
    for (std::uint32_t i = 0U; i < v.feature_count; ++i)
        if (!feature_tag(v.features[i].tag) || v.features[i].start > v.features[i].end) return false;
    for (std::uint32_t i = 0U; i < v.normalized_coordinate_count; ++i)
        if (v.normalized_coordinates[i] < -16384 || v.normalized_coordinates[i] > 16384) return false;
    std::vector<sfnt_font_view> fonts(v.font_source_count);
    std::vector<std::uint16_t> glyph_counts(v.font_source_count), axes(v.font_source_count);
    std::uint32_t byte_end = 0U;
    for (std::uint32_t i = 0U; i < v.font_source_count; ++i) {
        const auto& f = v.font_sources[i]; sfnt_header_metrics h{};
        if (f.byte_offset != byte_end || f.byte_count == 0U || f.byte_count > v.font_byte_count - byte_end || f.face_index > UINT16_MAX ||
            !sfnt_font_view::try_create({reinterpret_cast<const std::byte*>(v.font_bytes + byte_end), f.byte_count}, f.face_index, fonts[i]) ||
            !fonts[i].try_get_header_metrics(h) || h.units_per_em == 0U || f.units_per_em != h.units_per_em ||
            !fonts[i].try_get_glyph_count(glyph_counts[i]) || !fonts[i].try_get_variation_axis_count(axes[i])) return false;
        byte_end += f.byte_count;
    }
    if (byte_end != v.font_byte_count) return false;
    std::uint32_t scalar_end = 0U, variation_end = 0U;
    bool source_bidi = false;
    for (std::uint32_t i = 0U; i < c.style_count; ++i) {
        const auto& s = v.styles[i]; const auto& d = v.device_styles[i]; const auto& m = v.source_metrics[i];
        if (s.scalar_start != scalar_end || s.scalar_count == 0U || s.scalar_count > c.source_scalar_count - scalar_end ||
            s.font_index >= v.font_source_count || !std::isfinite(s.scale) || s.scale <= 0.0F || s.feature_start > v.feature_count ||
            s.feature_count > v.feature_count - s.feature_start || !valid_digit(s.digit_substitution) ||
            !scalar(s.percent) || !scalar(s.group_separator) || !scalar(s.decimal_separator) ||
            d.font_index != s.font_index || d.source_scale != s.scale || d.reserved != 0U ||
            d.logical_units_per_physical_pixel != 1.0F / v.dpi_scale || d.logical_units_per_physical_pixel * v.dpi_scale != 1.0F ||
            d.x_pixels_per_em_26_6 == 0U || d.y_pixels_per_em_26_6 == 0U || d.x_pixels_per_em_26_6 > INT32_MAX || d.y_pixels_per_em_26_6 > INT32_MAX ||
            (d.interpreter != 35U && d.interpreter != 40U) || d.x_phase_26_6 >= 64U || d.y_phase_26_6 >= 64U ||
            d.variation_start != variation_end || d.variation_count > v.variation_coordinate_count - variation_end || d.variation_count != axes[s.font_index] ||
            v.normalized_coordinate_count != axes[s.font_index] ||
            !std::isfinite(m.ascent) || m.ascent < 0.0F || !std::isfinite(m.descent) || m.descent < 0.0F || !std::isfinite(m.ascent + m.descent)) return false;
        for (std::uint16_t axis = 0U; axis < axes[s.font_index]; ++axis) {
            std::int16_t normalized{};
            if (!fonts[s.font_index].try_normalize_variation_coordinate(axis, v.variation_coordinates_16_16[variation_end + axis], normalized) ||
                v.normalized_coordinates[axis] != normalized) return false;
        }
        source_bidi |= (s.digit_substitution & PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_SOURCE_BIDI) != 0U;
        scalar_end += s.scalar_count; variation_end += d.variation_count;
    }
    if (scalar_end != c.source_scalar_count || variation_end != v.variation_coordinate_count || source_bidi != (v.source_digit_bidi != 0U)) return false;
    std::uint32_t logical_end = 0U, source_end = 0U, run_end = 0U, outline_end = 0U, segment_end = 0U;
    scalar_end = 0U;
    for (std::uint32_t i = 0U; i < c.run_count; ++i) {
        const auto& r = v.runs[i]; const auto& slice = v.run_slices[i];
        if (r.scalar_start != scalar_end || r.scalar_count == 0U || r.scalar_count > c.source_scalar_count - scalar_end ||
            r.logical_start != logical_end || r.logical_count > c.logical_glyph_count - logical_end || r.style_index >= c.style_count ||
            r.font_index >= v.font_source_count || r.bidi_level < 0 || r.bidi_level > 125 ||
            slice.source_start != source_end || slice.source_count != r.source_descriptor_count || slice.source_count > v.source_outline_count - source_end ||
            slice.run_start != run_end || slice.run_count != r.logical_count || slice.run_count > v.run_outline_count - run_end ||
            slice.outline_start != outline_end || slice.outline_count > v.outline_count - outline_end ||
            slice.segment_start != segment_end || slice.segment_count > v.segment_count - segment_end) return false;
        const auto& style = v.styles[r.style_index]; const auto& device = v.device_styles[r.style_index];
        if (r.font_index != style.font_index || r.source_scale != style.scale || r.logical_units_per_physical_pixel != device.logical_units_per_physical_pixel ||
            r.scalar_start < style.scalar_start || r.scalar_start - style.scalar_start > style.scalar_count ||
            r.scalar_count > style.scalar_count - (r.scalar_start - style.scalar_start)) return false;
        for (std::uint32_t j = 0U; j < r.scalar_count; ++j) if (v.scalar_levels[scalar_end + j].level != r.bidi_level) return false;
        std::uint32_t ink = 0U;
        for (std::uint32_t j = 0U; j < slice.source_count; ++j) {
            const auto index = v.source_outline_indices[source_end + j];
            if (index == hinted_no_outline) continue;
            if (ink >= slice.outline_count || index != outline_end + ink || v.outline_owners[index].run_index != i ||
                v.outline_owners[index].descriptor_index != j) return false;
            ++ink;
        }
        if (ink != slice.outline_count) return false;
        std::vector<bool> occurrences(r.logical_count, false);
        std::vector<std::uint32_t> descriptor_ids(slice.source_count, UINT32_MAX);
        std::uint32_t reversed_group_end = r.logical_count, group_start = 0U, group_end = 0U;
        for (std::uint32_t j = 0U; j < r.logical_count; ++j) {
            const auto index = logical_end + j; const auto& g = v.logical_glyphs[index]; const auto& o = v.logical_owners[index];
            if (o.run_index != i || o.run_glyph_index >= r.logical_count || occurrences[o.run_glyph_index] || o.descriptor_index >= slice.source_count ||
                g.glyph_id >= glyph_counts[r.font_index] || !scalar(g.code_point) || (g.flags & ~7U) != 0U || g.cluster < 0 ||
                v.logical_cluster_ends[index] <= g.cluster || v.logical_bidi_levels[index] != r.bidi_level ||
                v.glyph_scales[index] != r.logical_units_per_physical_pixel / 64.0F ||
                v.run_outline_indices[run_end + o.run_glyph_index] != v.source_outline_indices[source_end + o.descriptor_index]) return false;
            const auto scalar_first = v.source_scalars + r.scalar_start;
            const auto scalar_last = scalar_first + r.scalar_count;
            const auto cluster = std::lower_bound(scalar_first, scalar_last, static_cast<std::uint32_t>(g.cluster),
                [](const auto& source, std::uint32_t value) { return source.input_index < value; });
            if (cluster == scalar_last || cluster->input_index != static_cast<std::uint32_t>(g.cluster) ||
                static_cast<std::uint64_t>(v.logical_cluster_ends[index]) >
                    static_cast<std::uint64_t>(v.source_scalars[c.source_scalar_count - 1U].input_index) +
                        v.source_scalars[c.source_scalar_count - 1U].input_length ||
                (descriptor_ids[o.descriptor_index] != UINT32_MAX && descriptor_ids[o.descriptor_index] != g.glyph_id)) return false;
            descriptor_ids[o.descriptor_index] = g.glyph_id;
            if ((r.bidi_level & 1) == 0) { if (o.run_glyph_index != j) return false; }
            else {
                if (j == group_end) {
                    group_start = j; group_end = j + 1U;
                    while (group_end < r.logical_count && v.logical_glyphs[logical_end + group_end].cluster == g.cluster) ++group_end;
                    reversed_group_end -= group_end - group_start;
                }
                if (o.run_glyph_index != reversed_group_end + j - group_start) return false;
            }
            occurrences[o.run_glyph_index] = true;
            if (index != 0U) {
                const auto previous = v.logical_glyphs[index - 1U].cluster;
                if (g.cluster < previous || (g.cluster > previous && v.logical_cluster_ends[index - 1U] > g.cluster) ||
                    (g.cluster == previous && v.logical_cluster_ends[index] != v.logical_cluster_ends[index - 1U])) return false;
            }
        }
        scalar_end += r.scalar_count; logical_end += r.logical_count; source_end += slice.source_count;
        run_end += slice.run_count; outline_end += slice.outline_count; segment_end += slice.segment_count;
    }
    if (scalar_end != c.source_scalar_count || logical_end != c.logical_glyph_count || source_end != v.source_outline_count ||
        run_end != v.run_outline_count || outline_end != v.outline_count || segment_end != v.segment_count) return false;
    std::uint64_t next_segment = 0U;
    for (std::uint32_t i = 0U; i < v.outline_count; ++i) {
        const auto& o = v.outlines[i];
        if (o.segment_offset != next_segment || o.segment_count == 0U || o.segment_count > v.segment_count - next_segment ||
            o.raster_scale != 1.0F || o.subpixel_x != 0.0F || !std::isfinite(o.min_x) || !std::isfinite(o.min_y) ||
            !std::isfinite(o.max_x) || !std::isfinite(o.max_y) || o.max_x <= o.min_x || o.max_y <= o.min_y) return false;
        auto first = v.segments[next_segment].p0, end = first;
        for (std::uint64_t j = 0U; j < o.segment_count; ++j) {
            const auto& s = v.segments[next_segment + j];
            if (!valid_segment(s)) return false;
            if (!same(s.p0, end)) { if (!same(end, first)) return false; first = s.p0; }
            end = s.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE ? s.p1 : s.kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC ? s.p2 : s.p3;
            const auto inside = [&](progpu_native_point p) { return p.x >= o.min_x && p.x <= o.max_x && p.y >= o.min_y && p.y <= o.max_y; };
            if (!inside(s.p0) || !inside(s.p1) || (s.kind != PROGPU_NATIVE_PATH_SEGMENT_LINE && !inside(s.p2)) ||
                (s.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC && !inside(s.p3))) return false;
        }
        if (!same(end, first)) return false;
        const auto& slice = v.run_slices[v.outline_owners[i].run_index];
        if (o.segment_offset < slice.segment_start || o.segment_offset + o.segment_count > static_cast<std::uint64_t>(slice.segment_start) + slice.segment_count) return false;
        next_segment += o.segment_count;
    }
    if (next_segment != v.segment_count) return false;
    for (std::uint32_t i = 0U; i < c.positioned_glyph_count; ++i) {
        const auto& g = v.positioned_glyphs[i]; const auto& owner = v.positioned_owners[i];
        if (g.glyph_index >= c.logical_glyph_count || !same(owner, v.logical_owners[g.glyph_index]) || !finite({g.x,g.y}) ||
            !finite({g.advance_x,g.advance_y}) || !finite({g.x + g.advance_x,g.y + g.advance_y}) ||
            g.glyph_id != v.logical_glyphs[g.glyph_index].glyph_id || g.cluster != v.logical_glyphs[g.glyph_index].cluster ||
            g.font_index != v.runs[owner.run_index].font_index || v.positioned_cluster_ends[i] != v.logical_cluster_ends[g.glyph_index] ||
            v.positioned_bidi_levels[i] < 0 || v.positioned_bidi_levels[i] > 125 ||
            v.positioned_outline_indices[i] != v.source_outline_indices[v.run_slices[owner.run_index].source_start + owner.descriptor_index]) return false;
    }
    std::uint32_t covered = 0U;
    for (std::uint32_t i = 0U; i < c.line_count; ++i) {
        const auto& line = v.lines[i];
        if (line.glyph_start != covered || line.glyph_count == 0U || line.glyph_count > c.positioned_glyph_count - covered ||
            line.input_start < 0 || line.input_end < line.input_start || !finite_extent(0.0F,line.baseline_y,line.width,line.height) ||
            !std::isfinite(v.line_origins[i]) || line.clipped > 1U || (line.reserved0 & ~1U) != 0U || line.reserved1 != 0U || line.reserved2 != 0U) return false;
        std::uint32_t first = UINT32_MAX, last = 0U;
        for (std::uint32_t j = 0U; j < line.glyph_count; ++j) {
            const auto logical = v.positioned_glyphs[covered + j].glyph_index;
            first = std::min(first,logical); last = std::max(last,logical);
        }
        // Original measured writer endpoints (text_layout.cpp): the final
        // endpoint is last cluster + 1, even for a multi-code-unit scalar.
        // It must not be rounded to the transported scalar/cluster end.
        const auto input_end = last + 1U < c.logical_glyph_count
            ? v.logical_glyphs[last + 1U].cluster : v.logical_glyphs[last].cluster + 1;
        if (line.input_start != v.logical_glyphs[first].cluster || line.input_end != input_end) return false;
        covered += line.glyph_count;
    }
    if (covered != c.positioned_glyph_count) return false;
    for (std::uint32_t i = 0U; i < c.cluster_box_count; ++i) {
        const auto& box = v.boxes[i];
        if (box.input_start < 0 || box.input_end <= box.input_start || box.line_index >= c.line_count || box.bidi_level < 0 || box.bidi_level > 125 ||
            box.reserved0 != 0U || box.reserved1 != 0U || box.reserved2 != 0U || !finite_extent(box.x,box.y,box.width,box.height)) return false;
    }
    for (std::uint32_t i = 0U; i < c.caret_stop_count; ++i) {
        const auto& caret = v.carets[i];
        if (caret.input_position < 0 || caret.line_index >= c.line_count || caret.bidi_level < 0 || caret.bidi_level > 125 || caret.trailing > 1U ||
            caret.reserved0 != 0U || caret.reserved1 != 0U || !finite_extent(caret.x,caret.y,0.0F,caret.height)) return false;
    }
    font_error interaction_error{};
    return interaction_detail::validate_retained_records(
        std::span{v.positioned_glyphs,c.positioned_glyph_count},std::span{v.lines,c.line_count},
        std::span{v.positioned_cluster_ends,c.positioned_glyph_count},std::span{v.positioned_bidi_levels,c.positioned_glyph_count},
        std::span{v.boxes,c.cluster_box_count},std::span{v.carets,c.caret_stop_count},&interaction_error,
        true,std::span<const text_fragment_placement>{},std::span{v.line_origins,c.line_count});
}
} // namespace

hinted_paragraph_glyph_resource_result import_hinted_paragraph_glyph_resource(const progpu_native_hinted_glyph_resource_view& v) noexcept {
    hinted_paragraph_glyph_resource_result result{};
    try {
        if (!valid_records(v)) return result;
        auto records = std::make_shared<owned_hinted_glyph_import>();
        // Retain only pointer-free metadata. All borrowed pointer members stay
        // zero; the complete arrays below own their original values.
        records->metadata.abi_version = v.abi_version; records->metadata.struct_size = v.struct_size;
        records->metadata.dpi_scale = v.dpi_scale; records->metadata.projection_policy = v.projection_policy; records->metadata.coverage = v.coverage;
        records->metadata.source_digit_bidi = v.source_digit_bidi; records->metadata.paragraph_level = v.paragraph_level;
        records->metadata.shaping_direction = v.shaping_direction; records->metadata.shaping_flags = v.shaping_flags;
        records->metadata.counts = v.counts; records->metadata.result = v.result; records->metadata.layout = v.layout;
#define COUNT(field) records->metadata.field = v.field
        COUNT(font_source_count); COUNT(font_byte_count); COUNT(variation_coordinate_count); COUNT(normalized_coordinate_count);
        COUNT(outline_count); COUNT(segment_count); COUNT(source_outline_count); COUNT(run_outline_count);
        COUNT(pre_context_count); COUNT(post_context_count); COUNT(feature_count);
#undef COUNT
        const auto& c = v.counts;
#define COPY(field, count) copy(records->field, v.field, (count))
        COPY(font_sources,v.font_source_count); COPY(font_bytes,v.font_byte_count); COPY(device_styles,c.style_count);
        COPY(variation_coordinates_16_16,v.variation_coordinate_count); COPY(normalized_coordinates,v.normalized_coordinate_count);
        COPY(source_scalars,c.source_scalar_count); COPY(admitted_scalars,c.admitted_scalar_count); COPY(scalar_levels,c.source_scalar_count);
        COPY(styles,c.style_count); COPY(source_metrics,c.style_count); COPY(runs,c.run_count); COPY(logical_glyphs,c.logical_glyph_count);
        COPY(logical_owners,c.logical_glyph_count); COPY(logical_cluster_ends,c.logical_glyph_count); COPY(logical_bidi_levels,c.logical_glyph_count);
        COPY(glyph_scales,c.logical_glyph_count); COPY(positioned_glyphs,c.positioned_glyph_count); COPY(positioned_owners,c.positioned_glyph_count);
        COPY(positioned_cluster_ends,c.positioned_glyph_count); COPY(positioned_bidi_levels,c.positioned_glyph_count); COPY(lines,c.line_count);
        COPY(line_origins,c.line_count); COPY(boxes,c.cluster_box_count); COPY(carets,c.caret_stop_count);
        COPY(pre_context,v.pre_context_count); COPY(post_context,v.post_context_count); COPY(features,v.feature_count);
#undef COPY
        auto resource = std::shared_ptr<hinted_paragraph_glyph_resource>(new hinted_paragraph_glyph_resource{});
        resource->dpi_scale_ = v.dpi_scale; resource->projection_policy_ = static_cast<hinted_projection_policy>(v.projection_policy);
        resource->coverage_ = static_cast<hinted_outline_coverage>(v.coverage);
        copy(resource->outlines_,v.outlines,v.outline_count); copy(resource->segments_,v.segments,v.segment_count);
        copy(resource->source_outline_indices_,v.source_outline_indices,v.source_outline_count);
        copy(resource->run_outline_indices_,v.run_outline_indices,v.run_outline_count);
        copy(resource->positioned_outline_indices_,v.positioned_outline_indices,c.positioned_glyph_count);
        for (std::uint32_t i = 0U; i < c.run_count; ++i) { const auto& s = v.run_slices[i];
            resource->run_slices_.push_back({s.source_start,s.source_count,s.run_start,s.run_count,s.outline_start,s.outline_count,s.segment_start,s.segment_count}); }
        for (std::uint32_t i = 0U; i < v.outline_count; ++i)
            resource->outline_owners_.push_back({v.outline_owners[i].run_index,v.outline_owners[i].descriptor_index});
        for (std::uint32_t i = 0U; i < c.positioned_glyph_count; ++i) {
            const auto& g = v.positioned_glyphs[i]; const auto& o = v.positioned_owners[i]; const auto& r = v.runs[o.run_index];
            resource->positioned_owners_.push_back({i,g.glyph_index,o.run_index,o.run_glyph_index,o.descriptor_index,r.font_index,r.style_index});
        }
        resource->imported_ = std::move(records);
        result.status = PROGPU_NATIVE_STATUS_SUCCESS; result.error = {}; result.generation = std::move(resource);
    } catch (const std::bad_alloc&) {
        result.status = PROGPU_NATIVE_STATUS_OUT_OF_MEMORY; result.error.code = hinted_glyph_frame_error_code::resource_exhausted;
    } catch (...) { result.status = PROGPU_NATIVE_STATUS_INTERNAL_ERROR; }
    return result;
}
} // namespace progpu::native::text
